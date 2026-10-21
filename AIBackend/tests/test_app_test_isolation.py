import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from tests import app_test_isolation as guard


class AppTestIsolationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="embodiednpc-test-guard-")
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.environment = {"EMBODIEDNPC_TEST_ROOT": str(root), "EMBODIEDNPC_PROVIDER": "mock",
                            "CANONICAL_EMBEDDING_ENABLED": "false"}
        self.environment.update({key: str(root / (str(i) + ".test")) for i, key in enumerate(guard.DATA_PATHS)})

    def assert_rejected(self, environment):
        with patch.dict(os.environ, environment, clear=True), patch.object(guard, "_loaded_configuration", None), \
                patch.object(guard.importlib, "import_module") as load:
            with self.assertRaisesRegex(RuntimeError, "TEST_APP_ISOLATION_REQUIRED"):
                guard.import_isolated_app()
            load.assert_not_called()

    def test_unconfigured_and_unsafe_paths_reject_before_application_import(self):
        self.assert_rejected({})
        for override in (
            {"EMBODIEDNPC_MEMORY_DB": "relative.sqlite3"},
            {"EMBODIEDNPC_MEMORY_DB": str(Path(__file__).resolve().parents[1] / "data/embodiednpc_memory.sqlite3")},
            {"EMBODIEDNPC_MODEL_CONFIG_FILE": ""},
            {"EMBODIEDNPC_PROVIDER": "ollama"},
            {"CANONICAL_EMBEDDING_ENABLED": "true"},
            {"EMBODIEDNPC_TEST_ROOT": tempfile.gettempdir()},
        ):
            with self.subTest(override=override):
                self.assert_rejected({**self.environment, **override})

    def test_nonempty_root_and_preloaded_application_reject_before_import(self):
        with patch.dict(sys.modules, {"app.m3_main": types.ModuleType("app.m3_main")}):
            self.assert_rejected(self.environment)
        (Path(self.directory.name) / "existing-user-data").touch()
        self.assert_rejected(self.environment)

    def test_fresh_isolation_allows_import_but_cannot_rebind_loaded_singletons(self):
        fake = types.ModuleType("app.m3_main")
        with patch.dict(os.environ, self.environment, clear=True), patch.object(guard, "_loaded_configuration", None), \
                patch.object(guard.importlib, "import_module", return_value=fake) as load:
            self.assertIs(fake, guard.import_isolated_app())
            load.assert_called_once_with("app.m3_main")
            with patch.dict(sys.modules, {"app.m3_main": fake}):
                self.assertIs(fake, guard.import_isolated_app())
                os.environ["EMBODIEDNPC_MEMORY_DB"] = str(Path(self.directory.name) / "different.sqlite3")
                with self.assertRaisesRegex(RuntimeError, "TEST_APP_ISOLATION_REQUIRED"):
                    guard.import_isolated_app()
            self.assertEqual(1, load.call_count)
