"""Offline contract tests for the R4-C-C evaluation-only Cloud transport."""

import json
import os
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

import httpx

from app.providers import InferenceOptions, ModelMessage, ModelRequest, ModelResult
from tools.r4cc_cloud_adapter import CloudApiTestError, CloudApiTestProvider
from tools.r4cc_cloud_model_smoke import (
    case_model_requests, cloud_from_environment, evaluate_one, local_config_path,
)


SCHEMA = {"type": "object", "properties": {"ok": {"type": "boolean"}},
          "required": ["ok"], "additionalProperties": False}


def request_fixture() -> ModelRequest:
    return ModelRequest(
        messages=(ModelMessage(role="system", content="You return JSON."),
                  ModelMessage(role="user", content='Return {"ok": true}.')),
        response_schema=SCHEMA,
        inference_options=InferenceOptions(temperature=0, think=False, max_output_tokens=17),
    )


class CloudAdapterTests(unittest.IsolatedAsyncioTestCase):
    async def _run_mock(self, handler, *, mode="json_schema", token_field="max_tokens"):
        original_client = httpx.AsyncClient
        with patch("tools.r4cc_cloud_adapter.httpx.AsyncClient",
                   side_effect=lambda **kwargs: original_client(
                       transport=httpx.MockTransport(handler), **kwargs)):
            provider = CloudApiTestProvider(
                url="https://api.test/custom/chat", api_key="secret-test-key",
                model="cloud-eval", schema_mode=mode, timeout_seconds=1,
                token_field=token_field)
            return await provider.generate(request_fixture())

    async def test_exact_model_request_mapping_and_response_parse(self):
        captured = {}

        def handler(request: httpx.Request) -> httpx.Response:
            captured["url"] = str(request.url)
            captured["authorized"] = request.headers.get("Authorization") == "Bearer secret-test-key"
            captured["body"] = json.loads(request.content)
            return httpx.Response(200, json={
                "choices": [{"message": {"content": '{"ok": true}'}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 11, "completion_tokens": 5},
            })

        result = await self._run_mock(handler)
        self.assertEqual("https://api.test/custom/chat", captured["url"])
        self.assertTrue(captured["authorized"])
        body = captured["body"]
        self.assertEqual("cloud-eval", body["model"])
        self.assertEqual([message.model_dump() for message in request_fixture().messages], body["messages"])
        self.assertEqual(0, body["temperature"])
        self.assertEqual(17, body["max_tokens"])
        self.assertEqual(SCHEMA, body["response_format"]["json_schema"]["schema"])
        self.assertEqual("cognitive_response", body["response_format"]["json_schema"]["name"])
        self.assertTrue(body["response_format"]["json_schema"]["strict"])
        self.assertEqual('{"ok": true}', result.text)
        self.assertEqual("stop", result.finish_reason)
        self.assertEqual((11, 5), (result.usage.prompt_tokens, result.usage.completion_tokens))
        self.assertNotIn("secret-test-key", json.dumps(body))

    async def test_explicit_schema_modes_and_token_field(self):
        bodies = []

        def handler(request: httpx.Request) -> httpx.Response:
            bodies.append(json.loads(request.content))
            return httpx.Response(200, json={
                "choices": [{"message": {"content": '{"ok":true}'}, "finish_reason": "stop"}],
            })

        await self._run_mock(handler, mode="json_object", token_field="max_completion_tokens")
        await self._run_mock(handler, mode="none")
        self.assertEqual({"type": "json_object"}, bodies[0]["response_format"])
        self.assertEqual(17, bodies[0]["max_completion_tokens"])
        self.assertNotIn("response_format", bodies[1])

    async def test_schema_rejection_is_explicit_and_does_not_retry(self):
        calls = []

        def handler(request: httpx.Request) -> httpx.Response:
            calls.append(request)
            return httpx.Response(400, json={"error": {"message": "schema rejected"}})

        with self.assertRaises(CloudApiTestError) as caught:
            await self._run_mock(handler)
        self.assertEqual("API_SCHEMA_UNSUPPORTED", caught.exception.category)
        self.assertEqual(400, caught.exception.status_code)
        self.assertEqual(1, len(calls))

    async def test_http_failure_and_exception_do_not_expose_key(self):
        def handler(request: httpx.Request) -> httpx.Response:
            return httpx.Response(503, text="secret-test-key echoed by remote")

        with self.assertRaises(CloudApiTestError) as caught:
            await self._run_mock(handler)
        self.assertEqual("API_ERROR", caught.exception.category)
        self.assertNotIn("secret-test-key", str(caught.exception))
        self.assertNotIn("secret-test-key", repr(caught.exception))
        self.assertIsNone(caught.exception.__cause__)
        provider = CloudApiTestProvider(url="https://api.test/chat", api_key="secret-test-key",
                                        model="cloud-eval")
        self.assertNotIn("secret-test-key", repr(provider))
        self.assertEqual("reply [REDACTED]", provider.redact("reply secret-test-key"))

    async def test_malformed_response_is_protocol_error(self):
        def handler(request: httpx.Request) -> httpx.Response:
            return httpx.Response(200, json={"choices": [{"message": {"content": None}}]})

        with self.assertRaises(CloudApiTestError) as caught:
            await self._run_mock(handler)
        self.assertEqual("API_PROTOCOL_ERROR", caught.exception.category)

    async def test_four_cases_use_one_factory_request_for_both_providers(self):
        class FakeProvider:
            def __init__(self, name):
                self.name, self.model = name, name
                self.seen = []

            async def generate(self, model_request):
                self.seen.append(model_request)
                return ModelResult(text='{"decision_kind":"DIRECT_RESPONSE","utterance":"你好"}')

        local, cloud = FakeProvider("local"), FakeProvider("cloud_test")
        requests = case_model_requests()
        self.assertEqual(["A", "B", "C", "D"], [case for case, _ in requests])
        for case, model_request in requests:
            await evaluate_one(case, local, model_request)
            await evaluate_one(case, cloud, model_request)
        self.assertTrue(all(a is b for a, b in zip(local.seen, cloud.seen)))
        self.assertEqual(4, len(local.seen))

    async def test_missing_key_uses_visible_paste_input(self):
        with patch.dict(os.environ, {
            "R4CC_API_URL": "https://api.test/chat",
            "R4CC_API_MODEL": "cloud-eval",
            "R4CC_API_KEY": "",
            "R4CC_CONFIG_FILE": str(Path(tempfile.gettempdir()) / "r4cc-nonexistent-test-config.json"),
        }), patch("tools.r4cc_cloud_model_smoke.sys.stdin.isatty", return_value=True), \
                patch("builtins.input", return_value="paste-test-key") as visible_input:
            provider = cloud_from_environment()
        visible_input.assert_called_once_with("API key (visible; paste here): ")
        self.assertNotIn("paste-test-key", repr(provider))

    async def test_local_config_is_used_without_prompt_and_environment_can_override(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cloud.json"
            path.write_text(json.dumps({
                "R4CC_API_URL": "https://config.test/chat",
                "R4CC_API_MODEL": "config-model",
                "R4CC_API_KEY": "fake-config-key",
                "R4CC_API_SCHEMA_MODE": "json_object",
                "R4CC_API_TIMEOUT_SECONDS": 45,
            }), encoding="utf-8")
            with patch.dict(os.environ, {"R4CC_CONFIG_FILE": str(path),
                                      "R4CC_API_URL": "", "R4CC_API_MODEL": "",
                                      "R4CC_API_KEY": "", "R4CC_API_SCHEMA_MODE": "",
                                      "R4CC_API_TIMEOUT_SECONDS": ""}, clear=True), \
                    patch("builtins.input", side_effect=AssertionError("unexpected prompt")):
                provider = cloud_from_environment()
            self.assertEqual("https://config.test/chat", provider.url)
            self.assertEqual("config-model", provider.model)
            self.assertEqual("json_object", provider.schema_mode)
            self.assertEqual(45, provider.timeout_seconds)
            self.assertNotIn("fake-config-key", repr(provider))
            with patch.dict(os.environ, {"R4CC_CONFIG_FILE": str(path),
                                      "R4CC_API_MODEL": "env-model"}, clear=True):
                self.assertEqual("env-model", cloud_from_environment().model)

    async def test_external_preset_reads_key_only_from_named_environment_variable(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "preset.json"
            path.write_text(json.dumps({
                "preset": "offline-test", "endpoint": "https://preset.test/chat",
                "model": "preset-model", "api_key_env": "TEST_R4CC_KEY",
                "schema_mode": "json_object", "timeout_seconds": 42,
            }), encoding="utf-8")
            with patch.dict(os.environ, {"TEST_R4CC_KEY": "fake-preset-key"}, clear=True), \
                    patch("builtins.input", side_effect=AssertionError("unexpected prompt")):
                provider = cloud_from_environment(path)
            self.assertEqual("https://preset.test/chat", provider.url)
            self.assertEqual("preset-model", provider.model)
            self.assertEqual("json_object", provider.schema_mode)
            self.assertEqual(42, provider.timeout_seconds)
            self.assertNotIn("fake-preset-key", repr(provider))
            with patch.dict(os.environ, {}, clear=True), \
                    self.assertRaises(CloudApiTestError) as caught:
                cloud_from_environment(path)
            self.assertEqual("API_KEY_ENV_MISSING", caught.exception.category)

    async def test_external_preset_prefers_key_env_and_rejects_missing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "preset.json"
            path.write_text(json.dumps({
                "endpoint": "https://preset.test/chat", "model": "preset-model",
                "api_key_env": "TEST_R4CC_KEY", "api_key": "fake-inline-key",
            }), encoding="utf-8")
            with patch.dict(os.environ, {"TEST_R4CC_KEY": "fake-preset-key"}, clear=True):
                provider = cloud_from_environment(path)
            self.assertEqual("[REDACTED]", provider.redact("fake-preset-key"))
            self.assertEqual("fake-inline-key", provider.redact("fake-inline-key"))
            with patch.dict(os.environ, {}, clear=True), \
                    self.assertRaises(CloudApiTestError) as caught:
                cloud_from_environment(path)
            self.assertEqual("API_KEY_ENV_MISSING", caught.exception.category)
            self.assertNotIn("fake-inline-key", str(caught.exception))
            with self.assertRaises(CloudApiTestError) as caught:
                cloud_from_environment(Path(directory) / "missing.json")
            self.assertEqual("API_CONFIG_ERROR", caught.exception.category)

    async def test_five_model_config_selects_default_or_named_entry(self):
        with tempfile.TemporaryDirectory() as directory:
            config_dir = Path(directory) / "EmbodiedNPC"
            config_dir.mkdir()
            path = config_dir / "r4cc-models.json"
            models = {f"model-{number}": {
                "endpoint": "", "model": "", "api_key": "",
                "schema_mode": "json_object",
            } for number in range(1, 6)}
            models["model-1"].update(endpoint="https://one.test/chat",
                                      model="first-model", api_key="fake-first-key")
            models["model-2"].update(endpoint="https://two.test/chat",
                                      model="second-model", api_key="fake-second-key")
            path.write_text(json.dumps({"default_model": "model-1", "models": models}),
                            encoding="utf-8")
            with patch.dict(os.environ, {"LOCALAPPDATA": directory}, clear=True):
                self.assertEqual(path, local_config_path())
                self.assertEqual("first-model", cloud_from_environment().model)
                self.assertEqual("first-model", cloud_from_environment(path).model)
                selected = cloud_from_environment(path, "model-2")
                self.assertEqual("second-model", selected.model)
                self.assertNotIn("fake-second-key", repr(selected))
                with self.assertRaises(CloudApiTestError) as caught:
                    cloud_from_environment(path, "model-3")
                self.assertEqual("API_CONFIG_ERROR", caught.exception.category)
                with self.assertRaises(CloudApiTestError) as caught:
                    cloud_from_environment(path, "unknown")
                self.assertEqual("API_MODEL_NOT_FOUND", caught.exception.category)

    async def test_schema_failure_reports_only_field_locations_and_types(self):
        class BadActionProvider:
            name = "cloud_test"
            model = "cloud-eval"

            async def generate(self, model_request):
                return ModelResult(text=json.dumps({
                    "decision_kind": "ACTION_PROPOSAL",
                    "action_proposal": {"action_id": "bring_object", "bindings": "bad-secret-value"},
                }))

        request = dict(case_model_requests())["C"]
        record = await evaluate_one("C", BadActionProvider(), request)
        self.assertEqual("SCHEMA_ERROR", record["error_category"])
        self.assertEqual("ACTION_PROPOSAL", record["candidate_decision_kind"])
        self.assertGreater(record["validation_error_count"], 0)
        self.assertNotIn("bad-secret-value", json.dumps(record))


if __name__ == "__main__":
    unittest.main()
