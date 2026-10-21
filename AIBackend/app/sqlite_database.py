"""SQLite connection and immutable historical schema bootstrap for Runtime v2."""

from __future__ import annotations

import sqlite3
import time
from contextlib import closing
from pathlib import Path

from .semantic_memory_schema import SEMANTIC_FOUNDATION_SCHEMA_VERSION, migrate_semantic_foundation
from .memory_maintenance_schema import MAINTENANCE_SCHEMA_VERSION, migrate_memory_maintenance
from .memory_reflection_schema import migrate_memory_reflection
from .task_schema import TASK_SCHEMA_VERSION, migrate_tasks

LEGACY_RETIREMENT_SCHEMA_VERSION = 15
LATEST_SCHEMA_VERSION = TASK_SCHEMA_VERSION


class SQLiteDatabase:
    """Provide a connection factory and ordered, versioned schema bootstrap.

    Each connect() creates a new connection owned by its caller; repositories
    own their transactions. Neither the factory nor an asyncio cancellation
    makes one connection safe to share between concurrently running operations.
    """

    def __init__(self, database_path: str | Path) -> None:
        self.database_path = str(database_path)
        if self.database_path != ":memory:":
            Path(self.database_path).parent.mkdir(parents=True, exist_ok=True)
        self.migrate()

    def _connect(self) -> sqlite3.Connection:
        return self.connect()

    def connect(self) -> sqlite3.Connection:
        """Hand one initialized connection to its caller, who owns its close."""
        connection = sqlite3.connect(self.database_path, timeout=5)
        try:
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA foreign_keys = ON")
            connection.execute("PRAGMA busy_timeout = 5000")
            if self.database_path != ":memory:":
                connection.execute("PRAGMA journal_mode = WAL")
        except BaseException:
            # Ownership has not reached the repository's closing() context yet.
            try:
                connection.close()
            except Exception:
                pass  # Preserve the initialization error, never its private text.
            raise
        return connection

    def migrate(self) -> None:
        with closing(self.connect()) as connection:
            connection.execute("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at_utc REAL NOT NULL
                )
            """)
            current = connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0] or 0
            if current > LATEST_SCHEMA_VERSION:
                raise sqlite3.IntegrityError("UNSUPPORTED_FUTURE_MEMORY_SCHEMA")
            if current >= SEMANTIC_FOUNDATION_SCHEMA_VERSION:
                # Schema 16 staged both types. Refuse a store written by the old
                # production path rather than silently hiding incompatible profile memories.
                if connection.execute(
                    "SELECT 1 FROM memory_records WHERE memory_type='PROFILE_FACT' LIMIT 1"
                ).fetchone() or connection.execute("SELECT 1 FROM memory_profile_facts LIMIT 1").fetchone():
                    raise sqlite3.IntegrityError("INCOMPATIBLE_PROFILE_MEMORY_REQUIRES_EXPLICIT_MIGRATION")
                if current == LATEST_SCHEMA_VERSION:
                    return
            # Refuse before any historical migration can alter an incompatible
            # populated database. Migration 16 repeats this under its write lock.
            if current < SEMANTIC_FOUNDATION_SCHEMA_VERSION and connection.execute(
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='memory_records'"
            ).fetchone() and connection.execute("SELECT 1 FROM memory_records LIMIT 1").fetchone():
                raise sqlite3.IntegrityError("SEMANTIC_SCHEMA_REQUIRES_EMPTY_CANONICAL_MEMORY")

        # Keep the original 1–14 SQL available for an existing or fresh database.
        if current < LEGACY_RETIREMENT_SCHEMA_VERSION:
            self._migrate_historical()
            with closing(self.connect()) as connection:
                self._retire_legacy_schema(connection)
        with closing(self.connect()) as connection:
            migrate_semantic_foundation(connection)
            migrate_memory_maintenance(connection)
            migrate_memory_reflection(connection)
            migrate_tasks(connection)

    def _migrate_historical(self) -> None:
        with closing(self._connect()) as connection:
            with connection:
                connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at_utc REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS episodic_memories (
                    memory_id TEXT PRIMARY KEY,
                    candidate_id TEXT NOT NULL UNIQUE,
                    payload_hash TEXT NOT NULL,
                    client_session_id TEXT NOT NULL,
                    observer_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    destination_id TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    epistemic_status TEXT NOT NULL,
                    source_channel TEXT NOT NULL,
                    importance REAL NOT NULL,
                    confidence REAL NOT NULL,
                    occurred_at REAL NOT NULL,
                    observed_at REAL NOT NULL,
                    learned_at_utc REAL NOT NULL,
                    world_revision INTEGER NOT NULL,
                    perception_revision INTEGER NOT NULL,
                    rule_id TEXT NOT NULL,
                    rule_version INTEGER NOT NULL,
                    status TEXT NOT NULL DEFAULT 'active'
                );

                CREATE INDEX IF NOT EXISTS idx_memory_observer_learned
                    ON episodic_memories(observer_id, learned_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_memory_observer_event
                    ON episodic_memories(observer_id, event_type);
                CREATE INDEX IF NOT EXISTS idx_memory_observer_target
                    ON episodic_memories(observer_id, target_id);

                CREATE TABLE IF NOT EXISTS memory_observation_sources (
                    memory_id TEXT NOT NULL REFERENCES episodic_memories(memory_id) ON DELETE CASCADE,
                    source_observation_id TEXT NOT NULL,
                    PRIMARY KEY(memory_id, source_observation_id)
                );

                CREATE TABLE IF NOT EXISTS memory_event_sources (
                    memory_id TEXT NOT NULL REFERENCES episodic_memories(memory_id) ON DELETE CASCADE,
                    source_event_id TEXT NOT NULL,
                    PRIMARY KEY(memory_id, source_event_id)
                );

                CREATE TABLE IF NOT EXISTS memory_tags (
                    memory_id TEXT NOT NULL REFERENCES episodic_memories(memory_id) ON DELETE CASCADE,
                    tag TEXT NOT NULL,
                    PRIMARY KEY(memory_id, tag)
                );

                CREATE TABLE IF NOT EXISTS memory_unknown_fields (
                    memory_id TEXT NOT NULL REFERENCES episodic_memories(memory_id) ON DELETE CASCADE,
                    field_name TEXT NOT NULL,
                    PRIMARY KEY(memory_id, field_name)
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS episodic_memories_fts USING fts5(
                    memory_id UNINDEXED,
                    observer_id UNINDEXED,
                    summary,
                    tags,
                    tokenize = 'unicode61'
                );

                CREATE TABLE IF NOT EXISTS relationship_states (
                    subject_id TEXT NOT NULL,
                    other_entity_id TEXT NOT NULL,
                    trust_milli INTEGER NOT NULL CHECK(trust_milli BETWEEN -1000 AND 1000),
                    fear_milli INTEGER NOT NULL CHECK(fear_milli BETWEEN 0 AND 1000),
                    affinity_milli INTEGER NOT NULL CHECK(affinity_milli BETWEEN -1000 AND 1000),
                    revision INTEGER NOT NULL CHECK(revision >= 1),
                    policy_version TEXT NOT NULL,
                    updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= 0),
                    PRIMARY KEY(subject_id, other_entity_id),
                    CHECK(subject_id <> other_entity_id)
                );

                CREATE TABLE IF NOT EXISTS relationship_events (
                    relationship_event_id TEXT PRIMARY KEY,
                    subject_id TEXT NOT NULL,
                    other_entity_id TEXT NOT NULL,
                    client_session_id TEXT NOT NULL,
                    reason_event_id TEXT NOT NULL,
                    reason_observation_id TEXT NOT NULL,
                    source_memory_id TEXT NOT NULL
                        REFERENCES episodic_memories(memory_id) ON DELETE RESTRICT,
                    source_event_type TEXT NOT NULL,
                    rule_id TEXT NOT NULL,
                    policy_version TEXT NOT NULL,
                    old_trust_milli INTEGER NOT NULL
                        CHECK(old_trust_milli BETWEEN -1000 AND 1000),
                    old_fear_milli INTEGER NOT NULL CHECK(old_fear_milli BETWEEN 0 AND 1000),
                    old_affinity_milli INTEGER NOT NULL
                        CHECK(old_affinity_milli BETWEEN -1000 AND 1000),
                    delta_trust_milli INTEGER NOT NULL
                        CHECK(delta_trust_milli BETWEEN -2000 AND 2000),
                    delta_fear_milli INTEGER NOT NULL
                        CHECK(delta_fear_milli BETWEEN -1000 AND 1000),
                    delta_affinity_milli INTEGER NOT NULL
                        CHECK(delta_affinity_milli BETWEEN -2000 AND 2000),
                    new_trust_milli INTEGER NOT NULL
                        CHECK(new_trust_milli BETWEEN -1000 AND 1000),
                    new_fear_milli INTEGER NOT NULL CHECK(new_fear_milli BETWEEN 0 AND 1000),
                    new_affinity_milli INTEGER NOT NULL
                        CHECK(new_affinity_milli BETWEEN -1000 AND 1000),
                    state_revision INTEGER NOT NULL CHECK(state_revision >= 1),
                    applied_at_utc REAL NOT NULL CHECK(applied_at_utc >= 0),
                    UNIQUE(subject_id, client_session_id, reason_event_id),
                    UNIQUE(subject_id, other_entity_id, state_revision),
                    CHECK(subject_id <> other_entity_id)
                );

                CREATE TABLE IF NOT EXISTS memory_report_provenance (
                    memory_id TEXT PRIMARY KEY
                        REFERENCES episodic_memories(memory_id) ON DELETE CASCADE,
                    reporter_entity_id TEXT NOT NULL,
                    parent_memory_id TEXT NOT NULL
                        REFERENCES episodic_memories(memory_id) ON DELETE RESTRICT,
                    report_conversation_id TEXT NOT NULL,
                    report_hop INTEGER NOT NULL CHECK(report_hop = 1),
                    parent_confidence REAL NOT NULL
                        CHECK(parent_confidence BETWEEN 0.0 AND 1.0),
                    confidence_policy_version TEXT NOT NULL,
                    CHECK(memory_id <> parent_memory_id)
                );

                CREATE TABLE IF NOT EXISTS experience_observer_sequences (
                    observer_id TEXT PRIMARY KEY,
                    last_seq INTEGER NOT NULL CHECK(last_seq >= 0)
                );

                CREATE TABLE IF NOT EXISTS experience_events (
                    event_id TEXT PRIMARY KEY,
                    observer_id TEXT NOT NULL,
                    experience_seq INTEGER NOT NULL CHECK(experience_seq >= 1),
                    dedup_key TEXT NOT NULL UNIQUE,
                    payload_hash TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    source_type TEXT NOT NULL,
                    source_session_id TEXT NOT NULL,
                    source_event_id TEXT,
                    occurred_at REAL NOT NULL,
                    observed_at REAL NOT NULL,
                    ingested_at_utc REAL NOT NULL CHECK(ingested_at_utc >= 0),
                    clock_domain TEXT NOT NULL,
                    world_revision INTEGER,
                    request_id TEXT,
                    trace_id TEXT,
                    conversation_id TEXT,
                    provenance TEXT NOT NULL,
                    confidence REAL CHECK(confidence IS NULL OR (confidence >= 0.0 AND confidence <= 1.0)),
                    payload_json TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    UNIQUE(observer_id, experience_seq),
                    CHECK(world_revision IS NULL OR world_revision >= 0)
                );

                CREATE INDEX IF NOT EXISTS idx_experience_observer_source
                    ON experience_events(observer_id, source_type, source_event_id);
                CREATE INDEX IF NOT EXISTS idx_experience_request
                    ON experience_events(request_id) WHERE request_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_experience_trace
                    ON experience_events(trace_id) WHERE trace_id IS NOT NULL;

                CREATE TRIGGER IF NOT EXISTS experience_events_immutable_update
                BEFORE UPDATE ON experience_events
                BEGIN
                    SELECT RAISE(ABORT, 'EXPERIENCE_EVENT_IMMUTABLE');
                END;

                CREATE TRIGGER IF NOT EXISTS experience_events_immutable_delete
                BEFORE DELETE ON experience_events
                BEGIN
                    SELECT RAISE(ABORT, 'EXPERIENCE_EVENT_IMMUTABLE');
                END;

                CREATE TABLE IF NOT EXISTS episodes (
                    episode_id TEXT PRIMARY KEY,
                    observer_id TEXT NOT NULL,
                    source_session_id TEXT NOT NULL,
                    start_seq INTEGER NOT NULL CHECK(start_seq >= 1),
                    end_seq INTEGER NOT NULL CHECK(end_seq >= start_seq),
                    started_at_utc REAL NOT NULL CHECK(started_at_utc >= 0),
                    ended_at_utc REAL CHECK(ended_at_utc IS NULL OR ended_at_utc >= started_at_utc),
                    status TEXT NOT NULL CHECK(status IN ('OPEN', 'CLOSED')),
                    close_reason TEXT,
                    conversation_id TEXT,
                    primary_goal_id TEXT,
                    primary_goal_type TEXT,
                    event_count INTEGER NOT NULL CHECK(event_count = end_seq - start_seq + 1),
                    policy_version TEXT NOT NULL,
                    schema_version TEXT NOT NULL,
                    created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                    updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= created_at_utc),
                    CHECK(
                        (status = 'OPEN' AND ended_at_utc IS NULL AND close_reason IS NULL)
                        OR
                        (status = 'CLOSED' AND ended_at_utc IS NOT NULL AND close_reason IS NOT NULL)
                    )
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_episode_one_open_per_observer
                    ON episodes(observer_id) WHERE status = 'OPEN';
                CREATE INDEX IF NOT EXISTS idx_episode_observer_range
                    ON episodes(observer_id, start_seq, end_seq);

                CREATE TRIGGER IF NOT EXISTS episodes_no_overlap_insert
                BEFORE INSERT ON episodes
                WHEN EXISTS (
                    SELECT 1 FROM episodes existing
                    WHERE existing.observer_id = NEW.observer_id
                      AND NEW.start_seq <= existing.end_seq
                      AND NEW.end_seq >= existing.start_seq
                )
                BEGIN
                    SELECT RAISE(ABORT, 'EPISODE_RANGE_OVERLAP');
                END;

                CREATE TRIGGER IF NOT EXISTS episodes_no_overlap_update
                BEFORE UPDATE OF observer_id, start_seq, end_seq ON episodes
                WHEN EXISTS (
                    SELECT 1 FROM episodes existing
                    WHERE existing.episode_id <> OLD.episode_id
                      AND existing.observer_id = NEW.observer_id
                      AND NEW.start_seq <= existing.end_seq
                      AND NEW.end_seq >= existing.start_seq
                )
                BEGIN
                    SELECT RAISE(ABORT, 'EPISODE_RANGE_OVERLAP');
                END;

                CREATE TRIGGER IF NOT EXISTS episodes_closed_immutable_update
                BEFORE UPDATE ON episodes WHEN OLD.status = 'CLOSED'
                BEGIN
                    SELECT RAISE(ABORT, 'CLOSED_EPISODE_IMMUTABLE');
                END;

                CREATE TRIGGER IF NOT EXISTS episodes_closed_immutable_delete
                BEFORE DELETE ON episodes WHEN OLD.status = 'CLOSED'
                BEGIN
                    SELECT RAISE(ABORT, 'CLOSED_EPISODE_IMMUTABLE');
                END;

                CREATE TABLE IF NOT EXISTS memory_jobs (
                    job_id TEXT PRIMARY KEY,
                    observer_id TEXT NOT NULL,
                    episode_id TEXT NOT NULL REFERENCES episodes(episode_id) ON DELETE RESTRICT,
                    input_hash TEXT NOT NULL,
                    extractor_version TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL CHECK(status IN ('PENDING', 'RUNNING', 'RETRY', 'SUCCEEDED', 'DEAD_LETTER')),
                    retry_count INTEGER NOT NULL DEFAULT 0 CHECK(retry_count >= 0),
                    attempt_generation INTEGER NOT NULL DEFAULT 0 CHECK(attempt_generation >= 0),
                    next_retry_at_utc REAL,
                    created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                    updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= created_at_utc),
                    started_at_utc REAL,
                    finished_at_utc REAL,
                    error_code TEXT,
                    schema_version TEXT NOT NULL,
                    UNIQUE(episode_id, extractor_version)
                );

                CREATE INDEX IF NOT EXISTS idx_memory_jobs_status_retry
                    ON memory_jobs(status, next_retry_at_utc, created_at_utc);
                CREATE INDEX IF NOT EXISTS idx_memory_jobs_observer_status
                    ON memory_jobs(observer_id, status);

                CREATE TABLE IF NOT EXISTS memory_consolidation_state (
                    observer_id TEXT PRIMARY KEY,
                    consolidated_through_experience_seq INTEGER NOT NULL DEFAULT 0
                        CHECK(consolidated_through_experience_seq >= 0),
                    updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= 0),
                    schema_version TEXT NOT NULL
                );
                """
            )
                columns = connection.execute("PRAGMA table_info(memory_consolidation_state)").fetchall()
                primary_keys = [row["name"] for row in columns if row["pk"]]
                if primary_keys == ["observer_id"]:
                    connection.executescript(
                        """
                        ALTER TABLE memory_consolidation_state RENAME TO memory_consolidation_state_v6;
                        CREATE TABLE memory_consolidation_state (
                            observer_id TEXT NOT NULL,
                            extractor_version TEXT NOT NULL,
                            consolidated_through_experience_seq INTEGER NOT NULL DEFAULT 0
                                CHECK(consolidated_through_experience_seq >= 0),
                            updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= 0),
                            schema_version TEXT NOT NULL,
                            PRIMARY KEY(observer_id, extractor_version)
                        );
                        INSERT INTO memory_consolidation_state(
                            observer_id, extractor_version, consolidated_through_experience_seq,
                            updated_at_utc, schema_version
                        ) SELECT observer_id, 'memory-extractor/1.0',
                            consolidated_through_experience_seq, updated_at_utc,
                            'consolidation-state/2.0' FROM memory_consolidation_state_v6;
                        DROP TABLE memory_consolidation_state_v6;
                        """
                    )
                connection.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS memory_v1_records (
                        memory_id TEXT PRIMARY KEY,
                        observer_id TEXT NOT NULL,
                        memory_type TEXT NOT NULL CHECK(memory_type = 'EPISODIC'),
                        scope TEXT NOT NULL CHECK(scope = 'NPC_PRIVATE'),
                        summary TEXT NOT NULL,
                        subject_ids_json TEXT NOT NULL,
                        importance REAL NOT NULL CHECK(importance BETWEEN 0.0 AND 1.0),
                        confidence REAL NOT NULL CHECK(confidence BETWEEN 0.0 AND 1.0),
                        provenance_label TEXT NOT NULL,
                        provenance_set_json TEXT NOT NULL,
                        occurred_start REAL NOT NULL,
                        occurred_end REAL NOT NULL CHECK(occurred_end >= occurred_start),
                        episode_id TEXT NOT NULL REFERENCES episodes(episode_id) ON DELETE RESTRICT,
                        job_id TEXT NOT NULL REFERENCES memory_jobs(job_id) ON DELETE RESTRICT,
                        extractor_version TEXT NOT NULL,
                        admission_policy_version TEXT NOT NULL,
                        canonical_candidate_hash TEXT NOT NULL UNIQUE,
                        status TEXT NOT NULL CHECK(status = 'ACTIVE'),
                        learned_at_utc REAL NOT NULL CHECK(learned_at_utc >= 0),
                        schema_version TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_observer_learned
                        ON memory_v1_records(observer_id, learned_at_utc DESC);
                    CREATE TABLE IF NOT EXISTS memory_v1_evidence (
                        memory_id TEXT NOT NULL REFERENCES memory_v1_records(memory_id) ON DELETE CASCADE,
                        observer_id TEXT NOT NULL,
                        experience_event_id TEXT NOT NULL REFERENCES experience_events(event_id) ON DELETE RESTRICT,
                        experience_seq INTEGER NOT NULL CHECK(experience_seq >= 1),
                        PRIMARY KEY(memory_id, experience_event_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_evidence_observer_seq
                        ON memory_v1_evidence(observer_id, experience_seq);
                    CREATE TABLE IF NOT EXISTS memory_job_results (
                        job_id TEXT PRIMARY KEY REFERENCES memory_jobs(job_id) ON DELETE RESTRICT,
                        observer_id TEXT NOT NULL,
                        episode_id TEXT NOT NULL REFERENCES episodes(episode_id) ON DELETE RESTRICT,
                        extractor_version TEXT NOT NULL,
                        prompt_version TEXT NOT NULL,
                        response_schema_version TEXT NOT NULL,
                        provider_name TEXT NOT NULL,
                        model_name TEXT NOT NULL,
                        output_hash TEXT NOT NULL,
                        proposal_count INTEGER NOT NULL CHECK(proposal_count >= 0),
                        accepted_count INTEGER NOT NULL CHECK(accepted_count >= 0),
                        rejected_count INTEGER NOT NULL CHECK(rejected_count >= 0),
                        deduped_count INTEGER NOT NULL CHECK(deduped_count >= 0),
                        admission_policy_version TEXT NOT NULL,
                        finished_at_utc REAL NOT NULL CHECK(finished_at_utc >= 0),
                        schema_version TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS profile_consolidation_jobs (
                        job_id TEXT PRIMARY KEY,
                        observer_id TEXT NOT NULL,
                        start_seq INTEGER NOT NULL CHECK(start_seq >= 1),
                        end_seq INTEGER NOT NULL CHECK(end_seq >= start_seq),
                        episode_ids_json TEXT NOT NULL,
                        input_hash TEXT NOT NULL,
                        profile_extractor_version TEXT NOT NULL,
                        profile_policy_version TEXT NOT NULL,
                        pipeline_fingerprint TEXT NOT NULL,
                        idempotency_key TEXT NOT NULL UNIQUE,
                        status TEXT NOT NULL CHECK(status IN ('PENDING','RUNNING','RETRY','SUCCEEDED','DEAD_LETTER')),
                        retry_count INTEGER NOT NULL DEFAULT 0 CHECK(retry_count >= 0),
                        attempt_generation INTEGER NOT NULL DEFAULT 0 CHECK(attempt_generation >= 0),
                        next_retry_at_utc REAL,
                        created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                        updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= created_at_utc),
                        started_at_utc REAL,
                        finished_at_utc REAL,
                        error_code TEXT,
                        schema_version TEXT NOT NULL,
                        UNIQUE(observer_id, start_seq, end_seq, pipeline_fingerprint)
                    );
                    CREATE INDEX IF NOT EXISTS idx_profile_jobs_status_retry
                        ON profile_consolidation_jobs(status, next_retry_at_utc, created_at_utc);
                    CREATE INDEX IF NOT EXISTS idx_profile_jobs_observer_status
                        ON profile_consolidation_jobs(observer_id, pipeline_fingerprint, status);

                    CREATE TABLE IF NOT EXISTS profile_consolidation_state (
                        observer_id TEXT NOT NULL,
                        pipeline_fingerprint TEXT NOT NULL,
                        consolidated_through_experience_seq INTEGER NOT NULL DEFAULT 0
                            CHECK(consolidated_through_experience_seq >= 0),
                        updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= 0),
                        schema_version TEXT NOT NULL,
                        PRIMARY KEY(observer_id, pipeline_fingerprint)
                    );

                    CREATE TABLE IF NOT EXISTS memory_v1_facts (
                        memory_id TEXT PRIMARY KEY,
                        fact_key TEXT NOT NULL,
                        observer_id TEXT NOT NULL,
                        subject_id TEXT NOT NULL,
                        memory_type TEXT NOT NULL CHECK(memory_type = 'PROFILE_FACT'),
                        scope TEXT NOT NULL CHECK(scope = 'NPC_PRIVATE'),
                        attribute_key TEXT NOT NULL,
                        attribute_value TEXT NOT NULL,
                        normalized_value TEXT NOT NULL,
                        cardinality TEXT NOT NULL CHECK(cardinality IN ('SINGLE_VALUE','MULTI_VALUE')),
                        summary TEXT NOT NULL,
                        confidence REAL NOT NULL CHECK(confidence BETWEEN 0.0 AND 1.0),
                        provenance_label TEXT NOT NULL,
                        provenance_set_json TEXT NOT NULL,
                        provenance_rank INTEGER NOT NULL CHECK(provenance_rank BETWEEN 0 AND 5),
                        occurred_start REAL NOT NULL,
                        occurred_end REAL NOT NULL CHECK(occurred_end >= occurred_start),
                        learned_at_utc REAL NOT NULL CHECK(learned_at_utc >= 0),
                        valid_from_utc REAL,
                        valid_to_utc REAL,
                        temporal_precision TEXT NOT NULL CHECK(temporal_precision IN ('EXACT','UNKNOWN')),
                        status TEXT NOT NULL CHECK(status IN ('ACTIVE','SUPERSEDED','CONFLICTED')),
                        conflict_group_id TEXT NOT NULL,
                        supersedes_memory_id TEXT REFERENCES memory_v1_facts(memory_id) ON DELETE RESTRICT,
                        superseded_at_utc REAL,
                        profile_extractor_version TEXT NOT NULL,
                        profile_policy_version TEXT NOT NULL,
                        canonical_candidate_hash TEXT NOT NULL UNIQUE,
                        schema_version TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_facts_subject_attribute_status
                        ON memory_v1_facts(observer_id, subject_id, attribute_key, status);
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_facts_key_status
                        ON memory_v1_facts(observer_id, fact_key, status);
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_facts_temporal
                        ON memory_v1_facts(observer_id, fact_key, valid_from_utc, valid_to_utc);
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_facts_conflict
                        ON memory_v1_facts(observer_id, conflict_group_id);

                    CREATE TABLE IF NOT EXISTS memory_v1_fact_evidence (
                        memory_id TEXT NOT NULL REFERENCES memory_v1_facts(memory_id) ON DELETE CASCADE,
                        observer_id TEXT NOT NULL,
                        experience_event_id TEXT NOT NULL REFERENCES experience_events(event_id) ON DELETE RESTRICT,
                        experience_seq INTEGER NOT NULL CHECK(experience_seq >= 1),
                        provenance_class TEXT NOT NULL,
                        provenance_rank INTEGER NOT NULL CHECK(provenance_rank BETWEEN 0 AND 5),
                        PRIMARY KEY(memory_id, experience_event_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_memory_v1_fact_evidence_observer_seq
                        ON memory_v1_fact_evidence(observer_id, experience_seq);

                    CREATE TABLE IF NOT EXISTS profile_job_results (
                        job_id TEXT PRIMARY KEY REFERENCES profile_consolidation_jobs(job_id) ON DELETE RESTRICT,
                        observer_id TEXT NOT NULL,
                        profile_extractor_version TEXT NOT NULL,
                        profile_policy_version TEXT NOT NULL,
                        prompt_version TEXT NOT NULL,
                        response_schema_version TEXT NOT NULL,
                        provider_name TEXT NOT NULL,
                        model_name TEXT NOT NULL,
                        output_hash TEXT NOT NULL,
                        proposal_count INTEGER NOT NULL CHECK(proposal_count >= 0),
                        accepted_count INTEGER NOT NULL CHECK(accepted_count >= 0),
                        rejected_count INTEGER NOT NULL CHECK(rejected_count >= 0),
                        deduped_count INTEGER NOT NULL CHECK(deduped_count >= 0),
                        finished_at_utc REAL NOT NULL CHECK(finished_at_utc >= 0),
                        schema_version TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS relationship_shadow_jobs (
                        job_id TEXT PRIMARY KEY,
                        observer_id TEXT NOT NULL,
                        start_seq INTEGER NOT NULL CHECK(start_seq >= 1),
                        end_seq INTEGER NOT NULL CHECK(end_seq >= start_seq),
                        episode_ids_json TEXT NOT NULL,
                        input_hash TEXT NOT NULL,
                        relationship_extractor_version TEXT NOT NULL,
                        relationship_policy_version TEXT NOT NULL,
                        pipeline_fingerprint TEXT NOT NULL,
                        idempotency_key TEXT NOT NULL UNIQUE,
                        status TEXT NOT NULL CHECK(status IN ('PENDING','RUNNING','RETRY','SUCCEEDED','DEAD_LETTER')),
                        retry_count INTEGER NOT NULL DEFAULT 0 CHECK(retry_count >= 0),
                        attempt_generation INTEGER NOT NULL DEFAULT 0 CHECK(attempt_generation >= 0),
                        next_retry_at_utc REAL,
                        created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                        updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= created_at_utc),
                        started_at_utc REAL,
                        finished_at_utc REAL,
                        error_code TEXT,
                        schema_version TEXT NOT NULL,
                        UNIQUE(observer_id, start_seq, end_seq, pipeline_fingerprint)
                    );
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_jobs_status_retry
                        ON relationship_shadow_jobs(status, next_retry_at_utc, created_at_utc);
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_jobs_observer_status
                        ON relationship_shadow_jobs(observer_id, pipeline_fingerprint, status);

                    CREATE TABLE IF NOT EXISTS relationship_shadow_state (
                        observer_id TEXT NOT NULL,
                        pipeline_fingerprint TEXT NOT NULL,
                        processed_through_experience_seq INTEGER NOT NULL DEFAULT 0
                            CHECK(processed_through_experience_seq >= 0),
                        updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= 0),
                        schema_version TEXT NOT NULL,
                        PRIMARY KEY(observer_id, pipeline_fingerprint)
                    );

                    CREATE TABLE IF NOT EXISTS memory_v1_relationship_evidence_shadow (
                        relationship_evidence_id TEXT PRIMARY KEY,
                        observer_id TEXT NOT NULL,
                        counterparty_id TEXT NOT NULL,
                        semantic_category TEXT NOT NULL,
                        polarity TEXT NOT NULL CHECK(polarity IN ('POSITIVE','NEGATIVE','NEUTRAL')),
                        confidence REAL NOT NULL CHECK(confidence BETWEEN 0.0 AND 1.0),
                        summary TEXT NOT NULL,
                        provenance_label TEXT NOT NULL,
                        provenance_set_json TEXT NOT NULL,
                        occurred_start REAL NOT NULL,
                        occurred_end REAL NOT NULL CHECK(occurred_end >= occurred_start),
                        status TEXT NOT NULL CHECK(status = 'SHADOW'),
                        relationship_extractor_version TEXT NOT NULL,
                        relationship_policy_version TEXT NOT NULL,
                        canonical_candidate_hash TEXT NOT NULL UNIQUE,
                        created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                        schema_version TEXT NOT NULL,
                        CHECK(observer_id <> counterparty_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_pair_category
                        ON memory_v1_relationship_evidence_shadow(
                            observer_id, counterparty_id, semantic_category, polarity
                        );
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_observer_created
                        ON memory_v1_relationship_evidence_shadow(observer_id, created_at_utc);

                    CREATE TABLE IF NOT EXISTS memory_v1_relationship_evidence_links (
                        relationship_evidence_id TEXT NOT NULL
                            REFERENCES memory_v1_relationship_evidence_shadow(relationship_evidence_id)
                            ON DELETE CASCADE,
                        observer_id TEXT NOT NULL,
                        experience_event_id TEXT NOT NULL
                            REFERENCES experience_events(event_id) ON DELETE RESTRICT,
                        experience_seq INTEGER NOT NULL CHECK(experience_seq >= 1),
                        PRIMARY KEY(relationship_evidence_id, experience_event_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_links_observer_seq
                        ON memory_v1_relationship_evidence_links(observer_id, experience_seq);
                    CREATE INDEX IF NOT EXISTS idx_relationship_shadow_links_event
                        ON memory_v1_relationship_evidence_links(experience_event_id);

                    CREATE TABLE IF NOT EXISTS relationship_shadow_job_results (
                        job_id TEXT PRIMARY KEY REFERENCES relationship_shadow_jobs(job_id) ON DELETE RESTRICT,
                        observer_id TEXT NOT NULL,
                        relationship_extractor_version TEXT NOT NULL,
                        relationship_policy_version TEXT NOT NULL,
                        prompt_version TEXT NOT NULL,
                        response_schema_version TEXT NOT NULL,
                        provider_name TEXT NOT NULL,
                        model_name TEXT NOT NULL,
                        output_hash TEXT NOT NULL,
                        proposal_count INTEGER NOT NULL CHECK(proposal_count >= 0),
                        accepted_count INTEGER NOT NULL CHECK(accepted_count >= 0),
                        rejected_count INTEGER NOT NULL CHECK(rejected_count >= 0),
                        deduped_count INTEGER NOT NULL CHECK(deduped_count >= 0),
                        finished_at_utc REAL NOT NULL CHECK(finished_at_utc >= 0),
                        schema_version TEXT NOT NULL
                    );
                    """
                )
                memory_v1_columns = {
                    row["name"] for row in connection.execute("PRAGMA table_info(memory_v1_records)")
                }
                if "subject_ids_json" not in memory_v1_columns:
                    connection.execute(
                        "ALTER TABLE memory_v1_records ADD COLUMN subject_ids_json TEXT NOT NULL DEFAULT '[]'"
                    )
                fact_evidence_columns = {
                    row["name"] for row in connection.execute("PRAGMA table_info(memory_v1_fact_evidence)")
                }
                if "provenance_class" not in fact_evidence_columns:
                    connection.execute(
                        "ALTER TABLE memory_v1_fact_evidence ADD COLUMN provenance_class "
                        "TEXT NOT NULL DEFAULT 'UNKNOWN'"
                    )
                if "provenance_rank" not in fact_evidence_columns:
                    connection.execute(
                        "ALTER TABLE memory_v1_fact_evidence ADD COLUMN provenance_rank "
                        "INTEGER NOT NULL DEFAULT 0 CHECK(provenance_rank BETWEEN 0 AND 5)"
                    )
                migration_10 = connection.execute(
                    "SELECT 1 FROM schema_migrations WHERE version = 10"
                ).fetchone()
                if migration_10 is None:
                    connection.executescript(
                        """
                        CREATE VIRTUAL TABLE memory_v1_records_fts USING fts5(
                            memory_id UNINDEXED,
                            observer_id UNINDEXED,
                            summary,
                            subject_ids,
                            tokenize = 'unicode61'
                        );
                        INSERT INTO memory_v1_records_fts(memory_id, observer_id, summary, subject_ids)
                        SELECT memory_id, observer_id, summary, subject_ids_json FROM memory_v1_records;
                        CREATE TRIGGER memory_v1_records_fts_insert
                        AFTER INSERT ON memory_v1_records
                        BEGIN
                            INSERT INTO memory_v1_records_fts(memory_id, observer_id, summary, subject_ids)
                            VALUES(NEW.memory_id, NEW.observer_id, NEW.summary, NEW.subject_ids_json);
                        END;
                        CREATE TRIGGER memory_v1_records_fts_update
                        AFTER UPDATE ON memory_v1_records
                        BEGIN
                            DELETE FROM memory_v1_records_fts WHERE memory_id = OLD.memory_id;
                            INSERT INTO memory_v1_records_fts(memory_id, observer_id, summary, subject_ids)
                            VALUES(NEW.memory_id, NEW.observer_id, NEW.summary, NEW.subject_ids_json);
                        END;
                        CREATE TRIGGER memory_v1_records_fts_delete
                        AFTER DELETE ON memory_v1_records
                        BEGIN
                            DELETE FROM memory_v1_records_fts WHERE memory_id = OLD.memory_id;
                        END;
                        """
                    )
                    connection.execute(
                        "INSERT INTO schema_migrations(version, applied_at_utc) VALUES(10, ?)",
                        (time.time(),),
                    )
                migration_11 = connection.execute(
                    "SELECT 1 FROM schema_migrations WHERE version = 11"
                ).fetchone()
                if migration_11 is None:
                    connection.executescript(
                        """
                        CREATE TABLE memory_v1_embeddings (
                            embedding_id TEXT PRIMARY KEY,
                            observer_id TEXT NOT NULL,
                            memory_id TEXT NOT NULL,
                            memory_type TEXT NOT NULL CHECK(memory_type IN ('EPISODIC','PROFILE_FACT')),
                            embedding_model TEXT NOT NULL,
                            embedding_model_version TEXT NOT NULL,
                            embedding_pipeline_version TEXT NOT NULL,
                            dimension INTEGER NOT NULL CHECK(dimension > 0),
                            vector_blob BLOB,
                            content_hash TEXT NOT NULL,
                            status TEXT NOT NULL CHECK(status IN ('PENDING','READY','FAILED','STALE')),
                            error_code TEXT,
                            created_at_utc REAL NOT NULL CHECK(created_at_utc >= 0),
                            updated_at_utc REAL NOT NULL CHECK(updated_at_utc >= created_at_utc),
                            UNIQUE(memory_id,embedding_model,embedding_model_version,
                                   embedding_pipeline_version),
                            CHECK((status='READY' AND vector_blob IS NOT NULL AND error_code IS NULL)
                               OR (status<>'READY' AND vector_blob IS NULL))
                        );
                        CREATE INDEX idx_memory_v1_embeddings_lookup
                            ON memory_v1_embeddings(
                                observer_id,embedding_model,embedding_model_version,
                                embedding_pipeline_version,status,memory_type,memory_id
                            );
                        CREATE INDEX idx_memory_v1_embeddings_backfill
                            ON memory_v1_embeddings(status,updated_at_utc,memory_id);
                        """
                    )
                    connection.execute(
                        "INSERT INTO schema_migrations(version, applied_at_utc) VALUES(11, ?)",
                        (time.time(),),
                    )
                connection.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS memory_records (
                        memory_id TEXT PRIMARY KEY,
                        observer_entity_id TEXT NOT NULL,
                        identity_namespace TEXT,
                        identity_type TEXT,
                        persistent_id TEXT,
                        memory_type TEXT NOT NULL CHECK(memory_type IN ('EPISODIC','PROFILE_FACT')),
                        scope TEXT NOT NULL CHECK(scope = 'NPC_PRIVATE'),
                        summary TEXT NOT NULL CHECK(length(summary) BETWEEN 1 AND 500),
                        importance REAL CHECK(importance BETWEEN 0 AND 1),
                        confidence REAL NOT NULL CHECK(confidence BETWEEN 0 AND 1),
                        provenance_label TEXT NOT NULL,
                        provenance_set_json TEXT NOT NULL,
                        occurred_start REAL NOT NULL CHECK(occurred_start >= 0),
                        occurred_end REAL NOT NULL CHECK(occurred_end >= occurred_start),
                        learned_at REAL NOT NULL CHECK(learned_at >= 0),
                        episode_id TEXT NOT NULL REFERENCES episodes(episode_id) ON DELETE RESTRICT,
                        job_id TEXT NOT NULL REFERENCES memory_jobs(job_id) ON DELETE RESTRICT,
                        pipeline_version TEXT NOT NULL,
                        admission_policy_version TEXT NOT NULL,
                        status TEXT NOT NULL CHECK(status IN ('ACTIVE','SUPERSEDED','CONFLICTED')),
                        schema_version TEXT NOT NULL,
                        CHECK((identity_namespace IS NULL AND identity_type IS NULL AND persistent_id IS NULL)
                           OR (identity_namespace IS NOT NULL AND identity_type IS NOT NULL AND persistent_id IS NOT NULL)),
                        CHECK(memory_type <> 'PROFILE_FACT' OR persistent_id IS NOT NULL)
                    );
                    CREATE TABLE IF NOT EXISTS memory_subjects (
                        memory_id TEXT NOT NULL REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                        identity_namespace TEXT NOT NULL,
                        identity_type TEXT NOT NULL,
                        persistent_id TEXT NOT NULL,
                        PRIMARY KEY(memory_id, identity_namespace, identity_type, persistent_id)
                    );
                    CREATE TABLE IF NOT EXISTS memory_evidence (
                        memory_id TEXT NOT NULL REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                        experience_event_id TEXT NOT NULL REFERENCES experience_events(event_id) ON DELETE RESTRICT,
                        experience_seq INTEGER NOT NULL CHECK(experience_seq >= 1),
                        PRIMARY KEY(memory_id, experience_event_id)
                    );
                    CREATE TABLE IF NOT EXISTS memory_profile_facts (
                        memory_id TEXT PRIMARY KEY REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                        fact_key TEXT NOT NULL,
                        attribute_key TEXT NOT NULL,
                        attribute_value TEXT NOT NULL,
                        normalized_value TEXT NOT NULL,
                        cardinality TEXT NOT NULL CHECK(cardinality IN ('SINGLE_VALUE','MULTI_VALUE')),
                        valid_from REAL,
                        valid_to REAL,
                        temporal_precision TEXT NOT NULL CHECK(temporal_precision IN ('OBSERVED_UTC','UNKNOWN')),
                        supersedes_memory_id TEXT REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                        UNIQUE(fact_key, normalized_value)
                    );
                    CREATE INDEX IF NOT EXISTS idx_memory_owner
                        ON memory_records(identity_namespace, identity_type, persistent_id, memory_type, status);
                    CREATE INDEX IF NOT EXISTS idx_memory_subject_identity
                        ON memory_subjects(identity_namespace, identity_type, persistent_id, memory_id);
                    """
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(12, ?)",
                    (time.time(),),
                )
                connection.execute("""
                    CREATE TABLE IF NOT EXISTS memory_embeddings (
                        memory_id TEXT NOT NULL REFERENCES memory_records(memory_id) ON DELETE CASCADE,
                        model_key TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        dimension INTEGER NOT NULL CHECK(dimension > 0),
                        vector_blob BLOB NOT NULL,
                        indexed_at_utc REAL NOT NULL,
                        PRIMARY KEY(memory_id, model_key)
                    )
                """)
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(13, ?)",
                    (time.time(),),
                )
                connection.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS relationship_reflections (
                        reflection_id TEXT PRIMARY KEY,
                        observer_identity_namespace TEXT NOT NULL,
                        observer_identity_type TEXT NOT NULL,
                        observer_persistent_id TEXT NOT NULL,
                        subject_identity_namespace TEXT NOT NULL,
                        subject_identity_type TEXT NOT NULL,
                        subject_persistent_id TEXT NOT NULL,
                        episode_id TEXT NOT NULL REFERENCES episodes(episode_id) ON DELETE RESTRICT,
                        episode_input_hash TEXT NOT NULL,
                        outcome TEXT NOT NULL CHECK(outcome IN ('INTERPRETATION','NO_CHANGE')),
                        interpretation TEXT,
                        evidence_event_ids_json TEXT NOT NULL CHECK(json_valid(evidence_event_ids_json)),
                        policy_version TEXT NOT NULL,
                        generated_at REAL NOT NULL CHECK(generated_at >= 0),
                        CHECK((outcome = 'INTERPRETATION' AND interpretation IS NOT NULL
                               AND length(trim(interpretation)) BETWEEN 1 AND 500)
                           OR (outcome = 'NO_CHANGE' AND interpretation IS NULL)),
                        UNIQUE(
                            observer_identity_namespace, observer_identity_type, observer_persistent_id,
                            subject_identity_namespace, subject_identity_type, subject_persistent_id,
                            episode_id, policy_version
                        )
                    );
                    CREATE INDEX IF NOT EXISTS idx_relationship_reflections_pair_recent
                        ON relationship_reflections(
                            observer_identity_namespace, observer_identity_type, observer_persistent_id,
                            subject_identity_namespace, subject_identity_type, subject_persistent_id,
                            outcome, generated_at DESC, reflection_id DESC
                        );
                    """
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(14, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(1, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(2, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(3, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(4, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(5, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(6, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(7, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(8, ?)",
                    (time.time(),),
                )
                connection.execute(
                    "INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc) VALUES(9, ?)",
                    (time.time(),),
                )

    @staticmethod
    def _retire_legacy_schema(connection: sqlite3.Connection) -> None:
        """Apply the version 15 retirement as one SQLite transaction."""
        legacy_triggers = (
            "memory_v1_records_fts_delete",
            "memory_v1_records_fts_insert",
            "memory_v1_records_fts_update",
        )
        # Children precede parents; FTS5 virtual tables precede their source tables.
        legacy_tables = (
            "episodic_memories_fts",
            "memory_v1_records_fts",
            "relationship_events",
            "memory_report_provenance",
            "memory_observation_sources",
            "memory_event_sources",
            "memory_tags",
            "memory_unknown_fields",
            "episodic_memories",
            "memory_v1_evidence",
            "memory_v1_fact_evidence",
            "memory_v1_relationship_evidence_links",
            "relationship_shadow_job_results",
            "profile_job_results",
            "memory_job_results",
            "memory_v1_embeddings",
            "memory_v1_records",
            "memory_v1_facts",
            "memory_v1_relationship_evidence_shadow",
            "relationship_shadow_state",
            "relationship_shadow_jobs",
            "profile_consolidation_state",
            "profile_consolidation_jobs",
            "relationship_states",
        )
        connection.execute("BEGIN IMMEDIATE")
        try:
            if connection.execute(
                "SELECT 1 FROM schema_migrations WHERE version = ?",
                (LEGACY_RETIREMENT_SCHEMA_VERSION,),
            ).fetchone():
                connection.commit()
                return
            for trigger in legacy_triggers:
                connection.execute(f"DROP TRIGGER IF EXISTS {trigger}")
            for table in legacy_tables:
                if table == "memory_v1_facts":
                    # SQLite's implicit DELETE for DROP enforces this self-RESTRICT FK.
                    connection.execute(
                        "UPDATE memory_v1_facts SET supersedes_memory_id=NULL "
                        "WHERE supersedes_memory_id IS NOT NULL"
                    )
                try:
                    connection.execute(f"DROP TABLE IF EXISTS {table}")
                except sqlite3.IntegrityError as error:
                    raise sqlite3.IntegrityError(f"DROP {table}: {error}") from error
            connection.execute(
                "INSERT INTO schema_migrations(version, applied_at_utc) VALUES(?, ?)",
                (LEGACY_RETIREMENT_SCHEMA_VERSION, time.time()),
            )
            connection.commit()
        except BaseException:
            connection.rollback()
            raise
