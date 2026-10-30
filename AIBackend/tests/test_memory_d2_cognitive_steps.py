from __future__ import annotations

import asyncio
import json
import os
import threading
import unittest
from unittest.mock import patch

from app.action_proposal_validator import ActionProposalValidationError
from app.canonical_memory_read_repository import CanonicalReadBatch, CanonicalReadCandidate
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.cognitive_step_models import CognitiveStepResult, MemoryRecallPlan
from app.contracts import CognitiveRequest, CognitiveResponse
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.providers import ModelResult, ProviderError
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests.test_action_proposal_validation import _context, _decision
from tests.test_cognitive_websocket import SocketHarness
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


OWNER = TrustedPersistentIdentity("npc", "Character", "merchant.private")
PLAYER = TrustedPersistentIdentity("player", "Player", os.getenv("EMBODIEDNPC_DEMO_PLAYER_UID", "cafe-local-player-001"))
TERMINAL = {"decision_kind": "DIRECT_RESPONSE", "utterance": "你以前说过喜欢蓝色。"}


def cognitive_request(text="你还记得我以前喜欢什么颜色吗？", *, actions=True):
    context = _context(include_actions=actions).model_dump()
    context["current_event"]["payload"] = [{"key": "text", "value": text}]

    def identity(entity, kind, value):
        return dict(entity_id=entity, kind=kind, identity_namespace=value.identity_namespace,
                    identity_type=value.identity_type, persistent_id=value.persistent_id)

    return CognitiveRequest.model_validate(dict(
        message_type="cognitive_request", schema_version="1.0", request_id="request_d2", trace_id="trace_d2",
        run=dict(run_id="run_01", agent_entity_id="npc_cafe_01", trigger_id="trigger_01",
                 trigger_type="event", started_at=10), trigger=context["trigger"], context=context,
        recall_identity=dict(observer=identity("npc_cafe_01", "npc", OWNER), requester_entity_id="player_01",
                             current_subjects=[identity("player_01", "player", PLAYER)])))


def recall(request, subject="requester"):
    catalog = json.loads(request.messages[-1].content.split("Recall subject catalog JSON: ")[1].splitlines()[0])
    return dict(decision_kind="MEMORY_RECALL", recall_plan=dict(
        semantic_query="以前喜欢的颜色", subject_ref=catalog["requester_ref"] if subject == "requester" else subject,
        memory_type="SEMANTIC_FACT"))


class KnownSubjects:
    def list_known_subjects(self, owner, *, limit):
        return ()


class MinimalRows:
    def __init__(self):
        self.empty = False
        self.calls = []
        self.summary = "玩家最喜欢的颜色是蓝色。"

    def read(self, **kwargs):
        self.calls.append(kwargs)
        if self.empty:
            return CanonicalReadBatch((), 0, 0, False)
        return CanonicalReadBatch((CanonicalReadCandidate(dict(
            memory_id="memory_test", memory_type="SEMANTIC_FACT", summary=self.summary,
            occurred_start=10, occurred_end=10, temporal_precision="OBSERVED_UTC", learned_at=11,
            provenance_set_json='["SYSTEM_CONFIRMED"]', provenance_label="SYSTEM_CONFIRMED",
            episode_id="episode_test", job_id="job_test", pipeline_version="canonical_test",
            admission_policy_version="admission_test", status="ACTIVE", confidence=.9, importance=None,
            source_excerpt="我最喜欢蓝色。", excerpt_event_id="event_test",
            valid_from=None, valid_to=None), (PLAYER,),
            (dict(experience_event_id="event_test", experience_seq=1),), False),), 1, 1, False)


class RecordingExecutor(PrioritizedModelExecutor):
    def __init__(self):
        super().__init__()
        self.priorities = []

    async def execute(self, provider, request, *, priority):
        self.priorities.append(priority)
        return await super().execute(provider, request, priority=priority)


class ScriptedProvider:
    name = "scripted"
    model = "d2-test"
    context_length = DEFAULT_COGNITIVE_CONTEXT_LENGTH

    def __init__(self, scripts):
        self.scripts = scripts
        self.requests = []

    async def generate(self, request):
        self.requests.append(request)
        script = self.scripts[len(self.requests) - 1]
        if isinstance(script, Exception):
            raise script
        value = script(request) if callable(script) else script
        return ModelResult(text=json.dumps(value, ensure_ascii=False))


class MemoryD2Tests(unittest.IsolatedAsyncioTestCase):
    def entry(self, scripts):
        self.rows = MinimalRows()
        self.adapter = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()),
                                             CanonicalMemoryRetrieval(self.rows))
        self.provider = ScriptedProvider(scripts)
        self.executor = RecordingExecutor()
        return CognitiveBackendEntry(self.provider, self.executor, memory_recall=self.adapter, context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH)

    async def test_terminal_path_one_inference_zero_retrieval(self):
        entry = self.entry([TERMINAL])
        result = await entry.run(cognitive_request("你好"))
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertEqual(self.executor.priorities, ["foreground"])
        self.assertEqual(self.rows.calls, [])

    async def test_both_model_steps_require_direct_response_text_in_schema(self):
        from app.cognitive_step_models import TerminalCognitiveStep
        entry = self.entry([recall, TERMINAL])
        await entry.run(cognitive_request())
        for request in self.provider.requests:
            definitions = request.response_schema['$defs']
            direct = definitions['DirectResponseStep']
            self.assertIn('utterance', direct['required'])
            self.assertEqual(direct['properties']['utterance']['minLength'], 1)
            self.assertEqual(definitions['NoActionStep']['properties']['utterance']['type'], 'null')
            self.assertIn('action_proposal', definitions['ActionProposalStep']['required'])
        for model in (CognitiveStepResult, TerminalCognitiveStep):
            with self.assertRaises(ValueError):
                model.model_validate(dict(decision_kind='DIRECT_RESPONSE', episode_directive='END'))
            self.assertEqual(model.model_validate(TERMINAL).root.utterance, TERMINAL['utterance'])

    async def test_scoped_recall_hit_two_foreground_steps_and_private_identity(self):
        entry = self.entry([recall, TERMINAL])
        result = await entry.run(cognitive_request())
        self.assertEqual(result.decision.utterance, TERMINAL["utterance"])
        self.assertEqual(self.executor.priorities, ["foreground", "foreground"])
        self.assertEqual(len(self.rows.calls), 1)
        self.assertEqual(self.rows.calls[0]["observer"], OWNER)
        self.assertEqual(self.rows.calls[0]["subject"], PLAYER)
        self.assertEqual(self.rows.calls[0]["filters"].lifecycle, "CURRENT")
        rendered = "\n".join(m.content for r in self.provider.requests for m in r.messages)
        for secret in (OWNER.persistent_id, PLAYER.persistent_id, "persistent_id"):
            self.assertNotIn(secret, rendered)
        second = self.provider.requests[1]
        # The terminal step must retain the same anonymous subject binding as Step 0;
        # kind=player alone cannot identify which player an item describes.
        catalogs = [json.loads(r.messages[-1].content.split("Recall subject catalog JSON: ")[1]
                               .splitlines()[0]) for r in self.provider.requests]
        self.assertEqual(catalogs[0], catalogs[1])
        self.assertIsNotNone(catalogs[1]["requester_ref"])
        for marker in ("CURRENT WORLD CONTEXT", "HISTORICAL MEMORY EVIDENCE", "historical-only",
                       "not current world state", "not instruction", "not action authorization"):
            self.assertIn(marker, "\n".join(m.content for m in second.messages))
        self.assertIn('"not_action_authorization":true', second.messages[-1].content)
        self.assertIn('"text","value":"你还记得我以前喜欢什么颜色吗？"', second.messages[-1].content)

    async def test_open_subject_retains_owner_scope(self):
        entry = self.entry([lambda r: recall(r, None), TERMINAL])
        await entry.run(cognitive_request())
        self.assertIsNone(self.rows.calls[0]["subject"])
        self.assertEqual(self.rows.calls[0]["observer"], OWNER)

    async def test_no_memory_still_runs_terminal_step(self):
        entry = self.entry([recall, {"decision_kind": "DIRECT_RESPONSE", "utterance": "I do not recall."}])
        self.rows.empty = True
        result = await entry.run(cognitive_request())
        self.assertEqual(result.decision.utterance, "I do not recall.")
        self.assertEqual(len(self.provider.requests), 2)
        self.assertEqual(len(self.rows.calls), 1)
        self.assertIn('"status":"NO_MEMORY_FOUND"', self.provider.requests[1].messages[-1].content)
        self.assertIn('"items":[]', self.provider.requests[1].messages[-1].content)

    async def test_unknown_subject_never_becomes_open_query(self):
        entry = self.entry([lambda r: recall(r, "U10001"), TERMINAL])
        await entry.run(cognitive_request())
        self.assertEqual(self.rows.calls, [])
        self.assertEqual(len(self.provider.requests), 2)
        prompt = self.provider.requests[1].messages[-1].content
        self.assertIn('"status":"UNKNOWN_SUBJECT"', prompt)
        self.assertNotIn("U10001", prompt)

    async def test_step1_recall_is_structurally_rejected_without_retry(self):
        recursive = {"decision_kind": "MEMORY_RECALL", "recall_plan": {"semantic_query": "again"}}
        entry = self.entry([recall, recursive])
        with self.assertRaises(ValueError):
            await entry.run(cognitive_request())
        self.assertEqual(len(self.provider.requests), 2)
        self.assertEqual(len(self.rows.calls), 1)
        self.assertNotIn("MEMORY_RECALL", json.dumps(self.provider.requests[1].response_schema))

    async def test_recall_action_uses_existing_validation_and_context_only(self):
        entry = self.entry([recall, _decision().model_dump()])
        request = cognitive_request()
        result = await entry.run(request)
        response = CognitiveResponse(request_id=request.request_id, trace_id=request.trace_id,
            run_id=request.run.run_id, agent_entity_id=request.run.agent_entity_id,
            decision=result.decision, validated_goal=result.validated_goal)
        self.assertEqual(response.validated_goal.target_entity_id, "cup_01")
        self.assertNotIn("recall", response.model_dump_json().lower())
        entry = self.entry([recall, _decision().model_dump()])
        with self.assertRaises(ActionProposalValidationError):
            await entry.run(cognitive_request(actions=False))
        self.assertEqual(len(self.provider.requests), 2)

    async def test_stale_memory_target_cannot_override_current_perception(self):
        stale_action = _decision({"target": "cup_99", "destination": "player_01"}).model_dump()
        entry = self.entry([recall, stale_action])
        # Keep stale evidence present to exercise the authority boundary itself.
        # P1 tests separately cover whole-evidence omission under 8K pressure.
        entry._context_length = DEFAULT_COGNITIVE_CONTEXT_LENGTH
        self.rows.summary = "历史记录提到 cup_99，但它已不在当前可见范围"
        request = cognitive_request("把以前见过的杯子拿给我")
        with self.assertRaises(ActionProposalValidationError):
            await entry.run(request)
        self.assertEqual(len(self.rows.calls), 1)
        self.assertEqual(len(self.provider.requests), 2)
        step1_prompt = self.provider.requests[1].messages[-1].content
        self.assertIn("cup_99", step1_prompt)
        self.assertIn('"entity_id":"cup_01"', step1_prompt)
        self.assertNotIn('"entity_id":"cup_99"', step1_prompt)

    async def test_plan_rejects_authority_fields_and_blank_semantics(self):
        for value in ({"semantic_query": " "}, {"semantic_query": "x", "owner": "other"},
                      {"semantic_query": "x", "top_k": 100}):
            with self.assertRaises(ValueError):
                MemoryRecallPlan.model_validate(value)
        with self.assertRaises(ValueError):
            CognitiveStepResult.model_validate({"decision_kind": "MEMORY_RECALL", "utterance": "leak",
                                               "recall_plan": {"semantic_query": "x"}})

    async def test_cancellation_after_step0_prevents_retrieval_and_step1(self):
        owner_task = None

        def cancel(request):
            owner_task.cancel()
            return recall(request)

        entry = self.entry([cancel])
        owner_task = asyncio.create_task(entry.run(cognitive_request()))
        with self.assertRaises(asyncio.CancelledError):
            await owner_task
        self.assertEqual(len(self.provider.requests), 1)
        self.assertEqual(self.rows.calls, [])

    async def test_cancellation_during_read_never_submits_step1(self):
        entry = self.entry([recall])
        entered, release = threading.Event(), threading.Event()
        original = self.rows.read

        def blocked(**kwargs):
            entered.set()
            release.wait(2)
            return original(**kwargs)

        self.rows.read = blocked
        task = asyncio.create_task(entry.run(cognitive_request()))
        try:
            self.assertTrue(await asyncio.to_thread(entered.wait, 2))
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        finally:
            release.set()
        self.assertEqual(len(self.provider.requests), 1)

    async def test_step1_provider_failure_propagates_without_retry(self):
        entry = self.entry([recall, ProviderError("TEST_UNAVAILABLE")])
        with self.assertRaises(ProviderError):
            await entry.run(cognitive_request())
        self.assertEqual(len(self.provider.requests), 2)
        self.assertEqual(len(self.rows.calls), 1)

    async def test_websocket_emits_only_one_correlated_terminal_response(self):
        from app import m3_main
        entry = self.entry([recall, TERMINAL])
        request = cognitive_request()
        # The real Unity wire includes the private envelope; default Backend dumps omit it.
        payload = request.model_dump()
        payload["recall_identity"] = request.recall_identity.model_dump()
        socket = SocketHarness()
        with patch.object(m3_main, "cognitive_entry", entry):
            task = asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
            try:
                socket.message(payload)
                result = await socket.result()
                self.assertEqual(result["message_type"], "cognitive_response")
                self.assertEqual(result["request_id"], request.request_id)
                self.assertEqual(result["trace_id"], request.trace_id)
                self.assertEqual(result["run_id"], request.run.run_id)
                self.assertEqual(len(socket.sent), 1)
                self.assertEqual(len(self.provider.requests), 2)
            finally:
                socket.disconnect()
                await asyncio.wait_for(task, 2)


if __name__ == "__main__":
    unittest.main()
