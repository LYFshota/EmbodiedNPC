"""C2 config tests: temporary files/ledger, ASGI and scripted providers only."""

import json
import logging
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

import httpx
from fastapi import FastAPI
from pydantic import ValidationError

from app.context_policy_settings import ContextPolicyConfig, ContextPolicySettings, ReplaceContextPolicy, create_context_policy_router
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetExceeded
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests import test_cross_conversation_recent_dialogue as fixture_module
from tests import test_dialogue_continuation as continuation_module
from tests.test_context_budget_p1 import WireProvider, AllocationCapture, encode, prompt_turns
from tests.test_memory_d2_cognitive_steps import KnownSubjects, MinimalRows, recall


def replacement(n=None, revision=0):
    return ReplaceContextPolicy(expected_revision=revision,
        config=ContextPolicyConfig(mode="auto" if n is None else "manual", target_messages=n))


class ContextPolicySettingsTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "context_policy.json"
        self.settings = ContextPolicySettings(self.path)
        app = FastAPI()
        app.include_router(create_context_policy_router(self.settings))
        self.http = httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://c2.invalid")
        self.addAsyncCleanup(self.http.aclose)

    async def test_absent_defaults_and_automatic_capacity_rules(self):
        value = (await self.http.get("/v1/context-policy")).json()
        self.assertEqual("defaults", value["source"])
        self.assertTrue(value["saved_matches_applied"])
        self.assertEqual(8, self.settings.snapshot().limits(8192)["limit"])
        self.assertEqual(16, self.settings.snapshot().limits(16384)["limit"])
        self.assertFalse(self.path.exists())

    async def test_manual_presets_persist_restart_and_return_to_auto(self):
        for revision, n in enumerate((8, 16, 32, None)):
            response = await self.http.put("/v1/context-policy", json=replacement(n, revision).model_dump())
            self.assertEqual(200, response.status_code)
            value = response.json()
            self.assertEqual(n, value["applied"]["config"]["target_messages"])
            self.assertEqual(value["saved"], value["applied"])
            self.assertEqual(revision + 1, value["applied"]["revision"])
            reloaded = ContextPolicySettings(self.path)
            self.assertEqual(self.settings.snapshot(), reloaded.snapshot())
            self.assertEqual(value, reloaded.view())

    async def test_invalid_values_fail_without_change_or_file(self):
        cases = [dict(mode="manual", target_messages=n) for n in (0, -1, 65, True, False, 8.0, "8", None)]
        cases += [dict(mode="manual"), dict(mode="unknown", target_messages=8),
                  dict(mode="auto", target_messages=8), dict(mode="auto", extra=1)]
        for config in cases:
            with self.subTest(config=config):
                result = await self.http.put("/v1/context-policy", json=dict(expected_revision=0, config=config))
                self.assertEqual(422, result.status_code)
                self.assertIsNone(self.settings.snapshot().target_messages)
                self.assertFalse(self.path.exists())
        for revision in (True, -1, "0"):
            self.assertEqual(422, (await self.http.put("/v1/context-policy", json=dict(
                expected_revision=revision, config=dict(mode="auto")))).status_code)

    async def test_duplicate_keys_and_oversize_rejected(self):
        for body in ('{"expected_revision":0,"config":{"mode":"auto","mode":"manual"}}', " " * 4097):
            self.assertEqual(422, (await self.http.put("/v1/context-policy", content=body)).status_code)

    async def test_write_failure_preserves_saved_and_applied_and_cleans_temp(self):
        self.settings.save_and_apply(replacement(8))
        before = self.path.read_bytes()
        with patch("app.context_policy_settings.os.replace", side_effect=OSError("test failure")):
            result = await self.http.put("/v1/context-policy", json=replacement(32, 1).model_dump())
        self.assertEqual(503, result.status_code)
        self.assertEqual("CONTEXT_POLICY_WRITE_FAILED", result.json()["error"]["code"])
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual(8, self.settings.snapshot().target_messages)
        self.assertEqual([self.path], list(self.path.parent.iterdir()))

    async def test_corrupt_file_exposed_but_live_policy_unchanged(self):
        self.settings.save_and_apply(replacement(16))
        for raw in (b"{broken", b"{}", b'{"config":{"mode":"manual","target_messages":true}}',
                    b'{"schema_version":1,"schema_version":1}', b"x" * 4097):
            self.path.write_bytes(raw)
            value = (await self.http.get("/v1/context-policy")).json()
            self.assertEqual("CONTEXT_POLICY_DOCUMENT_INVALID", value["error"])
            self.assertIsNone(value["saved"])
            self.assertEqual(16, value["applied"]["config"]["target_messages"])
            self.assertIsNone(ContextPolicySettings(self.path).snapshot().target_messages)
            result = await self.http.put("/v1/context-policy", json=replacement(32, 1).model_dump())
            self.assertEqual(503, result.status_code)
            self.assertEqual(16, self.settings.snapshot().target_messages)

    async def test_disk_and_applied_divergence_and_revision_conflict(self):
        self.settings.save_and_apply(replacement(8))
        other = ContextPolicySettings(self.path)
        other.save_and_apply(replacement(32, 1))
        value = (await self.http.get("/v1/context-policy")).json()
        self.assertFalse(value["saved_matches_applied"])
        self.assertEqual(32, value["saved"]["config"]["target_messages"])
        self.assertEqual(8, value["applied"]["config"]["target_messages"])
        self.assertEqual(409, (await self.http.put("/v1/context-policy", json=replacement(16, 1).model_dump())).status_code)
        self.assertEqual(8, self.settings.snapshot().target_messages)

    async def test_bootstrap_wires_the_same_hot_policy_source(self):
        from app import m3_main
        self.assertEqual(m3_main.context_policy_settings.snapshot, m3_main.cognitive_entry._recent_dialogue_policy_source)
        self.assertEqual(m3_main.context_policy_settings.path, Path(__import__("os").environ["EMBODIEDNPC_CONTEXT_POLICY_FILE"]))


class ContextPolicyRunTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.settings = ContextPolicySettings(Path(directory.name) / "context_policy.json")
        self.fixture = fixture_module.CrossConversationRecentDialogueTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        for i in range(1, 21):
            self.fixture.pair(i, text=f"输入{i}", reply=f"回复{i}")
        raw = self.fixture.request(999, text="当前输入")
        self.fixture.append(raw, "当前输入", session="new_session")
        self.request = CognitiveRequest.model_validate(raw)

    def entry(self, provider, memory=None):
        return CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.fixture.ledger,
            recent_dialogue_policy_source=self.settings.snapshot, memory_recall=memory,
            inference_options=InferenceOptions(max_output_tokens=512))

    async def test_next_run_uses_hot_policy_without_rebuilding_entry(self):
        provider = WireProvider(16384)
        provider.scripts *= 3
        entry = self.entry(provider)
        with patch.object(self.fixture.ledger, "read_recent_dialogue", wraps=self.fixture.ledger.read_recent_dialogue) as reader:
            for revision, n in enumerate((8, 16, 32)):
                self.settings.save_and_apply(replacement(n, revision))
                await entry.run(self.request)
                self.assertEqual(n, reader.call_args.kwargs["limit"])
        self.assertEqual(3, len(provider.requests))

    async def test_two_steps_keep_one_snapshot_across_hot_update(self):
        self.settings.save_and_apply(replacement(8))
        provider = WireProvider(16384, True)
        def step_zero(_):
            self.settings.save_and_apply(replacement(32, 1))
            return recall(_)
        provider.scripts[0] = step_zero
        memory = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()), CanonicalMemoryRetrieval(MinimalRows()))
        source = Mock(wraps=self.settings.snapshot)
        entry = self.entry(provider, memory)
        entry._recent_dialogue_policy_source = source
        with patch.object(self.fixture.ledger, "read_recent_dialogue", wraps=self.fixture.ledger.read_recent_dialogue) as reader:
            await entry.run(self.request)
            reader.assert_called_once()
            self.assertEqual(8, reader.call_args.kwargs["limit"])
        source.assert_called_once()
        self.assertEqual(2, len(provider.requests))
        self.assertEqual(prompt_turns(provider.requests[0]), prompt_turns(provider.requests[1]))
        self.assertEqual(32, self.settings.snapshot().target_messages)

    async def test_continuation_uses_applied_policy_and_one_call(self):
        fixture = continuation_module.ContinuationTests()
        fixture.setUp()
        self.addCleanup(fixture.tearDown)
        self.settings.save_and_apply(replacement(32))
        provider = WireProvider(16384)
        provider.scripts = [dict(decision_kind="NO_ACTION")]
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=fixture.ledger,
            recent_dialogue_policy_source=self.settings.snapshot)
        with patch.object(fixture.ledger, "read_continuation_dialogue", wraps=fixture.ledger.read_continuation_dialogue) as reader:
            await entry.run(CognitiveRequest.model_validate(fixture.continuation()))
            self.assertEqual(32, reader.call_args.kwargs["limit"])
            self.assertTrue(reader.call_args.kwargs["timeline"])
        self.assertEqual(1, len(provider.requests))

    async def test_hot_n32_cannot_bypass_small_window_safe_failure(self):
        self.settings.save_and_apply(replacement(32))
        provider = WireProvider(8192)
        with self.assertRaises(ContextBudgetExceeded) as failure:
            await self.entry(provider).run(self.request)
        self.assertEqual(32, self.settings.snapshot().target_messages)
        self.assertEqual([], provider.requests)
        self.assertTrue(failure.exception.allocation.safe_failure)
        self.assertGreater(failure.exception.allocation.estimated_total,
                           failure.exception.allocation.input_hard_cap)
