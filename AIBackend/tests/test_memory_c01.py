from __future__ import annotations

import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.context_budget_allocator import ContextBudgetPolicy
from app.consolidation_evidence_projection import ConsolidationEvidenceProjector
from app.episode_assembler import EpisodeAssembler
from app.episode_models import EpisodeBoundaryPolicy
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_consolidation_context import (
    MemoryConsolidationContextError,
    MemoryContextResolver,
)
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import (
    LEGACY_MEMORY_EXTRACTOR_VERSION,
    SEMANTIC_MEMORY_CONSOLIDATOR_VERSION,
)
from app.sqlite_database import SQLiteDatabase


class MemoryC01Tests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.database = SQLiteDatabase(Path(self.directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)

    def tearDown(self) -> None:
        self.directory.cleanup()

    def append_dialogue(
        self,
        sequence: int,
        *,
        actor: str,
        target: str,
        player_uid: str,
        player_entity_id: str,
        end: bool = False,
    ) -> None:
        payload = {"direction": "npc_to_player" if actor == "npc_cafe_01" else "player_to_npc"}
        if end:
            payload["episode_directive"] = "END"
        participants = (
            {
                "entity_id": player_entity_id,
                "identity_namespace": "player",
                "identity_type": "Player",
                "persistent_id": player_uid,
            },
            {
                "entity_id": "npc_cafe_01",
                "identity_namespace": "npc",
                "identity_type": "Character",
                "persistent_id": "cafe.merchant.001",
            },
        )
        draft = ExperienceDraft(
            schema_version="experience-draft/1.1",
            observer_id="npc_cafe_01",
            event_type="dialogue_response" if actor == "npc_cafe_01" else "dialogue_input",
            source_type="dialogue",
            source_session_id="session_01",
            clock_domain="unity_realtime",
            source_event_id=f"dialogue_{sequence}",
            actor_entity_id=actor,
            target_entity_ids=(target,),
            persistent_participants=participants,
            structured_payload=payload,
            natural_language_payload="Alice orders coffee",
            occurred_at=float(sequence),
            observed_at=float(sequence),
            request_id=f"request_{sequence}",
            trace_id=f"trace_{sequence}",
            conversation_id="conversation_01",
            provenance="EXECUTED_BY_SELF" if actor == "npc_cafe_01" else "SYSTEM_CONFIRMED",
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"append_{sequence}", trace_id=f"append_trace_{sequence}", draft=draft
        ))
        self.assertEqual("inserted", result.status)

    def close_dialogue_episode(self, version: str, *, second_uid: str = "U10001"):
        self.append_dialogue(
            1, actor="player_instance_a", target="npc_cafe_01",
            player_uid="U10001", player_entity_id="player_instance_a",
        )
        self.append_dialogue(
            2, actor="npc_cafe_01", target="player_instance_b",
            player_uid=second_uid, player_entity_id="player_instance_b", end=True,
        )
        assembler = EpisodeAssembler(
            self.ledger, self.episodes, EpisodeBoundaryPolicy(), extractor_version=version
        )
        result = assembler.process_observer("npc_cafe_01")
        self.assertEqual(1, result.closed)
        episode = self.episodes.list_for_observer("npc_cafe_01")[0]
        return episode, self.jobs.get_for_episode(episode.episode_id, version)

    def test_default_close_creates_only_canonical_v2_job(self) -> None:
        _, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
        self.assertEqual(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, job.extractor_version)
        with closing(self.database.connect()) as connection:
            versions = [row[0] for row in connection.execute(
                "SELECT extractor_version FROM memory_jobs"
            )]
        self.assertEqual([SEMANTIC_MEMORY_CONSOLIDATOR_VERSION], versions)

    def test_v2_close_creates_only_canonical_v2_job(self) -> None:
        _, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
        self.assertEqual("PENDING", job.status)
        self.assertEqual(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, job.extractor_version)
        self.assertIsNone(self.jobs.claim_next(
            job.created_at_utc + 1, (SEMANTIC_MEMORY_CONSOLIDATOR_VERSION,)
        ))
        self.assertEqual("PENDING", self.jobs.get(job.job_id).status)

    def test_legacy_durable_job_remains_inert_without_migration(self) -> None:
        _, legacy = self.close_dialogue_episode(LEGACY_MEMORY_EXTRACTOR_VERSION)
        self.episodes.enqueue_for_closed_episode(
            legacy.episode_id, SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, legacy.created_at_utc + 1
        )
        canonical = self.jobs.claim_next_for_version(
            legacy.created_at_utc + 2, SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
        )
        self.assertEqual(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, canonical.extractor_version)
        self.assertEqual("PENDING", self.jobs.get(legacy.job_id).status)
        self.assertIsNotNone(self.jobs.get_for_episode(
            legacy.episode_id, SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
        ))
        self.assertEqual("RUNNING", self.jobs.get(canonical.job_id).status)
        self.assertEqual("PENDING", self.jobs.get(legacy.job_id).status)

    def test_context_rebuilds_exact_range_and_collapses_same_identity_to_one_handle(self) -> None:
        episode, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
        context = MemoryContextResolver(self.ledger, self.episodes).resolve(job)
        self.assertEqual((episode.start_seq, episode.end_seq), (context.start_seq, context.end_seq))
        self.assertEqual([1, 2], [value.experience_seq for value in context.evidence_events])
        player_handle = next(
            handle for handle, identity in context.identity_by_handle.items()
            if identity.persistent_id == "U10001"
        )
        player = next(value for value in context.participant_descriptors if value.handle == player_handle)
        self.assertEqual(("player_instance_a", "player_instance_b"), player.historical_entity_ids)
        self.assertEqual("cafe.merchant.001", context.observer_persistent_identity.persistent_id)
        self.assertEqual(player_handle, context.evidence_events[0].participant_handles["player_instance_a"])
        self.assertEqual(player_handle, context.evidence_events[1].participant_handles["player_instance_b"])

    def test_same_display_evidence_with_different_uids_does_not_merge(self) -> None:
        _, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, second_uid="U20002")
        context = MemoryContextResolver(self.ledger, self.episodes).resolve(job)
        player_handles = {
            handle for handle, identity in context.identity_by_handle.items()
            if identity.identity_namespace == "player"
        }
        self.assertEqual(2, len(player_handles))
        self.assertNotEqual(
            context.evidence_events[0].participant_handles["player_instance_a"],
            context.evidence_events[1].participant_handles["player_instance_b"],
        )

    def test_model_visible_serialization_hides_persistent_identity(self) -> None:
        _, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
        context = MemoryContextResolver(self.ledger, self.episodes).resolve(job)
        serialized = context.to_model_visible_json()
        self.assertNotIn("U10001", serialized)
        self.assertNotIn("cafe.merchant.001", serialized)
        self.assertNotIn("persistent_id", serialized)
        self.assertNotIn("player_uid", serialized)
        self.assertIn("participant_1", serialized)

    def test_core_projection_budget_fails_without_truncating_authority(self) -> None:
        _, job = self.close_dialogue_episode(SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
        policy = ContextBudgetPolicy(
            name="tiny-consolidation", max_context_tokens=64, input_hard_cap=20,
            response_reserve=20, estimation_reserve=24,
            recent_target_ceiling=0, summary_ceiling=0, evidence_ceiling=0,
            recent_hard_minimum_turns=0,
        )
        context = MemoryContextResolver(self.ledger, self.episodes).resolve(job)
        self.assertEqual(len(context.evidence_events), 2)
        with self.assertRaisesRegex(
            MemoryConsolidationContextError, "CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED"
        ):
            ConsolidationEvidenceProjector(policy).project(context)


if __name__ == "__main__":
    unittest.main()
