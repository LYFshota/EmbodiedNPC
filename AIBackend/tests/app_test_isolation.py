"""Fail closed before application imports in the guarded integration tests.

The caller must supply a fresh TemporaryDirectory and explicit paths; never silently
fall back to development data. Pure contract tests do not need this application import.
"""
import importlib
import os
from pathlib import Path
import sys
import tempfile
import atexit
from contextlib import contextmanager
from unittest.mock import patch


DATA_PATHS = (
    "EMBODIEDNPC_MEMORY_DB", "EMBODIEDNPC_SETTINGS_FILE", "EMBODIEDNPC_MODEL_CONFIG_FILE",
    "EMBODIEDNPC_KNOWLEDGE_FILE", "EMBODIEDNPC_KNOWLEDGE_INDEX_DB", "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",
)
_loaded_configuration = None
_managed_root = None
_managed_environment = None


def get_test_app():
    """Standalone unittest entry: create private Mock data before the first app import.

    An explicitly supplied runner configuration must still pass the original guard.
    Never adopt an already imported, unguarded production singleton.
    """
    global _managed_root, _managed_environment
    if _loaded_configuration is not None or os.environ.get("EMBODIEDNPC_TEST_ROOT"):
        return import_isolated_app()
    if "app.m3_main" in sys.modules or "app.main" in sys.modules:
        raise RuntimeError("TEST_APP_ALREADY_IMPORTED_WITHOUT_ISOLATION")
    _managed_root = tempfile.TemporaryDirectory(prefix="embodiednpc-unittest-")
    root = Path(_managed_root.name)
    environment = {k: v for k, v in os.environ.items() if not k.startswith((
        "EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "DEEPSEEK_", "OPENAI_",
        "ANTHROPIC_", "GEMINI_", "GOOGLE_", "HF_", "TRANSFORMERS_", "EPISODE_", "MEMORY_"))}
    names = ("memory.sqlite3", "settings.json", "models.json", "knowledge.json",
             "knowledge.sqlite3", "session.json")
    environment.update({key: str(root / name) for key, name in zip(DATA_PATHS, names)})
    environment.update(EMBODIEDNPC_TEST_ROOT=str(root), EMBODIEDNPC_PROVIDER="mock",
                       EMBODIEDNPC_CONTEXT_POLICY_FILE=str(root / "context.json"),
                       CANONICAL_EMBEDDING_ENABLED="false", HF_HUB_OFFLINE="1",
                       TRANSFORMERS_OFFLINE="1")
    _managed_environment = patch.dict(os.environ, environment, clear=True)
    _managed_environment.start()

    def cleanup():
        _managed_root.cleanup()
        _managed_environment.stop()
    atexit.register(cleanup)
    return import_isolated_app()


@contextmanager
def native_app_client(backend=None):
    """Real S01 session and middleware, in-process HTTP/WS, no lifespan/model workers."""
    from fastapi.testclient import TestClient
    from urllib.parse import urljoin

    backend = backend or get_test_app()
    # Recheck isolation even when the caller passes the module explicitly.
    if backend is not import_isolated_app():
        raise RuntimeError("TEST_APP_ISOLATION_MISMATCH")
    session = backend.knowledge_management.auth
    if session.token is not None:
        raise RuntimeError("TEST_SESSION_ALREADY_OWNED")
    base_url = "http://127.0.0.1:18947"

    class NativeClient(TestClient):
        def websocket_connect(self, url, *args, **kwargs):
            # Starlette otherwise resolves relative WS paths against ws://testserver.
            return super().websocket_connect(urljoin(base_url.replace("http", "ws", 1) + "/", url), *args, **kwargs)

    with patch.dict(os.environ, EMBODIEDNPC_KNOWLEDGE_ADMIN_URL=base_url):
        session.start()
        try:
            client = NativeClient(backend.app, base_url=base_url, client=("127.0.0.1", 19321),
                                  headers={"Authorization": "Bearer " + session.token})
            try:
                yield client
            finally:
                client.close()
        finally:
            session.stop()


def import_isolated_app():
    global _loaded_configuration
    def reject():
        raise RuntimeError("TEST_APP_ISOLATION_REQUIRED: use a fresh temporary test root, isolated data paths and Mock")

    raw_root = os.environ.get("EMBODIEDNPC_TEST_ROOT", "")
    if not raw_root or not Path(raw_root).is_absolute():
        reject()
    root = Path(raw_root).resolve()
    temporary = Path(tempfile.gettempdir()).resolve()
    repository = Path(__file__).resolve().parents[2]
    if (not root.is_dir() or root == temporary or not root.is_relative_to(temporary)
            or root.is_relative_to(repository) or os.environ.get("EMBODIEDNPC_DATA_ROOT")
            or os.environ.get("EMBODIEDNPC_PROVIDER") != "mock"
            or os.environ.get("CANONICAL_EMBEDDING_ENABLED") != "false"):
        reject()
    paths = []
    for key in DATA_PATHS:
        value = os.environ.get(key, "")
        if not value or not Path(value).is_absolute():
            reject()
        path = Path(value).resolve()
        if path == root or not path.is_relative_to(root):
            reject()
        paths.append(str(path))
    configuration = (str(root), *paths)
    if len(set(paths)) != len(paths):
        reject()
    if "app.m3_main" in sys.modules:
        if _loaded_configuration != configuration:
            reject()  # Environment changes cannot rebind an already initialized singleton.
        return sys.modules["app.m3_main"]
    if _loaded_configuration is not None or any(root.iterdir()):
        reject()
    module = importlib.import_module("app.m3_main")
    _loaded_configuration = configuration
    return module
