"""Offline DeepSeek Responses transport and preset routing tests."""

import asyncio
import contextlib
import hashlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx

from app.providers import InferenceOptions, ModelMessage, ModelRequest
from tools.r4cc_cloud_adapter import CloudApiTestError, CloudApiTestProvider
from tools.r4cc_cloud_model_smoke import (
    cloud_from_environment, main, preset_safe_metadata,
)
from tools.r4cc_deepseek_responses_adapter import (
    DeepSeekResponsesEvaluationError, DeepSeekResponsesEvaluationProvider,
)


KEY = "fake-test-key"
QUERY_SECRET = "fake-query-secret"
URL = "https://api.test/responses?secret_query=" + QUERY_SECRET


def model_request(schema=None) -> ModelRequest:
    return ModelRequest(
        messages=(ModelMessage(role="system", content="Keep this system text."),
                  ModelMessage(role="user", content="Keep this user text.")),
        response_schema=schema or {"type": "object", "properties": {
            "utterance": {"type": "string"}}, "required": ["utterance"],
            "additionalProperties": False},
        inference_options=InferenceOptions(temperature=0.3, think=False,
                                           max_output_tokens=123),
    )


def response_body(text='{"utterance":"你好"}', *, status="completed", usage=None):
    return {"id": "resp_test", "object": "response", "status": status,
            "output": [
                {"type": "reasoning", "content": [{"type": "reasoning_text",
                                                    "text": "not final text"}]},
                {"type": "message", "role": "assistant", "content": [
                    {"type": "output_text", "text": text}]},
            ],
            "usage": usage if usage is not None else {"input_tokens": 11,
                                                       "output_tokens": 7}}


class DeepSeekResponsesAdapterTests(unittest.IsolatedAsyncioTestCase):
    def provider(self):
        return DeepSeekResponsesEvaluationProvider(
            url=URL, api_key=KEY, model="deepseek-flash", schema_mode="json_schema",
            timeout_seconds=2)

    async def mock_generate(self, handler, request=None):
        original_client = httpx.AsyncClient
        with patch("tools.r4cc_deepseek_responses_adapter.httpx.AsyncClient",
                   side_effect=lambda **kwargs: original_client(
                       transport=httpx.MockTransport(handler), **kwargs)):
            return await self.provider().generate(request or model_request())

    async def test_request_mapping_output_extraction_usage_and_secret_safety(self):
        captured = {}

        def handler(request: httpx.Request):
            captured["url"] = str(request.url)
            captured["auth_ok"] = request.headers.get("Authorization") == "Bearer " + KEY
            captured["body"] = json.loads(request.content)
            return httpx.Response(200, json=response_body())

        result = await self.mock_generate(handler)
        body = captured["body"]
        self.assertEqual(URL, captured["url"])
        self.assertTrue(captured["auth_ok"])
        self.assertEqual("deepseek-flash", body["model"])
        self.assertEqual([{"role": m.role, "content": m.content}
                          for m in model_request().messages], body["input"])
        self.assertEqual(0.3, body["temperature"])
        self.assertEqual(123, body["max_output_tokens"])
        self.assertEqual({"effort": "none"}, body["reasoning"])
        self.assertFalse(body["stream"])
        self.assertEqual("json_schema", body["text"]["format"]["type"])
        self.assertEqual("embodiednpc_cognitive_result", body["text"]["format"]["name"])
        self.assertNotIn("strict", body["text"]["format"])
        self.assertEqual('{"utterance":"你好"}', result.text)
        self.assertEqual("completed", result.finish_reason)
        self.assertEqual((11, 7), (result.usage.prompt_tokens,
                                    result.usage.completion_tokens))
        self.assertNotIn(KEY, json.dumps(body))
        self.assertNotIn(QUERY_SECRET, repr(self.provider()))
        self.assertNotIn(KEY, repr(self.provider()))

    async def test_source_schema_is_unchanged_for_simple_step0_and_step1(self):
        from tools import r4cc_model_ab_eval as common
        from tools.r4cc_four_model_fixtures import build_requests

        provider = self.provider()
        old_out = common.OUT
        try:
            with tempfile.TemporaryDirectory() as directory:
                common.OUT = Path(directory)
                requests = build_requests(common.canonical_fixture())
                for request in (model_request(), requests["G0"][1], requests["G1"][1]):
                    source = request.response_schema
                    sent = provider.build_payload(request)["text"]["format"]["schema"]
                    self.assertEqual(source, sent)
                    digest = lambda value: hashlib.sha256(json.dumps(
                        value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
                    self.assertEqual(digest(source), digest(sent))
        finally:
            common.OUT = old_out

    async def test_json_object_and_thinking_true_mapping(self):
        provider = self.provider()
        provider.schema_mode = "json_object"
        request = model_request().model_copy(update={
            "inference_options": InferenceOptions(temperature=0.5, think=True)})
        payload = provider.build_payload(request)
        self.assertEqual({"type": "json_object"}, payload["text"]["format"])
        self.assertNotIn("reasoning", payload)
        self.assertNotIn("max_output_tokens", payload)
        assistant_request = model_request().model_copy(update={
            "messages": model_request().messages +
            (ModelMessage(role="assistant", content="Keep this assistant text."),)})
        self.assertEqual("Keep this assistant text.",
                         provider.build_payload(assistant_request)["input"][2]["content"])

    async def test_http_statuses_timeout_and_body_never_leak_secrets(self):
        for status in (400, 401, 422, 429, 500):
            with self.subTest(status=status):
                with self.assertRaises(DeepSeekResponsesEvaluationError) as caught:
                    await self.mock_generate(
                        lambda _: httpx.Response(status, text=KEY + " " + QUERY_SECRET))
                self.assertEqual("DEEPSEEK_RESPONSES_HTTP_ERROR", caught.exception.category)
                self.assertEqual(status, caught.exception.status_code)
                self.assertNotIn(KEY, repr(caught.exception))
                self.assertNotIn(QUERY_SECRET, str(caught.exception))
        def timeout(request):
            raise httpx.ReadTimeout(KEY, request=request)
        with self.assertRaises(DeepSeekResponsesEvaluationError) as caught:
            await self.mock_generate(timeout)
        self.assertEqual("DEEPSEEK_RESPONSES_TIMEOUT", caught.exception.category)
        self.assertNotIn(KEY, str(caught.exception))
        self.assertIsNone(caught.exception.__cause__)

    async def test_confirmed_schema_keyword_reports_path_without_response_body(self):
        schema = {"type": "object", "properties": {
            "utterance": {"type": "string", "pattern": "^.+$"}},
            "required": ["utterance"], "additionalProperties": False}
        error_body = {"error": {
            "param": "text.format.schema.properties.utterance.pattern",
            "message": KEY + " " + QUERY_SECRET}}
        with self.assertRaises(DeepSeekResponsesEvaluationError) as caught:
            await self.mock_generate(
                lambda _: httpx.Response(422, json=error_body), model_request(schema))
        self.assertEqual("DEEPSEEK_SCHEMA_UNSUPPORTED", caught.exception.category)
        self.assertEqual("$.properties.utterance.pattern", caught.exception.schema_path)
        self.assertEqual("pattern", caught.exception.schema_keyword)
        self.assertNotIn(KEY, repr(caught.exception))
        self.assertNotIn(QUERY_SECRET, str(caught.exception))

    async def test_malformed_empty_incomplete_and_usage_absent(self):
        with self.assertRaises(DeepSeekResponsesEvaluationError) as caught:
            await self.mock_generate(lambda _: httpx.Response(200, json={"status": "completed",
                                                                         "output": {}}))
        self.assertEqual("DEEPSEEK_RESPONSES_PROTOCOL_ERROR", caught.exception.category)
        with self.assertRaises(DeepSeekResponsesEvaluationError) as caught:
            await self.mock_generate(lambda _: httpx.Response(200, json=response_body("  ")))
        self.assertEqual("DEEPSEEK_RESPONSES_EMPTY_RESPONSE", caught.exception.category)
        incomplete = response_body(status="incomplete", usage={})
        incomplete["incomplete_details"] = {"reason": "max_output_tokens"}
        result = await self.mock_generate(lambda _: httpx.Response(200, json=incomplete))
        self.assertEqual("incomplete:max_output_tokens", result.finish_reason)
        self.assertIsNone(result.usage.prompt_tokens)
        self.assertIsNone(result.usage.completion_tokens)


class PresetRoutingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "models.json"

    def write(self, entry):
        self.path.write_text(json.dumps({"default_model": "selected",
                                         "models": {"selected": entry}}), encoding="utf-8")

    def entry(self, **changes):
        value = {"endpoint": URL, "model": "deepseek-flash", "api_key": KEY,
                 "schema_mode": "json_schema", "timeout_seconds": 60}
        value.update(changes)
        return value

    def test_legacy_explicit_gateway_and_official(self):
        self.write(self.entry())
        self.assertIsInstance(cloud_from_environment(self.path), CloudApiTestProvider)
        self.write(self.entry(provider="openai_compatible", api_style="chat_completions"))
        self.assertIsInstance(cloud_from_environment(self.path), CloudApiTestProvider)
        self.write(self.entry(provider="deepseek_official", api_style="responses"))
        provider = cloud_from_environment(self.path)
        self.assertIsInstance(provider, DeepSeekResponsesEvaluationProvider)
        meta = preset_safe_metadata(self.path)
        self.assertEqual("deepseek_official", meta["provider"])
        self.assertEqual("responses", meta["api_style"])
        self.assertNotIn(KEY, json.dumps(meta))
        self.assertNotIn(QUERY_SECRET, json.dumps(meta))

    def test_bad_provider_style_missing_fields_and_unknown_fields(self):
        cases = (
            (self.entry(provider="unknown"), "API_PROVIDER_UNSUPPORTED"),
            (self.entry(provider="deepseek_official", api_style="chat_completions"),
             "API_STYLE_UNSUPPORTED"),
            (self.entry(endpoint=""), "API_CONFIG_ERROR"),
            (self.entry(model=""), "API_CONFIG_ERROR"),
            (self.entry(api_key=""), "API_CONFIG_ERROR"),
        )
        for entry, category in cases:
            with self.subTest(category=category):
                self.write(entry)
                with self.assertRaises(CloudApiTestError) as caught:
                    cloud_from_environment(self.path)
                self.assertEqual(category, caught.exception.category)
                self.assertNotIn(KEY, str(caught.exception))
                self.assertNotIn(QUERY_SECRET, str(caught.exception))
        self.write(self.entry(provider="deepseek_official", api_style="responses",
                              unused_local_note="ignored"))
        self.assertIsInstance(cloud_from_environment(self.path),
                              DeepSeekResponsesEvaluationProvider)

    def test_missing_key_env_is_safe_and_env_has_priority(self):
        self.write(self.entry(provider="deepseek_official", api_style="responses",
                              api_key_env="R4CC_FAKE_TEST_KEY"))
        with patch.dict(os.environ, {}, clear=True), \
                self.assertRaises(CloudApiTestError) as caught:
            cloud_from_environment(self.path)
        self.assertEqual("API_KEY_ENV_MISSING", caught.exception.category)
        self.assertNotIn(KEY, str(caught.exception))
        with patch.dict(os.environ, {"R4CC_FAKE_TEST_KEY": "fake-env-key"}, clear=True):
            provider = cloud_from_environment(self.path)
        self.assertEqual("[REDACTED]", provider.redact("fake-env-key"))
        self.assertEqual(KEY, provider.redact(KEY))

    def test_dry_run_builds_safe_metadata_without_http(self):
        self.write(self.entry(provider="deepseek_official", api_style="responses"))
        stdout = io.StringIO()
        args = ["r4cc_cloud_model_smoke.py", "--preset-file", str(self.path),
                "--preset", "selected", "--cases", "B,E,G0,G1,H1",
                "--samples", "3", "--dry-run"]
        with patch.object(sys, "argv", args), \
                patch("tools.r4cc_deepseek_responses_adapter.httpx.AsyncClient",
                      side_effect=AssertionError("HTTP must not run")), \
                contextlib.redirect_stdout(stdout):
            self.assertEqual(0, asyncio.run(main()))
        output = stdout.getvalue()
        metadata = json.loads(output)
        self.assertEqual("selected", metadata["preset"])
        self.assertEqual("deepseek_official", metadata["provider"])
        self.assertEqual(metadata["source_schema_digest"],
                         metadata["sent_schema_digest"])
        self.assertNotIn(KEY, output)
        self.assertNotIn(QUERY_SECRET, output)
        self.assertNotIn("Authorization", output)

    def test_selected_case_runner_uses_official_adapter_with_mock_http(self):
        self.write(self.entry(provider="deepseek_official", api_style="responses"))
        original_client = httpx.AsyncClient
        requests = []

        def handler(request):
            requests.append(request)
            return httpx.Response(200, json=response_body(
                '{"decision_kind":"DIRECT_RESPONSE","utterance":"好的，我记住了。"}'))

        with tempfile.TemporaryDirectory() as output_dir:
            stdout = io.StringIO()
            args = ["r4cc_cloud_model_smoke.py", "--preset-file", str(self.path),
                    "--preset", "selected", "--cases", "B", "--samples", "1"]
            with patch.object(sys, "argv", args), \
                    patch("tools.r4cc_cloud_model_smoke.tempfile.gettempdir",
                          return_value=output_dir), \
                    patch("tools.r4cc_deepseek_responses_adapter.httpx.AsyncClient",
                          side_effect=lambda **kwargs: original_client(
                              transport=httpx.MockTransport(handler), **kwargs)), \
                    contextlib.redirect_stdout(stdout):
                self.assertEqual(0, asyncio.run(main()))
            self.assertEqual(1, len(requests))
            paths = list(Path(output_dir).glob("r4cc-selected-model-*.jsonl"))
            self.assertEqual(1, len(paths))
            report = paths[0].read_text(encoding="utf-8")
            row = json.loads(report)
            self.assertEqual("selected", row["preset"])
            self.assertEqual("deepseek_official_responses", row["provider"])
            self.assertNotIn(KEY, report + stdout.getvalue())
            self.assertNotIn(QUERY_SECRET, report + stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
