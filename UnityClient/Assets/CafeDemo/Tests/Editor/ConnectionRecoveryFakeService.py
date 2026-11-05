"""Read-only connection recovery fixture on loopback 18847. No Backend imports or model calls."""
import sys
sys.dont_write_bytecode = True
import importlib.util
import json
import os
from pathlib import Path
import uvicorn
spec = importlib.util.spec_from_file_location("recovery_fixture", Path(__file__).with_name("SettingsHierarchyFakeService.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
root = fixture.root
health_calls = 0

def counter():
    temp = root / "health-count.tmp"
    temp.write_text(str(health_calls))
    os.replace(temp,root / "health-count.txt")

async def app(scope, receive, send):
    global health_calls
    path = scope.get("path")
    if path == "/fixture/reset":
        health_calls = 0
        counter()
    mode = (root / "mode.txt").read_text().strip() if (root / "mode.txt").exists() else "ready"
    if path == "/v2/model-config":
        fixture.base.fixture.main["health_strategy"] = "generation_once" if mode == "generation" else "models"
        fixture.base.active["main_model"]["health_strategy"] = fixture.base.fixture.main["health_strategy"]
    with (root / "request-sequence.jsonl").open("a") as log:
        log.write(json.dumps(dict(path=path,mode=mode,health=health_calls))+"\n")
    if path != "/v1/runtime-status":
        return await fixture.app(scope, receive, send)
    health_calls += 1
    counter()
    available = mode == "ready" or (mode == "transient" and health_calls > 1)
    code = "" if available else "OPENAI_COMPATIBLE_AUTH_FAILED" if mode == "auth" else "OPENAI_COMPATIBLE_TLS_FAILED"
    result = dict(status="ok", restart_required=False,
        model_config=dict(configured=True, revision=fixture.base.fixture.revision),
        active_provider=dict(provider="openai_compatible", model="fixture-main", available=available, detail=code),
        active_main_model=dict(provider="openai_compatible", model="fixture-main"), active_embedding=dict(enabled=True))
    await send(dict(type="http.response.start", status=200, headers=[(b"content-type", b"application/json")]))
    await send(dict(type="http.response.body", body=json.dumps(result).encode()))

if __name__ == "__main__":
    (root / "mode.txt").write_text("ready")
    (root / "fake-ready.txt").write_text("read-only recovery fixture 18847")
    uvicorn.run(app, host="127.0.0.1", port=18847, lifespan="off", log_level="warning", access_log=False)
