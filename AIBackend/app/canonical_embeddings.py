"""Small rebuildable canonical embedding index, independent of Memory writes."""

from __future__ import annotations

import asyncio
from contextlib import closing
from dataclasses import dataclass, field
import hashlib
import logging
import math
import re
import json
import sqlite3
import struct
import time
import unicodedata
from typing import Protocol

import httpx

from .model_api_config import valid_base_url
from .native_model_providers import model_resource

logger = logging.getLogger("embodiednpc.backend.canonical_embeddings")
TEXT_VERSION = "canonical-embedding-text/3"


def embedding_space_key(protocol, endpoint, model, dimensions=None, version=None):
    identity = [protocol, valid_base_url(endpoint), model, dimensions, version, TEXT_VERSION]
    return "space_v2:" + hashlib.sha256(json.dumps(identity, ensure_ascii=True).encode()).hexdigest()


def _embedding_row(db, row):
    """Derived metadata from durable subjects, never display names or UID text."""
    result = dict(row)
    if row["memory_type"] not in ("SEMANTIC_FACT", "DERIVED_INSIGHT"):
        return result
    # Preserve the pending snapshot metadata for the stale-commit comparison.
    if "_embedding_subject_kind" in result:
        return result
    records = db.execute(
        "SELECT r.persistent_id,r.provenance_label,s.identity_namespace,s.identity_type,"
        "s.persistent_id FROM memory_records r LEFT JOIN memory_subjects s USING(memory_id) "
        "WHERE r.memory_id=?", (row["memory_id"],)).fetchall()
    subjects = {(r[2], r[3]) for r in records}
    kind = ("player" if len(records) == 1 and subjects == {("player", "Player")} else
            "npc" if len(records) == 1 and subjects == {("npc", "Character")} else "unknown")
    result["_embedding_subject_kind"] = kind
    result["_embedding_provenance_label"] = records[0][1] if records else ""
    # These identifiers are only used for redaction, never interpolated into text.
    result["_embedding_private_ids"] = tuple(sorted({str(v) for r in records for v in (r[0], r[4]) if v}))
    return result


def embedding_text(row: dict) -> str:
    text = unicodedata.normalize("NFKC", row["summary"]).strip()
    if row["memory_type"] not in ("SEMANTIC_FACT", "DERIVED_INSIGHT"):
        return text
    private = row.get("_embedding_private_ids", ())
    if private:
        pattern = "|".join(re.escape(unicodedata.normalize("NFKC", p))
                           for p in sorted(private, key=len, reverse=True))
        text = re.sub(pattern, "[redacted]", text, flags=re.IGNORECASE)
    # The admitted natural-language claim already preserves its relationships,
    # negation and time. No fixed predicate translation or added fact strength.
    if row.get("_embedding_provenance_label") == "NPC_SELF_STATEMENT" and not text.startswith(
            unicodedata.normalize("NFKC", "NPC曾自述（未独立证实）：")):
        text = "NPC曾自述（未独立证实）：" + text
    if row["memory_type"] == "DERIVED_INSIGHT":
        text = "Tentative derived interpretation (not a direct fact): " + text
    return text


def content_hash(row: dict) -> str:
    return hashlib.sha256((TEXT_VERSION + "\n" + embedding_text(row)).encode("utf-8")).hexdigest()


def normalized(values) -> tuple[float, ...]:
    values = tuple(float(value) for value in values)
    if not values or not all(math.isfinite(value) for value in values):
        raise ValueError("INVALID_EMBEDDING")
    norm = math.hypot(*values)
    if not math.isfinite(norm) or norm <= 0:
        raise ValueError("INVALID_EMBEDDING_NORM")
    return tuple(value / norm for value in values)


def decode(blob, dimension) -> tuple[float, ...] | None:
    try:
        if not isinstance(dimension, int) or dimension <= 0 or len(blob) != dimension * 4:
            return None
        return normalized(struct.unpack(f"<{dimension}f", blob))
    except (TypeError, ValueError, OverflowError, struct.error):
        return None


class CanonicalEmbeddingProvider(Protocol):
    model_key: str

    def embed(self, text: str) -> tuple[float, ...]: ...


@dataclass(frozen=True)
class OllamaEmbeddingProvider:
    base_url: str
    model: str = "qwen3-embedding:0.6b"
    timeout_seconds: float = 5.0
    transport: httpx.BaseTransport | None = field(default=None, repr=False, compare=False)
    dimensions: int | None = None
    model_version: str | None = None
    identity_v2: bool = False

    @property
    def model_key(self) -> str:
        # Switching model tags isolates the derived rows. Pin a model tag/digest
        # when weights change under a mutable name; no automatic model migration.
        return (embedding_space_key("ollama", self.base_url, self.model, self.dimensions, self.model_version)
                if self.identity_v2 else "ollama:" + self.model + "|" + TEXT_VERSION)

    def embed(self, text: str) -> tuple[float, ...]:
        if self.dimensions is not None:
            raise ValueError("EMBEDDING_DIMENSIONS_UNSUPPORTED")
        client_kwargs = {"transport": self.transport} if self.transport is not None else {}
        with httpx.Client(timeout=self.timeout_seconds, trust_env=False,
                          **client_kwargs) as client:
            response = client.post(self.base_url.rstrip("/") + "/api/embed",
                json={"model": self.model, "input": text, "truncate": False})
            response.raise_for_status()
            vectors = response.json()["embeddings"]
        if len(vectors) != 1:
            raise ValueError("EMBEDDING_COUNT_MISMATCH")
        return normalized(vectors[0])


@dataclass(frozen=True, repr=False)
class OpenAICompatibleEmbeddingProvider:
    """Independent synchronous embedding adapter for canonical ranking/indexing."""

    base_url: str
    api_key: str | None
    model: str
    timeout_seconds: float = 30.0
    transport: httpx.BaseTransport | None = field(default=None, repr=False, compare=False)
    dimensions: int | None = None
    model_version: str | None = None
    identity_v2: bool = False

    def __post_init__(self) -> None:
        object.__setattr__(self, "base_url", valid_base_url(self.base_url))

    def __repr__(self) -> str:
        return "OpenAICompatibleEmbeddingProvider()"

    @property
    def model_key(self) -> str:
        if self.identity_v2:
            return embedding_space_key("openai_compatible", self.base_url, self.model, self.dimensions, self.model_version)
        endpoint = hashlib.sha256(self.base_url.encode("utf-8")).hexdigest()[:12]
        return f"openai_compatible:{endpoint}:{self.model}|{TEXT_VERSION}"

    def embed(self, text: str) -> tuple[float, ...]:
        headers = {"Authorization": "Bearer " + self.api_key} if self.api_key else {}
        try:
            with httpx.Client(timeout=self.timeout_seconds, trust_env=False, transport=self.transport) as client:
                response = client.post(self.base_url + "/embeddings", headers=headers,
                                       json={"model": self.model, "input": text,
                                             **({"dimensions": self.dimensions} if self.dimensions is not None else {})})
        except httpx.TimeoutException:
            raise ValueError("EMBEDDING_TIMEOUT") from None
        except httpx.HTTPError:
            raise ValueError("EMBEDDING_UNAVAILABLE") from None
        if response.status_code >= 400:
            raise ValueError("EMBEDDING_REQUEST_FAILED")
        try:
            data = response.json()["data"]
            if not isinstance(data, list) or len(data) != 1 or not isinstance(data[0], dict):
                raise ValueError
            vector = data[0]["embedding"]
            if not isinstance(vector, list) or any(isinstance(value, bool) or not isinstance(value, (int, float))
                                                   for value in vector):
                raise ValueError
            if self.dimensions is not None and len(vector) != self.dimensions:
                raise ValueError
            return normalized(vector)
        except (KeyError, TypeError, ValueError, OverflowError):
            raise ValueError("EMBEDDING_PROTOCOL_ERROR") from None


@dataclass(frozen=True, repr=False)
class GeminiEmbeddingProvider:
    base_url: str
    api_key: str | None
    model: str
    timeout_seconds: float = 30.0
    transport: httpx.BaseTransport | None = field(default=None, repr=False, compare=False)
    dimensions: int | None = None
    model_version: str | None = None
    identity_v2: bool = True

    def __repr__(self):
        return "GeminiEmbeddingProvider()"

    @property
    def model_key(self):
        return embedding_space_key("gemini_native", self.base_url, self.model, self.dimensions, self.model_version)

    def embed(self, text):
        resource = model_resource(self.model)
        payload = {"model": resource, "content": {"parts": [{"text": text}]}}
        if self.dimensions is not None:
            payload["outputDimensionality"] = self.dimensions
        try:
            with httpx.Client(timeout=self.timeout_seconds, trust_env=False, transport=self.transport) as client:
                response = client.post(valid_base_url(self.base_url) + "/" + resource + ":embedContent",
                    headers={"x-goog-api-key": self.api_key or ""}, json=payload)
            if response.status_code >= 400:
                raise ValueError("EMBEDDING_REQUEST_FAILED")
            values = response.json()["embedding"]["values"]
            if not isinstance(values, list) or any(isinstance(v, bool) or not isinstance(v, (int, float)) for v in values):
                raise ValueError
            if self.dimensions is not None and len(values) != self.dimensions:
                raise ValueError
            return normalized(values)
        except httpx.TimeoutException:
            raise ValueError("EMBEDDING_TIMEOUT") from None
        except httpx.HTTPError:
            raise ValueError("EMBEDDING_UNAVAILABLE") from None
        except (KeyError, TypeError, ValueError, OverflowError):
            raise ValueError("EMBEDDING_TEST_FAILED") from None


class CanonicalEmbeddingIndex:
    def __init__(self, connection_factory):
        self._connect = connection_factory

    @staticmethod
    def load_valid(db, rows, model_key):
        """Only load exact eligible IDs; never perform a global vector query."""
        by_id = {row["memory_id"]: _embedding_row(db, row) for row in rows}
        result = {}
        ids = list(by_id)
        for start in range(0, len(ids), 400):
            chunk = ids[start:start + 400]
            records = db.execute(
                "SELECT memory_id,content_hash,dimension,vector_blob FROM memory_embeddings "
                "WHERE model_key=? AND memory_id IN (" + ",".join("?" for _ in chunk) + ")",
                (model_key, *chunk))
            for record in records:
                if record["content_hash"] != content_hash(by_id[record["memory_id"]]):
                    continue
                vector = decode(record["vector_blob"], record["dimension"])
                if vector is not None:
                    result[record["memory_id"]] = vector
        return result

    def pending(self, model_key, limit, dimensions=None):
        pending = []
        with closing(self._connect()) as db:
            rows = db.execute(
                "SELECT r.memory_id,r.memory_type,r.summary,"
                "e.content_hash AS indexed_hash,e.dimension,e.vector_blob FROM memory_records r "
                "LEFT JOIN memory_embeddings e ON e.memory_id=r.memory_id AND e.model_key=? "
                "WHERE r.memory_type IN ('EPISODIC','SEMANTIC_FACT','DERIVED_INSIGHT') "
                "ORDER BY r.learned_at,r.memory_id", (model_key,))
            for record in rows:
                row = _embedding_row(db, record)
                if (row["indexed_hash"] != content_hash(row) or
                    (dimensions is not None and row["dimension"] != dimensions) or
                    decode(row["vector_blob"], row["dimension"]) is None):
                    pending.append(row)
                    if len(pending) >= limit:
                        break
        return pending

    def statistics(self, model_key, dimensions=None):
        total = completed = 0
        with closing(self._connect()) as db:
            rows = db.execute(
                "SELECT r.memory_id,r.memory_type,r.summary,"
                "e.content_hash AS indexed_hash,e.dimension,e.vector_blob FROM memory_records r "
                "LEFT JOIN memory_embeddings e ON e.memory_id=r.memory_id AND e.model_key=? "
                "WHERE r.memory_type IN ('EPISODIC','SEMANTIC_FACT','DERIVED_INSIGHT')", (model_key,))
            for record in rows:
                total += 1
                if (record["indexed_hash"] == content_hash(_embedding_row(db, record)) and
                    (dimensions is None or record["dimension"] == dimensions) and
                    decode(record["vector_blob"], record["dimension"]) is not None):
                    completed += 1
        return dict(total=total, completed=completed, pending=total-completed,
                    progress=100.0 if total == 0 else round(100.0*completed/total, 1))

    def store_if_current(self, row, model_key, vector):
        vector = normalized(vector)
        with closing(self._connect()) as db:
            db.execute("BEGIN IMMEDIATE")
            current = db.execute(
                "SELECT r.memory_id,r.memory_type,r.summary "
                "FROM memory_records r "
                "WHERE r.memory_id=?", (row["memory_id"],)).fetchone()
            if current is None:
                return False
            current = _embedding_row(db, current)
            row = _embedding_row(db, row)
            if content_hash(current) != content_hash(row):
                return False
            db.execute("INSERT INTO memory_embeddings VALUES(?,?,?,?,?,?) "
                       "ON CONFLICT(memory_id,model_key) DO UPDATE SET content_hash=excluded.content_hash,"
                       "dimension=excluded.dimension,vector_blob=excluded.vector_blob,indexed_at_utc=excluded.indexed_at_utc",
                       (row["memory_id"], model_key, content_hash(row), len(vector),
                        struct.pack(f"<{len(vector)}f", *vector), time.time()))
            db.commit()
        return True


class CanonicalEmbeddingIndexer:
    def __init__(self, index: CanonicalEmbeddingIndex, provider: CanonicalEmbeddingProvider,
                 *, interval_seconds: float = 30, batch_size: int = 8):
        if interval_seconds < 1 or not 1 <= batch_size <= 16:
            raise ValueError("INVALID_EMBEDDING_INDEXER_BUDGET")
        self.index, self.provider = index, provider
        self.interval_seconds, self.batch_size = interval_seconds, batch_size
        self._task = None

    async def index_missing_or_stale(self, limit=8):
        if not 1 <= limit <= 16:
            raise ValueError("INVALID_EMBEDDING_BATCH")
        pending = await asyncio.to_thread(self.index.pending, self.provider.model_key, limit)
        count = 0
        for row in pending:
            vector = await asyncio.to_thread(self.provider.embed, embedding_text(row))
            # Cancellation of the HTTP wait cannot publish an embedding afterwards.
            await asyncio.sleep(0)
            count += self.index.store_if_current(row, self.provider.model_key, vector)
        return count

    def start(self):
        if self._task is None or self._task.done():
            self._task = asyncio.create_task(self._run())

    async def stop(self):
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            self._task = None

    async def _run(self):
        while True:
            try:
                await self.index_missing_or_stale(self.batch_size)
            except Exception as exc:
                logger.warning("Canonical embedding index unavailable error=%s", type(exc).__name__)
            await asyncio.sleep(self.interval_seconds)
