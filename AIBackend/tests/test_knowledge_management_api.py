"""Focused native Developer Panel contract checks; no production app/database."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient
from app.knowledge_management_api import KnowledgeManagement, create_knowledge_management_router
from app.world_knowledge import KnowledgeDeployment
from app.world_knowledge_store import WorldKnowledgeStore, WorldKnowledgeResolver, read_document


class KnowledgeManagementApiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        store = WorldKnowledgeStore(Path(self.tmp.name) / "knowledge.json")
        store.initialize()
        deployment = read_document(Path(__file__).parents[1] / "app/knowledge/cafedemo_v2_deployment.json", KnowledgeDeployment)
        self.service = KnowledgeManagement(WorldKnowledgeResolver(store, deployment))
        self.service.auth.path = Path(self.tmp.name) / "session.json"
        with patch.dict("os.environ", EMBODIEDNPC_KNOWLEDGE_ADMIN_URL="http://127.0.0.1:18889"):
            self.service.auth.start()
        self.addCleanup(self.service.auth.stop)
        self.assertIsNotNone(self.service.auth.token)
        app = FastAPI()
        app.include_router(create_knowledge_management_router(self.service))
        self.client = TestClient(app, base_url="http://127.0.0.1:18889", client=("127.0.0.1", 12345))
        self.addCleanup(self.client.close)
        self.client.headers["Authorization"] = "Bearer " + self.service.auth.token
        self.url = "/v1/developer/knowledge"

    def edit(self, revision, **extra):
        return dict(expected_revision=revision, name="测试知识", content="测试专用知识内容。", **extra)

    def test_explicit_public_usage_and_legacy_edit_preservation(self):
        created = self.client.post(self.url, json=self.edit(0, access_mode="public", usage="constant")).json()
        key = created["selected_id"]
        def saved(result):
            return next(i["entry"] for i in result["items"] if i["entry"]["knowledge_id"] == key)
        self.assertEqual(saved(created)["access"]["mode"], "public")
        self.assertEqual(saved(created)["access"]["character_ids"], [])
        self.assertEqual(created["index"]["total"], len(created["items"]) - 1)
        legacy = self.client.put(self.url + "/" + key, json=self.edit(1)).json()
        self.assertEqual(saved(legacy)["access"]["mode"], "public")
        self.assertEqual(saved(legacy)["usage"], "constant")
        restricted = self.client.put(self.url + "/" + key, json=self.edit(2, access_mode="restricted",
            character_ids=["cafe.merchant"], usage="retrieval")).json()
        self.assertEqual(saved(restricted)["access"]["mode"], "restricted")
        self.assertEqual(saved(restricted)["usage"], "retrieval")
        self.assertEqual(self.client.post(self.url, json=self.edit(3, access_mode="public",
            character_ids=["cafe.merchant"])).status_code, 422)
        # The legacy all-current-characters command never becomes public.
        old = self.client.post(self.url, json=self.edit(3)).json()
        self.assertEqual(saved(old)["access"]["mode"], "restricted")

    def test_native_authorization_scope_and_invalid_input(self):
        self.assertEqual(self.client.get(self.url, headers={"Origin": "http://localhost"}).status_code, 403)
        self.assertEqual(self.client.get(self.url, headers={"Authorization": "Bearer wrong"}).status_code, 403)
        self.assertEqual(self.client.post(self.url, json=self.edit(0, access_mode="restricted", character_ids=["forged"])).status_code, 422)
        self.assertEqual(self.client.post(self.url, json=self.edit(0, access_mode="restricted", character_ids=["cafe.merchant"], scene_ids=["unknown"])).status_code, 422)
        self.assertEqual(self.client.post(self.url, json={**self.edit(0), "content": " "}).status_code, 422)
        self.assertEqual(self.service.store.snapshot()[1].revision, 0)

    def test_save_version_revision_conflict_delete_and_restore(self):
        created = self.client.post(self.url, json=self.edit(0)).json()
        key = created["selected_id"]
        self.assertTrue(key.startswith("custom."))
        self.assertEqual(created["revision"], 1)
        self.assertEqual(self.client.put(self.url + "/" + key, json=self.edit(0)).status_code, 409)
        updated = self.client.put(self.url + "/" + key, json=self.edit(1)).json()
        self.assertEqual(next(i["entry"]["version"] for i in updated["items"] if i["entry"]["knowledge_id"] == key), 2)
        deleted = self.client.request("DELETE", self.url + "/" + key, json={"expected_revision": 2}).json()
        self.assertNotIn(key, [i["entry"]["knowledge_id"] for i in deleted["items"]])
        # P4-B links cafe.background.name to cafe.setting: preserve the existing
        # reference guard, then exercise hide/restore on an unreferenced default.
        blocked = self.client.request("DELETE", self.url + "/cafe.setting", json={"expected_revision": 3})
        self.assertEqual(blocked.status_code, 409)
        self.assertEqual(blocked.json()["detail"], "KNOWLEDGE_REFERENCED_UNLINK_FIRST")
        self.assertEqual(self.client.get(self.url).json()["revision"], 3)
        default_id = "cafe.staff.taste_guidance"
        hidden_response = self.client.request("DELETE", self.url + "/" + default_id, json={"expected_revision": 3})
        self.assertEqual(hidden_response.status_code, 200)
        hidden = hidden_response.json()
        self.assertNotIn(default_id, [i["entry"]["knowledge_id"] for i in hidden["items"]])
        self.assertEqual(hidden["storage_mode"], "current")
        self.assertEqual(hidden["schema_version"], 2)
        restored_response = self.client.post(self.url + "/" + default_id + "/restore", json={"expected_revision": 4})
        self.assertEqual(restored_response.status_code, 200)
        restored = restored_response.json()
        self.assertFalse(next(i["hidden"] for i in restored["items"] if i["entry"]["knowledge_id"] == default_id))
        self.assertEqual(json.loads(self.service.store.path.read_text(encoding="utf-8"))["revision"], 5)

    def test_references_block_delete_and_indexer_is_not_duplicated(self):
        class Indexer:
            provider = object()
            def __init__(self): self.notifications = 0
            def notify(self): self.notifications += 1
        indexer = Indexer()
        self.service.indexer = indexer
        with patch.object(self.service, "index_status", return_value=dict(mode="running", ready=0, total=5)):
            result = self.client.post(self.url, json=self.edit(0, related_ids=["coffee.latte"])).json()
            self.assertEqual(indexer.notifications, 1)
            self.assertEqual(self.client.request("DELETE", self.url + "/coffee.latte", json={"expected_revision": 1}).status_code, 409)
            self.assertEqual(indexer.notifications, 1)
            self.assertEqual(self.client.get(self.url + "/status").json()["revision"], 1)
            references = next(i["referenced_by"] for i in result["items"] if i["entry"]["knowledge_id"] == "coffee.latte")
            self.assertIn(result["selected_id"], [r["knowledge_id"] for r in references])

    def test_get_never_migrates_and_valid_catalog_does_not_read_template(self):
        current = self.service.store.path.read_bytes()
        self.service.store.path.write_text('{"schema_version":1,"revision":8,"entries":[],"deleted_ids":[]}', encoding="utf-8")
        old = self.service.store.path.read_bytes()
        self.assertEqual(self.client.get(self.url).status_code, 503)
        self.assertEqual(self.client.get(self.url + "/status").status_code, 503)
        self.assertEqual(self.service.store.path.read_bytes(), old)
        self.service.store.path.write_bytes(current)
        with patch.object(self.service.store, "builtin_path", Path(self.tmp.name) / "missing-template.json"):
            response = self.client.get(self.url)
        self.assertEqual(response.status_code, 200)
        self.assertTrue(all(i["origin"] == "current" and not i["hidden"] for i in response.json()["items"]))
