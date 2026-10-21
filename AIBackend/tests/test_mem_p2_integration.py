from __future__ import annotations

import asyncio
import tempfile
import unittest
from dataclasses import fields
from pathlib import Path
from unittest.mock import patch

from app.consolidation_state import ConsolidationStateRepository
from app.episode_assembler import EpisodeAssembler
from app.episode_models import EpisodeBoundaryPolicy, MemoryJobPolicy
from app.episode_processing import EpisodeProcessingLoop
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_job_repository import MemoryJobRepository
from app.sqlite_database import SQLiteDatabase


class MemP2IntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "memory.sqlite3"
        self.database = SQLiteDatabase(self.path)
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.state = ConsolidationStateRepository(self.database.connect)
        self.assembler = EpisodeAssembler(
            self.ledger,
            self.episodes,
            EpisodeBoundaryPolicy(idle_timeout_seconds=30, max_events=64, max_duration_seconds=300),
            extractor_version="memory-consolidator/2.0",
        )

    async def asyncTearDown(self) -> None:
        self.directory.cleanup()

    def append(self, seq: int, *, observer: str = "npc_01", session: str = "session_01",
               terminal: bool = False) -> str:
        event_type = "goal_succeeded" if terminal else "skill_succeeded"
        draft = ExperienceDraft(
            observer_id=observer, event_type=event_type,
            source_type="goal_outcome" if terminal else "skill_result",
            source_session_id=session, clock_domain="unity_realtime",
            source_event_id=f"event_{observer}_{seq}", actor_entity_id=observer,
            structured_payload={"goal_type": "bring_object"} if terminal else {"step": seq},
            occurred_at=float(seq), observed_at=float(seq), provenance="EXECUTED_BY_SELF",
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"request_{observer}_{seq}", trace_id=f"trace_{observer}_{seq}", draft=draft
        ))
        self.assertEqual("inserted", result.status)
        return result.event_id

    def append_dialogue(
        self, seq: int, *, observer: str = "npc_cafe_01", event_type: str,
        text: str, session: str = "cafe_session_01", conversation: str = "cafe_conversation_01",
        episode_directive: str | None = None,
    ) -> str:
        is_response = event_type == "dialogue_response"
        actor = observer if is_response else "player_01"
        target = "player_01" if is_response else observer
        structured_payload = {
            "direction": "npc_to_player" if is_response else "player_to_npc",
            "runtime_path": "cognitive_runtime_v2",
        }
        if episode_directive is not None:
            structured_payload["episode_directive"] = episode_directive
        draft = ExperienceDraft(
            schema_version="experience-draft/1.1",
            observer_id=observer,
            event_type=event_type,
            source_type="dialogue",
            source_session_id=session,
            clock_domain="unity_realtime",
            source_event_id=f"runtime_v2_{observer}_{seq}_{event_type}",
            actor_entity_id=actor,
            target_entity_ids=(target,),
            persistent_participants=({
                "entity_id": "player_01", "identity_namespace": "player",
                "identity_type": "Player", "persistent_id": "U10001",
            },),
            structured_payload=structured_payload,
            natural_language_payload=text,
            occurred_at=float(seq),
            observed_at=float(seq),
            world_revision=seq,
            request_id=f"cognitive_request_{observer}_{seq}",
            trace_id=f"cognitive_trace_{observer}_{seq}",
            conversation_id=conversation,
            provenance="SYSTEM_CONFIRMED",
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"append_request_{observer}_{seq}",
            trace_id=f"append_trace_{observer}_{seq}",
            draft=draft,
        ))
        self.assertEqual("inserted", result.status)
        return result.event_id

    async def test_runtime_v2_dialogue_remains_one_open_episode_with_exact_ledger_replay(self) -> None:
        expected = (
            ("dialogue_input", "请给我一杯咖啡。"),
            ("dialogue_response", "好的，请稍等。"),
            ("dialogue_input", "谢谢。"),
            ("dialogue_response", "不客气。"),
        )
        for seq, (event_type, text) in enumerate(expected, start=1):
            self.append_dialogue(
                seq, event_type=event_type, text=text,
                episode_directive="CONTINUE" if event_type == "dialogue_response" else None,
            )

        reopened = EpisodeAssembler(
            ExperienceLedger(SQLiteDatabase(self.path).connect),
            EpisodeRepository(SQLiteDatabase(self.path).connect),
            self.assembler.policy,
            extractor_version="memory-consolidator/2.0",
        )
        EpisodeProcessingLoop(reopened, self.jobs).process_once(100)

        episodes = self.episodes.list_for_observer("npc_cafe_01")
        self.assertEqual(1, len(episodes))
        episode = episodes[0]
        self.assertEqual(
            ("OPEN", 1, 4, 4, "cafe_conversation_01"),
            (episode.status, episode.start_seq, episode.end_seq, episode.event_count,
             episode.conversation_id),
        )
        replay = self.assembler.rebuild_episode(episode.episode_id)
        self.assertEqual(
            expected,
            tuple((event.event_type, event.natural_language_payload) for event in replay),
        )
        self.assertEqual(
            [("player_01", "U10001")] * 4,
            [(event.persistent_participants[0].entity_id,
              event.persistent_participants[0].persistent_id) for event in replay],
        )
        episode_fields = {field.name for field in fields(episode)}
        self.assertTrue(
            episode_fields.isdisjoint({"events", "dialogue", "observations", "actions", "raw_text"})
        )

    async def test_runtime_v2_semantic_end_is_included_then_closes_episode(self) -> None:
        self.append_dialogue(1, event_type="dialogue_input", text="那我先走了。")
        self.append_dialogue(2, event_type="dialogue_response", text="好，明天见。",
                             episode_directive="END")

        reopened = EpisodeAssembler(
            ExperienceLedger(SQLiteDatabase(self.path).connect),
            EpisodeRepository(SQLiteDatabase(self.path).connect),
            self.assembler.policy,
            extractor_version="memory-consolidator/2.0",
        )
        EpisodeProcessingLoop(reopened, self.jobs).process_once(100)

        episodes = self.episodes.list_for_observer("npc_cafe_01")
        self.assertEqual(1, len(episodes))
        episode = episodes[0]
        self.assertEqual(("CLOSED", "semantic_end", 1, 2, 2),
                         (episode.status, episode.close_reason, episode.start_seq,
                          episode.end_seq, episode.event_count))
        replay = self.assembler.rebuild_episode(episode.episode_id)
        self.assertEqual(["dialogue_input", "dialogue_response"],
                         [event.event_type for event in replay])
        self.assertEqual("END", replay[-1].structured_payload["episode_directive"])
        self.assertEqual("PENDING",
                         self.jobs.get_for_episode(episode.episode_id, "memory-consolidator/2.0").status)

    async def test_runtime_v2_continue_does_not_override_max_events_safeguard(self) -> None:
        assembler = EpisodeAssembler(
            self.ledger,
            self.episodes,
            EpisodeBoundaryPolicy(idle_timeout_seconds=30, max_events=2, max_duration_seconds=300),
            extractor_version="memory-consolidator/2.0",
        )
        for seq, event_type in enumerate((
            "dialogue_input", "dialogue_response", "dialogue_input", "dialogue_response"
        ), start=1):
            self.append_dialogue(
                seq, event_type=event_type, text=f"turn {seq}",
                episode_directive="CONTINUE" if event_type == "dialogue_response" else None,
            )

        EpisodeProcessingLoop(assembler, self.jobs).process_once(100)

        episodes = self.episodes.list_for_observer("npc_cafe_01")
        self.assertEqual(
            [(1, 2, "CLOSED", "max_events"), (3, 4, "OPEN", None)],
            [(episode.start_seq, episode.end_seq, episode.status, episode.close_reason)
             for episode in episodes],
        )

    async def test_runtime_v2_dialogue_episodes_are_isolated_per_observer(self) -> None:
        observers = ("npc_cafe_01", "npc_cafe_regular_01")
        for observer in observers:
            self.append_dialogue(1, observer=observer, event_type="dialogue_input",
                                 text=f"你好，{observer}。")
            self.append_dialogue(2, observer=observer, event_type="dialogue_response",
                                 text=f"{observer} 收到。")

        EpisodeProcessingLoop(self.assembler, self.jobs).process_once(100)

        for observer in observers:
            with self.subTest(observer=observer):
                episodes = self.episodes.list_for_observer(observer)
                self.assertEqual(1, len(episodes))
                self.assertEqual(("OPEN", 1, 2, 2),
                                 (episodes[0].status, episodes[0].start_seq,
                                  episodes[0].end_seq, episodes[0].event_count))
                replay = self.assembler.rebuild_episode(episodes[0].episode_id)
                self.assertEqual([observer, observer], [event.observer_id for event in replay])
                self.assertEqual(
                    [f"你好，{observer}。", f"{observer} 收到。"],
                    [event.natural_language_payload for event in replay],
                )

    async def test_background_poll_eventually_closes_episode_and_only_creates_pending_job(self) -> None:
        self.append(1, terminal=True)
        loop = EpisodeProcessingLoop(self.assembler, self.jobs, interval_seconds=0.01)
        loop.start()
        try:
            for _ in range(100):
                episodes = self.episodes.list_for_observer("npc_01")
                if episodes and episodes[0].status == "CLOSED":
                    break
                await asyncio.sleep(0.01)
        finally:
            await loop.stop()
        episode = self.episodes.list_for_observer("npc_01")[0]
        job = self.jobs.get_for_episode(episode.episode_id, "memory-consolidator/2.0")
        self.assertEqual(("CLOSED", "PENDING", 0),
                         (episode.status, job.status, self.state.get("npc_01", "memory-consolidator/2.0")))

    async def test_episode_failure_does_not_rollback_committed_ledger(self) -> None:
        event_id = self.append(1)
        loop = EpisodeProcessingLoop(self.assembler, self.jobs)
        with patch.object(self.assembler, "process_observer", side_effect=RuntimeError("forced")):
            # Poll failures now reach the worker's FAULTED boundary; previously
            # committed raw evidence must remain intact either way.
            with self.assertRaises(RuntimeError):
                loop.process_once(100)
        self.assertIsNotNone(self.ledger.get_by_event_id(event_id))

    async def test_duplicate_poll_is_idempotent_and_observers_do_not_mix(self) -> None:
        self.append(1, observer="npc_01", terminal=True)
        self.append(1, observer="npc_02", terminal=True)
        loop = EpisodeProcessingLoop(self.assembler, self.jobs)
        loop.process_once(100)
        loop.process_once(101)
        self.assertEqual(1, len(self.episodes.list_for_observer("npc_01")))
        self.assertEqual(1, len(self.episodes.list_for_observer("npc_02")))
        self.assertEqual({"npc_01": 1, "npc_02": 1}, self.jobs.backlog(102).jobs_per_observer)

    async def test_restart_recovers_open_episode_and_retry_job_without_closing_on_restart(self) -> None:
        self.append(1)
        self.assembler.process_observer("npc_01")
        open_id = self.episodes.get_open("npc_01").episode_id
        reopened_database = SQLiteDatabase(self.path)
        reopened_ledger = ExperienceLedger(reopened_database.connect)
        reopened_episodes = EpisodeRepository(reopened_database.connect)
        reopened_jobs = MemoryJobRepository(reopened_database.connect)
        reopened = EpisodeAssembler(
            reopened_ledger, reopened_episodes, self.assembler.policy,
            extractor_version="memory-consolidator/2.0",
        )
        self.assertEqual(open_id, reopened_episodes.get_open("npc_01").episode_id)
        self.append(2, terminal=True)
        reopened.process_observer("npc_01")
        pending = reopened_jobs.backlog(0)
        self.assertEqual(1, pending.pending_count)
        created = reopened_jobs.get_for_episode(
            reopened_episodes.list_for_observer("npc_01")[0].episode_id, "memory-consolidator/2.0"
        ).created_at_utc
        job = reopened_jobs.claim_next(created + 1)
        self.assertIsNotNone(job)
        policy = MemoryJobPolicy(stale_timeout_seconds=10, extractor_version="memory-consolidator/2.0")
        recovered = reopened_jobs.recover_stale(created + 12, policy)
        self.assertEqual("RETRY", recovered[0].status)

    async def test_source_session_boundary_is_recovered_from_persisted_events(self) -> None:
        self.append(1, session="session_a")
        self.append(2, session="session_b")
        reopened = EpisodeAssembler(
            ExperienceLedger(SQLiteDatabase(self.path).connect),
            EpisodeRepository(SQLiteDatabase(self.path).connect),
            self.assembler.policy,
            extractor_version="memory-consolidator/2.0",
        )
        reopened.process_observer("npc_01")
        values = reopened.episodes.list_for_observer("npc_01")
        self.assertEqual(("CLOSED", "source_session_changed", "OPEN"),
                         (values[0].status, values[0].close_reason, values[1].status))


if __name__ == "__main__":
    unittest.main()
