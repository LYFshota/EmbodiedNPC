from __future__ import annotations

import asyncio
from contextlib import closing
from dataclasses import replace
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import textwrap
import time
import unittest
from unittest.mock import AsyncMock, patch

from app.base_memory_consolidator import BaseMemoryConsolidator
from app.canonical_memory_repository import CanonicalMemoryRepository
from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.consolidation_state import ConsolidationStateRepository
from app.context_budget_allocator import ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.episode_models import MemoryJobPolicy
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.local_model_scheduler import PreemptedByForeground, PrioritizedModelExecutor
from app.memory_consolidation_admission import MemoryConsolidationAdmission
from app.memory_consolidation_context import MemoryContextResolver
from app.memory_consolidation_worker import MemoryConsolidationWorker
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import LEGACY_MEMORY_EXTRACTOR_VERSION as LEGACY, SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.sqlite_database import SQLiteDatabase
from app.providers import ModelMessage, ModelRequest, ModelResult, ProviderError
from tests.test_cognitive_request import _event_request


EMPTY = '{"episodic_memories": [], "semantic_facts": []}'


class FakeProvider:
    name = "fake"
    model = "c2b-test"
    context_length = 8192

    def __init__(self):
        self.calls = []
        self.output = EMPTY
        self.error = None
        self.entered = asyncio.Event()
        self.release = None

    async def generate(self, request):
        self.calls.append(request)
        self.entered.set()
        if self.release is not None:
            await self.release.wait()
        if self.error is not None:
            raise self.error
        return ModelResult(text=self.output)


class MemoryC2BTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.database = SQLiteDatabase(Path(self.directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.store = CanonicalMemoryRepository(self.database.connect)
        self.state = ConsolidationStateRepository(self.database.connect)
        self.provider = FakeProvider()
        self.executor = PrioritizedModelExecutor()
        self.consolidator = BaseMemoryConsolidator(self.provider, self.executor)
        self.now = time.time() + 60
        self.policy = MemoryJobPolicy(extractor_version=V2, max_retries=2,
                                     initial_backoff_seconds=1, stale_timeout_seconds=10)
        self.worker = MemoryConsolidationWorker(
            self.jobs, self.resolver, self.consolidator, MemoryConsolidationAdmission(), self.store,
            job_policy=self.policy, clock=lambda: self.now, interval_seconds=.01,
        )
        self.counter = 0

    def pending(self, *, version=V2, text="My favorite color is blue.", close=True):
        self.counter += 1
        draft = ExperienceDraft(
            schema_version="experience-draft/1.1", observer_id="npc_01", event_type="dialogue_input",
            source_type="dialogue", source_session_id="session_01", clock_domain="unity_realtime",
            source_event_id=f"source_{self.counter}", actor_entity_id="player_01", target_entity_ids=("npc_01",),
            persistent_participants=[
                dict(entity_id="player_01", identity_namespace="player", identity_type="Player", persistent_id="SECRET_UID"),
                dict(entity_id="npc_01", identity_namespace="npc", identity_type="Character", persistent_id="SECRET_OWNER"),
            ], natural_language_payload=text, occurred_at=self.counter, observed_at=self.counter,
            request_id=f"request_{self.counter}", conversation_id=f"conversation_{self.counter}",
            provenance="SYSTEM_CONFIRMED",
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"append_{self.counter}", trace_id=f"trace_{self.counter}", draft=draft,
        ))
        event = self.ledger.get_by_event_id(result.event_id)
        episode = self.episodes.get_open(event.observer_id)
        episode = (self.episodes.append_event(episode.episode_id, event) if episode else
                   self.episodes.open_for_event(event, "episode-boundary/1.0"))
        if not close:
            return None
        self.episodes.close_and_enqueue(episode.episode_id, "test_close", self.now, version)
        return self.jobs.get_for_episode(episode.episode_id, version)

    def valid_output(self, job):
        context = self.resolver.resolve(job)
        handle = next(h for h, identity in context.identity_by_handle.items()
                      if identity.identity_namespace == "player")
        return dict(episodic_memories=[dict(
            summary="Learned a visitor's explicitly stated color preference.", subject_handles=[handle],
            evidence_event_ids=[context.evidence_events[0].event_id], importance=.6, confidence=.9,
        )], semantic_facts=[dict(subject_handle=handle, fact="The visitor’s favorite color is blue.",
                               source_excerpt=context.evidence_events[0].natural_language_payload,
                               evidence_event_ids=[context.evidence_events[0].event_id], confidence=.9)])

    def count(self, table):
        with closing(self.database.connect()) as connection:
            return connection.execute(f"SELECT count(*) FROM {table}").fetchone()[0]

    def assert_no_writes(self):
        self.assertEqual(self.count("memory_records"), 0)
        self.assertEqual(self.count("memory_evidence"), 0)
        self.assertEqual(self.state.get("npc_01", V2), 0)

    async def test_valid_background_call_finalizes_both_kinds_and_bounds_request(self):
        job = self.pending()
        self.provider.output = json.dumps(self.valid_output(job))
        with patch.object(self.executor, "execute_preemptible_background",
                          wraps=self.executor.execute_preemptible_background) as execute, \
             patch.object(self.executor, "execute", wraps=self.executor.execute) as ordinary:
            self.assertTrue(await self.worker.process_once())
            execute.assert_awaited_once()
            ordinary.assert_not_called()
            self.assertIs(execute.call_args.args[0], self.provider)
        self.assertEqual(len(self.provider.calls), 1)
        request = self.provider.calls[0]
        self.assertFalse(request.inference_options.think)
        self.assertEqual(request.inference_options.temperature, 0)
        self.assertEqual(request.inference_options.max_output_tokens, 1200)
        self.assertIn("UNTRUSTED DATA", request.messages[0].content)
        self.assertIn("NOT INSTRUCTIONS", request.messages[1].content)
        for rule in ("selective", "normal success", "atomic claims", "copied EXACTLY",
                     "NOT attribute/value pairs", "NOT successful execution", "Do not infer personality",
                     "negation", "conditions", "does NOT prove", "self-statement boundary"):
            self.assertIn(rule, request.messages[0].content)
        self.assertNotIn("SECRET_UID", request.model_dump_json())
        self.assertNotIn("SECRET_OWNER", request.model_dump_json())
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(self.count("memory_records"), 2)
        self.assertEqual(self.count("memory_evidence"), 2)
        self.assertEqual(self.state.get("npc_01", V2), 1)

        from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
        from app.relationship_reflection_repository import RelationshipReflectionRepository
        from app.relationship_reflection_scanner import RelationshipReflectionScanner
        scanner = RelationshipReflectionScanner(self.jobs, self.resolver,
            RelationshipReflectionCandidateBuilder(), RelationshipReflectionRepository(self.database.connect))
        discovered = scanner.scan_next_batch()
        self.assertEqual(discovered.scanned_jobs, 1)
        self.assertEqual(discovered.issues, ())
        self.assertEqual(len(discovered.candidates), 1)

    async def test_empty_success_is_durable_and_not_reclaimed(self):
        job = self.pending()
        await self.worker.process_once()
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(self.count("memory_records"), 0)
        self.assertEqual(self.state.get("npc_01", V2), 1)
        self.assertFalse(await self.worker.process_once())
        self.assertEqual(len(self.provider.calls), 1)

    async def test_final_request_budget_exceeds_after_c1_fits(self):
        for _ in range(3):
            self.pending(text="事实" * 500, close=False)
        job = self.pending(text="事实" * 500)
        context = self.resolver.resolve(job)
        self.assertLess(ContextTokenEstimator().estimate_text(context.to_model_visible_json()),
                        ContextBudgetPolicy.consolidation().input_hard_cap)
        await self.worker.process_once()
        failed = self.jobs.get(job.job_id)
        self.assertEqual(failed.status, "DEAD_LETTER")
        self.assertEqual(failed.error_code, "CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED")
        self.assertEqual(self.provider.calls, [])
        self.assert_no_writes()

    async def test_malformed_and_schema_invalid_outputs_retry_without_repair(self):
        for output, code in [("not json", "CONSOLIDATION_OUTPUT_MALFORMED"),
                             ('{"episodic_memories": [], "semantic_facts": [], "status": "ok"}',
                              "CONSOLIDATION_SCHEMA_INVALID")]:
            with self.subTest(code=code):
                job = self.pending()
                self.provider.output = output
                await self.worker.process_once()
                failed = self.jobs.get(job.job_id)
                self.assertEqual((failed.status, failed.retry_count, failed.error_code), ("RETRY", 1, code))
                self.assert_no_writes()
        self.assertEqual(len(self.provider.calls), 2)

    async def test_provider_error_retries_then_exhausts_without_raw_error_logging(self):
        job = self.pending()
        self.provider.error = ProviderError("OLLAMA_TIMEOUT")
        await self.worker.process_once()
        failed = self.jobs.get(job.job_id)
        self.assertEqual((failed.status, failed.error_code), ("RETRY", "PROVIDER_TIMEOUT"))
        self.now = failed.next_retry_at_utc
        self.provider.error = ProviderError("secret raw provider payload SECRET_UID")
        with self.assertLogs("embodiednpc.backend.memory_consolidation") as logs:
            await self.worker.process_once()
        self.assertNotIn("SECRET_UID", str(logs.output))
        self.assertEqual(self.jobs.get(job.job_id).status, "DEAD_LETTER")
        self.assertEqual(self.jobs.get(job.job_id).error_code, "PROVIDER_PROTOCOL_ERROR")
        self.assertEqual(len(self.provider.calls), 2)
        self.assert_no_writes()

    async def test_mixed_response_finalizes_valid_candidate_without_retry(self):
        job = self.pending()
        output = self.valid_output(job)
        output["semantic_facts"][0]["evidence_event_ids"] = ["invented_event"]
        self.provider.output = json.dumps(output)
        await self.worker.process_once()
        finished = self.jobs.get(job.job_id)
        self.assertEqual((finished.status, finished.error_code, finished.retry_count), ("SUCCEEDED", None, 0))
        self.assertEqual(len(self.provider.calls), 1)
        self.assertEqual(self.count("memory_records"), 1)
        self.assertEqual(self.count("memory_semantic_facts"), 0)
        self.assertEqual(self.state.get("npc_01", V2), 1)

    async def test_all_rejected_worker_succeeds_without_retry_or_memory(self):
        job = self.pending()
        output = self.valid_output(job)
        output["episodic_memories"] = []
        output["semantic_facts"][0]["subject_handle"] = "participant_99"
        self.provider.output = json.dumps(output)
        await self.worker.process_once()
        finished = self.jobs.get(job.job_id)
        self.assertEqual((finished.status, finished.retry_count), ("SUCCEEDED", 0))
        self.assertEqual(self.count("memory_records"), 0)
        self.assertEqual(self.state.get("npc_01", V2), 1)
        self.assertFalse(await self.worker.process_once())
        self.assertEqual(len(self.provider.calls), 1)

    async def test_batch_admission_integrity_is_permanent_not_retry(self):
        job = self.pending()
        context = replace(self.resolver.resolve(job), evidence_events=())
        with patch.object(self.resolver, "resolve", return_value=context):
            await self.worker.process_once()
        failed = self.jobs.get(job.job_id)
        self.assertEqual((failed.status, failed.error_code, failed.retry_count),
                         ("DEAD_LETTER", "CONSOLIDATION_ADMISSION_INTEGRITY_FAILED", 0))
        self.assert_no_writes()

    async def test_context_hash_mismatch_is_permanent_without_model_call(self):
        job = self.pending()
        with closing(self.database.connect()) as connection, connection:
            connection.execute("UPDATE memory_jobs SET input_hash=? WHERE job_id=?", ("corrupt", job.job_id))
        await self.worker.process_once()
        failed = self.jobs.get(job.job_id)
        self.assertEqual((failed.status, failed.error_code), ("DEAD_LETTER", "JOB_INPUT_HASH_MISMATCH"))
        self.assertEqual(self.provider.calls, [])
        self.assert_no_writes()

    async def test_stale_attempt_never_marks_new_owner_failed(self):
        for error in (None, ProviderError("OLLAMA_TIMEOUT")):
            with self.subTest(error=error):
                job = self.pending()
                self.provider.error = error
                self.provider.entered.clear()
                self.provider.release = asyncio.Event()
                task = asyncio.create_task(self.worker.process_once())
                await asyncio.wait_for(self.provider.entered.wait(), 2)
                old = self.jobs.get(job.job_id)
                self.jobs.mark_failed(job.job_id, old.attempt_generation, self.now, "STALE_RUNNING", self.policy)
                self.now += 1
                new = self.jobs.claim_next_for_version(self.now, V2)
                with patch.object(self.jobs, "mark_failed", wraps=self.jobs.mark_failed) as fail, \
                     patch.object(self.jobs, "mark_permanent_failure", wraps=self.jobs.mark_permanent_failure) as permanent:
                    self.provider.release.set()
                    await task
                    fail.assert_not_called()
                    permanent.assert_not_called()
                self.assertEqual(self.jobs.get(job.job_id), new)
                self.assertEqual(new.attempt_generation, 2)
                self.assert_no_writes()

    async def test_exact_version_claim_and_legacy_exclusion_keep_ownership(self):
        legacy = self.pending(version=LEGACY)
        future = self.pending(version="memory-consolidator/4.0")
        v2 = self.pending()
        claimed = self.jobs.claim_next_for_version(self.now, V2)
        self.assertEqual((claimed.job_id, claimed.attempt_generation, claimed.status), (v2.job_id, 1, "RUNNING"))
        self.assertIsNone(self.jobs.claim_next_for_version(self.now, V2))
        self.assertEqual(self.jobs.get(legacy.job_id).status, "PENDING")
        self.assertEqual(self.jobs.get(future.job_id).status, "PENDING")
        self.jobs.mark_failed(v2.job_id, 1, self.now, "TEST", self.policy)
        self.assertIsNone(self.jobs.claim_next_for_version(self.now, V2))
        self.assertEqual(self.jobs.claim_next(self.now, (V2, "memory-consolidator/4.0")).job_id, legacy.job_id)
        self.assertEqual(self.jobs.claim_next(self.now).job_id, future.job_id)
        retried = self.jobs.claim_next_for_version(self.now + 1, V2)
        self.assertEqual((retried.job_id, retried.attempt_generation), (v2.job_id, 2))

    async def test_retry_then_success_has_one_durable_result(self):
        job = self.pending()
        self.provider.output = "broken"
        await self.worker.process_once()
        self.now = self.jobs.get(job.job_id).next_retry_at_utc
        self.provider.output = json.dumps(self.valid_output(job))
        await self.worker.process_once()
        self.assertFalse(await self.worker.process_once())
        done = self.jobs.get(job.job_id)
        self.assertEqual((done.status, done.attempt_generation, done.retry_count), ("SUCCEEDED", 2, 1))
        self.assertEqual(len(self.provider.calls), 2)
        self.assertEqual(self.count("memory_records"), 2)
        self.assertEqual(self.count("memory_evidence"), 2)
        self.assertEqual(self.state.get("npc_01", V2), 1)

    async def test_crash_after_inference_recovers_using_existing_stale_policy(self):
        job = self.pending()
        running = self.jobs.claim_next_for_version(self.now, V2)
        self.provider.output = json.dumps(self.valid_output(job))
        await self.consolidator.consolidate(self.resolver.resolve(running))
        # Simulate process loss before Admission/finalization, then a fresh worker.
        self.assert_no_writes()
        self.now += 11
        self.assertFalse(await self.worker.process_once())
        recovered = self.jobs.get(job.job_id)
        self.assertEqual((recovered.status, recovered.error_code), ("RETRY", "STALE_RUNNING"))
        self.now = recovered.next_retry_at_utc
        await self.worker.process_once()
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(self.count("memory_records"), 2)
        self.assertEqual(self.count("memory_evidence"), 2)

    async def test_poll_survives_claim_error_and_stop_drains_active_attempt(self):
        job = self.pending()
        self.provider.release = asyncio.Event()
        original = self.jobs.claim_next_for_version
        attempts = []

        def claim(*args, **kwargs):
            attempts.append(args)
            if len(attempts) == 1:
                raise sqlite3.OperationalError("SECRET_UID simulated lock")
            return original(*args, **kwargs)

        with patch.object(self.jobs, "claim_next_for_version", side_effect=claim), \
             self.assertLogs("embodiednpc.backend.memory_consolidation", level="WARNING") as logs:
            self.worker.start()
            task = self.worker._task
            self.worker.start()
            self.assertIs(task, self.worker._task)
            try:
                await asyncio.wait_for(self.provider.entered.wait(), 2)
                self.assertEqual(len(attempts), 2)
                stopping = asyncio.create_task(self.worker.stop())
                await asyncio.sleep(.01)
                self.assertFalse(stopping.done())
            finally:
                self.provider.release.set()
                await self.worker.stop()
            await stopping
        self.assertNotIn("SECRET_UID", str(logs.output))
        self.assertTrue(task.done())
        self.assertIsNone(self.worker._task)
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")

    async def test_foreground_preempts_durable_attempt_then_retry_finalizes_once(self):
        # This shared provider must admit a full foreground cognition before
        # this test can exercise worker preemption; other 8K worker cases stay unchanged.
        self.provider.context_length = 16384
        job = self.pending()
        self.provider.output = json.dumps(self.valid_output(job))
        self.provider.release = asyncio.Event()
        foreground_entered, foreground_release, retry_resolved = (asyncio.Event() for _ in range(3))
        order = []
        generate = self.provider.generate

        async def routed(request):
            if request.response_schema.get("title") == "CognitiveStepResult":
                order.append("foreground")
                foreground_entered.set()
                await foreground_release.wait()
                return ModelResult(text='{"decision_kind":"NO_ACTION"}')
            order.append("background")
            return await generate(request)

        resolve = self.resolver.resolve
        loop = asyncio.get_running_loop()

        def resolved(job):
            result = resolve(job)
            loop.call_soon_threadsafe(retry_resolved.set)
            return result

        with patch.object(self.provider, "generate", side_effect=routed), \
             patch.object(self.jobs, "mark_failed", wraps=self.jobs.mark_failed) as failed, \
             patch.object(self.jobs, "mark_permanent_failure", wraps=self.jobs.mark_permanent_failure) as permanent:
            background = asyncio.create_task(self.worker.process_once())
            await asyncio.wait_for(self.provider.entered.wait(), 2)
            foreground = asyncio.create_task(CognitiveBackendEntry(self.provider, self.executor, context_length=16384).run(
                CognitiveRequest.model_validate(_event_request())))
            await asyncio.wait_for(foreground_entered.wait(), 2)
            await asyncio.wait_for(background, 2)
            requeued = self.jobs.get(job.job_id)
            self.assertEqual((requeued.status, requeued.retry_count, requeued.attempt_generation,
                              requeued.error_code, requeued.next_retry_at_utc, requeued.finished_at_utc),
                             ("RETRY", 0, 1, "PREEMPTED_BY_FOREGROUND", self.now, None))
            self.assert_no_writes()
            self.assertEqual(self.count("memory_semantic_facts"), 0)
            with patch.object(self.resolver, "resolve", side_effect=resolved):
                retry = asyncio.create_task(self.worker.process_once())
                await asyncio.wait_for(retry_resolved.wait(), 2)
                self.assertEqual(order, ["background", "foreground"])
                foreground_release.set()
                self.provider.release.set()
                await asyncio.wait_for(asyncio.gather(foreground, retry), 2)
            failed.assert_not_called()
            permanent.assert_not_called()
        self.assertEqual(order, ["background", "foreground", "background"])
        done = self.jobs.get(job.job_id)
        self.assertEqual((done.status, done.retry_count, done.attempt_generation), ("SUCCEEDED", 0, 2))
        self.assertEqual((self.count("memory_records"), self.count("memory_evidence")), (2, 2))
        self.assertEqual(self.state.get("npc_01", V2), 1)
        self.assertFalse(await self.worker.process_once())

    async def test_repeated_preemption_preserves_existing_failure_budget(self):
        job = self.pending()
        running = self.jobs.claim_next_for_version(self.now, V2)
        failed = self.jobs.mark_failed(job.job_id, running.attempt_generation, self.now, "PROVIDER_TIMEOUT", self.policy)
        self.now = failed.next_retry_at_utc
        with patch.object(self.executor, "execute_preemptible_background", new_callable=AsyncMock,
                          side_effect=PreemptedByForeground("PREEMPTED_BY_FOREGROUND")):
            for generation in range(2, 7):
                await self.worker.process_once()
                requeued = self.jobs.get(job.job_id)
                self.assertEqual((requeued.status, requeued.retry_count, requeued.attempt_generation),
                                 ("RETRY", 1, generation))
                self.assert_no_writes()

    async def test_stale_requeue_cannot_overwrite_reclaimed_or_completed_job(self):
        job = self.pending()
        running = self.jobs.claim_next_for_version(self.now, V2)
        self.jobs.requeue_preempted(job.job_id, running.attempt_generation, self.now)
        current = self.jobs.claim_next_for_version(self.now, V2)
        self.assertIsNone(self.jobs.requeue_preempted(job.job_id, running.attempt_generation, self.now))
        self.assertEqual(self.jobs.get(job.job_id), current)
        context = self.resolver.resolve(current)
        self.store.finalize_success(job_id=job.job_id, attempt_generation=current.attempt_generation,
            expected_input_hash=current.input_hash, batch=MemoryConsolidationAdmission().evaluate(context, json.loads(EMPTY)),
            now=self.now)
        self.assertIsNone(self.jobs.requeue_preempted(job.job_id, current.attempt_generation, self.now))
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")

    async def test_worker_stale_preemption_is_noop_without_failure_mutation(self):
        job = self.pending()
        new_owner = None

        async def lose_ownership(*args):
            nonlocal new_owner
            current = self.jobs.get(job.job_id)
            self.jobs.requeue_preempted(job.job_id, current.attempt_generation, self.now)
            new_owner = self.jobs.claim_next_for_version(self.now, V2)
            raise PreemptedByForeground("PREEMPTED_BY_FOREGROUND")

        with patch.object(self.executor, "execute_preemptible_background", side_effect=lose_ownership), \
             patch.object(self.worker, "_fail", new_callable=AsyncMock) as fail:
            await self.worker.process_once()
            fail.assert_not_called()
        self.assertEqual(self.jobs.get(job.job_id), new_owner)
        self.assert_no_writes()

    async def test_runtime_cancellation_is_not_preemption(self):
        job = self.pending()
        self.provider.release = asyncio.Event()
        with patch.object(self.jobs, "requeue_preempted", wraps=self.jobs.requeue_preempted) as requeue:
            task = asyncio.create_task(self.worker.process_once())
            await asyncio.wait_for(self.provider.entered.wait(), 2)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            requeue.assert_not_called()
        self.assertEqual(self.jobs.get(job.job_id).status, "RETRY")
        self.assertEqual(self.jobs.get(job.job_id).error_code, "ATTEMPT_CANCELLED")
        self.assert_no_writes()

    async def test_foreground_during_finalization_does_not_cancel_durable_write(self):
        job = self.pending()
        self.provider.output = json.dumps(self.valid_output(job))
        entered, release = threading.Event(), threading.Event()
        finalize = self.store.finalize_success

        def blocked_finalize(**kwargs):
            entered.set()
            if not release.wait(3):
                raise TimeoutError("test finalization barrier")
            return finalize(**kwargs)

        with patch.object(self.store, "finalize_success", side_effect=blocked_finalize), \
             patch.object(self.jobs, "requeue_preempted", wraps=self.jobs.requeue_preempted) as requeue:
            task = asyncio.create_task(self.worker.process_once())
            try:
                self.assertTrue(await asyncio.to_thread(entered.wait, 2))
                await self.executor.execute(self.provider, ModelRequest(messages=(
                    ModelMessage(role="user", content="foreground"),)), priority="foreground")
                self.assertFalse(task.done())
            finally:
                release.set()
                await task
            requeue.assert_not_called()
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(self.count("memory_records"), 2)

    async def test_m3_lifespan_pipeline_gates_and_shared_instances(self):
        # A subprocess isolates m3_main's module-level DB/env from other test modules.
        script = textwrap.dedent('''
            import asyncio
            from types import SimpleNamespace
            from unittest.mock import AsyncMock, Mock, patch
            from app import m3_main as app
            assert app.base_memory_consolidator.provider is app.provider
            assert app.base_memory_consolidator.executor is app.model_executor
            assert app.memory_consolidation_worker.job_policy.extractor_version == "memory-consolidator/3.0"
            assert app.memory_consolidation_worker.job_policy.max_retries == 3
            assert app.memory_consolidation_worker.job_policy.stale_timeout_seconds == 27
            assert app.memory_consolidation_worker.interval_seconds == .2
            assert app.episode_assembler.extractor_version == "memory-consolidator/3.0"
            assert app.cognitive_entry.provider is app.provider
            async def check():
                for provider in ("fake", "mock"):
                    names = ("episode_processing_loop", "memory_consolidation_worker", "relationship_reflection_worker")
                    workers = {name: SimpleNamespace(start=Mock(), stop=AsyncMock()) for name in names}
                    with patch.multiple(app, **workers,
                                        provider=SimpleNamespace(name=provider)):
                        async with app.lifespan(app.app):
                            pass
                    for name, worker in workers.items():
                        expected = name == "episode_processing_loop" or provider != "mock"
                        assert worker.start.call_count == int(expected), (provider, name)
                        assert worker.stop.await_count == int(expected), (provider, name)
            asyncio.run(check())
        ''')
        env = {**os.environ, "EMBODIEDNPC_MEMORY_DB": str(Path(self.directory.name) / "app.sqlite3"),
               "EMBODIEDNPC_PROVIDER": "mock",
               "MEMORY_JOB_MAX_RETRIES": "3",
               "MEMORY_JOB_STALE_TIMEOUT_SECONDS": "27", "MEMORY_JOB_POLL_INTERVAL_SECONDS": ".2"}
        result = await asyncio.to_thread(subprocess.run, [sys.executable, "-c", script], env=env,
                                         capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
