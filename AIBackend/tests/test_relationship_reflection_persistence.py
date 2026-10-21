from __future__ import annotations

from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest

from pydantic import ValidationError

from app.memory_consolidation_context import TrustedPersistentIdentity
from app.sqlite_database import SQLiteDatabase
from app.relationship_reflection_models import RelationshipReflectionOutcome, RelationshipReflectionRecord
from app.relationship_reflection_repository import RelationshipReflectionRepository


NPC_A = TrustedPersistentIdentity("npc", "Character", "merchant.001")
NPC_B = TrustedPersistentIdentity("npc", "Character", "merchant.002")
PLAYER_A = TrustedPersistentIdentity("player", "Player", "U10001")
PLAYER_B = TrustedPersistentIdentity("player", "Player", "U10002")


class RelationshipReflectionPersistenceTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.database_path = Path(directory.name) / "memory.sqlite3"
        self.database = SQLiteDatabase(self.database_path)
        self.repository = RelationshipReflectionRepository(self.database.connect)
        with closing(self.database.connect()) as connection:
            with connection:
                connection.execute(
                    """
                    INSERT INTO episodes (
                        episode_id, observer_id, source_session_id, start_seq, end_seq,
                        started_at_utc, ended_at_utc, status, close_reason, event_count,
                        policy_version, schema_version, created_at_utc, updated_at_utc
                    ) VALUES ('episode_1', 'npc_01', 'session_1', 1, 1,
                              1, 2, 'CLOSED', 'test_end', 1,
                              'episode-boundary/1.0', 'episode/1.0', 1, 2)
                    """
                )

    def record(self, reflection_id: str = "reflection_1", **overrides) -> RelationshipReflectionRecord:
        values = dict(
            reflection_id=reflection_id, observer=NPC_A, subject=PLAYER_A,
            episode_id="episode_1", episode_input_hash="a" * 64,
            outcome=RelationshipReflectionOutcome.INTERPRETATION,
            interpretation="这次交流较友善。", evidence_event_ids=("event_1", "event_2"),
            policy_version="relationship-reflection/1.0", generated_at=10.0,
        )
        values.update(overrides)
        return RelationshipReflectionRecord(**values)

    def test_interpretation_round_trip_and_migration(self) -> None:
        value = self.record()
        self.repository.insert(value)
        self.assertEqual(self.repository.get(value.reflection_id), value)
        with closing(self.database.connect()) as connection:
            row = connection.execute(
                "SELECT evidence_event_ids_json FROM relationship_reflections WHERE reflection_id=?",
                (value.reflection_id,),
            ).fetchone()
            self.assertEqual(row[0], '["event_1","event_2"]')
            self.assertIsNotNone(connection.execute(
                "SELECT 1 FROM schema_migrations WHERE version=14"
            ).fetchone())
        SQLiteDatabase(self.database_path).migrate()
        self.assertEqual(self.repository.get(value.reflection_id), value)

    def test_no_change_is_persisted_as_processed_but_not_interpretation(self) -> None:
        value = self.record(outcome=RelationshipReflectionOutcome.NO_CHANGE, interpretation=None)
        self.repository.insert(value)
        self.assertEqual(self.repository.get(value.reflection_id), value)
        self.assertTrue(self.repository.exists_for_episode_pair(
            NPC_A, PLAYER_A, "episode_1", value.policy_version
        ))
        self.assertEqual(self.repository.list_recent_interpretations(NPC_A, PLAYER_A), ())

    def test_invalid_outcome_interpretation_and_evidence_are_rejected(self) -> None:
        for changes in (
            dict(outcome="UNKNOWN"),
            dict(interpretation=None),
            dict(interpretation="   "),
            dict(outcome=RelationshipReflectionOutcome.NO_CHANGE, interpretation="changed"),
            dict(evidence_event_ids=()),
            dict(evidence_event_ids=("event_1", "event_1")),
            dict(evidence_event_ids=("event_1", " ")),
            dict(evidence_event_ids=tuple(f"event_{i}" for i in range(65))),
        ):
            with self.subTest(changes=changes), self.assertRaises(ValidationError):
                self.record(**changes)

    def test_unique_pair_episode_policy_rejects_second_result_even_with_new_hash(self) -> None:
        first = self.record()
        self.repository.insert(first)
        with self.assertRaises(sqlite3.IntegrityError):
            self.repository.insert(self.record(
                "reflection_2", episode_input_hash="b" * 64,
                outcome=RelationshipReflectionOutcome.NO_CHANGE, interpretation=None,
            ))
        self.assertEqual(self.repository.get(first.reflection_id), first)
        self.assertIsNone(self.repository.get("reflection_2"))

    def test_observer_and_subject_pairs_are_isolated(self) -> None:
        self.repository.insert(self.record("reflection_a"))
        self.repository.insert(self.record("reflection_b", subject=PLAYER_B))
        self.repository.insert(self.record("reflection_c", observer=NPC_B))
        for observer, subject, expected in (
            (NPC_A, PLAYER_A, "reflection_a"),
            (NPC_A, PLAYER_B, "reflection_b"),
            (NPC_B, PLAYER_A, "reflection_c"),
        ):
            with self.subTest(observer=observer, subject=subject):
                self.assertTrue(self.repository.exists_for_episode_pair(
                    observer, subject, "episode_1", "relationship-reflection/1.0"
                ))
                self.assertEqual(
                    tuple(value.reflection_id for value in
                          self.repository.list_recent_interpretations(observer, subject)),
                    (expected,),
                )
        self.assertFalse(self.repository.exists_for_episode_pair(
            NPC_B, PLAYER_B, "episode_1", "relationship-reflection/1.0"
        ))

    def test_recent_query_is_bounded_interpretation_only_and_stably_ordered(self) -> None:
        rows = (
            self.record("reflection_a", generated_at=10, policy_version="policy_a"),
            self.record("reflection_b", generated_at=20, policy_version="policy_b"),
            self.record("reflection_c", generated_at=20, policy_version="policy_c"),
            self.record("reflection_d", generated_at=30, policy_version="policy_d",
                        outcome=RelationshipReflectionOutcome.NO_CHANGE, interpretation=None),
        )
        for row in rows:
            self.repository.insert(row)
        self.assertEqual(
            tuple(value.reflection_id for value in
                  self.repository.list_recent_interpretations(NPC_A, PLAYER_A, limit=2)),
            ("reflection_b", "reflection_c"),
        )
        with self.assertRaises(ValueError):
            self.repository.list_recent_interpretations(NPC_A, PLAYER_A, limit=0)
