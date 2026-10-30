"""Additive schema 20: immutable pair snapshots on the existing reflection table."""
import sqlite3
import time

RELATIONSHIP_STATE_SCHEMA_VERSION = 20


def migrate_relationship_state(connection):
    connection.execute("BEGIN IMMEDIATE")
    try:
        if connection.execute("SELECT 1 FROM schema_migrations WHERE version=20").fetchone():
            connection.commit()
            return
        version = connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0]
        if version != 19:
            raise sqlite3.IntegrityError("RELATIONSHIP_STATE_PREDECESSOR_REQUIRED")
        connection.execute("ALTER TABLE relationship_reflections ADD COLUMN pair_state_json TEXT CHECK(pair_state_json IS NULL OR json_valid(pair_state_json))")
        connection.execute("ALTER TABLE relationship_reflections ADD COLUMN state_revision INTEGER CHECK((state_revision IS NULL AND pair_state_json IS NULL) OR (state_revision IS NOT NULL AND state_revision>0 AND pair_state_json IS NOT NULL))")
        connection.execute("ALTER TABLE relationship_reflections ADD COLUMN base_reflection_id TEXT REFERENCES relationship_reflections(reflection_id)")
        connection.execute("""CREATE UNIQUE INDEX idx_relationship_pair_revision ON relationship_reflections(
            observer_identity_namespace,observer_identity_type,observer_persistent_id,
            subject_identity_namespace,subject_identity_type,subject_persistent_id,state_revision DESC)
            WHERE state_revision IS NOT NULL""")
        for operation in ("UPDATE", "DELETE"):
            connection.execute(f"""CREATE TRIGGER relationship_snapshot_immutable_{operation.lower()}
                BEFORE {operation} ON relationship_reflections WHEN OLD.state_revision IS NOT NULL
                BEGIN SELECT RAISE(ABORT,'RELATIONSHIP_SNAPSHOT_IMMUTABLE'); END""")
        connection.execute("INSERT INTO schema_migrations VALUES(20,?)", (time.time(),))
        connection.commit()
    except BaseException:
        connection.rollback()
        raise
