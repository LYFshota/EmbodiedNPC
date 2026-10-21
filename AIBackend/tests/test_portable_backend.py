"""Portable path/resource boundaries only; never imports app.main or calls a model."""

import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from app.builtin_embedding_provider import MODEL, REVISION
from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_api_config_api import _view
from app.model_api_config_store import ModelApiConfigStore
from app.model_config_runtime import build_embedding_provider, effective_runtime_config, restart_required
from app.model_profile_api import public_profile_view
from app.model_profile_config import as_v2
from app.portable_resources import resolve_embedding_resources
from app import portable_runtime

SPEC = importlib.util.spec_from_file_location("portable_launcher", Path(__file__).parents[1] / "portable/launcher.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


class PortableBackendTests(unittest.TestCase):
    def setUp(self):
        self.previous_temp = tempfile.tempdir
        self.env = patch.dict(os.environ)
        self.env.start()
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("PORTABLE_TEST_SCRATCH"))
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(self.env.stop)
        self.addCleanup(lambda: setattr(tempfile, "tempdir", self.previous_temp))
        self.root = Path(self.temp.name) / "测试 UserData"
        self.backend = Path(self.temp.name) / "Backend"
        self.backend.mkdir()
        os.environ.pop("EMBODIEDNPC_DATA_ROOT", None)

    def builtin(self, directory=None):
        return EmbeddingConfig(enabled=True, provider_type="builtin", model=MODEL,
            dimensions=768, model_version=REVISION,
            resource_directory=str(directory or (Path(self.temp.name) / "old missing install")))

    def portable(self):
        os.environ.update(EMBODIEDNPC_DATA_ROOT=str(self.root),
            EMBODIEDNPC_BUILTIN_EMBEDDING_DIR=str(self.backend / "models" / MODEL))

    def test_complete_environment_mapping_before_app_import_without_config_creation(self):
        os.environ.update(EMBODIEDNPC_MEMORY_DB="forbidden-development-db", OPENAI_API_KEY="synthetic-inherited")
        launcher.writable(self.root)
        launcher.prepare_environment(self.root, self.backend, 18271)
        for key, name in launcher.FILES.items():
            self.assertEqual(str(self.root / "Backend" / name), os.environ[key])
        self.assertEqual(str(self.root / "runtime/knowledge_admin_session.json"), os.environ["EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE"])
        self.assertEqual("http://127.0.0.1:18271", os.environ["EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"])
        self.assertNotIn("OPENAI_API_KEY", os.environ)
        self.assertFalse(list((self.root / "Backend").iterdir()))
        self.assertEqual(str(self.root / "runtime/tmp"), tempfile.gettempdir())

    def test_missing_relative_or_volume_root_is_rejected(self):
        for value in ("", "relative/UserData", str(Path(self.temp.name).anchor)):
            os.environ["EMBODIEDNPC_DATA_ROOT"] = value
            with self.assertRaises(launcher.PortableError):
                launcher.data_root()

    def test_portable_composition_requires_live_in_process_lock_witness(self):
        self.portable()
        with patch.object(portable_runtime, "_owner", None):
            with self.assertRaisesRegex(RuntimeError, "PORTABLE_LAUNCH_REQUIRED"):
                portable_runtime.require_portable_owner()
            portable_runtime.own_portable_data(self.root, lambda: True)
            portable_runtime.require_portable_owner()
            portable_runtime.own_portable_data(self.root, lambda: False)
            with self.assertRaisesRegex(RuntimeError, "PORTABLE_LAUNCH_REQUIRED"):
                portable_runtime.require_portable_owner()

    def test_paths_cannot_escape_root(self):
        with self.assertRaisesRegex(launcher.PortableError, "ESCAPES"):
            launcher.contained(self.root, self.root / "../other/config.json")

    def test_unwritable_probe_aborts_before_app_import(self):
        (self.backend / "app").mkdir()
        (self.backend / "app/main.py").touch()
        with patch.object(launcher, "writable", side_effect=PermissionError):
            with self.assertRaises(PermissionError):
                launcher.start(self.root, self.backend, 18271)
        self.assertFalse(self.root.exists())

    def test_denied_write_probe_is_single_attempt(self):
        self.root.mkdir()
        with patch.object(launcher.os, "open", side_effect=PermissionError) as opening:
            with self.assertRaises(PermissionError):
                launcher.writable(self.root)
        self.assertEqual(1, opening.call_count)

    def test_stop_does_not_initialize_or_rewrite_configuration(self):
        launcher.stop(self.root, self.backend, 1)
        self.assertFalse(self.root.exists())
        (self.root / "runtime").mkdir(parents=True)
        config = self.root / "Backend/model_api_configs.json"
        config.parent.mkdir()
        config.write_bytes(b"preserve existing bytes")
        with launcher.DataLock(self.root / "runtime/backend.lock", create=True):
            pass
        launcher.stop(self.root, self.backend, 1)
        self.assertEqual(b"preserve existing bytes", config.read_bytes())

    def test_os_lock_rejects_second_handle_and_releases(self):
        path = Path(self.temp.name) / "backend.lock"
        with launcher.DataLock(path, create=True) as first:
            self.assertTrue(first.acquire())
            with launcher.DataLock(path, create=True) as second:
                self.assertFalse(second.acquire())
                first.release()
                self.assertTrue(second.acquire())
        with launcher.DataLock(path, create=False) as recovered:
            self.assertTrue(recovered.acquire())

    def test_resource_view_runtime_comparison_agree_without_writing_or_new_identity(self):
        self.portable()
        store = ModelApiConfigStore(Path(self.temp.name) / "model_api_configs.json")
        legacy = store.save(ModelApiConfig(main_model=MainModelConfig(display_name="fixture",
            provider_type="openai_compatible", base_url="https://synthetic.invalid/v1",
            api_key="synthetic-only-retained-key", model="fixture"), embedding=self.builtin()), expected_revision=0)
        document = as_v2(legacy)
        store._write_document(document)
        before = store.path.read_bytes()
        newer = document.model_copy(update={"activation": document.activation.model_copy(update={
            "embedding": self.builtin(Path(self.temp.name) / "another old install")})})
        self.assertFalse(restart_required(document, newer))
        self.assertEqual(effective_runtime_config(document), effective_runtime_config(newer))
        view = public_profile_view(document, newer)
        effective = str(self.backend / "models" / MODEL)
        self.assertEqual(effective, view["embedding_profiles"][0]["config"]["resource_directory"])
        self.assertEqual(effective, view["selected_to_activate"]["embedding"]["resource_directory"])
        self.assertEqual(effective, _view(document).embedding.resource_directory)
        self.assertNotIn("synthetic-only-retained-key", json.dumps(view))
        provider = build_embedding_provider(document.embedding)
        self.assertEqual(Path(effective), provider.resource_directory)
        model_key = provider.model_key
        os.environ["EMBODIEDNPC_BUILTIN_EMBEDDING_DIR"] = str(self.backend / "relocated models")
        self.assertEqual(model_key, build_embedding_provider(document.embedding).model_key)
        self.assertEqual(before, store.path.read_bytes())
        self.assertIn(b"synthetic-only-retained-key", before)

    def test_development_and_nonbuiltin_semantics_are_unchanged(self):
        original = self.builtin()
        self.assertIs(original, resolve_embedding_resources(original))
        self.assertEqual(Path(original.resource_directory), build_embedding_provider(original).resource_directory)
        self.portable()
        for config in (EmbeddingConfig(), EmbeddingConfig(enabled=True, provider_type="ollama",
                base_url="http://127.0.0.1:11434", model="synthetic"),
                EmbeddingConfig(enabled=True, provider_type="openai_compatible",
                    base_url="https://synthetic.invalid/v1", model="synthetic")):
            self.assertIs(config, resolve_embedding_resources(config))

    def test_portable_missing_resource_contract_fails_without_old_path_fallback(self):
        self.portable()
        os.environ.pop("EMBODIEDNPC_BUILTIN_EMBEDDING_DIR")
        with self.assertRaisesRegex(ValueError, "PORTABLE_RESOURCE_DIRECTORY_INVALID"):
            resolve_embedding_resources(self.builtin())


if __name__ == "__main__":
    unittest.main()
