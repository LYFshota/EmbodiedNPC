"""Small independent Knowledge derived index, sharing the existing Embedding provider."""

from __future__ import annotations

import asyncio
from contextlib import closing
import hashlib
import json
import logging
from pathlib import Path
import sqlite3
import struct

from .canonical_embeddings import CanonicalEmbeddingIndexer, normalized, decode
from .context_token_estimator import ContextTokenEstimator
from .world_knowledge import KnowledgeEntry, KnowledgeQuery, compact, lexical_rank, text_score

logger = logging.getLogger("embodiednpc.backend.knowledge_embeddings")
TEXT_VERSION = "knowledge-embedding-text/1"
CANDIDATE_LIMIT = 16  # Per branch; independent of final Top-K=3.


def embedding_text(entry: KnowledgeEntry) -> str:
    attributes = "\n".join(f"{key}: {json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False)}"
                           for key, value in sorted(entry.attributes.items()))
    return (f"Knowledge ID: {entry.knowledge_id}\nName: {entry.name}\nType: {entry.knowledge_type}\n"
            f"Content: {entry.content}\nAttributes:\n{attributes}")


def content_hash(entry: KnowledgeEntry, bundle_version: str) -> str:
    # Include version/source/access too: metadata-only changes must invalidate derived rows.
    document = compact(dict(projection_version=TEXT_VERSION, bundle_version=bundle_version,
                            entry=entry.model_dump(mode="json")))
    return hashlib.sha256(document.encode("utf-8")).hexdigest()


def knowledge_space(provider) -> str:
    raw = getattr(provider, "provider", provider)
    # Preserve the shared model key; additionally isolate legacy bootstrap keys
    # that predate endpoint/dimension identity. Never include credentials.
    identity = compact(dict(model_key=provider.model_key, projection_version=TEXT_VERSION,
        provider_type=type(raw).__name__, endpoint=getattr(raw, "base_url", None),
        dimensions=getattr(provider, "dimensions", None), model_version=getattr(raw, "model_version", None)))
    return "knowledge-space/1:" + hashlib.sha256(identity.encode("utf-8")).hexdigest()


class KnowledgeEmbeddingIndex:
    def __init__(self, store, path: Path):
        self.store, self.path = store, Path(path)

    def connect(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(self.path, timeout=2)
        db.row_factory = sqlite3.Row
        try:
            # Refuse a Memory database instead of installing Knowledge tables into it.
            names = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            if names - {"knowledge_embeddings"} or db.execute("PRAGMA user_version").fetchone()[0] not in (0, 1):
                raise ValueError("KNOWLEDGE_INDEX_DATABASE_INCOMPATIBLE")
            db.execute("CREATE TABLE IF NOT EXISTS knowledge_embeddings ("
                       "knowledge_id TEXT NOT NULL, space_key TEXT NOT NULL, content_hash TEXT NOT NULL, "
                       "dimension INTEGER NOT NULL, vector_blob BLOB NOT NULL, "
                       "PRIMARY KEY(knowledge_id, space_key))")
            db.execute("PRAGMA user_version=1")
            db.commit()
            return db
        except Exception:
            db.close()
            raise

    def load_valid(self, entries, bundle_version, space_key, dimensions=None):
        if not entries:
            return {}
        by_id = {e.knowledge_id: e for e in entries}
        # Only these authorized IDs are read for a foreground query. No global ANN search.
        with closing(self.connect()) as db:
            rows = db.execute("SELECT * FROM knowledge_embeddings WHERE space_key=? AND knowledge_id IN ("
                              + ",".join("?" for _ in by_id) + ")", (space_key, *by_id))
            result = {}
            for row in rows:
                if (row["content_hash"] != content_hash(by_id[row["knowledge_id"]], bundle_version)
                        or dimensions not in (None, row["dimension"])):
                    continue
                vector = decode(row["vector_blob"], row["dimension"])
                if vector is not None:
                    result[row["knowledge_id"]] = vector
            return result

    def pending(self, space_key, limit, dimensions=None):
        bundle, _, entries = self.store.snapshot()
        ids = [e.knowledge_id for e in entries]
        with closing(self.connect()) as db, db:
            if ids:
                db.execute("DELETE FROM knowledge_embeddings WHERE knowledge_id NOT IN ("
                           + ",".join("?" for _ in ids) + ")", ids)
            else:
                db.execute("DELETE FROM knowledge_embeddings")
        valid = self.load_valid(entries, bundle.bundle_version, space_key, dimensions)
        return [(e, bundle.bundle_version) for e in entries if e.knowledge_id not in valid][:limit]

    def store_if_current(self, entry, bundle_version, space_key, vector):
        vector = normalized(vector)
        current_bundle, _, current_entries = self.store.snapshot()
        current = next((e for e in current_entries if e.knowledge_id == entry.knowledge_id), None)
        expected = content_hash(entry, bundle_version)
        if current is None or content_hash(current, current_bundle.bundle_version) != expected:
            return False
        with closing(self.connect()) as db, db:
            db.execute("INSERT INTO knowledge_embeddings VALUES(?,?,?,?,?) "
                       "ON CONFLICT(knowledge_id,space_key) DO UPDATE SET "
                       "content_hash=excluded.content_hash,dimension=excluded.dimension,vector_blob=excluded.vector_blob",
                       (entry.knowledge_id, space_key, expected, len(vector),
                        struct.pack(f"<{len(vector)}f", *vector)))
        return True


class KnowledgeEmbeddingIndexer(CanonicalEmbeddingIndexer):
    """Reuse start/stop/poll lifecycle; adapt only the derived document/index contract."""

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._wake = asyncio.Event()

    def notify(self):
        self._wake.set()

    async def index_missing_or_stale(self, limit=8):
        if not 1 <= limit <= 16:
            raise ValueError("INVALID_EMBEDDING_BATCH")
        if getattr(self.provider, "mode", "running") != "running":
            return 0  # Reuse the same pause/Cloud-consent gate as Memory.
        space = knowledge_space(self.provider)
        dimensions = getattr(self.provider, "dimensions", None)
        # Inspect the bounded catalog, not only the first batch: lexical-only
        # long documents must not starve shorter entries behind them.
        pending = await _worker_call(self.index.pending, space, 128, dimensions)
        count = attempted = 0
        for entry, version in pending:
            try:
                text = embedding_text(entry)
                raw = getattr(self.provider, "provider", self.provider)
                from .builtin_embedding_provider import BuiltinEmbeddingProvider, RESOURCE_SPEC
                if (isinstance(raw, BuiltinEmbeddingProvider)
                        and ContextTokenEstimator().estimate_text(text) + 32 > RESOURCE_SPEC["max_tokens"]):
                    # A long Knowledge entry remains lexical-only; do not send an
                    # obviously over-budget document into the shared Memory gate.
                    continue
                if attempted >= limit or getattr(self.provider, "mode", "running") != "running":
                    break
                attempted += 1
                vector = normalized(await _worker_call(self.provider.embed, text))
                await asyncio.sleep(0)  # Cancelled worker must not publish its completed encode.
                if knowledge_space(self.provider) != space or dimensions not in (None, len(vector)):
                    continue
                count += await _worker_call(self.index.store_if_current, entry, version, space, vector)
            except Exception as error:
                logger.warning("knowledge_index_entry_unavailable error=%s", type(error).__name__)
        return count

    async def _run(self):
        while True:
            self._wake.clear()
            try:
                await self.index_missing_or_stale(self.batch_size)
            except Exception as error:
                logger.warning("knowledge_index_unavailable error=%s", type(error).__name__)
            try:
                await asyncio.wait_for(self._wake.wait(), self.interval_seconds)
            except TimeoutError:
                pass


async def _worker_call(function, *args):
    # to_thread cannot interrupt native SQLite/ONNX work. Drain the current call
    # before inherited stop() returns or the shared provider/file is closed.
    task = asyncio.create_task(asyncio.to_thread(function, *args))
    try:
        return await asyncio.shield(task)
    except asyncio.CancelledError:
        await asyncio.gather(task, return_exceptions=True)
        raise


class KnowledgeHybridRanker:
    def __init__(self, index: KnowledgeEmbeddingIndex, provider, *, min_similarity=0.25):
        self.index, self.provider = index, provider
        self.min_similarity = min_similarity

    def rank(self, entries: tuple[KnowledgeEntry, ...], query: KnowledgeQuery, *, bundle_version: str):
        by_id = {e.knowledge_id: e for e in entries}
        lexical = [e.knowledge_id for e in lexical_rank(entries, query)][:CANDIDATE_LIMIT]
        if not entries or not query.text.strip() or query.knowledge_ids:
            return tuple(by_id[key] for key in lexical)
        exact = [e.knowledge_id for e in entries if e.knowledge_id == query.text.strip()]
        dense, similarities = [], {}
        if self.provider is not None:
            try:
                space = knowledge_space(self.provider)
                vectors = self.index.load_valid(entries, bundle_version, space,
                                                getattr(self.provider, "dimensions", None))
                if vectors:
                    encode = getattr(self.provider, "embed_query", self.provider.embed)
                    vector = normalized(encode(query.text))
                    if knowledge_space(self.provider) == space:
                        similarities = {key: sum(a*b for a, b in zip(vector, v)) for key, v in vectors.items()
                                        if len(v) == len(vector)}
                        dense = sorted((key for key, score in similarities.items() if score >= self.min_similarity),
                                       key=lambda key: (-similarities[key], key))[:CANDIDATE_LIMIT]
            except Exception as error:
                logger.warning("knowledge_dense_fallback error=%s", type(error).__name__)
        scores = {}
        for branch in (lexical, dense):
            for rank, key in enumerate(branch, 1):
                scores[key] = scores.get(key, 0) + 1 / (60 + rank)
        fused = sorted(scores, key=lambda key: (-scores[key], -text_score(by_id[key], query.text),
                                                -similarities.get(key, 0), key))
        ordered = list(dict.fromkeys([*exact, *fused]))
        return tuple(by_id[key] for key in ordered)
