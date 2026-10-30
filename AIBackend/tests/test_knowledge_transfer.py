"""Full-catalog HTTP transfer acceptance with private JSON/SQLite and fake encoders."""
from contextlib import closing
import asyncio
import copy
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from app.knowledge_embeddings import KnowledgeEmbeddingIndex, KnowledgeEmbeddingIndexer
from app.world_knowledge import KnowledgeAccess
from app.world_knowledge_store import KnowledgeStoreError, KNOWLEDGE_MAX_BYTES
from app.model_api_config_store import ModelConfigStoreError
from tests import test_knowledge_management_api as api
from tests.test_world_knowledge import TinyEmbeddings, cafe_request


class KnowledgeTransferTests(unittest.TestCase):
    setUp = api.KnowledgeManagementApiTests.setUp
    edit = api.KnowledgeManagementApiTests.edit

    def package(self, entries=None):
        current = self.service.store.snapshot()[1]
        return current.model_copy(update={"revision": 9876, "entries": current.entries if entries is None else entries}).model_dump(mode="json")

    def preview(self, package):
        return self.client.post(self.url + "/import/preview", json=package)

    def commit(self, package, preview):
        return self.client.post(self.url + "/import", json=package, headers={
            "X-Knowledge-Revision": str(preview["revision"]), "X-Knowledge-Preview": preview["preview_token"]})

    def test_export_is_full_and_roundtrips_all_fields_without_foreign_revision(self):
        original = self.service.store.snapshot()[2][0]
        public = original.model_copy(update={"knowledge_id": "custom.public", "related_ids": (),
            "keywords": ("alias",), "attributes": {"nested": [1, "value"]}, "usage": "constant",
            "access": KnowledgeAccess(mode="public", deployment_ids=("cafedemo-v2",))})
        self.service.store.manage("upsert", expected_revision=0, entry=public)
        response = self.client.get(self.url + "/export")
        self.assertEqual(response.status_code, 200)
        exported = response.json()
        self.assertEqual(exported, self.service.store.snapshot()[1].model_dump(mode="json"))
        self.assertEqual(exported["schema_version"], 2)
        self.assertNotIn("deleted_ids", exported)
        self.assertNotIn("items", exported)
        exported["revision"] = 9999
        before = self.service.store.path.read_bytes()
        preview = self.preview(exported).json()
        self.assertEqual(self.service.store.path.read_bytes(), before)
        self.assertEqual(len(preview["unchanged"]), len(exported["entries"]))
        self.assertFalse(preview["deleted"] or preview["added"] or preview["modified"])
        self.assertEqual(self.commit(exported, preview).status_code, 200)
        current = self.service.store.snapshot()[1]
        self.assertEqual(current.revision, 2)
        self.assertEqual(current.model_dump(mode="json")["entries"], exported["entries"])
        self.assertTrue(any(p.read_bytes() == before for p in self.service.store.backup_path.glob("before-replace-*.json")))

    def test_thirty_replace_one_hundred_and_deleted_defaults_never_reappear(self):
        current = self.service.store.snapshot()[1]
        original = current.entries[0]
        entries = tuple(original.model_copy(update={"knowledge_id": f"item.{i}", "related_ids": ()}) for i in range(100))
        self.service.store.replace(current.model_copy(update={"entries": entries}), expected_revision=0)
        package = self.package(entries[:30])
        preview = self.preview(package).json()
        self.assertEqual(len(preview["deleted"]), 70)
        self.assertEqual(self.commit(package, preview).status_code, 200)
        self.assertEqual(len(self.service.store.snapshot()[2]), 30)
        self.service.store.initialize()
        self.assertEqual(len(self.service.store.snapshot()[2]), 30)
        self.assertNotIn("coffee.latte", {e.knowledge_id for e in self.service.store.snapshot()[2]})

    def test_cross_entry_references_import_together_and_diff_lists_changes(self):
        old = self.service.store.snapshot()[2][0]
        first = old.model_copy(update={"knowledge_id": "custom.a", "related_ids": ("custom.b",)})
        second = old.model_copy(update={"knowledge_id": "custom.b", "related_ids": ("custom.a",)})
        changed = old.model_copy(update={"content": "changed", "related_ids": ()})
        package = self.package((first, second, changed))
        preview = self.preview(package).json()
        self.assertEqual(preview["added"], ["custom.a", "custom.b"])
        self.assertIn("content", preview["modified"][0]["fields"])
        self.assertEqual(self.commit(package, preview).status_code, 200)
        self.assertEqual({e.knowledge_id for e in self.service.store.snapshot()[2]}, {first.knowledge_id, second.knowledge_id, old.knowledge_id})

    def test_any_invalid_entry_or_format_rejects_the_whole_package(self):
        original = self.service.store.snapshot()[2][0].model_copy(update={"related_ids": ()})
        valid = self.package((original,))
        bad_values = []
        for field, value in (("schema_version", 1), ("schema_version", 99)):
            bad = copy.deepcopy(valid); bad[field] = value; bad_values.append(bad)
        bad = copy.deepcopy(valid); del bad["schema_version"]; bad_values.append(bad)
        bad = copy.deepcopy(valid); bad["entries"] *= 2; bad_values.append(bad)
        for field, value in (("content", " "), ("usage", "permanent"), ("related_ids", ["missing"]),
                             ("related_ids", ["invalid"]), ("access", {"mode":"restricted", "character_ids":[], "deployment_ids":["cafedemo-v2"]})):
            bad = copy.deepcopy(valid); bad["entries"].append({**bad["entries"][0], "knowledge_id": "invalid"})
            bad["entries"][1][field] = value; bad_values.append(bad)
        before = self.service.store.path.read_bytes()
        for case, bad in enumerate(bad_values):
            with self.subTest(case=case):
                self.assertEqual(self.preview(bad).status_code, 422)
                self.assertEqual(self.service.store.path.read_bytes(), before)
        raw = json.dumps(valid).replace('"schema_version": 2', '"schema_version": 2, "schema_version": 2')
        self.assertEqual(self.client.post(self.url+"/import/preview", content=raw, headers={"Content-Type":"application/json"}).status_code, 422)

    def test_unencodable_unicode_is_rejected_before_preview_or_writes(self):
        package = self.package()
        package["entries"][0]["content"] = "\ud800"
        before = self.service.store.path.read_bytes()
        response = self.client.post(self.url+"/import/preview", content=json.dumps(package).encode("ascii"),
            headers={"Content-Type":"application/json"})
        self.assertEqual(response.status_code, 422)
        self.assertEqual(self.service.store.path.read_bytes(), before)

    def test_preview_is_bound_to_revision_content_and_deployment(self):
        package = self.package()
        preview = self.preview(package).json()
        changed = copy.deepcopy(package); changed["entries"][0]["content"] = "swapped file"
        self.assertEqual(self.commit(changed, preview).status_code, 409)
        deployment = self.service.resolver.deployment
        self.service.resolver.deployment = deployment.model_copy(update={"scene_id": "changed"})
        self.assertEqual(self.commit(package, preview).status_code, 409)
        self.service.resolver.deployment = deployment
        self.service.store.manage("upsert", expected_revision=0, entry=self.service.store.snapshot()[2][0])
        before = self.service.store.path.read_bytes()
        self.assertEqual(self.commit(package, preview).status_code, 409)
        self.assertEqual(self.service.store.path.read_bytes(), before)
        self.assertEqual(self.client.post(self.url+"/import", json=package).status_code, 422)

    def test_scope_warnings_do_not_expand_permission_or_bind_unknown_npcs(self):
        old = self.service.store.snapshot()[2][0]
        private = old.model_copy(update={"related_ids": (), "access": KnowledgeAccess(
            character_ids=("unknown.character",), deployment_ids=("other.deployment",), scene_ids=("other.scene",)), "usage": "constant"})
        package = self.package((private,))
        preview = self.preview(package).json()
        self.assertEqual({w["code"] for w in preview["warnings"]},
            {"UNKNOWN_CHARACTER_IDS", "OTHER_DEPLOYMENT_IDS", "OTHER_SCENE_IDS", "NOT_APPLICABLE_HERE"})
        self.assertEqual(self.commit(package, preview).status_code, 200)
        stored = self.service.store.snapshot()[2][0]
        self.assertEqual(stored.access, private.access)
        result = self.service.resolver.resolve(cafe_request(old.name))
        self.assertFalse(result.items or result.constant_items)
        self.assertEqual(self.service.resolver.resolve(cafe_request().model_copy(update={"recall_identity": None})).status, "UNAUTHORIZED")
        # A regular body edit may explicitly preserve an imported, currently unbound scope.
        result = self.client.put(self.url+"/"+private.knowledge_id, json=self.edit(1, preserve_access=True))
        self.assertEqual(result.status_code, 200)
        self.assertEqual(self.service.store.snapshot()[2][0].access, private.access)

    def test_backup_and_atomic_publication_failures_keep_original(self):
        package = self.package(())
        preview = self.preview(package).json()
        before = self.service.store.path.read_bytes()
        with patch.object(self.service.store, "_backup", side_effect=KnowledgeStoreError("KNOWLEDGE_BACKUP_FAILED")):
            self.assertEqual(self.commit(package, preview).status_code, 422)
        self.assertEqual(self.service.store.path.read_bytes(), before)
        import app.world_knowledge_store as module
        write = module.atomic_json_write
        def fail_current(path, raw, **kwargs):
            if path == self.service.store.path:
                raise ModelConfigStoreError("MODEL_CONFIG_WRITE_FAILED")
            return write(path, raw, **kwargs)
        with patch.object(module, "atomic_json_write", side_effect=fail_current):
            self.assertEqual(self.commit(package, preview).status_code, 422)
        self.assertEqual(self.service.store.path.read_bytes(), before)

    def test_restore_all_uses_installed_template_backup_and_local_revision(self):
        current = self.service.store.snapshot()[1]
        self.service.store.replace(current.model_copy(update={"entries": ()}), expected_revision=0)
        before = self.service.store.path.read_bytes()
        response = self.client.post(self.url+"/restore-all", json={"expected_revision":1})
        self.assertEqual(response.status_code, 200)
        from app.world_knowledge_store import read_document
        from app.world_knowledge import KnowledgeBundle
        template = read_document(self.service.store.builtin_path, KnowledgeBundle)
        current = self.service.store.snapshot()[1]
        self.assertEqual(current.entries, tuple(sorted(template.entries, key=lambda e:e.knowledge_id)))
        self.assertEqual(current.bundle_version, template.bundle_version)
        self.assertEqual(current.revision, 2)
        self.assertTrue(any(p.read_bytes() == before for p in self.service.store.backup_path.glob("before-restore-*.json")))
        self.assertEqual(self.client.post(self.url+"/restore-all", json={"expected_revision":1}).status_code, 409)

    def test_transfer_notifies_existing_indexer_and_metadata_reuses_vectors(self):
        current = self.service.store.snapshot()[1]
        self.service.store.replace(current.model_copy(update={"entries": (current.entries[0].model_copy(update={"related_ids": ()}),)}), expected_revision=0)
        provider = TinyEmbeddings()
        index = KnowledgeEmbeddingIndex(self.service.store, Path(self.tmp.name)/"vectors.sqlite3")
        worker = KnowledgeEmbeddingIndexer(index, provider)
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 1)
        self.service.indexer = worker
        package = self.package()
        package["entries"][0]["usage"] = "constant"
        package["entries"][0]["access"] = dict(mode="public", character_ids=[], deployment_ids=["cafedemo-v2"], scene_ids=[])
        with patch.object(worker, "notify") as notify:
            preview = self.preview(package).json()
            notify.assert_not_called()
            self.assertEqual(self.commit(package, preview).status_code, 200)
            notify.assert_called_once()
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 0)
        package["entries"][0]["usage"] = "retrieval"
        self.assertEqual(self.commit(package, self.preview(package).json()).status_code, 200)
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 0)
        package["entries"][0]["content"] = "new semantic content"
        self.assertEqual(self.commit(package, self.preview(package).json()).status_code, 200)
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 1)
        self.assertEqual(len(provider.documents), 2)

    def test_import_request_uses_knowledge_limit_not_model_config_limit(self):
        package = self.package()
        raw = json.dumps(package).encode()
        # Whitespace remains valid JSON and pushes the request beyond the unrelated 256 KiB cap.
        padded = raw + b" " * (300*1024)
        response = self.client.post(self.url+"/import/preview", content=padded, headers={"Content-Type":"application/json"})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(self.client.post(self.url+"/import/preview", content=b" "*(KNOWLEDGE_MAX_BYTES+1),
            headers={"Content-Type":"application/json"}).status_code, 413)
