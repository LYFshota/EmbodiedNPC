"""Schema 18: legal multi-source derived lineage, preserving populated Schema 17."""
import sqlite3
import time

REFLECTION_SCHEMA_VERSION = 18


def migrate_memory_reflection(db):
    if db.execute('SELECT 1 FROM schema_migrations WHERE version=18').fetchone():
        return
    # Rebuild the parent with foreign keys disabled only on this migration
    # connection. No child row is deleted; verify all references before commit.
    db.execute('PRAGMA foreign_keys=OFF')
    db.execute('PRAGMA legacy_alter_table=ON')
    db.execute('BEGIN IMMEDIATE')
    try:
        if db.execute('SELECT 1 FROM schema_migrations WHERE version=18').fetchone():
            db.commit()
            return
        sql = db.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name='memory_records'").fetchone()[0]
        # Only extend the current parent constraints; old migrations stay frozen.
        sql = sql.replace('CREATE TABLE memory_records', 'CREATE TABLE memory_records_v18', 1)
        sql = sql.replace("'EPISODIC','PROFILE_FACT','SEMANTIC_FACT'", "'EPISODIC','PROFILE_FACT','SEMANTIC_FACT','DERIVED_INSIGHT'", 1)
        sql = sql.replace('episode_id TEXT NOT NULL REFERENCES', 'episode_id TEXT REFERENCES')
        sql = sql.replace('job_id TEXT NOT NULL REFERENCES', 'job_id TEXT REFERENCES')
        sql = sql.replace("memory_type NOT IN ('PROFILE_FACT','SEMANTIC_FACT')", "memory_type NOT IN ('PROFILE_FACT','SEMANTIC_FACT','DERIVED_INSIGHT')")
        end = sql.rfind(')')
        sql = sql[:end] + ", CHECK((memory_type='DERIVED_INSIGHT' AND episode_id IS NULL AND job_id IS NULL AND schema_version='memory-record/4.0') OR (memory_type<>'DERIVED_INSIGHT' AND episode_id IS NOT NULL AND job_id IS NOT NULL))" + sql[end:]
        db.execute(sql)
        triggers = [r[0] for r in db.execute("SELECT sql FROM sqlite_master WHERE type='trigger' AND tbl_name='memory_records'")]
        indexes = [r[0] for r in db.execute("SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='memory_records' AND sql IS NOT NULL")]
        db.execute('INSERT INTO memory_records_v18 SELECT * FROM memory_records')
        db.execute('DROP TABLE memory_records')
        db.execute('ALTER TABLE memory_records_v18 RENAME TO memory_records')
        for statement in (*indexes, *triggers):
            db.execute(statement)
        db.execute("ALTER TABLE memory_maintenance_jobs ADD COLUMN task_kind TEXT NOT NULL DEFAULT 'MAINTENANCE' CHECK(task_kind IN ('MAINTENANCE','REFLECTION'))")
        statements = (
            '''CREATE TABLE memory_reflection_items (
                job_id TEXT NOT NULL REFERENCES memory_maintenance_jobs(job_id),
                memory_id TEXT NOT NULL REFERENCES memory_records(memory_id),
                enqueued_at REAL NOT NULL, processed_at REAL,
                PRIMARY KEY(job_id,memory_id))''',
            'CREATE INDEX idx_reflection_pending ON memory_reflection_items(job_id,processed_at,enqueued_at)',
            '''CREATE TABLE memory_derived_insights (
                memory_id TEXT PRIMARY KEY REFERENCES memory_records(memory_id),
                run_id TEXT NOT NULL REFERENCES memory_maintenance_runs(run_id),
                derivation_type TEXT NOT NULL CHECK(derivation_type IN ('THEME','PATTERN','EXPERIENCE_LESSON')),
                uncertainty TEXT NOT NULL CHECK(uncertainty='TENTATIVE'))''',
            '''CREATE TABLE memory_insight_sources (
                memory_id TEXT NOT NULL REFERENCES memory_derived_insights(memory_id),
                source_memory_id TEXT NOT NULL REFERENCES memory_records(memory_id),
                source_revision INTEGER NOT NULL CHECK(source_revision>=1),
                source_status TEXT NOT NULL CHECK(source_status IN ('ACTIVE','CONFLICTED','SUPERSEDED')),
                PRIMARY KEY(memory_id,source_memory_id), CHECK(memory_id<>source_memory_id))''',
            '''CREATE TABLE memory_insight_episodes (
                memory_id TEXT NOT NULL REFERENCES memory_derived_insights(memory_id),
                episode_id TEXT NOT NULL REFERENCES episodes(episode_id),
                PRIMARY KEY(memory_id,episode_id))''',
            '''CREATE TRIGGER memory_insight_type_insert BEFORE INSERT ON memory_derived_insights
                WHEN NOT EXISTS (SELECT 1 FROM memory_records WHERE memory_id=NEW.memory_id AND memory_type='DERIVED_INSIGHT')
                BEGIN SELECT RAISE(ABORT,'DERIVED_INSIGHT_RECORD_REQUIRED'); END''',
            '''CREATE TRIGGER memory_insight_source_insert BEFORE INSERT ON memory_insight_sources
                WHEN NOT EXISTS (SELECT 1 FROM memory_records WHERE memory_id=NEW.source_memory_id AND memory_type IN ('EPISODIC','SEMANTIC_FACT'))
                BEGIN SELECT RAISE(ABORT,'DIRECT_INSIGHT_SOURCE_REQUIRED'); END''',
            '''CREATE TRIGGER memory_records_insight_type_update BEFORE UPDATE OF memory_type ON memory_records
                WHEN NEW.memory_type<>'DERIVED_INSIGHT' AND EXISTS (SELECT 1 FROM memory_derived_insights WHERE memory_id=OLD.memory_id)
                BEGIN SELECT RAISE(ABORT,'DERIVED_INSIGHT_RECORD_REQUIRED'); END''',
        )
        for statement in statements:
            db.execute(statement)
        # Existing admitted Episodic memories and already maintained facts can
        # join the queue. Unmaintained facts are enrolled by maintenance commit.
        from .memory_reflection_repository import enqueue_reflection
        now = time.time()
        for row in db.execute("SELECT r.* FROM memory_records r WHERE r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED') AND (r.memory_type='EPISODIC' OR (r.memory_type='SEMANTIC_FACT' AND EXISTS (SELECT 1 FROM memory_maintenance_items i WHERE i.memory_id=r.memory_id AND i.processed_at IS NOT NULL)))"):
            enqueue_reflection(db, dict(row), now)
        if db.execute('PRAGMA foreign_key_check').fetchone():
            raise sqlite3.IntegrityError('REFLECTION_FOREIGN_KEY_CHECK_FAILED')
        db.execute('INSERT INTO schema_migrations VALUES(18,?)', (now,))
        db.commit()
    except BaseException:
        db.rollback()
        raise
    finally:
        db.execute('PRAGMA legacy_alter_table=OFF')
        db.execute('PRAGMA foreign_keys=ON')
