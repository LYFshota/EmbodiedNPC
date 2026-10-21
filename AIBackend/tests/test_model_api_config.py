"""P4-A local model configuration and offline connection tests."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient
from pydantic import ValidationError

from app.canonical_embeddings import OpenAICompatibleEmbeddingProvider
from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_api_config_api import create_model_api_config_router
from app.model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from app.openai_compatible_provider import OpenAICompatibleProvider
from app.providers import InferenceOptions, ModelMessage, ModelRequest, ProviderError


KEY = "local-demo-key"


def main(**updates) -> dict:
    return {"display_name": "My Cloud Model", "provider_type": "openai_compatible",
            "base_url": "https://gateway.test/v1", "api_key": KEY, "model": "chat-model",
            "temperature": 0.2, "thinking": False, "max_output_tokens": 512,
            "context_length": 8192, "timeout_seconds": 20.0, **updates}


def embedding(**updates) -> dict:
    return {"enabled": True, "provider_type": "openai_compatible", "reuse_main_api": True,
            "base_url": None, "api_key": None, "model": "embedding-x",
            "timeout_seconds": 10.0, **updates}


def config() -> ModelApiConfig:
    return ModelApiConfig(main_model=MainModelConfig(**main()), embedding=EmbeddingConfig(**embedding()))


class ModelConfigDomainStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "nested" / "model_api_configs.json"
        self.store = ModelApiConfigStore(self.path)

    def test_missing_valid_slots_and_reuse(self) -> None:
        self.assertIsNone(self.store.load())
        self.assertFalse(self.path.exists())
        value = config()
        self.assertEqual(("https://gateway.test/v1", KEY, "embedding-x"),
                         value.effective_embedding_connection())
        self.assertNotIn(KEY, repr(value))

    def test_invalid_values_are_rejected(self) -> None:
        for changes in ({"provider_type": "unsupported"}, {"base_url": "not-a-url"},
                        {"timeout_seconds": -1}, {"temperature": 3.0},
                        {"model": ""}, {"extra": True}):
            with self.subTest(changes=changes), self.assertRaises(ValidationError):
                MainModelConfig(**main(**changes))
        with self.assertRaises(ValidationError):
            EmbeddingConfig(**embedding(timeout_seconds=-1))
        with self.assertRaises(ValidationError):
            ModelApiConfig(main_model=MainModelConfig(**main(provider_type="ollama", api_key=None)),
                           embedding=EmbeddingConfig(**embedding()))

    def test_save_reload_revision_conflict_and_atomic_failure(self) -> None:
        first = self.store.save(config(), expected_revision=0)
        self.assertEqual(1, first.revision)
        self.assertEqual(KEY, ModelApiConfigStore(self.path).load().main_model.api_key)
        second = self.store.save(config(), expected_revision=1)
        self.assertEqual(2, second.revision)
        before = self.path.read_bytes()
        with self.assertRaises(ModelConfigStoreError) as caught:
            self.store.save(config(), expected_revision=1)
        self.assertEqual("MODEL_CONFIG_REVISION_CONFLICT", caught.exception.code)
        self.assertEqual(before, self.path.read_bytes())
        with patch("app.model_api_config_store.os.replace", side_effect=OSError("blocked")):
            with self.assertRaises(ModelConfigStoreError) as caught:
                self.store.save(config(), expected_revision=2)
        self.assertEqual("MODEL_CONFIG_WRITE_FAILED", caught.exception.code)
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual([], list(self.path.parent.glob(".model_api_configs-*.tmp")))

    def test_corrupt_document_is_not_overwritten(self) -> None:
        self.store.save(config(), expected_revision=0)
        self.path.write_text('{"api_key":"sensitive"', encoding="utf-8")
        with self.assertRaises(ModelConfigStoreError) as caught:
            self.store.load()
        self.assertEqual("MODEL_CONFIG_DOCUMENT_INVALID", caught.exception.code)
        with self.assertRaises(ModelConfigStoreError):
            self.store.save(config(), expected_revision=0)
        self.assertIn("sensitive", self.path.read_text(encoding="utf-8"))


class ModelConfigApiTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "model_api_configs.json"
        self.store = ModelApiConfigStore(self.path)

    def client(self, handler=None) -> TestClient:
        app = FastAPI()
        transport = httpx.MockTransport(handler) if handler else None
        app.include_router(create_model_api_config_router(
            self.store, async_transport=transport, sync_transport=transport))
        return TestClient(app)

    def test_get_put_key_roundtrip_update_and_conflict(self) -> None:
        with self.client() as client:
            missing = client.get("/v1/model-config")
            self.assertEqual({"configured": False, "revision": 0, "updated_at": None,
                              "main_model": None, "embedding": None, "schema_version": 1}, missing.json())
            self.assertFalse(self.path.exists())
            payload = {"expected_revision": 0, "config": config().model_dump(mode="json")}
            saved = client.put("/v1/model-config", json=payload)
            self.assertEqual(200, saved.status_code, saved.text)
            self.assertEqual(1, saved.json()["revision"])
            self.assertEqual(KEY, saved.json()["main_model"]["api_key"])
            self.assertEqual("embedding-x", saved.json()["embedding"]["model"])
            self.assertEqual(saved.json(), client.get("/v1/model-config").json())
            payload["expected_revision"] = 1
            payload["config"]["main_model"]["model"] = "chat-model-2"
            self.assertEqual(2, client.put("/v1/model-config", json=payload).json()["revision"])
            before = self.path.read_bytes()
            stale = client.put("/v1/model-config", json=payload)
            self.assertEqual(409, stale.status_code)
            self.assertEqual("MODEL_CONFIG_REVISION_CONFLICT", stale.json()["error"]["code"])
            self.assertEqual(before, self.path.read_bytes())

    def test_invalid_request_and_corrupt_store_use_code_only_errors(self) -> None:
        with self.client() as client:
            invalid = {"expected_revision": 0, "config": config().model_dump(mode="json")}
            invalid["config"]["main_model"]["temperature"] = 5
            response = client.put("/v1/model-config", json=invalid)
            self.assertEqual(422, response.status_code)
            self.assertNotIn(KEY, response.text)
            self.path.write_text('{"api_key":"sensitive"', encoding="utf-8")
            response = client.get("/v1/model-config")
            self.assertEqual(500, response.status_code)
            self.assertNotIn("sensitive", response.text)

    def test_discover_openai_ollama_deepseek_and_failures(self) -> None:
        observed = []

        def handler(req: httpx.Request):
            observed.append((str(req.url), req.headers.get("Authorization")))
            if req.url.path == "/api/tags":
                return httpx.Response(200, json={"models": [{"name": "qwen:latest"}]})
            return httpx.Response(200, json={"data": [{"id": "model-b"}, {"id": "model-a"}]})

        with self.client(handler) as client:
            for provider_type, expected in (("openai_compatible", ["model-a", "model-b"]),
                                            ("ollama", ["qwen:latest"]),
                                            ("deepseek_official_responses", ["model-a", "model-b"])):
                response = client.post("/v1/model-config/discover-models", json={
                    "provider_type": provider_type, "base_url": "https://gateway.test/v1" if provider_type != "ollama" else "http://local.test",
                    "api_key": KEY})
                self.assertEqual({"success": True, "models": expected, "error_code": None}, response.json())
            self.assertEqual("https://gateway.test/v1/models", observed[0][0])
            self.assertEqual("http://local.test/api/tags", observed[1][0])
            self.assertEqual("Bearer " + KEY, observed[2][1])
            unsupported = client.post("/v1/model-config/discover-models", json={
                "provider_type": "unknown", "base_url": "https://gateway.test"})
            self.assertEqual("MODEL_DISCOVERY_UNSUPPORTED", unsupported.json()["error_code"])

        for handler, code in (
            (lambda _: httpx.Response(200, json={"data": {}}), "MODEL_DISCOVERY_PROTOCOL_ERROR"),
            (lambda _: httpx.Response(200, json={"data": []}), "MODEL_DISCOVERY_EMPTY"),
            (lambda _: (_ for _ in ()).throw(httpx.ReadTimeout("timeout")), "MODEL_DISCOVERY_TIMEOUT"),
        ):
            with self.client(handler) as client:
                response = client.post("/v1/model-config/discover-models", json={
                    "provider_type": "openai_compatible", "base_url": "https://gateway.test"})
            self.assertEqual(code, response.json()["error_code"])

    def test_main_and_embedding_connection_are_isolated_probes(self) -> None:
        calls = []

        def handler(req: httpx.Request):
            calls.append((str(req.url), req.headers.get("Authorization"), json.loads(req.content)))
            if req.url.path.endswith("/chat/completions"):
                return httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}'},
                                                               "finish_reason": "stop"}], "usage": {}})
            return httpx.Response(200, json={"data": [{"embedding": [3.0, 4.0]}]})

        with self.client(handler) as client:
            main_result = client.post("/v1/model-config/test", json={"kind": "main", "main_model": main()})
            embed_result = client.post("/v1/model-config/test", json={
                "kind": "embedding", "main_model": main(), "embedding": embedding()})
        self.assertEqual({"success": True, "error_code": None}, main_result.json())
        self.assertEqual({"success": True, "error_code": None}, embed_result.json())
        self.assertEqual(("https://gateway.test/v1/chat/completions", "Bearer " + KEY), calls[0][:2])
        self.assertEqual(("https://gateway.test/v1/embeddings", "Bearer " + KEY), calls[1][:2])
        self.assertEqual("embedding-x", calls[1][2]["model"])
        self.assertFalse(self.path.exists())

    def test_ollama_and_deepseek_main_probes_and_ollama_embedding(self) -> None:
        paths = []

        def handler(req: httpx.Request):
            paths.append(req.url.path)
            if req.url.path == "/api/chat":
                return httpx.Response(200, json={"message": {"content": '{"ok":true}'},
                                                 "done_reason": "stop"})
            if req.url.path == "/api/embed":
                return httpx.Response(200, json={"embeddings": [[1.0, 2.0]]})
            return httpx.Response(200, json={"status": "completed", "output": [
                {"type": "message", "role": "assistant", "content": [
                    {"type": "output_text", "text": '{"ok":true}'}]}]})

        ollama_main = main(provider_type="ollama", base_url="http://local.test", api_key=None)
        ollama_embedding = embedding(provider_type="ollama", model="embedding-local")
        deepseek_main = main(provider_type="deepseek_official_responses",
                             base_url="https://api.deepseek.com", model="deepseek-flash")
        with self.client(handler) as client:
            responses = [
                client.post("/v1/model-config/test", json={"kind": "main", "main_model": ollama_main}),
                client.post("/v1/model-config/test", json={"kind": "embedding",
                                                              "main_model": ollama_main,
                                                              "embedding": ollama_embedding}),
                client.post("/v1/model-config/test", json={"kind": "main", "main_model": deepseek_main}),
            ]
        self.assertTrue(all(item.json()["success"] for item in responses), [item.text for item in responses])
        self.assertEqual(["/api/chat", "/api/embed", "/responses"], paths)

    def test_embedding_separate_connection_and_invalid_deepseek_url(self) -> None:
        captured = {}

        def handler(req: httpx.Request):
            captured["url"] = str(req.url)
            captured["auth"] = req.headers.get("Authorization")
            captured["body"] = json.loads(req.content)
            return httpx.Response(200, json={"data": [{"embedding": [1.0]}]})

        separate = embedding(reuse_main_api=False, base_url="https://embedding.test/v1",
                             api_key="embedding-key", model="embedding-separate")
        with self.client(handler) as client:
            result = client.post("/v1/model-config/test", json={"kind": "embedding",
                                                                 "main_model": main(), "embedding": separate})
        self.assertTrue(result.json()["success"])
        self.assertEqual("https://embedding.test/v1/embeddings", captured["url"])
        self.assertEqual("Bearer embedding-key", captured["auth"])
        self.assertEqual("embedding-separate", captured["body"]["model"])

        with self.client(handler) as client:
            rejected = client.post("/v1/model-config/test", json={"kind": "main", "main_model": main(
                provider_type="deepseek_official_responses", base_url="http://local.test")})
        self.assertEqual({"success": False, "error_code": "MODEL_TEST_CONFIG_INVALID"}, rejected.json())

    def test_test_connection_failure_is_safe_and_does_not_block_manual_model(self) -> None:
        with self.client(lambda _: httpx.Response(401, text=KEY)) as client:
            failed = client.post("/v1/model-config/test", json={"kind": "main", "main_model": main()})
            self.assertEqual(False, failed.json()["success"])
            self.assertEqual("OPENAI_COMPATIBLE_AUTH_FAILED", failed.json()["error_code"])
            self.assertNotIn(KEY, failed.text)
            saved = client.put("/v1/model-config", json={
                "expected_revision": 0, "config": config().model_dump(mode="json")})
            self.assertEqual(200, saved.status_code)

    def test_new_config_activates_only_at_startup_and_put_does_not_hot_switch(self) -> None:
        self.store.save(config(), expected_revision=0)
        with tempfile.TemporaryDirectory() as directory:
            env = {**os.environ, "EMBODIEDNPC_MODEL_CONFIG_FILE": str(self.path),
                   "EMBODIEDNPC_SETTINGS_FILE": str(Path(directory) / "old-settings.json"),
                   "EMBODIEDNPC_MEMORY_DB": str(Path(directory) / "memory.sqlite3"),
                   "EMBODIEDNPC_PROVIDER": "mock"}
            script = (
                "from app import m3_main as m; from fastapi.testclient import TestClient; "
                "c=TestClient(m.app); s=c.get('/v1/model-config').json(); "
                "p=c.put('/v1/model-config', json={'expected_revision': s['revision'], "
                "'config': {'main_model': s['main_model'], 'embedding': s['embedding']}}); "
                "print(m.provider.name, m.canonical_embedding_provider is None, s['configured'], "
                "s['main_model']['model'], p.status_code, m.provider.name)"
            )
            result = subprocess.run([sys.executable, "-B", "-c", script], env=env,
                                    capture_output=True, text=True, check=True)
            self.assertEqual("openai_compatible False True chat-model 200 openai_compatible",
                             result.stdout.strip())


class CompatibleProviderTests(unittest.IsolatedAsyncioTestCase):
    async def test_context_overflow_is_distinct_and_never_exposes_upstream_text(self):
        for error in (
            {"code": "context_length_exceeded", "message": "private secret-sentinel"},
            {"message": "This model's maximum context length is 8192 tokens; requested more."},
        ):
            provider = OpenAICompatibleProvider(base_url="https://gateway.test/v1", api_key=KEY,
                model="chat-model", transport=httpx.MockTransport(lambda _, error=error:
                    httpx.Response(400, json={"error": error})))
            with self.assertRaisesRegex(ProviderError, "^OPENAI_COMPATIBLE_CONTEXT_LENGTH_EXCEEDED$"):
                await provider.generate(ModelRequest(messages=(ModelMessage(role="user", content="test"),)))
        from app.model_schema_projection import rejection_reason
        self.assertEqual(rejection_reason(httpx.Response(400, json={"error": {"message": "secret-sentinel"}})),
                         "REQUEST_REJECTED")
        self.assertEqual(rejection_reason(httpx.Response(400, json={"error": {"message": "max_tokens is invalid"}})),
                         "PARAMETER_REJECTED")

    async def test_native_thinking_disable_usage_and_conservative_enabled_budget(self):
        captured = {}
        def handler(req):
            captured.update(json.loads(req.content))
            return httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}'}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 5, "completion_tokens": 3, "completion_tokens_details": {"reasoning_tokens": 0}}})
        provider = OpenAICompatibleProvider(base_url="https://gateway.test/v1", api_key=KEY,
            model="[特价]DeepSeek-V4.1-Flash", transport=httpx.MockTransport(handler))
        options = provider.cognitive_options(InferenceOptions(think=False, max_output_tokens=512), 16384)
        result = await provider.generate(ModelRequest(messages=(ModelMessage(role="user", content="test"),), inference_options=options))
        self.assertEqual(captured["thinking"], {"type": "disabled"})
        self.assertEqual(captured["max_tokens"], options.output_token_reserve)
        self.assertEqual(result.usage.reasoning_tokens, 0)
        self.assertNotIn("output_token_reserve", captured)
        with self.assertRaisesRegex(ProviderError, "OUTPUT_RESERVE_EXCEEDS_CONTEXT"):
            provider.cognitive_options(InferenceOptions(think=True, max_output_tokens=512), 16384)
        enabled = provider.cognitive_options(InferenceOptions(think=True, max_output_tokens=512), 1_000_000)
        self.assertTrue(enabled.think)
        self.assertEqual(enabled.output_token_reserve, 393216)
        plain = OpenAICompatibleProvider(base_url="https://gateway.test/v1", api_key=KEY, model="chat-model")
        self.assertNotIn("thinking", plain.budget_payload(ModelRequest(messages=(ModelMessage(role="user", content="test"),))))

    async def test_native_disable_violation_is_not_a_valid_cognitive_result(self):
        provider = OpenAICompatibleProvider(base_url="https://gateway.test/v1", api_key=KEY,
            model="deepseek-flash", transport=httpx.MockTransport(lambda _: httpx.Response(200, json={
                "choices": [{"message": {"content": '{"ok":true}', "reasoning_content": "must never be logged"}}],
                "usage": {"completion_tokens": 655, "completion_tokens_details": {"reasoning_tokens": 583}}})))
        request = ModelRequest(messages=(ModelMessage(role="user", content="test"),),
            inference_options=provider.cognitive_options(InferenceOptions(think=False, max_output_tokens=512), 16384))
        with self.assertRaisesRegex(ProviderError, "THINKING_DISABLE_NOT_HONORED"):
            await provider.generate(request)

    async def test_chat_mapping_parsing_and_failures(self) -> None:
        captured = {}

        def handler(req):
            captured["url"] = str(req.url)
            captured["auth"] = req.headers.get("Authorization")
            captured["json"] = json.loads(req.content)
            return httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}'},
                                                           "finish_reason": "stop"}],
                                             "usage": {"prompt_tokens": 5, "completion_tokens": 3}})

        adapter = OpenAICompatibleProvider(base_url="https://gateway.test/v1/", api_key=KEY,
                                           model="chat-model", transport=httpx.MockTransport(handler))
        request = ModelRequest(messages=(ModelMessage(role="system", content="system"),
                                         ModelMessage(role="user", content="user")),
                               response_schema={"type": "object"},
                               inference_options=InferenceOptions(temperature=0.3, think=True,
                                                                  max_output_tokens=45))
        result = await adapter.generate(request)
        self.assertEqual("https://gateway.test/v1/chat/completions", captured["url"])
        self.assertEqual("Bearer " + KEY, captured["auth"])
        self.assertEqual("chat-model", captured["json"]["model"])
        self.assertEqual([{"role": "system", "content": "system"},
                          {"role": "user", "content": "user"}], captured["json"]["messages"])
        self.assertEqual(0.3, captured["json"]["temperature"])
        self.assertEqual(45, captured["json"]["max_tokens"])
        self.assertEqual("json_schema", captured["json"]["response_format"]["type"])
        self.assertNotIn("reasoning", captured["json"])
        self.assertEqual(("stop", 5, 3), (result.finish_reason, result.usage.prompt_tokens,
                                          result.usage.completion_tokens))
        self.assertNotIn(KEY, repr(adapter))
        for handler, code in (
            (lambda _: httpx.Response(500, text=KEY), "OPENAI_COMPATIBLE_UNAVAILABLE"),
            (lambda _: httpx.Response(200, json={"choices": []}), "OPENAI_COMPATIBLE_PROTOCOL_ERROR"),
            (lambda _: (_ for _ in ()).throw(httpx.ReadTimeout(KEY)), "OPENAI_COMPATIBLE_TIMEOUT"),
        ):
            failing = OpenAICompatibleProvider(base_url="https://gateway.test/v1", api_key=KEY,
                                               model="chat-model", transport=httpx.MockTransport(handler))
            with self.assertRaises(ProviderError) as caught:
                await failing.generate(request)
            self.assertEqual(code, str(caught.exception))
            self.assertNotIn(KEY, str(caught.exception))


class CompatibleEmbeddingTests(unittest.TestCase):
    def test_vector_http_and_failures(self) -> None:
        captured = {}

        def handler(req):
            captured["url"] = str(req.url)
            captured["auth"] = req.headers.get("Authorization")
            captured["json"] = json.loads(req.content)
            return httpx.Response(200, json={"data": [{"embedding": [3, 4]}]})

        adapter = OpenAICompatibleEmbeddingProvider("https://gateway.test/v1", KEY, "embedding-x",
                                                     transport=httpx.MockTransport(handler))
        self.assertEqual((0.6, 0.8), adapter.embed("test"))
        self.assertEqual("https://gateway.test/v1/embeddings", captured["url"])
        self.assertEqual("Bearer " + KEY, captured["auth"])
        self.assertEqual({"model": "embedding-x", "input": "test"}, captured["json"])
        self.assertNotIn(KEY, repr(adapter))
        for handler, code in (
            (lambda _: httpx.Response(200, json={"data": [{"embedding": [True]}]}),
             "EMBEDDING_PROTOCOL_ERROR"),
            (lambda _: httpx.Response(500, text=KEY), "EMBEDDING_REQUEST_FAILED"),
            (lambda _: (_ for _ in ()).throw(httpx.ReadTimeout(KEY)), "EMBEDDING_TIMEOUT"),
        ):
            failing = OpenAICompatibleEmbeddingProvider(
                "https://gateway.test/v1", KEY, "embedding-x", transport=httpx.MockTransport(handler))
            with self.assertRaises(ValueError) as caught:
                failing.embed("test")
            self.assertEqual(code, str(caught.exception))
            self.assertNotIn(KEY, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
