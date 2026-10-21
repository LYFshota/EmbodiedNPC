"""One-shot exit-code restart, retained owner lock; synthetic children, private data only."""
import contextlib
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("unified_c2", Path(__file__).parents[1] / "portable/unified.py")
entry = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(entry)


class Child:
    def __init__(self, pid, exit_code=0):
        self.pid, self.returncode, self.exit_code = pid, None, exit_code
    def poll(self): return self.returncode
    def wait(self, timeout=None):
        self.returncode = self.exit_code
        return self.returncode


class RestartTests(unittest.TestCase):
    def exercise(self, codes, *, stop_failure=False, spawn_failure_at=None, publication_failure=False, backend_failure=False):
        with tempfile.TemporaryDirectory(prefix="PortableC2-") as directory, contextlib.ExitStack() as stack:
            base = Path(directory)
            backend, root, player = base / "Backend", base / "UserData", base / "Player.exe"
            backend.mkdir()
            spawned, stopped = [], []
            def spawn(args, **options):
                # Guard never opens a gap between cycles, even after B1 releases its own lock.
                with entry.owner.DataLock(root / "runtime/unified.lock", create=False) as guard:
                    self.assertFalse(guard.acquire())
                if len(spawned) == spawn_failure_at: raise OSError("synthetic only")
                is_player = args[0] == str(player)
                code = codes[len(spawned)//2] if is_player else 0
                child = Child(100 + len(spawned), code)
                spawned.append((child, options))
                return child
            def stop(data, folder, state, timeout):
                self.assertEqual(root, data)
                self.assertEqual(backend, folder)
                self.assertEqual("a"*64, state["backend_instance"])
                self.assertIsNone(state["player"])
                self.assertIsNone(state["player_pending_pid"])
                stopped.append(dict(state))
                if stop_failure: raise entry.owner.PortableError("PORTABLE_STOP_TIMEOUT")
            def wait(process, game):
                game.wait()
                return backend_failure
            stack.enter_context(patch.object(entry.subprocess, "Popen", spawn))
            stack.enter_context(patch.object(entry, "owned_identity", lambda p: dict(pid=p.pid)))
            stack.enter_context(patch.object(entry, "backend_record", return_value=dict(instance_id="a"*64, state="listening")))
            stack.enter_context(patch.object(entry, "controlled_stop", stop))
            stack.enter_context(patch.object(entry, "wait_player", wait))
            if publication_failure: stack.enter_context(patch.object(entry, "publish_optional", return_value=False))
            try:
                result = entry.start(root, backend, player, 18962, 1, 1)
            except Exception as error:
                result = str(error)
            self.assertFalse(entry.record_path(root).exists() if not stop_failure else False)
            with entry.owner.DataLock(root / "runtime/unified.lock", create=False) as guard:
                self.assertTrue(guard.acquire())
            return result, spawned, stopped

    def test_explicit_restart_retains_lock_and_relaunches_once_same_root_port(self):
        result, spawned, stopped = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG, 0])
        self.assertEqual(0, result)
        self.assertEqual(4, len(spawned))
        self.assertEqual([0, 1], [s["restart_count"] for s in stopped])
        self.assertEqual("1", spawned[1][1]["env"]["EMBODIEDNPC_UNIFIED_RESTART"])
        self.assertEqual("0", spawned[3][1]["env"]["EMBODIEDNPC_UNIFIED_RESTART"])
        self.assertEqual(spawned[1][1]["env"]["EMBODIEDNPC_DATA_ROOT"], spawned[3][1]["env"]["EMBODIEDNPC_DATA_ROOT"])
        self.assertEqual(spawned[0][1]["env"], spawned[2][1]["env"])

    def test_normal_exit_does_not_restart(self):
        result, spawned, stopped = self.exercise([0])
        self.assertEqual((0, 2, 1), (result, len(spawned), len(stopped)))

    def test_crash_does_not_restart(self):
        result, spawned, _ = self.exercise([0xC0000005])
        self.assertEqual((1, 2), (result, len(spawned)))

    def test_restart_code_is_one_shot_even_for_misbehaving_player(self):
        result, spawned, stopped = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG]*2)
        self.assertEqual("UNIFIED_RESTART_LIMIT", result)
        self.assertEqual((4, 2), (len(spawned), len(stopped)))

    def test_stop_failure_never_launches_second_backend(self):
        result, spawned, _ = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG], stop_failure=True)
        self.assertEqual(("PORTABLE_STOP_TIMEOUT", 2), (result, len(spawned)))

    def test_second_backend_spawn_failure_stops_sequence(self):
        result, spawned, _ = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG], spawn_failure_at=2)
        self.assertEqual(("UNIFIED_BACKEND_START_FAILED", 2), (result, len(spawned)))

    def test_second_player_spawn_failure_stops_only_second_owned_backend(self):
        result, spawned, stopped = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG], spawn_failure_at=3)
        self.assertEqual(("UNIFIED_PLAYER_START_FAILED", 3, 2), (result, len(spawned), len(stopped)))

    def test_degraded_publication_or_backend_failure_does_not_restart(self):
        for failure in ({"publication_failure": True}, {"backend_failure": True}):
            with self.subTest(failure=failure):
                result, spawned, _ = self.exercise([entry.RESTART_TO_APPLY_AI_CONFIG], **failure)
                self.assertEqual(("UNIFIED_RESTART_UNCONFIRMED", 2), (result, len(spawned)))


if __name__ == "__main__": unittest.main()
