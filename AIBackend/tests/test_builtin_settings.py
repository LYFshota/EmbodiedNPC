"""Settings-only contracts; temporary configs, no production DB or network."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient
from app.model_api_config_store import ModelApiConfigStore
from app.model_profile_api import create_model_profile_router, public_profile_view
from app.builtin_embedding_provider import MODEL, REVISION, RESOURCE_SPEC


class BuiltinSettingsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.resources = Path(self.temp.name) / "resources"
        self.env = patch.dict("os.environ", {"EMBODIEDNPC_BUILTIN_EMBEDDING_DIR": str(self.resources)})
        self.env.start(); self.addCleanup(self.env.stop)
        self.store = ModelApiConfigStore(Path(self.temp.name) / "config.json")
        self.client = self.client_for()

    def client_for(self, active=None):
        app = FastAPI()
        app.include_router(create_model_profile_router(self.store, active_document=active))
        return TestClient(app)

    def combined(self, revision=0, provider="builtin"):
        config = self.client.get("/v2/model-config").json()["builtin_embedding_defaults"]
        config.pop("key_present"); config.pop("resource_status")
        if provider != "builtin":
            config = dict(enabled=True, provider_type="openai_compatible", model="existing-vector",
                base_url="https://synthetic.invalid/v1", reuse_main_api=False, api_key="synthetic-secret")
        def draft(kind, preset, values):
            return dict(expected_revision=revision, kind=kind, profile_id=kind + "-" + preset,
                preset_id=preset, config=values, key_action="replace" if values.get("api_key") else "retain")
        return dict(expected_revision=revision, embedding_enabled=True,
            main=draft("main", "custom", dict(provider_type="mock", model="fixed-greeting-v1", display_name="Test")),
            embedding=draft("embedding", "builtin_gemma" if provider == "builtin" else "custom", config))

    def save(self, command):
        response = self.client.post("/v2/model-config/save-and-activate", json=command)
        self.assertEqual(200, response.status_code, response.text)
        return response.json()

    def test_first_run_defaults_are_controlled_without_activating_or_writing(self):
        view = self.client.get("/v2/model-config").json()
        self.assertEqual("builtin_gemma", view["default_embedding_preset_id"])
        self.assertEqual("deepseek", view["default_main_preset_id"])
        self.assertFalse(view["configured"]); self.assertIsNone(view["active_runtime"])
        c = view["builtin_embedding_defaults"]
        self.assertEqual((MODEL, REVISION, 768), (c["model"], c["model_version"], c["dimensions"]))
        self.assertEqual(str(self.resources), c["resource_directory"])
        self.assertEqual("missing", c["resource_status"])
        self.assertNotIn("api_key", c); self.assertNotIn("base_url", c)
        self.assertFalse(self.store.path.exists())

    def test_presence_never_claims_sha_verified_or_loaded(self):
        for name in RESOURCE_SPEC["files"]:
            path = self.resources / name; path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic-invalid-not-model")
        view = public_profile_view(None)
        self.assertEqual("unverified", view["builtin_resource_status"])
        self.assertIsNone(view["active_runtime"])

    def test_atomic_save_restart_and_roundtrip_do_not_load_resources(self):
        result = self.save(self.combined())
        self.assertEqual(1, result["revision"]); self.assertTrue(result["restart_required"])
        self.assertIsNone(result["active_runtime"])
        restarted = self.client_for(self.store.load()).get("/v2/model-config").json()
        self.assertFalse(restarted["restart_required"])
        self.assertEqual("builtin", restarted["active_runtime"]["embedding"]["provider_type"])
        self.assertEqual("missing", restarted["active_runtime"]["embedding"]["resource_status"])
        self.assertNotIn("resource_status", json.loads(self.store.path.read_text())["activation"]["embedding"])

    def test_external_switchback_preserves_saved_profile_and_key(self):
        self.save(self.combined(provider="external"))
        original = self.store.load().embedding_profiles[0].config
        self.save(self.combined(1))
        self.assertEqual(original, self.store.load().embedding_profiles[0].config)
        self.assertEqual("synthetic-secret", self.store.load().embedding_profiles[0].config.api_key)
        view = self.client.get("/v2/model-config").json()
        self.assertEqual("builtin", view["selected_to_activate"]["embedding"]["provider_type"])
        external = self.combined(2, provider="external")
        external["embedding"]["config"].pop("api_key")
        external["embedding"]["key_action"] = "retain"
        self.save(external)
        self.assertEqual("existing-vector", self.store.load().embedding.model)
        self.assertEqual("synthetic-secret", self.store.load().embedding.api_key)
        self.assertEqual(2, len(self.store.load().embedding_profiles))

    def test_existing_configuration_not_default_migrated(self):
        self.save(self.combined(provider="external"))
        before = self.store.path.read_bytes()
        result = self.client.get("/v2/model-config").json()
        self.assertEqual("custom", result["selected_to_activate"]["embedding_profile_id"].split("-", 1)[1])
        self.assertEqual("aliyun", result["default_embedding_preset_id"])
        self.assertEqual(before, self.store.path.read_bytes())

    def test_revision_failure_and_invalid_fixed_params_are_atomic(self):
        self.save(self.combined()); before = self.store.path.read_bytes()
        self.assertEqual(409, self.client.post("/v2/model-config/save-and-activate", json=self.combined()).status_code)
        for field, value in (("dimensions", 1024), ("model", "wrong-model"), ("model_version", "wrong-revision")):
            command = self.combined(1); command["embedding"]["config"][field] = value
            self.assertEqual(422, self.client.post("/v2/model-config/save-and-activate", json=command).status_code)
            self.assertEqual(before, self.store.path.read_bytes())

    def test_existing_optional_pins_remain_valid_on_settings_roundtrip(self):
        command = self.combined()
        command["embedding"]["config"].update(dimensions=None, model_version=None)
        self.save(command)
        self.assertEqual(MODEL, self.store.load().embedding.model)
        self.assertIsNone(self.store.load().embedding.dimensions)
