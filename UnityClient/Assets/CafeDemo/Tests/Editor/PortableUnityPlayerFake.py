"""Portable Player fixture. Reuses the existing Title HTTP/WS fixture; no Backend imports/models/DB.

python -B PortableUnityPlayerFake.py <evidence> <private UserData> [port=18846]
mode.txt supports the existing ready/unconfigured modes. Re-launch rotates the local test capability.
"""
import asyncio
import http
import importlib.util
import json
from pathlib import Path
import secrets
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("portable_title_fixture", Path(__file__).with_name("StartupProgressFakeService.py"))
progress = importlib.util.module_from_spec(spec)
spec.loader.exec_module(progress)
fixture = progress.fixture
data_root = Path(sys.argv[2]).resolve()
port = int(sys.argv[3]) if len(sys.argv) > 3 else 18846
assert port not in (8000, 18000)
url = f"http://127.0.0.1:{port}"
session_path = data_root / "runtime/knowledge_admin_session.json"
token = secrets.token_urlsafe(32)
fixture.stats["types"] = {}
fixture.stats["knowledge_reads"] = 0
fixture.stats["knowledge_rejected"] = 0


async def request(connection, incoming):
    if incoming.headers.get("Upgrade", "").lower() != "websocket":
        if incoming.method != "GET":
            fixture.stats["writes"].append(incoming.path)
            fixture.snapshot()
            return connection.respond(http.HTTPStatus.FORBIDDEN, '{"detail":"PORTABLE_FIXTURE_READ_ONLY"}')
        if incoming.path.startswith("/v1/developer/knowledge"):
            fixture.stats["knowledge_reads"] += 1
            if incoming.headers.get("Authorization") != "Bearer " + token:
                fixture.stats["knowledge_rejected"] += 1
                fixture.snapshot()
                return connection.respond(http.HTTPStatus.FORBIDDEN, '{"detail":"KNOWLEDGE_MANAGEMENT_UNAUTHORIZED"}')
            result = dict(revision=0, items=[], selected_id=None, index=dict(mode="disabled", ready=0, total=0),
                          scopes=dict(deployment_id="portable-fixture", character_ids=["cafe_merchant"], scene_ids=["CafeDemoV2"]),
                          limits=dict(name=120, content=6000, knowledge_type=80, attribute_keys=16, attribute_key_length=80, attributes_chars=3000, related_ids=20))
            if not incoming.path.endswith("/status"):
                result["items"] = [dict(entry=dict(knowledge_id="portable-knowledge", name="Portable 隔离知识", content="授权路径验证，无真实 Backend 数据。",
                                                     knowledge_type="reference", source="fixture", version=1, attributes={}, related_ids=[], access={}),
                                        has_default=True, hidden=False, origin="builtin", referenced_by=[])]
            fixture.snapshot()
            return connection.respond(http.HTTPStatus.OK, json.dumps(result))
    return await progress.request(connection, incoming)


class ObservedConnection:
    def __init__(self, connection):
        self.connection = connection

    async def __aiter__(self):
        async for message in self.connection:
            kind = json.loads(message).get("message_type", "unknown")
            fixture.stats["types"][kind] = fixture.stats["types"].get(kind, 0) + 1
            yield message

    async def send(self, message):
        await self.connection.send(message)


async def websocket(connection):
    # The reused handler ACKs only temporary Observations. Never returns cognition/actions.
    await fixture.websocket(ObservedConnection(connection))


async def run():
    session_path.parent.mkdir(parents=True, exist_ok=True)
    session_path.write_text(json.dumps(dict(schema_version=1, base_url=url, token=token)), encoding="utf-8")
    fixture.snapshot()
    try:
        async with fixture.serve(websocket, "127.0.0.1", port, process_request=request):
            (fixture.root / "fake-ready.txt").write_text(f"Portable Player Fake loopback {port}; no DB/models")
            print(f"Portable Player fixture ready on {port}", flush=True)
            await asyncio.Future()
    finally:
        session_path.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass
