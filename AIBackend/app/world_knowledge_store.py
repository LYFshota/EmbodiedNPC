"""Versioned builtin catalog + atomic local overrides. No network mutation surface."""

from __future__ import annotations

import json
import logging
import os
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Literal

from pydantic import Field, model_validator

from .contracts import CognitiveRequest
from .model_api_config_store import atomic_json_write, _reject_duplicates, ModelConfigStoreError
from .world_knowledge import (KnowledgeModel, KnowledgeBundle, KnowledgeEntry, KnowledgeDeployment,
                              KnowledgeContext, KnowledgeQuery, retrieve_knowledge)

_LOCK = threading.RLock()
_ROOT = Path(__file__).resolve().parent / "knowledge"
_MAX_BYTES = 256 * 1024
logger = logging.getLogger("embodiednpc.backend.knowledge")


class KnowledgeStoreError(ValueError):
    pass


class KnowledgeOverrides(KnowledgeModel):
    schema_version: Literal[1] = 1
    revision: int = Field(default=0, ge=0, strict=True)
    updated_at: datetime | None = None
    entries: tuple[KnowledgeEntry, ...] = Field(default=(), max_length=128)
    deleted_ids: tuple[str, ...] = Field(default=(), max_length=128)

    @model_validator(mode="after")
    def unique_ids(self):
        ids = [e.knowledge_id for e in self.entries]
        if (len(ids) != len(set(ids)) or len(self.deleted_ids) != len(set(self.deleted_ids))
                or set(ids).intersection(self.deleted_ids)
                or any(not i.strip() or len(i) > 120 for i in self.deleted_ids)):
            raise ValueError("KNOWLEDGE_OVERRIDE_INVALID")
        return self


def read_document(path: Path, domain):
    try:
        with path.open("rb") as stream:
            raw = stream.read(_MAX_BYTES + 1)
        if len(raw) > _MAX_BYTES:
            raise ValueError("too large")
        value = json.loads(raw, object_pairs_hook=_reject_duplicates)
        return domain.model_validate(value)
    except FileNotFoundError:
        raise
    except (OSError, ValueError, RecursionError):
        raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None


class WorldKnowledgeStore:
    def __init__(self, path: Path | None = None, builtin_path: Path | None = None):
        self.path = Path(path) if path is not None else Path(os.getenv(
            "EMBODIEDNPC_KNOWLEDGE_FILE", str(_ROOT.parents[1] / "data" / "world_knowledge.json")))
        self.builtin_path = Path(builtin_path) if builtin_path is not None else _ROOT / "cafedemo_v2.json"

    def _documents(self):
        bundle = read_document(self.builtin_path, KnowledgeBundle)
        try:
            overrides = read_document(self.path, KnowledgeOverrides)
        except FileNotFoundError:
            overrides = KnowledgeOverrides()
        return bundle, overrides

    @staticmethod
    def _merge(bundle, overrides):
        entries = {e.knowledge_id: e for e in bundle.entries}
        entries.update({e.knowledge_id: e for e in overrides.entries})
        for knowledge_id in overrides.deleted_ids:
            entries.pop(knowledge_id, None)
        if len(entries) > 128:
            raise KnowledgeStoreError("KNOWLEDGE_CATALOG_TOO_LARGE")
        return tuple(entries[key] for key in sorted(entries))

    def snapshot(self) -> tuple[KnowledgeBundle, KnowledgeOverrides, tuple[KnowledgeEntry, ...]]:
        with _LOCK:
            bundle, overrides = self._documents()
            return bundle, overrides, self._merge(bundle, overrides)

    def manage(self, operation: Literal["upsert", "delete", "restore", "restore-all"], *,
               expected_revision: int, entry: KnowledgeEntry | None = None,
               knowledge_id: str | None = None) -> KnowledgeOverrides:
        """Local developer operation, with revision check. Never called from cognition/HTTP."""
        if type(expected_revision) is not int or expected_revision < 0:
            raise KnowledgeStoreError("KNOWLEDGE_REVISION_INVALID")
        with _LOCK:
            bundle, previous, current = self.snapshot()
            if previous.revision != expected_revision:
                raise KnowledgeStoreError("KNOWLEDGE_REVISION_CONFLICT")
            entries = {e.knowledge_id: e for e in previous.entries}
            deleted = set(previous.deleted_ids)
            now = datetime.now(timezone.utc)
            if operation == "upsert":
                if not isinstance(entry, KnowledgeEntry) or knowledge_id is not None:
                    raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
                entry = KnowledgeEntry.model_validate_json(entry.model_dump_json())
                older = next((e for e in current if e.knowledge_id == entry.knowledge_id), None)
                entry = entry.model_copy(update={"version": older.version + 1 if older else 1, "updated_at": now})
                entries[entry.knowledge_id] = entry
                deleted.discard(entry.knowledge_id)
            elif operation in {"delete", "restore"}:
                if not knowledge_id or entry is not None:
                    raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
                known = {e.knowledge_id for e in current} | set(deleted)
                if knowledge_id not in known:
                    raise KnowledgeStoreError("KNOWLEDGE_ID_UNKNOWN")
                if operation == "delete":
                    deleted.add(knowledge_id)
                else:
                    entries.pop(knowledge_id, None)
                    deleted.discard(knowledge_id)
            elif operation == "restore-all" and entry is None and knowledge_id is None:
                entries, deleted = {}, set()
            else:
                raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
            changed = KnowledgeOverrides(revision=previous.revision + 1, updated_at=now,
                entries=tuple(e for key, e in sorted(entries.items()) if key not in deleted),
                deleted_ids=tuple(sorted(deleted)))
            self._merge(bundle, changed)
            try:
                atomic_json_write(self.path, changed.model_dump_json(indent=2).encode("utf-8") + b"\n")
            except ModelConfigStoreError:
                raise KnowledgeStoreError("KNOWLEDGE_WRITE_FAILED") from None
            return changed


class WorldKnowledgeResolver:
    def __init__(self, store: WorldKnowledgeStore, deployment: KnowledgeDeployment | None, ranker=None):
        self.store = store
        self.deployment = deployment
        self.ranker = ranker

    @classmethod
    def from_environment(cls, embedding_provider=None):
        try:
            deployment = read_document(Path(os.getenv("EMBODIEDNPC_KNOWLEDGE_DEPLOYMENT_FILE",
                str(_ROOT / "cafedemo_v2_deployment.json"))), KnowledgeDeployment)
        except (OSError, KnowledgeStoreError):
            logger.warning("knowledge_deployment_unavailable")
            deployment = None
        store = WorldKnowledgeStore()
        ranker = None
        if embedding_provider is not None:
            from .knowledge_embeddings import KnowledgeEmbeddingIndex, KnowledgeHybridRanker
            index = KnowledgeEmbeddingIndex(store, Path(os.getenv("EMBODIEDNPC_KNOWLEDGE_INDEX_DB",
                str(store.path.with_name("world_knowledge_index.sqlite3")))))
            ranker = KnowledgeHybridRanker(index, embedding_provider)
        return cls(store, deployment, ranker)

    def resolve(self, request: CognitiveRequest) -> KnowledgeContext:
        principal = self.deployment.resolve(request) if self.deployment else None
        if principal is None:
            return KnowledgeContext(status="UNAUTHORIZED")
        event = request.context.current_event
        # No fabricated utterance or full-catalog injection for non-dialogue events.
        if event is None or event.event_type != "player.utterance":
            return KnowledgeContext(status="NO_MATCH")
        text = next((p.value for p in event.payload if p.key == "text"), "")
        if not text.strip():
            return KnowledgeContext(status="NO_MATCH")
        try:
            bundle, overrides, entries = self.store.snapshot()
            return retrieve_knowledge(entries, principal, KnowledgeQuery(text=text[:512]),
                                      bundle_version=bundle.bundle_version, revision=overrides.revision,
                                      ranker=self.ranker, defer_budget=True)
        except (OSError, ValueError, RecursionError):
            logger.warning("knowledge_context_unavailable request_id=%s", request.request_id)
            return KnowledgeContext(status="UNAVAILABLE")
