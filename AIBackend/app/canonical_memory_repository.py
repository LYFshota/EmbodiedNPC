"""Canonical Memory writes guarded by the durable job finalization transaction."""

from __future__ import annotations

from collections.abc import Callable
from contextlib import closing
import json
import math
import sqlite3

from .consolidation_state import ConsolidationStateRepository
from .episode_models import DurableMemoryJob
from .episode_repository import EpisodeRepository
from .experience_ledger import ExperienceLedger
from .memory_consolidation_admission import (
    MemoryConsolidationAdmission, identity_key, raw_is_owner_speech, NPC_SELF_STATEMENT_LABEL,
)
from .memory_consolidation_context import MemoryContextResolver, TrustedPersistentIdentity
from .memory_consolidation_models import (
    AdmittedCanonicalMemory, CanonicalFinalizationOutcome, ConsolidationAdmissionBatch,
)
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
from .semantic_memory_models import SEMANTIC_MEMORY_RECORD_VERSION
from .memory_maintenance_repository import enqueue_fact
from .memory_reflection_repository import enqueue_reflection


class CanonicalMemoryFinalizationError(ValueError):
    pass


class CanonicalMemoryRepository:
    def __init__(self, connection_factory: Callable[[], sqlite3.Connection], *, admission=None) -> None:
        self._connection_factory = connection_factory
        self._admission = admission or MemoryConsolidationAdmission()

    def list_known_subjects(self, owner: TrustedPersistentIdentity, *, limit: int = 16) -> tuple[TrustedPersistentIdentity, ...]:
        """Enumerate only subjects linked to this owner's admitted canonical memories."""
        bounded_limit = max(1, min(limit, 32))
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT DISTINCT s.identity_namespace,s.identity_type,s.persistent_id "
                "FROM memory_subjects s JOIN memory_records r ON r.memory_id=s.memory_id "
                "WHERE r.identity_namespace=? AND r.identity_type=? AND r.persistent_id=? "
                "AND r.scope='NPC_PRIVATE' AND r.status IN ('ACTIVE','SUPERSEDED','CONFLICTED') "
                "ORDER BY s.identity_namespace,s.identity_type,s.persistent_id LIMIT ?",
                (owner.identity_namespace, owner.identity_type, owner.persistent_id, bounded_limit),
            ).fetchall()
        return tuple(TrustedPersistentIdentity(*row) for row in rows)

    def finalize_success(self, *, job_id: str, attempt_generation: int, expected_input_hash: str,
                         batch: ConsolidationAdmissionBatch, now: float) -> CanonicalFinalizationOutcome:
        if not math.isfinite(now) or now < 0:
            raise CanonicalMemoryFinalizationError("INVALID_FINALIZATION_TIME")
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                row = connection.execute("SELECT * FROM memory_jobs WHERE job_id=?", (job_id,)).fetchone()
                if row is None:
                    raise CanonicalMemoryFinalizationError("JOB_NOT_FOUND")
                job = DurableMemoryJob(**dict(row))
                if job.extractor_version != SEMANTIC_MEMORY_CONSOLIDATOR_VERSION:
                    raise CanonicalMemoryFinalizationError("UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION")
                if job.input_hash != expected_input_hash:
                    raise CanonicalMemoryFinalizationError("JOB_INPUT_HASH_MISMATCH")
                context = batch.context
                if (context.episode_id, context.observer_entity_id, context.input_hash, context.pipeline_version) != (
                    job.episode_id, job.observer_id, job.input_hash, job.extractor_version
                ):
                    raise CanonicalMemoryFinalizationError("ADMISSION_SCOPE_MISMATCH")
                if job.attempt_generation != attempt_generation:
                    raise CanonicalMemoryFinalizationError("STALE_ATTEMPT")
                watermark = ConsolidationStateRepository(self._connection_factory).get(
                    job.observer_id, job.extractor_version
                )
                if job.status == "SUCCEEDED":
                    # The job is the durable completion marker, including a zero-memory result.
                    connection.commit()
                    return CanonicalFinalizationOutcome(False, watermark)
                if job.status != "RUNNING":
                    raise CanonicalMemoryFinalizationError("JOB_NOT_RUNNING")
                # BEGIN IMMEDIATE prevents concurrent writers. These existing readers use
                # the same database; CLOSED Episodes and their raw ledger rows are immutable.
                authoritative = MemoryContextResolver(
                    ExperienceLedger(self._connection_factory), EpisodeRepository(self._connection_factory)
                ).resolve(job)
                verified = self._admission.evaluate(authoritative, batch.response)
                if (verified.memories != batch.memories or verified.policy_version != batch.policy_version
                        or verified.rejections != batch.rejections
                        or verified.visible_event_ids != batch.visible_event_ids):
                    raise CanonicalMemoryFinalizationError("ADMISSION_INTEGRITY_MISMATCH")
                for memory in verified.memories:
                    self._write_memory(connection, memory, job, batch.policy_version, now)
                changed = connection.execute(
                    "UPDATE memory_jobs SET status='SUCCEEDED', updated_at_utc=?, finished_at_utc=?, "
                    "next_retry_at_utc=NULL, error_code=NULL WHERE job_id=? AND status='RUNNING' "
                    "AND attempt_generation=?", (now, now, job_id, attempt_generation),
                ).rowcount
                if changed != 1:
                    raise CanonicalMemoryFinalizationError("STALE_ATTEMPT")
                watermark = ConsolidationStateRepository.advance_in_transaction(
                    connection, job.observer_id, job.extractor_version, now
                )
                connection.commit()
                return CanonicalFinalizationOutcome(True, watermark)
            except Exception:
                connection.rollback()
                raise

    @staticmethod
    def _events(connection: sqlite3.Connection, memory_id: str):
        rows = connection.execute(
            "SELECT e.* FROM experience_events e JOIN memory_evidence m "
            "ON m.experience_event_id=e.event_id WHERE m.memory_id=? ORDER BY e.event_id", (memory_id,),
        ).fetchall()
        return tuple(ExperienceLedger._row_to_event(row) for row in rows)

    def _write_memory(self, connection: sqlite3.Connection, memory: AdmittedCanonicalMemory,
                      job: DurableMemoryJob, policy: str, now: float) -> bool:
        owner = identity_key(memory.owner) if memory.owner else (None, None, None)
        existing = connection.execute(
            "SELECT * FROM memory_records WHERE memory_id=?", (memory.memory_id,)
        ).fetchone()
        if existing is None:
            # Time/provenance are immediately replaced with authoritative evidence aggregates.
            connection.execute(
                "INSERT INTO memory_records(memory_id,observer_entity_id,identity_namespace,identity_type,"
                "persistent_id,memory_type,scope,summary,importance,confidence,provenance_label,"
                "provenance_set_json,occurred_start,occurred_end,learned_at,episode_id,job_id,pipeline_version,"
                "admission_policy_version,status,schema_version) "
                "VALUES(?,?,?,?,?,?,'NPC_PRIVATE',?,?,?,'', '[]',0,0,?,?,?,?,?,'ACTIVE',?)",
                (memory.memory_id, job.observer_id, *owner, memory.memory_type, memory.summary,
                 memory.importance, memory.confidence, now, job.episode_id, job.job_id,
                 job.extractor_version, policy, SEMANTIC_MEMORY_RECORD_VERSION),
            )
            connection.executemany(
                "INSERT INTO memory_subjects VALUES(?,?,?,?)",
                ((memory.memory_id, *identity_key(subject)) for subject in memory.subjects),
            )
        else:
            subjects = {tuple(row) for row in connection.execute(
                "SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?",
                (memory.memory_id,),
            )}
            if ((existing["identity_namespace"], existing["identity_type"], existing["persistent_id"]) != owner
                    or existing["memory_type"] != memory.memory_type
                    or existing["summary"] != memory.summary
                    or subjects != {identity_key(subject) for subject in memory.subjects}):
                raise CanonicalMemoryFinalizationError("DETERMINISTIC_MEMORY_INTEGRITY_CONFLICT")
        changed = False
        for event_id, sequence in memory.evidence:
            changed |= connection.execute(
                "INSERT INTO memory_evidence VALUES(?,?,?) ON CONFLICT(memory_id,experience_event_id) DO NOTHING",
                (memory.memory_id, event_id, sequence),
            ).rowcount == 1
        events = self._events(connection, memory.memory_id)
        self_statement = memory.subjects == (memory.owner,) and raw_is_owner_speech(events, memory.owner)
        incoming_ids = {event_id for event_id, _ in memory.evidence}
        incoming_self = memory.subjects == (memory.owner,) and raw_is_owner_speech(
            tuple(event for event in events if event.event_id in incoming_ids), memory.owner)
        provenance = tuple(sorted({event.provenance for event in events}))
        start, end = min(e.occurred_at for e in events), max(e.occurred_at for e in events)
        connection.execute(
            "UPDATE memory_records SET occurred_start=?,occurred_end=?,provenance_label=?,provenance_set_json=?,"
            "confidence=?,importance=? WHERE memory_id=?",
            (start, end, NPC_SELF_STATEMENT_LABEL if self_statement else
             provenance[0] if len(provenance) == 1 else "MIXED", json.dumps(provenance),
             existing["confidence"] if existing and incoming_self else
             max(existing["confidence"], memory.confidence) if existing else memory.confidence,
             existing["importance"] if existing and incoming_self else
             max(existing["importance"], memory.importance) if existing and
             memory.importance is not None else memory.importance, memory.memory_id),
        )
        if memory.memory_type == "SEMANTIC_FACT":
            current = connection.execute(
                "SELECT source_excerpt,excerpt_event_id FROM memory_semantic_facts WHERE memory_id=?",
                (memory.memory_id,),
            ).fetchone()
            if current is not None and tuple(current) != (memory.source_excerpt, memory.excerpt_event_id):
                raise CanonicalMemoryFinalizationError("DETERMINISTIC_MEMORY_INTEGRITY_CONFLICT")
            utc = all(e.clock_domain == "backend_utc" for e in events)
            # Evidence observation time is not a semantic claim's validity time.
            # Future maintenance owns valid_from/valid_to and supersedes lineage.
            connection.execute(
                "INSERT INTO memory_semantic_facts(memory_id,source_excerpt,excerpt_event_id,temporal_precision) "
                "VALUES(?,?,?,?) ON CONFLICT(memory_id) DO NOTHING",
                (memory.memory_id, memory.source_excerpt, memory.excerpt_event_id,
                 "OBSERVED_UTC" if utc else "UNKNOWN"),
            )
            if existing is None:
                enqueue_fact(connection, dict(connection.execute(
                    "SELECT * FROM memory_records WHERE memory_id=?", (memory.memory_id,)).fetchone()), now)
        elif memory.memory_type == 'EPISODIC' and existing is None:
            enqueue_reflection(connection, dict(connection.execute(
                'SELECT * FROM memory_records WHERE memory_id=?', (memory.memory_id,)).fetchone()), now)
        # Original record lineage remains the first admission; every later source is linked
        # by evidence back to the immutable ledger and its observer/Episode stream.
        return changed
