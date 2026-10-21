"""Progress UX fixture: existing read-only P0 responses and ACKs; no Backend/DB/provider imports."""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("startup_fixture", Path(__file__).with_name("TitleP0FakeService.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
fixture.stats["paths"] = {}


def snapshot():
    # Windows inspection tools may briefly hold the destination without FILE_SHARE_DELETE.
    # A statistics read must never abort a fixture handshake or leave a false active_ws count.
    temporary = fixture.root / "stats.tmp"
    temporary.write_text(json.dumps(fixture.stats), encoding="utf-8")
    for attempt in range(30):
        try:
            os.replace(temporary, fixture.root / "stats.json")
            return
        except PermissionError:
            if attempt == 29:
                raise
            time.sleep(.01)


fixture.snapshot = snapshot


async def request(connection, incoming):
    if incoming.headers.get("Upgrade", "").lower() != "websocket":
        path = incoming.path.split("?")[0]
        paths = fixture.stats["paths"]
        paths[path] = paths.get(path, 0) + 1
        # Real async boundaries, so tests can observe requests before successful responses.
        if path == "/v2/model-config":
            await asyncio.sleep(.45)
        elif path == "/v1/runtime-status":
            await asyncio.sleep(.9)
    return await fixture.process_request(connection, incoming)


async def run():
    fixture.snapshot()
    async with fixture.serve(fixture.websocket, "127.0.0.1", 18846, process_request=request):
        (fixture.root / "fake-ready.txt").write_text("startup UX Fake; loopback 18846; no database or models")
        print("Startup progress fixture 18846", flush=True)
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(run())
