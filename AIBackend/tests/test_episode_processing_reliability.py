"""S02 fault injection. Isolate every persistent path before importing app code.

Run this module in a fresh subprocess; no service, real Provider or model is used.
Lifespan tests execute the actual function AST against explicitly fake resources.
"""
from __future__ import annotations

import ast
import asyncio
import logging
import os
import sqlite3
import sys
import tempfile
import threading
import time
import unittest
from contextlib import asynccontextmanager, closing
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import AsyncMock, Mock, patch


if "app.m3_main" in sys.modules or "app.main" in sys.modules:
    raise RuntimeError("S02_TEST_REQUIRES_FRESH_PROCESS")
_isolation = tempfile.TemporaryDirectory(prefix="embodiednpc-s02-")
_root = Path(_isolation.name)
for _key in list(os.environ):
    if _key.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "DEEPSEEK_",
                        "OPENAI_", "ANTHROPIC_", "GEMINI_", "GOOGLE_", "HF_", "TRANSFORMERS_")):
        os.environ.pop(_key, None)
for _key, _name in {
    "EMBODIEDNPC_MEMORY_DB": "memory.sqlite3",
    "EMBODIEDNPC_MODEL_CONFIG_FILE": "model_api_configs.json",
    "EMBODIEDNPC_SETTINGS_FILE": "runtime_settings.json",
    "EMBODIEDNPC_CONTEXT_POLICY_FILE": "context_policy.json",
    "EMBODIEDNPC_KNOWLEDGE_FILE": "knowledge.json",
    "EMBODIEDNPC_KNOWLEDGE_INDEX_DB": "knowledge.sqlite3",
    "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE": "session.json",
}.items():
    os.environ[_key] = str(_root / _name)
os.environ.update(EMBODIEDNPC_PROVIDER="mock", CANONICAL_EMBEDDING_ENABLED="false",
                  HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONDONTWRITEBYTECODE="1")
sys.dont_write_bytecode = True

import httpx
from fastapi import FastAPI
from app.canonical_embeddings import CanonicalEmbeddingIndexer
from app.embedding_index_control import ManagedEmbeddingIndexer
from app.episode_assembler import EpisodeAssembler
from app.episode_models import MemoryJobPolicy
from app.episode_processing import EpisodeProcessingLoop, EpisodeStorageGuard
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.knowledge_embeddings import KnowledgeEmbeddingIndexer
from app.local_access import LocalBackendAccessMiddleware, LocalBackendSession
from app.memory_job_repository import MemoryJobRepository
from app.providers import ProviderHealth
from app.runtime_settings_api import create_runtime_settings_router
from app.runtime_settings_store import RuntimeSettingsStore
from app.sqlite_database import SQLiteDatabase
from tests import test_mem_p2_integration as fixtures


VERSION = "memory-consolidator/3.0"
ENTRY = Path(__file__).resolve().parents[1] / "app" / "m3_main.py"


def storage_error(code=sqlite3.SQLITE_BUSY):
    error = sqlite3.OperationalError("private SQL/path/key must never be logged")
    error.sqlite_errorcode = code
    return error


def function_from_entry(name, namespace):
    tree = ast.parse(ENTRY.read_text(encoding="utf-8-sig"))
    node = next(n for n in tree.body if isinstance(n, ast.AsyncFunctionDef) and n.name == name)
    if name != "lifespan":
        node.decorator_list = []
    exec(compile(ast.Module(body=[node], type_ignores=[]), str(ENTRY), "exec"), namespace)
    return namespace[name]


async def until(predicate, timeout=2):
    async def wait():
        while not predicate():
            await asyncio.sleep(.001)
    await asyncio.wait_for(wait(), timeout)


def fake_worker(guard=None):
    jobs = NS(recover_stale=Mock(return_value=()), backlog=Mock(return_value=NS(
        pending_count=0, retry_count=0, oldest_pending_age_seconds=None,
        jobs_per_observer={}, last_failure_reason=None)))
    assembler = NS(ledger=NS(list_observer_heads=Mock(return_value={"npc_01": 1})),
                   process_observer=Mock(), idle_sweep=Mock())
    return EpisodeProcessingLoop(assembler, jobs, interval_seconds=10,
                                 storage_guard=guard or EpisodeStorageGuard(sleep=lambda _: None))


class ReliabilityTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.fixture = fixtures.MemP2IntegrationTests("runTest")
        await self.fixture.asyncSetUp()
        self.addAsyncCleanup(self.fixture.asyncTearDown)
        self.delays = []
        self.guard = EpisodeStorageGuard(sleep=self.delays.append, clock=lambda: 1000.)
        self.assembler = EpisodeAssembler(self.fixture.ledger, self.fixture.episodes,
            self.fixture.assembler.policy, VERSION, clock=lambda: 1000., storage_call=self.guard.call)
        self.worker = EpisodeProcessingLoop(self.assembler, self.fixture.jobs,
            job_policy=MemoryJobPolicy(extractor_version=VERSION, stale_timeout_seconds=10),
            storage_guard=self.guard, interval_seconds=10)
        self.addAsyncCleanup(self.worker.stop)

    def append(self, seq, *, observer="npc_01", terminal=True):
        with patch("app.experience_ledger.time.time", return_value=1000.):
            return self.fixture.append(seq, observer=observer, terminal=terminal)

    def rows(self, table):
        with closing(self.fixture.database.connect()) as connection:
            return connection.execute("SELECT * FROM " + table).fetchall()

    def pair(self, observer, *, directive="END", middle=False):
        events = [("dialogue_input", 1)]
        if middle:
            events.append(("goal_succeeded", 2))
        events.append(("dialogue_response", len(events) + 1))
        for kind, seq in events:
            if kind == "goal_succeeded":
                draft = ExperienceDraft(observer_id=observer, event_type=kind, source_type="goal_outcome",
                    source_session_id="session_01", clock_domain="unity_realtime",
                    source_event_id="goal_" + observer, actor_entity_id=observer,
                    conversation_id="conversation_01", structured_payload={"goal_type": "bring_object"},
                    occurred_at=float(seq), observed_at=float(seq), provenance="EXECUTED_BY_SELF")
                with patch("app.experience_ledger.time.time", return_value=1000.):
                    self.fixture.ingestion.append(ExperienceAppendRequest(
                        request_id="goal_" + observer, trace_id="goal_trace", draft=draft))
                continue
            response = kind == "dialogue_response"
            draft = ExperienceDraft(schema_version="experience-draft/1.1", observer_id=observer,
                event_type=kind, source_type="dialogue", source_session_id="session_01",
                clock_domain="unity_realtime", source_event_id="turn_" + observer,
                actor_entity_id=observer if response else "player_01",
                target_entity_ids=("player_01" if response else observer,),
                structured_payload=dict(direction="npc_to_player" if response else "player_to_npc",
                    runtime_path="cognitive_runtime_v2", **({"episode_directive": directive} if response else {})),
                natural_language_payload="fictional offline text", occurred_at=float(seq),
                observed_at=float(seq), request_id="turn_" + observer, trace_id="trace_" + observer,
                conversation_id="conversation_01", provenance="SYSTEM_CONFIRMED")
            with patch("app.experience_ledger.time.time", return_value=1000.):
                self.fixture.ingestion.append(ExperienceAppendRequest(
                    request_id=f"append_{observer}_{seq}", trace_id="append_trace", draft=draft))

    async def test_T01_T03_busy_recovers_at_each_outer_operation(self):
        for phase in ("recover_stale", "list_observer_heads", "backlog"):
            with self.subTest(phase=phase):
                delays = []
                worker = fake_worker(EpisodeStorageGuard(sleep=delays.append))
                owner = worker.assembler.ledger if phase == "list_observer_heads" else worker.jobs
                operation = getattr(owner, phase)
                value = operation.return_value
                operation.side_effect = [storage_error(), storage_error(517), value]
                worker.start()
                await until(lambda: worker.snapshot()["last_success_at"] is not None)
                await worker.stop()
                self.assertEqual(delays, [.25, 1.])
                self.assertEqual(operation.call_count, 3)
                self.assertEqual(worker.assembler.process_observer.call_count, 1)
                self.assertEqual(worker.snapshot()["state"], "STOPPED")
                self.assertIsNotNone(worker.snapshot()["last_failure_at"])

    async def test_T04_T13_busy_exhaustion_fault_latch_and_repeated_stop(self):
        worker = fake_worker()
        worker.jobs.recover_stale.side_effect = storage_error()
        worker.start()
        await worker._task
        await worker.stop()
        await worker.stop()
        worker.start()
        self.assertEqual(worker.jobs.recover_stale.call_count, 3)
        self.assertEqual(worker.snapshot()["state"], "FAULTED")
        self.assertEqual(worker.snapshot()["error_code"], "EPISODE_STORAGE_BUSY_EXHAUSTED")
        self.assertTrue(worker.is_quiescent)

    async def test_T05_unrecoverable_errors_do_not_retry_or_log_private_text(self):
        for error in (storage_error(sqlite3.SQLITE_LOCKED), storage_error(sqlite3.SQLITE_IOERR),
                      storage_error(sqlite3.SQLITE_CORRUPT), sqlite3.OperationalError("private SQL/path/key"),
                      RuntimeError("private SQL/path/key")):
            with self.subTest(code=getattr(error, "sqlite_errorcode", None)):
                worker = fake_worker()
                worker.jobs.recover_stale.side_effect = error
                with self.assertLogs("embodiednpc.backend.episode_processing", "ERROR") as logs:
                    worker.start()
                    await worker._task
                    await worker.stop()
                self.assertEqual(worker.jobs.recover_stale.call_count, 1)
                self.assertEqual(worker.snapshot()["state"], "FAULTED")
                self.assertNotIn("private SQL/path/key", " ".join(logs.output))

    async def test_T06_real_sqlite_busy_recovers_after_production_timeout(self):
        self.append(1)
        self.worker.process_once(1000.)
        self.fixture.jobs.claim_next_for_version(1000., VERSION)
        held, release = threading.Event(), threading.Event()
        def hold():
            with closing(self.fixture.database.connect()) as connection:
                connection.execute("BEGIN IMMEDIATE")
                held.set()
                try:
                    release.wait(10)
                finally:
                    connection.rollback()
        holder = asyncio.create_task(asyncio.to_thread(hold))
        guard = EpisodeStorageGuard()
        worker = fake_worker(guard)
        worker.jobs = self.fixture.jobs
        worker.assembler = self.assembler
        worker.job_policy = self.worker.job_policy
        worker.process_once = lambda: EpisodeProcessingLoop.process_once(worker, 2000.)
        try:
            await until(held.is_set)
            started = time.monotonic()
            worker.start()
            await until(lambda: worker.snapshot()["state"] == "RETRYING", timeout=8)
            self.assertGreaterEqual(time.monotonic() - started, 4.8)
            release.set()
            await holder
            await until(lambda: worker.snapshot()["last_success_at"] is not None)
            self.assertNotEqual(worker.snapshot()["state"], "FAULTED")
            self.assertEqual(self.rows("memory_jobs")[0]["retry_count"], 1)
        finally:
            release.set()
            await holder
            await worker.stop()

    async def test_T07_open_and_append_rollback_busy_replays_only_atomic_call(self):
        self.pair("npc_01")
        repository = self.fixture.episodes
        # Inject a real SQLite BUSY from inside each original transaction before
        # COMMIT; the connection context must roll it back before guard replay.
        original_factory = repository._connection_factory
        injected = set()
        class Connection:
            def __init__(self): self.inner = original_factory()
            def __getattr__(self, name): return getattr(self.inner, name)
            def __enter__(self): self.inner.__enter__(); return self
            def __exit__(self, *args): return self.inner.__exit__(*args)
            def execute(self, sql, *args):
                result = self.inner.execute(sql, *args)
                operation = "open" if "INSERT INTO episodes" in sql else "append" if "UPDATE episodes SET end_seq" in sql else None
                if operation and operation not in injected:
                    injected.add(operation)
                    raise storage_error()
                return result
            def close(self): self.inner.close()
        with patch.object(repository, "_connection_factory", Connection):
            self.worker.process_once(1000.)
        self.assertEqual(injected, {"open", "append"})
        self.assertEqual(len(self.rows("episodes")), 1)
        self.assertEqual(self.rows("episodes")[0]["event_count"], 2)
        self.assertEqual(len(self.rows("memory_jobs")), 1)

    async def test_T08_original_terminal_close_arguments_and_unique_job(self):
        self.append(1)
        original = self.fixture.episodes.close_and_enqueue
        calls = []
        def close(*args):
            calls.append(args)
            if len(calls) == 1:
                self.assertEqual(self.fixture.episodes.get(args[0]).end_seq, 1)
                raise storage_error()
            return original(*args)
        with patch.object(self.fixture.episodes, "close_and_enqueue", side_effect=close):
            self.worker.process_once(1000.)
        self.assertEqual(calls[0], calls[1])
        episode = self.fixture.episodes.list_for_observer("npc_01")[0]
        self.assertEqual((episode.close_reason, episode.ended_at_utc), ("goal_succeeded", 1000.))
        original(*calls[0])
        self.assertEqual([len(self.rows(t)) for t in ("experience_events", "episodes", "memory_jobs")], [1, 1, 1])

    async def test_T09_group_mid_append_and_close_keep_end_reason(self):
        for observer, directive, middle, fits, reason in (
            ("npc_a", "END", False, True, "semantic_end"),
            ("npc_b", "CONTINUE", True, True, "goal_succeeded"),
            ("npc_c", "END", False, False, "indivisible_capacity_exceeded"),
        ):
            self.pair(observer, directive=directive, middle=middle)
            self.assembler.capacity_probe = lambda events: fits
            events = self.fixture.ledger.read_range(observer, 0, 64)
            self.assertEqual(len(self.assembler._interaction_groups(events)[0]), 3 if middle else 2)
            append, close = self.fixture.episodes.append_event, self.fixture.episodes.close_and_enqueue
            append_calls, close_calls = [], []
            failed_append = False
            def append_once(*args):
                nonlocal failed_append
                append_calls.append(args)
                if args[1].event_type == "dialogue_response" and not failed_append:
                    failed_append = True
                    raise storage_error()
                return append(*args)
            def close_once(*args):
                close_calls.append(args)
                if len(close_calls) == 1: raise storage_error()
                return close(*args)
            with patch.object(self.fixture.episodes, "append_event", side_effect=append_once), \
                 patch.object(self.fixture.episodes, "close_and_enqueue", side_effect=close_once):
                self.assembler.process_observer(observer)
            episode = self.fixture.episodes.list_for_observer(observer)[0]
            self.assertEqual((episode.status, episode.close_reason, episode.end_seq),
                             ("CLOSED", reason, 3 if middle else 2))
            self.assertEqual(close_calls[0], close_calls[1])
        self.assertEqual(len(self.rows("memory_jobs")), 3)

    async def test_T10_io_error_never_replays_ambiguous_append(self):
        self.pair("npc_01")
        with patch.object(self.fixture.episodes, "append_event", side_effect=storage_error(sqlite3.SQLITE_IOERR)) as operation:
            with self.assertRaises(sqlite3.OperationalError): self.worker.process_once(1000.)
        self.assertEqual(operation.call_count, 1)
        self.assertEqual(self.guard.snapshot()["state"], "FAULTED")
        self.assertEqual(len(self.rows("memory_jobs")), 0)

    async def test_T11_stale_recovery_committed_prefix_is_not_charged_twice(self):
        for observer in ("npc_a", "npc_b"): self.append(1, observer=observer)
        self.worker.process_once(1000.)
        jobs = self.fixture.jobs
        first = jobs.claim_next_for_version(1000., VERSION)
        second = jobs.claim_next_for_version(1001., VERSION)
        original = jobs.mark_failed
        failed = False
        def fail_second(job_id, *args):
            nonlocal failed
            if job_id == second.job_id and not failed:
                failed = True
                raise storage_error()
            return original(job_id, *args)
        with patch.object(jobs, "mark_failed", side_effect=fail_second):
            self.worker.process_once(2000.)
        self.assertTrue(failed)
        self.assertEqual([(jobs.get(j.job_id).status, jobs.get(j.job_id).retry_count)
                          for j in (first, second)], [("RETRY", 1), ("RETRY", 1)])
        self.assertIsNone(jobs.mark_succeeded(first.job_id, first.attempt_generation, 2001.))

    async def test_T12_observer_and_idle_failure_fault_the_worker(self):
        for phase in ("process_observer", "idle_sweep"):
            worker = fake_worker()
            getattr(worker.assembler, phase).side_effect = ValueError("private content")
            worker.start()
            await worker._task
            await worker.stop()
            self.assertEqual(worker.snapshot()["state"], "FAULTED")
            self.assertIsNone(worker.snapshot()["last_success_at"])

    async def test_T14_T15_stop_waiter_cancellation_drains_and_prevents_second_start(self):
        worker = fake_worker()
        entered, release = threading.Event(), threading.Event()
        worker.process_once = lambda: (entered.set(), release.wait(5))
        worker.start()
        task = worker._task
        try:
            await until(entered.is_set)
            stopping = asyncio.create_task(worker.stop())
            await asyncio.sleep(0)
            stopping.cancel()
            await asyncio.sleep(.01)
            worker.start()
            self.assertIs(worker._task, task)
            self.assertIsNotNone(worker._inflight)
            self.assertFalse(stopping.done())
            self.assertFalse(worker.is_quiescent)
            release.set()
            with self.assertRaises(asyncio.CancelledError): await stopping
            self.assertTrue(worker.is_quiescent)
            worker.start()
            self.assertIsNot(worker._task, task)
        finally:
            release.set()
            await worker.stop()

    async def test_T16_repeated_worker_cancel_and_late_failure_are_both_observed(self):
        worker = fake_worker()
        entered, release = threading.Event(), threading.Event()
        def operation():
            entered.set(); release.wait(5)
            raise RuntimeError("private late failure")
        worker.process_once = operation
        worker.start()
        try:
            await until(entered.is_set)
            task = worker._task
            task.cancel()
            await asyncio.sleep(.01)
            task.cancel()
            await asyncio.sleep(.01)
            self.assertFalse(task.done())
            self.assertIsNotNone(worker._inflight)
            release.set()
            with self.assertRaises(asyncio.CancelledError): await task
            await worker.stop()
            self.assertTrue(worker.is_quiescent)
            self.assertEqual(worker.snapshot()["state"], "FAULTED")
        finally:
            release.set()
            await worker.stop()

    async def test_T17_stop_wakes_interval_and_drains_retry(self):
        worker = fake_worker()
        worker.start()
        await until(lambda: worker.snapshot()["last_success_at"] is not None)
        await asyncio.wait_for(worker.stop(), .5)
        self.assertEqual(worker.snapshot()["state"], "STOPPED")
        entered, release = threading.Event(), threading.Event()
        guard = EpisodeStorageGuard(sleep=lambda delay: (entered.set(), release.wait(5)))
        worker = fake_worker(guard)
        worker.jobs.recover_stale.side_effect = [storage_error(), ()]
        worker.start()
        try:
            await until(entered.is_set)
            stopping = asyncio.create_task(worker.stop())
            await asyncio.sleep(.01)
            self.assertFalse(stopping.done())
            release.set()
            await stopping
            self.assertTrue(worker.is_quiescent)
        finally:
            release.set()
            await worker.stop()

    async def test_T22_persistent_jobs_reopen_with_original_stale_generation_policy(self):
        for observer in ("npc_a", "npc_b", "npc_c"): self.append(1, observer=observer)
        self.worker.process_once(1000.)
        jobs = self.fixture.jobs
        running = jobs.claim_next_for_version(1000., VERSION)
        retry = jobs.claim_next_for_version(1000., VERSION)
        jobs.mark_failed(retry.job_id, retry.attempt_generation, 1001., "TEST_FAILURE", self.worker.job_policy)
        reopened = MemoryJobRepository(SQLiteDatabase(self.fixture.path).connect)
        self.assertEqual(reopened.recover_stale(1005., self.worker.job_policy), ())
        self.assertEqual(len(reopened.recover_stale(1011., self.worker.job_policy)), 1)
        self.assertIsNone(reopened.mark_succeeded(running.job_id, running.attempt_generation, 1012.))
        self.assertEqual(len(self.rows("memory_jobs")), 3)
        self.assertTrue(any(r["status"] == "PENDING" for r in self.rows("memory_jobs")))

    async def test_T23_multinpc_repeated_polls_keep_ids_hashes_and_ownership(self):
        for observer in ("npc_a", "npc_b", "npc_c", "npc_d"): self.append(1, observer=observer)
        self.worker.process_once(1000.)
        before = [tuple(row) for row in self.rows("memory_jobs")]
        self.worker.process_once(1001.)
        self.assertEqual(before, [tuple(row) for row in self.rows("memory_jobs")])
        self.assertEqual(set(self.fixture.jobs.backlog(1001.).jobs_per_observer),
                         {"npc_a", "npc_b", "npc_c", "npc_d"})

    async def test_T24_persistent_close_failure_never_fakes_success(self):
        self.append(1)
        self.worker.process_once = lambda: EpisodeProcessingLoop.process_once(self.worker, 1000.)
        with patch.object(self.fixture.episodes, "close_and_enqueue", side_effect=storage_error()) as close:
            self.worker.start()
            await self.worker._task
            await self.worker.stop()
        self.assertEqual(close.call_count, 3)
        self.assertEqual(self.worker.snapshot()["state"], "FAULTED")
        self.assertEqual(self.rows("episodes")[0]["status"], "OPEN")
        self.assertEqual([len(self.rows(t)) for t in ("experience_events", "memory_jobs")], [1, 0])

    def test_T29_pragma_failure_closes_unpublished_connection_and_preserves_error(self):
        database = object.__new__(SQLiteDatabase)
        database.database_path = str(_root / "not-opened.sqlite3")
        for step in (1, 2, 3):
            error = storage_error()
            connection = Mock()
            connection.execute.side_effect = [None] * (step - 1) + [error]
            with patch("app.sqlite_database.sqlite3.connect", return_value=connection):
                with self.assertRaises(sqlite3.OperationalError) as caught: database.connect()
            self.assertIs(caught.exception, error)
            connection.close.assert_called_once()


class LifespanTests(unittest.IsolatedAsyncioTestCase):
    def make(self, *, fail_stop=None, fail_start=None, blocked=None, unsafe=False, mock=False):
        calls, entered, release = [], asyncio.Event(), asyncio.Event()
        class Worker:
            def __init__(self, name): self.name, self.is_quiescent = name, True
            def start(self):
                calls.append(self.name + ".start")
                if self.name == fail_start: raise RuntimeError("private startup error")
            async def stop(self):
                calls.append(self.name + ".stop")
                if self.name == blocked:
                    entered.set()
                    await release.wait()
                if self.name == fail_stop:
                    self.is_quiescent = not unsafe
                    raise RuntimeError("private stop error")
        workers = {n: Worker(n) for n in ("episode", "canonical", "knowledge", "consolidation", "relationship", "maintenance")}
        auth = NS(start=lambda: calls.append("auth.start"), invalidate=lambda: calls.append("auth.invalidate"),
                  stop=lambda: calls.append("auth.stop"))
        namespace = dict(asynccontextmanager=asynccontextmanager, asyncio=asyncio, FastAPI=object,
            logger=logging.getLogger("embodiednpc.backend.s02_test"),
            CanonicalEmbeddingIndexer=CanonicalEmbeddingIndexer, ManagedEmbeddingIndexer=ManagedEmbeddingIndexer,
            KnowledgeEmbeddingIndexer=KnowledgeEmbeddingIndexer, knowledge_management=NS(auth=auth),
            episode_processing_loop=workers["episode"], canonical_embedding_indexer=workers["canonical"],
            knowledge_embedding_indexer=workers["knowledge"], memory_consolidation_worker=workers["consolidation"],
            relationship_reflection_worker=workers["relationship"], memory_maintenance_worker=workers["maintenance"],
            provider=NS(name="mock" if mock else "fake-real-gate"),
            canonical_embedding_provider=NS(close=lambda: calls.append("embedding.close")))
        return function_from_entry("lifespan", namespace), calls, entered, release, namespace

    async def test_T18_each_stop_failure_still_closes_independent_resources(self):
        for name in ("episode", "canonical", "knowledge", "consolidation", "relationship", "maintenance"):
            context, calls, *_ = self.make(fail_stop=name)
            async with context(None): pass
            self.assertEqual([c for c in calls if c.endswith(".stop")],
                ["knowledge.stop", "canonical.stop", "maintenance.stop", "relationship.stop",
                 "consolidation.stop", "episode.stop", "auth.stop"])
            self.assertLess(calls.index("episode.stop"), calls.index("embedding.close"))
            self.assertEqual(calls[-1], "auth.stop")

    async def test_T19_unjoined_embedding_user_prevents_provider_close(self):
        context, calls, *_ = self.make(fail_stop="canonical", unsafe=True)
        async with context(None): pass
        self.assertIn("episode.stop", calls)
        self.assertNotIn("embedding.close", calls)
        self.assertEqual(calls[-1], "auth.stop")

    async def test_T19_legacy_cancelled_thread_is_not_mistaken_for_quiescent(self):
        context, calls, _, _, namespace = self.make()
        entered, release, finished = threading.Event(), threading.Event(), threading.Event()
        def blocking():
            entered.set()
            release.wait(5)
            finished.set()
        legacy = CanonicalEmbeddingIndexer(NS(), NS())
        async def poll(limit): await asyncio.to_thread(blocking)
        legacy.index_missing_or_stale = poll
        namespace["canonical_embedding_indexer"] = legacy
        try:
            async with context(None): await until(entered.is_set)
            self.assertFalse(finished.is_set())
            self.assertNotIn("embedding.close", calls)
            self.assertEqual(calls[-1], "auth.stop")
        finally:
            release.set()
            await asyncio.to_thread(finished.wait, 2)

    async def test_T20_auth_stop_error_does_not_mask_body_failure(self):
        context, calls, _, _, namespace = self.make()
        namespace["knowledge_management"].auth.stop = Mock(side_effect=RuntimeError("private cleanup"))
        with self.assertRaisesRegex(ValueError, "body failure"):
            async with context(None): raise ValueError("body failure")
        namespace["knowledge_management"].auth.stop.assert_called_once()
        self.assertIn("embedding.close", calls)

    async def test_T20_partial_start_and_invalidate_failure_cleanup(self):
        for name in ("episode", "canonical", "knowledge", "consolidation", "relationship", "maintenance"):
            context, calls, *_ = self.make(fail_start=name)
            with self.assertRaises(RuntimeError):
                async with context(None): self.fail("startup must fail")
            self.assertEqual({c[:-6] for c in calls if c.endswith(".start") and not c.startswith("auth")},
                             {c[:-5] for c in calls if c.endswith(".stop") and not c.startswith("auth")})
            self.assertEqual(calls[-1], "auth.stop")
        context, calls, _, _, ns = self.make()
        ns["knowledge_management"].auth.invalidate = Mock(side_effect=RuntimeError("private"))
        async with context(None): pass
        self.assertIn("embedding.close", calls)
        self.assertEqual(calls[-1], "auth.stop")

    async def test_T21_cancellation_and_cleanup_error_still_drain_all_resources(self):
        context, calls, entered, release, _ = self.make(fail_stop="maintenance", blocked="knowledge")
        async def run():
            async with context(None): pass
        task = asyncio.create_task(run())
        await entered.wait()
        task.cancel()
        await asyncio.sleep(.01)
        task.cancel()
        await asyncio.sleep(.01)
        self.assertFalse(task.done())
        self.assertNotIn("auth.stop", calls)
        release.set()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertIn("episode.stop", calls)
        self.assertIn("embedding.close", calls)
        self.assertEqual(calls[-1], "auth.stop")

    async def test_T28_mock_gate_start_order_and_body_error_remain_unchanged(self):
        context, calls, *_ = self.make(mock=True)
        with self.assertRaisesRegex(ValueError, "body error"):
            async with context(None): raise ValueError("body error")
        self.assertEqual(calls[:4], ["auth.start", "episode.start", "canonical.start", "knowledge.start"])
        self.assertNotIn("maintenance.start", calls)
        self.assertEqual(calls[-1], "auth.stop")


class DiagnosticTests(unittest.IsolatedAsyncioTestCase):
    async def test_T25_T26_pure_live_protected_status_and_owned_session_cleanup(self):
        session = LocalBackendSession(NS(path=_root / "knowledge.json"))
        session.start()
        self.addCleanup(session.stop)
        guard = EpisodeStorageGuard(sleep=lambda _: None)
        guard.fault(storage_error(sqlite3.SQLITE_IOERR))
        provider = NS(name="mock", model="offline", generate=Mock(side_effect=AssertionError("no inference")),
            health=AsyncMock(return_value=ProviderHealth(provider="mock", model="offline", available=True)))
        app = FastAPI()
        app.add_middleware(LocalBackendAccessMiddleware, session=session)
        app.get("/live")(function_from_entry("live", {}))
        app.get("/health")(function_from_entry("health_check", {"provider": provider}))
        store = RuntimeSettingsStore(_root / "runtime_settings.json")
        app.include_router(create_runtime_settings_router(store, provider, episode_worker_status=guard.snapshot))
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app, client=("127.0.0.1", 1234)),
                                     base_url="http://127.0.0.1:8000") as client:
            with patch.object(store, "load_with_source", side_effect=AssertionError("live must not read storage")):
                self.assertEqual((await client.get("/live")).json()["status"], "alive")
                self.assertEqual((await client.get("/v1/runtime-status")).status_code, 403)
                self.assertEqual((await client.get("/health")).status_code, 403)
            provider.health.assert_not_awaited()
            old = {"Authorization": "Bearer " + session.token}
            value = (await client.get("/v1/runtime-status", headers=old)).json()
            self.assertEqual((value["status"], value["episode_worker"]["state"]), ("ok", "FAULTED"))
            self.assertEqual((await client.get("/health", headers=old)).status_code, 200)
            session.stop()
            self.assertFalse(session.path.exists())
            session.start()
            self.assertEqual((await client.get("/v1/runtime-status", headers=old)).status_code, 403)
            new = {"Authorization": "Bearer " + session.token}
            self.assertEqual((await client.get("/v1/runtime-status", headers=new)).status_code, 200)
            session.invalidate()
            self.assertEqual((await client.get("/health", headers=new)).status_code, 403)
        provider.generate.assert_not_called()
        session.stop()
        self.assertFalse(session.path.exists())
        context, _, _, _, namespace = LifespanTests("runTest").make(fail_stop="maintenance")
        namespace["knowledge_management"].auth = session
        async with context(None):
            self.assertTrue(session.path.exists())
        self.assertFalse(session.path.exists())
        self.assertIsNone(session.endpoint)


if __name__ == "__main__":
    unittest.main()
