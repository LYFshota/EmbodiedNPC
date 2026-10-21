"""P1 acceptance at the trusted ledger -> final provider payload boundary."""

import copy
import json
import logging
import unittest
import httpx
from unittest.mock import Mock

from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.context_budget_allocator import (
    AtomicContextGroup, ContextBudgetAllocator, ContextBudgetExceeded, ContextBudgetPolicy,
)
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveRequest, CognitiveCharacterContext, PerceivedEntity
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recall_subjects import RecallSubjectCatalogBuilder
from app.relationship_context import RelationshipContext
from app.world_knowledge import KnowledgeContext
from tests import test_cross_conversation_recent_dialogue as ledger_fixtures
from tests.test_memory_d2_cognitive_steps import KnownSubjects, MinimalRows, recall


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def prompt_turns(request):
    return json.JSONDecoder().raw_decode(
        request.messages[-1].content.split("PRIOR DIALOGUE TURNS JSON: ", 1)[1])[0]


def messages(turns):
    if turns and "role" in turns[0]:
        return len(turns)
    return sum(1 + (len(t["npc_utterances"]) if t.get("npc_utterances")
                    else int(t.get("npc_text") is not None)) for t in turns)


class WireProvider(ledger_fixtures.ScriptedProvider):
    """Pure production payload builder, scripted inference, no HTTP or credentials."""

    def __init__(self, capacity, two_steps=False):
        super().__init__([recall, dict(decision_kind="DIRECT_RESPONSE", utterance="收到。")]
                         if two_steps else None)
        self.context_length = capacity
        self.adapter = DeepSeekOfficialResponsesProvider(
            base_url="https://p1.invalid", api_key="test-only", model="p1-test")

    def budget_payload(self, request):
        return self.adapter.budget_payload(request)


class AllocationCapture(logging.Handler):
    def __init__(self):
        super().__init__()
        self.records = []

    def emit(self, record):
        if record.getMessage().startswith("context_budget="):
            self.records.append(json.loads(record.getMessage().split("=", 1)[1]))


class ContextBudgetP1Tests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.fixture = ledger_fixtures.CrossConversationRecentDialogueTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.capture = AllocationCapture()
        self.logger = logging.getLogger("embodiednpc.backend.context_budget")
        level = self.logger.level
        self.logger.setLevel(logging.INFO)
        self.logger.addHandler(self.capture)
        self.addCleanup(self.logger.setLevel, level)
        self.addCleanup(self.logger.removeHandler, self.capture)
        self.estimator = ContextTokenEstimator()

    def current(self, text="不喜欢。"):
        value = self.fixture.request(999, text=text)
        self.fixture.append(value, text, session="current-session", clock=.5)
        self.current_wire = value
        self.current_request = CognitiveRequest.model_validate(value)
        return self.current_request

    async def build(self, capacity=16384, output=512, *, two_steps=False,
                    request=None, knowledge=None, relationship=None):
        provider = WireProvider(capacity, two_steps)
        memory = (CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()),
                  CanonicalMemoryRetrieval(MinimalRows())) if two_steps else None)
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
            experience_ledger=self.fixture.ledger, context_length=capacity,
            inference_options=InferenceOptions(max_output_tokens=output), memory_recall=memory,
            knowledge_resolver=Mock(resolve=Mock(return_value=knowledge)) if knowledge else None,
            relationship_context_resolver=Mock(resolve=Mock(return_value=relationship))
            if relationship else None)
        request = request or self.current()
        reader = await entry._read_recent_dialogue(request)
        start = len(self.capture.records)
        await entry.run(request)
        for model, allocation in zip(provider.requests, self.capture.records[start:]):
            wire_tokens = self.estimator.estimate_text(encode(provider.budget_payload(model)))
            self.assertEqual(wire_tokens, allocation["estimated_total"])
            self.assertLessEqual(wire_tokens, allocation["input_hard_cap"])
            self.assertLessEqual(wire_tokens + allocation["reserved_output_tokens"] +
                                 allocation["reserved_estimation_tokens"], capacity)
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
            self.assertNotIn("merchant.private", encode(provider.budget_payload(model)))
            self.assertNotIn("cafe-local-player-001", encode(provider.budget_payload(model)))
        self.assertEqual(len(provider.requests), 2 if two_steps else 1)
        self.assertEqual(len(self.capture.records[start:]), len(provider.requests))
        return reader, provider.requests

    def add_continuation(self, number, player, normal, extra):
        req, first, reply = self.fixture.pair(number, text=player, reply=normal,
                                            conversation="old", clock=float(number))
        follow = self.fixture.request(number + 100, conversation="old")
        self.fixture.append(follow, extra, response=True, session="session_old",
            clock=float(number) + .2, changes={"structured_payload": dict(
                initiative="dialogue_continuation", continuation_index=1,
                parent_request_id=first.request_id, parent_event_id=first.source_event_id,
                last_response_event_id=reply.event_id, last_response_seq=reply.experience_seq)})

    async def test_16k_eight_long_reader_turns_fit_final_wire(self):
        for i in range(1, 9):
            self.fixture.pair(i, text=f"第{i}轮" + "甲" * 110, reply="乙" * 110)
        reader, models = await self.build()
        self.assertEqual(16, len(reader))
        self.assertEqual(16, messages(reader))
        self.assertEqual(list(reader), prompt_turns(models[0]))

    async def test_16k_single_long_turn_is_not_lost_to_category_cap(self):
        self.fixture.pair(1, text="甲" * 1000, reply="乙" * 1000)
        reader, models = await self.build()
        self.assertEqual(2, len(reader))
        self.assertEqual(list(reader), prompt_turns(models[0]))

    async def test_16k_pressure_old_long_turn_cannot_evict_new_short_turn(self):
        self.fixture.pair(1, text="甲" * 1000, reply="旧轮结束。")
        self.fixture.pair(2, text="那你觉得呢？", reply="你希望具体解释哪个方面？")
        # Leave room for the measured mandatory contract, while still excluding
        # the old long turn. 7168 output now correctly rejects the bare request.
        reader, models = await self.build(16384, output=6144)
        self.assertEqual(4, len(reader))
        self.assertIn("你希望具体解释哪个方面？", [m["text"] for m in prompt_turns(models[0])])
        self.assertNotIn("甲" * 1000, [m["text"] for m in prompt_turns(models[0])])
        self.assertIn("PRIOR DIALOGUE COVERAGE: partial", models[0].messages[-1].content)

    async def test_latest_npc_question_survives_current_player_short_answer(self):
        self.fixture.pair(1, text="甲" * 1000, reply="旧轮结束。")
        self.fixture.pair(2, text="我的习惯是这样。", reply="你喜欢加糖吗？")
        reader, models = await self.build(16384, output=6144, request=self.current("不喜欢。"))
        kept = prompt_turns(models[0])
        self.assertIn("我的习惯是这样。", [m["text"] for m in kept])
        self.assertEqual("你喜欢加糖吗？", kept[-1]["text"])
        focus = next(line for line in models[0].messages[-1].content.splitlines()
                     if line.startswith("CURRENT TASK BINDING JSON: "))
        self.assertEqual("不喜欢。", json.loads(focus.split(": ", 1)[1])["current_player_text"])

    async def test_long_continuation_is_serialized_once_without_mutating_reader(self):
        self.add_continuation(1, "甲" * 400, "乙" * 1000, "丙" * 1000)
        reader, models = await self.build()
        before = copy.deepcopy(list(reader))
        kept = prompt_turns(models[0])
        self.assertEqual(3, len(kept))
        self.assertNotIn("npc_text", encode(kept))
        self.assertEqual(["乙" * 1000, "丙" * 1000], [m["text"] for m in kept if m["role"] == "npc"])
        legacy = self.fixture.read(self.current_wire, limit=16, max_chars=2400)
        self.assertEqual("乙" * 1000, legacy[0]["npc_text"])
        self.assertEqual(before, list(reader))
        self.assertEqual(1, encode(kept).count("乙" * 1000))
        self.assertEqual(2, encode(legacy).count("乙" * 1000))

    async def test_multiple_turn_continuations_keep_order_and_distinct_sources(self):
        for i in range(1, 6):
            self.add_continuation(i, str(i), f"第{i}段" + "乙" * 474, "丙")
        reader, models = await self.build()
        kept = prompt_turns(models[0])
        self.assertEqual(15, len(reader))
        self.assertEqual(15, messages(kept))
        self.assertEqual([t["seq"] for t in reader], [t["seq"] for t in kept])
        for raw, visible in zip(reader, kept):
            self.assertNotIn("npc_text", visible)
            self.assertEqual(raw["text"], visible["text"])

    async def test_identical_real_npc_utterances_are_not_text_deduplicated(self):
        self.add_continuation(1, "输入", "同一句", "同一句")
        reader, models = await self.build()
        kept = prompt_turns(models[0])
        self.assertEqual(["同一句", "同一句"], [m["text"] for m in kept if m["role"] == "npc"])
        self.assertEqual(2, encode(kept).count("同一句"))
        self.assertEqual(3, encode(self.fixture.read(self.current_wire)).count("同一句"))

    async def test_short_history_at_both_capacities_preserves_reader_contract(self):
        for i in range(1, 9):
            self.fixture.pair(i, text=f"输入{i}", reply=f"回复{i}")
        request = self.current()
        for cap, count in ((8192, 8), (16384, 16)):
            with self.subTest(cap=cap):
                if cap == 8192:
                    provider = WireProvider(cap)
                    entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                        context_length=cap, experience_ledger=self.fixture.ledger,
                        inference_options=InferenceOptions(max_output_tokens=512))
                    reader = await entry._read_recent_dialogue(request)
                    self.assertEqual(count, reader.target_messages)
                    with self.assertRaises(ContextBudgetExceeded):
                        await entry.run(request)
                    self.assertEqual([], provider.requests)
                    continue
                reader, models = await self.build(cap, request=request)
                self.assertEqual(count, reader.target_messages)
                self.assertLessEqual(messages(reader), count + 2)
                self.assertEqual(reader.visible(m["seq"] for m in reader), prompt_turns(models[0]))

    async def test_16k_pressure_long_history_keeps_whole_utterances_with_explicit_partial_context(self):
        self.fixture.pair(1, text="甲" * 550, reply="乙" * 550)
        reader, models = await self.build(16384, output=6144)
        self.assertEqual(2, len(reader))
        kept = prompt_turns(models[0])
        self.assertLess(len(kept), len(reader))
        for message in kept:
            self.assertIn(message["text"], ("甲" * 550, "乙" * 550))
            if message["role"] == "npc":
                self.assertTrue(message["context_incomplete"])
        self.assertIn("PRIOR DIALOGUE COVERAGE: partial", models[0].messages[-1].content)

    async def test_output_reserve_cannot_displace_mandatory_contract(self):
        request = self.current()
        provider = WireProvider(16384)
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384,
            inference_options=InferenceOptions(max_output_tokens=7168))
        with self.assertRaises(ContextBudgetExceeded) as error:
            await entry.run(request)
        self.assertEqual([], provider.requests)
        self.assertTrue(error.exception.allocation.safe_failure)
        self.assertEqual(7680, error.exception.allocation.input_hard_cap)
        self.assertGreater(error.exception.allocation.estimated_total, 7680)

    async def test_two_steps_keep_sources_and_complete_final_wire_budget(self):
        for i in range(1, 9):
            self.fixture.pair(i, text="输入" + "甲" * 100, reply="乙" * 100)
        knowledge = KnowledgeContext(status="OK", items=tuple(dict(
            knowledge_id=f"k{i}", content="有条件的参考知识。" * 15, version=1) for i in range(3)))
        social = RelationshipContext(interpretations=("历史主观解释，不授权动作。" * 10,),
            sources=({"reflection_id": "test", "generated_at": 1.0},),
            truncated=False, policy_version="p1-test")
        reader, models = await self.build(two_steps=True, knowledge=knowledge, relationship=social)
        self.assertEqual(16, messages(reader))
        for model in models:
            self.assertIn("k0", model.messages[-1].content)
            self.assertIn(social.interpretations[0], model.messages[-1].content)
        self.assertIn("玩家最喜欢的颜色是蓝色", models[1].messages[-1].content)
        self.assertNotIn("MEMORY_RECALL", encode(models[1].response_schema))

    async def test_output_reserve_changes_optional_retention_not_mandatory_context(self):
        for i in range(1, 9):
            self.fixture.pair(i, text="甲" * 140, reply="乙" * 140)
        request = self.current()
        counts = []
        for output in (512, 1024, 4096, 6144):
            reader, models = await self.build(output=output, request=request)
            counts.append(len(prompt_turns(models[0])))
            self.assertEqual(output, self.capture.records[-1]["reserved_output_tokens"])
        self.assertEqual(sorted(counts, reverse=True), counts)
        self.assertLess(counts[-1], counts[0])

    async def test_complex_persona_is_preserved_or_safe_failure_before_call(self):
        request = self.current()
        character = CognitiveCharacterContext.model_validate({**request.context.character.model_dump(),
            "role_summary": "甲" * 240, "backstory": "乙" * 600,
            "personality": "丙" * 600, "speaking_style": "丁" * 480, "boundaries": "戊" * 600})
        request = request.model_copy(update={"context": request.context.model_copy(
            update={"character": character})})
        _, models = await self.build(request=request)
        self.assertEqual(character, CognitiveModelRequestFactory.context_from(models[0]).character)
        provider = WireProvider(8192)
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=8192,
            inference_options=InferenceOptions(max_output_tokens=512))
        with self.assertRaises(ContextBudgetExceeded):
            await entry.run(request)
        self.assertEqual([], provider.requests)

    async def test_current_perception_is_mandatory_under_history_pressure(self):
        request = self.current()
        entities = tuple(PerceivedEntity(entity_id=f"visible_{i}", kind="item", display_name=f"物品{i}",
            aliases=(), semantic_tags=("item",), visibility="current_visible", capabilities=()) for i in range(16))
        request = request.model_copy(update={"context": request.context.model_copy(update={
            "perception": request.context.perception.model_copy(update={"entities": entities})})})
        self.fixture.pair(1, text="甲" * 1000, reply="乙" * 1000)
        _, models = await self.build(request=request)
        self.assertEqual(entities, CognitiveModelRequestFactory.context_from(models[0]).perception.entities)

    async def test_provider_protocol_overhead_is_counted_before_call(self):
        request = self.current()
        def payload(model):
            return {**WireProvider(8192).budget_payload(model), "protocol": "额" * 4000}
        with self.assertRaises(ContextBudgetExceeded) as error:
            CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=8192,
                inference_options=InferenceOptions(max_output_tokens=512), budget_payload_builder=payload)
        self.assertTrue(error.exception.allocation.safe_failure)
        self.assertGreater(error.exception.allocation.estimated_total, error.exception.allocation.input_hard_cap)

    async def test_actual_http_body_matches_the_budgeted_final_wire(self):
        self.fixture.pair(1, text="甲" * 1000, reply="乙" * 1000)
        request = self.current()
        sent = []
        def respond(req):
            sent.append(json.loads(req.content))
            return httpx.Response(200, json={"status": "completed", "output": [{
                "type": "message", "role": "assistant", "content": [{"type": "output_text",
                "text": '{"decision_kind":"DIRECT_RESPONSE","utterance":"收到。"}'}]}]})
        provider = DeepSeekOfficialResponsesProvider(base_url="https://p1.invalid", api_key="test-only",
            model="p1-test", transport=httpx.MockTransport(respond))
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
            experience_ledger=self.fixture.ledger, context_length=16384,
            inference_options=InferenceOptions(max_output_tokens=512))
        await entry.run(request)
        self.assertEqual(1, len(sent))
        self.assertEqual(512, sent[0]["max_output_tokens"])
        self.assertEqual(self.capture.records[-1]["estimated_total"],
                         self.estimator.estimate_text(encode(sent[0])))
        turns = json.JSONDecoder().raw_decode(sent[0]["input"][-1]["content"].split(
            "PRIOR DIALOGUE TURNS JSON: ", 1)[1])[0]
        self.assertEqual(2, messages(turns))

    async def test_16k_pressure_whole_recall_omission_keeps_current_perception_and_rejects_stale_action(self):
        from tests.test_memory_d2_cognitive_steps import MemoryD2Tests, cognitive_request
        from tests.test_action_proposal_validation import _decision
        from app.action_proposal_validator import ActionProposalValidationError
        fixture = MemoryD2Tests()
        entry = fixture.entry([recall, _decision({"target": "cup_99", "destination": "player_01"}).model_dump()])
        # The shared D2 fixture is 16K, not 8K. Explicit output pressure leaves
        # the mandatory request intact but excludes this complete evidence item.
        entry = CognitiveBackendEntry(fixture.provider, fixture.executor, memory_recall=fixture.adapter,
            context_length=16384, inference_options=InferenceOptions(max_output_tokens=6144))
        fixture.rows.summary = "历史记录提到 cup_99，但它已不在当前可见范围"
        with self.assertRaises(ActionProposalValidationError):
            await entry.run(cognitive_request("把以前见过的杯子拿给我"))
        self.assertEqual(2, len(fixture.provider.requests))
        prompt = fixture.provider.requests[1].messages[-1].content
        self.assertIn('"entity_id":"cup_01"', prompt)
        self.assertNotIn('"entity_id":"cup_99"', prompt)
        outcome = json.JSONDecoder().raw_decode(prompt.split(
            "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ", 1)[1])[0]
        self.assertEqual("EVIDENCE_BUDGET_EXHAUSTED", outcome["status"])
        self.assertEqual([], outcome["evidence"]["items"])
        self.assertTrue(outcome["evidence"]["not_action_authorization"])

    def test_capacity_policy_keeps_output_and_uncertainty_reserves_separate(self):
        for cap, output in ((8192, 512), (16384, 512), (16384, 2048)):
            policy = ContextBudgetPolicy.cognition(cap, output)
            self.assertEqual(output, policy.response_reserve)
            self.assertGreater(policy.estimation_reserve, 512)
            self.assertEqual(cap, policy.input_hard_cap + policy.response_reserve + policy.estimation_reserve)
            self.assertEqual(policy.input_hard_cap, policy.recent_target_ceiling)
        self.assertGreater(ContextBudgetPolicy.cognition(16384, 512).estimation_reserve,
                           ContextBudgetPolicy.cognition(8192, 512).estimation_reserve)

    def test_margin_change_deterministically_trims_optional_and_explains_why(self):
        results = []
        for margin in (1024, 1536):
            policy = ContextBudgetPolicy.cognition(8192, 512, estimation_reserve=margin)
            allocator = ContextBudgetAllocator(policy)
            result = allocator.allocate({"required": "甲" * 5200}, (
                AtomicContextGroup("recent:latest", "recent", "乙" * 300, 20, 0),),
                lambda ids: "甲" * 5200 + ("乙" * 300 if ids else ""))
            results.append(result)
        self.assertEqual(("recent:latest",), results[0].included_components)
        self.assertEqual((), results[1].included_components)
        self.assertEqual({"recent:latest": "global_input_cap"}, results[1].drop_reasons)

    async def test_provider_capacity_is_minimum_of_config_and_adapter(self):
        for configured, declared, expected in ((16384, 8192, 8192), (8192, 16384, 8192)):
            provider = WireProvider(declared)
            entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=configured)
            self.assertEqual(expected, entry._context_length)

    async def test_far_cross_turn_semantic_dependency_remains_explicitly_bounded(self):
        self.fixture.pair(1, text="我的习惯", reply="要加糖吗？")
        # Beyond both the default 16-message target and C1's two boundary slots.
        for i in range(2, 12):
            self.fixture.pair(i, text=f"换话题{i}", reply="其他内容")
        reader, models = await self.build(request=self.current("之前那个问题，不喜欢。"))
        self.assertNotIn("要加糖吗？", encode(reader))
        self.assertNotIn("要加糖吗？", encode(prompt_turns(models[0])))
        self.assertIn("PRIOR DIALOGUE COVERAGE: partial", models[0].messages[-1].content)
