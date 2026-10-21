"""Deterministic RelationshipSummary selection against real pair-scoped storage."""

from __future__ import annotations

from contextlib import closing
from pathlib import Path
import tempfile
import unittest

from app.context_token_estimator import ContextTokenEstimator
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.sqlite_database import SQLiteDatabase
from app.relationship_reflection_models import RelationshipReflectionOutcome, RelationshipReflectionRecord
from app.relationship_reflection_repository import RelationshipReflectionRepository
from app.relationship_summary_reader import RelationshipSummaryPolicy, RelationshipSummaryReader


NPC_A = TrustedPersistentIdentity("npc", "Character", "merchant.001")
NPC_B = TrustedPersistentIdentity("npc", "Character", "merchant.002")
PLAYER_A = TrustedPersistentIdentity("player", "Player", "U10001")
PLAYER_B = TrustedPersistentIdentity("player", "Player", "U10002")


class RelationshipSummaryReaderTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        database = SQLiteDatabase(Path(directory.name) / "memory.sqlite3")
        self.reflections = RelationshipReflectionRepository(database.connect)
        self.connection_factory = database.connect
        self.reader = RelationshipSummaryReader(self.reflections)
        self.serial = 0

    def add(
        self, text: str | None, *, observer: TrustedPersistentIdentity = NPC_A,
        subject: TrustedPersistentIdentity = PLAYER_A,
    ) -> None:
        self.serial += 1
        serial = self.serial
        episode_id = f"episode_{serial}"
        with closing(self.connection_factory()) as connection:
            with connection:
                connection.execute(
                    """INSERT INTO episodes (
                        episode_id, observer_id, source_session_id, start_seq, end_seq,
                        started_at_utc, ended_at_utc, status, close_reason, event_count,
                        policy_version, schema_version, created_at_utc, updated_at_utc
                    ) VALUES (?, 'npc_01', 'session_1', ?, ?, 1, 2, 'CLOSED',
                              'test_end', 1, 'episode-boundary/1.0', 'episode/1.0', 1, 2)""",
                    (episode_id, serial, serial),
                )
        self.reflections.insert(RelationshipReflectionRecord(
            reflection_id=f"reflection_{serial}", observer=observer, subject=subject,
            episode_id=episode_id, episode_input_hash="a" * 64,
            outcome=(RelationshipReflectionOutcome.INTERPRETATION if text is not None
                     else RelationshipReflectionOutcome.NO_CHANGE),
            interpretation=text, evidence_event_ids=(f"event_{serial}",),
            policy_version="relationship-reflection/1.0", generated_at=float(serial),
        ))

    def test_empty_pair(self) -> None:
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ())
        self.assertEqual((summary.source_count, summary.selected_count), (0, 0))
        self.assertFalse(summary.truncated)
        self.assertEqual((summary.char_budget_used, summary.token_budget_used), (0, 0))

    def test_no_change_only_is_not_an_interpretation(self) -> None:
        self.add(None)
        self.assertEqual(self.reader.get(NPC_A, PLAYER_A).interpretations, ())

    def test_single_interpretation_and_budget_metadata(self) -> None:
        self.add("这次交流较友善。")
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("这次交流较友善。",))
        self.assertEqual((summary.source_count, summary.selected_count), (1, 1))
        self.assertEqual(summary.char_budget_used, len("这次交流较友善。"))
        self.assertEqual(summary.token_budget_used, ContextTokenEstimator().estimate_text("这次交流较友善。"))
        self.assertFalse(summary.truncated)

    def test_chronology_and_conflict_are_preserved(self) -> None:
        for text in ("较早交流友善。", "逐渐熟悉。", "最近交流不愉快。"):
            self.add(text)
        self.assertEqual(self.reader.get(NPC_A, PLAYER_A).interpretations,
                         ("较早交流友善。", "逐渐熟悉。", "最近交流不愉快。"))

    def test_exact_dedup_keeps_newer_occurrence(self) -> None:
        for text in ("A", "B", "A", "C"):
            self.add(text)
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("B", "A", "C"))
        self.assertEqual((summary.source_count, summary.selected_count), (4, 3))
        self.assertFalse(summary.truncated)

    def test_final_item_limit_selects_recent_unique_items(self) -> None:
        self.reader = RelationshipSummaryReader(
            self.reflections, RelationshipSummaryPolicy(source_window=8, max_items=3),
        )
        for text in ("A", "B", "C", "D", "E"):
            self.add(text)
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("C", "D", "E"))
        self.assertTrue(summary.truncated)

    def test_source_window_is_bounded_even_with_duplicates(self) -> None:
        self.reader = RelationshipSummaryReader(
            self.reflections, RelationshipSummaryPolicy(source_window=5, max_items=3),
        )
        for index in range(25):
            self.add(f"outside_{index}")
        for text in ("A", "A", "A", "B", "C"):
            self.add(text)
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("A", "B", "C"))
        self.assertEqual(summary.source_count, 5)
        self.assertTrue(summary.truncated)

    def test_char_budget_stops_before_older_atomic_item(self) -> None:
        self.reader = RelationshipSummaryReader(
            self.reflections, RelationshipSummaryPolicy(max_chars=10, max_tokens=1200),
        )
        self.add("较早的一整条解释不能截断")
        self.add("最新解释")
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("最新解释",))
        self.assertEqual(summary.char_budget_used, len("最新解释"))
        self.assertTrue(summary.truncated)

    def test_token_budget_stops_before_older_atomic_item(self) -> None:
        self.reader = RelationshipSummaryReader(
            self.reflections, RelationshipSummaryPolicy(max_chars=2000, max_tokens=8),
        )
        self.add("较早的一整条解释不能截断")
        self.add("最新解释")
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertEqual(summary.interpretations, ("最新解释",))
        self.assertEqual(summary.token_budget_used,
                         ContextTokenEstimator().estimate_text("最新解释"))
        self.assertTrue(summary.truncated)

    def test_default_budget_fits_one_max_length_interpretation(self) -> None:
        self.add("字" * 500)
        self.assertEqual(self.reader.get(NPC_A, PLAYER_A).interpretations, ("字" * 500,))

    def test_pair_isolation(self) -> None:
        self.add("A1")
        self.add("A2", subject=PLAYER_B)
        self.add("B1", observer=NPC_B)
        self.assertEqual(self.reader.get(NPC_A, PLAYER_A).interpretations, ("A1",))
        self.assertEqual(self.reader.get(NPC_A, PLAYER_B).interpretations, ("A2",))
        self.assertEqual(self.reader.get(NPC_B, PLAYER_A).interpretations, ("B1",))
        self.assertEqual(self.reader.get(NPC_B, PLAYER_B).interpretations, ())

    def test_summary_has_no_identity_or_action_authority(self) -> None:
        self.add("这次交流友善。")
        summary = self.reader.get(NPC_A, PLAYER_A)
        self.assertTrue(summary.not_current_world_state)
        self.assertTrue(summary.not_instruction)
        self.assertTrue(summary.not_action_authorization)
        wire = summary.model_dump_json()
        for forbidden in ("U10001", "merchant.001", "reflection_id", "episode_id",
                          "evidence_event_ids", "episode_input_hash", "capabilities"):
            self.assertNotIn(forbidden, wire)

    def test_policy_and_identity_inputs_are_bounded(self) -> None:
        for policy in (
            dict(source_window=65), dict(max_items=0), dict(max_items=17),
            dict(source_window=2, max_items=3), dict(max_chars=0), dict(max_tokens=0),
        ):
            with self.subTest(policy=policy), self.assertRaises(ValueError):
                RelationshipSummaryPolicy(**policy)
        with self.assertRaises(TypeError):
            self.reader.get("npc_01", PLAYER_A)
