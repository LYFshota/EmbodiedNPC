"""C1 ownership recovery only. No app.main, real providers, Unity assets or production data."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
SPEC = importlib.util.spec_from_file_location("unified_c1", Path(__file__).parents[1] / "portable/unified.py")
entry = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(entry)


class Child:
    def __init__(self, pid, code=None, on_wait=None):
        self.pid, self.returncode, self.on_wait = pid, code, on_wait
    def poll(self): return self.returncode
    def wait(self, timeout=None):
        if self.on_wait: self.on_wait()
        self.returncode = 0
        return 0


class C1RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="PortableC1-")
        self.addCleanup(self.temp.cleanup)
        self.game = Path(self.temp.name)
        self.backend, self.root = self.game / "Backend", self.game / "UserData"
        self.backend.mkdir()
        self.backend_process, self.player_process = Child(101), Child(202)
        self.identity = lambda child: dict(pid=child.pid, created=child.pid*10, image=str(self.backend / "synthetic.exe"))

    def exercise(self, **changes):
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(entry.subprocess, "Popen", side_effect=[self.backend_process, self.player_process]))
            stack.enter_context(patch.object(entry, "owned_identity", changes.pop("identity", self.identity)))
            stack.enter_context(patch.object(entry, "backend_record", return_value=dict(instance_id="a"*64, state="listening")))
            stopping = stack.enter_context(patch.object(entry, "controlled_stop"))
            for name, value in changes.items(): stack.enter_context(patch.object(entry, name, value))
            result = entry.start(self.root, self.backend, self.game / "Player.exe", 18993, 1, 1)
            stopping.assert_called_once()
            self.assertIsNone(stopping.call_args.args[2]["player"])
            self.assertEqual("a"*64, stopping.call_args.args[2]["backend_instance"])
            return result

    def test_temporary_real_metadata_failure_is_retried_not_ignored(self):
        (self.root / "runtime").mkdir(parents=True)
        path = self.root / "runtime/record.json"
        real = entry.owner.private_record
        attempts = []
        def once(path, value):
            attempts.append(1)
            if len(attempts) == 1:
                error = PermissionError("synthetic-sensitive-message")
                error.winerror = 5
                raise error
            return real(path, value)
        with patch.object(entry.owner, "private_record", side_effect=once):
            entry.publish(path, dict(state="running", marker=1))
        self.assertEqual(2, len(attempts))
        self.assertEqual(1, json.loads(path.read_text())["marker"])

    def test_permanent_publish_failure_retains_supervision_and_os_lock(self):
        checked = []
        def while_live():
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_STILL_SUPERVISING"):
                entry.stop(self.root, self.backend, 1)
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_ALREADY_RUNNING"):
                entry.start(self.root, self.backend, self.game / "Player.exe", 18994, 1, 1)
            checked.append(True)
        self.player_process.on_wait = while_live
        real = entry.publish
        def publish(path, state):
            if state.get("player_pending_pid") or state["state"] == "running":
                raise RuntimeError("synthetic-sensitive-message")
            return real(path, state)
        result = self.exercise(publish=publish)
        self.assertEqual([True], checked)
        self.assertEqual(1, result)  # Degraded publication, but children drained and stopped normally.
        self.assertFalse(entry.record_path(self.root).exists())

    def test_identity_failure_keeps_actual_child_and_stops_after_exit(self):
        def unavailable(child):
            if child is self.player_process: raise entry.LaunchError("UNIFIED_PROCESS_IDENTITY_UNAVAILABLE")
            return self.identity(child)
        self.assertEqual(0, self.exercise(identity=unavailable))

    def test_transient_identity_error_retries_same_child(self):
        attempts = []
        def transient(child):
            if child is self.player_process:
                attempts.append(child)
                if len(attempts) == 1:
                    error = entry.LaunchError("UNIFIED_PROCESS_IDENTITY_UNAVAILABLE")
                    error.winerror = 87
                    raise error
            return self.identity(child)
        self.assertEqual(0, self.exercise(identity=transient))
        self.assertEqual([self.player_process]*2, attempts)

    def test_backend_identity_query_failure_still_uses_fresh_b1_capability(self):
        def unavailable(child):
            if child is self.backend_process: raise entry.LaunchError("UNIFIED_PROCESS_IDENTITY_UNAVAILABLE")
            return self.identity(child)
        self.assertEqual(0, self.exercise(identity=unavailable))

    def test_player_fast_exit_does_not_claim_live_identity(self):
        self.player_process.returncode = 0
        def identity(child): return None if child is self.player_process else self.identity(child)
        self.assertEqual(0, self.exercise(identity=identity))

    def test_backend_early_exit_keeps_player_until_normal_close(self):
        self.backend_process.returncode = 1
        calls = []
        def poll():
            calls.append(1)
            return None if len(calls) == 1 else 1
        self.backend_process.poll = poll
        self.assertEqual(1, self.exercise())

    def test_keyboard_interrupt_after_spawn_does_not_release_guard(self):
        calls = []
        def wait(timeout=None):
            calls.append(1)
            if len(calls) == 1: raise KeyboardInterrupt
            with entry.owner.DataLock(self.root / "runtime/unified.lock", create=False) as lock:
                self.assertFalse(lock.acquire())
            self.player_process.returncode = 0
        self.player_process.wait = wait
        self.assertEqual(0, self.exercise())
        self.assertEqual(2, len(calls))

    def test_incomplete_old_launch_record_is_fail_closed(self):
        (self.root / "runtime").mkdir(parents=True)
        entry.owner.private_record(entry.record_path(self.root), dict(state="launching_player", schema_version=1,
            data_root=str(self.root), backend_directory=str(self.backend), player=None))
        with patch.object(entry.owner, "stop") as stopping:
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_PLAYER_IDENTITY_UNCONFIRMED"):
                entry.stop(self.root, self.backend, 1)
            stopping.assert_not_called()
        with patch.object(entry.subprocess, "Popen") as spawning:
            with self.assertRaisesRegex(entry.LaunchError, "UNIFIED_PLAYER_IDENTITY_UNCONFIRMED"):
                entry.start(self.root, self.backend, self.game / "Player.exe", 18993, 1, 1)
            spawning.assert_not_called()

    def test_stale_complete_owner_does_not_authorize_adopting_old_instance(self):
        (self.root / "runtime").mkdir(parents=True)
        entry.owner.private_record(entry.record_path(self.root), dict(state="stopping", player=None,
            schema_version=1, data_root=str(self.root), backend_directory=str(self.backend), backend_instance="b"*64))
        self.assertEqual(0, self.exercise())
        self.assertFalse(entry.record_path(self.root).exists())

    def test_diagnostics_exclude_exception_messages_and_locals(self):
        output = io.StringIO()
        try: raise ValueError("synthetic-secret-never-log")
        except ValueError as error:
            with contextlib.redirect_stderr(output): entry.diagnostic("test", error)
        self.assertNotIn("synthetic-secret-never-log", output.getvalue())
        self.assertIn("ValueError", output.getvalue())
        self.assertIn("function", output.getvalue())

    def test_non_transient_error_is_not_retried(self):
        calls = []
        def invalid():
            calls.append(1)
            raise ValueError("synthetic-secret-never-log")
        with self.assertRaises(ValueError): entry.retry_metadata("invalid", invalid)
        self.assertEqual(1, len(calls))

    def test_retry_deadline_is_bounded(self):
        calls = []
        def denied():
            calls.append(1)
            error = PermissionError("synthetic denied")
            error.winerror = 5
            raise error
        with patch.object(entry.time, "monotonic", side_effect=[0, 0, .02]), patch.object(entry.time, "sleep") as sleeping:
            with self.assertRaises(PermissionError): entry.retry_metadata("bounded", denied, timeout=.01)
        self.assertEqual(2, len(calls))
        sleeping.assert_called_once()

    def test_unexpected_later_error_drains_original_children_before_reraising(self):
        wait = entry.wait_player
        calls = []
        def failure_once(process, game):
            self.assertIs(process, self.backend_process)
            self.assertIs(game, self.player_process)
            calls.append(1)
            if len(calls) == 1: raise ValueError("synthetic-secret-never-log")
            return wait(process, game)
        with patch.object(entry.subprocess, "Popen", side_effect=[self.backend_process, self.player_process]) as spawning, \
             patch.object(entry, "owned_identity", self.identity), \
             patch.object(entry, "backend_record", return_value=dict(instance_id="a"*64, state="listening")), \
             patch.object(entry, "wait_player", failure_once), patch.object(entry, "controlled_stop") as stopping:
            with self.assertRaises(ValueError): entry.start(self.root, self.backend, self.game / "Player.exe", 18993, 1, 1)
        self.assertEqual(2, spawning.call_count)
        stopping.assert_called_once()
        self.assertEqual(0, self.player_process.returncode)
        self.assertFalse(entry.record_path(self.root).exists())

    @unittest.skipUnless(os.name == "nt", "Windows retained handle")
    def test_real_popen_handle_identity_before_and_after_exit(self):
        child = subprocess.Popen([sys.executable, "-B", "-c", "import sys;sys.stdin.read()"], stdin=subprocess.PIPE)
        try:
            with patch.object(entry, "identity", side_effect=AssertionError("must not reopen PID")):
                value = entry.owned_identity(child)
            self.assertEqual(child.pid, value["pid"])
            self.assertEqual(Path(sys.executable).resolve(), Path(value["image"]))
        finally:
            child.stdin.close()
            child.wait(timeout=10)
        self.assertIsNone(entry.owned_identity(child))


if __name__ == "__main__": unittest.main()
