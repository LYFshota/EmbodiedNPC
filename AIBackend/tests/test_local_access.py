"""Local capability boundary tests. No production app import, DB or real Provider."""
import asyncio
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from app.local_access import LocalBackendSession, LocalBackendAccessMiddleware, local_endpoint


class LocalAccessTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "knowledge_admin_session.json"
        self.env = patch.dict(os.environ, {
            "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE": str(self.path),
            "EMBODIEDNPC_KNOWLEDGE_ADMIN_URL": "http://127.0.0.1:18941"})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.session = LocalBackendSession(SimpleNamespace(path=self.path))
        self.session.start()
        self.addCleanup(self.session.stop)
        self.calls = 0

    def scope(self, *, path="/v2/model-config", method="GET", ws=False, headers=None):
        return dict(type="websocket" if ws else "http", scheme="ws" if ws else "http",
                    path=path, method=method, client=("127.0.0.1", 43210), server=("127.0.0.1", 18941),
                    headers=headers if headers is not None else self.headers())

    def headers(self):
        return [(b"host", b"127.0.0.1:18941"),
                (b"authorization", ("Bearer " + self.session.token).encode())]

    async def invoke(self, scope):
        sent = []
        async def receive():
            self.fail("Rejected requests must not read a body or WebSocket frame")
        async def handler(scope, receive, send):
            self.calls += 1
            if scope["type"] == "websocket":
                await send(dict(type="websocket.accept"))
            else:
                await send(dict(type="http.response.start", status=200, headers=[]))
                await send(dict(type="http.response.body", body=b"{}"))
        async def send(message):
            sent.append(message)
        await LocalBackendAccessMiddleware(handler, self.session)(scope, receive, send)
        return sent

    async def denied(self, scope):
        before = self.calls
        sent = await self.invoke(scope)
        self.assertEqual(self.calls, before)
        self.assertEqual(sent[0].get("status", sent[0].get("code")), 1008 if scope["type"] == "websocket" else 403)
        self.assertNotIn(self.session.token.encode(), repr(sent).encode())

    async def test_valid_reusable_http_ws_and_private_responses(self):
        results = await asyncio.gather(*(self.invoke(self.scope()) for _ in range(4)))
        self.assertTrue(all((b"cache-control", b"no-store") in r[0]["headers"] for r in results))
        for _ in range(4):
            self.assertEqual((await self.invoke(self.scope(ws=True, path="/ws")))[0]["type"], "websocket.accept")
        self.assertEqual(self.calls, 8)

    async def test_absent_wrong_stale_long_duplicate_or_malformed_bearer(self):
        variants = [[], [(b"authorization", b"Bearer wrong")],
                    [(b"authorization", b"Bearer " + b"x" * 43)],
                    [(b"authorization", b"Bearer " + b"x" * 10000)],
                    self.headers()[1:] * 2, [(b"authorization", b"Bearer " + b"x" * 32 + b"\r\n")],
                    [(b"authorization", b"bearer " + self.session.token.encode())]]
        for auth in variants:
            for ws in (False, True):
                await self.denied(self.scope(ws=ws, headers=self.headers()[:1] + auth))

    async def test_all_origins_and_forwarded_headers_are_rejected(self):
        for key, value in [(b"origin", b""), (b"origin", b"null"), (b"origin", b"http://localhost"),
                           (b"origin", b"https://evil.test"), (b"forwarded", b"for=127.0.0.1"),
                           (b"x-forwarded-for", b"127.0.0.1"), (b"x-forwarded-host", b"127.0.0.1:18941")]:
            for ws in (False, True):
                await self.denied(self.scope(ws=ws, headers=self.headers() + [(key, value)]))
        await self.denied(self.scope(headers=self.headers() + [(b"origin", b""), (b"origin", b"null")]))

    async def test_peer_host_port_scheme_and_alias_are_bound(self):
        for host in (b"evil.test:18941", b"localhost:18941", b"127.0.0.2:18941", b"127.1:18941",
                     b"127.0.0.1.:18941", b"127.0.0.1.evil.test:18941", b"127.0.0.1:8000",
                     b"user@127.0.0.1:18941", b"127.0.0.1:18941/", b"127.0.0.1:18941,evil.test",
                     b"127.0.0.1:18941?", b"127.0.0.1:0"):
            await self.denied(self.scope(headers=[(b"host", host)] + self.headers()[1:]))
        await self.denied(self.scope(headers=self.headers() + self.headers()[:1]))
        for peer in ("192.168.1.10", "testclient", "evil.test"):
            await self.denied({**self.scope(), "client": (peer, 1234)})
        await self.denied({**self.scope(), "server": ("127.0.0.1", 8000)})
        await self.denied({**self.scope(), "scheme": "https"})

    async def test_public_only_get_and_normalized_sensitive_paths(self):
        for path in ("/live", "/v1/context-policy", "/v1/settings", "/docs", "/openapi.json", "/redoc", "/docs/oauth2-redirect"):
            self.assertEqual((await self.invoke(self.scope(path=path, headers=self.headers()[:1])))[0]["status"], 200)
            await self.denied(self.scope(path=path, method="POST", headers=self.headers()[:1]))
        for path in ("/v2/model-config/", "/v2/model-config", "/health/", "/unknown/new-route", "/ws/"):
            for method in ("GET", "HEAD", "POST", "OPTIONS"):
                await self.denied(self.scope(path=path, method=method, headers=self.headers()[:1]))

    async def test_rotation_old_invalid_new_reusable_and_owned_cleanup(self):
        old = self.headers()
        self.session.stop()
        self.assertFalse(self.path.exists())
        self.session.start()
        self.assertNotEqual(old, self.headers())
        await self.denied(self.scope(headers=old))
        self.assertEqual((await self.invoke(self.scope()))[0]["status"], 200)
        replacement = json.loads(self.path.read_text())
        replacement["token"] = "other-owner-" + "x" * 32
        self.path.write_text(json.dumps(replacement))
        self.session.stop()
        self.assertTrue(self.path.exists(), "Late cleanup must not delete another owner's record")

    def test_same_path_lock_and_failure_do_not_replace_owner(self):
        before = self.path.read_bytes()
        second = LocalBackendSession(SimpleNamespace(path=self.path))
        with self.assertRaisesRegex(RuntimeError, "LOCAL_BACKEND_SESSION_START_FAILED"):
            second.start()
        self.assertEqual(before, self.path.read_bytes())
        self.assertIsNone(second.token)

    def test_crashed_subprocess_releases_os_lock_and_stale_file_is_replaced(self):
        self.session.stop()
        code = ("from app.local_access import LocalBackendSession; from types import SimpleNamespace; "
                "from pathlib import Path; import sys,os; s=LocalBackendSession(SimpleNamespace(path=Path(sys.argv[1]))); "
                "s.start(); print('READY',flush=True); sys.stdin.readline(); os._exit(0)")
        child = subprocess.Popen([sys.executable, "-B", "-c", code, str(self.path)],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            self.assertEqual(child.stdout.readline().strip(), "READY")
            before = self.path.read_bytes()
            with self.assertRaisesRegex(RuntimeError, "LOCAL_BACKEND_SESSION_START_FAILED"):
                self.session.start()
            self.assertEqual(before, self.path.read_bytes())
            child.communicate("crash\n", timeout=10)
            self.assertEqual(child.returncode, 0)
            self.assertTrue(self.path.exists())
            self.session.start()
            self.assertNotEqual(before, self.path.read_bytes())
        finally:
            if child.poll() is None:
                child.kill()
                child.communicate()

    def test_publication_acl_failure_fails_closed_and_releases_lock(self):
        self.session.stop()
        with patch("app.model_api_config_store.restrict_config_file", side_effect=OSError("synthetic ACL failure")):
            with self.assertRaisesRegex(RuntimeError, "LOCAL_BACKEND_SESSION_START_FAILED"):
                self.session.start()
        self.assertIsNone(self.session.token)
        self.assertFalse(self.path.exists())
        self.session.start()
        self.assertTrue(self.path.exists())

    def test_invalid_endpoint_fails_closed(self):
        for url in ("http://evil.test:18941", "http://127.1:18941", "http://localhost.:18941",
                    "http://127.0.0.1:18941?token=x", "http://127.0.0.1:18941/path", "http://127.0.0.1:"):
            with self.assertRaises(ValueError):
                local_endpoint(url)


if __name__ == "__main__":
    unittest.main()
