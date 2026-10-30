"""Offline presentation contracts; these do not score real-model language quality."""
import asyncio
import json
from pathlib import Path
import re
import unittest
from unittest.mock import AsyncMock, Mock

from pydantic import ValidationError
from app.contracts import CognitiveDecision, CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_step_models import CognitiveStepResult, TerminalCognitiveStep, DialogueContinuationStep, RecallOutcome
from app.model_requests import CognitiveModelRequestFactory
from app.reply_language import hard_limit, response_schema
from app.language_projection import english_character, semantic_hash
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import MockProvider, ModelResult
from tests.test_cognitive_request import _event_request
from tests.app_test_isolation import import_isolated_app
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


def request(language="en-US"):
    value = _event_request()
    value["reply_language"] = language
    value["context"]["current_event"].update(event_type="player.utterance",
        target_entity_id="npc_cafe_01", payload=[dict(key="text", value="goodmorning")])
    return CognitiveRequest.model_validate(value)


class ReplyLanguageTests(unittest.IsolatedAsyncioTestCase):
    def test_source_hash_and_custom_override_for_all_four_defaults(self):
        root = Path(__file__).resolve().parents[2] / "UnityClient/Assets/CafeDemo/V2/Characters/Personas"
        for name, char in (("Merchant", "cafe.merchant"), ("RegularCustomer", "cafe.regular_customer"),
                           ("CompanionCustomer", "cafe.companion_customer"), ("QuietCustomer", "cafe.quiet_customer")):
            asset = (root / f"CafeV2{name}Character.asset").read_text(encoding="utf-8-sig")
            source = {"character_id": char}
            for src, dst in (("displayName", "display_name"), ("roleSummary", "role_summary"),
                             ("backstory", "backstory"), ("speakingStyle", "speaking_style"),
                             ("personality", "personality"), ("boundaries", "boundaries")):
                source[dst] = json.loads(re.search(r"^  " + src + r": (.*)$", asset, re.M)[1])
            projected = english_character(source)
            self.assertNotEqual(semantic_hash(source), semantic_hash(projected))
            self.assertEqual(source.keys(), projected.keys())
            for field in source:
                override = dict(source, **{field: source[field] + " (custom)"})
                self.assertEqual(override, english_character(override))
            req = request().model_copy(update={"context": request().context.model_copy(update={
                "character": type(request().context.character).model_validate(source)})})
            before = req.model_dump_json()
            model = CognitiveModelRequestFactory.create_event(req, knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
            profile = json.loads(next(l.split(": ", 1)[1] for l in model.messages[-1].content.splitlines()
                if l.startswith("Cognitive context JSON: ")))["character"]
            self.assertEqual(projected, profile)
            self.assertEqual(before, req.model_dump_json())
            self.assertEqual(char, CognitiveModelRequestFactory.context_from(model).character.character_id)

    def test_locale_schema_and_pydantic_boundary_for_all_spoken_variants(self):
        for locale, soft, hard in (("zh-CN", 100, 150), ("en-US", 200, 250)):
            for model in (CognitiveStepResult, TerminalCognitiveStep, DialogueContinuationStep):
                schema = response_schema(model, locale)
                self.assertIn(f'"maxLength": {hard}', json.dumps(schema))
                for size in (soft, soft + 1, hard):
                    # Astral emoji count as one scalar in Python and Unity.
                    text = "😀" * size
                    value = model.model_validate(dict(decision_kind="DIRECT_RESPONSE", utterance=text),
                        context={"reply_language": locale}).root
                    self.assertEqual(text, value.utterance)
                with self.assertRaises(ValidationError):
                    model.model_validate(dict(decision_kind="DIRECT_RESPONSE", utterance="x" * (hard + 1)),
                        context={"reply_language": locale})
            with self.assertRaises(ValidationError):
                CognitiveDecision.model_validate(dict(decision_kind="DIRECT_RESPONSE", utterance="\ud800"),
                    context={"reply_language": locale})
            decision = dict(decision_kind="ACTION_PROPOSAL", utterance="x" * hard,
                action_proposal=dict(action_id="prepare_drink", bindings={"drink_type": "latte"}))
            self.assertEqual(hard, len(CognitiveDecision.model_validate(decision,
                context={"reply_language": locale}).utterance))
            with self.assertRaises(ValidationError):
                CognitiveDecision.model_validate({**decision, "utterance": "x" * (hard+1)}, context={"reply_language": locale})

    async def test_formal_run_soft_limit_keeps_verbatim_hard_limit_fails_without_retry(self):
        for locale in ("zh-CN", "en-US"):
            for size in (hard_limit(locale), hard_limit(locale) + 1):
                text = "a" * size
                provider = Mock(generate=AsyncMock(return_value=ModelResult(text=json.dumps(dict(
                    decision_kind="DIRECT_RESPONSE", utterance=text)))))
                entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
                if size <= hard_limit(locale):
                    self.assertEqual(text, (await entry.run(request(locale))).decision.utterance)
                else:
                    with self.assertRaises(ValidationError): await entry.run(request(locale))
                self.assertEqual(1, provider.generate.await_count)

    async def test_step0_step1_same_locale_and_source_and_schema(self):
        models = []
        async def generate(model):
            models.append(model)
            return ModelResult(text=json.dumps(dict(decision_kind="MEMORY_RECALL", recall_plan={
                "semantic_query": "reported preference"}) if len(models) == 1 else dict(
                decision_kind="DIRECT_RESPONSE", utterance="x" * 220)))
        req = request()
        entry = CognitiveBackendEntry(Mock(generate=generate), PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
        result = await entry.run(req)
        self.assertEqual(220, len(result.decision.utterance))
        for model in models:
            self.assertIn("Trusted reply_language = en-US", model.messages[-1].content)
            self.assertIn("goodmorning", model.messages[-1].content)
            self.assertIn('"maxLength": 250', json.dumps(model.response_schema))
        self.assertNotIn("MemoryRecallStep", models[1].response_schema["$defs"])

    def test_legacy_fallback_invalid_locale_and_no_action_protocol_widening(self):
        value = request().model_dump(); value.pop("reply_language")
        self.assertEqual("zh-CN", CognitiveRequest.model_validate(value).reply_language)
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate({**value, "reply_language": "auto"})
        en, zh = response_schema(CognitiveStepResult, "en-US"), response_schema(CognitiveStepResult, "zh-CN")
        def neutralize(v):
            if isinstance(v, dict): return {k: neutralize(x) for k, x in v.items() if k != "maxLength"}
            if isinstance(v, list): return [neutralize(x) for x in v]
            return v
        self.assertEqual(neutralize(en), neutralize(zh))


    def test_formal_websocket_length_contract_preserves_full_reply_or_errors(self):
        from fastapi.testclient import TestClient
        m3_main = import_isolated_app()
        from unittest.mock import patch
        for locale, size, success in (("en-US", 220, True), ("zh-CN", 150, True),
                                      ("zh-CN", 151, False), ("en-US", 251, False)):
            text = "a" * size
            entry = CognitiveBackendEntry(MockProvider(responder=lambda _: ModelResult(text=json.dumps(
                dict(decision_kind="DIRECT_RESPONSE", utterance=text)))), PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
            # Retain private identity metadata (excluded from ordinary model_dump).
            wire = _event_request()
            wire.update(reply_language=locale)
            wire["context"]["current_event"].update(event_type="player.utterance", target_entity_id="npc_cafe_01",
                payload=[dict(key="text", value="hello")])
            with patch.object(m3_main, "cognitive_entry", entry):
                with TestClient(m3_main.app).websocket_connect("/ws") as websocket:
                    websocket.send_json(wire); response = websocket.receive_json()
            self.assertEqual("cognitive_response" if success else "error", response["message_type"])
            if success: self.assertEqual(text, response["decision"]["utterance"])
            else: self.assertNotIn("decision", response)
