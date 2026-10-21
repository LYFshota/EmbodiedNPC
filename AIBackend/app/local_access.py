"""One process capability for native local HTTP/WS callers, not NPC/world authority.

The private file is shared with the existing Knowledge client. Same-user processes
that can read it are inside this trust boundary; this is not executable identity.
"""

from datetime import datetime, timezone
import hmac
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
from urllib.parse import urlsplit

from fastapi import HTTPException, Request
from starlette.responses import JSONResponse

from .model_api_config_store import atomic_json_write


PUBLIC_GET_PATHS = frozenset({
    "/live", "/v1/settings", "/v1/context-policy", "/openapi.json",
    "/docs", "/docs/oauth2-redirect", "/redoc",
})
# Explicit inventory for regression tests. Unknown routes still require a token.
SENSITIVE_ROUTES = frozenset({
    ("GET", "/health"), ("GET", "/v1/runtime-status"),
    ("GET", "/v1/model-config"), ("PUT", "/v1/model-config"),
    ("POST", "/v1/model-config/discover-models"), ("POST", "/v1/model-config/test"),
    ("GET", "/v2/model-config"),
    *(("POST", "/v2/model-config/" + name) for name in
      ("profile", "activate", "save-and-activate", "reset-main", "probe")),
    ("GET", "/v1/developer/knowledge"), ("POST", "/v1/developer/knowledge"),
    ("GET", "/v1/developer/knowledge/status"),
    *((method, "/v1/developer/knowledge/{knowledge_id}") for method in ("GET", "PUT", "DELETE")),
    ("POST", "/v1/developer/knowledge/{knowledge_id}/restore"),
    ("GET", "/v1/embedding-index/status"), ("POST", "/v1/embedding-index/control"),
    ("PUT", "/v1/settings"), ("PUT", "/v1/context-policy"),
    ("POST", "/v1/context-budget/recheck"), ("GET", "/v1/dialogue-history"),
    ("POST", "/v1/task-failure-response"),
})


def local_endpoint(value):
    """No DNS resolution or alias equivalence; a capability binds one authority."""
    parsed = urlsplit(value)
    if (parsed.scheme not in {"http", "https"} or
            parsed.hostname not in {"127.0.0.1", "localhost", "::1"} or
            parsed.username is not None or parsed.password is not None or
            parsed.path not in {"", "/"} or parsed.query or parsed.fragment or
            any(c.isspace() for c in value)):
        raise ValueError("LOCAL_BACKEND_URL_INVALID")
    port = parsed.port or (443 if parsed.scheme == "https" else 80)
    host = "[::1]" if parsed.hostname == "::1" else parsed.hostname
    # Reject empty port, trailing delimiters and non-canonical authority syntax.
    if parsed.netloc.lower() not in {host, f"{host}:{port}"}:
        raise ValueError("LOCAL_BACKEND_URL_INVALID")
    return parsed.scheme, parsed.hostname, port


class _PublicationLock:
    """OS ownership, not the existence of a stale lock file, serializes publication."""
    def __init__(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = path.open("a+b")
        self.held = False
        try:
            if os.fstat(self.stream.fileno()).st_size == 0:
                self.stream.write(b"\0")
                self.stream.flush()
            self.stream.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.held = True
        except BaseException:
            self.stream.close()
            raise

    def close(self):
        try:
            if self.held:
                self.stream.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(self.stream.fileno(), fcntl.LOCK_UN)
        finally:
            self.held = False
            self.stream.close()


class LocalBackendSession:
    def __init__(self, store):
        self.path = Path(os.getenv("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",
                                   str(store.path.with_name("knowledge_admin_session.json"))))
        self.token = None
        self.endpoint = None
        self._lock = None

    def start(self):
        if self._lock is not None:
            raise RuntimeError("LOCAL_BACKEND_SESSION_ALREADY_STARTED")
        try:
            url = os.getenv("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL", "http://127.0.0.1:8000").rstrip("/")
            endpoint = local_endpoint(url)
            self._lock = _PublicationLock(self.path.with_suffix(".lock"))
            token = secrets.token_urlsafe(32)
            # atomic_json_write restricts the temp file before writing any secret.
            atomic_json_write(self.path, json.dumps(dict(schema_version=1, base_url=url,
                token=token, pid=os.getpid(), created_at=datetime.now(timezone.utc).isoformat())).encode())
            self.endpoint, self.token = endpoint, token
        except Exception:
            self.stop()
            raise RuntimeError("LOCAL_BACKEND_SESSION_START_FAILED") from None

    def invalidate(self):
        # Keep the ownership token privately until file cleanup; reject new calls now.
        self.endpoint = None

    def stop(self):
        self.invalidate()
        token, self.token = self.token, None
        try:
            if token is not None:
                try:
                    value = json.loads(self.path.read_text(encoding="utf-8"))
                    supplied = value.get("token")
                    if (value.get("pid") == os.getpid() and isinstance(supplied, str) and
                            hmac.compare_digest(supplied.encode(), token.encode())):
                        self.path.unlink()
                except (OSError, ValueError, TypeError, AttributeError):
                    pass
        finally:
            lock, self._lock = self._lock, None
            if lock is not None:
                lock.close()

    def allows(self, scope, *, credential=True):
        if self.endpoint is None or self.token is None:
            return False
        try:
            if not ipaddress.ip_address(scope["client"][0]).is_loopback:
                return False
            headers = [(k.lower(), v) for k, v in scope.get("headers", ())]
            if any(k == b"origin" or k == b"forwarded" or k.startswith(b"x-forwarded-") for k, _ in headers):
                return False
            hosts = [v for k, v in headers if k == b"host"]
            if len(hosts) != 1:
                return False
            scheme, host, port = self.endpoint
            actual_scheme = {"ws": "http", "wss": "https"}.get(scope["scheme"], scope["scheme"])
            authority = hosts[0].decode("ascii")
            if (any(c in authority for c in "/?#") or
                    local_endpoint(scheme + "://" + authority) != self.endpoint or
                    actual_scheme != scheme or scope["server"][1] != port):
                return False
            if not credential:
                return True
            auth = [v for k, v in headers if k == b"authorization"]
            return (len(auth) == 1 and re.fullmatch(rb"Bearer [A-Za-z0-9_-]{32,256}", auth[0]) is not None
                    and hmac.compare_digest(auth[0][7:], self.token.encode("ascii")))
        except (ValueError, TypeError, KeyError, IndexError, UnicodeError):
            return False

    def require(self, request: Request):
        """Knowledge's existing dependency and error contract use the same authority."""
        if not self.allows(request.scope):
            raise HTTPException(403, detail="KNOWLEDGE_MANAGEMENT_UNAUTHORIZED")


class LocalBackendAccessMiddleware:
    """Runs before routing, redirects, body parsing, and WebSocket accept/state."""
    def __init__(self, app, session):
        self.app, self.session = app, session

    async def __call__(self, scope, receive, send):
        if scope["type"] not in {"http", "websocket"}:
            return await self.app(scope, receive, send)
        path = scope["path"].rstrip("/") or "/"
        method = scope.get("method", "")
        sensitive = not (scope["type"] == "http" and method in {"GET", "HEAD"} and path in PUBLIC_GET_PATHS)
        if not self.session.allows(scope, credential=sensitive):
            if scope["type"] == "websocket":
                await send({"type": "websocket.close", "code": 1008})
            else:
                body = ({"detail": "KNOWLEDGE_MANAGEMENT_UNAUTHORIZED"} if path.startswith("/v1/developer/knowledge")
                        else {"error": {"code": "LOCAL_BACKEND_ACCESS_DENIED"}})
                await JSONResponse(body, status_code=403, headers={"Cache-Control": "no-store"})(scope, receive, send)
            return

        async def private_send(message):
            if sensitive and message["type"] == "http.response.start":
                message = dict(message, headers=[(k, v) for k, v in message.get("headers", ())
                                                if k.lower() != b"cache-control"] + [(b"cache-control", b"no-store")])
            await send(message)
        await self.app(scope, receive, private_send)
