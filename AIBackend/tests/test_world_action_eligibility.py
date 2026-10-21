from __future__ import annotations

import asyncio
import json
import unittest

from app.action_proposal_validator import ActionProposalValidator
from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import MockProvider, ModelResult

from tests.test_cognitive_request import _event_request
from tests.app_test_isolation import get_test_app, native_app_client


def _request(event_type: str = "player.utterance") -> dict:
    value = _event_request()
    value["trigger"]["reason"] = event_type
    value["context"]["trigger"]["reason"] = event_type
    event = value["context"]["current_event"]
    event["event_type"] = event_type
    event["target_entity_id"] = "npc_cafe_01"
    event["payload"] = [{"key": "text", "value": "把杯子给我"}]
    value["context"]["perception"]["entities"] = [{
        "entity_id": "cup_01", "kind": "item", "display_name": "杯子",
        "aliases": [], "semantic_tags": ["cup"], "visibility": "current_visible",
        "capabilities": ["pick_up"],
    }]
    value["context"]["capabilities"] = {"available_actions": [{
        "action_id": "bring_object", "description": "Bring a cup to a player.",
        "bindings": [
            {"name": "target", "kind": "entity_ref", "required": True},
            {"name": "destination", "kind": "entity_ref", "required": True},
        ],
    }]}
    return value


ACTION = {
    "decision_kind": "ACTION_PROPOSAL", "utterance": "好的，我去拿杯子。",
    "emotion": "neutral", "gesture": "none", "look_at": "player_01",
    "action_proposal": {
        "action_id": "bring_object",
        "bindings": {"target": "cup_01", "destination": "player_01"},
    },
}


class RecordingValidator(ActionProposalValidator):
    def __init__(self) -> None:
        super().__init__()
        self.calls = 0

    def validate_and_adapt(self, decision, context):
        self.calls += 1
        return super().validate_and_adapt(decision, context)


class WorldActionEligibilityTests(unittest.TestCase):
    def _run(self, payload: dict, decision: dict = ACTION):
        validator = RecordingValidator()
        provider = MockProvider(responder=lambda _: ModelResult(text=json.dumps(decision)))
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                                      action_validator=validator, context_length=16384)
        request = CognitiveRequest.model_validate(payload)
        return entry, request, validator

    def test_passive_events_cannot_start_new_world_action(self) -> None:
        for event_type in ("entity.became_visible", "action.completed", "action.failed"):
            with self.subTest(event_type=event_type):
                entry, request, validator = self._run(_request(event_type))
                with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
                    asyncio.run(entry.run(request))
                self.assertEqual(0, validator.calls)

    def test_current_player_utterance_may_propose_without_text_classification(self) -> None:
        for text in ("把杯子给我", "你好"):
            with self.subTest(text=text):
                payload = _request()
                payload["context"]["current_event"]["payload"][0]["value"] = text
                entry, request, validator = self._run(payload)
                result = asyncio.run(entry.run(request))
                self.assertEqual("bring_object", result.validated_goal.type)
                self.assertEqual(1, validator.calls)

    def test_passive_direct_response_keeps_expression_contract(self) -> None:
        decision = {
            "decision_kind": "DIRECT_RESPONSE", "utterance": "你来了。",
            "emotion": "happy", "gesture": "wave", "look_at": "player_01",
        }
        entry, request, validator = self._run(_request("entity.became_visible"), decision)
        result = asyncio.run(entry.run(request))
        self.assertEqual("DIRECT_RESPONSE", result.decision.decision_kind)
        self.assertEqual("wave", result.decision.gesture)
        self.assertIsNone(result.validated_goal)

    def test_inconsistent_or_missing_event_provenance_fails_closed(self) -> None:
        mismatch = _request()
        mismatch["trigger"]["source_event_id"] = "other_event"
        mismatch["context"]["trigger"]["source_event_id"] = "other_event"
        with self.assertRaisesRegex(ValueError, "COGNITIVE_SOURCE_EVENT_MISMATCH"):
            CognitiveRequest.model_validate(mismatch)

        trigger_type = _request()
        trigger_type["run"]["trigger_type"] = "goal"
        with self.assertRaisesRegex(ValueError, "COGNITIVE_RUN_TRIGGER_TYPE_MISMATCH"):
            CognitiveRequest.model_validate(trigger_type)

        missing = _request()
        missing["context"]["current_event"] = None
        entry, request, validator = self._run(missing)
        with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
            asyncio.run(entry.run(request))
        self.assertEqual(0, validator.calls)

    def test_wrong_agent_cannot_borrow_player_utterance_origin(self) -> None:
        payload = _request()
        payload["context"]["current_event"]["target_entity_id"] = "npc_cafe_regular_01"
        entry, request, validator = self._run(payload)
        with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
            asyncio.run(entry.run(request))
        self.assertEqual(0, validator.calls)

    def test_whitespace_source_event_id_and_event_id_are_denied(self) -> None:
        for change in ("source_event_id", "event_id"):
            with self.subTest(change=change):
                payload = _request()
                payload["trigger"]["source_event_id"] = "  "
                payload["context"]["trigger"]["source_event_id"] = "  "
                if change == "event_id":
                    payload["context"]["current_event"]["event_id"] = "  "
                    entry, request, validator = self._run(payload)
                    with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
                        asyncio.run(entry.run(request))
                    self.assertEqual(0, validator.calls)
                else:
                    with self.assertRaisesRegex(ValueError, "COGNITIVE_SOURCE_EVENT_MISMATCH"):
                        CognitiveRequest.model_validate(payload)

    def test_whitespace_player_source_is_denied(self) -> None:
        payload = _request()
        payload["context"]["current_event"]["source_entity_id"] = "  "
        entry, request, validator = self._run(payload)
        with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
            asyncio.run(entry.run(request))
        self.assertEqual(0, validator.calls)

    def test_whitespace_event_trace_is_absent_but_nonblank_mismatch_is_denied(self) -> None:
        payload = _request()
        payload["context"]["current_event"]["trace_id"] = "  "
        entry, request, validator = self._run(payload)
        result = asyncio.run(entry.run(request))
        self.assertEqual("bring_object", result.validated_goal.type)
        self.assertEqual(1, validator.calls)

        payload["context"]["current_event"]["trace_id"] = "other_trace"
        with self.assertRaisesRegex(ValueError, "COGNITIVE_EVENT_TRACE_MISMATCH"):
            CognitiveRequest.model_validate(payload)

    def test_unknown_trigger_or_missing_player_source_fails_closed(self) -> None:
        for change in ("unknown_trigger", "missing_source", "missing_player"):
            with self.subTest(change=change):
                payload = _request()
                if change == "unknown_trigger":
                    payload["run"]["trigger_type"] = "goal"
                    payload["trigger"]["trigger_type"] = "goal"
                    payload["context"]["trigger"]["trigger_type"] = "goal"
                elif change == "missing_source":
                    payload["context"]["current_event"]["source_entity_id"] = ""
                else:
                    payload["context"]["relevant_player"] = None
                decision = {**ACTION, "look_at": None} if change == "missing_player" else ACTION
                entry, request, validator = self._run(payload, decision)
                with self.assertRaisesRegex(ValueError, "WORLD_ACTION_NOT_ELIGIBLE"):
                    asyncio.run(entry.run(request))
                self.assertEqual(0, validator.calls)

    def test_passive_rejection_uses_correlated_safe_failure_envelope(self) -> None:
        m3_main = get_test_app()

        previous = m3_main.cognitive_entry
        validator = RecordingValidator()
        m3_main.cognitive_entry = CognitiveBackendEntry(
            MockProvider(responder=lambda _: ModelResult(text=json.dumps(ACTION))),
            PrioritizedModelExecutor(), action_validator=validator)
        try:
            with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                websocket.send_json(_request("entity.became_visible"))
                response = websocket.receive_json()
            self.assertEqual("error", response["message_type"])
            self.assertEqual("request_01", response["request_id"])
            self.assertEqual("run_01", response["run_id"])
            self.assertEqual("npc_cafe_01", response["agent_entity_id"])
            self.assertNotIn("decision", response)
            self.assertNotIn("validated_goal", response)
            self.assertEqual(0, validator.calls)
        finally:
            m3_main.cognitive_entry = previous


if __name__ == "__main__":
    unittest.main()
