"""Loopback UI fixture. In-memory Knowledge plus the P1 model fixture; no Backend app/DB imports."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import uvicorn

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("settings_fixture", Path(__file__).with_name("SettingsP1FakeService.py"))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
base.initial_embed["enabled"] = base.fixture.embed["enabled"] = True
base.active = copy.deepcopy(base.fixture.activation)
root = base.root

initial = dict(revision=4, selected_id="fixture-knowledge", items=[dict(
    entry=dict(knowledge_id="fixture-knowledge", name="隔离参考知识", content="仅用于设置布局验证的静态内容。",
               knowledge_type="reference", source="fixture", version=1, attributes={}, related_ids=[],
               access=dict(character_ids=["cafe_merchant"], scene_ids=["CafeDemoV2"], deployment_ids=[])),
    has_default=True, hidden=False, origin="builtin", referenced_by=[])],
    limits=dict(name=120, content=6000, knowledge_type=80, attribute_keys=16, attribute_key_length=80, attributes_chars=3000, related_ids=20),
    scopes=dict(deployment_id="fixture", character_ids=["cafe_merchant"], scene_ids=["CafeDemoV2"]),
    index=dict(mode="disabled", ready=0, total=1))
catalog = copy.deepcopy(initial)

async def app(scope, receive, send):
    global catalog
    path, method = scope.get("path", ""), scope.get("method", "GET")
    if scope["type"] != "http" or (not path.startswith("/v1/developer/knowledge") and path != "/v1/embedding-index/status"):
        if path == "/fixture/reset":
            catalog = copy.deepcopy(initial)
        return await base.app(scope, receive, send)
    status = 200
    if path == "/v1/embedding-index/status":
        result = dict(status="waiting", applied=True, cloud=False, control_allowed=True, counts_known=True,
                      total=3, pending=3, completed=0, in_flight=0, progress=0, model="fixture-embedding")
    elif method == "GET":
        result = dict(revision=catalog["revision"], index=catalog["index"]) if path.endswith("/status") else catalog
    elif method in ("POST", "PUT") and not path.endswith("/restore"):
        body = b""
        while True:
            event = await receive()
            body += event.get("body", b"")
            if not event.get("more_body"): break
        command = json.loads(body)
        if command["expected_revision"] != catalog["revision"]:
            status, result = 409, dict(detail="KNOWLEDGE_REVISION_CONFLICT")
        else:
            item = catalog["items"][0]
            for key in ("name", "content", "knowledge_type", "attributes", "related_ids"):
                item["entry"][key] = command[key]
            item["entry"]["version"] += 1
            catalog["revision"] += 1
            base.writes.append(dict(resource="knowledge", revision=catalog["revision"]))
            result = catalog
    else:
        status, result = 403, dict(detail="UI_FIXTURE_WRITE_NOT_ALLOWLISTED")
    base.fixture.stats["http"] += 1
    base.state()
    await send(dict(type="http.response.start", status=status, headers=[(b"content-type", b"application/json")]))
    await send(dict(type="http.response.body", body=json.dumps(result).encode()))

if __name__ == "__main__":
    base.state()
    (root / "fake-ready.txt").write_text("isolated loopback fixture 18846")
    uvicorn.run(app, host="127.0.0.1", port=18846, lifespan="off", log_level="warning", access_log=False)
