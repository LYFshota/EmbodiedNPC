"""Additive Schema 17; populated Schema 16 needs no conversion or reset."""
import sqlite3
import time

from .memory_maintenance_repository import enqueue_fact

MAINTENANCE_SCHEMA_VERSION = 17


def migrate_memory_maintenance(db: sqlite3.Connection):
    db.execute("BEGIN IMMEDIATE")
    try:
        if db.execute("SELECT 1 FROM schema_migrations WHERE version=17").fetchone():
            db.commit()
            return
        # Execute statements within this transaction; executescript would commit it.
        statements = (
            """CREATE TABLE memory_maintenance_jobs (
                job_id TEXT PRIMARY KEY, owner_json TEXT NOT NULL, subject_json TEXT NOT NULL,
                policy_version TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'PENDING'
                    CHECK(status IN ('PENDING','RUNNING','IDLE','FAILED')),
                attempt_generation INTEGER NOT NULL DEFAULT 0,
                failures INTEGER NOT NULL DEFAULT 0, pending_since REAL,
                started_at REAL, updated_at REAL NOT NULL, next_retry_at REAL,
                error_code TEXT)""",
            """CREATE TABLE memory_maintenance_items (
                memory_id TEXT PRIMARY KEY REFERENCES memory_records(memory_id) ON DELETE RESTRICT,
                job_id TEXT NOT NULL REFERENCES memory_maintenance_jobs(job_id) ON DELETE RESTRICT,
                enqueued_at REAL NOT NULL, processed_at REAL)""",
            """CREATE INDEX idx_maintenance_pending ON memory_maintenance_items(job_id,processed_at,enqueued_at)""",
            """CREATE INDEX idx_maintenance_jobs ON memory_maintenance_jobs(status,pending_since)""",
            """CREATE TABLE memory_maintenance_runs (
                run_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES memory_maintenance_jobs(job_id),
                attempt_generation INTEGER NOT NULL, input_json TEXT NOT NULL, result_json TEXT NOT NULL,
                changes_json TEXT NOT NULL, completed_at REAL NOT NULL, undone_at REAL,
                UNIQUE(job_id,attempt_generation))""",
            """CREATE TABLE memory_relations (
                relation_id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES memory_maintenance_runs(run_id),
                new_memory_id TEXT NOT NULL REFERENCES memory_records(memory_id),
                old_memory_id TEXT NOT NULL REFERENCES memory_records(memory_id),
                relation TEXT NOT NULL CHECK(relation IN
                    ('DUPLICATE','REFINES','COEXISTS','SUPERSEDES','CONFLICTS','UNDETERMINED')),
                reason TEXT NOT NULL, evidence_ids_json TEXT NOT NULL, source_excerpt TEXT,
                active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
                CHECK(new_memory_id <> old_memory_id), UNIQUE(run_id,new_memory_id,old_memory_id))""",
            "CREATE INDEX idx_memory_relations_new ON memory_relations(new_memory_id,active)",
            "CREATE INDEX idx_memory_relations_old ON memory_relations(old_memory_id,active)",
        )
        for statement in statements:
            db.execute(statement)
        now = time.time()
        for row in db.execute("SELECT r.* FROM memory_records r JOIN memory_semantic_facts f USING(memory_id) "
                              "WHERE memory_type='SEMANTIC_FACT' AND status IN ('ACTIVE','CONFLICTED')"):
            enqueue_fact(db, dict(row), now)
        db.execute("INSERT INTO schema_migrations VALUES(17,?)", (now,))
        if db.execute("PRAGMA foreign_key_check").fetchone():
            raise sqlite3.IntegrityError("MAINTENANCE_FOREIGN_KEY_CHECK_FAILED")
        db.commit()
    except BaseException:
        db.rollback()
        raise
