"""Frozen retirement 15 and staged Semantic Fact foundation 16."""

from __future__ import annotations

import hashlib
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.sqlite_database import LATEST_SCHEMA_VERSION, SQLiteDatabase


KEEP_TABLES = {
    "schema_migrations", "experience_observer_sequences", "experience_events",
    "episodes", "memory_jobs", "memory_consolidation_state", "memory_records",
    "memory_subjects", "memory_evidence", "memory_profile_facts",
    "memory_embeddings", "relationship_reflections",
}
KEEP_INDEXES = {
    "idx_episode_observer_range", "idx_episode_one_open_per_observer",
    "idx_experience_observer_source", "idx_experience_request", "idx_experience_trace",
    "idx_memory_jobs_observer_status", "idx_memory_jobs_status_retry",
    "idx_memory_owner", "idx_memory_subject_identity",
    "idx_relationship_reflections_pair_recent",
}
KEEP_TRIGGERS = {
    "experience_events_immutable_delete", "experience_events_immutable_update",
    "episodes_closed_immutable_delete", "episodes_closed_immutable_update",
    "episodes_no_overlap_insert", "episodes_no_overlap_update",
}


def objects(connection: sqlite3.Connection, kind: str) -> set[str]:
    return {row[0] for row in connection.execute(
        "SELECT name FROM sqlite_master WHERE type=? AND name NOT LIKE 'sqlite_%'", (kind,)
    )}


def fingerprint(connection: sqlite3.Connection, table: str) -> str:
    rows = [tuple(row) for row in connection.execute(f'SELECT * FROM "{table}" ORDER BY rowid')]
    return hashlib.sha256(repr(rows).encode("utf-8")).hexdigest()


def seed_row(connection: sqlite3.Connection, table: str, **values: object) -> None:
    """Fill irrelevant required fields while keeping fixture identities explicit."""
    columns = connection.execute(f'PRAGMA table_info("{table}")').fetchall()
    data = {}
    for column in columns:
        name, kind, required, default, primary_key = (
            column[1], column[2], column[3], column[4], column[5]
        )
        if name in values:
            data[name] = values[name]
        elif (required or primary_key) and default is None:
            data[name] = (1 if kind.upper().startswith(("INT", "REAL"))
                          else f"{table}_{name}")
    names = ", ".join(data)
    placeholders = ", ".join("?" for _ in data)
    connection.execute(f'INSERT INTO "{table}" ({names}) VALUES ({placeholders})', tuple(data.values()))


def seed_populated_v14(connection: sqlite3.Connection) -> None:
    seed_row(connection, "experience_observer_sequences", observer_id="npc", last_seq=1)
    seed_row(connection, "experience_events", event_id="event_1", observer_id="npc",
             experience_seq=1, source_event_id="ledger_source", payload_json="{}")
    seed_row(connection, "episodes", episode_id="episode_1", observer_id="npc",
             source_session_id="session", status="CLOSED", close_reason="test",
             start_seq=1, end_seq=1, event_count=1, started_at_utc=1,
             ended_at_utc=2, created_at_utc=1, updated_at_utc=2)
    for job_id, version in (("v2_job", "memory-consolidator/2.0"),
                            ("legacy_job", "memory-extractor/1.0")):
        seed_row(connection, "memory_jobs", job_id=job_id, observer_id="npc",
                 episode_id="episode_1", extractor_version=version,
                 idempotency_key=job_id, status="SUCCEEDED")
    seed_row(connection, "memory_consolidation_state", observer_id="npc",
             extractor_version="memory-consolidator/2.0", schema_version="state/2.0")
    seed_row(connection, "memory_records", memory_id="canonical_1", observer_entity_id="npc",
             identity_namespace="npc", identity_type="Character", persistent_id="npc.1",
             memory_type="PROFILE_FACT", scope="NPC_PRIVATE", summary="Known preference",
             provenance_set_json='["event_1"]', episode_id="episode_1", job_id="v2_job",
             status="ACTIVE")
    seed_row(connection, "memory_subjects", memory_id="canonical_1",
             identity_namespace="player", identity_type="Player", persistent_id="player.1")
    seed_row(connection, "memory_evidence", memory_id="canonical_1",
             experience_event_id="event_1", experience_seq=1)
    seed_row(connection, "memory_profile_facts", memory_id="canonical_1",
             fact_key="favorite_color", attribute_key="favorite_color", attribute_value="blue",
             normalized_value="blue", cardinality="SINGLE_VALUE", temporal_precision="UNKNOWN")
    seed_row(connection, "memory_embeddings", memory_id="canonical_1",
             model_key="test-model", vector_blob=b"\x00", dimension=1)
    seed_row(connection, "relationship_reflections", reflection_id="reflection_1",
             observer_identity_namespace="npc", observer_identity_type="Character",
             observer_persistent_id="npc.1", subject_identity_namespace="player",
             subject_identity_type="Player", subject_persistent_id="player.1",
             episode_id="episode_1", episode_input_hash="a" * 64, outcome="NO_CHANGE",
             interpretation=None, evidence_event_ids_json='["event_1"]',
             policy_version="relationship-reflection/1.0")

    for memory_id in ("old_1", "old_2"):
        seed_row(connection, "episodic_memories", memory_id=memory_id,
                 candidate_id=memory_id, observer_id="npc", status="ACTIVE")
    seed_row(connection, "memory_observation_sources", memory_id="old_1")
    seed_row(connection, "memory_event_sources", memory_id="old_1",
             source_event_id="missing_from_ledger")
    seed_row(connection, "memory_tags", memory_id="old_1")
    seed_row(connection, "memory_unknown_fields", memory_id="old_1")
    seed_row(connection, "memory_report_provenance", memory_id="old_2",
             parent_memory_id="old_1", report_hop=1)
    connection.execute("INSERT INTO episodic_memories_fts(memory_id,observer_id,summary,tags) "
                       "VALUES('old_1','npc','Old derived summary','old')")
    seed_row(connection, "relationship_states", subject_id="npc", other_entity_id="player")
    seed_row(connection, "relationship_events", relationship_event_id="old_relationship_1",
             subject_id="npc", other_entity_id="player", source_memory_id="old_1",
             reason_event_id="missing_from_ledger")

    seed_row(connection, "memory_v1_records", memory_id="v1_memory_1", observer_id="npc",
             memory_type="EPISODIC", scope="NPC_PRIVATE", episode_id="episode_1",
             job_id="legacy_job", status="ACTIVE", subject_ids_json="[]",
             canonical_candidate_hash="v1_hash", provenance_set_json='["event_1"]')
    seed_row(connection, "memory_v1_evidence", memory_id="v1_memory_1",
             observer_id="npc", experience_event_id="event_1")
    seed_row(connection, "memory_job_results", job_id="legacy_job", episode_id="episode_1")
    for fact_id, status, predecessor in (("fact_1", "SUPERSEDED", None),
                                          ("fact_2", "ACTIVE", "fact_1")):
        seed_row(connection, "memory_v1_facts", memory_id=fact_id, fact_key=fact_id,
                 observer_id="npc", subject_id="player", memory_type="PROFILE_FACT",
                 scope="NPC_PRIVATE", cardinality="SINGLE_VALUE",
                 temporal_precision="UNKNOWN", status=status,
                 canonical_candidate_hash=fact_id, supersedes_memory_id=predecessor,
                 provenance_set_json='["event_1"]')
    seed_row(connection, "memory_v1_fact_evidence", memory_id="fact_2",
             observer_id="npc", experience_event_id="event_1")
    seed_row(connection, "memory_v1_embeddings", embedding_id="embedding_1",
             memory_id="v1_memory_1", memory_type="EPISODIC", status="PENDING",
             dimension=1, content_hash="legacy_embedding_hash")

    seed_row(connection, "profile_consolidation_jobs", job_id="profile_job_1",
             observer_id="npc", idempotency_key="profile_job_1", status="SUCCEEDED",
             episode_ids_json='["episode_1"]')
    seed_row(connection, "profile_consolidation_state", observer_id="npc")
    seed_row(connection, "profile_job_results", job_id="profile_job_1")
    seed_row(connection, "relationship_shadow_jobs", job_id="shadow_job_1",
             observer_id="npc", idempotency_key="shadow_job_1", status="SUCCEEDED",
             episode_ids_json='["episode_1"]')
    seed_row(connection, "relationship_shadow_state", observer_id="npc")
    seed_row(connection, "relationship_shadow_job_results", job_id="shadow_job_1")
    seed_row(connection, "memory_v1_relationship_evidence_shadow",
             relationship_evidence_id="shadow_evidence_1", observer_id="npc",
             counterparty_id="player", polarity="POSITIVE", status="SHADOW",
             canonical_candidate_hash="shadow_hash", provenance_set_json='["event_1"]')
    seed_row(connection, "memory_v1_relationship_evidence_links",
             relationship_evidence_id="shadow_evidence_1", observer_id="npc",
             experience_event_id="event_1")


class SchemaRetirementTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "memory.sqlite3"

    def pre_b7_database(self) -> SQLiteDatabase:
        database = object.__new__(SQLiteDatabase)
        database.database_path = str(self.path)
        database._migrate_historical()
        return database

    def assert_final_schema(self, connection: sqlite3.Connection) -> None:
        semantic = connection.execute("SELECT 1 FROM schema_migrations WHERE version=16").fetchone()
        maintenance = connection.execute("SELECT 1 FROM schema_migrations WHERE version=17").fetchone()
        reflection = connection.execute('SELECT 1 FROM schema_migrations WHERE version=18').fetchone()
        tasks = connection.execute('SELECT 1 FROM schema_migrations WHERE version=19').fetchone()
        pair_state = connection.execute('SELECT 1 FROM schema_migrations WHERE version=20').fetchone()
        self.assertEqual(objects(connection, "table"), KEEP_TABLES | ({"memory_semantic_facts"} if semantic else set()) | ({
            "memory_maintenance_jobs", "memory_maintenance_items", "memory_maintenance_runs", "memory_relations"
        } if maintenance else set()) | ({'memory_reflection_items','memory_derived_insights',
            'memory_insight_sources','memory_insight_episodes'} if reflection else set()) |
            ({'npc_tasks', 'npc_task_commands'} if tasks else set()))
        self.assertEqual(objects(connection, "index"), KEEP_INDEXES | ({
            "idx_maintenance_pending", "idx_maintenance_jobs", "idx_memory_relations_new", "idx_memory_relations_old"
        } if maintenance else set()) | ({'idx_reflection_pending'} if reflection else set()) |
            ({'npc_tasks_active_dedup', 'npc_tasks_lookup', 'npc_task_commands_by_task'} if tasks else set()) |
            ({'idx_relationship_pair_revision'} if pair_state else set()))
        self.assertEqual(objects(connection, "trigger"), KEEP_TRIGGERS | ({
            "memory_semantic_facts_type_insert", "memory_semantic_facts_type_update",
            "memory_records_semantic_type_update",
        } if semantic else set()) | ({'memory_insight_type_insert','memory_insight_source_insert',
            'memory_records_insight_type_update'} if reflection else set()) |
            ({'npc_task_commands_immutable_update', 'npc_task_commands_immutable_delete',
              'npc_tasks_identity_immutable'} if tasks else set()) |
            ({'relationship_snapshot_immutable_update','relationship_snapshot_immutable_delete'} if pair_state else set()))
        self.assertFalse(any("fts" in name for name in objects(connection, "table")))
        self.assertEqual(connection.execute("PRAGMA foreign_key_check").fetchall(), [])

    def test_fresh_database_and_second_start_keep_only_runtime_schema(self) -> None:
        database = SQLiteDatabase(self.path)
        with closing(database.connect()) as connection:
            self.assert_final_schema(connection)
            self.assertEqual([row[0] for row in connection.execute(
                "SELECT version FROM schema_migrations ORDER BY version"
            )], list(range(1, LATEST_SCHEMA_VERSION + 1)))
        database.migrate()
        SQLiteDatabase(self.path)
        with closing(database.connect()) as connection:
            self.assert_final_schema(connection)
            self.assertEqual(connection.execute(
                "SELECT COUNT(*) FROM schema_migrations WHERE version=15"
            ).fetchone()[0], 1)

    def test_populated_v14_upgrade_preserves_every_runtime_table(self) -> None:
        database = self.pre_b7_database()
        with closing(database.connect()) as connection:
            with connection:
                seed_populated_v14(connection)
            before = {table: fingerprint(connection, table) for table in KEEP_TABLES
                      if table != "schema_migrations"}
            self.assertEqual(connection.execute("PRAGMA foreign_key_check").fetchall(), [])
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM memory_v1_facts").fetchone()[0], 2)
        # A1 must not silently reset/convert the populated canonical store, or
        # even run the historical retirement before refusing the new upgrade.
        with self.assertRaisesRegex(sqlite3.IntegrityError, "REQUIRES_EMPTY_CANONICAL_MEMORY"):
            database.migrate()
        with closing(database.connect()) as connection:
            self.assertEqual(before, {table: fingerprint(connection, table) for table in KEEP_TABLES
                                      if table != "schema_migrations"})
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM memory_v1_facts").fetchone()[0], 2)
            self.assertIsNone(connection.execute("SELECT 1 FROM schema_migrations WHERE version=15").fetchone())
            # The already published migration 15 retains its original behavior.
            database._retire_legacy_schema(connection)
        with closing(database.connect()) as connection:
            self.assert_final_schema(connection)

            self.assertEqual(before, {table: fingerprint(connection, table) for table in KEEP_TABLES
                                      if table != "schema_migrations"})
            self.assertEqual(connection.execute(
                "SELECT evidence_event_ids_json FROM relationship_reflections"
            ).fetchone()[0], '["event_1"]')

    def test_semantic_fact_constraints_and_evidence_foreign_key(self) -> None:
        database = SQLiteDatabase(self.path)
        with closing(database.connect()) as connection:
            with connection:
                seed_row(connection, "experience_events", event_id="source", observer_id="npc",
                         payload_json="{}")
                seed_row(connection, "episodes", episode_id="episode", observer_id="npc",
                         status="CLOSED", close_reason="test", started_at_utc=1, ended_at_utc=2,
                         created_at_utc=1, updated_at_utc=2)
                seed_row(connection, "memory_jobs", job_id="job", episode_id="episode",
                         observer_id="npc", status="SUCCEEDED")
                seed_row(connection, "memory_records", memory_id="semantic", observer_entity_id="npc",
                         identity_namespace="npc", identity_type="Character", persistent_id="npc.1",
                         memory_type="SEMANTIC_FACT", scope="NPC_PRIVATE", summary="以前喜欢茶。",
                         provenance_set_json='["HEARD"]', episode_id="episode", job_id="job",
                         status="ACTIVE", schema_version="memory-record/3.0")
            # An existing Experience alone is insufficient: the excerpt must be
            # linked to this particular canonical memory's evidence.
            with self.assertRaises(sqlite3.IntegrityError), connection:
                connection.execute("INSERT INTO memory_semantic_facts(memory_id,source_excerpt,excerpt_event_id) "
                                   "VALUES('semantic','以前喜欢茶','source')")
            with connection:
                seed_row(connection, "memory_evidence", memory_id="semantic",
                         experience_event_id="source", experience_seq=1)
                connection.execute("INSERT INTO memory_semantic_facts(memory_id,source_excerpt,excerpt_event_id) "
                                   "VALUES('semantic','以前喜欢茶','source')")
            for sql in (
                "UPDATE memory_records SET revision=0 WHERE memory_id='semantic'",
                "UPDATE memory_records SET revision=1.5 WHERE memory_id='semantic'",
                "UPDATE memory_records SET schema_version='memory-record/2.0' WHERE memory_id='semantic'",
                "UPDATE memory_records SET persistent_id=NULL WHERE memory_id='semantic'",
                "UPDATE memory_records SET memory_type='EPISODIC' WHERE memory_id='semantic'",
                "UPDATE memory_semantic_facts SET supersedes_memory_id='semantic'",
                "UPDATE memory_semantic_facts SET excerpt_event_id='missing'",
                "UPDATE memory_semantic_facts SET valid_from=10",
                "UPDATE memory_semantic_facts SET temporal_precision='OBSERVED_UTC',valid_from=20,valid_to=10",
                "UPDATE memory_semantic_facts SET source_excerpt=''",
                "UPDATE memory_semantic_facts SET schema_version='unsupported'",
            ):
                with self.subTest(sql=sql), self.assertRaises(sqlite3.IntegrityError), connection:
                    connection.execute(sql)
            with connection:
                seed_row(connection, "memory_records", memory_id="episodic", observer_entity_id="npc",
                         memory_type="EPISODIC", scope="NPC_PRIVATE", summary="历史经历",
                         provenance_set_json='[]', episode_id="episode", job_id="job", status="ACTIVE")
            with self.assertRaisesRegex(sqlite3.IntegrityError, "SEMANTIC_FACT_RECORD_REQUIRED"), connection:
                connection.execute("INSERT INTO memory_semantic_facts(memory_id,source_excerpt,excerpt_event_id) "
                                   "VALUES('episodic','原文','source')")
            self.assertEqual(connection.execute("PRAGMA foreign_key_check").fetchall(), [])

    def test_semantic_migration_failure_rolls_back_and_future_version_refused(self) -> None:
        database = self.pre_b7_database()
        with closing(database.connect()) as connection:
            database._retire_legacy_schema(connection)
            connection.execute("CREATE TRIGGER reject_v16 BEFORE INSERT ON schema_migrations "
                               "WHEN NEW.version=16 BEGIN SELECT RAISE(ABORT,'reject semantic migration'); END")
            connection.commit()
        with self.assertRaisesRegex(sqlite3.IntegrityError, "reject semantic migration"):
            database.migrate()
        with closing(database.connect()) as connection:
            self.assertNotIn("memory_semantic_facts", objects(connection, "table"))
            self.assertNotIn("revision", {r[1] for r in connection.execute("PRAGMA table_info(memory_records)")})
            self.assertIsNone(connection.execute("SELECT 1 FROM schema_migrations WHERE version=16").fetchone())
            self.assertIn("idx_memory_owner", objects(connection, "index"))
            connection.execute("DROP TRIGGER reject_v16")
            connection.commit()
        database.migrate()
        with closing(database.connect()) as connection:
            self.assert_final_schema(connection)
            connection.execute("INSERT INTO schema_migrations VALUES(?,1)", (LATEST_SCHEMA_VERSION+1,))
            connection.commit()
        with self.assertRaisesRegex(sqlite3.IntegrityError, "UNSUPPORTED_FUTURE_MEMORY_SCHEMA"):
            database.migrate()

    def test_v16_profile_store_is_refused_without_mutating_rows(self) -> None:
        database = SQLiteDatabase(self.path)
        with closing(database.connect()) as connection:
            with connection:
                seed_row(connection, "episodes", episode_id="old", observer_id="npc",
                         status="CLOSED", close_reason="test", started_at_utc=1, ended_at_utc=2,
                         created_at_utc=1, updated_at_utc=2)
                seed_row(connection, "memory_jobs", job_id="old", episode_id="old",
                         observer_id="npc", status="SUCCEEDED")
                seed_row(connection, "memory_records", memory_id="old", observer_entity_id="npc",
                         identity_namespace="npc", identity_type="Character", persistent_id="npc.1",
                         memory_type="PROFILE_FACT", scope="NPC_PRIVATE", summary="old profile",
                         provenance_set_json='["HEARD"]', episode_id="old", job_id="old",
                         status="ACTIVE", schema_version="memory-record/2.0")
            before = tuple(tuple(row) for row in connection.execute("SELECT * FROM memory_records"))
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INCOMPATIBLE_PROFILE_MEMORY"):
            database.migrate()
        with closing(database.connect()) as connection:
            self.assertEqual(before, tuple(tuple(row) for row in connection.execute("SELECT * FROM memory_records")))

    def test_cleanup_failure_rolls_back_drops_and_version(self) -> None:
        database = self.pre_b7_database()
        with closing(database.connect()) as connection:
            connection.execute("CREATE TRIGGER reject_v15 BEFORE INSERT ON schema_migrations "
                               "WHEN NEW.version=15 BEGIN SELECT RAISE(ABORT,'test failure'); END")
            connection.commit()
            with self.assertRaisesRegex(sqlite3.IntegrityError, "test failure"):
                database._retire_legacy_schema(connection)
            self.assertIn("episodic_memories", objects(connection, "table"))
            self.assertIn("memory_v1_records_fts", objects(connection, "table"))
            self.assertIn("memory_v1_records_fts_insert", objects(connection, "trigger"))
            self.assertIsNone(connection.execute(
                "SELECT 1 FROM schema_migrations WHERE version=15"
            ).fetchone())
            connection.execute("DROP TRIGGER reject_v15")
            connection.commit()
            database._retire_legacy_schema(connection)
            self.assert_final_schema(connection)


if __name__ == "__main__":
    unittest.main()
