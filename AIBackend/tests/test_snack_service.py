import json
import unittest
from unittest.mock import patch

from pydantic import ValidationError
from app.action_catalog import SERVE_SNACK_REGISTRATION
from app.action_proposal_validator import ActionProposalValidator, ActionProposalValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_step_models import CognitiveStepResult
from app.contracts import CognitiveContextSnapshot, CognitiveDecision, CognitiveRequest, GoalProposal
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.snack_catalog import SNACKS, SNACK_TYPE_IDS, SNACK_QUANTITY_IDS
from tests.test_prepare_drink import context as drink_context, request as drink_request, ScriptedDrinkProvider


def context(agent="npc_cafe_01"):
    value=drink_context(agent).model_dump()
    d=SERVE_SNACK_REGISTRATION.descriptor
    value["capabilities"]["available_actions"]=[dict(action_id=d.action_id,description=d.description,
        bindings=[dict(name=b.name,kind=b.kind,required=b.required,enum_values=b.enum_values) for b in d.binding_schema])]
    return CognitiveContextSnapshot.model_validate(value)


def decision(snack="croissant",quantity="1"):
    return dict(decision_kind="ACTION_PROPOSAL",utterance="好呀，我去取点心，请稍等。",
        action_proposal=dict(action_id="serve_snack",bindings=dict(snack_type=snack,quantity=quantity)))


class SnackServiceTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        for target in ("socket.socket.connect","sqlite3.connect"):
            guard=patch(target,side_effect=AssertionError("Snack tests must remain offline and storage-free"))
            guard.start(); self.addCleanup(guard.stop)

    def test_all_configured_types_and_quantities_roundtrip(self):
        self.assertEqual(("croissant","macaron","cupcake","pudding"),SNACK_TYPE_IDS)
        for snack in SNACK_TYPE_IDS:
            for quantity in SNACK_QUANTITY_IDS:
                step=CognitiveStepResult.model_validate(decision(snack,quantity)).root
                goal=ActionProposalValidator().validate_and_adapt(step,context())
                self.assertEqual(GoalProposal(type="serve_snack",snack_type=snack,quantity=quantity),goal)
                self.assertIsNone(goal.drink_type); self.assertIsNone(goal.target_entity_id)

    def test_invalid_type_quantity_and_cross_action_arguments(self):
        for snack,quantity in (("cheesecake","1"),("chocolate_cake","1"),("croissant","0"),("croissant","5"),
                               ("croissant","1.5"),("croissant",1),("croissant",True)):
            with self.subTest(snack=snack,quantity=quantity), self.assertRaises((ValidationError,ActionProposalValidationError)):
                ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(decision(snack,quantity)),context())
        for args in (dict(drink_type="latte"),dict(target_entity_id="cup_01"),dict(destination_entity_id="player_01")):
            with self.assertRaises(ValidationError):
                GoalProposal(type="serve_snack",snack_type="croissant",quantity="1",**args)
        with self.assertRaises(ValidationError):
            GoalProposal(type="prepare_drink",drink_type="latte",snack_type="croissant",quantity="1")

    def test_other_npc_missing_player_or_capability_cannot_serve(self):
        d=CognitiveDecision.model_validate(decision())
        for agent in ("npc_cafe_regular_01","npc_cafe_companion_01","npc_cafe_quiet_01"):
            with self.assertRaises(ActionProposalValidationError): ActionProposalValidator().validate_and_adapt(d,context(agent))
        for field in ("capabilities","relevant_player"):
            value=context().model_dump(); value[field]=None
            with self.assertRaises(ActionProposalValidationError):
                ActionProposalValidator().validate_and_adapt(d,CognitiveContextSnapshot.model_validate(value))

    def test_tampered_menu_manifest_rejected(self):
        value=context().model_dump()
        value["capabilities"]["available_actions"][0]["bindings"][0]["enum_values"]=(*SNACK_TYPE_IDS,"cheesecake")
        with self.assertRaises(ActionProposalValidationError):
            ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(decision()),CognitiveContextSnapshot.model_validate(value))

    async def test_scripted_order_uses_existing_cognition_pipeline(self):
        for snack,quantity in (("croissant","1"),("pudding","3")):
            value=drink_request().model_dump(); value["context"]["capabilities"]=context().capabilities.model_dump()
            value["context"]["current_event"]["payload"]=[dict(key="text",value=f"请给我{quantity}份{snack}")]
            current=CognitiveRequest.model_validate(value)
            provider=ScriptedDrinkProvider(decision(snack,quantity))
            result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=16384,
                inference_options=InferenceOptions(max_output_tokens=1024)).run(current)
            self.assertEqual(snack,result.validated_goal.snack_type); self.assertEqual(quantity,result.validated_goal.quantity)
            self.assertEqual(1,len(provider.requests))
            wire=json.dumps(provider.budget_payload(provider.requests[0]),ensure_ascii=False)
            for item in SNACKS: self.assertIn(item["chinese"],wire); self.assertIn(item["id"],wire)
            self.assertIn("not a physical entity handover",wire)

    async def test_menu_and_unsupported_request_stay_dialogue_without_goal(self):
        for text in ("有什么点心，推荐一下？","请给我一块芝士蛋糕"):
            value=drink_request().model_dump(); value["context"]["capabilities"]=context().capabilities.model_dump()
            value["context"]["current_event"]["payload"]=[dict(key="text",value=text)]
            provider=ScriptedDrinkProvider(dict(decision_kind="DIRECT_RESPONSE",utterance="目前配置的点心有牛角包、马卡龙、小蛋糕和布丁，可以从中选择。"))
            result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=16384,
                inference_options=InferenceOptions(max_output_tokens=1024)).run(CognitiveRequest.model_validate(value))
            self.assertIsNone(result.validated_goal)

    def test_menu_guidance_is_capability_scoped(self):
        from app.model_requests import _action_model_usage
        self.assertIn("牛角包",_action_model_usage(context().capabilities))
        self.assertNotIn("牛角包",_action_model_usage(drink_context().capabilities))

    def test_execution_snapshot_requires_matching_snack_semantics_and_receipt(self):
        from app.contracts import CognitiveExecutionState
        from tests.test_execution_context import snapshot
        for status in ("IN_PROGRESS","SUCCEEDED","FAILED","CANCELLED"):
            value=snapshot(status); value.pop("drink_type")
            value.update(action_id="serve_snack",snack_type="pudding",quantity="3")
            current=CognitiveExecutionState.model_validate(value)
            self.assertEqual((current.snack_type,current.quantity),("pudding","3"))
            if status=="SUCCEEDED":
                value.pop("receipt_id")
                with self.assertRaises(ValidationError): CognitiveExecutionState.model_validate(value)
        value=snapshot(); value.update(snack_type="pudding",quantity="1")
        with self.assertRaises(ValidationError): CognitiveExecutionState.model_validate(value)

    async def test_autonomous_service_zone_cannot_order_snacks(self):
        from tests.test_daily_behavior_cognition import service_zone_request
        value=service_zone_request().model_dump(); value["context"]["capabilities"]=context().capabilities.model_dump()
        with self.assertRaises(ValidationError):
            await CognitiveBackendEntry(ScriptedDrinkProvider(decision()),PrioritizedModelExecutor(),context_length=16384).run(CognitiveRequest.model_validate(value))
