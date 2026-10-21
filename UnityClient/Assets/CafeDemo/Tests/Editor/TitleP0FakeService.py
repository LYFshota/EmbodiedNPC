"""Isolated Title P0 fixture, no Backend imports, database, model calls or persistent settings.
Run with AIBackend/.venv/Scripts/python.exe <this file> <UnityClient/Temp/TitleP0>.
Only listens on loopback 18846. mode.txt selects deterministic HTTP/WS responses.
"""
import asyncio
import http
import json
import os
from pathlib import Path
import sys
from urllib.parse import parse_qs, urlsplit

from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed

root = Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
stats = {"active_ws": 0, "max_ws": 0, "messages": 0, "writes": [], "http": 0}
revision = 7


def mode():
    return (root / "mode.txt").read_text().strip() if (root / "mode.txt").exists() else "ready"


def snapshot():
    temporary = root / "stats.tmp"
    temporary.write_text(json.dumps(stats), encoding="utf-8")
    os.replace(temporary, root / "stats.json")


main = dict(display_name="Isolated fixture", provider_type="openai_compatible",
            base_url="http://127.0.0.1:18846", model="fixture-main", key_present=True,
            temperature=0.2, thinking=False, max_output_tokens=1024, context_length=16384,
            timeout_seconds=10, schema_profile="portable_union", token_parameter="max_tokens",
            thinking_parameter="none", send_temperature=True, top_p=-1, health_strategy="models")
embed = dict(enabled=False, provider_type="ollama", reuse_main_api=False,
             base_url="http://127.0.0.1:18846", model="fixture-embedding", timeout_seconds=10)
builtin = dict(enabled=True, provider_type="builtin", reuse_main_api=False,
               model="embeddinggemma-300m-qat-q4_0", dimensions=768, model_version="fixture-v1",
               resource_status="available", resource_directory="isolated-fixture")
activation = dict(main_profile_id="fixture-main", embedding_profile_id="fixture-embedding",
                  main_model=main, embedding=embed)


def profiles():
    unconfigured = mode() == "unconfigured"
    return dict(schema_version=2, revision=0 if unconfigured else revision, configured=not unconfigured,
                migration_required=False, restart_required=mode() == "pending",
                main_profiles=[] if unconfigured else [dict(profile_id="fixture-main", preset_id="fixture-cloud", config=main)],
                embedding_profiles=[] if unconfigured else [dict(profile_id="fixture-embedding", preset_id="ollama", config=embed)],
                main_presets=[dict(preset_id="fixture-cloud", name="隔离测试服务", source="cloud", protocol="openai_compatible",
                                   base_url="http://127.0.0.1:18846", discovery="models", schema_profile="portable_union",
                                   token_parameter="max_tokens", thinking_parameter="none", send_temperature=True,
                                   health_strategy="models", default_context_length=16384)],
                embedding_presets=[dict(preset_id="builtin_gemma", name="内置本地模型", source="local", protocol="builtin"),
                                   dict(preset_id="ollama", name="Ollama", source="local", protocol="ollama",
                                        base_url="http://127.0.0.1:18846", discovery="models")],
                selected_to_activate=activation, active_runtime=None if unconfigured else activation,
                default_main_preset_id="fixture-cloud", default_embedding_preset_id="ollama",
                builtin_embedding_defaults=builtin, builtin_resource_status="available")


async def process_request(connection, request):
    if request.headers.get("Upgrade", "").lower() == "websocket":
        if mode() == "wsfail":
            return connection.respond(http.HTTPStatus.SERVICE_UNAVAILABLE, "fixture WS unavailable")
        return None
    stats["http"] += 1
    if mode() == "httpfail":
        snapshot()
        return connection.respond(http.HTTPStatus.SERVICE_UNAVAILABLE, json.dumps({"error": {"code": "BACKEND_UNAVAILABLE"}}))
    # This service deliberately accepts only read-only GET fixtures. Save transport is covered
    # by the existing model form tests; it cannot silently mutate real model settings.
    path = request.path.split("?")[0]
    if path == "/v2/model-config":
        result = profiles()
    elif path == "/v1/runtime-status":
        result = dict(status="ok", restart_required=mode() == "pending", model_config=dict(configured=True, revision=revision),
                      active_provider=dict(provider="fixture", model="fixture-main", available=mode() != "providerfail"),
                      active_main_model=dict(provider="fixture", model="fixture-main"), active_embedding=dict(enabled=False))
    elif path == "/v1/context-policy":
        document = dict(schema_version=1, revision=0, config=dict(mode="auto", target_messages=None))
        result = dict(saved=document, applied=document, source="defaults", hot_apply=True, saved_matches_applied=True)
    elif path == "/v1/embedding-index/status":
        result = dict(status="disabled", applied=True, cloud=False, total=0, pending=0, completed=0, in_flight=0)
    elif path == "/v1/dialogue-history" and mode() == "localization":
        query = parse_qs(urlsplit(request.path).query)
        observer = query.get("observer_id", [""])[0]
        conversation = query.get("conversation_id", [""])[0]
        cursor = int(query.get("after_seq", ["0"])[0])
        items = [] if cursor else [dict(event_id="localization-ui-history", experience_seq=1,
            role="npc", actor=observer, text="历史正文保持原样。", timestamp=1)]
        result = dict(observer_id=observer, conversation_id=conversation, items=items,
            next_after_seq=cursor or 1, has_more=False)
    else:
        snapshot()
        return connection.respond(http.HTTPStatus.NOT_FOUND, json.dumps({"error": {"code": "ISOLATED_READ_ONLY_FIXTURE"}}))
    snapshot()
    return connection.respond(http.HTTPStatus.OK, json.dumps(result))


async def websocket(connection):
    stats["active_ws"] += 1
    stats["max_ws"] = max(stats["max_ws"], stats["active_ws"])
    snapshot()
    try:
        async for message in connection:
            stats["messages"] += 1
            snapshot()
            # No cognition response / inference. Only acknowledge isolated experience receipts.
            value = json.loads(message)
            if value.get("message_type") == "experience_append_request":
                draft = value["draft"]
                await connection.send(json.dumps(dict(message_type="experience_append_result", schema_version="1.0",
                    request_id=value["request_id"], trace_id=value["trace_id"], observer_id=draft["observer_id"],
                    event_id="experience-fixture-" + str(stats["messages"]), experience_seq=stats["messages"], status="inserted", reason="")))
    except ConnectionClosed:
        pass
    finally:
        stats["active_ws"] -= 1
        snapshot()


async def run():
    snapshot()
    async with serve(websocket, "127.0.0.1", 18846, process_request=process_request):
        (root / "fake-ready.txt").write_text("loopback 18846, no Backend/database/model")
        print("Title P0 isolated fixture listening on 127.0.0.1:18846", flush=True)
        await asyncio.Future()


if __name__ == "__main__":
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass
