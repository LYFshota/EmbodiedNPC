"""Offline adapter, config activation and canonical fallback contracts (no weights)."""

import asyncio
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import numpy as np
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app import builtin_embedding_provider as builtin
from app.canonical_embeddings import CanonicalEmbeddingIndex, OllamaEmbeddingProvider
from app.canonical_memory_ranking import CanonicalMemoryRanker
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.embedding_index_control import EmbeddingControl, ManagedEmbeddingIndexer
from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_api_config_api import _test_embedding
from app.model_api_config_store import ModelApiConfigStore
from app.model_config_runtime import build_embedding_from_model_config, build_embedding_provider, restart_required
from app.model_profile_api import create_model_profile_router
from tests import test_memory_d1a_retrieval as d1a


def values(directory):
    return dict(enabled=True, provider_type="builtin", model=builtin.MODEL,
                model_version=builtin.REVISION, resource_directory=str(directory))


class BuiltinAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        files = {}
        for name in builtin.RESOURCE_SPEC["files"]:
            path = self.directory / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic fixture")
            files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
        self.addCleanup(patch.stopall)
        patch.object(builtin, "RESOURCE_SPEC", {**builtin.RESOURCE_SPEC, "files": files}).start()
        self.tokenizer = Mock()
        self.tokenizer.encode.return_value = SimpleNamespace(ids=[1, 2], attention_mask=[1, 1])
        self.tokenizer_factory = patch("tokenizers.Tokenizer.from_file", return_value=self.tokenizer).start()
        self.session = Mock()
        self.session.get_providers.return_value = ["CPUExecutionProvider"]
        self.session.get_inputs.return_value = [SimpleNamespace(name=n) for n in ("input_ids", "attention_mask")]
        self.session.get_outputs.return_value = [SimpleNamespace(name="sentence_embedding")]
        self.session.run.return_value = [np.ones((1, 768), dtype=np.float32)]
        self.session_factory = patch("onnxruntime.InferenceSession", return_value=self.session).start()
        self.provider = builtin.BuiltinEmbeddingProvider(self.directory)
        self.addCleanup(self.provider.close)

    def check_error(self, code, operation=None):
        with self.assertRaisesRegex(builtin.BuiltinEmbeddingError, "^" + code + "$"):
            (operation or (lambda: self.provider.embed("synthetic")))()
        self.assertEqual(self.provider.error_code, code)
        self.assertEqual(self.provider.provider_status, "failed")

    def test_lazy_cpu_reuse_and_task_prefixes(self):
        self.session_factory.assert_not_called()
        a = self.provider.embed("  咖啡  ")
        b = self.provider.embed_query("What coffee?")
        self.assertEqual(len(a), 768)
        self.assertAlmostEqual(np.linalg.norm(a), 1, places=6)
        self.assertTrue(np.isfinite(b).all())
        self.assertEqual(self.tokenizer.encode.call_args_list[0].args[0], "title: none | text: 咖啡")
        self.assertEqual(self.tokenizer.encode.call_args_list[1].args[0], "task: search result | query: What coffee?")
        self.session_factory.assert_called_once()
        self.assertEqual(self.session_factory.call_args.kwargs["providers"], ["CPUExecutionProvider"])
        self.session.disable_fallback.assert_called_once()

    def test_threaded_calls_load_once_and_close_is_terminal(self):
        with ThreadPoolExecutor(max_workers=4) as workers:
            vectors = list(workers.map(self.provider.embed_query, ["English", "中文"] * 4))
        self.assertEqual(len(vectors), 8)
        self.session_factory.assert_called_once()
        self.provider.close()
        with self.assertRaisesRegex(ValueError, "BUILTIN_EMBEDDING_CLOSED"):
            self.provider.embed("closed")
        self.assertIsNone(self.provider._session)

    def test_missing_external_data(self):
        (self.directory / "onnx/model_q4.onnx_data").unlink()
        self.check_error("BUILTIN_EMBEDDING_FILE_MISSING")
        self.session_factory.assert_not_called()

    def test_corrupt_file_hash(self):
        (self.directory / "tokenizer.json").write_bytes(b"damaged")
        self.check_error("BUILTIN_EMBEDDING_SHA_MISMATCH")
        self.tokenizer_factory.assert_not_called()

    def test_tokenizer_initialization_failure(self):
        self.tokenizer_factory.side_effect = RuntimeError("do not expose private details")
        self.check_error("BUILTIN_EMBEDDING_TOKENIZER_INIT_FAILED")
        self.session_factory.assert_not_called()

    def test_onnx_initialization_failure(self):
        self.session_factory.side_effect = RuntimeError("private runtime path")
        self.check_error("BUILTIN_EMBEDDING_ONNX_INIT_FAILED")

    def test_no_gpu_fallback(self):
        self.session.get_providers.return_value = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        self.check_error("BUILTIN_EMBEDDING_ONNX_INIT_FAILED")

    def test_inference_failure_safe_code(self):
        self.session.run.side_effect = RuntimeError("memory contents must not leak")
        self.check_error("BUILTIN_EMBEDDING_INFERENCE_FAILED")

    def test_vector_validation_rejects_wrong_shape_zero_nan_and_overflow(self):
        for vector in (np.ones((1, 767)), np.zeros((1, 768)), np.full((1, 768), np.nan),
                       np.full((1, 768), np.inf)):
            with self.subTest(shape=vector.shape):
                self.session.run.return_value = [vector]
                self.check_error("BUILTIN_EMBEDDING_VECTOR_INVALID")

    def test_input_too_long_is_not_silently_truncated(self):
        self.tokenizer.encode.return_value = SimpleNamespace(ids=[1] * 2049, attention_mask=[1] * 2049)
        self.check_error("BUILTIN_EMBEDDING_INPUT_TOO_LONG")
        self.session.run.assert_not_called()

    def test_control_preserves_fault_code_and_stops_worker(self):
        gate = EmbeddingControl(self.provider, self.directory / "control.json", cloud=False)
        gate.command("start")
        self.session.run.side_effect = RuntimeError("synthetic")
        with self.assertRaisesRegex(ValueError, "BUILTIN_EMBEDDING_INFERENCE_FAILED"):
            gate.embed_query("text")
        self.assertEqual(gate.mode, "failed")
        self.assertEqual(gate.error_code, "BUILTIN_EMBEDDING_INFERENCE_FAILED")
        self.assertEqual(gate.in_flight, 0)
        with self.assertRaisesRegex(ValueError, "EMBEDDING_PAUSED"):
            gate.embed("another")

    def test_probe_releases_session(self):
        main = MainModelConfig(display_name="Mock", provider_type="mock", model="fixed-greeting-v1")
        with patch("app.model_api_config_api.build_embedding_provider", return_value=self.provider):
            result = asyncio.run(_test_embedding(ModelApiConfig(main_model=main,
                embedding=EmbeddingConfig(**values(self.directory))), None))
        self.assertTrue(result["success"])
        self.assertEqual(self.provider.provider_status, "closed")


class BuiltinConfigurationTests(unittest.TestCase):
    def test_v1_builtin_uses_local_preset_when_projected_to_v2(self):
        from app.model_profile_config import as_v2
        with tempfile.TemporaryDirectory() as directory:
            store = ModelApiConfigStore(Path(directory) / "legacy.json")
            main = MainModelConfig(display_name="Mock", provider_type="mock", model="fixed-greeting-v1")
            original = store.save(ModelApiConfig(main_model=main,
                embedding=EmbeddingConfig(**values(Path(directory)))), expected_revision=0)
            before = store.path.read_bytes()
            projected = as_v2(store.load())
            self.assertEqual(projected.embedding_profiles[0].preset_id, "builtin_gemma")
            self.assertEqual(projected.activation, ModelApiConfig(main_model=main, embedding=original.embedding))
            self.assertEqual(before, store.path.read_bytes())
            self.assertEqual(build_embedding_from_model_config(projected).provider_status, "unloaded")

    def test_wrong_revision_model_dimension_and_remote_connection_rejected(self):
        directory = Path(tempfile.gettempdir())
        for update in (dict(model="latest"), dict(model_version="wrong"), dict(dimensions=1024),
                       dict(base_url="https://example.test"), dict(api_key="synthetic"),
                       dict(reuse_main_api=True), dict(resource_directory="relative")):
            with self.subTest(update=update), self.assertRaises(ValueError):
                EmbeddingConfig(**{**values(directory), **update})
        with self.assertRaisesRegex(ValueError, "BUILTIN_EMBEDDING_VERSION_INVALID"):
            builtin.BuiltinEmbeddingProvider(directory, model_version="wrong")

    def test_space_fixed_and_path_independent_but_text_version_separate(self):
        first = build_embedding_provider(EmbeddingConfig(**values(Path(tempfile.gettempdir()))))
        second = builtin.BuiltinEmbeddingProvider(Path(tempfile.gettempdir()) / "other")
        self.assertEqual(first.model_key, second.model_key)
        self.assertNotEqual(first.model_key, OllamaEmbeddingProvider("http://127.0.0.1:11434",
            "qwen3-embedding:0.6b", identity_v2=True).model_key)
        with patch("app.canonical_embeddings.TEXT_VERSION", "canonical-embedding-text/1"):
            old = first.model_key
        self.assertNotEqual(old, first.model_key)
        self.assertEqual(first.provider_status, "unloaded")

    def test_v2_save_activation_restart_and_defaults_are_compatible(self):
        with tempfile.TemporaryDirectory() as directory:
            store = ModelApiConfigStore(Path(directory) / "models.json")
            api = FastAPI()
            api.include_router(create_model_profile_router(store))
            client = TestClient(api)
            empty = client.get("/v2/model-config").json()
            self.assertFalse(empty["configured"])
            self.assertEqual(empty["default_embedding_preset_id"], "builtin_gemma")
            self.assertEqual(empty["embedding_presets"][0]["preset_id"], "aliyun")
            def draft(kind, config, preset, revision):
                return dict(expected_revision=revision, kind=kind, profile_id=kind + "-test",
                            preset_id=preset, config=config)
            main = dict(display_name="Mock", provider_type="mock", model="fixed-greeting-v1")
            command = dict(expected_revision=0, main=draft("main", main, "custom", 0),
                embedding=draft("embedding", values(Path(directory)), "builtin_gemma", 0), embedding_enabled=True)
            response = client.post("/v2/model-config/save-and-activate", json=command)
            self.assertEqual(response.status_code, 200, response.text)
            self.assertTrue(response.json()["restart_required"])
            active = store.load()
            adapter = build_embedding_from_model_config(active)
            self.assertIsInstance(adapter, builtin.BuiltinEmbeddingProvider)
            self.assertEqual(adapter.provider_status, "unloaded")
            # A newly saved v2 Main profile now receives the explicit 16K default.
            self.assertEqual(active.main_model.model_dump(), MainModelConfig(**main, context_length=16384).model_dump())
            self.assertFalse(restart_required(active, store.load()))
            edited = draft("embedding", values(Path(directory) / "other"), "builtin_gemma", 1)
            self.assertEqual(client.post("/v2/model-config/profile", json=edited).status_code, 200)
            self.assertFalse(restart_required(active, store.load()))
            apply = dict(expected_revision=2, kind="embedding", profile_id="embedding-test", embedding_enabled=True)
            self.assertEqual(client.post("/v2/model-config/activate", json=apply).status_code, 200)
            self.assertTrue(restart_required(active, store.load()))
            self.assertEqual(adapter.resource_directory, Path(directory))


class BuiltinCanonicalContracts(unittest.TestCase):
    def setUp(self):
        self.fixture = d1a.CanonicalRetrievalTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.adapter = builtin.BuiltinEmbeddingProvider(Path(self.temp.name))
        self.gate = EmbeddingControl(self.adapter, Path(self.temp.name) / "control.json", cloud=False)
        self.service = CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(
            self.fixture.fixture.database.connect, CanonicalMemoryRanker(self.gate)))

    def test_pending_and_model_failure_do_not_claim_memory_absent(self):
        target = self.fixture.add()
        args = dict(observer=d1a.OWNER, subject=d1a.A)
        result = self.service.retrieve(semantic_query="galactic telescope", **args)
        self.assertEqual(result.status, "RECALL_UNAVAILABLE")
        self.assertEqual(result.eligible_count, 1)
        lexical = self.service.retrieve(semantic_query="杯子", **args)
        self.assertEqual([i.memory_id for i in lexical.items], [target])
        self.assertEqual(lexical.status, "OK")
        self.assertEqual(self.service.retrieve(observer=d1a.OWNER, subject=d1a.B,
            semantic_query="galactic telescope").status, "NO_MEMORY_FOUND")
        self.gate.command("start")
        index = CanonicalEmbeddingIndex(self.fixture.fixture.database.connect)
        row = index.pending(self.gate.model_key, 8, 768)[0]
        index.store_if_current(row, self.gate.model_key, [1] + [0] * 767)
        failed = self.service.retrieve(semantic_query="galactic telescope", **args)
        self.assertEqual(failed.status, "RECALL_UNAVAILABLE")
        self.assertEqual(self.gate.error_code, "BUILTIN_EMBEDDING_FILE_MISSING")
        self.assertEqual(self.fixture.read("杯子").items[0].memory_id, target)

    def test_indexer_uses_documents_and_ranker_uses_query_method(self):
        target = self.fixture.add(profile="blue")
        self.adapter.embed = Mock(return_value=tuple([1] + [0] * 767))
        self.adapter.embed_query = Mock(return_value=tuple([1] + [0] * 767))
        self.gate.command("start")
        worker = ManagedEmbeddingIndexer(CanonicalEmbeddingIndex(self.fixture.fixture.database.connect), self.gate)
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 1)
        self.assertIn("blue", self.adapter.embed.call_args.args[0])
        result = self.service.retrieve(observer=d1a.OWNER, subject=d1a.A, semantic_query="colour preference")
        self.assertEqual(result.items[0].memory_id, target)
        self.adapter.embed_query.assert_called_once_with("colour preference")
        self.adapter.embed.assert_called_once()
