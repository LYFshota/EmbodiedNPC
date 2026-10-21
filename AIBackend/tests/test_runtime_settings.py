from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

from pydantic import ValidationError

from app.runtime_settings import (
    ProviderPreset, RuntimeSettings, RuntimeSettingsDocument, code_defaults,
)
from app.runtime_settings_store import (
    RuntimeSettingsStore, SettingsInvalidDocument, SettingsRevisionConflict,
    SettingsStorageError, default_settings_path,
)


def settings_with_selected(selected: str) -> RuntimeSettings:
    return code_defaults().model_copy(update={"selected_preset_id": selected})


class RuntimeSettingsDomainTests(unittest.TestCase):
    def test_defaults_match_current_safe_bootstrap_values(self) -> None:
        settings = code_defaults()
        self.assertEqual("mock", settings.selected_preset_id)
        qwen = settings.provider_presets[1]
        self.assertEqual(("ollama", "qwen3.5:4b", 8192, 30),
                         (qwen.provider_type, qwen.model, qwen.context_length, qwen.timeout_seconds))
        self.assertEqual("http://127.0.0.1:11434", qwen.base_url)

    def test_valid_future_provider_type_and_credential_reference(self) -> None:
        preset = ProviderPreset(preset_id="deepseek-official", display_name="DeepSeek",
                                provider_type="deepseek_responses", model="deepseek-chat",
                                credential_reference="DEEPSEEK_API_KEY")
        self.assertEqual("deepseek_responses", preset.provider_type)

    def test_duplicate_and_missing_selected_id_rejected(self) -> None:
        mock = code_defaults().provider_presets[0]
        with self.assertRaises(ValidationError):
            RuntimeSettings(provider_presets=(mock, mock), selected_preset_id="mock")
        with self.assertRaises(ValidationError):
            RuntimeSettings(provider_presets=(mock,), selected_preset_id="missing")

    def test_malformed_generation_and_url_rejected(self) -> None:
        for update in ({"generation": {"temperature": 3}},
                       {"generation": {"thinking": "yes"}},
                       {"timeout_seconds": -1}, {"base_url": "https://user:pass@example.test"},
                       {"base_url": "https://example.test/?token=secret"}):
            with self.subTest(update=update), self.assertRaises(ValidationError):
                ProviderPreset.model_validate({"preset_id": "future", "display_name": "Future",
                                               "provider_type": "future", "model": "model", **update})

    def test_unknown_and_direct_secret_fields_rejected(self) -> None:
        base = {"preset_id": "future", "display_name": "Future",
                "provider_type": "future", "model": "model"}
        for secret_field in ("api_key", "token", "authorization", "bearer_token",
                             "password", "secret_value", "cookie"):
            with self.subTest(secret_field=secret_field), self.assertRaises(ValidationError):
                ProviderPreset.model_validate({**base, secret_field: "sk-example"})
        with self.assertRaises(ValidationError):
            ProviderPreset.model_validate({**base, "model": "sk-1234567890"})

    def test_schema_revision_and_utc_are_strict(self) -> None:
        payload = {"schema_version": 1, "revision": 0,
                   "updated_at": "2026-09-28T12:00:00Z",
                   "settings": code_defaults().model_dump(mode="json")}
        self.assertEqual(0, RuntimeSettingsDocument.model_validate_json(json.dumps(payload)).revision)
        for bad in ({**payload, "schema_version": 2}, {**payload, "revision": -1},
                    {**payload, "updated_at": "2026-09-28T12:00:00"},
                    {**payload, "extra": 1}):
            with self.subTest(bad=bad), self.assertRaises(ValidationError):
                RuntimeSettingsDocument.model_validate_json(json.dumps(bad))

    def test_diagnostic_is_allowlisted_without_credential_reference_or_url(self) -> None:
        future = ProviderPreset(preset_id="deepseek", display_name="DeepSeek",
                                provider_type="deepseek_responses", model="deepseek-chat",
                                credential_reference="DEEPSEEK_API_KEY",
                                base_url="https://api.example.test")
        settings = RuntimeSettings(provider_presets=(future,), selected_preset_id="deepseek")
        document = RuntimeSettingsDocument(revision=4, updated_at=datetime(2026, 9, 28, 12, tzinfo=timezone.utc),
                                           settings=settings)
        with patch.dict(os.environ, {"DEEPSEEK_API_KEY": "sk-test-sensitive-123456789"}):
            diagnostic = document.diagnostic()
        text = json.dumps(diagnostic)
        self.assertEqual("environment", diagnostic["credential_source"])
        self.assertEqual(4, diagnostic["settings_revision"])
        self.assertNotIn("DEEPSEEK_API_KEY", text)
        self.assertNotIn("api.example.test", text)
        self.assertNotIn("api_key", text)
        self.assertNotIn("sk-test-sensitive", text)


class RuntimeSettingsStoreTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "nested" / "runtime_settings.json"
        self.store = RuntimeSettingsStore(self.path)

    def test_missing_file_returns_defaults_without_writing(self) -> None:
        document = self.store.load()
        self.assertEqual(0, document.revision)
        self.assertEqual("mock", document.settings.selected_preset_id)
        self.assertFalse(self.path.exists())

    def test_first_and_next_save_reload_and_parent_creation(self) -> None:
        first = self.store.save(settings_with_selected("local-qwen"), expected_revision=0)
        self.assertEqual(1, first.revision)
        self.assertTrue(self.path.exists())
        self.assertEqual("local-qwen", RuntimeSettingsStore(self.path).load().settings.selected_preset_id)
        second = self.store.save(code_defaults(), expected_revision=1)
        self.assertEqual(2, second.revision)
        self.assertEqual("mock", RuntimeSettingsStore(self.path).load().settings.selected_preset_id)

    def test_stale_revision_preserves_file(self) -> None:
        self.store.save(code_defaults(), expected_revision=0)
        before = self.path.read_bytes()
        with self.assertRaises(SettingsRevisionConflict) as caught:
            RuntimeSettingsStore(self.path).save(settings_with_selected("local-qwen"), expected_revision=0)
        self.assertEqual("SETTINGS_REVISION_CONFLICT", caught.exception.code)
        self.assertEqual(before, self.path.read_bytes())

    def test_corrupt_unknown_or_future_schema_is_not_overwritten(self) -> None:
        self.path.parent.mkdir(parents=True)
        valid = self.store.save(code_defaults(), expected_revision=0).model_dump(mode="json")
        for raw in (b'{"schema_ver',
                    json.dumps({**valid, "schema_version": 2}).encode(),
                    json.dumps({**valid, "settings": {"provider_presets": [],
                                                     "selected_preset_id": "mock"}}).encode(),
                    json.dumps({**valid, "unknown": 1}).encode(),
                    b'{"revision":0,"revision":1}'):
            with self.subTest(raw=raw):
                self.path.write_bytes(raw)
                with self.assertRaises(SettingsInvalidDocument):
                    self.store.load()
                with self.assertRaises(SettingsInvalidDocument):
                    self.store.save(code_defaults(), expected_revision=0)
                self.assertEqual(raw, self.path.read_bytes())

    def test_atomic_replace_failure_keeps_previous_document(self) -> None:
        self.store.save(code_defaults(), expected_revision=0)
        before = self.path.read_bytes()
        with patch("app.runtime_settings_store.os.replace", side_effect=OSError("blocked")):
            with self.assertRaises(SettingsStorageError):
                self.store.save(settings_with_selected("local-qwen"), expected_revision=1)
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual([], list(self.path.parent.glob(".runtime_settings-*.tmp")))

    def test_invalid_expected_revision_rejected(self) -> None:
        for revision in (-1, True, "0"):
            with self.subTest(revision=revision), self.assertRaises(SettingsInvalidDocument):
                self.store.save(code_defaults(), expected_revision=revision)

    def test_revalidates_even_a_model_copy_that_bypassed_validation(self) -> None:
        invalid = code_defaults().model_copy(update={"selected_preset_id": "missing"})
        with self.assertRaises(SettingsInvalidDocument):
            self.store.save(invalid, expected_revision=0)
        self.assertFalse(self.path.exists())

    def test_corrupt_file_blocks_startup_without_printing_document_contents(self) -> None:
        self.path.parent.mkdir(parents=True)
        self.path.write_text('{"api_key":"sk-test-sensitive-123456789"', encoding="utf-8")
        env = {**os.environ, "EMBODIEDNPC_SETTINGS_FILE": str(self.path),
               "EMBODIEDNPC_PROVIDER": "mock"}
        result = subprocess.run([sys.executable, "-B", "-c", "from app import m3_main"],
                                env=env, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SETTINGS_DOCUMENT_INVALID", result.stderr)
        self.assertNotIn("sk-test-sensitive", result.stderr)
        self.assertEqual('{"api_key":"sk-test-sensitive-123456789"',
                         self.path.read_text(encoding="utf-8"))

    def test_default_path_is_independent_of_memory_db(self) -> None:
        path = self.path
        with patch.dict(os.environ, {"EMBODIEDNPC_SETTINGS_FILE": str(path),
                                      "EMBODIEDNPC_MEMORY_DB": str(path.parent / "other.db")}):
            self.assertEqual(path, default_settings_path())

    def test_memory_database_cleanup_cannot_remove_saved_settings(self) -> None:
        self.store.save(settings_with_selected("local-qwen"), expected_revision=0)
        memory_path = self.path.parent / "memory.sqlite3"
        memory_path.write_bytes(b"test fixture")
        memory_path.unlink()
        self.assertEqual("local-qwen", self.store.load().settings.selected_preset_id)

    def test_saved_preset_does_not_change_production_provider_or_worker_wiring(self) -> None:
        deepseek = ProviderPreset(preset_id="deepseek", display_name="DeepSeek",
                                  provider_type="deepseek_responses", model="deepseek-chat",
                                  credential_reference="DEEPSEEK_API_KEY")
        settings = RuntimeSettings(provider_presets=code_defaults().provider_presets + (deepseek,),
                                   selected_preset_id="deepseek")
        self.store.save(settings, expected_revision=0)
        code = ("from app import m3_main as m; "
                "print(m.saved_settings_document.settings.selected_preset_id, "
                "m.provider.name, m.cognitive_entry.provider is m.provider, "
                "m.base_memory_consolidator.provider is m.provider, "
                "m.relationship_reflection_worker.model.provider is m.provider)")
        for active in ("mock", "ollama"):
            with self.subTest(active=active):
                env = {**os.environ, "EMBODIEDNPC_SETTINGS_FILE": str(self.path),
                       "EMBODIEDNPC_MEMORY_DB": str(self.path.parent / (active + ".db")),
                       "EMBODIEDNPC_PROVIDER": active,
                       "CANONICAL_EMBEDDING_ENABLED": "false"}
                result = subprocess.run([sys.executable, "-B", "-c", code], env=env,
                                        capture_output=True, text=True, check=True)
                self.assertEqual(f"deepseek {active} True True True", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
