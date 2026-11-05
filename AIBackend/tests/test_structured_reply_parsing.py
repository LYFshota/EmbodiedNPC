"""Synthetic replies only; no credentials, network, or player data."""
import json
import unittest
import httpx
from app.cognitive_step_models import TaskCognitiveStep
from app.model_requests import _task_response_schema
from app.model_schema_projection import unwrap_text
from app.model_config_runtime import build_main_provider
from app.providers import ModelRequest, ModelMessage, InferenceOptions, ProviderError
from app.structured_output import extract_json_object, StructuredOutputError
from app.model_compatibility import _CACHE
from tests.test_model_compatibility import config, good
from app.model_api_config_api import _test_main

VALID = {"decision_kind": "DIRECT_RESPONSE", "utterance": "hello", "future_commitment": False}
SCHEMA = _task_response_schema(TaskCognitiveStep, "zh-CN")
REQUEST = ModelRequest(messages=(ModelMessage(role="user", content="Synthetic NPC input"),),
    response_schema=SCHEMA, inference_options=InferenceOptions(max_output_tokens=1024))


class StructuredReplyParsingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        _CACHE.clear()

    async def test_consecutive_replies_accept_envelopes_without_retry_or_state_pollution(self):
        plain = json.dumps(VALID)
        replies = [plain, "```json\n" + plain + "\n```", "Here is the result: " + plain,
                   json.dumps({"decision_kind": "DIRECT_RESPONSE", "utterance": "hello"}), plain]
        calls = []
        def send(request):
            calls.append(json.loads(request.content))
            return good(replies[len(calls) - 1])
        provider = build_main_provider(config(), httpx.MockTransport(send))
        for index in range(len(replies)):
            if index == 3:
                with self.assertRaises(ProviderError) as caught:
                    await provider.generate(REQUEST)
                self.assertEqual(caught.exception.diagnostic_category, "RESPONSE_FIELDS_INVALID")
                self.assertEqual(caught.exception.field_path, "$.future_commitment")
            else:
                result = await provider.generate(REQUEST)
                TaskCognitiveStep.model_validate(extract_json_object(result.text))
                self.assertEqual(json.loads(result.text), VALID)
        self.assertEqual(len(calls), len(replies))
        self.assertTrue(all(call == calls[0] for call in calls))
        self.assertFalse(_CACHE)

    async def test_invalid_responses_keep_strict_validation_and_safe_diagnostics(self):
        cases = [
            ("not JSON private-sentinel", "RESPONSE_JSON_INVALID"),
            ('{"decision_kind":', "RESPONSE_JSON_INVALID"),
            (json.dumps({**VALID, "future_commitment": "private-sentinel"}), "RESPONSE_FIELDS_INVALID"),
            (json.dumps({**VALID, "private-sentinel": True}), "RESPONSE_FIELDS_INVALID"),
            (json.dumps({**VALID, "utterance": "x" * 151}), "RESPONSE_TOO_LONG"),
            (json.dumps(VALID) + json.dumps(VALID), "RESPONSE_JSON_INVALID"),
            (json.dumps(VALID) + " trailing private-sentinel", "RESPONSE_JSON_INVALID"),
            ('{"decision_kind":"DIRECT_RESPONSE","utterance":"hello","future_commitment":false,"future_commitment":true}', "RESPONSE_JSON_INVALID"),
        ]
        for content, category in cases:
            with self.subTest(category=category, length=len(content)):
                provider = build_main_provider(config(), httpx.MockTransport(lambda request: good(content)))
                with self.assertLogs("embodiednpc.backend.model_output", level="WARNING") as logs:
                    with self.assertRaises(ProviderError) as caught:
                        await provider.generate(REQUEST)
                error = caught.exception
                self.assertEqual(str(error), "MODEL_SCHEMA_ENVELOPE_INVALID")
                self.assertEqual(error.diagnostic_category, category)
                self.assertNotIn("private-sentinel", repr(vars(error)) + str(error) + " ".join(logs.output))
                self.assertFalse(_CACHE)

    async def test_reasoning_and_budget_fail_before_envelope_normalization(self):
        content = "```json\n" + json.dumps(VALID) + "\n```"
        provider = build_main_provider(config(), httpx.MockTransport(lambda request: good(content, reasoning_content="hidden")))
        with self.assertRaisesRegex(ProviderError, "THINKING_CONTROL_UNVERIFIED"):
            await provider.generate(REQUEST)
        def truncated(request):
            return httpx.Response(200, json={"choices": [{"message": {"content": content}, "finish_reason": "length"}]})
        with self.assertRaisesRegex(ProviderError, "OUTPUT_TRUNCATED"):
            await build_main_provider(config(), httpx.MockTransport(truncated)).generate(REQUEST)
        bounded = REQUEST.model_copy(update={"inference_options": InferenceOptions(max_output_tokens=7, output_token_reserve=7)})
        with self.assertRaisesRegex(ProviderError, "OUTPUT_RESERVE_VIOLATED"):
            await build_main_provider(config(), httpx.MockTransport(lambda request: good(content))).generate(bounded)

    def test_single_result_mode_does_not_change_legacy_parser_or_length_limit(self):
        raw = json.dumps(VALID)
        self.assertEqual(extract_json_object(raw + " trailing"), VALID)
        with self.assertRaises(StructuredOutputError):
            extract_json_object(raw + raw, require_single_result=True)
        with self.assertRaisesRegex(StructuredOutputError, "MODEL_OUTPUT_TOO_LONG"):
            extract_json_object(" " * 4001 + raw, require_single_result=True)
        fenced = "```json\n" + raw + "\n```"
        self.assertEqual(unwrap_text(fenced, False, SCHEMA, "raw"), fenced)

    def test_dynamic_map_keys_are_never_logged_as_field_paths(self):
        schema = {"type": "object", "additionalProperties": {"type": "integer"}}
        with self.assertLogs("embodiednpc.backend.model_output", level="WARNING") as logs:
            with self.assertRaises(ProviderError) as caught:
                unwrap_text('{"private-sentinel":"secret-value"}', False, schema, "json_object")
        self.assertEqual(caught.exception.field_path, "$.*")
        for sentinel in ("private-sentinel", "secret-value"):
            self.assertNotIn(sentinel, repr(vars(caught.exception)) + " ".join(logs.output))

    async def test_connection_test_reports_same_safe_categories(self):
        for content, category in [('not JSON', 'RESPONSE_JSON_INVALID'),
                                  ('{"ok":"wrong type"}', 'RESPONSE_FIELDS_INVALID')]:
            with self.assertLogs("embodiednpc.backend.model_output", level="WARNING"):
                result = await _test_main(config(), httpx.MockTransport(lambda request: good(content)))
            self.assertFalse(result['success'])
            self.assertEqual(result['error_code'], category)

    async def test_contract_probe_reports_same_category_and_stops_after_failure(self):
        from app.model_profile_probes import contract_probe
        calls = []
        def send(request):
            calls.append(request)
            return good('{"utterance":"hello"}')
        with self.assertLogs("embodiednpc.backend.model_output", level="WARNING"):
            result = await contract_probe(config(), httpx.MockTransport(send))
        self.assertFalse(result['success'])
        self.assertEqual(result['error_code'], 'RESPONSE_FIELDS_INVALID')
        self.assertEqual(len(calls), 1)
        self.assertTrue(all(case['status'] == 'NOT_RUN' for case in result['results'][1:]))
