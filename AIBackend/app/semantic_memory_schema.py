"""Migration 16: dormant Semantic Fact storage, without switching production.

No historical fact conversion is performed. Nonempty canonical stores require
an explicit cleanup or a future migration, never an implicit destructive reset.
The historical PROFILE_FACT table/type are retained for schema compatibility;
M3-A2 production refuses populated Profile stores and writes Semantic Facts only.
"""

import sqlite3
import time


SEMANTIC_FOUNDATION_SCHEMA_VERSION = 16


def migrate_semantic_foundation(connection: sqlite3.Connection) -> None:
    connection.execute("BEGIN IMMEDIATE")
    try:
        if connection.execute(
            "SELECT 1 FROM schema_migrations WHERE version=?",
            (SEMANTIC_FOUNDATION_SCHEMA_VERSION,),
        ).fetchone():
            connection.commit()
            return
        # Version 15's type CHECK requires rebuilding the parent table. Reject
        # populated stores rather than converting or dropping their records.
        for table in ("memory_records", "memory_subjects", "memory_evidence",
                      "memory_profile_facts", "memory_embeddings"):
            if connection.execute(f"SELECT 1 FROM {table} LIMIT 1").fetchone():
                raise sqlite3.IntegrityError("SEMANTIC_SCHEMA_REQUIRES_EMPTY_CANONICAL_MEMORY")
        connection.execute("DROP TABLE memory_records")
        connection.execute("""
            CREATE TABLE memory_records (
                memory_id TEXT PRIMARY KEY,
                observer_entity_id TEXT NOT NULL,
                identity_namespace TEXT,
                identity_type TEXT,
                persistent_id TEXT,
                memory_type TEXT NOT NULL CHECK(memory_type IN ('EPISODIC','PROFILE_FACT','SEMANTIC_FACT')),
                scope TEXT NOT NULL CHECK(scope = 'NPC_PRIVATE'),
                summary TEXT NOT NULL CHECK(length(trim(summary)) BETWEEN 1 AND 500),
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
                revision INTEGER NOT NULL DEFAULT 1 CHECK(typeof(revision)='integer' AND revision >= 1),
                CHECK((identity_namespace IS NULL AND identity_type IS NULL AND persistent_id IS NULL)
                   OR (identity_namespace IS NOT NULL AND identity_type IS NOT NULL AND persistent_id IS NOT NULL)),
                CHECK(memory_type NOT IN ('PROFILE_FACT','SEMANTIC_FACT') OR
                      (length(trim(identity_namespace)) > 0 AND length(trim(identity_type)) > 0
                       AND length(trim(persistent_id)) > 0 AND persistent_id IS NOT NULL)),
                CHECK(memory_type <> 'SEMANTIC_FACT' OR schema_version='memory-record/3.0')
            )
        """)
        connection.execute("""
            CREATE INDEX idx_memory_owner
            ON memory_records(identity_namespace, identity_type, persistent_id, memory_type, status)
        """)
        # Natural-language fact text lives once, in memory_records.summary.
        # Revision and predecessor support future optimistic, reversible edits;
        # maintenance operations and their audit history belong to M3-B.
        connection.execute("""
            CREATE TABLE memory_semantic_facts (
                memory_id TEXT PRIMARY KEY REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                source_excerpt TEXT NOT NULL CHECK(length(trim(source_excerpt)) BETWEEN 1 AND 1000),
                excerpt_event_id TEXT NOT NULL,
                temporal_precision TEXT NOT NULL DEFAULT 'UNKNOWN'
                    CHECK(temporal_precision IN ('OBSERVED_UTC','UNKNOWN')),
                valid_from REAL CHECK(valid_from IS NULL OR valid_from >= 0),
                valid_to REAL CHECK(valid_to IS NULL OR valid_to >= 0),
                supersedes_memory_id TEXT REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                schema_version TEXT NOT NULL DEFAULT 'semantic-fact/1.0'
                    CHECK(schema_version='semantic-fact/1.0'),
                FOREIGN KEY(memory_id,excerpt_event_id)
                    REFERENCES memory_evidence(memory_id,experience_event_id) ON DELETE RESTRICT,
                CHECK(supersedes_memory_id IS NULL OR supersedes_memory_id <> memory_id),
                CHECK(valid_from IS NULL OR valid_to IS NULL OR valid_to >= valid_from),
                CHECK(temporal_precision <> 'UNKNOWN' OR (valid_from IS NULL AND valid_to IS NULL))
            )
        """)
        connection.execute("""
            CREATE TRIGGER memory_semantic_facts_type_insert
            BEFORE INSERT ON memory_semantic_facts
            WHEN NOT EXISTS (SELECT 1 FROM memory_records
                WHERE memory_id=NEW.memory_id AND memory_type='SEMANTIC_FACT')
            BEGIN SELECT RAISE(ABORT, 'SEMANTIC_FACT_RECORD_REQUIRED'); END
        """)
        connection.execute("""
            CREATE TRIGGER memory_semantic_facts_type_update
            BEFORE UPDATE OF memory_id ON memory_semantic_facts
            WHEN NOT EXISTS (SELECT 1 FROM memory_records
                WHERE memory_id=NEW.memory_id AND memory_type='SEMANTIC_FACT')
            BEGIN SELECT RAISE(ABORT, 'SEMANTIC_FACT_RECORD_REQUIRED'); END
        """)
        connection.execute("""
            CREATE TRIGGER memory_records_semantic_type_update
            BEFORE UPDATE OF memory_type ON memory_records
            WHEN NEW.memory_type <> 'SEMANTIC_FACT' AND EXISTS (
                SELECT 1 FROM memory_semantic_facts WHERE memory_id=OLD.memory_id)
            BEGIN SELECT RAISE(ABORT, 'SEMANTIC_FACT_RECORD_REQUIRED'); END
        """)
        if connection.execute("PRAGMA foreign_key_check").fetchone() is not None:
            raise sqlite3.IntegrityError("SEMANTIC_SCHEMA_FOREIGN_KEY_CHECK_FAILED")
        connection.execute(
            "INSERT INTO schema_migrations(version,applied_at_utc) VALUES(?,?)",
            (SEMANTIC_FOUNDATION_SCHEMA_VERSION, time.time()),
        )
        connection.commit()
    except BaseException:
        connection.rollback()
        raise
