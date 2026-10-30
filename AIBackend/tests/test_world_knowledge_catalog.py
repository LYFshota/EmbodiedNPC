"""Complete-catalog safety checks. All files and subprocesses use isolated data."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

from app.model_api_config_store import atomic_json_write, ModelConfigStoreError
from app.world_knowledge import KnowledgeBundle
from app.world_knowledge_store import (KnowledgeOverrides, KnowledgeStoreError,
                                      WorldKnowledgeStore, KNOWLEDGE_MAX_BYTES, read_document)


class CurrentCatalogTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.template = self.root / "template.json"
        self.template.write_bytes((Path(__file__).parents[1] / "app/knowledge/cafedemo_v2.json").read_bytes())
        self.bundle = read_document(self.template, KnowledgeBundle)
        self.store = WorldKnowledgeStore(self.root / "current.json", self.template)

    def legacy(self):
        seed = self.bundle.entries[0]
        override = seed.model_copy(update={"content": "Local override"})
        custom = seed.model_copy(update={"knowledge_id": "custom.kept"})
        hidden = self.bundle.entries[1].knowledge_id
        old = KnowledgeOverrides(revision=8, entries=(override, custom), deleted_ids=(hidden,))
        raw = old.model_dump_json(indent=2).encode()
        self.store.path.write_bytes(raw)
        return old, raw, hidden

    def test_reads_never_initialize_or_migrate(self):
        with self.assertRaisesRegex(KnowledgeStoreError, "INITIALIZATION_REQUIRED"):
            self.store.snapshot()
        self.assertFalse(self.store.path.exists())
        _, raw, _ = self.legacy()
        with self.assertRaisesRegex(KnowledgeStoreError, "MIGRATION_REQUIRED"):
            self.store.snapshot()
        self.assertEqual(self.store.path.read_bytes(), raw)
        self.assertFalse(self.store.backup_path.exists())

    def test_migration_exact_effective_catalog_backups_and_idempotency(self):
        old, raw, hidden = self.legacy()
        template_raw = self.template.read_bytes()
        expected = {e.knowledge_id: e for e in self.bundle.entries}
        expected.update({e.knowledge_id: e for e in old.entries})
        expected.pop(hidden)
        current = self.store.initialize()
        self.assertEqual(current.schema_version, 2)
        self.assertEqual(current.revision, 8)
        self.assertEqual({e.knowledge_id: e for e in current.entries}, expected)
        self.assertEqual(self.store.snapshot()[0].bundle_version, self.bundle.bundle_version)
        backups = list(self.store.backup_path.glob("*.json"))
        self.assertEqual({p.read_bytes() for p in backups}, {raw, template_raw})
        committed = self.store.path.read_bytes()
        self.assertEqual(self.store.initialize(), current)
        self.assertEqual(self.store.path.read_bytes(), committed)
        self.assertEqual(len(list(self.store.backup_path.glob("*.json"))), 2)

    def test_template_update_corruption_and_absence_do_not_change_current(self):
        current = self.store.initialize()
        raw = self.store.path.read_bytes()
        for contents in (self.bundle.model_copy(update={"bundle_version": "new", "entries": ()}).model_dump_json(), "{bad"):
            self.template.write_text(contents, encoding="utf-8")
            self.assertEqual(self.store.snapshot()[2], current.entries)
            self.assertEqual(self.store.initialize(), current)
        self.template.unlink()
        self.assertEqual(self.store.snapshot()[2], current.entries)
        self.assertEqual(self.store.initialize(), current)
        self.assertEqual(self.store.path.read_bytes(), raw)

    def test_corrupt_existing_files_never_initialize(self):
        for raw in (b"{bad", b"null", b"[]", b'{"schema_version":99}', b'{"revision":0,"revision":1}'):
            self.store.path.write_bytes(raw)
            with self.assertRaises((KnowledgeStoreError, ValueError)):
                self.store.initialize()
            self.assertEqual(self.store.path.read_bytes(), raw)
        self.assertFalse(self.store.backup_path.exists())

    def test_backup_failure_keeps_legacy_and_retry_succeeds(self):
        _, raw, _ = self.legacy()
        with patch("app.world_knowledge_store.atomic_json_write", side_effect=ModelConfigStoreError("test")):
            with self.assertRaisesRegex(KnowledgeStoreError, "BACKUP_FAILED"):
                self.store.initialize()
        self.assertEqual(self.store.path.read_bytes(), raw)
        self.assertEqual(self.store.initialize().schema_version, 2)

    def test_interruption_keeps_old_current_and_retry_reuses_backups(self):
        _, raw, _ = self.legacy()
        real = os.replace
        def interrupted(source, target):
            if Path(target) == self.store.path:
                raise OSError("simulated interruption")
            return real(source, target)
        with patch("app.world_knowledge_store.os.replace", side_effect=interrupted):
            with self.assertRaisesRegex(KnowledgeStoreError, "WRITE_FAILED"):
                self.store.initialize()
        self.assertEqual(self.store.path.read_bytes(), raw)
        self.assertEqual(len(list(self.store.backup_path.glob("*.json"))), 2)
        self.store.initialize()
        self.assertEqual(len(list(self.store.backup_path.glob("*.json"))), 2)

    def test_missing_previously_initialized_file_requires_recovery(self):
        self.store.initialize()
        self.store.path.unlink()
        with self.assertRaisesRegex(KnowledgeStoreError, "CURRENT_FILE_MISSING"):
            self.store.initialize()
        self.assertFalse(self.store.path.exists())

    def test_delete_never_resurrects_restore_is_explicit_and_backed_up(self):
        self.store.initialize()
        key = self.bundle.entries[0].knowledge_id
        self.store.manage("delete", knowledge_id=key, expected_revision=0)
        self.store.initialize()
        self.assertNotIn(key, {e.knowledge_id for e in self.store.snapshot()[2]})
        raw = self.store.path.read_bytes()
        self.store.manage("restore", knowledge_id=key, expected_revision=1)
        self.assertIn(raw, [p.read_bytes() for p in self.store.backup_path.glob("before-restore-*.json")])
        self.assertEqual(self.store.snapshot()[1].revision, 2)
        self.assertIn(key, {e.knowledge_id for e in self.store.snapshot()[2]})

    def test_replace_never_merges_defaults_or_uses_import_revision(self):
        current = self.store.initialize()
        supplied = current.model_copy(update={"revision": 999, "entries": (current.entries[0],)})
        changed = self.store.replace(supplied, expected_revision=0)
        self.assertEqual(changed.revision, 1)
        self.assertEqual(self.store.snapshot()[2], supplied.entries)
        raw = self.store.path.read_bytes()
        with self.assertRaisesRegex(KnowledgeStoreError, "REVISION_CONFLICT"):
            self.store.replace(current, expected_revision=0)
        self.assertEqual(self.store.path.read_bytes(), raw)

    def test_restore_backup_failure_keeps_current(self):
        self.store.initialize()
        raw = self.store.path.read_bytes()
        with patch.object(self.store, "_backup", side_effect=KnowledgeStoreError("KNOWLEDGE_BACKUP_FAILED")):
            with self.assertRaisesRegex(KnowledgeStoreError, "BACKUP_FAILED"):
                self.store.manage("restore-all", expected_revision=0)
        self.assertEqual(self.store.path.read_bytes(), raw)

    def test_129_1000_3000_round_trip_and_model_config_limit_unchanged(self):
        current = self.store.initialize()
        for revision, count in enumerate((129, 1000, 3000)):
            entries = tuple(current.entries[0].model_copy(update={"knowledge_id": f"item.{i:05}", "content": "Knowledge " * 50}) for i in range(count))
            changed = self.store.replace(current.model_copy(update={"entries": entries}), expected_revision=revision)
            self.assertEqual(changed.entries, self.store.snapshot()[2])
            self.assertEqual(len(changed.entries), count)
        self.assertGreater(self.store.path.stat().st_size, 256 * 1024)
        self.assertLess(self.store.path.stat().st_size, KNOWLEDGE_MAX_BYTES)
        with self.assertRaises(ModelConfigStoreError):
            atomic_json_write(self.root / "model.json", b" " * (256 * 1024 + 1))
        self.assertFalse((self.root / "model.json").exists())

    def test_cross_process_writers_cannot_lose_updates(self):
        self.store.initialize()
        code = """import sys,time
from pathlib import Path
from app.world_knowledge_store import WorldKnowledgeStore,KnowledgeStoreError
s=WorldKnowledgeStore(Path(sys.argv[1]),Path(sys.argv[2]))
e=s.snapshot()[2][0].model_copy(update={'knowledge_id':sys.argv[3]})
Path(sys.argv[4]).write_text('ready')
while not Path(sys.argv[5]).exists(): time.sleep(.01)
try:
 s.manage('upsert',entry=e,expected_revision=0)
 print('OK')
except KnowledgeStoreError as error: print(str(error))
"""
        release = self.root / "release"
        processes = []
        try:
            for i in range(2):
                processes.append(subprocess.Popen([sys.executable, "-B", "-u", "-c", code, str(self.store.path), str(self.template), f"custom.process{i}", str(self.root / f"ready{i}"), str(release)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True))
            deadline = time.monotonic() + 15
            while not all((self.root / f"ready{i}").exists() for i in range(2)):
                if time.monotonic() > deadline: self.fail("child initialization timed out")
                time.sleep(.01)
            release.write_text("go")
            outputs = []
            for process in processes:
                out, err = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, err)
                outputs.append(out.strip())
            self.assertEqual(outputs.count("OK"), 1, outputs)
            self.assertTrue(any(v in {"KNOWLEDGE_STORE_BUSY", "KNOWLEDGE_REVISION_CONFLICT"} for v in outputs), outputs)
            current = self.store.snapshot()[1]
            self.assertEqual(current.revision, 1)
            self.assertEqual(len(current.entries), len(self.bundle.entries) + 1)
        finally:
            for process in processes:
                if process.poll() is None: process.kill()
                process.communicate()

    def test_cli_initialization_is_explicit_and_listing_is_read_only(self):
        command = [sys.executable, "-B", "-m", "tools.manage_world_knowledge"]
        env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
        before = subprocess.run([*command, "list", "--file", str(self.store.path)], capture_output=True, env=env, timeout=15)
        self.assertNotEqual(before.returncode, 0)
        self.assertFalse(self.store.path.exists())
        init = subprocess.run([*command, "initialize", "--file", str(self.store.path)], capture_output=True, env=env, timeout=15)
        self.assertEqual(init.returncode, 0, init.stderr)
        raw = self.store.path.read_bytes()
        listed = subprocess.run([*command, "list", "--file", str(self.store.path)], capture_output=True, env=env, timeout=15)
        self.assertEqual(listed.returncode, 0, listed.stderr)
        self.assertEqual(json.loads(listed.stdout)["schema_version"], 2)
        self.assertEqual(self.store.path.read_bytes(), raw)

    def test_real_backend_startup_initializes_isolated_catalog_and_preserves_corruption(self):
        code = """from fastapi.testclient import TestClient
from tests.app_test_isolation import get_test_app
from app.world_knowledge_store import KnowledgeStoreError
backend=get_test_app()
store=backend.knowledge_resolver.store
assert not store.path.exists()
with TestClient(backend.app):
 assert store.snapshot()[1].schema_version==2
store.path.write_bytes(b'{broken')
with TestClient(backend.app):
 try: store.snapshot()
 except KnowledgeStoreError: pass
 else: raise AssertionError('corruption accepted')
 assert store.path.read_bytes()==b'{broken'
print('STARTUP_OK')
"""
        env = {k: v for k, v in os.environ.items() if not k.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_"))}
        env["PYTHONIOENCODING"] = "utf-8"
        result = subprocess.run([sys.executable, "-B", "-c", code], capture_output=True, env=env, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        self.assertIn(b"STARTUP_OK", result.stdout)
