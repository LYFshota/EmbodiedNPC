"""P2-A contract checks: scripted inference and pure wire builders; no service/config/DB access."""
import json
import socket
import unittest
from unittest.mock import patch

from pydantic import ValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.action_proposal_validator import ActionProposalValidationError
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.cognitive_step_models import CognitiveStepResult, DialogueContinuationStep, RecallOutcome
from app.context_budget_allocator import ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveDecision, CognitiveRequest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests.test_memory_d2_cognitive_steps import cognitive_request, ScriptedProvider, KnownSubjects, MinimalRows, recall


def request(*, candidates=True, text="薇薇，能帮我拿杯子吗？"):
    value = cognitive_request(text).model_dump()
    value["context"]["daily_behavior"] = dict(
        context_token="captured-local-context", current_mode="rest", mode_revision=1, profile_revision=1,
        condition_revision=1, objective="进行个人休闲；可根据职责和当前请求恢复工作。", tendency="休息、观景和普通交谈。",
        considered_activities=["window_idle", "natural_idle", "observe_environment", "dialogue"],
        candidates=[dict(target_mode="work", condition="根据店员职责和当前合理请求，自愿恢复工作；仍需现有动作许可。")]
        if candidates else [], constraints=["模式目标不构成当前世界事实或正式动作许可。", "活动自行检查位置和导航条件；不可用时安全待机。",
            "提议只用于当前玩家请求，任务/导航/Carry 冲突或状态变化会拒绝。", "模式意图可附带普通回复或合法 ActionProposal；动作另行验证和接收，完成须有真实 ActionOutcome。"])
    return CognitiveRequest.model_validate(value)


class WireScriptedProvider(ScriptedProvider):
    def __init__(self, scripts):
        super().__init__(scripts)
        self.adapter = DeepSeekOfficialResponsesProvider(base_url="https://p2a.invalid", api_key="test-only", model="p2a-test")

    def budget_payload(self, value):
        return self.adapter.budget_payload(value)


def service_zone_request():
    value = request().model_dump()
    event = value["context"]["current_event"]
    event.update(event_type="player.dwelled_in_service_zone", area_entity_id="area_cafe_counter",
        trace_id=value["trace_id"], source_observation_id=None,
        payload=[dict(key=k, value=v) for k, v in dict(zone_id="counter_service_zone",
            condition_token="a" * 32, condition_revision="1", dwell_seconds="4.5").items()])
    value["trigger"]["reason"] = event["event_type"]
    value["context"]["trigger"] = value["trigger"].copy()
    return CognitiveRequest.model_validate(value)


class ServiceZoneContractTests(unittest.IsolatedAsyncioTestCase):
    async def test_existing_mock_provider_uses_silent_system_contract(self):
        from app.providers import MockProvider
        result = await CognitiveBackendEntry(MockProvider(), PrioritizedModelExecutor(), context_length=16384).run(service_zone_request())
        self.assertEqual("NO_ACTION", result.decision.decision_kind)
        self.assertIsNone(result.decision.utterance)
        self.assertIsNone(result.validated_goal)

    async def test_silent_terminal_contract_and_original_16k_wire(self):
        from app.cognitive_step_models import SystemModeStep
        from app.world_action_eligibility import may_propose_new_world_action
        for decision in (dict(decision_kind="NO_ACTION"), dict(decision_kind="DAILY_MODE_INTENT", target_daily_mode="work")):
            provider = WireScriptedProvider([decision])
            current = service_zone_request()
            result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(current)
            self.assertEqual(decision["decision_kind"], result.decision.decision_kind)
            self.assertIsNone(result.validated_goal)
            self.assertFalse(may_propose_new_world_action(current))
            self.assertEqual(1, len(provider.requests))
            model = provider.requests[0]
            self.assertEqual("SystemModeStep", model.response_schema["title"])
            self.assertEqual(1024, model.inference_options.max_output_tokens)
            self.assertFalse(model.inference_options.think)
            self.assertEqual(0, model.inference_options.temperature)
            self.assertEqual("rest", CognitiveModelRequestFactory.context_from(model).daily_behavior.current_mode)
            wire = json.dumps(provider.budget_payload(model), ensure_ascii=False, separators=(",", ":"))
            self.assertLessEqual(ContextTokenEstimator().estimate_text(wire), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
            self.assertNotIn("MEMORY_RECALL", json.dumps(model.response_schema))
        for illegal in [dict(decision_kind="DIRECT_RESPONSE", utterance="你好"),
            dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="bring_object", bindings=dict(target="cup_01", destination="player_01"))),
            dict(decision_kind="NO_ACTION", episode_directive="CONTINUE"),
            dict(decision_kind="DAILY_MODE_INTENT", target_daily_mode="work", utterance="我来帮你"),
            dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="杯子"))]:
            with self.subTest(illegal=illegal), self.assertRaises(ValidationError):
                await CognitiveBackendEntry(ScriptedProvider([illegal]), PrioritizedModelExecutor(), context_length=16384).run(service_zone_request())
        with self.assertRaises(ValidationError):
            CognitiveStepResult.model_validate(dict(decision_kind="DAILY_MODE_INTENT", target_daily_mode="work"))

    async def test_invalid_source_rejected_before_provider(self):
        for field, replacement in [("source_entity_id", "cup_01"), ("source_observation_id", "fake_visual")]:
            value = service_zone_request().model_dump(); value["context"]["current_event"][field] = replacement
            provider = ScriptedProvider([])
            with self.assertRaisesRegex(ValueError, "SYSTEM_MODE_SOURCE_INVALID"):
                await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(CognitiveRequest.model_validate(value))
            self.assertEqual([], provider.requests)


class DailyBehaviorCognitionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        guard = patch.object(socket.socket, "connect", side_effect=AssertionError("P2-A tests must not connect"))
        guard.start(); self.addCleanup(guard.stop)

    async def test_combined_work_and_action_preserves_both_authority_checks(self):
        combined = dict(decision_kind="ACTION_PROPOSAL", target_daily_mode="work",
            utterance="我恢复工作，开始处理取杯请求。", action_proposal=dict(action_id="bring_object",
                bindings=dict(target="cup_01", destination="player_01")))
        self.assertEqual(CognitiveStepResult.model_validate(combined).root.target_daily_mode, "work")
        result = await CognitiveBackendEntry(ScriptedProvider([combined]), PrioritizedModelExecutor(),
            context_length=16384).run(request())
        self.assertEqual(result.decision.target_daily_mode, "work")
        self.assertEqual(result.validated_goal.type, "bring_object")
        with self.assertRaisesRegex(ValueError, "DAILY_MODE_NOT_ELIGIBLE"):
            await CognitiveBackendEntry(ScriptedProvider([combined]), PrioritizedModelExecutor(),
                context_length=16384).run(request(candidates=False))
        value = request().model_dump()
        value["context"]["capabilities"]["available_actions"] = []
        with self.assertRaises(ActionProposalValidationError):
            await CognitiveBackendEntry(ScriptedProvider([combined]), PrioritizedModelExecutor(),
                context_length=16384).run(CognitiveRequest.model_validate(value))
        value = request().model_dump()
        value["context"]["current_event"]["event_type"] = "entity.became_visible"
        with self.assertRaisesRegex(ValueError, "DAILY_MODE_NOT_ELIGIBLE"):
            await CognitiveBackendEntry(ScriptedProvider([combined]), PrioritizedModelExecutor(),
                context_length=16384).run(CognitiveRequest.model_validate(value))

    def test_contract_allows_direct_or_action_intent_and_legacy_null_context(self):
        self.assertEqual(CognitiveDecision(decision_kind="DIRECT_RESPONSE", utterance="我先恢复工作。",
                                          target_daily_mode="work").target_daily_mode, "work")
        for value in [dict(decision_kind="NO_ACTION", target_daily_mode="work"),
                      dict(decision_kind="DIRECT_RESPONSE", utterance="你好", target_daily_mode="unknown"),
                      dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="偏好"), target_daily_mode="work")]:
            with self.subTest(value=value), self.assertRaises(ValidationError):
                CognitiveStepResult.model_validate(value)
        with self.assertRaises(ValidationError):
            DialogueContinuationStep.model_validate(dict(decision_kind="DIRECT_RESPONSE", utterance="继续说。", target_daily_mode="work"))
        with self.assertRaises(ValidationError):
            DialogueContinuationStep.model_validate(dict(decision_kind="ACTION_PROPOSAL", target_daily_mode="work",
                action_proposal=dict(action_id="bring_object", bindings=dict(target="cup_01", destination="player_01"))))
        self.assertIsNone(cognitive_request().context.daily_behavior)

    async def test_service_request_can_propose_work_without_world_action(self):
        def decide(model):
            context = CognitiveModelRequestFactory.context_from(model)
            self.assertEqual(context.daily_behavior.current_mode, "rest")
            self.assertEqual(context.daily_behavior.candidates[0].target_mode, "work")
            self.assertEqual(context.behavior_mode.mode_id, "working")
            self.assertIn("bring_object", [a.action_id for a in context.capabilities.available_actions])
            self.assertIn("not promise delivery", model.messages[0].content)
            return dict(decision_kind="DIRECT_RESPONSE", utterance="我先恢复工作。", target_daily_mode="work")
        provider = ScriptedProvider([decide])
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(request())
        self.assertEqual(result.decision.target_daily_mode, "work")
        self.assertIsNone(result.validated_goal)
        self.assertEqual(len(provider.requests), 1)

    async def test_greeting_and_no_transition_condition_keep_rest(self):
        for current, reply in [(request(text="你好，薇薇。"), "你好，休息时聊聊也好。"),
                               (request(candidates=False), "我现在仍在休息，暂不恢复工作。")]:
            provider = ScriptedProvider([dict(decision_kind="DIRECT_RESPONSE", utterance=reply)])
            result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(current)
            self.assertIsNone(result.decision.target_daily_mode)
            self.assertEqual(current.context.daily_behavior.current_mode, "rest")
            self.assertIsNone(result.validated_goal)

    async def test_existing_action_authority_is_independent_of_life_mode(self):
        action = dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="bring_object",
            bindings=dict(target="cup_01", destination="player_01")))
        provider = ScriptedProvider([action])
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(request())
        self.assertEqual(result.validated_goal.type, "bring_object")
        self.assertIsNone(result.decision.target_daily_mode)
        value = request().model_dump()
        value["context"]["daily_behavior"]["current_mode"] = "work"
        value["context"]["daily_behavior"]["candidates"] = []
        value["context"]["capabilities"]["available_actions"] = []
        with self.assertRaises(ActionProposalValidationError):
            await CognitiveBackendEntry(ScriptedProvider([action]), PrioritizedModelExecutor(), context_length=16384).run(
                CognitiveRequest.model_validate(value))

    async def test_absent_candidates_context_or_wrong_event_forbid_intent(self):
        values = [request(candidates=False).model_dump(), request().model_dump(), request().model_dump(), request().model_dump()]
        values[1]["context"]["daily_behavior"] = None
        values[2]["context"]["current_event"]["event_type"] = "entity.became_visible"
        values[3]["context"]["current_event"]["target_entity_id"] = "npc_cafe_regular_01"
        for value in values:
            provider = ScriptedProvider([dict(decision_kind="DIRECT_RESPONSE", utterance="我恢复工作。", target_daily_mode="work")])
            with self.subTest(value=value["context"]["current_event"]), self.assertRaisesRegex(ValueError, "DAILY_MODE_NOT_ELIGIBLE"):
                await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(CognitiveRequest.model_validate(value))

    async def test_two_step_recall_keeps_same_mode_context_and_complete_16k_wire(self):
        current = request(text="你还记得我的偏好吗？")
        provider = WireScriptedProvider([recall, dict(decision_kind="DIRECT_RESPONSE", utterance="我先恢复工作，再继续聊。", target_daily_mode="work")])
        memory = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()), CanonicalMemoryRetrieval(MinimalRows()))
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384,
            inference_options=InferenceOptions(max_output_tokens=1024), memory_recall=memory).run(current)
        self.assertEqual(result.decision.target_daily_mode, "work")
        self.assertEqual(len(provider.requests), 2)
        policy = ContextBudgetPolicy.cognition(16384, 1024)
        for model in provider.requests:
            self.assertEqual(CognitiveModelRequestFactory.context_from(model).daily_behavior, current.context.daily_behavior)
            complete_wire = json.dumps(provider.budget_payload(model), ensure_ascii=False, separators=(",", ":"))
            self.assertIn("target_daily_mode", complete_wire)
            self.assertIn("captured-local-context", complete_wire)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(complete_wire), policy.input_hard_cap)
        with self.assertRaises(ValidationError):
            CognitiveDecision.model_validate(dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="again")))

    async def test_recall_then_combined_action_uses_two_steps_and_full_16k_wire(self):
        terminal = dict(decision_kind="ACTION_PROPOSAL", target_daily_mode="work",
            action_proposal=dict(action_id="bring_object", bindings=dict(target="cup_01", destination="player_01")))
        current = request()
        provider = WireScriptedProvider([recall, terminal])
        memory = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()), CanonicalMemoryRetrieval(MinimalRows()))
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384,
            inference_options=InferenceOptions(max_output_tokens=1024), memory_recall=memory).run(current)
        self.assertEqual(result.validated_goal.type, "bring_object")
        self.assertEqual(result.decision.target_daily_mode, "work")
        self.assertEqual(len(provider.requests), 2)
        policy = ContextBudgetPolicy.cognition(16384, 1024)
        for model in provider.requests:
            wire = json.dumps(provider.budget_payload(model), ensure_ascii=False, separators=(",", ":"))
            self.assertEqual(CognitiveModelRequestFactory.context_from(model).daily_behavior, current.context.daily_behavior)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(wire), policy.input_hard_cap)


    def test_empty_item_target_guidance_survives_both_final_16k_wires(self):
        value = request().model_dump()
        value['context']['perception']['entities'] = [dict(entity_id='player_01', kind='player',
            display_name='玩家', aliases=[], semantic_tags=[], visibility='remembered', capabilities=[])]
        current = CognitiveRequest.model_validate(value)
        adapter = DeepSeekOfficialResponsesProvider(base_url='https://p2bc.invalid', api_key='test-only', model='p2bc-test')
        for outcome in (None, RecallOutcome(status='NO_MEMORY_FOUND')):
            with self.subTest(step=0 if outcome is None else 1):
                model = CognitiveModelRequestFactory.create_event(current, recall_outcome=outcome,
                    knowledge_context_limit=16384, budget_payload_builder=adapter.budget_payload,
                    inference_options=InferenceOptions(temperature=0, max_output_tokens=1024))
                wire = adapter.budget_payload(model)
                system = wire['input'][0]['content']
                self.assertIn('bring_object.target needs trusted item identity', system)
                self.assertIn('bring_object.destination needs player identity', system)
                self.assertIn('unknown is not item evidence', system)
                self.assertIn('If no trustworthy item can be bound, use DIRECT_RESPONSE', system)
                self.assertIn('Action availability is not target availability', system)
                decoded = CognitiveModelRequestFactory.context_from(model)
                self.assertEqual(decoded.perception, current.context.perception)
                self.assertEqual([], [e for e in decoded.perception.entities if e.kind == 'item'])
                self.assertEqual('bring_object', decoded.capabilities.available_actions[0].action_id)
                self.assertNotIn('cup_01', wire['input'][1]['content'])
                self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False)),
                    ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)

    def test_remembered_and_unknown_item_evidence_is_preserved_in_wire(self):
        for kind, capabilities in [('item', ['pick_up']), ('item', []), ('unknown', [])]:
            with self.subTest(kind=kind, capabilities=capabilities):
                value = request().model_dump()
                value['context']['perception']['entities'][0].update(entity_id='avatar_01', kind=kind,
                    visibility='remembered', capabilities=capabilities)
                current = CognitiveRequest.model_validate(value)
                model = CognitiveModelRequestFactory.create_event(current, knowledge_context_limit=16384)
                decoded = CognitiveModelRequestFactory.context_from(model)
                self.assertEqual(decoded.perception, current.context.perception)
                self.assertEqual(decoded.perception.entities[0].entity_id, 'avatar_01')
                self.assertIn('current visibility is not required', model.messages[0].content)
                self.assertIn('Item identity does not prove PickUp capability', model.messages[0].content)


if __name__ == "__main__":
    unittest.main()
