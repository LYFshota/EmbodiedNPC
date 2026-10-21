"""P4-B startup selection and manual-restart behavior, without cloud traffic."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from app.canonical_embeddings import OllamaEmbeddingProvider, OpenAICompatibleEmbeddingProvider
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_api_config_store import ModelApiConfigStore
from app.model_config_runtime import (
    build_embedding_from_model_config, build_provider_from_model_config,
    cognition_options, restart_required,
)
from app.provider_registry import build_model_provider
from app.providers import InferenceOptions, OllamaProvider
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests.test_memory_d2_cognitive_steps import (
    KnownSubjects, MinimalRows, ScriptedProvider, TERMINAL, cognitive_request, recall,
)
from tests.test_model_api_config import embedding, main


def saved_document(directory: Path, *, main_changes=None, embedding_changes=None):
    store = ModelApiConfigStore(directory / "model_api_configs.json")
    document = store.save(ModelApiConfig(
        main_model=MainModelConfig(**main(**(main_changes or {}))),
        embedding=EmbeddingConfig(**embedding(**(embedding_changes or {})))),
        expected_revision=0)
    return store, document


class StartupConstructionTests(unittest.TestCase):
    def test_env_bootstrap_compatibility_without_file(self):
        self.assertEqual("mock", build_model_provider({"EMBODIEDNPC_PROVIDER": "mock"}).name)
        ollama = build_model_provider({"EMBODIEDNPC_PROVIDER": "ollama", "OLLAMA_MODEL": "local-a"})
        self.assertIsInstance(ollama, OllamaProvider)
        self.assertEqual("local-a", ollama.model)

    def test_four_main_providers_from_json(self):
        cases = (
            ({"provider_type": "mock", "base_url": None, "api_key": None}, "mock"),
            ({"provider_type": "ollama", "base_url": "http://local.test", "api_key": None,
              "context_length": None}, "ollama"),
            ({"provider_type": "deepseek_official_responses", "base_url": "https://api.deepseek.com",
              "model": "deepseek-flash"}, "deepseek_official_responses"),
            ({}, "openai_compatible"),
        )
        with tempfile.TemporaryDirectory() as root:
            for index, (changes, expected) in enumerate(cases):
                with self.subTest(expected=expected):
                    _, document = saved_document(Path(root) / str(index), main_changes=changes,
                                                  embedding_changes={"enabled": False,
                                                                     "reuse_main_api": False})
                    provider = build_provider_from_model_config(document)
                    self.assertEqual(expected, provider.name)
                    if expected == "mock":
                        self.assertEqual("fixed-greeting-v1", provider.model)
                    elif expected == "ollama":
                        self.assertEqual(8192, provider.context_length)
                    else:
                        self.assertEqual(document.main_model.model, provider.model)

    def test_embedding_enabled_disabled_reuse_and_independent_main(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            _, disabled = saved_document(path / "disabled", embedding_changes={"enabled": False})
            self.assertIsNone(build_embedding_from_model_config(disabled))
            _, ollama = saved_document(path / "ollama", embedding_changes={
                "provider_type": "ollama", "reuse_main_api": False,
                "base_url": "http://localhost:11434", "api_key": None, "model": "embed-local"})
            local = build_embedding_from_model_config(ollama)
            self.assertIsInstance(local, OllamaEmbeddingProvider)
            self.assertEqual("embed-local", local.model)
            _, cloud = saved_document(path / "cloud")
            reused = build_embedding_from_model_config(cloud)
            self.assertIsInstance(reused, OpenAICompatibleEmbeddingProvider)
            self.assertEqual(("https://gateway.test/v1", "embedding-x"),
                             (reused.base_url, reused.model))
            self.assertEqual("local-demo-key", reused.api_key)
            _, mock_main = saved_document(path / "mock-main", main_changes={
                "provider_type": "mock", "base_url": None, "api_key": None},
                embedding_changes={"reuse_main_api": False,
                                   "base_url": "https://embedding.test/v1", "api_key": "embed-key"})
            self.assertEqual("mock", build_provider_from_model_config(mock_main).name)
            independent = build_embedding_from_model_config(mock_main)
            self.assertIsInstance(independent, OpenAICompatibleEmbeddingProvider)
            self.assertEqual("embed-key", independent.api_key)

    def test_effective_restart_comparison(self):
        with tempfile.TemporaryDirectory() as root:
            store, active = saved_document(Path(root))
            self.assertFalse(restart_required(active, active))
            self.assertFalse(restart_required(None, None))
            self.assertTrue(restart_required(None, active))

            def replacement(main_change=None, embedding_change=None):
                return store.save(ModelApiConfig(
                    main_model=MainModelConfig(**main(**(main_change or {}))),
                    embedding=EmbeddingConfig(**embedding(**(embedding_change or {})))),
                    expected_revision=store.load().revision)

            self.assertFalse(restart_required(active, replacement()))
            self.assertFalse(restart_required(active, replacement({"display_name": "Renamed"})))
            for change in ({"model": "other-chat"}, {"api_key": "new-key"},
                           {"temperature": 0.7}, {"thinking": True},
                           {"max_output_tokens": 900}):
                with self.subTest(main_change=change):
                    self.assertTrue(restart_required(active, replacement(change)))
            self.assertTrue(restart_required(active, replacement(embedding_change={"model": "other-embed"})))
            self.assertTrue(restart_required(active, replacement(embedding_change={"enabled": False})))


class CognitionGenerationTests(unittest.IsolatedAsyncioTestCase):
    async def test_startup_options_are_shared_by_step_zero_and_one(self):
        with tempfile.TemporaryDirectory() as root:
            _, document = saved_document(Path(root), main_changes={
                "temperature": 0.7, "thinking": True, "max_output_tokens": 900})
            options = cognition_options(document)
            self.assertEqual(InferenceOptions(temperature=0.7, think=True, max_output_tokens=900), options)
            provider = ScriptedProvider([recall, TERMINAL])
            memory = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(KnownSubjects()),
                                           CanonicalMemoryRetrieval(MinimalRows()))
            entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                                          memory_recall=memory, inference_options=options, context_length=16384)
            await entry.run(cognitive_request())
            self.assertEqual(2, len(provider.requests))
            self.assertEqual([options, options], [item.inference_options for item in provider.requests])


class RestartProcessTests(unittest.TestCase):
    def _run(self, script: str, root: Path, *, provider_env: str = "mock") -> dict:
        env = {**os.environ, "EMBODIEDNPC_MODEL_CONFIG_FILE": str(root / "model_api_configs.json"),
               "EMBODIEDNPC_SETTINGS_FILE": str(root / "runtime_settings.json"),
               "EMBODIEDNPC_MEMORY_DB": str(root / "memory.sqlite3"),
               "CANONICAL_EMBEDDING_ENABLED": "true",
               "EMBODIEDNPC_PROVIDER": provider_env}
        result = subprocess.run([sys.executable, "-B", "-c", script], cwd=Path(__file__).resolve().parents[1],
                                env=env, capture_output=True, text=True, check=True)
        return json.loads(result.stdout.strip())

    def test_save_then_manual_restart_applies_new_model(self):
        with tempfile.TemporaryDirectory() as root_text:
            root = Path(root_text)
            saved_document(root, main_changes={"model": "model-a"})
            process_a = """
import json
from unittest.mock import AsyncMock
from fastapi.testclient import TestClient
from app import m3_main as m
from app.providers import ProviderHealth
m.provider.health = AsyncMock(return_value=ProviderHealth(
    provider=m.provider.name, model=m.provider.model, available=True))
c = TestClient(m.app)
before = c.get('/v1/runtime-status').json()
config = c.get('/v1/model-config').json()
config['main_model']['model'] = 'model-b'
saved = c.put('/v1/model-config', json={'expected_revision': config['revision'],
    'config': {'main_model': config['main_model'], 'embedding': config['embedding']}})
after = c.get('/v1/runtime-status').json()
print(json.dumps({'before': before, 'saved_code': saved.status_code, 'after': after,
                  'indexer_active': m.canonical_embedding_indexer is not None,
                  'same_provider': m.provider is m.cognitive_entry.provider is m.failure_response_service._provider
                  is m.base_memory_consolidator.provider
                  is m.relationship_reflection_worker.model.provider}))
"""
            first = self._run(process_a, root)
            self.assertEqual(200, first["saved_code"])
            self.assertTrue(first["same_provider"])
            self.assertTrue(first["indexer_active"])
            self.assertEqual("openai_compatible", first["before"]["active_main_model"]["provider"])
            self.assertEqual("model-a", first["before"]["active_main_model"]["model"])
            self.assertEqual({"enabled": True, "provider": "openai_compatible", "model": "embedding-x"},
                             first["before"]["active_embedding"])
            self.assertFalse(first["before"]["restart_required"])
            self.assertEqual("model-a", first["after"]["active_main_model"]["model"])
            self.assertTrue(first["after"]["restart_required"])
            self.assertEqual(2, first["after"]["model_config"]["revision"])
            self.assertNotIn("local-demo-key", json.dumps(first))

            process_b = """
import json
from unittest.mock import AsyncMock
from fastapi.testclient import TestClient
from app import m3_main as m
from app.providers import ProviderHealth
m.provider.health = AsyncMock(return_value=ProviderHealth(
    provider=m.provider.name, model=m.provider.model, available=True))
s = TestClient(m.app).get('/v1/runtime-status').json()
print(json.dumps(s))
"""
            second = self._run(process_b, root)
            self.assertEqual("model-b", second["active_main_model"]["model"])
            self.assertFalse(second["restart_required"])

    def test_missing_config_reports_bootstrap_without_restart(self):
        with tempfile.TemporaryDirectory() as root_text:
            result = self._run("""
import json
from fastapi.testclient import TestClient
from app import m3_main as m
from tests.test_model_api_config import main, embedding
c = TestClient(m.app)
before = c.get('/v1/runtime-status').json()
c.put('/v1/model-config', json={'expected_revision': 0,
    'config': {'main_model': main(), 'embedding': embedding(enabled=False)}})
after = c.get('/v1/runtime-status').json()
print(json.dumps({'before': before, 'after': after, 'provider': m.provider.name}))
""", Path(root_text))
            self.assertEqual("bootstrap", result["before"]["active_main_model"]["source"])
            self.assertFalse(result["before"]["model_config"]["configured"])
            self.assertFalse(result["before"]["restart_required"])
            self.assertEqual("mock", result["provider"])
            self.assertTrue(result["after"]["model_config"]["configured"])
            self.assertTrue(result["after"]["restart_required"])

    def test_missing_config_still_bootstraps_ollama(self):
        with tempfile.TemporaryDirectory() as root_text:
            result = self._run("""
import json
from app import m3_main as m
print(json.dumps({'provider': m.provider.name, 'source': m.startup_model_config_document is None,
                  'embedding': type(m.canonical_embedding_provider).__name__}))
""", Path(root_text), provider_env="ollama")
            self.assertEqual({"provider": "ollama", "source": True,
                              "embedding": "OllamaEmbeddingProvider"}, result)


if __name__ == "__main__":
    unittest.main()
