"""Offline production DeepSeek Provider and bootstrap regression tests."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.provider_registry import PRODUCTION_PROVIDERS, build_model_provider
from app.providers import InferenceOptions, MockProvider, ModelMessage, ModelRequest, OllamaProvider, ProviderError
from app.runtime_settings import ProviderPreset, RuntimeSettings, code_defaults
from app.runtime_settings_store import RuntimeSettingsStore
from app.runtime_settings_api import create_runtime_settings_router


KEY = "sk-test-secret-123456789"
SCHEMA = {"type": "object", "properties": {"utterance": {"type": "string"}},
          "required": ["utterance"], "additionalProperties": False}


def request(*, think: bool = False, schema: dict | None = SCHEMA) -> ModelRequest:
    return ModelRequest(
        messages=(ModelMessage(role="system", content="system"),
                  ModelMessage(role="user", content="user"),
                  ModelMessage(role="assistant", content="assistant")),
        response_schema=schema,
        inference_options=InferenceOptions(temperature=0.4, think=think, max_output_tokens=128),
    )


def body(*, status: str = "completed", text: str = '{"utterance":"ok"}', usage=True) -> dict:
    result = {"status": status, "output": [
        {"type": "reasoning", "content": [{"type": "reasoning_text", "text": "private reasoning"}]},
        {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": text}]},
    ]}
    if usage:
        result["usage"] = {"input_tokens": 11, "output_tokens": 7}
    return result


def provider(handler) -> DeepSeekOfficialResponsesProvider:
    return DeepSeekOfficialResponsesProvider(
        base_url="https://api.deepseek.com/", api_key=KEY, model="deepseek-flash",
        timeout_seconds=2, transport=httpx.MockTransport(handler),
    )


class DeepSeekProviderTests(unittest.IsolatedAsyncioTestCase):
    async def test_request_mapping_response_usage_and_secret_boundary(self) -> None:
        captured = {}

        def handler(req: httpx.Request) -> httpx.Response:
            captured["url"] = str(req.url)
            captured["auth"] = req.headers["Authorization"] == "Bearer " + KEY
            captured["payload"] = json.loads(req.content)
            return httpx.Response(200, json=body())

        active = provider(handler)
        result = await active.generate(request())
        payload = captured["payload"]
        self.assertEqual("https://api.deepseek.com/responses", captured["url"])
        self.assertTrue(captured["auth"])
        self.assertEqual("deepseek-flash", payload["model"])
        self.assertEqual([{"role": m.role, "content": m.content} for m in request().messages], payload["input"])
        self.assertEqual(0.4, payload["temperature"])
        self.assertEqual(128, payload["max_output_tokens"])
        self.assertEqual({"effort": "none"}, payload["reasoning"])
        self.assertFalse(payload["stream"])
        self.assertEqual({"type": "json_schema", "name": active.schema_name, "schema": SCHEMA},
                         payload["text"]["format"])
        self.assertEqual('{"utterance":"ok"}', result.text)
        self.assertNotIn("private reasoning", result.text)
        self.assertEqual("completed", result.finish_reason)
        self.assertEqual((11, 7), (result.usage.prompt_tokens, result.usage.completion_tokens))
        self.assertNotIn(KEY, json.dumps(payload))
        self.assertNotIn(KEY, repr(active))

    async def test_thinking_true_uses_official_default_and_unstructured_request(self) -> None:
        sent = {}

        def handler(req):
            sent.update(json.loads(req.content))
            return httpx.Response(200, json=body(usage=False))

        result = await provider(handler).generate(request(think=True, schema=None))
        self.assertNotIn("reasoning", sent)
        self.assertNotIn("text", sent)
        self.assertEqual(0.4, sent["temperature"])
        self.assertIsNone(result.usage.prompt_tokens)
        self.assertIsNone(result.usage.completion_tokens)

    async def test_incomplete_failed_empty_and_malformed_responses(self) -> None:
        for reason in ("max_output_tokens", "content_filter"):
            value = body(status="incomplete")
            value["incomplete_details"] = {"reason": reason}
            result = await provider(lambda _: httpx.Response(200, json=value)).generate(request())
            self.assertEqual("incomplete:" + reason, result.finish_reason)
        cases = [
            (body(status="failed"), "DEEPSEEK_RESPONSE_FAILED"),
            (body(text="  "), "DEEPSEEK_EMPTY_RESPONSE"),
            ({"status": "completed", "output": {}}, "DEEPSEEK_PROTOCOL_ERROR"),
            ({"status": "completed", "output": [{"type": "message", "role": "assistant", "content": [{}]}]},
             "DEEPSEEK_EMPTY_RESPONSE"),
            ({"status": "completed", "output": [{"type": "message", "role": "assistant", "content": [
                {"type": "output_text", "text": 3}]}]}, "DEEPSEEK_PROTOCOL_ERROR"),
            ({**body(), "usage": {"input_tokens": True}}, "DEEPSEEK_PROTOCOL_ERROR"),
        ]
        for value, expected in cases:
            with self.subTest(expected=expected), self.assertRaises(ProviderError) as caught:
                await provider(lambda _: httpx.Response(200, json=value)).generate(request())
            self.assertEqual(expected, str(caught.exception))

    async def test_http_errors_and_transport_errors_never_expose_remote_text(self) -> None:
        expected = {400: "DEEPSEEK_BAD_REQUEST", 401: "DEEPSEEK_AUTH_FAILED",
                    403: "DEEPSEEK_AUTH_FAILED", 404: "DEEPSEEK_MODEL_NOT_FOUND",
                    422: "DEEPSEEK_BAD_REQUEST", 429: "DEEPSEEK_RATE_LIMITED",
                    500: "DEEPSEEK_UNAVAILABLE"}
        for status, category in expected.items():
            with self.subTest(status=status), self.assertRaises(ProviderError) as caught:
                await provider(lambda _: httpx.Response(status, text=KEY + " query-secret")) .generate(request())
            self.assertEqual(category, str(caught.exception))
            self.assertNotIn(KEY, str(caught.exception))
            self.assertNotIn("query-secret", str(caught.exception))
        for exception, category in ((httpx.ReadTimeout, "DEEPSEEK_TIMEOUT"),
                                    (httpx.ConnectError, "DEEPSEEK_UNAVAILABLE")):
            def fail(req, error=exception):
                raise error(KEY, request=req)
            with self.assertRaises(ProviderError) as caught:
                await provider(fail).generate(request())
            self.assertEqual(category, str(caught.exception))
            self.assertIsNone(caught.exception.__cause__)

    async def test_confirmed_schema_rejection_does_not_fallback(self) -> None:
        def handler(_):
            return httpx.Response(422, json={"error": {
                "param": "text.format.schema.properties.utterance.type", "message": KEY}})
        with self.assertRaises(ProviderError) as caught:
            await provider(handler).generate(request())
        self.assertEqual("DEEPSEEK_SCHEMA_UNSUPPORTED", str(caught.exception))

    async def test_health_models_and_safe_failures(self) -> None:
        observed = {}

        def available(req):
            observed["url"] = str(req.url)
            observed["auth"] = req.headers["Authorization"] == "Bearer " + KEY
            return httpx.Response(200, json={"object": "list", "data": [{"id": "deepseek-flash"}]})
        result = await provider(available).health()
        self.assertTrue(result.available)
        self.assertEqual("", result.detail)
        self.assertEqual("https://api.deepseek.com/models", observed["url"])
        self.assertTrue(observed["auth"])
        for response, category in (
            (httpx.Response(200, json={"object": "list", "data": [{"id": "other"}]}),
             "DEEPSEEK_MODEL_NOT_FOUND"),
            (httpx.Response(401, text=KEY), "DEEPSEEK_AUTH_FAILED"),
            (httpx.Response(429, text=KEY), "DEEPSEEK_RATE_LIMITED"),
            (httpx.Response(200, json={"data": []}), "DEEPSEEK_PROTOCOL_ERROR"),
        ):
            with self.subTest(category=category):
                health = await provider(lambda _: response).health()
                self.assertFalse(health.available)
                self.assertEqual(category, health.detail)
                self.assertNotIn(KEY, health.model_dump_json())
        def fail(req):
            raise httpx.ConnectError(KEY, request=req)
        self.assertEqual("DEEPSEEK_UNAVAILABLE", (await provider(fail).health()).detail)

    async def test_runtime_status_reports_safe_active_deepseek_health(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = RuntimeSettingsStore(Path(directory) / "settings.json")
            active = provider(lambda _: httpx.Response(401, text=KEY))
            app = FastAPI()
            app.include_router(create_runtime_settings_router(store, active))
            with TestClient(app) as client:
                status = client.get("/v1/runtime-status")
            self.assertEqual(200, status.status_code)
            self.assertEqual("deepseek_official_responses", status.json()["active_provider"]["provider"])
            self.assertEqual("DEEPSEEK_AUTH_FAILED", status.json()["active_provider"]["detail"])
            self.assertEqual("not_supported", status.json()["provider_settings_activation"])
            self.assertNotIn(KEY, status.text)

    def test_invalid_base_url_and_safe_repr(self) -> None:
        for url in ("http://api.deepseek.com", "https://user:pass@api.deepseek.com",
                    "https://api.deepseek.com/?token=secret", "https://api.deepseek.com/responses"):
            with self.subTest(url=url), self.assertRaises(ValueError) as caught:
                DeepSeekOfficialResponsesProvider(base_url=url, api_key=KEY, model="deepseek-flash")
            self.assertEqual("DEEPSEEK_CONFIG_INVALID", str(caught.exception))
        with self.assertRaises(ValueError) as caught:
            DeepSeekOfficialResponsesProvider(base_url="https://api.deepseek.com", api_key=KEY, model=KEY)
        self.assertEqual("DEEPSEEK_CONFIG_INVALID", str(caught.exception))


class ProviderRegistryTests(unittest.TestCase):
    def test_explicit_registry_preserves_mock_and_ollama(self) -> None:
        self.assertEqual({"mock", "ollama", "deepseek_official_responses"}, set(PRODUCTION_PROVIDERS))
        self.assertIsInstance(build_model_provider({"EMBODIEDNPC_PROVIDER": "mock"}), MockProvider)
        ollama = build_model_provider({"EMBODIEDNPC_PROVIDER": "ollama", "OLLAMA_BASE_URL": "http://local:11434/",
                                       "OLLAMA_MODEL": "qwen-test", "LLM_TIMEOUT_SECONDS": "17",
                                       "OLLAMA_NUM_CTX": "4096"})
        self.assertIsInstance(ollama, OllamaProvider)
        self.assertEqual(("http://local:11434", "qwen-test", 17, 4096),
                         (ollama.base_url, ollama.model, ollama.timeout_seconds, ollama.context_length))

    def test_deepseek_bootstrap_and_unknown_provider(self) -> None:
        deepseek = build_model_provider({"EMBODIEDNPC_PROVIDER": "deepseek_official_responses",
                                         "DEEPSEEK_API_KEY": KEY})
        self.assertIsInstance(deepseek, DeepSeekOfficialResponsesProvider)
        self.assertEqual(("https://api.deepseek.com", "deepseek-flash", 60),
                         (deepseek.base_url, deepseek.model, deepseek.timeout_seconds))
        self.assertNotIn(KEY, repr(deepseek))
        for env, category in (({"EMBODIEDNPC_PROVIDER": "unknown"}, "PROVIDER_NOT_SUPPORTED"),
                              ({"EMBODIEDNPC_PROVIDER": "deepseek_official_responses"}, "DEEPSEEK_API_KEY_MISSING")):
            with self.assertRaises(ValueError) as caught:
                build_model_provider(env)
            self.assertEqual(category, str(caught.exception))

    def test_startup_shares_provider_and_keeps_saved_and_embedding_independent(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings.json"
            cloud = ProviderPreset(preset_id="cloud", display_name="Cloud",
                                   provider_type="deepseek_official_responses", model="deepseek-flash")
            saved = RuntimeSettings(provider_presets=code_defaults().provider_presets + (cloud,),
                                    selected_preset_id="local-qwen")
            RuntimeSettingsStore(path).save(saved, expected_revision=0)
            env = {**os.environ, "EMBODIEDNPC_SETTINGS_FILE": str(path),
                   "EMBODIEDNPC_MEMORY_DB": str(Path(directory) / "memory.sqlite3"),
                   "EMBODIEDNPC_PROVIDER": "deepseek_official_responses", "DEEPSEEK_API_KEY": KEY,
                   "CANONICAL_EMBEDDING_ENABLED": "true",
                   "CANONICAL_EMBEDDING_BASE_URL": "http://embedding.local:11434",
                   "CANONICAL_EMBEDDING_MODEL": "embedding-test"}
            script = (
                "from app import m3_main as m; "
                "print(m.saved_settings_document.settings.selected_preset_id, m.provider.name, "
                "m.cognitive_entry.provider is m.provider, "
                "m.failure_response_service._provider is m.provider, "
                "m.base_memory_consolidator.provider is m.provider, "
                "m.relationship_reflection_worker.model.provider is m.provider, "
                "m.canonical_embedding_provider.base_url, m.canonical_embedding_provider.model)"
            )
            result = subprocess.run([sys.executable, "-B", "-c", script], env=env,
                                    capture_output=True, text=True, check=True)
            self.assertEqual("local-qwen deepseek_official_responses True True True True "
                             "http://embedding.local:11434 embedding-test", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
