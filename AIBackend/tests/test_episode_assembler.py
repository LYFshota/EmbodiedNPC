from __future__ import annotations

import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.episode_assembler import EpisodeAssembler
from app.episode_models import EpisodeBoundaryPolicy
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_job_repository import MemoryJobRepository
from app.sqlite_database import SQLiteDatabase


class EpisodeAssemblerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.database_path = Path(self.directory.name) / "memory.sqlite3"
        self.database = SQLiteDatabase(self.database_path)
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.policy = EpisodeBoundaryPolicy(
            idle_timeout_seconds=10, max_events=3, max_duration_seconds=30, processing_batch=64
        )
        self.assembler = EpisodeAssembler(self.ledger, self.episodes, self.policy, clock=lambda: 1000)

    def tearDown(self) -> None:
        self.directory.cleanup()

    def append(
        self, seq: int, *, observer: str = "npc_01", session: str = "session_01",
        conversation: str | None = None, observed: float | None = None,
        event_type: str = "skill_succeeded",
    ) -> None:
        terminal = event_type.startswith("goal_")
        time_value = float(seq if observed is None else observed)
        draft = ExperienceDraft(
            observer_id=observer,
            event_type=event_type,
            source_type="goal_outcome" if terminal else "skill_result",
            source_session_id=session,
            clock_domain="unity_realtime",
            source_event_id=f"source_{observer}_{seq}_{event_type}",
            actor_entity_id=observer,
            target_entity_ids=("cup_01",),
            structured_payload={"goal_type": "bring_object"} if terminal else {"step": seq},
            occurred_at=time_value,
            observed_at=time_value,
            conversation_id=conversation,
            provenance="EXECUTED_BY_SELF",
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"request_{observer}_{seq}_{event_type}",
            trace_id=f"trace_{observer}_{seq}_{event_type}",
            draft=draft,
        ))
        self.assertEqual("inserted", result.status)

    def test_single_observer_range_and_rebuild_do_not_copy_raw_experience(self) -> None:
        self.append(1)
        self.append(2)
        result = self.assembler.process_observer("npc_01")
        episode = self.episodes.get_open("npc_01")
        self.assertEqual((2, 0, None), (result.processed, result.closed, result.gap_at_seq))
        self.assertEqual((1, 2, 2), (episode.start_seq, episode.end_seq, episode.event_count))
        self.assertEqual([1, 2], [value.experience_seq for value in self.assembler.rebuild_episode(episode.episode_id)])

    def test_goal_terminal_is_included_then_closes_and_enqueues_pending_job(self) -> None:
        for index, event_type in enumerate((
            "goal_succeeded", "goal_failed", "goal_cancelled", "goal_timed_out"
        ), start=1):
            with self.subTest(event_type=event_type):
                observer = f"npc_{index:02d}"
                self.append(1, observer=observer)
                self.append(2, observer=observer, event_type=event_type)
                result = self.assembler.process_observer(observer)
                episode = self.episodes.list_for_observer(observer)[0]
                job = self.jobs.get_for_episode(episode.episode_id, "memory-extractor/1.0")
                self.assertEqual((2, 1), (result.processed, result.closed))
                self.assertEqual(("CLOSED", event_type, 2),
                                 (episode.status, episode.close_reason, episode.end_seq))
                self.assertEqual("PENDING", job.status)

    def test_observers_never_share_episode(self) -> None:
        self.append(1, observer="npc_01")
        self.append(1, observer="npc_02")
        self.assembler.process_all()
        self.assertEqual("npc_01", self.episodes.get_open("npc_01").observer_id)
        self.assertEqual("npc_02", self.episodes.get_open("npc_02").observer_id)

    def test_source_session_and_conversation_changes_close_before_current(self) -> None:
        self.append(1, session="session_a", conversation="conversation_a")
        self.append(2, session="session_b", conversation="conversation_a")
        self.append(3, session="session_b", conversation="conversation_b")
        self.assembler.process_observer("npc_01")
        episodes = self.episodes.list_for_observer("npc_01")
        self.assertEqual(
            [(1, 1, "source_session_changed"), (2, 2, "conversation_changed"), (3, 3, None)],
            [(value.start_seq, value.end_seq, value.close_reason) for value in episodes],
        )

    def test_idle_max_events_and_max_duration_close_before_current(self) -> None:
        self.append(1, observed=1)
        self.append(2, observed=20)
        self.assembler.process_observer("npc_01")
        self.assertEqual("idle_timeout", self.episodes.list_for_observer("npc_01")[0].close_reason)

        max_events = EpisodeAssembler(
            self.ledger, self.episodes,
            EpisodeBoundaryPolicy(idle_timeout_seconds=1000, max_events=3, max_duration_seconds=1000),
        )
        for seq in range(1, 5):
            self.append(seq, observer="npc_02", observed=seq)
        max_events.process_observer("npc_02")
        self.assertEqual("max_events", self.episodes.list_for_observer("npc_02")[0].close_reason)

        max_duration = EpisodeAssembler(
            self.ledger, self.episodes,
            EpisodeBoundaryPolicy(idle_timeout_seconds=1000, max_events=64, max_duration_seconds=30),
        )
        self.append(1, observer="npc_03", observed=1)
        self.append(2, observer="npc_03", observed=40)
        max_duration.process_observer("npc_03")
        self.assertEqual("max_duration", self.episodes.list_for_observer("npc_03")[0].close_reason)

    def test_idle_sweep_closes_without_new_event(self) -> None:
        self.append(1)
        self.assembler.process_observer("npc_01")
        episode = self.episodes.get_open("npc_01")
        last = self.ledger.read_exact_range("npc_01", 1, 1)[0]
        self.assertEqual(1, self.assembler.idle_sweep(last.ingested_at_utc + 11))
        self.assertEqual("idle_timeout", self.episodes.get(episode.episode_id).close_reason)

    def test_restart_recovers_open_episode_and_replay_is_deterministic(self) -> None:
        self.append(1)
        self.assembler.process_observer("npc_01")
        first_id = self.episodes.get_open("npc_01").episode_id
        self.append(2)
        reopened = EpisodeAssembler(
            ExperienceLedger(SQLiteDatabase(self.database_path).connect),
            EpisodeRepository(SQLiteDatabase(self.database_path).connect),
            self.policy,
        )
        reopened.process_observer("npc_01")
        self.assertEqual(first_id, reopened.episodes.get_open("npc_01").episode_id)
        self.assertEqual(2, reopened.episodes.get_open("npc_01").end_seq)

    def test_timestamp_anomaly_is_traced_without_reordering(self) -> None:
        self.append(1, observed=20)
        self.append(2, observed=10)
        result = self.assembler.process_observer("npc_01")
        self.assertEqual(1, result.clock_anomalies)
        self.assertEqual([1, 2], [event.experience_seq for event in self.assembler.rebuild_episode(
            self.episodes.get_open("npc_01").episode_id
        )])

    def test_sequence_gap_stops_observer_without_crossing_gap(self) -> None:
        self.append(1)
        with closing(self.database.connect()) as connection, connection:
            connection.execute("UPDATE experience_observer_sequences SET last_seq = 2 WHERE observer_id = 'npc_01'")
        self.append(3)
        result = self.assembler.process_observer("npc_01")
        self.assertEqual(2, result.gap_at_seq)
        self.assertEqual(1, self.episodes.get_open("npc_01").end_seq)


if __name__ == "__main__":
    unittest.main()
