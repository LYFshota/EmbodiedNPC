"""Only orchestration boundaries; no app.main, provider inference or production processes."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).parents[1]))

SPEC = importlib.util.spec_from_file_location("portable_unified", Path(__file__).parents[1] / "portable/unified.py")
entry = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(entry)


class UnifiedLaunchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="UnifiedLaunch-")
        self.game = Path(self.temp.name) / "中文 game"
        self.backend = self.game / "Backend"
        self.root = self.game / "UserData"
        self.player = self.game / "UnityPlayer/EmbodiedNPC.exe"
        for name in ("python/python.exe", "launcher.py", "app/main.py", "app/builtin_embedding_resources.json"):
            path = self.backend / name; path.parent.mkdir(parents=True, exist_ok=True); path.touch()
        self.player.parent.mkdir(parents=True); self.player.touch()
        (self.player.parent / "EmbodiedNPC_Data").mkdir()
        (self.player.parent / "EmbodiedNPC_Data/globalgamemanagers").touch()
        (self.backend / "app/builtin_embedding_resources.json").write_text(json.dumps(dict(files={"fixture.onnx":"hash"})))
        self.model = self.backend / "models/embeddinggemma-300m-q4/fixture.onnx"
        self.model.parent.mkdir(parents=True); self.model.touch()

    def tearDown(self): self.temp.cleanup()

    def test_layout_is_package_relative_not_cwd_or_inherited_root(self):
        with patch.dict(os.environ, EMBODIEDNPC_DATA_ROOT="must-not-use"):
            game, root, player = entry.layout(self.backend)
        self.assertEqual((game, root, player), (self.game, self.root, self.player))
        self.assertFalse(root.exists())

    def test_missing_required_resources_does_not_create_userdata(self):
        self.player.unlink()
        with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_RESOURCE_MISSING"): entry.layout(self.backend)
        self.assertFalse(self.root.exists())

    def test_missing_embedding_files_fail_before_launch_or_data_write(self):
        self.model.unlink()
        with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_MODEL_RESOURCES_MISSING"): entry.layout(self.backend)
        self.assertFalse(self.root.exists())

    def test_child_environment_has_one_root_and_endpoint_no_inherited_credentials(self):
        with patch.dict(os.environ, EMBODIEDNPC_MEMORY_DB="wrong", EMBODIEDNPC_DATA_ROOT="wrong", OPENAI_API_KEY="synthetic-only"):
            values = entry.environment(self.root, 18991)
        self.assertEqual(values["EMBODIEDNPC_DATA_ROOT"], str(self.root))
        self.assertEqual(values["EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE"], str(self.root / "runtime/knowledge_admin_session.json"))
        self.assertEqual(values["EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"], "http://127.0.0.1:18991")
        self.assertNotIn("OPENAI_API_KEY", values); self.assertNotIn("EMBODIEDNPC_MEMORY_DB", values)

    def test_live_identity_uses_process_handle_creation_time_and_image(self):
        value = entry.identity(os.getpid())
        self.assertTrue(entry.alive(value))
        self.assertFalse(entry.alive({**value, "created": value["created"]-1}))
        self.assertFalse(entry.alive({**value, "image": "wrong.exe"}))

    def test_second_orchestrator_refused_with_os_lock_not_pid_file(self):
        (self.root / "runtime").mkdir(parents=True)
        with entry.owner.DataLock(self.root / "runtime/unified.lock", create=True) as lock:
            self.assertTrue(lock.acquire())
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_ALREADY_RUNNING"):
                entry.start(self.root, self.backend, self.player, 18991, 1, 1)

    def test_live_player_keeps_backend_and_does_not_send_stop(self):
        state=dict(schema_version=1, data_root=str(self.root), backend_directory=str(self.backend), player=entry.identity(os.getpid()))
        with patch.object(entry.owner, "stop") as stopping:
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_PLAYER_STILL_RUNNING"):
                entry.controlled_stop(self.root, self.backend, state, 1)
            stopping.assert_not_called()

    def test_wrong_data_root_stop_never_signals_backend(self):
        state=dict(schema_version=1, data_root="different", backend_directory=str(self.backend))
        with patch.object(entry.owner, "stop") as stopping:
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_OWNERSHIP_MISMATCH"):
                entry.controlled_stop(self.root, self.backend, state, 1)
            stopping.assert_not_called()

    def test_b1_expected_instance_is_checked_before_mailbox_write(self):
        (self.root / "runtime").mkdir(parents=True)
        lock_path, owner_path, mailbox = entry.owner.runtime_paths(self.root)
        owner_path.write_text(json.dumps(dict(schema_version=1, data_root=str(self.root), backend_directory=str(self.backend), instance_id="a"*64)))
        with entry.owner.DataLock(lock_path, create=True) as lock:
            self.assertTrue(lock.acquire())
            with self.assertRaisesRegex(entry.owner.PortableError, "PORTABLE_INSTANCE_CHANGED"):
                entry.owner.stop(self.root, self.backend, 1, expected_instance="b"*64)
        self.assertFalse(mailbox.exists())

    def test_stop_without_owned_record_does_not_create_or_adopt_data(self):
        entry.stop(self.root, self.backend, 1)
        self.assertFalse(self.root.exists())

    def test_assembly_uses_distinct_cmd_templates_and_whitelisted_model_files(self):
        spec = importlib.util.spec_from_file_location("portable_assemble", Path(__file__).parents[1] / "portable/assemble.py")
        assembler = importlib.util.module_from_spec(spec); spec.loader.exec_module(assembler)
        resources = json.loads((Path(__file__).parents[1] / "app/builtin_embedding_resources.json").read_text())
        models = Path(self.temp.name) / "models"
        for name in resources["files"]:
            path = models / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b"synthetic resource")
        (models / "do-not-copy-extra.json").write_text("synthetic forbidden extra file")
        destination = Path(self.temp.name) / "assembled"
        assembler.assemble(destination, self.backend / "python", models, self.player.parent)
        self.assertIn("unified.py", (destination / "Start.cmd").read_text())
        self.assertNotIn("unified.py", (destination / "Backend/start.cmd").read_text())
        self.assertFalse((destination / "UserData").exists())
        self.assertFalse((destination / "Backend/models/embeddinggemma-300m-q4/do-not-copy-extra.json").exists())

    def test_assembly_refuses_existing_destination_before_any_copy(self):
        spec = importlib.util.spec_from_file_location("portable_assemble", Path(__file__).parents[1] / "portable/assemble.py")
        assembler = importlib.util.module_from_spec(spec); spec.loader.exec_module(assembler)
        with self.assertRaisesRegex(ValueError, "new directory"):
            assembler.assemble(self.game, self.backend / "python", self.model.parent, self.player.parent)

    def test_failed_normal_stop_is_bounded_once_and_keeps_recovery_record(self):
        backend = SimpleNamespace(pid=111, poll=lambda: None)
        player = SimpleNamespace(pid=222, poll=lambda: 0, returncode=0)
        with patch.object(entry.subprocess, "Popen", side_effect=[backend, player]), \
             patch.object(entry, "owned_identity", return_value=None), \
             patch.object(entry, "backend_record", return_value={"instance_id":"x"*64,"state":"listening"}), \
             patch.object(entry, "controlled_stop", side_effect=entry.owner.PortableError("PORTABLE_STOP_TIMEOUT")) as stopping:
            with self.assertRaisesRegex(entry.owner.PortableError, "PORTABLE_STOP_TIMEOUT"):
                entry.start(self.root, self.backend, self.player, 18991, 1, 1)
        self.assertEqual(1, stopping.call_count)
        state = json.loads((self.root / "runtime/unified-instance.json").read_text())
        self.assertEqual("stopping", state["state"])


if __name__ == "__main__": unittest.main()
