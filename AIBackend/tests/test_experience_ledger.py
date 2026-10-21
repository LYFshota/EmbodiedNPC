from __future__ import annotations

import concurrent.futures
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.sqlite_database import LATEST_SCHEMA_VERSION, SQLiteDatabase


def draft(*, observer: str = "npc_ybot_01", source_event: str = "goal_01",
          event_type: str = "goal_succeeded", occurred: float = 1.0,
          payload: dict | None = None) -> ExperienceDraft:
    return ExperienceDraft(
        observer_id=observer,
        event_type=event_type,
        source_type="goal_outcome",
        source_session_id="session_test_01",
        clock_domain="unity_realtime",
        source_event_id=source_event,
        actor_entity_id=observer,
        target_entity_ids=("cup_01",),
        structured_payload=payload or {"status": "Succeeded"},
        occurred_at=occurred,
        observed_at=occurred,
        world_revision=3,
        trace_id="trace_01",
        provenance="EXECUTED_BY_SELF",
    )


def request(value: ExperienceDraft, suffix: str = "01") -> ExperienceAppendRequest:
    return ExperienceAppendRequest(
        request_id=f"experience_request_{suffix}", trace_id=f"experience_trace_{suffix}", draft=value
    )


class ExperienceLedgerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.database_path = Path(self.directory.name) / "memory.sqlite3"
        self.memory_repository = SQLiteDatabase(self.database_path)
        self.ledger = ExperienceLedger(self.memory_repository.connect)
        self.service = ExperienceIngestionService(self.ledger)

    def tearDown(self) -> None:
        self.directory.cleanup()

    def test_sequence_is_monotonic_per_observer_and_independent(self) -> None:
        first = self.service.append(request(draft(source_event="goal_01"), "01"))
        second = self.service.append(request(draft(source_event="goal_02", occurred=2), "02"))
        other = self.service.append(request(draft(observer="npc_ybot_02", source_event="goal_03"), "03"))
        self.assertEqual((1, 2, 1), (first.experience_seq, second.experience_seq, other.experience_seq))

    def test_duplicate_is_idempotent_without_allocating_sequence(self) -> None:
        value = request(draft(), "same")
        first = self.service.append(value)
        duplicate = self.service.append(value)
        following = self.service.append(request(draft(source_event="goal_02"), "next"))
        self.assertEqual("inserted", first.status)
        self.assertEqual("already_exists", duplicate.status)
        self.assertEqual(first.event_id, duplicate.event_id)
        self.assertEqual(2, following.experience_seq)

    def test_same_identity_with_different_payload_conflicts(self) -> None:
        first = self.service.append(request(draft(payload={"status": "Succeeded"}), "a"))
        changed = self.service.append(request(draft(payload={"status": "Failed"}), "b"))
        self.assertEqual("inserted", first.status)
        self.assertEqual("id_conflict", changed.status)
        self.assertEqual(1, self.ledger.latest_seq("npc_ybot_01"))

    def test_same_goal_source_with_contradictory_terminal_status_conflicts(self) -> None:
        succeeded = self.service.append(request(draft(event_type="goal_succeeded"), "success"))
        failed = self.service.append(request(draft(event_type="goal_failed", payload={"status": "Failed"}), "failed"))
        self.assertEqual("inserted", succeeded.status)
        self.assertEqual("id_conflict", failed.status)
        self.assertEqual(1, self.ledger.latest_seq("npc_ybot_01"))

    def test_reopen_continues_sequence_and_replays_by_sequence_not_timestamp(self) -> None:
        self.service.append(request(draft(source_event="later", occurred=20), "later"))
        reopened = ExperienceLedger(SQLiteDatabase(self.database_path).connect)
        reopened_service = ExperienceIngestionService(reopened)
        second = reopened_service.append(request(draft(source_event="earlier", occurred=10), "earlier"))
        replay = reopened.read_range("npc_ybot_01")
        self.assertEqual(2, second.experience_seq)
        self.assertEqual([1, 2], [event.experience_seq for event in replay])
        self.assertEqual([20, 10], [event.occurred_at for event in replay])

    def test_transaction_rollback_does_not_consume_sequence(self) -> None:
        with closing(self.memory_repository.connect()) as connection:
            connection.execute(
                "CREATE TRIGGER reject_experience BEFORE INSERT ON experience_events "
                "BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
            )
            connection.commit()
        failed = self.service.append(request(draft(), "failed"))
        self.assertEqual("storage_failed", failed.status)
        self.assertEqual(0, self.ledger.latest_seq("npc_ybot_01"))
        with closing(self.memory_repository.connect()) as connection:
            connection.execute("DROP TRIGGER reject_experience")
            connection.commit()
        inserted = self.service.append(request(draft(), "retry"))
        self.assertEqual(1, inserted.experience_seq)

    def test_reads_are_observer_scoped_and_identity_lookups_work(self) -> None:
        first = self.service.append(request(draft(), "a"))
        self.service.append(request(draft(observer="npc_ybot_02", source_event="goal_02"), "b"))
        key = self.ledger.get_by_event_id(first.event_id).dedup_key
        self.assertEqual("npc_ybot_01", self.ledger.get_by_dedup_key(key).observer_id)
        self.assertEqual(["npc_ybot_01"], [event.observer_id for event in self.ledger.read_range("npc_ybot_01")])
        self.assertEqual(["npc_ybot_02"], [event.observer_id for event in self.ledger.read_range("npc_ybot_02")])

    def test_observer_heads_and_exact_range_support_episode_work_discovery(self) -> None:
        self.service.append(request(draft(source_event="goal_01"), "01"))
        self.service.append(request(draft(source_event="goal_02", occurred=2), "02"))
        self.service.append(request(draft(observer="npc_ybot_02", source_event="goal_03"), "03"))
        self.assertEqual({"npc_ybot_01": 2, "npc_ybot_02": 1}, self.ledger.list_observer_heads())
        self.assertEqual([1, 2], [event.experience_seq for event in self.ledger.read_exact_range(
            "npc_ybot_01", 1, 2
        )])
        with self.assertRaisesRegex(ValueError, "EXPERIENCE_SEQUENCE_GAP"):
            self.ledger.read_exact_range("npc_ybot_01", 1, 3)

    def test_concurrent_append_has_unique_monotonic_sequences(self) -> None:
        values = [request(draft(source_event=f"goal_{index:02d}"), str(index)) for index in range(12)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
            results = list(executor.map(self.service.append, values))
        self.assertTrue(all(result.status == "inserted" for result in results))
        replay = self.ledger.read_range("npc_ybot_01")
        self.assertEqual(list(range(1, 13)), [event.experience_seq for event in replay])

    def test_current_migration_is_repeatable_and_retires_legacy_tables(self) -> None:
        self.memory_repository.migrate()
        self.memory_repository.migrate()
        with closing(self.memory_repository.connect()) as connection:
            versions = [row[0] for row in connection.execute(
                "SELECT version FROM schema_migrations ORDER BY version"
            ).fetchall()]
            tables = {row[0] for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table'"
            ).fetchall()}
        self.assertEqual(list(range(1, LATEST_SCHEMA_VERSION + 1)), versions)
        self.assertTrue({"experience_events", "episodes", "memory_jobs",
                         "memory_consolidation_state", "memory_records",
                         "relationship_reflections"}.issubset(tables))
        self.assertFalse({"episodic_memories", "memory_v1_records",
                          "memory_job_results", "relationship_states"} & tables)

    def test_committed_experience_is_database_immutable(self) -> None:
        inserted = self.service.append(request(draft(), "immutable"))
        with closing(self.memory_repository.connect()) as connection:
            with self.assertRaises(sqlite3.IntegrityError):
                connection.execute(
                    "UPDATE experience_events SET provenance = 'INFERRED' WHERE event_id = ?",
                    (inserted.event_id,),
                )
            connection.rollback()
            with self.assertRaises(sqlite3.IntegrityError):
                connection.execute("DELETE FROM experience_events WHERE event_id = ?", (inserted.event_id,))
        self.assertIsNotNone(self.ledger.get_by_event_id(inserted.event_id))


if __name__ == "__main__":
    unittest.main()
