"""Immediate controls use offered execution evidence, not TaskService or speech parsing."""
import json
import unittest
from pydantic import ValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveDecision, CognitiveRequest
from app.cognitive_step_models import TaskDirectResponseStep
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from tests.test_execution_context import wire
from tests.test_daily_behavior_cognition import WireScriptedProvider


def request(text="不对，先别做了"):
    raw = wire()
    for item in raw["context"]["current_event"]["payload"]:
        if item["key"] == "text": item["value"] = text
    raw["context"]["runtime_state"]["allowed_controls"] = ["cancel_active_goal"]
    return CognitiveRequest.model_validate(raw)


def control():
    return dict(operation="cancel_active_goal", proposal_id="proposal_original", proposal_revision=0, trace_id="trace_original")


def reply(**kw):
    return dict(decision_kind="DIRECT_RESPONSE", utterance="好，这次就不做了。之后想喝的话再告诉我。", **kw)


class ImmediateGoalControlTests(unittest.IsolatedAsyncioTestCase):
    async def execute(self, req, decision):
        provider = WireScriptedProvider([decision])
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=32768).run(req)
        self.assertEqual(len(provider.requests), 1)
        return result, provider.requests[0]

    async def test_cancel_reaches_wire_as_intent_with_no_new_goal_or_task(self):
        for text in ("不对，先别做了", "不做了", "取消这杯咖啡", "先停一下", "等一下，先别继续"):
            with self.subTest(text=text):
                result, model = await self.execute(request(text), reply(action_control=control()))
                self.assertEqual(result.decision.action_control.proposal_id, "proposal_original")
                self.assertIsNone(result.validated_goal)
                self.assertIsNone(result.task_receipt)
                self.assertIn("ACTION CONTROL CONTRACT", model.messages[0].content)
                self.assertIn("Suspend/Resume are unavailable", model.messages[0].content)
                self.assertIn("action_control", json.dumps(model.response_schema))
                self.assertEqual(next(p.value for p in CognitiveModelRequestFactory.context_from(model).current_event.payload if p.key=="text"), text)

    async def test_chat_and_progress_leave_execution_untouched_and_no_control(self):
        for text in ("你在做什么？", "你喜欢画什么？", "不要取消，继续做", "如果我说取消会怎样？"):
            with self.subTest(text=text):
                req = request(text); original = req.model_dump()
                result, _ = await self.execute(req, reply())
                self.assertIsNone(result.decision.action_control)
                self.assertEqual(req.model_dump(), original)
                self.assertIsNone(result.validated_goal)

    async def test_forged_proposal_trace_and_revision_rejected(self):
        for changes in (dict(proposal_id="someone_else"), dict(trace_id="other_trace"), dict(proposal_revision=1)):
            with self.subTest(changes=changes):
                c = control(); c.update(changes)
                with self.assertRaisesRegex(ValueError, "COGNITIVE_ACTION_CONTROL_NOT_ELIGIBLE"):
                    await self.execute(request(), reply(action_control=c))

    async def test_legacy_or_unoffered_goal_does_not_grant_cancel(self):
        for controls in (None, []):
            req=request(); raw=req.model_dump(); raw["recall_identity"]=req.recall_identity.model_dump(); raw["context"]["runtime_state"]["allowed_controls"]=controls
            req=CognitiveRequest.model_validate(raw)
            with self.assertRaisesRegex(ValueError, "COGNITIVE_ACTION_CONTROL_NOT_ELIGIBLE"):
                await self.execute(req, reply(action_control=control()))
            _, model=await self.execute(req, reply())
            self.assertNotIn("ACTION CONTROL CONTRACT", model.messages[0].content)
            self.assertNotIn('"action_control"', json.dumps(model.response_schema))

    async def test_completed_goal_cannot_be_relabelled_cancelled(self):
        for status in ("SUCCEEDED", "CANCELLED", "FAILED", "UNKNOWN"):
            with self.subTest(status=status):
                req=CognitiveRequest.model_validate(wire(status))
                with self.assertRaisesRegex(ValueError, "COGNITIVE_ACTION_CONTROL_NOT_ELIGIBLE"):
                    await self.execute(req, reply(action_control=control()))

    async def test_only_current_directed_player_input_can_control(self):
        for change in ("other_player", "other_npc", "no_conversation", "continuation"):
            req=request(); raw=req.model_dump(); raw["recall_identity"]=req.recall_identity.model_dump(); event=raw["context"]["current_event"]
            if change=="other_player": event["source_entity_id"]="player_other"
            if change=="other_npc": event["target_entity_id"]="npc_other"
            if change=="no_conversation": event["payload"]=[x for x in event["payload"] if x["key"]!="conversation_id"]
            if change=="continuation":
                event["event_type"]="npc.dialogue_continue"; event["source_entity_id"]="npc_cafe_01"
            with self.subTest(change=change), self.assertRaises((ValueError,ValidationError)):
                await self.execute(CognitiveRequest.model_validate(raw), reply(action_control=control()))

    def test_control_cannot_combine_with_new_action_mode_or_continuation(self):
        for changes in (dict(target_daily_mode="rest"), dict(dialogue_turn_policy="MAY_CONTINUE"),
                        dict(decision_kind="NO_ACTION"),
                        dict(action_proposal=dict(action_id="prepare_drink",bindings={"drink_type":"latte"}))):
            raw=reply(action_control=control());raw.update(changes)
            with self.subTest(changes=changes), self.assertRaises(ValidationError): CognitiveDecision.model_validate(raw)

    def test_control_is_not_a_persistent_task_cancel(self):
        raw=reply(action_control=control(),future_commitment=True,task_operation=None)
        with self.assertRaises(ValidationError): TaskDirectResponseStep.model_validate(raw)
        raw=reply(action_control=control(),future_commitment=False,task_operation=dict(
            op="update_task",task_ref="opaque_ref",expected_version=1,change="cancel",reason="stop"))
        with self.assertRaises(ValidationError): TaskDirectResponseStep.model_validate(raw)

    def test_strict_control_fields_and_suspend_resume_not_supported(self):
        for changes in (dict(operation="suspend"),dict(operation="resume"),dict(proposal_revision=True),
                        dict(proposal_revision="0"),dict(proposal_revision=-1),dict(actor_entity_id="npc_other")):
            c=control();c.update(changes)
            with self.subTest(changes=changes),self.assertRaises(ValidationError): CognitiveDecision.model_validate(reply(action_control=c))

    def test_absent_control_does_not_break_legacy_response_wire(self):
        self.assertNotIn("action_control", CognitiveDecision.model_validate(reply()).model_dump())


if __name__=="__main__": unittest.main()
