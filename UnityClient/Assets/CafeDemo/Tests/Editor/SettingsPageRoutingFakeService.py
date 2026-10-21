"""Read-only routing fixture, including first-start HTTP failure. Never loads the Backend app."""
import importlib.util
from pathlib import Path
import sys
import uvicorn

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("hierarchy_fixture", Path(__file__).with_name("SettingsHierarchyFakeService.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
root = fixture.root

async def app(scope, receive, send):
    mode = (root / "routing-mode.txt").read_text().strip() if (root / "routing-mode.txt").exists() else "ready"
    path = scope.get("path", "")
    if scope["type"] == "http" and not path.startswith("/fixture/") and (mode == "offline" or scope["method"] != "GET"):
        fixture.base.fixture.stats["http"] += 1
        if scope["method"] != "GET":
            fixture.base.writes.append(dict(resource="FORBIDDEN", path=path))
        fixture.base.state()
        await send(dict(type="http.response.start", status=503 if mode == "offline" else 403,
                        headers=[(b"content-type", b"application/json")]))
        await send(dict(type="http.response.body", body=b'{"error":{"code":"BACKEND_UNAVAILABLE"}}'))
        return
    return await fixture.app(scope, receive, send)

if __name__ == "__main__":
    fixture.base.state()
    (root / "fake-ready.txt").write_text("routing fixture 18846; all writes denied")
    uvicorn.run(app, host="127.0.0.1", port=18846, lifespan="off", log_level="warning", access_log=False)
