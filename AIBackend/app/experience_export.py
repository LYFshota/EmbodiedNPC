"""Local, player-scoped read-only export. Never bootstraps/migrates a database or calls a model."""
from __future__ import annotations

import asyncio
from bisect import bisect_left, bisect_right
from contextlib import closing
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sqlite3
import tempfile
import time
from zipfile import ZipFile, ZIP_DEFLATED

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import FileResponse

from .canonical_memory_read_repository import event_player_scope_allowed, mentions_player, player_identity_allowed, validate_demo_player_uid
from .experience_ledger import ExperienceLedger
from .task_repository import TaskRepository
from .task_models import TaskCommandResult


class ExportLimitError(RuntimeError):
    """A complete snapshot cannot fit the export's resource envelope; never truncate it."""


class _ExportFileResponse(FileResponse):
    """Starlette background tasks are skipped when sending fails/disconnects."""
    async def __call__(self, scope, receive, send):
        try:
            await super().__call__(scope, receive, send)
        finally:
            Path(self.path).unlink(missing_ok=True)


MAX_SCAN_BYTES = 64 * 1024 * 1024
MAX_SCAN_ROWS = 250_000
MAX_ARCHIVE_BYTES = 256 * 1024 * 1024
MAX_SECONDS = 60


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def safe_metadata(value):
    """Configuration/structural metadata only. Authored prose is preserved separately, verbatim."""
    if isinstance(value, dict):
        return {k: safe_metadata(v) for k, v in value.items()
                if not re.search(r"(?i)(api.?key|authorization|password|credential|secret|token|cookie|base_url|file_path|directory)", k)}
    if isinstance(value, list):
        return [safe_metadata(v) for v in value]
    if isinstance(value, str) and (re.match(r"^(?:[A-Za-z]:[\\/]|\\\\|/(?:home|Users|tmp|var)/)", value)
                                   or re.match(r"(?i)^Bearer\s", value)):
        return "[local metadata omitted]"
    return value


def row_document(row):
    result = {}
    for k, v in dict(row).items():
        if k.endswith("_json"):
            result[k[:-5]] = safe_metadata(json.loads(v)) if v is not None else None
        else:
            result[k] = v
    return result


class ExperienceExport:
    def __init__(self, database_path, knowledge_store, player_uid, *, runtime=None):
        self.path = Path(database_path)
        self.store = knowledge_store
        self.uid = validate_demo_player_uid(player_uid)  # Host deployment, never a query parameter/model choice.
        self.runtime = runtime or {}

    def database_snapshot(self):
        if any(p.is_symlink() or getattr(p.lstat(), "st_file_attributes", 0) & 0x400
               for p in (self.path, *self.path.parents)):
            raise ValueError("DATABASE_LINK_NOT_ALLOWED")
        started, scanned_bytes, scanned_rows = time.monotonic(), 0, 0
        def check():
            if time.monotonic() - started > MAX_SECONDS:
                raise ExportLimitError("EXPORT_SNAPSHOT_TIMEOUT")
        # mode=ro sees live WAL. immutable=1 would silently miss committed WAL data.
        with closing(sqlite3.connect(self.path.resolve().as_uri() + "?mode=ro", uri=True, timeout=3)) as db:
            db.row_factory = sqlite3.Row
            db.execute("PRAGMA query_only=ON")
            db.execute("BEGIN")
            db.setlimit(sqlite3.SQLITE_LIMIT_LENGTH, 16 * 1024 * 1024)
            db.set_progress_handler(lambda: int(time.monotonic() - started > MAX_SECONDS), 10000)
            tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            data, sections = {}, {}

            def rows(table):
                nonlocal scanned_bytes, scanned_rows
                check()
                if table not in tables:
                    raise ValueError("TABLE_UNAVAILABLE:" + table)
                result = []
                for row in db.execute('SELECT * FROM "' + table + '"'):
                    scanned_rows += 1
                    scanned_bytes += sum(len(v.encode("utf-8")) if isinstance(v, str) else len(v) if isinstance(v, bytes) else 8 for v in row)
                    if scanned_rows > MAX_SCAN_ROWS or scanned_bytes > MAX_SCAN_BYTES:
                        raise ExportLimitError("EXPORT_SNAPSHOT_LIMIT")
                    check()
                    result.append(row)
                return result

            def section(name, source, collect):
                try:
                    value, count = collect()
                    data[name] = value
                    sections[name] = dict(status="ok", count=count, source=source)
                except (sqlite3.Error, ValueError, KeyError, TypeError):
                    check()
                    data[name] = None
                    sections[name] = dict(status="unavailable", count=0, source=source, error="PERSISTED_DATA_UNREADABLE")

            events, decoded = [], []
            malformed = 0
            for row in rows("experience_events"):
                try:
                    event = ExperienceLedger._row_to_event(row)
                    decoded.append(event)
                except (ValueError, KeyError, TypeError):
                    malformed += 1
            def trace_key(e): return (e.observer_id, e.source_session_id, e.trace_id)
            direct = {e.event_id for e in decoded if any(player_identity_allowed(p, self.uid) for p in e.persistent_participants)
                      and event_player_scope_allowed(e, self.uid)}
            foreign_traces = {trace_key(e) for e in decoded if any(mentions_player(p) and not player_identity_allowed(p, self.uid)
                              for p in e.persistent_participants)}
            own_traces = {trace_key(e) for e in decoded if e.event_id in direct and e.trace_id
                          and e.event_type in {"dialogue_input", "dialogue_response"}} - foreign_traces
            for event in decoded:
                correlated = (event.trace_id and trace_key(event) in own_traces
                    and event.source_type in {"goal_outcome", "skill_result"} and event_player_scope_allowed(event, self.uid))
                # Failed/cancelled actions may have no Player recipient. Only an unambiguous
                # persisted dialogue trace in the same NPC/session can certify their attribution.
                if event.event_id not in direct and not correlated: continue
                item = event.model_dump(mode="json")
                item["structured_payload"] = safe_metadata(item["structured_payload"])
                item["export_player_attribution"] = "persistent_participant" if event.event_id in direct else "persisted_dialogue_trace"
                events.append(item)
            events.sort(key=lambda e: (e["ingested_at_utc"], e["observer_id"], e["experience_seq"]))
            safe_ids = {e["event_id"] for e in events}
            data["experiences"] = events
            sections["experiences"] = dict(status="partial" if malformed else "ok", count=len(events),
                source="ExperienceLedger", excluded_other_or_unbound_player_scope=len(decoded)-len(events), unreadable_records=malformed)
            conversations = [dict(event_id=e["event_id"], observer_id=e["observer_id"], experience_seq=e["experience_seq"],
                role="player" if e["event_type"] == "dialogue_input" else "npc", text=e["natural_language_payload"] or "",
                actor_entity_id=e["actor_entity_id"], target_entity_ids=e["target_entity_ids"],
                persistent_participants=e["persistent_participants"], conversation_id=e["conversation_id"],
                source_session_id=e["source_session_id"], request_id=e["request_id"], trace_id=e["trace_id"],
                occurred_at=e["occurred_at"], clock_domain=e["clock_domain"], ingested_at_utc=e["ingested_at_utc"],
                world_revision=e["world_revision"]) for e in events
                if e["event_type"] in {"dialogue_input", "dialogue_response"} and e["source_type"] == "dialogue"]
            data["conversations"] = conversations
            sections["conversations"] = dict(status=sections["experiences"]["status"], count=len(conversations), source="ExperienceLedger displayed dialogue")

            sequences = {}
            for e in events: sequences.setdefault(e["observer_id"], []).append(e["experience_seq"])
            for values in sequences.values(): values.sort()
            def episodes():
                result = []
                for r in rows("episodes"):
                    seqs = sequences.get(r["observer_id"], [])
                    count = bisect_right(seqs, r["end_seq"]) - bisect_left(seqs, r["start_seq"])
                    if count:
                        result.append(dict(row_document(r), exported_event_count=count, complete_episode_in_player_scope=count == r["event_count"]))
                return result, len(result)
            section("episodes", "EpisodeRepository", episodes)

            memory_ids = set()
            def memories():
                nonlocal memory_ids
                records, subjects, evidence = rows("memory_records"), rows("memory_subjects"), rows("memory_evidence")
                by_evidence, by_subject = {}, {}
                for r in evidence: by_evidence.setdefault(r["memory_id"], set()).add(r["experience_event_id"])
                for r in subjects: by_subject.setdefault(r["memory_id"], []).append(r)
                for r in records:
                    refs = by_evidence.get(r["memory_id"], set())
                    foreign = any((s["identity_namespace"].casefold() == "player" or s["identity_type"].casefold() == "player")
                        and (s["identity_namespace"], s["identity_type"], s["persistent_id"]) != ("player", "Player", self.uid)
                        for s in by_subject.get(r["memory_id"], []))
                    if refs and refs <= safe_ids and not foreign:
                        memory_ids.add(r["memory_id"])
                links = rows("memory_insight_sources")
                # A derived insight must not smuggle excluded source memories into its prose.
                while True:
                    remove = {r["memory_id"] for r in links if r["memory_id"] in memory_ids and r["source_memory_id"] not in memory_ids}
                    if not remove: break
                    memory_ids -= remove
                selected = [row_document(r) for r in records if r["memory_id"] in memory_ids]
                result = {"records": selected, "subjects": [row_document(r) for r in subjects if r["memory_id"] in memory_ids],
                    "evidence": [row_document(r) for r in evidence if r["memory_id"] in memory_ids]}
                for table in ("memory_semantic_facts", "memory_profile_facts", "memory_derived_insights", "memory_insight_sources", "memory_insight_episodes"):
                    result[table] = [row_document(r) for r in rows(table) if r["memory_id"] in memory_ids]
                result["relations"] = [row_document(r) for r in rows("memory_relations")
                    if r["new_memory_id"] in memory_ids and r["old_memory_id"] in memory_ids]
                episode_ids = {r["episode_id"] for r in (data.get("episodes") or [])}
                result["consolidation_jobs"] = [row_document(r) for r in rows("memory_jobs") if r["episode_id"] in episode_ids]
                result["excluded_outside_player_evidence_scope"] = len(records) - len(selected)
                return result, len(selected)
            section("memories", "CanonicalMemoryRepository and source evidence", memories)

            def relationships():
                result = []
                for r in rows("relationship_reflections"):
                    if (r["subject_identity_namespace"], r["subject_identity_type"], r["subject_persistent_id"]) != ("player", "Player", self.uid): continue
                    refs = set(json.loads(r["evidence_event_ids_json"]))
                    state = json.loads(r["pair_state_json"]) if r["pair_state_json"] else None
                    def anchors(v):
                        if isinstance(v, dict):
                            return ({v["event_id"]} if "event_id" in v else set()).union(*(anchors(x) for x in v.values()))
                        if isinstance(v, list): return set().union(*(anchors(x) for x in v))
                        return set()
                    if refs and (refs | anchors(state)) <= safe_ids:
                        result.append(row_document(r))
                return result, len(result)
            section("relationships", "RelationshipReflectionRepository (all retained pair snapshots)", relationships)

            def tasks():
                selected = []
                for r in rows("npc_tasks"):
                    key = tuple(json.loads(r["player_key"])) if r["player_key"] else None
                    source = json.loads(r["source_ref_json"])
                    if key == ("player", "Player", self.uid) or (key is None and source.get("experience_id") in safe_ids):
                        selected.append(TaskRepository._record(r).model_dump(mode="json"))
                ids = {r["task_id"] for r in selected}
                commands = []
                by_id = {r["task_id"]: r for r in selected}
                for r in rows("npc_task_commands"):
                    if r["task_id"] not in ids: continue
                    result = TaskCommandResult.model_validate_json(r["result_json"])
                    if (result.task.task_id != r["task_id"] or result.task.owner.model_dump(mode="json") != by_id[r["task_id"]]["owner"]
                            or (result.task.player.model_dump(mode="json") if result.task.player else None) != by_id[r["task_id"]]["player"]):
                        raise ValueError("TASK_COMMAND_SCOPE_MISMATCH")
                    commands.append(row_document(r))
                return dict(records=selected, commands=commands), len(selected)
            section("tasks", "TaskRepository including all retained statuses and commands", tasks)
            actions = [e for e in events if e["source_type"] in {"goal_outcome", "skill_result"}
                       or e["structured_payload"].get("initiative") == "action_feedback"]
            data["actions"] = {"ledger_results": actions, "task_executions": [dict(task_id=t["task_id"], status=t["status"], execution=t["execution"])
                for t in (data.get("tasks") or {}).get("records", []) if t.get("execution") is not None]}
            sections["actions"] = dict(status="ok" if all(sections[n]["status"] == "ok" for n in ("tasks", "experiences")) else "partial",
                dependencies={n: sections[n]["status"] for n in ("tasks", "experiences")}, count=len(actions) + len(data["actions"]["task_executions"]),
                source="Persisted Ledger outcomes/feedback and Task execution receipts; generated proposals are not success")
            # Retired entity-only stores cannot be safely joined to a durable player.
            legacy = ("episodic_memories", "relationship_states", "relationship_events", "memory_v1_records", "memory_v1_facts")
            data["legacy_unscoped"] = {t: db.execute('SELECT COUNT(*) FROM "' + t + '"').fetchone()[0] for t in legacy if t in tables}
            sections["legacy_unscoped"] = dict(status="partial" if any(data["legacy_unscoped"].values()) else "ok",
                count=0, source="Retired stores", note="Entity-only legacy records are excluded; no durable current-player attribution.")
            check()
            return data, sections

    def build(self):
        sections, data = {}, {}
        try:
            data, sections = self.database_snapshot()
        except (sqlite3.Error, ValueError, OSError):
            for name in ("experiences", "episodes", "conversations", "memories", "relationships", "tasks", "actions"):
                sections[name] = dict(status="unavailable", count=0, source="SQLite read transaction", error="DATABASE_SNAPSHOT_UNAVAILABLE")
        try:
            bundle, current, entries = self.store.snapshot()  # No initialize/migration/write on export.
            data["world_knowledge"] = current.model_dump(mode="json")
            sections["world_knowledge"] = dict(status="ok", count=len(entries), source="WorldKnowledgeStore current catalog")
        except (OSError, ValueError):
            sections["world_knowledge"] = dict(status="unavailable", count=0, source="WorldKnowledgeStore", error="KNOWLEDGE_SNAPSHOT_UNAVAILABLE")
        try:
            from .language_projection import _resources
            data["persona_language_projections"] = _resources()[0]
            sections["persona_language_projections"] = dict(status="ok", count=len(data["persona_language_projections"]["entries"]),
                source="Existing model-only Persona projections; apply only on exact source/projection hash match, otherwise keep the effective Unity text")
        except (OSError, ValueError, KeyError):
            sections["persona_language_projections"] = dict(status="unavailable", count=0, error="PERSONA_PROJECTION_UNAVAILABLE")
        manifest = dict(schema_version=1, exported_at_utc=utc_now(), player_identity=dict(identity_namespace="player", identity_type="Player", persistent_id=self.uid),
            sections=sections, runtime=safe_metadata(self.runtime),
            complete=all(s["status"] == "ok" for s in sections.values()),
            consistency="One read-only SQLite transaction; world knowledge is a separately revisioned snapshot. Only retained current-player evidence is exported; no row limits.",
            limitations=["No reconstruction of deleted history or unpersisted RAG hits/model requests.", "No vectors, weights, credentials, raw technical logs or SQLite/WAL files.",
                         "Episodes spanning other players include metadata and only this player's exported evidence, explicitly marked."],
            limits=dict(scan_bytes=MAX_SCAN_BYTES, scan_rows=MAX_SCAN_ROWS, uncompressed_bytes=MAX_ARCHIVE_BYTES,
                        snapshot_seconds=MAX_SECONDS, policy="Limits fail the export; records are never silently truncated."))
        fd, filename = tempfile.mkstemp(prefix="embodiednpc-experience-", suffix=".zip")
        import os
        os.close(fd)
        path = Path(filename)
        try:
            with ZipFile(path, "w", ZIP_DEFLATED) as z:
                size = 0
                started = time.monotonic()
                def write(stream, text):
                    nonlocal size
                    chunk = text.encode("utf-8")
                    size += len(chunk)
                    if size > MAX_ARCHIVE_BYTES or time.monotonic() - started > MAX_SECONDS:
                        raise ExportLimitError("EXPORT_ARCHIVE_LIMIT")
                    stream.write(chunk)
                def document(name, value):
                    with z.open(name, "w") as stream:
                        for chunk in json.JSONEncoder(ensure_ascii=False, indent=2, allow_nan=False).iterencode(value):
                            write(stream, chunk)
                for name, value in data.items():
                    if value is None: continue
                    if name in {"experiences", "conversations"}:
                        with z.open(name + ".jsonl", "w") as stream:
                            for row in value: write(stream, json.dumps(row, ensure_ascii=False, allow_nan=False) + "\n")
                    else:
                        document(name + ".json", value)
                with z.open("conversations.md", "w") as stream:
                    write(stream, "# Persisted conversations\n\nTimes are Ledger ingestion UTC; relative event clocks are preserved in JSONL. NPC rows are presented speech, not unshown model drafts.\n\n")
                    for r in data.get("conversations", []):
                        stamp = datetime.fromtimestamp(r["ingested_at_utc"], timezone.utc).isoformat()
                        write(stream, f"### {stamp} | {r['observer_id']} | {r['role']}\nSession: `{r['source_session_id']}` · Conversation: `{r['conversation_id']}` · Event: `{r['event_id']}`\n\n")
                        for line in r["text"].splitlines(): write(stream, "> " + line + "\n")
                        write(stream, "\n")
                document("backend_manifest.json", manifest)
            return path
        except BaseException:
            path.unlink(missing_ok=True)
            raise


def create_experience_export_router(exporter, auth):
    router, gate = APIRouter(), asyncio.Lock()

    @router.get("/v1/experience-export", dependencies=[Depends(auth.require)])
    async def export():
        if gate.locked(): raise HTTPException(409, detail="EXPERIENCE_EXPORT_BUSY")
        async with gate:
            # Await cleanup even if the HTTP caller disconnects, so a completed temp ZIP never leaks.
            job = asyncio.create_task(asyncio.to_thread(exporter.build))
            try:
                path = await asyncio.shield(job)
            except ExportLimitError as error:
                raise HTTPException(413, detail=str(error)) from error
            except asyncio.CancelledError:
                path = await job
                path.unlink(missing_ok=True)
                raise
            return _ExportFileResponse(path, media_type="application/zip", filename="experience-backend.zip")
    return router
