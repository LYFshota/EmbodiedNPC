from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.providers import ProviderHealth
from app.runtime_settings import ProviderPreset, RuntimeSettings, code_defaults
from app.runtime_settings_api import create_runtime_settings_router
from app.runtime_settings_store import RuntimeSettingsStore


class FakeProvider:
    name = "ollama"
    model = "active-local-model"

    def __init__(self) -> None:
        self.available = True
        self.detail = ""

    async def health(self) -> ProviderHealth:
        return ProviderHealth(provider=self.name, model=self.model,
                              available=self.available, detail=self.detail)


class RuntimeSettingsApiTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "settings.json"
        self.store = RuntimeSettingsStore(self.path)
        self.provider = FakeProvider()
        app = FastAPI()
        app.include_router(create_runtime_settings_router(self.store, self.provider))
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def replacement(self) -> dict:
        view = self.client.get("/v1/settings").json()
        return {"expected_revision": view["revision"], "settings": view["settings"]}

    def test_defaults_roundtrip_and_fresh_persisted_read(self) -> None:
        initial = self.client.get("/v1/settings")
        self.assertEqual(200, initial.status_code)
        self.assertEqual(("defaults", 0, None),
                         (initial.json()["source"], initial.json()["revision"], initial.json()["updated_at"]))
        self.assertFalse(self.path.exists())
        payload = self.replacement()
        payload["settings"]["selected_preset_id"] = "local-qwen"
        saved = self.client.put("/v1/settings", json=payload)
        self.assertEqual(200, saved.status_code, saved.text)
        self.assertEqual(("persisted", 1, "local-qwen"),
                         (saved.json()["source"], saved.json()["revision"],
                          saved.json()["settings"]["selected_preset_id"]))
        self.assertIsNotNone(saved.json()["updated_at"])
        self.assertEqual(saved.json(), self.client.get("/v1/settings").json())
        self.assertEqual("local-qwen", RuntimeSettingsStore(self.path).load().settings.selected_preset_id)
        again = self.replacement()
        again["settings"]["selected_preset_id"] = "mock"
        self.assertEqual(2, self.client.put("/v1/settings", json=again).json()["revision"])

    def test_revision_conflict_preserves_file(self) -> None:
        payload = self.replacement()
        self.assertEqual(200, self.client.put("/v1/settings", json=payload).status_code)
        before = self.path.read_bytes()
        response = self.client.put("/v1/settings", json=payload)
        self.assertEqual(409, response.status_code)
        self.assertEqual("SETTINGS_REVISION_CONFLICT", response.json()["error"]["code"])
        self.assertEqual(before, self.path.read_bytes())

    def test_invalid_and_secret_input_returns_safe_422(self) -> None:
        payload = self.replacement()
        preset = payload["settings"]["provider_presets"][0]
        for key, value in (("api_key", "sk-test-sensitive-123456789"),
                           ("base_url", "https://user:secret@example.test"),
                           ("model", "sk-1234567890"),
                           ("timeout_seconds", -1)):
            with self.subTest(key=key):
                invalid = json.loads(json.dumps(payload))
                invalid["settings"]["provider_presets"][0][key] = value
                response = self.client.put("/v1/settings", json=invalid)
                self.assertEqual(422, response.status_code)
                self.assertNotIn("sensitive", response.text)
                self.assertNotIn("secret", response.text)
                self.assertFalse(self.path.exists())

        for mutation in (lambda body: body["settings"].update(selected_preset_id="unknown"),
                         lambda body: body["settings"]["provider_presets"].append(
                             body["settings"]["provider_presets"][0])):
            invalid = json.loads(json.dumps(payload))
            mutation(invalid)
            response = self.client.put("/v1/settings", json=invalid)
            self.assertEqual(422, response.status_code)
            self.assertEqual("SETTINGS_VALUE_INVALID", response.json()["error"]["code"])
        self.assertFalse(self.path.exists())

    def test_existing_credential_binding_roundtrips_without_disclosure(self) -> None:
        cloud = ProviderPreset(preset_id="cloud", display_name="Cloud", provider_type="cloud",
                               model="cloud-model", credential_reference="CLOUD_API_KEY")
        settings = RuntimeSettings(provider_presets=code_defaults().provider_presets + (cloud,),
                                   selected_preset_id="cloud")
        self.store.save(settings, expected_revision=0)
        view = self.client.get("/v1/settings")
        self.assertEqual(200, view.status_code)
        self.assertNotIn("CLOUD_API_KEY", view.text)
        self.assertEqual("environment", view.json()["settings"]["provider_presets"][-1]["credential_source"])
        payload = self.replacement()
        response = self.client.put("/v1/settings", json=payload)
        self.assertEqual(200, response.status_code, response.text)
        self.assertEqual("CLOUD_API_KEY", self.store.load().settings.provider_presets[-1].credential_reference)
        self.assertNotIn("CLOUD_API_KEY", response.text)
        payload = self.replacement()
        payload["settings"]["provider_presets"].append({**payload["settings"]["provider_presets"][-1],
                                                            "preset_id": "new-cloud"})
        rejected = self.client.put("/v1/settings", json=payload)
        self.assertEqual(422, rejected.status_code)
        self.assertNotIn("CLOUD_API_KEY", rejected.text)

    def test_runtime_status_separates_saved_and_active_and_sanitizes_health(self) -> None:
        payload = self.replacement()
        payload["settings"]["selected_preset_id"] = "local-qwen"
        self.client.put("/v1/settings", json=payload)
        status = self.client.get("/v1/runtime-status")
        self.assertEqual(200, status.status_code)
        self.assertEqual("local-qwen", status.json()["settings"]["selected_preset_id"])
        self.assertEqual(("ollama", "active-local-model"),
                         (status.json()["active_provider"]["provider"], status.json()["active_provider"]["model"]))
        self.assertEqual("not_supported", status.json()["provider_settings_activation"])
        self.assertNotIn("restart_required", status.text)
        self.provider.available = False
        self.provider.detail = "sk-test-sensitive-123456789"
        degraded = self.client.get("/v1/runtime-status")
        self.assertEqual("degraded", degraded.json()["status"])
        self.assertEqual("PROVIDER_HEALTH_UNAVAILABLE", degraded.json()["active_provider"]["detail"])
        self.assertNotIn("sensitive", degraded.text)

    def test_saved_cloud_never_changes_active_mock_and_secret_stays_private(self) -> None:
        cloud = ProviderPreset(preset_id="deepseek", display_name="DeepSeek",
                               provider_type="deepseek_responses", model="deepseek-chat",
                               credential_reference="DEEPSEEK_API_KEY")
        settings = RuntimeSettings(provider_presets=code_defaults().provider_presets + (cloud,),
                                   selected_preset_id="deepseek")
        self.store.save(settings, expected_revision=0)
        self.provider.name, self.provider.model = "mock", "fixed-greeting-v1"
        with patch.dict(os.environ, {"DEEPSEEK_API_KEY": "sk-test-sensitive-123456789"}):
            get_response = self.client.get("/v1/settings")
            payload = self.replacement()
            put_response = self.client.put("/v1/settings", json=payload)
            status_response = self.client.get("/v1/runtime-status")
            error_response = self.client.put("/v1/settings", json=payload)
        for response in (get_response, put_response, status_response, error_response):
            self.assertNotIn("DEEPSEEK_API_KEY", response.text)
            self.assertNotIn("sk-test-sensitive", response.text)
            self.assertNotIn(str(self.path), response.text)
        self.assertEqual("deepseek", status_response.json()["settings"]["selected_preset_id"])
        self.assertEqual("mock", status_response.json()["active_provider"]["provider"])
        self.assertEqual(409, error_response.status_code)

    def test_corrupt_file_and_storage_failure_are_safe(self) -> None:
        payload = self.replacement()
        self.path.write_text('{"api_key":"sk-test-sensitive-123456789"', encoding="utf-8")
        for url in ("/v1/settings", "/v1/runtime-status"):
            response = self.client.get(url)
            self.assertEqual(500, response.status_code)
            self.assertEqual("SETTINGS_DOCUMENT_INVALID", response.json()["error"]["code"])
            self.assertNotIn("sensitive", response.text)
        response = self.client.put("/v1/settings", json=payload)
        self.assertEqual(500, response.status_code)
        self.assertEqual("SETTINGS_DOCUMENT_INVALID", response.json()["error"]["code"])
        self.assertNotIn("sensitive", response.text)
        self.path.unlink()
        with patch("app.runtime_settings_store.os.replace", side_effect=OSError("sk-sensitive")):
            response = self.client.put("/v1/settings", json=payload)
        self.assertEqual(503, response.status_code)
        self.assertEqual("SETTINGS_WRITE_FAILED", response.json()["error"]["code"])
        self.assertNotIn("sensitive", response.text)
        self.assertFalse(self.path.exists())

    def test_production_app_routes_use_actual_mock_provider(self) -> None:
        env = {**os.environ, "EMBODIEDNPC_PROVIDER": "mock",
               "EMBODIEDNPC_SETTINGS_FILE": str(self.path),
               "EMBODIEDNPC_MEMORY_DB": str(self.path.with_suffix(".sqlite3"))}
        script = (
            "from fastapi.testclient import TestClient; "
            "from app import m3_main as m; "
            "c=TestClient(m.app); "
            "s=c.get('/v1/runtime-status'); "
            "print(s.status_code, s.json()['active_provider']['provider'], "
            "s.json()['settings']['source'], c.get('/v1/settings').status_code)"
        )
        result = subprocess.run([sys.executable, "-B", "-c", script], env=env,
                                capture_output=True, text=True, check=True)
        self.assertEqual("200 mock defaults 200", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
