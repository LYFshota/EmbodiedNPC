"""Regression for observation-heavy Cafe Episodes; all databases are temporary."""
from __future__ import annotations

from contextlib import closing
from dataclasses import replace
import json
from pathlib import Path
import tempfile
import time
import unittest

from app.base_memory_consolidator import BaseMemoryConsolidator
from app.canonical_memory_repository import CanonicalMemoryRepository, CanonicalMemoryFinalizationError
from app.context_token_estimator import ContextTokenEstimator
from app.consolidation_evidence_projection import ConsolidationEvidenceProjector
from app.consolidation_model_request import consolidation_request, serialize_consolidation_request
from app.memory_consolidation_context import MemoryConsolidationContextError
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_consolidation_admission import MemoryConsolidationAdmission
from app.memory_consolidation_context import MemoryContextResolver
from app.memory_consolidation_worker import MemoryConsolidationWorker
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from app.sqlite_database import SQLiteDatabase
from tests.test_memory_c2b import FakeProvider


class ConsolidationEvidenceProjectionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.database = SQLiteDatabase(Path(directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.provider = FakeProvider()
        self.consolidator = BaseMemoryConsolidator(self.provider, PrioritizedModelExecutor())
        self.admission = MemoryConsolidationAdmission()
        self.store = CanonicalMemoryRepository(self.database.connect)
        self.serial = 0

    def fixture(self, observations=42, kinds=None, core_text="我喜欢喝卡布奇诺。"):
        kinds = kinds or ("dialogue_input", "dialogue_response") * 3
        episode = None
        for kind in ("observation",) * observations + tuple(kinds):
            self.serial += 1
            observation = kind == "observation"
            dialogue = kind.startswith("dialogue_")
            actor = "player_01" if kind == "dialogue_input" else "npc_01"
            target = "npc_01" if actor == "player_01" else "player_01"
            source = "observation" if observation else "dialogue" if dialogue else (
                "goal_outcome" if kind.startswith("goal_") else "skill_result")
            draft = ExperienceDraft(
                schema_version="experience-draft/1.1", observer_id="npc_01",
                event_type=kind, source_type=source, source_session_id="session_01",
                clock_domain="unity_realtime", source_event_id=f"source_{self.serial}",
                source_observation_ids=(f"obs_{self.serial}",) if observation else (),
                actor_entity_id=actor, target_entity_ids=(target,),
                persistent_participants=(
                    dict(entity_id="npc_01", identity_namespace="npc", identity_type="Character", persistent_id="merchant.001"),
                    dict(entity_id="player_01", identity_namespace="player", identity_type="Player", persistent_id="player.001"),
                ),
                structured_payload={"perception_revision": self.serial, "visible_state": "桌子咖啡杯椅子" * 30} if observation else {},
                natural_language_payload=core_text if dialogue else None,
                occurred_at=self.serial, observed_at=self.serial,
                request_id=f"request_{self.serial}" if dialogue else None,
                conversation_id="conversation_01" if dialogue else None,
                provenance="HEARD" if actor == "player_01" else "EXECUTED_BY_SELF",
            )
            result = self.ingestion.append(ExperienceAppendRequest(
                request_id=f"append_{self.serial}", trace_id=f"trace_{self.serial}", draft=draft))
            event = self.ledger.get_by_event_id(result.event_id)
            episode = (self.episodes.open_for_event(event, "episode-boundary/1.0") if episode is None
                       else self.episodes.append_event(episode.episode_id, event))
        _, job = self.episodes.close_and_enqueue(episode.episode_id, "test_close", time.time(), V2)
        return job

    async def test_observation_heavy_episode_calls_model_and_succeeds(self):
        job = self.fixture()
        # This exact full Episode formerly failed in resolve(), before model execution.
        context = self.resolver.resolve(job)
        self.assertEqual(len(context.evidence_events), 48)
        self.assertGreater(ContextTokenEstimator().estimate_text(context.to_model_visible_json()), 6000)
        worker = MemoryConsolidationWorker(self.jobs, self.resolver, self.consolidator,
                                          self.admission, self.store)
        self.assertTrue(await worker.process_once())
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(len(self.provider.calls), 1)
        request = self.provider.calls[0]
        serialized = json.dumps(request.model_dump(mode="json"), ensure_ascii=False,
                                sort_keys=True, separators=(",", ":"))
        self.assertLessEqual(ContextTokenEstimator().estimate_text(serialized), 6000)

    def projected(self, **kwargs):
        job = self.fixture(**kwargs)
        context = self.resolver.resolve(job)
        return job, context, ConsolidationEvidenceProjector().project(context)

    def preference(self, context, event_id=None):
        event = next(e for e in context.evidence_events if e.event_type == "dialogue_input")
        return dict(episodic_memories=[], semantic_facts=[dict(
            subject_handle=event.participant_handles["player_01"], fact="玩家喜欢喝卡布奇诺。",
            source_excerpt="我喜欢喝卡布奇诺。", evidence_event_ids=[event_id or event.event_id], confidence=.9)])

    def test_all_core_types_preserved(self):
        kinds = ("dialogue_input", "dialogue_response") + tuple(
            f"{prefix}_{suffix}" for prefix in ("goal", "skill")
            for suffix in ("succeeded", "failed", "cancelled", "timed_out"))
        _, context, projection = self.projected(kinds=kinds)
        core = [e for e in projection.visible_events if e.event_type != "observation"]
        self.assertEqual([e.event_type for e in core], list(kinds))
        self.assertEqual(core[0].natural_language_payload, "我喜欢喝卡布奇诺。")
        self.assertEqual(len(context.evidence_events), 52)

    def test_observations_bounded_full_range_unchanged(self):
        _, context, projection = self.projected()
        self.assertEqual([e.experience_seq for e in context.evidence_events], list(range(1, 49)))
        count = sum(e.event_type == "observation" for e in projection.visible_events)
        self.assertGreater(count, 0)
        self.assertLess(count, 42)
        self.assertTrue(set(projection.visible_event_ids).issubset(e.event_id for e in context.evidence_events))
        request = consolidation_request(context, projection.visible_events)
        self.assertEqual(projection.budget_allocation.estimated_total,
                         ContextTokenEstimator().estimate_text(serialize_consolidation_request(request)))

    def test_hidden_evidence_rejected(self):
        _, context, projection = self.projected()
        hidden = next(e for e in context.evidence_events if e.event_id not in projection.visible_event_ids)
        batch = self.admission.evaluate(context, self.preference(context, hidden.event_id),
                                        visible_event_ids=projection.visible_event_ids)
        self.assertEqual(batch.memories, ())
        self.assertEqual([r.reason for r in batch.rejections], ["EVIDENCE_NOT_MODEL_VISIBLE"])

    def test_visible_direct_self_report_admitted(self):
        _, context, projection = self.projected()
        batch = self.admission.evaluate(context, self.preference(context),
                                        visible_event_ids=projection.visible_event_ids)
        self.assertEqual(len(batch.memories), 1)
        self.assertEqual(batch.memories[0].memory_type, "SEMANTIC_FACT")
        self.assertEqual(batch.memories[0].summary, "玩家喜欢喝卡布奇诺。")
        self.assertEqual(batch.rejections, ())

    def test_projection_deterministic(self):
        _, context, first = self.projected()
        second = ConsolidationEvidenceProjector().project(context)
        self.assertEqual(first, second)
        self.assertEqual([e.experience_seq for e in first.visible_events],
                         sorted(e.experience_seq for e in first.visible_events))

    async def test_worker_and_transaction_reprojection_agree(self):
        job, context, projection = self.projected()
        self.provider.output = json.dumps(self.preference(context))
        worker = MemoryConsolidationWorker(self.jobs, self.resolver, self.consolidator,
                                          self.admission, self.store)
        await worker.process_once()
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        payload = json.loads(self.provider.calls[0].messages[1].content.split("\n", 1)[1])
        self.assertEqual(tuple(e["event_id"] for e in payload["evidence_events"]), projection.visible_event_ids)
        with closing(self.database.connect()) as connection:
            row = connection.execute("SELECT summary,source_excerpt FROM memory_semantic_facts JOIN memory_records USING(memory_id)").fetchone()
            self.assertEqual(tuple(row), ("玩家喜欢喝卡布奇诺。", "我喜欢喝卡布奇诺。"))

    def test_forged_visible_ids_fail_finalization_without_writes(self):
        job, context, projection = self.projected()
        running = self.jobs.claim_next_for_version(time.time() + 1, V2)
        batch = self.admission.evaluate(context, self.preference(context))
        forged = replace(batch, visible_event_ids=projection.visible_event_ids[:-1])
        with self.assertRaisesRegex(CanonicalMemoryFinalizationError, "ADMISSION_INTEGRITY_MISMATCH"):
            self.store.finalize_success(job_id=job.job_id, attempt_generation=running.attempt_generation,
                                       expected_input_hash=job.input_hash, batch=forged, now=time.time() + 2)
        with closing(self.database.connect()) as connection:
            self.assertEqual(connection.execute("SELECT count(*) FROM memory_records").fetchone()[0], 0)

    def test_relationship_builder_keeps_full_dialogue(self):
        _, context, _ = self.projected()
        candidates = RelationshipReflectionCandidateBuilder().build(context)
        self.assertEqual(len(candidates), 1)
        self.assertEqual(candidates[0].evidence_event_ids, tuple(
            e.event_id for e in context.evidence_events if e.source_type == "dialogue"))
        self.assertEqual(len(context.evidence_events), 48)

    def test_core_overflow_safe_failure_without_model_call(self):
        job = self.fixture(observations=0, core_text="事实" * 500)
        context = self.resolver.resolve(job)
        self.assertEqual(len(context.evidence_events), 6)
        with self.assertRaisesRegex(MemoryConsolidationContextError, "CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED"):
            ConsolidationEvidenceProjector().project(context)
        self.assertEqual(self.provider.calls, [])

    def test_no_observations_and_stable_descriptor_handles(self):
        _, context, projection = self.projected(observations=0)
        self.assertEqual(projection.visible_events, context.evidence_events)
        event = context.evidence_events[0]
        # Presentation for a single participant preserves its original handle.
        player_handle = event.participant_handles["player_01"]
        only_player = event.model_copy(update={"participant_handles": {"player_01": player_handle}})
        payload = context.model_visible_payload((only_player,))
        self.assertEqual([d["handle"] for d in payload["participant_descriptors"]], [player_handle])
        self.assertEqual(payload["provenance"], [event.provenance])
        self.assertEqual(len(context.identity_by_handle), 2)
