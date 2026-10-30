"""Offline final-wire contract checks, not model-semantic acceptance tests."""
import json
import unittest
from unittest.mock import patch

from app.action_proposal_validator import ActionProposalValidator
from app.cognitive_step_models import CognitiveStepResult, RecallOutcome, TerminalCognitiveStep
from app.context_budget_allocator import ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveDecision, CognitiveRequest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from tests.test_clean_counter import request as cleaning_request, decision as cleaning_decision
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


CASES = (
    ("A", False, "薇薇，帮我把旁边的柜台擦一下吧。"),
    ("B", True, "薇薇，这边柜台是不是该收拾一下啦？方便的话帮我擦一擦吧。"),
    ("C", False, "薇薇，我怎么感觉那块柜台有点脏呀，你确定不用擦一擦吗？"),
)


class CognitionFactCommitmentTests(unittest.TestCase):
    def setUp(self):
        for target in ("socket.socket.connect", "sqlite3.connect"):
            guard = patch(target, side_effect=AssertionError("Wire checks must remain offline/storage-free"))
            guard.start()
            self.addCleanup(guard.stop)
        self.adapter = DeepSeekOfficialResponsesProvider(
            base_url="https://fact-contract.invalid", api_key="test-only", model="offline")

    def model(self, request, **kwargs):
        return CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH,
            budget_payload_builder=self.adapter.budget_payload,
            inference_options=InferenceOptions(temperature=0, think=False, max_output_tokens=1024), **kwargs)

    def assert_contract(self, model):
        wire = self.adapter.budget_payload(model)
        system = wire["input"][0]["content"]
        self.assertIn("player.utterance payload expresses the player's intent or report, not verified world state", system)
        self.assertIn("trusted current Unity projections establish the confirmed state", system)
        self.assertIn("without either confirming it or dismissing it as false", system)
        self.assertIn("Do not invent having inspected, noticed or confirmed something", system)
        self.assertIn("its current need and action preconditions are confirmed", system)
        self.assertIn("without claiming a world task is starting, in progress or completed", system)
        self.assertIn("only a real GoalOutcome supports completion", system)
        self.assertNotIn("counter_cafe_clean_01", system, "The policy must not special-case this entity.")
        self.assertNotIn("Treat events as structured world evidence", system)
        user = wire["input"][-1]["content"]
        self.assertIn("环境报告不自动覆盖可信事实", user)
        self.assertIn("无本轮合法动作提案，不用回复承诺执行或完成", user)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False,
            separators=(",", ":"))), ContextBudgetPolicy.cognition(DEFAULT_COGNITIVE_CONTEXT_LENGTH, 1024).input_hard_cap)
        self.assertEqual({"effort": "none"}, wire["reasoning"])
        self.assertEqual(1024, wire["max_output_tokens"])
        return wire

    def test_abc_state_manifest_and_fact_contract_survive_both_final_wires(self):
        for name, dirty, text in CASES:
            value = cleaning_request().model_dump()
            value["context"]["counter_work"].update(needs_cleaning=dirty, condition_revision=int(dirty))
            for item in value["context"]["current_event"]["payload"]:
                if item["key"] == "text":
                    item["value"] = text
            request = CognitiveRequest.model_validate(value)
            for outcome in (None, RecallOutcome(status="NO_MEMORY_FOUND")):
                with self.subTest(case=name, step=int(outcome is not None)):
                    model = self.model(request, cognition_phase=1 if outcome else 0, recall_outcome=outcome)
                    self.assert_contract(model)
                    self.assertEqual(request.context, CognitiveModelRequestFactory.context_from(model))
                    self.assertIn("clean_counter", [a.action_id for a in request.context.capabilities.available_actions])
                    self.assertEqual((CognitiveStepResult if outcome is None else TerminalCognitiveStep).model_json_schema(),
                                     model.response_schema)

    def test_dirty_goal_remains_legal_without_new_permission_or_schema(self):
        request = cleaning_request()
        model = self.model(request)
        self.assert_contract(model)
        goal = ActionProposalValidator().validate_and_adapt(
            CognitiveDecision.model_validate(cleaning_decision()), request.context)
        self.assertEqual("clean_counter", goal.type)
        self.assertEqual(request.context.counter_work.target_entity_id, goal.target_entity_id)

    def test_optional_history_pressure_cannot_evict_rules_or_current_fact(self):
        request = cleaning_request()
        model = self.model(request, recent_dialogue=[dict(turn_index=i, input_seq=i,
            player_text="ordinary conversation " * 300, npc_text="prior reply " * 300,
            evidence_status="paired") for i in range(1, 7)])
        self.assert_contract(model)
        self.assertEqual(request.context.counter_work, CognitiveModelRequestFactory.context_from(model).counter_work)
