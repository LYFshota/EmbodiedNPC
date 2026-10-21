"""Loopback-only HTTP/WS fixture. No application imports, SQLite, keys, or model calls.
Usage: python SettingsP1FakeService.py <UnityClient/Temp/SettingsP1>
"""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import uvicorn

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("title_fixture", Path(__file__).with_name("TitleP0FakeService.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
root = fixture.root
active = copy.deepcopy(fixture.activation)
context = dict(schema_version=1, revision=3, config=dict(mode="auto", target_messages=None))
writes = []
initial_main, initial_embed = copy.deepcopy(fixture.main), copy.deepcopy(fixture.embed)


def state():
    fixture.stats["writes"] = writes
    try:
        fixture.snapshot()
    except PermissionError:
        # Windows may briefly retain a reader/AV handle. Evidence output must never fail an HTTP operation.
        # The next status poll writes the complete in-memory history again.
        pass


def profiles():
    result = fixture.profiles()
    result["updated_at"] = "2026-10-16T00:00:00Z"
    result["active_runtime"] = copy.deepcopy(active)
    result["restart_required"] = result["selected_to_activate"] != active
    return result


async def app(scope, receive, send):
    global active, context
    if scope["type"] == "websocket":
        await send(dict(type="websocket.accept"))
        fixture.stats["active_ws"] += 1
        fixture.stats["max_ws"] = max(fixture.stats["max_ws"], fixture.stats["active_ws"])
        state()
        try:
            while True:
                event = await receive()
                if event["type"] == "websocket.disconnect":
                    break
                if event["type"] != "websocket.receive":
                    continue
                fixture.stats["messages"] += 1
                value = json.loads(event.get("text") or event["bytes"])
                if value.get("message_type") == "experience_append_request":
                    response = dict(message_type="experience_append_result", schema_version="1.0",
                                    request_id=value["request_id"], trace_id=value["trace_id"],
                                    observer_id=value["draft"]["observer_id"],
                                    event_id="p1-fixture-" + str(fixture.stats["messages"]),
                                    experience_seq=fixture.stats["messages"], status="inserted", reason="")
                    await send(dict(type="websocket.send", text=json.dumps(response)))
                state()
        finally:
            fixture.stats["active_ws"] -= 1
            state()
        return
    path, method = scope["path"], scope["method"]
    body = b""
    while True:
        event = await receive()
        body += event.get("body", b"")
        if not event.get("more_body"):
            break
    command = json.loads(body) if body else None
    status = 200
    if method == "POST" and path == "/v2/model-config/probe" and command.get("operation") == "discover":
        fixture.stats["discovery"] = dict(model=command["config"].get("model"), key_action=command["key_action"])
        result = dict(success=True, models=["fixture-draft-a", "fixture-draft-b"])
    elif method == "POST" and path == "/v2/model-config/save-and-activate":
        if command["expected_revision"] != fixture.revision:
            status, result = 409, dict(error=dict(code="MODEL_CONFIG_REVISION_CONFLICT"))
        else:
            actions = {}
            for slot, target in (("main", fixture.main), ("embedding", fixture.embed)):
                draft = command.get(slot)
                if draft:
                    config = {key: value for key, value in draft["config"].items() if value is not None}
                    config.pop("api_key", None)  # Never persist or log even synthetic key text.
                    present = target.get("key_present", False)
                    target.clear()
                    target.update(config)
                    target["key_present"] = present if draft["key_action"] == "retain" else draft["key_action"] == "replace"
                    actions[slot] = draft["key_action"]
            fixture.embed["enabled"] = command["embedding_enabled"]
            fixture.revision += 1
            writes.append(dict(resource="profiles", revision=fixture.revision, slots=list(actions), key_actions=actions))
            result = profiles()  # Saved selection deliberately differs from active until explicit fixture activation.
    elif method == "PUT" and path == "/v1/context-policy":
        writes.append(dict(resource="context", expected_revision=command["expected_revision"]))
        if fixture.mode() == "contextfail":
            status, result = 503, dict(error=dict(code="BACKEND_UNAVAILABLE"))
        elif command["expected_revision"] != context["revision"]:
            status, result = 409, dict(error=dict(code="CONTEXT_POLICY_REVISION_CONFLICT"))
        else:
            context = dict(schema_version=1, revision=context["revision"] + 1, config=command["config"])
            result = dict(saved=context, applied=context, hot_apply=True, saved_matches_applied=True, source="persisted")
    elif method != "GET":
        status, result = 403, dict(error=dict(code="P1_WRITE_NOT_ALLOWLISTED"))
    elif path == "/v2/model-config":
        result = profiles()
    elif path == "/v1/context-policy":
        result = dict(saved=context, applied=context, hot_apply=True, saved_matches_applied=True, source="persisted")
    elif path == "/v1/runtime-status":
        result = dict(status="ok", restart_required=profiles()["restart_required"],
                      model_config=dict(configured=True, revision=fixture.revision),
                      active_provider=dict(provider="fixture", model=active["main_model"]["model"], available=True),
                      active_main_model=dict(provider="fixture", model=active["main_model"]["model"]),
                      active_embedding=dict(enabled=active["embedding"]["enabled"]))
    elif path == "/v1/embedding-index/status":
        result = dict(status="disabled", applied=True, cloud=False, total=0, pending=0, completed=0, in_flight=0)
    elif path == "/fixture/reset":
        fixture.main.clear()
        fixture.main.update(copy.deepcopy(initial_main))
        fixture.embed.clear()
        fixture.embed.update(copy.deepcopy(initial_embed))
        fixture.revision = 7
        context = dict(schema_version=1, revision=3, config=dict(mode="auto", target_messages=None))
        active = copy.deepcopy(fixture.activation)
        writes.clear()
        result = dict(ok=True)
    elif path == "/fixture/activate":
        active = copy.deepcopy(fixture.activation)
        result = dict(ok=True)
    else:
        status, result = 404, dict(error=dict(code="P1_FIXTURE_NOT_IMPLEMENTED"))
    fixture.stats["http"] += 1
    state()
    await send(dict(type="http.response.start", status=status, headers=[(b"content-type", b"application/json")]))
    await send(dict(type="http.response.body", body=json.dumps(result).encode()))


if __name__ == "__main__":
    state()
    (root / "fake-ready.txt").write_text("isolated loopback fixture 18846")
    uvicorn.run(app, host="127.0.0.1", port=18846, lifespan="off", log_level="warning", access_log=False)
