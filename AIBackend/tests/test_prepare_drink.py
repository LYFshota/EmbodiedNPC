"""Offline contracts and full provider-wire checks; no application startup or formal storage."""
import json
import socket
import sqlite3
import unittest
from unittest.mock import patch

from pydantic import ValidationError
from app.action_catalog import PREPARE_DRINK_REGISTRATION
from app.action_proposal_validator import ActionProposalValidator, ActionProposalValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_step_models import CognitiveStepResult
from app.context_budget_allocator import ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveContextSnapshot, CognitiveDecision, CognitiveRequest, GoalProposal
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions, ModelResult
from tests.test_action_proposal_validation import _context, _decision
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


def context(agent="npc_cafe_01"):
    value = _context(agent_id=agent).model_dump()
    descriptor = PREPARE_DRINK_REGISTRATION.descriptor
    value["capabilities"]["available_actions"] = [dict(action_id=descriptor.action_id,
        description=descriptor.description, bindings=[dict(name=b.name, kind=b.kind,
            required=b.required, enum_values=b.enum_values) for b in descriptor.binding_schema])]
    return CognitiveContextSnapshot.model_validate(value)


def decision(bindings=None, **extras):
    return dict(decision_kind="ACTION_PROPOSAL", utterance="我去准备，请稍等。",
        action_proposal=dict(action_id="prepare_drink", bindings=bindings if bindings is not None else dict(drink_type="latte")), **extras)


def request(system=False):
    value = context().model_dump()
    value["current_event"]["payload"] = [dict(key="text", value="请帮我做一杯拿铁")]
    if system:
        value["current_event"].update(event_type="player.dwelled_in_service_zone", trace_id="trace_drink",
            payload=[dict(key=k, value=v) for k, v in dict(zone_id="counter_service_zone",
                condition_token="a"*32, condition_revision="1", dwell_seconds="4.5").items()])
        value["trigger"]["reason"] = "player.dwelled_in_service_zone"
    return CognitiveRequest.model_validate(dict(message_type="cognitive_request", schema_version="1.0",
        request_id="request_drink", trace_id="trace_drink", trigger=value["trigger"], context=value,
        run=dict(run_id=value["run_id"], agent_entity_id=value["agent_entity_id"], trigger_id="trigger_01",
            trigger_type="event", started_at=10.0)))


class ScriptedDrinkProvider:
    name = "test"
    model = "prepare-drink-scripted"
    def __init__(self, result):
        self.result = result
        self.requests = []
        self.adapter = DeepSeekOfficialResponsesProvider(base_url="https://drink.invalid", api_key="test-only", model=self.model)
    def budget_payload(self, request):
        return self.adapter.budget_payload(request)
    async def generate(self, request):
        self.requests.append(request)
        return ModelResult(text=json.dumps(self.result, ensure_ascii=False), finish_reason="stop")


class PrepareDrinkTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        for target in ("socket.socket.connect", "sqlite3.connect"):
            guard = patch(target, side_effect=AssertionError("prepare_drink tests must remain offline and storage-free"))
            guard.start(); self.addCleanup(guard.stop)

    def test_registered_enum_values_and_step_goal_roundtrip(self):
        for drink in PREPARE_DRINK_REGISTRATION.descriptor.binding_schema[0].enum_values:
            with self.subTest(drink=drink):
                step = CognitiveStepResult.model_validate(decision(dict(drink_type=drink))).root
                goal = ActionProposalValidator().validate_and_adapt(step, context())
                self.assertEqual(GoalProposal(type="prepare_drink", drink_type=drink), goal)
                self.assertIsNone(goal.target_entity_id)
                self.assertIsNone(goal.destination_entity_id)

    def test_missing_extra_unsupported_bindings_and_types_rejected(self):
        for bindings in ({}, dict(drink_type="unregistered_drink"), dict(drink_type="latte", recipient="player_01"),
                         dict(drink_type="LATTE"), dict(drink_type=1), dict(drink_type=True),
                         dict(drink_type=[]), dict(drink_type=None), dict(type="latte")):
            with self.subTest(bindings=bindings), self.assertRaises((ValidationError, ActionProposalValidationError)):
                d = CognitiveDecision.model_validate(decision(bindings))
                ActionProposalValidator().validate_and_adapt(d, context())
        for args in (dict(), dict(drink_type="unregistered_drink"), dict(drink_type="latte", target_entity_id="cup_01"),
                     dict(drink_type="latte", recipient="player_01")):
            with self.subTest(args=args), self.assertRaises(ValidationError):
                GoalProposal(type="prepare_drink", **args)

    def test_manifest_enum_tamper_and_unauthorized_npcs_rejected(self):
        d = CognitiveDecision.model_validate(decision())
        for agent in ("npc_cafe_regular_01", "npc_cafe_companion_01", "npc_cafe_quiet_01"):
            with self.subTest(agent=agent), self.assertRaises(ActionProposalValidationError):
                ActionProposalValidator().validate_and_adapt(d, context(agent))
        for replacement in ([], ["latte"], [*PREPARE_DRINK_REGISTRATION.descriptor.binding_schema[0].enum_values, "unregistered_drink"]):
            v = context().model_dump()
            v["capabilities"]["available_actions"][0]["bindings"][0]["enum_values"] = replacement
            with self.subTest(values=replacement), self.assertRaises((ValidationError, ActionProposalValidationError)):
                ActionProposalValidator().validate_and_adapt(d, CognitiveContextSnapshot.model_validate(v))
        for field in ("capabilities", "relevant_player"):
            v = context().model_dump(); v[field] = None
            with self.assertRaises(ActionProposalValidationError):
                ActionProposalValidator().validate_and_adapt(d, CognitiveContextSnapshot.model_validate(v))

    def test_original_bring_object_entity_scope_remains(self):
        self.assertEqual("cup_01", ActionProposalValidator().validate_and_adapt(_decision(), _context()).target_entity_id)
        with self.assertRaises(ActionProposalValidationError):
            ActionProposalValidator().validate_and_adapt(_decision(dict(target="latte", destination="player_01")), _context())
        with self.assertRaises(ValidationError):
            GoalProposal(type="bring_object", target_entity_id="cup_01", destination_entity_id="player_01", drink_type="latte")

    async def test_scripted_full_16k_provider_wire_and_terminal_goal(self):
        for mode in (None, "work"):
            current = request()
            # Existing P2-B daily context is optional; pure Work and combined Work remain one action inference.
            if mode:
                from tests.test_daily_behavior_cognition import request as daily_request
                daily = daily_request().model_dump()
                daily["context"]["capabilities"] = current.context.capabilities.model_dump()
                current = CognitiveRequest.model_validate(daily)
            scripted = decision(**({"target_daily_mode": mode} if mode else {}))
            provider = ScriptedDrinkProvider(scripted)
            result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384,
                inference_options=InferenceOptions(max_output_tokens=1024)).run(current)
            self.assertEqual("latte", result.validated_goal.drink_type)
            self.assertEqual(mode, result.decision.target_daily_mode)
            self.assertEqual(1, len(provider.requests))
            model = provider.requests[0]
            wire = json.dumps(provider.budget_payload(model), ensure_ascii=False, separators=(",", ":"))
            self.assertIn("prepare_drink", wire); self.assertIn("enum_values", wire)
            self.assertEqual(context().capabilities, CognitiveModelRequestFactory.context_from(model).capabilities)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(wire), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)

    async def test_service_zone_cannot_prepare_drink(self):
        from app.world_action_eligibility import may_propose_new_world_action
        from tests.test_daily_behavior_cognition import service_zone_request
        value = service_zone_request().model_dump()
        value["context"]["capabilities"] = context().capabilities.model_dump()
        current = CognitiveRequest.model_validate(value)
        self.assertFalse(may_propose_new_world_action(current))
        with self.assertRaises(ValidationError):
            await CognitiveBackendEntry(ScriptedDrinkProvider(decision()), PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH).run(current)
