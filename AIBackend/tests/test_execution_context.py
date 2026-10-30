"""Unity execution snapshots at the real Provider body boundary; zero network/model cost."""
import json
import unittest

import httpx
from pydantic import ValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.context_budget_allocator import ContextBudgetExceeded
from app.contracts import CognitiveRequest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_action_results import RecentInteractionGroups
from tests.test_recent_dialogue_cognition import request


def snapshot(status="IN_PROGRESS"):
    state = dict(source="unity_execution_snapshot", actor_entity_id="npc_cafe_01", player_entity_id="player_01",
                 source_session_id="session_current", scene_instance_id="scene_current", captured_at=10., world_revision=2,
                 status=status)
    if status != "UNKNOWN":
        state.update(proposal_id="proposal_original", proposal_revision=0, trace_id="trace_original",
                     action_id="prepare_drink", drink_type="latte", stage="action", step="PrepareDrink")
        if status != "IN_PROGRESS":
            state.update(stage="terminal", step=None, outcome_id="outcome_original", outcome_world_revision=2)
            if status == "SUCCEEDED": state["receipt_id"] = "receipt_original"
            else: state["failure_code"] = "CANCELLED_BY_REQUEST" if status == "CANCELLED" else "STATION_UNAVAILABLE"
    return state


def wire(status="IN_PROGRESS"):
    req = request("拿铁现在做得怎么样了？")
    raw = req.model_dump()
    raw["recall_identity"] = req.recall_identity.model_dump()
    raw["context"]["runtime_state"] = snapshot(status)
    return raw


def provider(bodies, *, recall=False):
    def capture(req):
        bodies.append(json.loads(req.content))
        text = ('{"decision_kind":"MEMORY_RECALL","recall_plan":{"semantic_query":"拿铁"}}'
                if recall and len(bodies) == 1 else
                '{"decision_kind":"DIRECT_RESPONSE","utterance":"我正在制作你的拿铁。"}')
        return httpx.Response(200, json={"status":"completed", "output":[{"type":"message","role":"assistant",
            "content":[{"type":"output_text","text":text}]}]})
    return DeepSeekOfficialResponsesProvider(base_url="https://api.deepseek.com", api_key="offline-placeholder",
        model="test", transport=httpx.MockTransport(capture))


def body_context(body):
    text = body["input"][-1]["content"]
    return json.loads(next(line.split(": ", 1)[1] for line in text.splitlines() if line.startswith("Cognitive context JSON: ")))


class ExecutionContextTests(unittest.IsolatedAsyncioTestCase):
    async def test_all_five_states_reach_actual_provider_body_without_private_identities(self):
        for status in ("UNKNOWN", "IN_PROGRESS", "SUCCEEDED", "FAILED", "CANCELLED"):
            with self.subTest(status=status):
                bodies = []
                req = CognitiveRequest.model_validate(wire(status))
                await CognitiveBackendEntry(provider(bodies), PrioritizedModelExecutor(), context_length=32768).run(req)
                self.assertEqual(len(bodies), 1)
                state = body_context(bodies[0])["runtime_state"]
                self.assertEqual((state["status"], state["actor_entity_id"], state["player_entity_id"]),
                                 (status, "npc_cafe_01", "player_01"))
                if status != "UNKNOWN":
                    self.assertEqual((state["proposal_id"], state["trace_id"], state["proposal_revision"]),
                                     ("proposal_original", "trace_original", 0))
                self.assertNotIn("merchant.private", json.dumps(bodies))
                self.assertNotIn("cafe-local-player-001", json.dumps(bodies))
                self.assertIn("EXECUTION SNAPSHOT CONTRACT", bodies[0]["input"][0]["content"])

    async def test_internal_recall_preserves_the_same_execution_snapshot(self):
        bodies = []
        await CognitiveBackendEntry(provider(bodies, recall=True), PrioritizedModelExecutor(), context_length=32768).run(
            CognitiveRequest.model_validate(wire()))
        self.assertEqual(len(bodies), 2)
        self.assertEqual(body_context(bodies[0])["runtime_state"], body_context(bodies[1])["runtime_state"])

    def test_snapshot_is_mandatory_budget_data_and_not_replaced_by_historical_success(self):
        req = CognitiveRequest.model_validate(wire())
        historical = RecentInteractionGroups([[dict(kind="action_result", status="SUCCEEDED", historical_only=True,
            action_type="prepare_drink", proposal_id="old_order", trace_id="old_trace", seq=1)]],
            action_scope=dict(offered_results=1, ongoing_execution_state="NOT_OBSERVED", historical_only=True))
        p = provider([])
        model = CognitiveModelRequestFactory.create_event(req, recent_interactions=historical,
            knowledge_context_limit=32768, budget_payload_builder=p.budget_payload,
            inference_options=InferenceOptions(max_output_tokens=1024))
        body = p.budget_payload(model)
        self.assertEqual(body_context(body)["runtime_state"]["status"], "IN_PROGRESS")
        self.assertIn("old_order", json.dumps(body))
        with self.assertRaises(ContextBudgetExceeded):
            CognitiveModelRequestFactory.create_event(req, knowledge_context_limit=2048,
                budget_payload_builder=p.budget_payload, inference_options=InferenceOptions(max_output_tokens=1024))

    def test_invalid_scope_or_fabricated_progress_is_rejected(self):
        mutations = [dict(actor_entity_id="npc_other"), dict(player_entity_id="player_other"),
                     dict(percentage=50), dict(remaining_seconds=10), dict(receipt_id="fake"),
                     dict(outcome_id="fake"), dict(stage="terminal"), dict(captured_at=float("nan"))]
        for changes in mutations:
            with self.subTest(changes=changes):
                raw = wire(); raw["context"]["runtime_state"].update(changes)
                with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)

    def test_unknown_never_leaks_previous_goal_and_success_requires_receipt(self):
        for status, changes in [("UNKNOWN", dict(proposal_id="private_old_order")),
                                ("SUCCEEDED", dict(receipt_id=None)),
                                ("CANCELLED", dict(receipt_id="fake")),
                                ("FAILED", dict(outcome_world_revision=99))]:
            raw = wire(status); raw["context"]["runtime_state"].update(changes)
            with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)

    def test_snapshot_must_not_precede_the_current_input(self):
        raw = wire(); raw["context"]["runtime_state"]["captured_at"] = 9
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)
        raw = wire(); raw["context"]["current_event"].update(has_world_revision=True, world_revision=3)
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)

    def test_current_persistent_identities_required_without_projecting_them_to_model(self):
        raw = wire(); raw.pop("recall_identity")
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)
        self.assertIsNone(CognitiveRequest.model_validate(wire("UNKNOWN")).context.runtime_state.proposal_id)

    def test_legacy_request_without_snapshot_remains_supported_as_unconfirmed(self):
        raw = wire(); raw["context"].pop("runtime_state")
        self.assertIsNone(CognitiveRequest.model_validate(raw).context.runtime_state)


if __name__ == "__main__": unittest.main()
