from __future__ import annotations

import asyncio
import json
import os
import unittest
from unittest.mock import patch

from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.reply_language import response_schema
from app.contracts import CognitiveCharacterContext, CognitiveDecision, CognitiveRequest
from app.providers import MockProvider, ModelRequest, ModelResult, ProviderError
from tests.app_test_isolation import get_test_app, native_app_client


def _event_request() -> dict:
    trigger = {
        "trigger_id": "trigger_01",
        "trigger_type": "event",
        "agent_entity_id": "npc_cafe_01",
        "source_event_id": "event_01",
        "created_at": 10.0,
        "reason": "player.entered_area",
    }
    return {
        "message_type": "cognitive_request",
        "schema_version": "1.0",
        "request_id": "request_01",
        "trace_id": "trace_01",
        "recall_identity": {
            "observer": dict(entity_id="npc_cafe_01", kind="npc", identity_namespace="npc",
                             identity_type="Character", persistent_id="test_npc"),
            "current_subjects": [dict(entity_id="player_01", kind="player", identity_namespace="player",
                identity_type="Player", persistent_id=os.getenv("EMBODIEDNPC_DEMO_PLAYER_UID", "cafe-local-player-001"))],
        },
        "run": {
            "run_id": "run_01",
            "agent_entity_id": "npc_cafe_01",
            "trigger_id": "trigger_01",
            "trigger_type": "event",
            "started_at": 10.1,
        },
        "trigger": trigger,
        "context": {
            "run_id": "run_01",
            "agent_entity_id": "npc_cafe_01",
            "trigger": trigger,
            "current_event": {
                "event_id": "event_01",
                "event_type": "player.entered_area",
                "source_entity_id": "player_01",
                "target_entity_id": "",
                "area_entity_id": "area_cafe_counter",
                "payload": [],
                "occurred_at": 10.0,
                "world_revision": 0,
                "has_world_revision": False,
            },
            "character": {
                "character_id": "cafe.merchant",
                "display_name": "店员",
                "role_summary": "咖啡馆店员，负责接待顾客",
                "speaking_style": "友好、自然、简洁",
                "backstory": "在咖啡馆工作几年。",
                "personality": "温和慢热。",
                "boundaries": "不编造共同经历。",
            },
            "behavior_mode": {"mode_id": "working"},
            "relevant_player": {"player_entity_id": "player_01", "display_name": "玩家"},
            "perception": {
                "observer_entity_id": "npc_cafe_01",
                "perception_revision": 0,
                "entities": [],
            },
        },
    }


class CognitiveRequestTests(unittest.TestCase):
    def test_player_language_precedence_survives_final_prompt_without_keyword_routing(self):
        from app.model_requests import CognitiveModelRequestFactory, NPC_REPLY_LANGUAGE_RULES
        from app.cognitive_step_models import CognitiveStepResult
        from app.deepseek_provider import DeepSeekOfficialResponsesProvider
        provider = DeepSeekOfficialResponsesProvider(base_url="https://example.invalid",
            api_key="synthetic-contract-key", model="synthetic-language-contract")
        for text in ("goodmorning", "good morning", "hello", "早上好", "请用英文回答我", "OK", "我想聊聊 DeepSeek"):
            with self.subTest(text=text):
                value = _event_request()
                value["context"]["current_event"].update(event_type="player.utterance",
                    payload=[dict(key="text", value=text)])
                request = CognitiveRequest.model_validate(value)
                before = request.model_dump_json()
                model = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384,
                    recent_dialogue=[dict(player_text="Let's speak English.", npc_text="Of course.")],
                    budget_payload_builder=provider.budget_payload)
                wire = provider.budget_payload(model)
                system = wire["input"][0]["content"]
                self.assertEqual(1, system.count(NPC_REPLY_LANGUAGE_RULES))
                self.assertLess(system.index(NPC_REPLY_LANGUAGE_RULES), system.index("CHARACTER PROFILE"))
                self.assertIn(text, wire["input"][-1]["content"])
                binding = next(line for line in wire["input"][-1]["content"].splitlines()
                    if line.startswith("CURRENT TASK BINDING JSON: "))
                self.assertEqual(text, json.loads(binding.split(": ", 1)[1])["current_player_text"])
                self.assertIn("Let's speak English.", wire["input"][-1]["content"])
                self.assertEqual(response_schema(CognitiveStepResult, "zh-CN"), wire["text"]["format"]["schema"])
                self.assertEqual(before, request.model_dump_json())
        self.assertNotIn("goodmorning", NPC_REPLY_LANGUAGE_RULES)
        self.assertIn("trusted per-run reply_language", NPC_REPLY_LANGUAGE_RULES)
        self.assertIn("Do not infer it again", NPC_REPLY_LANGUAGE_RULES)
        self.assertIn("IDs, action bindings/parameters", NPC_REPLY_LANGUAGE_RULES)

    def test_language_switch_and_provider_output_are_not_translated_by_backend(self):
        """Offline transport contract only; a scripted answer cannot prove model language adherence."""
        import httpx
        from app.deepseek_provider import DeepSeekOfficialResponsesProvider
        from app.model_config_runtime import build_main_provider
        from app.model_api_config import MainModelConfig
        from app.model_requests import CognitiveModelRequestFactory
        from app.cognitive_step_models import CognitiveStepResult
        for text, earlier, answer in (("早上好", "Good morning.", "早上好。"),
                                      ("good morning", "早上好。", "Good morning.")):
            value = _event_request()
            value["context"]["current_event"].update(event_type="player.utterance", payload=[dict(key="text",value=text)])
            captured = {}
            raw = json.dumps(dict(decision_kind="DIRECT_RESPONSE", utterance=answer, emotion="neutral",
                                 gesture="none", look_at="player_01", episode_directive="END"),ensure_ascii=False)
            def handler(request):
                captured.update(json.loads(request.content))
                return httpx.Response(200,json=dict(status="completed",output=[dict(type="message",role="assistant",
                    content=[dict(type="output_text",text=raw)])]))
            provider = build_main_provider(MainModelConfig(display_name="Synthetic",provider_type="deepseek_official_responses",
                base_url="https://example.invalid",model="synthetic-language",api_key="synthetic-only",context_length=16384),
                transport=httpx.MockTransport(handler))
            model = CognitiveModelRequestFactory.create_event(CognitiveRequest.model_validate(value),knowledge_context_limit=16384,
                recent_dialogue=[dict(player_text=earlier,npc_text=earlier)],budget_payload_builder=provider.budget_payload)
            result = asyncio.run(provider.generate(model))
            self.assertEqual(raw,result.text)
            self.assertEqual(answer,CognitiveStepResult.model_validate_json(result.text).root.utterance)
            self.assertEqual(provider.budget_payload(model),captured)

    def test_player_language_rule_is_prompt_only(self) -> None:
        from app.model_requests import CognitiveModelRequestFactory, NPC_REPLY_LANGUAGE_RULES
        from app.cognitive_step_models import CognitiveStepResult
        value = _event_request()
        value["context"]["current_event"].update(event_type="player.utterance",
            payload=[dict(key="text", value="介绍一下 DeepSeek。")])
        request = CognitiveRequest.model_validate(value)
        before = request.model_dump_json()
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384)
        self.assertIn(NPC_REPLY_LANGUAGE_RULES, model.messages[0].content)
        self.assertIn("介绍一下 DeepSeek。", model.messages[-1].content)
        self.assertEqual(response_schema(CognitiveStepResult, "zh-CN"), model.response_schema)
        self.assertEqual(before, request.model_dump_json())

    def test_persona_fields_are_required_bounded_frozen_and_extra_forbidden(self) -> None:
        original = _event_request()["context"]["character"]
        limits = {"display_name": 80, "role_summary": 240, "backstory": 1200,
                  "personality": 600, "speaking_style": 480, "boundaries": 600}
        for field, limit in limits.items():
            with self.subTest(field=field):
                self.assertEqual("人" * limit, getattr(CognitiveCharacterContext.model_validate(
                    {**original, field: "人" * limit}), field))
                for invalid in (None, "  ", "人" * (limit + 1)):
                    with self.assertRaises(ValueError):
                        CognitiveCharacterContext.model_validate({**original, field: invalid})
                missing = dict(original)
                del missing[field]
                with self.assertRaises(ValueError):
                    CognitiveCharacterContext.model_validate(missing)
        with self.assertRaises(ValueError):
            CognitiveCharacterContext.model_validate({**original, "mood": "happy"})
        profile = CognitiveCharacterContext.model_validate(original)
        with self.assertRaises(ValueError):
            profile.personality = "changed"
        self.assertEqual(len(CognitiveCharacterContext.model_validate(
            {**original, "backstory": "🙂" * 1200}).backstory), 1200)
        with self.assertRaises(ValueError):
            CognitiveCharacterContext.model_validate({**original, "backstory": "🙂" * 1201})

    def test_health_uses_direct_shared_provider_and_keeps_response_shape(self) -> None:
        m3_main = get_test_app()

        with patch.object(m3_main, "provider", MockProvider()):
            with native_app_client(m3_main) as client:
                self.assertEqual(403, client.get("/health", headers={"Authorization": "Bearer invalid"}).status_code)
                health = client.get("/health").json()
        self.assertEqual("ok", health["status"])
        self.assertEqual("embodiednpc-backend", health["service"])
        self.assertEqual("mock", health["provider"]["provider"])
        self.assertTrue(health["provider"]["available"])

    def test_cognitive_decision_direct_response_requires_bounded_utterance(self) -> None:
        decision = CognitiveDecision.model_validate({
            "decision_kind": "DIRECT_RESPONSE", "utterance": "欢迎光临。",
            "emotion": "neutral", "gesture": "none", "look_at": "player_01",
            "episode_directive": "END",
        })
        self.assertEqual("欢迎光临。", decision.utterance)
        self.assertEqual("END", decision.episode_directive)
        with self.assertRaises(ValueError):
            CognitiveDecision.model_validate({"decision_kind": "DIRECT_RESPONSE", "utterance": "  "})
        with self.assertRaises(ValueError):
            CognitiveDecision.model_validate({
                "decision_kind": "DIRECT_RESPONSE", "utterance": "欢迎光临。",
                "episode_directive": "TRANSITION",
            })

    def test_cognitive_decision_no_action_is_canonical_silence(self) -> None:
        decision = CognitiveDecision.model_validate({
            "decision_kind": "NO_ACTION", "utterance": None, "episode_directive": "END",
        })
        self.assertIsNone(decision.utterance)
        self.assertEqual("END", decision.episode_directive)
        with self.assertRaises(ValueError):
            CognitiveDecision.model_validate({"decision_kind": "NO_ACTION", "utterance": "hello"})

    def test_cognitive_decision_rejects_forbidden_legacy_slots(self) -> None:
        with self.assertRaises(ValueError):
            CognitiveDecision.model_validate({
                "decision_kind": "DIRECT_RESPONSE", "utterance": "no",
                "requested_operation": "bring_object", "target_entity_id": "cup_01",
            })

    def test_action_proposal_rejects_semantic_end_until_action_settlement_has_a_carrier(self) -> None:
        with self.assertRaises(ValueError):
            CognitiveDecision.model_validate({
                "decision_kind": "ACTION_PROPOSAL",
                "episode_directive": "END",
                "action_proposal": {
                    "action_id": "bring_object",
                    "bindings": {"target": "cup_01", "destination": "player_01"},
                },
            })

    def test_event_cognitive_request_validates_without_fake_utterance(self) -> None:
        payload = _event_request()
        self.assertNotIn("utterance", payload)

        request = CognitiveRequest.model_validate(payload)

        self.assertIsNotNone(request.context.current_event)
        self.assertEqual("player.entered_area", request.context.current_event.event_type)
        self.assertEqual("area_cafe_counter", request.context.current_event.area_entity_id)
        self.assertEqual("npc_cafe_01", request.context.perception.observer_entity_id)

    def test_optional_event_causation_is_retained_and_trace_must_match(self) -> None:
        payload = _event_request()
        event = payload["context"]["current_event"]
        event["trace_id"] = "trace_01"
        event["source_observation_id"] = "observation_01"
        request = CognitiveRequest.model_validate(payload)
        self.assertEqual("trace_01", request.context.current_event.trace_id)
        self.assertEqual("observation_01", request.context.current_event.source_observation_id)

        payload["trace_id"] = "other_trace"
        with self.assertRaisesRegex(ValueError, "COGNITIVE_EVENT_TRACE_MISMATCH"):
            CognitiveRequest.model_validate(payload)

    def test_event_cognitive_entry_reuses_mock_provider_and_structured_schema(self) -> None:
        captured: list[ModelRequest] = []

        def respond(request: ModelRequest) -> ModelResult:
            captured.append(request)
            return ModelResult(text=json.dumps({
                "utterance": "欢迎光临。",
                "emotion": "neutral",
                "gesture": "none",
                "look_at": None,
                "decision_kind": "DIRECT_RESPONSE",
            }, ensure_ascii=False))

        provider = MockProvider(responder=respond)
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384)
        result = asyncio.run(entry.run(CognitiveRequest.model_validate(_event_request())))

        self.assertIs(entry.provider, provider)
        self.assertEqual("DIRECT_RESPONSE", result.decision.decision_kind)
        self.assertIsNone(result.decision.episode_directive)
        self.assertIsNone(result.validated_goal)
        self.assertEqual(1, len(captured))
        self.assertIsNotNone(captured[0].response_schema)
        self.assertIn("player.entered_area", captured[0].messages[-1].content)

    def test_cognitive_websocket_route_returns_correlated_response(self) -> None:
        m3_main = get_test_app()

        previous = m3_main.cognitive_entry
        m3_main.cognitive_entry = CognitiveBackendEntry(MockProvider(responder=lambda _: ModelResult(
            text='{"decision_kind":"NO_ACTION","utterance":null,"emotion":null,'
                 '"gesture":null,"look_at":null}'
        )), PrioritizedModelExecutor(), context_length=16384)
        try:
            with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                websocket.send_json(_event_request())
                response = websocket.receive_json()
            self.assertEqual("cognitive_response", response["message_type"])
            self.assertEqual("1.0", response["schema_version"])
            self.assertEqual("request_01", response["request_id"])
            self.assertEqual("run_01", response["run_id"])
            self.assertEqual("npc_cafe_01", response["agent_entity_id"])
            self.assertEqual("NO_ACTION", response["decision"]["decision_kind"])
        finally:
            m3_main.cognitive_entry = previous

    def test_malformed_provider_output_returns_correlated_safe_failure(self) -> None:
        m3_main = get_test_app()

        previous = m3_main.cognitive_entry
        m3_main.cognitive_entry = CognitiveBackendEntry(MockProvider(
            responder=lambda _: ModelResult(text='{"decision_kind":"propose_action"}')
        ), PrioritizedModelExecutor(), context_length=16384)
        try:
            with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                websocket.send_json(_event_request())
                response = websocket.receive_json()
            self.assertEqual("error", response["message_type"])
            self.assertEqual("request_01", response["request_id"])
            self.assertEqual("run_01", response["run_id"])
            self.assertEqual("npc_cafe_01", response["agent_entity_id"])
            self.assertEqual("COGNITIVE_REQUEST_FAILED", response["reason"])
        finally:
            m3_main.cognitive_entry = previous

    def test_provider_timeout_fails_closed_with_cognition_correlation(self) -> None:
        m3_main = get_test_app()

        def timeout(_: ModelRequest) -> ModelResult:
            raise TimeoutError("provider timed out")

        previous = m3_main.cognitive_entry
        m3_main.cognitive_entry = CognitiveBackendEntry(
            MockProvider(responder=timeout), PrioritizedModelExecutor(), context_length=16384)
        try:
            with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                websocket.send_json(_event_request())
                response = websocket.receive_json()
            self.assertEqual("error", response["message_type"])
            self.assertEqual("COGNITIVE_REQUEST_FAILED", response["reason"])
            self.assertEqual("request_01", response["request_id"])
            self.assertEqual("trace_01", response["trace_id"])
            self.assertEqual("run_01", response["run_id"])
            self.assertEqual("npc_cafe_01", response["agent_entity_id"])
            self.assertNotIn("decision", response)
            self.assertNotIn("validated_goal", response)
        finally:
            m3_main.cognitive_entry = previous

    def test_provider_feedback_preserves_reason_and_only_exports_known_diagnostics(self):
        m3_main = get_test_app()

        for error, expected in ((ProviderError("OPENAI_COMPATIBLE_AUTH_FAILED"), "AUTH_FAILED"),
                                (ProviderError("DEEPSEEK_TIMEOUT"), "TIMEOUT"),
                                (ProviderError("OPENAI_COMPATIBLE_CONTEXT_LENGTH_EXCEEDED"), "CONTEXT_LENGTH_EXCEEDED"),
                                (ProviderError("OPENAI_COMPATIBLE_OUTPUT_RESERVE_EXCEEDS_CONTEXT"), "OUTPUT_RESERVE_EXCEEDS_CONTEXT"),
                                (ProviderError("private upstream body secret-sentinel"), None),
                                (RuntimeError("DEEPSEEK_TIMEOUT"), None)):
            with self.subTest(expected=expected):
                def fail(_, error=error):
                    raise error
                entry = CognitiveBackendEntry(MockProvider(responder=fail), PrioritizedModelExecutor(),
                                              context_length=16384)
                with patch.object(m3_main, "cognitive_entry", entry):
                    with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                        websocket.send_json(_event_request())
                        response = websocket.receive_json()
                self.assertEqual(response["reason"], "COGNITIVE_REQUEST_FAILED")
                self.assertEqual(response.get("diagnostic_category"), expected)
                self.assertEqual(response["trace_id"], "trace_01")
                self.assertNotIn("secret-sentinel", json.dumps(response))

    def test_local_context_overflow_reports_diagnostic_without_calling_model(self):
        m3_main = get_test_app()

        calls = []
        def unexpected(request):
            calls.append(request)
            raise AssertionError("Overflow must fail before inference")
        entry = CognitiveBackendEntry(MockProvider(responder=unexpected), PrioritizedModelExecutor(),
                                      context_length=4096)
        with patch.object(m3_main, "cognitive_entry", entry):
            with native_app_client(m3_main) as client, client.websocket_connect("/ws") as websocket:
                websocket.send_json(_event_request())
                response = websocket.receive_json()
        self.assertEqual(response["reason"], "COGNITIVE_REQUEST_FAILED")
        self.assertEqual(response["diagnostic_category"], "CONTEXT_BUDGET_EXCEEDED")
        self.assertEqual(response["run_id"], "run_01")
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
