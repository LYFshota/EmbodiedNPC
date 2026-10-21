"""Isolated profile persistence, activation and safe v1 migration contracts."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from app.model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from app.model_config_runtime import build_provider_from_model_config, restart_required
from app.model_profile_api import create_model_profile_router
from app.model_api_config_api import create_model_api_config_router
import httpx


KEY = "synthetic-profile-secret"


def main_values(model="chat-a"):
    return {"display_name": "Cloud", "provider_type": "openai_compatible",
            "base_url": "https://synthetic.test/v1", "model": model,
            "temperature": 0.2, "thinking": False, "max_output_tokens": 512,
            "timeout_seconds": 30.0}


def embed_values():
    return {"enabled": True, "provider_type": "openai_compatible", "reuse_main_api": False,
            "base_url": "https://embedding.test/v1", "model": "vector-a", "timeout_seconds": 30.0}


class ModelProfilesV2Tests(unittest.TestCase):
    def test_default_output_1024_preserves_saved_512_and_requires_explicit_activation(self):
        values = main_values()
        values.pop("max_output_tokens")
        self.assertEqual(self.save(values=values).status_code, 200)
        self.assertEqual(self.store.load().main_profiles[0].config.max_output_tokens, 1024)
        self.assertEqual(self.save(1, values={**values, "max_output_tokens": 512}, action="retain").status_code, 200)
        self.activate(2)
        active = self.store.load()
        before = self.store.path.read_bytes()
        self.client.get("/v2/model-config")
        self.assertEqual(before, self.store.path.read_bytes())
        self.assertEqual(self.save(3, values=values, action="retain").status_code, 200)
        self.assertEqual(self.store.load().main_profiles[0].config.max_output_tokens, 512)
        self.assertEqual(self.save(4, values={**values, "max_output_tokens": 1024}, action="retain").status_code, 200)
        self.assertEqual(self.store.load().main_model.max_output_tokens, 512)
        self.activate(5)
        self.assertEqual(self.store.load().main_model.max_output_tokens, 1024)
        self.assertTrue(restart_required(active, self.store.load()))

    def test_new_profiles_default_to_32k_and_unknown_runtime_stays_conservative(self):
        presets = self.client.get("/v2/model-config").json()["main_presets"]
        for preset in presets:
            self.assertEqual(preset["default_context_length"], 32768)
        # An arbitrary gateway/model name receives the explicitly requested cloud
        # default; this is not a statement about its physical service capacity.
        self.assertEqual(self.save().status_code, 200)
        self.assertEqual(self.store.load().main_profiles[0].config.context_length, 32768)
        self.assertEqual(self.activate(1).status_code, 200)
        self.assertEqual(self.store.load().main_model.context_length, 32768)
        from app.model_provider_presets import new_main_context_length
        for provider in ("deepseek_official_responses", "openai_compatible", "gemini_native", "anthropic_native"):
            self.assertEqual(new_main_context_length(dict(provider_type=provider, model="unknown")), 32768)
        self.assertEqual(new_main_context_length(dict(provider_type="ollama")), 32768)
        self.assertEqual(new_main_context_length(dict(provider_type="mock")), 32768)
        self.assertEqual(new_main_context_length(dict(provider_type="unknown")), 32768)
        self.assertEqual(new_main_context_length(dict(provider_type="deepseek_official_responses", model="deepseek-flash")), 32768)

    def test_new_deepseek_32k_and_saved_explicit_capacities_are_not_migrated(self):
        from app.model_profile_api import profile_connection, SaveProfileCommand
        from app.model_profile_config import ModelConfigV2Document, MainProfile
        from datetime import datetime, timezone
        values = dict(display_name="DeepSeek", provider_type="deepseek_official_responses", base_url="https://synthetic.test", model="deepseek-flash", api_key=KEY)
        empty = ModelConfigV2Document(revision=1, updated_at=datetime.now(timezone.utc))
        command = SaveProfileCommand(expected_revision=0, kind="main", profile_id="main-deepseek", preset_id="deepseek", key_action="replace", config=values)
        new = profile_connection(command, empty)
        self.assertEqual((new.context_length, new.max_output_tokens), (32768,1024))
        for capacity in (8192,16384,49152):
            saved = new.model_copy(update={"context_length":capacity})
            document = empty.model_copy(update={"main_profiles":(MainProfile(profile_id="main-deepseek",preset_id="deepseek",config=saved),)})
            self.assertEqual(profile_connection(command, document).context_length,capacity)
        # Explicit capacities on NEW local/custom/native profiles are not replaced
        # by the UI default. Ollama must send the chosen value, not always 32K.
        from app.model_config_runtime import build_main_provider
        from app.providers import ModelRequest, ModelMessage
        request = ModelRequest(messages=(ModelMessage(role="user", content="synthetic"),))
        for protocol in ("ollama", "openai_compatible", "gemini_native", "anthropic_native"):
            for capacity in (4096, 8192, 12000, 16384, 49152):
                explicit = {**values, "provider_type": protocol, "context_length": capacity}
                cmd = command.model_copy(update={"config": explicit})
                config = profile_connection(cmd, empty)
                self.assertEqual(config.context_length, capacity)
                if protocol == "ollama":
                    self.assertEqual(build_main_provider(config).budget_payload(request)["options"]["num_ctx"], capacity)

    def test_saved_8k_is_unchanged_and_explicit_capacity_still_requires_restart(self):
        values = {**main_values(), "context_length": 8192}
        self.save(values=values); self.activate(1)
        active = self.store.load()
        before = self.store.path.read_bytes()
        view = self.client.get("/v2/model-config").json()
        self.assertEqual(view["main_profiles"][0]["config"]["context_length"], 8192)
        self.assertEqual(self.store.path.read_bytes(), before)
        # An update omitting the capacity retains the old value, not the new default.
        self.assertEqual(self.save(2, values=main_values(), action="retain").status_code, 200)
        self.assertEqual(self.store.load().main_profiles[0].config.context_length, 8192)
        self.assertEqual(self.save(3, values={**values, "context_length": 32768}, action="retain").status_code, 200)
        self.assertEqual(self.store.load().main_model.context_length, 8192)
        self.activate(4)
        self.assertEqual(self.store.load().main_model.context_length, 32768)
        self.assertTrue(restart_required(active, self.store.load()))

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.store = ModelApiConfigStore(Path(self.directory.name) / "models.json")
        app = FastAPI()
        app.include_router(create_model_profile_router(self.store))
        self.client = TestClient(app)

    def save(self, revision=0, kind="main", profile="main-custom", values=None, action="replace"):
        config = dict(values if values is not None else main_values())
        if action == "replace":
            config["api_key"] = KEY
        return self.client.post("/v2/model-config/profile", json={
            "expected_revision": revision, "kind": kind, "profile_id": profile,
            "preset_id": "custom", "key_action": action, "config": config})

    def activate(self, revision, kind="main", profile="main-custom", enabled=False):
        return self.client.post("/v2/model-config/activate", json={
            "expected_revision": revision, "kind": kind, "profile_id": profile,
            "embedding_enabled": enabled})

    def test_missing_file_no_write_and_no_activation(self):
        view = self.client.get("/v2/model-config").json()
        self.assertEqual(0, view["revision"])
        self.assertFalse(view["configured"])
        self.assertIsNone(view["selected_to_activate"])
        self.assertFalse(self.store.path.exists())

    def combined(self, revision=0, enabled=True):
        def draft(kind, values):
            return {"expected_revision": revision, "kind": kind, "profile_id": kind + "-custom",
                    "preset_id": "custom", "key_action": "retain", "config": values}
        return {"expected_revision": revision, "main": draft("main", main_values()),
                "embedding": draft("embedding", embed_values()), "embedding_enabled": enabled}

    def test_combined_save_and_activate_single_revision_both_snapshots_no_runtime_mutation(self):
        response = self.client.post("/v2/model-config/save-and-activate", json=self.combined())
        self.assertEqual(200, response.status_code)
        view = response.json()
        self.assertEqual(1, view["revision"])
        self.assertEqual("chat-a", view["selected_to_activate"]["main_model"]["model"])
        self.assertEqual("vector-a", view["selected_to_activate"]["embedding"]["model"])
        self.assertIsNone(view["active_runtime"])
        self.assertTrue(view["restart_required"])

    def test_combined_invalid_embedding_or_revision_leaves_entire_file_unchanged(self):
        self.save()
        before = self.store.path.read_bytes()
        invalid = self.combined(1)
        invalid["embedding"]["config"]["timeout_seconds"] = -1.0
        self.assertEqual(422, self.client.post("/v2/model-config/save-and-activate", json=invalid).status_code)
        self.assertEqual(before, self.store.path.read_bytes())
        self.assertEqual(409, self.client.post("/v2/model-config/save-and-activate", json=self.combined()).status_code)
        self.assertEqual(before, self.store.path.read_bytes())

    def test_combined_retain_keys_and_disable_embedding_without_deleting_profile(self):
        self.save()
        self.save(1, "embedding", "embedding-custom", embed_values())
        response = self.client.post("/v2/model-config/save-and-activate", json=self.combined(2, False))
        self.assertEqual(200, response.status_code)
        doc = self.store.load()
        self.assertEqual(KEY, doc.main_profiles[0].config.api_key)
        self.assertEqual(KEY, doc.embedding_profiles[0].config.api_key)
        self.assertFalse(doc.embedding.enabled)
        self.assertEqual("vector-a", doc.embedding_profiles[0].config.model)
        self.assertNotIn(KEY, response.text)

    def test_combined_main_only_first_run_and_enabled_embedding_cannot_be_omitted(self):
        command = self.combined(enabled=False)
        command["embedding"] = None
        self.assertEqual(200, self.client.post("/v2/model-config/save-and-activate", json=command).status_code)
        before = self.store.path.read_bytes()
        command["expected_revision"] = command["main"]["expected_revision"] = 1
        command["embedding_enabled"] = True
        self.assertEqual(422, self.client.post("/v2/model-config/save-and-activate", json=command).status_code)
        self.assertEqual(before, self.store.path.read_bytes())

    def test_combined_atomic_write_failure_and_nested_revision_mismatch_preserve_file(self):
        self.save()
        before = self.store.path.read_bytes()
        with patch("app.model_api_config_store.os.replace", side_effect=OSError("blocked")):
            self.assertEqual(503, self.client.post("/v2/model-config/save-and-activate", json=self.combined(1)).status_code)
        self.assertEqual(before, self.store.path.read_bytes())
        command = self.combined(1)
        command["embedding"]["expected_revision"] = 0
        self.assertEqual(422, self.client.post("/v2/model-config/save-and-activate", json=command).status_code)
        self.assertEqual(before, self.store.path.read_bytes())

    def test_profile_keys_masked_independent_and_retain_replace_clear(self):
        response = self.save()
        self.assertEqual(200, response.status_code)
        self.assertNotIn(KEY, response.text)
        self.assertNotIn("api_key", response.text)
        self.assertTrue(response.json()["main_profiles"][0]["config"]["key_present"])
        response = self.save(1, "embedding", "embedding-custom", embed_values())
        self.assertEqual(200, response.status_code)
        response = self.save(2, values=main_values("chat-b"), action="retain")
        self.assertEqual(200, response.status_code)
        self.assertEqual(KEY, self.store.load().main_profiles[0].config.api_key)
        self.assertEqual("vector-a", self.store.load().embedding_profiles[0].config.model)
        response = self.save(3, values=main_values(), action="clear")
        self.assertEqual(200, response.status_code)
        self.assertIsNone(self.store.load().main_profiles[0].config.api_key)
        self.assertEqual(KEY, self.store.load().embedding_profiles[0].config.api_key)

    def reset_fixture(self):
        values = {**main_values("synthetic-deepseek"), "provider_type":"deepseek_official_responses",
                  "base_url":"https://api.deepseek.com", "context_length":16384, "api_key":"reset-main-synthetic"}
        response = self.client.post("/v2/model-config/profile", json=dict(expected_revision=0,
            kind="main", profile_id="main-deepseek", preset_id="deepseek", key_action="replace", config=values))
        self.assertEqual(response.status_code, 200)
        self.assertEqual(self.save(1, profile="main-other").status_code, 200)
        self.assertEqual(self.save(2, "embedding", "embedding-custom", embed_values()).status_code, 200)
        self.assertEqual(self.activate(3, profile="main-deepseek").status_code, 200)
        self.assertEqual(self.activate(4, "embedding", "embedding-custom", True).status_code, 200)
        return self.store.load()

    def test_reset_main_removes_profile_and_snapshot_key_preserves_other_credentials_and_restart_state(self):
        active = self.reset_fixture()
        before = self.store.path.read_bytes()
        app = FastAPI(); app.include_router(create_model_profile_router(self.store, active_document=active))
        client = TestClient(app)
        command = dict(expected_revision=5, profile_id="main-deepseek", key_action="clear")
        # Reads and drafting never mutate the file. A missing target cannot clear another Main.
        self.assertEqual(client.get("/v2/model-config").json()["revision"],5)
        self.assertEqual(client.post("/v2/model-config/reset-main", json={**command,"profile_id":"main-other"}).status_code,422)
        self.assertEqual(self.store.path.read_bytes(),before)
        response = client.post("/v2/model-config/reset-main", json=command)
        self.assertEqual(response.status_code,200)
        saved = self.store.load(); view = response.json()
        self.assertNotIn(b"reset-main-synthetic",self.store.path.read_bytes())
        self.assertEqual(saved.revision,6); self.assertIsNone(saved.activation)
        self.assertIsNone(saved.selected_main_profile_id);self.assertIsNone(saved.selected_embedding_profile_id)
        self.assertEqual([p.profile_id for p in saved.main_profiles],["main-other"])
        self.assertEqual(saved.main_profiles[0].config.api_key,KEY)
        self.assertEqual(saved.embedding_profiles,active.embedding_profiles)
        self.assertFalse(view["configured"]);self.assertIsNone(view["selected_to_activate"])
        self.assertTrue(view["restart_required"])
        self.assertEqual(view["active_runtime"]["main_model"]["model"],"synthetic-deepseek")
        self.assertTrue(view["active_runtime"]["main_model"]["key_present"])
        self.assertEqual(active.main_model.api_key,"reset-main-synthetic")
        self.assertNotIn("reset-main-synthetic",response.text)
        # A new startup snapshot is unconfigured; the deleted profile/key cannot return.
        app = FastAPI();app.include_router(create_model_profile_router(self.store,active_document=self.store.load()))
        fresh = TestClient(app).get("/v2/model-config").json()
        self.assertFalse(fresh["configured"]);self.assertIsNone(fresh["active_runtime"]);self.assertFalse(fresh["restart_required"])
        from app.model_profile_config import has_activation
        from app.provider_registry import build_model_provider
        self.assertFalse(has_activation(self.store.load()))
        self.assertEqual(build_model_provider({}).name,"mock")
        probe = dict(expected_revision=6, kind="main",profile_id="main-deepseek",preset_id="deepseek",
            key_action="retain", config={**main_values(),"provider_type":"deepseek_official_responses","base_url":"https://api.deepseek.com"},operation="discover")
        self.assertEqual(client.post("/v2/model-config/probe",json=probe).status_code,422)

    def test_reset_main_conflict_and_atomic_failure_leave_entire_configuration_unchanged(self):
        self.reset_fixture();before=self.store.path.read_bytes()
        command=dict(expected_revision=5,profile_id="main-deepseek",key_action="clear")
        self.assertEqual(self.client.post("/v2/model-config/reset-main",json={**command,"expected_revision":4}).status_code,409)
        with patch("app.model_api_config_store.os.replace",side_effect=OSError("synthetic atomic failure")):
            response=self.client.post("/v2/model-config/reset-main",json=command)
        self.assertEqual(response.status_code,503)
        self.assertEqual(self.store.path.read_bytes(),before)
        self.assertEqual(self.store.load().main_model.api_key,"reset-main-synthetic")
        # Ordinary DeepSeek save still rejects an empty key; it never becomes a runnable empty slot.
        empty={**main_values(),"provider_type":"deepseek_official_responses","base_url":"https://api.deepseek.com"}
        self.assertEqual(self.client.post("/v2/model-config/profile",json=dict(expected_revision=5,kind="main",
            profile_id="main-deepseek",preset_id="deepseek",key_action="clear",config=empty)).status_code,422)
        self.assertEqual(self.store.path.read_bytes(),before)

    def test_save_does_not_stage_and_snapshot_is_frozen_until_explicit_apply(self):
        self.save()
        saved = self.store.load()
        self.assertFalse(restart_required(None, saved))
        self.assertEqual(200, self.activate(1).status_code)
        active = self.store.load()
        provider = build_provider_from_model_config(active)
        self.assertEqual(200, self.save(2, values=main_values("chat-b"), action="retain").status_code)
        edited = self.store.load()
        self.assertEqual("chat-b", edited.main_profiles[0].config.model)
        self.assertEqual("chat-a", edited.main_model.model)
        self.assertEqual("chat-a", provider.model)
        self.assertFalse(restart_required(active, edited))
        self.assertEqual(200, self.activate(3).status_code)
        pending = self.store.load()
        self.assertTrue(restart_required(active, pending))
        self.assertEqual("chat-b", build_provider_from_model_config(pending).model)

    def test_main_apply_does_not_change_embedding_snapshot(self):
        self.save()
        self.save(1, "embedding", "embedding-custom", embed_values())
        self.activate(2)
        self.activate(3, "embedding", "embedding-custom", True)
        first = self.store.load()
        self.save(4, "embedding", "embedding-custom", {**embed_values(), "model": "vector-b"}, "retain")
        self.save(5, values=main_values("chat-b"), action="retain")
        self.activate(6)
        self.assertEqual(first.embedding, self.store.load().embedding)

    def test_v1_migrates_only_on_write_preserves_effective_connection_and_secrets(self):
        old = self.store.save(ModelApiConfig(main_model=MainModelConfig(**main_values(), api_key=KEY),
            embedding=EmbeddingConfig(enabled=True, provider_type="openai_compatible", reuse_main_api=True,
                                      model="legacy-vector", timeout_seconds=12.0)), expected_revision=0)
        before = self.store.path.read_bytes()
        view = self.client.get("/v2/model-config")
        self.assertEqual(before, self.store.path.read_bytes())
        self.assertNotIn(KEY, view.text)
        self.assertTrue(view.json()["migration_required"])
        response = self.save(1)
        self.assertEqual(200, response.status_code)
        migrated = self.store.load()
        self.assertEqual(2, migrated.schema_version)
        self.assertEqual(old.main_model, migrated.main_model)
        self.assertEqual(KEY, migrated.embedding.api_key)
        self.assertEqual(old.main_model.base_url, migrated.embedding.base_url)
        self.assertFalse(migrated.embedding.reuse_main_api)
        self.assertFalse(restart_required(old, migrated))
        self.assertEqual(2, len(migrated.main_profiles))
        with self.assertRaises(ModelConfigStoreError):
            self.store.save(ModelApiConfig(main_model=old.main_model, embedding=old.embedding), expected_revision=2)

    def test_disabled_v1_embedding_fields_are_not_discarded(self):
        embedding = EmbeddingConfig(enabled=False, base_url="https://archived.test/v1", api_key=KEY, model="archived")
        self.store.save(ModelApiConfig(main_model=MainModelConfig(**main_values()), embedding=embedding), expected_revision=0)
        self.assertEqual(200, self.save(1).status_code)
        self.assertEqual(embedding, self.store.load().embedding_profiles[0].config)

    def test_conflict_corruption_atomic_failure_and_future_schema_do_not_overwrite(self):
        self.save()
        before = self.store.path.read_bytes()
        self.assertEqual(409, self.save().status_code)
        self.assertEqual(before, self.store.path.read_bytes())
        with patch("app.model_api_config_store.os.replace", side_effect=OSError("blocked")):
            self.assertEqual(503, self.save(1, action="retain").status_code)
        self.assertEqual(before, self.store.path.read_bytes())
        self.store.path.write_text(json.dumps({"schema_version": 999, "api_key": KEY}), encoding="utf-8")
        before = self.store.path.read_bytes()
        response = self.save(1)
        self.assertEqual(500, response.status_code)
        self.assertNotIn(KEY, response.text)
        self.assertEqual(before, self.store.path.read_bytes())

    def test_invalid_unknown_fields_and_key_action_are_rejected(self):
        self.assertEqual(422, self.save(values={**main_values(), "not_supported": True}).status_code)
        values = {**main_values(), "api_key": "must-not-be-sent"}
        self.assertEqual(422, self.save(values=values, action="retain").status_code)
        self.assertFalse(self.store.path.exists())

    def test_masked_retained_key_probe_uses_unsaved_values_without_save(self):
        self.save()
        captured = []
        def handler(request):
            captured.append(request)
            return httpx.Response(200, json={"choices": [{"message": {"content": '{"ok":true}'}}]})
        app = FastAPI()
        app.include_router(create_model_profile_router(self.store, async_transport=httpx.MockTransport(handler)))
        client = TestClient(app)
        request = dict(expected_revision=1, kind="main", profile_id="main-custom", preset_id="custom",
                       key_action="retain", config=main_values("draft-id"), operation="connection")
        self.assertEqual(client.post("/v2/model-config/probe", json=request).status_code, 422)
        before = self.store.path.read_bytes()
        response = client.post("/v2/model-config/probe", json={**request,"confirm_paid_requests":True})
        self.assertTrue(response.json()["success"])
        self.assertEqual(before,self.store.path.read_bytes())
        self.assertEqual(captured[0].headers["Authorization"],"Bearer "+KEY)
        self.assertEqual(json.loads(captured[0].content)["model"],"draft-id")
        self.assertNotIn(KEY,response.text)

    def test_contract_probe_uses_draft_capacity_and_preserves_schema_validation(self):
        self.save(values={**main_values(), "context_length": 16384})
        before = self.store.path.read_bytes()
        calls = []
        def handler(request):
            calls.append(json.loads(request.content))
            return httpx.Response(200, json={"choices": [{"message": {"content": '{"unexpected":true}'}}]})
        app = FastAPI()
        app.include_router(create_model_profile_router(self.store, async_transport=httpx.MockTransport(handler)))
        client = TestClient(app)
        command = dict(expected_revision=1, kind="main", profile_id="main-custom", preset_id="custom",
            key_action="retain", config={**main_values("unsaved-draft"), "context_length":16384, "max_output_tokens":2048},
            operation="contracts", confirm_paid_requests=True)
        response = client.post("/v2/model-config/probe", json=command)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(len(calls), 3)
        self.assertTrue(all(c["model"] == "unsaved-draft" for c in calls))
        self.assertTrue(all(r["error_code"] == "MODEL_CONTRACT_RESPONSE_INVALID"
            for r in response.json()["results"] if r["status"] != "NOT_RUN"))
        self.assertEqual(self.store.path.read_bytes(), before)
        self.assertNotIn(KEY, response.text)

    def test_contract_probe_low_capacity_and_invalid_draft_do_not_call_provider(self):
        self.save()
        before = self.store.path.read_bytes()
        calls = []
        def handler(request):
            calls.append(request)
            raise AssertionError("Invalid/low-capacity drafts must not reach the provider")
        app = FastAPI()
        app.include_router(create_model_profile_router(self.store, async_transport=httpx.MockTransport(handler)))
        client = TestClient(app)
        command = dict(expected_revision=1, kind="main", profile_id="main-custom", preset_id="custom",
            key_action="retain", config={**main_values(), "context_length":8192, "max_output_tokens":2048},
            operation="contracts", confirm_paid_requests=True)
        response = client.post("/v2/model-config/probe", json=command)
        self.assertEqual(response.status_code, 200)
        self.assertTrue(all(r["error_code"] == "COGNITIVE_PROMPT_BUDGET_EXCEEDED"
            for r in response.json()["results"] if r["status"] != "NOT_RUN"))
        command["config"]["model"] = ""
        response = client.post("/v2/model-config/probe", json=command)
        self.assertEqual(response.status_code, 422)
        self.assertEqual(response.json()["error"]["code"], "MODEL_PROFILE_MODEL_REQUIRED")
        self.assertEqual(calls, [])
        self.assertEqual(self.store.path.read_bytes(), before)

    def test_v1_api_cannot_read_v2_credentials_or_replace_profiles(self):
        self.save()
        app = FastAPI(); app.include_router(create_model_api_config_router(self.store))
        response = TestClient(app).get("/v1/model-config")
        self.assertEqual(response.status_code,409)
        self.assertNotIn(KEY,response.text)

    def test_permission_failure_preserves_previous_document(self):
        self.save()
        before=self.store.path.read_bytes()
        with patch("app.model_api_config_store.restrict_config_file",side_effect=OSError("synthetic ACL failure")):
            response=self.save(1,action="retain")
        self.assertEqual(response.status_code,503)
        self.assertEqual(before,self.store.path.read_bytes())
