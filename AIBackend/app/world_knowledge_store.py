"""Complete current catalog, explicit legacy migration and atomic local writes."""

from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import json
import logging
import os
import re
import time
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Literal

from pydantic import Field, model_validator

from .contracts import CognitiveRequest
from .local_access import _PublicationLock
from .model_api_config_store import atomic_json_write, _reject_duplicates, ModelConfigStoreError
from .world_knowledge import (KnowledgeModel, KnowledgeBundle, KnowledgeEntry, KnowledgeDeployment,
                              KnowledgeContext, KnowledgePrincipal, KnowledgeQuery, retrieve_knowledge, constant_knowledge, text_score)

_LOCK = threading.RLock()
_ROOT = Path(__file__).resolve().parent / "knowledge"
KNOWLEDGE_MAX_BYTES = 16 * 1024 * 1024
logger = logging.getLogger("embodiednpc.backend.knowledge")


class KnowledgeStoreError(ValueError):
    pass


class KnowledgeOverrides(KnowledgeModel):
    """Read-only v1 migration input; never written by the current Store."""
    schema_version: Literal[1] = 1
    revision: int = Field(default=0, ge=0, strict=True)
    updated_at: datetime | None = None
    entries: tuple[KnowledgeEntry, ...] = ()
    deleted_ids: tuple[str, ...] = ()

    @model_validator(mode="after")
    def unique_ids(self):
        ids = [e.knowledge_id for e in self.entries]
        if (len(ids) != len(set(ids)) or len(self.deleted_ids) != len(set(self.deleted_ids))
                or set(ids).intersection(self.deleted_ids)
                or any(not i.strip() or len(i) > 120 for i in self.deleted_ids)):
            raise ValueError("KNOWLEDGE_OVERRIDE_INVALID")
        return self


class KnowledgeCatalog(KnowledgeModel):
    schema_version: Literal[2] = 2
    bundle_version: str = Field(min_length=1, max_length=80)
    revision: int = Field(default=0, ge=0, strict=True)
    updated_at: datetime
    entries: tuple[KnowledgeEntry, ...]
    # IDs only: permit the explicit legacy restore operation without reading
    # template content on a catalog GET. These IDs never add runtime entries.
    template_ids: tuple[str, ...] = ()

    @model_validator(mode="after")
    def validate_catalog(self):
        ids = [e.knowledge_id for e in self.entries]
        if len(ids) != len(set(ids)) or len(self.template_ids) != len(set(self.template_ids)):
            raise ValueError("KNOWLEDGE_ID_DUPLICATE")
        if any(not i.strip() or len(i) > 120 for i in self.template_ids):
            raise ValueError("KNOWLEDGE_TEMPLATE_IDS_INVALID")
        if self.updated_at.tzinfo is None:
            raise ValueError("KNOWLEDGE_TIME_REQUIRES_TIMEZONE")
        return self


def _read_json(path: Path):
    try:
        with path.open("rb") as stream:
            raw = stream.read(KNOWLEDGE_MAX_BYTES + 1)
        if len(raw) > KNOWLEDGE_MAX_BYTES:
            raise ValueError("too large")
        return raw, json.loads(raw, object_pairs_hook=_reject_duplicates)
    except FileNotFoundError:
        raise
    except (OSError, ValueError, RecursionError):
        raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None


def read_document(path: Path, domain):
    try:
        return domain.model_validate(_read_json(path)[1])
    except (ValueError, RecursionError):
        raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None


class WorldKnowledgeStore:
    def __init__(self, path: Path | None = None, builtin_path: Path | None = None):
        self.path = Path(path) if path is not None else Path(os.getenv(
            "EMBODIEDNPC_KNOWLEDGE_FILE", str(_ROOT.parents[1] / "data" / "world_knowledge.json")))
        self.builtin_path = Path(builtin_path) if builtin_path is not None else _ROOT / "cafedemo_v2.json"

    @property
    def backup_path(self):
        return self.path.with_name(self.path.name + ".backups")

    @contextmanager
    def _writing(self):
        with _LOCK:
            try:
                guard = _PublicationLock(self.path.with_name(self.path.name + ".lock"))
            except OSError:
                raise KnowledgeStoreError("KNOWLEDGE_STORE_BUSY") from None
            try:
                yield
            finally:
                guard.close()

    def _current(self):
        try:
            _, value = _read_json(self.path)
        except FileNotFoundError:
            raise KnowledgeStoreError("KNOWLEDGE_INITIALIZATION_REQUIRED") from None
        if isinstance(value, dict) and value.get("schema_version", 1) == 1:
            # Reads never consult the template or perform migration.
            raise KnowledgeStoreError("KNOWLEDGE_MIGRATION_REQUIRED")
        try:
            return KnowledgeCatalog.model_validate(value)
        except (ValueError, RecursionError):
            raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None

    def _backup(self, purpose, raw):
        digest = hashlib.sha256(raw).hexdigest()
        target = self.backup_path / f"{purpose}-{digest}.json"
        try:
            if target.exists():
                if target.read_bytes() != raw:
                    raise KnowledgeStoreError("KNOWLEDGE_BACKUP_INVALID")
            else:
                atomic_json_write(target, raw, max_bytes=KNOWLEDGE_MAX_BYTES)
        except (OSError, ModelConfigStoreError):
            raise KnowledgeStoreError("KNOWLEDGE_BACKUP_FAILED") from None

    def _write(self, document):
        payload = document.model_dump_json(indent=2).encode("utf-8") + b"\n"
        try:
            atomic_json_write(self.path, payload, max_bytes=KNOWLEDGE_MAX_BYTES)
        except ModelConfigStoreError:
            raise KnowledgeStoreError("KNOWLEDGE_WRITE_FAILED") from None
        return document

    def initialize(self):
        """Explicit startup/CLI entry. Existing v2 never reads the template."""
        with self._writing():
            try:
                old_raw, value = _read_json(self.path)
            except FileNotFoundError:
                if self.backup_path.exists():
                    raise KnowledgeStoreError("KNOWLEDGE_CURRENT_FILE_MISSING") from None
                old_raw, value = None, None
            if isinstance(value, dict) and value.get("schema_version") == 2:
                return self._current()
            if value is not None:
                try:
                    previous = KnowledgeOverrides.model_validate(value)
                except (ValueError, RecursionError):
                    raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None
            elif old_raw is not None:  # A JSON null document is corruption, not first use.
                raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID")
            else:
                previous = KnowledgeOverrides()
            template_raw, template_value = _read_json(self.builtin_path)
            try:
                template = KnowledgeBundle.model_validate(template_value)
                entries = {e.knowledge_id: e for e in template.entries}
                entries.update({e.knowledge_id: e for e in previous.entries})
                for key in previous.deleted_ids:
                    entries.pop(key, None)
                document = KnowledgeCatalog(bundle_version=template.bundle_version,
                    revision=previous.revision, updated_at=datetime.now(timezone.utc),
                    entries=tuple(entries[key] for key in sorted(entries)),
                    template_ids=tuple(e.knowledge_id for e in template.entries))
            except (ValueError, RecursionError):
                raise KnowledgeStoreError("KNOWLEDGE_DOCUMENT_INVALID") from None
            # Both source bytes survive before the only commit point. A retry
            # reuses hash-addressed backups; schema_version=2 ends migration.
            if old_raw is not None:
                self._backup("legacy", old_raw)
            self._backup("template", template_raw)
            self._write(document)
            logger.info("knowledge_catalog_ready schema_version=2 revision=%d entries=%d migrated=%s",
                        document.revision, len(document.entries), old_raw is not None)
            return document

    def snapshot(self) -> tuple[KnowledgeBundle, KnowledgeCatalog, tuple[KnowledgeEntry, ...]]:
        with _LOCK:
            current = self._current()
            # Preserve the existing request/index snapshot interface and the
            # exact legacy bundle_version used by the unchanged vector hash.
            bundle = KnowledgeBundle(bundle_version=current.bundle_version, entries=current.entries)
            return bundle, current, current.entries

    @staticmethod
    def _check_revision(current, expected_revision):
        if type(expected_revision) is not int or expected_revision < 0:
            raise KnowledgeStoreError("KNOWLEDGE_REVISION_INVALID")
        if current.revision != expected_revision:
            raise KnowledgeStoreError("KNOWLEDGE_REVISION_CONFLICT")

    def manage(self, operation: Literal["upsert", "delete", "restore", "restore-all"], *,
               expected_revision: int, entry: KnowledgeEntry | None = None,
               knowledge_id: str | None = None) -> KnowledgeCatalog:
        with self._writing():
            previous = self._current()
            self._check_revision(previous, expected_revision)
            entries = {e.knowledge_id: e for e in previous.entries}
            now = datetime.now(timezone.utc)
            version, template_ids = previous.bundle_version, previous.template_ids
            if operation == "upsert":
                if not isinstance(entry, KnowledgeEntry) or knowledge_id is not None:
                    raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
                entry = KnowledgeEntry.model_validate_json(entry.model_dump_json())
                older = entries.get(entry.knowledge_id)
                entries[entry.knowledge_id] = entry.model_copy(update={
                    "version": older.version + 1 if older else 1, "updated_at": now})
            elif operation == "delete" and knowledge_id and entry is None:
                if knowledge_id not in entries:
                    raise KnowledgeStoreError("KNOWLEDGE_ID_UNKNOWN")
                del entries[knowledge_id]
            elif operation in {"restore", "restore-all"} and entry is None:
                template = read_document(self.builtin_path, KnowledgeBundle)
                defaults = {e.knowledge_id: e for e in template.entries}
                if operation == "restore":
                    if not knowledge_id or knowledge_id not in defaults:
                        raise KnowledgeStoreError("KNOWLEDGE_NO_DEFAULT")
                    entries[knowledge_id] = defaults[knowledge_id]
                    template_ids = tuple(dict.fromkeys((*template_ids, knowledge_id)))
                elif knowledge_id is None:
                    entries = defaults
                    version = template.bundle_version
                    template_ids = tuple(defaults)
                else:
                    raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
                self._backup("before-restore", self.path.read_bytes())
            else:
                raise KnowledgeStoreError("KNOWLEDGE_OPERATION_INVALID")
            changed = KnowledgeCatalog(bundle_version=version, revision=previous.revision + 1,
                updated_at=now, entries=tuple(entries[key] for key in sorted(entries)), template_ids=template_ids)
            return self._write(changed)

    def replace(self, catalog: KnowledgeCatalog, *, expected_revision: int) -> KnowledgeCatalog:
        """Complete replacement primitive; caller confirmation/preview belongs to tooling."""
        catalog = KnowledgeCatalog.model_validate_json(catalog.model_dump_json())
        with self._writing():
            previous = self._current()
            self._check_revision(previous, expected_revision)
            changed = catalog.model_copy(update={"revision": previous.revision + 1,
                "updated_at": datetime.now(timezone.utc)})
            self._backup("before-replace", self.path.read_bytes())
            return self._write(changed)


@dataclass(frozen=True)
class KnowledgeReadSnapshot:
    """Run-local trusted catalog, never accepted from the model or persisted."""
    context: KnowledgeContext
    principal: KnowledgePrincipal | None = None
    catalog: KnowledgeCatalog | None = None


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

    def capture(self, request: CognitiveRequest) -> KnowledgeReadSnapshot:
        principal = self.deployment.resolve(request) if self.deployment else None
        if principal is None:
            return KnowledgeReadSnapshot(KnowledgeContext(status="UNAUTHORIZED"))
        event = request.context.current_event
        if event is None or event.event_type not in {"player.utterance", "npc.dialogue_continue"}:
            return KnowledgeReadSnapshot(KnowledgeContext(status="NO_MATCH"))
        try:
            _, current, entries = self.store.snapshot()
            constants = constant_knowledge(entries, principal)
            return KnowledgeReadSnapshot(KnowledgeContext(
                status="OK" if constants else "NO_MATCH", bundle_version=current.bundle_version,
                revision=current.revision, constant_items=constants), principal, current)
        except (OSError, ValueError, RecursionError):
            return KnowledgeReadSnapshot(KnowledgeContext(status="UNAVAILABLE"))

    @staticmethod
    def automatic_query(request: CognitiveRequest, recent_dialogue=()) -> str:
        event = request.context.current_event
        if event is None or event.event_type != "player.utterance":
            return ""
        current = next((p.value for p in event.payload if p.key == "text"), "").strip()
        # Discourse continuity only, never an intent/permission or fact classifier.
        # A fresh explicit topic uses the current text; short follow-ups may carry
        # just the nearest verified player/NPC topic, never the whole history.
        followup = re.match(r"^(那(?:个|种|么|哪|一)|这(?:个|种|样|些)|它|他们|她们|如果|可是|不过|还有|那么|所以|and\b|but\b|then\b|what about\b|which\b)", current, re.I)
        if not followup:
            return current[:512]
        session = getattr(recent_dialogue, "current_session_id", None)
        sources = getattr(recent_dialogue, "sources", {})
        recent = []
        for turn in recent_dialogue or ():
            if "role" in turn:
                if session and sources.get(turn.get("seq"), {}).get("source_session_id") != session:
                    continue
                if turn.get("role") in {"player", "npc"}:
                    recent.append(turn.get("text", ""))
            else:
                recent.extend([turn.get("player_text", ""), *turn.get("npc_utterances", [turn.get("npc_text", "")])])
        context = "\n".join(text for text in recent[-2:] if text)
        current = current[:384] if context else current[:512]
        return (context[-max(0, 511-len(current)):] + "\n" + current) if context else current

    def prefetch(self, snapshot: KnowledgeReadSnapshot, request: CognitiveRequest, recent_dialogue=()) -> KnowledgeContext:
        query = self.automatic_query(request, recent_dialogue)
        if not query:
            return self.validate_snapshot(snapshot, snapshot.context)
        started = time.perf_counter()
        result = self._query_snapshot(snapshot, query)
        if snapshot.catalog is None or result.status in {"UNAVAILABLE", "UNAUTHORIZED", "SNAPSHOT_CHANGED"}:
            return result
        by_id = {entry.knowledge_id: entry for entry in snapshot.catalog.entries}
        # RRF/dense scores order candidates but do not establish relevance. Reuse
        # the existing lexical eligibility on names/keywords as topic support (not
        # generic body phrases such as "suitable"),
        # without changing ranking, adding a cosine threshold, or filtering a
        # model's explicit semantic query. Dense-only matches remain supplementable.
        supported = tuple(item for item in result.items if text_score(by_id[item["knowledge_id"]].model_copy(update={"content": ""}), query) > 0)
        result = result.model_copy(update={"items": supported,
            "status": "OK" if supported or result.constant_items else "NO_MATCH"})
        logger.info("knowledge_prefetch request_id=%s ids=%s latency_ms=%.1f", request.request_id,
                    [item["knowledge_id"] for item in supported], (time.perf_counter()-started)*1000)
        return result

    @staticmethod
    def merge_results(automatic: KnowledgeContext | None, supplement: KnowledgeContext) -> KnowledgeContext:
        if automatic is None or supplement.status == "SNAPSHOT_CHANGED":
            return supplement
        # A run can expose at most THREE distinct retrieval IDs, across both
        # inferences. Automatic evidence reserves its slots; a supplement fills
        # vacancies and never obtains another Top-K allowance.
        items = list(automatic.items)
        seen = {item["knowledge_id"] for item in (*automatic.constant_items, *items)}
        excluded = False
        for item in supplement.items:
            if item["knowledge_id"] in seen:
                continue
            if len(items) >= 3:
                excluded = True
                continue
            items.append(item); seen.add(item["knowledge_id"])
        return automatic.model_copy(update={"items": tuple(items),
            "supplement_status": "BUDGET_EXHAUSTED" if excluded else supplement.status,
            "status": "OK" if items or automatic.constant_items else supplement.status,
            "truncated": automatic.truncated or supplement.truncated or excluded})

    def validate_snapshot(self, snapshot: KnowledgeReadSnapshot, context: KnowledgeContext) -> KnowledgeContext:
        if snapshot.catalog is None:
            return context
        try:
            # Compare the complete catalog as well as revision: an externally replaced
            # file must not reuse a revision to resurrect deleted or private evidence.
            if self.store.snapshot()[1] == snapshot.catalog:
                return context
            status = "SNAPSHOT_CHANGED"
        except (OSError, ValueError, RecursionError):
            status = "UNAVAILABLE"
        return KnowledgeContext(status=status, revision=snapshot.catalog.revision,
                                bundle_version=snapshot.catalog.bundle_version)

    def query(self, snapshot: KnowledgeReadSnapshot, semantic_query: str) -> KnowledgeContext:
        return self._query_snapshot(snapshot, semantic_query)

    def _query_snapshot(self, snapshot: KnowledgeReadSnapshot, semantic_query: str) -> KnowledgeContext:
        context = self.validate_snapshot(snapshot, snapshot.context)
        if snapshot.catalog is None or context.status in {"SNAPSHOT_CHANGED", "UNAVAILABLE"}:
            return context
        try:
            result = retrieve_knowledge(snapshot.catalog.entries, snapshot.principal,
                KnowledgeQuery(text=semantic_query), bundle_version=snapshot.catalog.bundle_version,
                revision=snapshot.catalog.revision, ranker=self.ranker, defer_budget=True)
            result = result.model_copy(update={"constant_items": context.constant_items})
            return self.validate_snapshot(snapshot, result)
        except (OSError, ValueError, RecursionError):
            return context.model_copy(update={"status": "UNAVAILABLE", "items": ()})

    def resolve(self, request: CognitiveRequest) -> KnowledgeContext:
        principal = self.deployment.resolve(request) if self.deployment else None
        if principal is None:
            return KnowledgeContext(status="UNAUTHORIZED")
        event = request.context.current_event
        # Fixed mode and restricted action recovery bypass this resolver in the
        # entry point. Continuation receives background, never a fabricated query.
        if event is None or event.event_type not in {"player.utterance", "npc.dialogue_continue"}:
            return KnowledgeContext(status="NO_MATCH")
        text = (next((p.value for p in event.payload if p.key == "text"), "")
                if event.event_type == "player.utterance" else "")
        try:
            bundle, current, entries = self.store.snapshot()
            constants = constant_knowledge(entries, principal)
            result = retrieve_knowledge(entries, principal, KnowledgeQuery(text=text[:512]),
                bundle_version=bundle.bundle_version, revision=current.revision,
                ranker=self.ranker, defer_budget=True)
            return result.model_copy(update={"constant_items": constants,
                "status": "OK" if constants else result.status})
        except (OSError, ValueError, RecursionError):
            logger.warning("knowledge_context_unavailable request_id=%s", request.request_id)
            return KnowledgeContext(status="UNAVAILABLE")
