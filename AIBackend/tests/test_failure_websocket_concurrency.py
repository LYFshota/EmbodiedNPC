"""S03: real WS dispatch, Fake Providers, and import-before-storage isolation.

Run in a fresh subprocess, before other app integration modules. No server or
lifespan worker is started; the existing Starlette ASGI queue fixture is reused.
"""
from __future__ import annotations

import asyncio
from contextlib import closing
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import AsyncMock, patch


if "app.m3_main" in sys.modules or "app.main" in sys.modules:
    raise RuntimeError("S03_TEST_REQUIRES_FRESH_PROCESS")
_isolation = tempfile.TemporaryDirectory(prefix="embodiednpc-s03-")
_root = Path(_isolation.name)
for _key in list(os.environ):
    if _key.startswith(("EMBODIEDNPC_", "CANONICAL_EMBEDDING_", "OLLAMA_", "DEEPSEEK_",
                        "OPENAI_", "ANTHROPIC_", "GEMINI_", "GOOGLE_", "HF_", "TRANSFORMERS_")):
        os.environ.pop(_key, None)
for _key, _name in {
    "EMBODIEDNPC_MEMORY_DB": "memory.sqlite3",
    "EMBODIEDNPC_SETTINGS_FILE": "settings.json",
    "EMBODIEDNPC_MODEL_CONFIG_FILE": "models.json",
    "EMBODIEDNPC_CONTEXT_POLICY_FILE": "context.json",
    "EMBODIEDNPC_KNOWLEDGE_FILE": "knowledge.json",
    "EMBODIEDNPC_KNOWLEDGE_INDEX_DB": "knowledge.sqlite3",
    "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE": "session.json",
}.items():
    os.environ[_key] = str(_root / _name)
os.environ.update(EMBODIEDNPC_TEST_ROOT=str(_root), EMBODIEDNPC_PROVIDER="mock",
                  CANONICAL_EMBEDDING_ENABLED="false", HF_HUB_OFFLINE="1",
                  TRANSFORMERS_OFFLINE="1", PYTHONDONTWRITEBYTECODE="1")
sys.dont_write_bytecode = True

from tests.app_test_isolation import import_isolated_app
main = import_isolated_app()
from app.cognitive_entry import CognitiveBackendEntry
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.failure_service import TaskFailureResponse, TaskFailureResponseService
from app.local_access import LocalBackendAccessMiddleware, LocalBackendSession
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import ModelResult, ProviderError
from app.sqlite_database import SQLiteDatabase
from tests.test_cognitive_request import _event_request
from tests.test_cognitive_websocket import SocketHarness
from tests.test_m72f_failure_service import make_report
from tests import test_task_lifecycle as lifecycle_fixtures


class FailureProvider:
    name, model = "fake-failure", "offline"

    def __init__(self):
        self.started, self.release = asyncio.Event(), asyncio.Event()
        self.cancelled, self.finished = asyncio.Event(), asyncio.Event()
        self.calls, self.tasks = [], []
        self.error = None
        self.suppress_cancel = False

    async def generate(self, request):
        self.calls.append(request)
        self.tasks.append(asyncio.current_task())
        self.started.set()
        try:
            try:
                await self.release.wait()
            except asyncio.CancelledError:
                self.cancelled.set()
                if not self.suppress_cancel:
                    raise
                await self.release.wait()  # Controlled late completion on the old connection.
            if self.error:
                raise self.error
            return ModelResult(text=json.dumps(dict(resolution="explain", utterance="这次没有完成。",
                emotion="neutral", gesture="none", selected_alternative_id=None), ensure_ascii=False))
        finally:
            self.finished.set()


class FailureWebSocketTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.provider = FailureProvider()
        self.executor = PrioritizedModelExecutor()
        self.connections = []
        self.other_providers = []
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.database = SQLiteDatabase(Path(self.directory.name) / "events.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        instant = NS(name="fake-cognition", model="offline", context_length=16384,
                     generate=AsyncMock(return_value=ModelResult(text='{"decision_kind":"NO_ACTION"}')))
        for name, value in (
            ("failure_response_service", TaskFailureResponseService(self.provider, self.executor)),
            ("cognitive_entry", CognitiveBackendEntry(instant, PrioritizedModelExecutor())),
            ("experience_ingestion_service", ExperienceIngestionService(self.ledger)),
        ):
            scope = patch.object(main, name, value)
            scope.start()
            self.addCleanup(scope.stop)

    async def asyncTearDown(self):
        self.provider.release.set()
        for provider in self.other_providers:
            provider.release.set()
        for socket, task in self.connections:
            socket.send_release.set()
            if not task.done(): socket.disconnect()
        await asyncio.wait_for(asyncio.gather(*(t for _, t in self.connections), return_exceptions=True), 2)
        self.assertTrue(all(t.done() for p in (self.provider, *self.other_providers) for t in p.tasks))
        self.assertFalse(any(t.get_name() == "failure-explanation" for t in asyncio.all_tasks()))

    def connect(self, downstream=None):
        socket = SocketHarness()
        socket.socket.scope.update(scheme="ws", client=("127.0.0.1", 43210), server=("127.0.0.1", 8000))
        task = asyncio.create_task(main.websocket_endpoint(socket.socket) if downstream is None else downstream(socket))
        self.connections.append((socket, task))
        return socket, task

    @staticmethod
    def report(number=1, *, language="zh-CN"):
        payload = make_report().model_copy(update={"request_id": f"failure_{number}",
            "trace_id": f"trace_{number}", "proposal_id": f"proposal_{number}",
            "reply_language": language}).model_dump(mode="json")
        payload["character"] = dict(character_id=f"npc.synthetic.{number}", display_name=f"Fake NPC {number}",
            role_summary="offline fixture", backstory="synthetic", personality="calm",
            speaking_style="brief", boundaries="synthetic only")
        return payload

    async def block(self, socket, report=None):
        report = report or self.report()
        socket.message(report)
        await asyncio.wait_for(self.provider.started.wait(), 1)
        return report

    @staticmethod
    def assert_correlated(value, report):
        parsed = TaskFailureResponse.model_validate(value)
        for name in ("request_id", "trace_id", "proposal_id", "world_revision"):
            assert getattr(parsed, name) == report[name], name
        assert parsed.proposal_revision == report["proposal_revision"] + 1
        return parsed

    async def ping(self, socket):
        socket.message({"message_type": "ping", "request_id": "s03_ping"})
        response = await socket.result()
        self.assertEqual(response["message_type"], "pong")

    async def test_slow_failure_allows_durable_experience_ack(self):
        socket, _ = self.connect()
        report = await self.block(socket)
        example = Path(__file__).resolve().parents[2] / "Contracts/examples/valid/experience_append_request.json"
        payload = json.loads(example.read_text(encoding="utf-8"))
        socket.message(payload)
        ack = await socket.result()
        self.assertEqual((ack["message_type"], ack["status"], ack["request_id"]),
                         ("experience_append_result", "inserted", payload["request_id"]))
        with closing(self.database.connect()) as connection:
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM experience_events").fetchone()[0], 1)
        self.assertFalse(self.provider.finished.is_set())
        self.provider.release.set()
        self.assert_correlated(await socket.result(), report)

    async def test_slow_failure_allows_real_task_sync_and_cancel_transactions(self):
        fixture = lifecycle_fixtures.TaskLifecycleTests("runTest")
        fixture.setUp()
        task = fixture.seed()
        sync = fixture.sync_wire(fixture.modern(fixture.a), offer=False)
        cancel = fixture.cancellation(fixture.a, task)
        with patch.object(main, "task_cognition", fixture.tasks):
            socket, reader = self.connect()
            await self.block(socket)
            socket.message(sync.model_dump(mode="json"))
            snapshot = await socket.result()
            self.assertEqual(snapshot["message_type"], "task_runtime_snapshot")
            self.assertIn(task.task_id, [row["task_id"] for row in snapshot["tasks"]])
            socket.message(cancel.model_dump(mode="json"))
            result = await socket.result()
            self.assertEqual((result["message_type"], result["accepted"], result["outcome"]),
                             ("task_cancel_result", True, "cancelled"))
            self.assertEqual(fixture.service.get_task(fixture.scope, task.task_id).status, "cancelled")
            self.assertFalse(self.provider.finished.is_set())
            socket.disconnect()
            await asyncio.wait_for(reader, 1)

    async def test_ping_and_cognitive_response_bypass_slow_explanation(self):
        socket, _ = self.connect()
        await self.block(socket)
        await self.ping(socket)
        request = _event_request()
        socket.message(request)
        response = await socket.result()
        self.assertEqual(response["message_type"], "cognitive_response")
        self.assertEqual(response["request_id"], request["request_id"])
        self.assertFalse(self.provider.finished.is_set())

    async def test_capacity_bounds_one_running_and_three_queued_with_safe_overflow(self):
        socket, _ = self.connect()
        reports = [self.report(n) for n in range(1, 7)]
        await self.block(socket, reports[0])
        for report in reports[1:]: socket.message(report)
        overflow = [await socket.result(), await socket.result()]
        self.assertEqual({r["request_id"] for r in overflow}, {"failure_5", "failure_6"})
        for response in overflow:
            parsed = self.assert_correlated(response, next(r for r in reports if r["request_id"] == response["request_id"]))
            self.assertEqual((parsed.resolution, parsed.selected_alternative_id), ("clarify", None))
        self.assertEqual(len(self.provider.calls), 1)
        self.assertEqual(sum(t.get_name() == "failure-explanation" for t in asyncio.all_tasks()), 4)
        await self.ping(socket)
        self.provider.release.set()
        accepted = [await socket.result() for _ in range(4)]
        self.assertEqual({r["request_id"] for r in accepted}, {f"failure_{n}" for n in range(1, 5)})
        for response in accepted:
            self.assert_correlated(response, next(r for r in reports if r["request_id"] == response["request_id"]))
        self.assertEqual(len(self.provider.calls), 4)
        for number, request in enumerate(self.provider.calls, 1):
            self.assertIn(f"Fake NPC {number}", request.messages[-1].content)

    async def test_duplicate_inflight_report_has_one_model_call_and_owned_reply(self):
        socket, _ = self.connect()
        report = await self.block(socket)
        for _ in range(5): socket.message(report)
        await self.ping(socket)
        self.provider.release.set()
        self.assert_correlated(await socket.result(), report)
        await self.ping(socket)
        self.assertEqual(len(self.provider.calls), 1)
        self.assertEqual(sum(v["message_type"] == "task_failure_response" for v in socket.sent), 1)

    async def test_disconnect_cancels_active_and_queued_without_late_send(self):
        socket, reader = self.connect()
        await self.block(socket)
        for n in range(2, 5): socket.message(self.report(n))
        await self.ping(socket)
        socket.disconnect()
        await asyncio.wait_for(reader, 1)
        self.assertTrue(self.provider.cancelled.is_set())
        self.assertEqual(len(self.provider.calls), 1)
        self.assertFalse(any(v["message_type"] == "task_failure_response" for v in socket.sent))

    async def test_reader_cancellation_drains_owned_tasks(self):
        socket, reader = self.connect()
        await self.block(socket)
        socket.message(self.report(2))
        await self.ping(socket)
        reader.cancel()
        with self.assertRaises(asyncio.CancelledError): await reader
        self.assertTrue(self.provider.cancelled.is_set())
        self.assertTrue(all(t.done() for t in self.provider.tasks))

    async def test_disconnecting_one_socket_does_not_cancel_other_socket_explanation(self):
        first, reader = self.connect()
        await self.block(first)
        second, _ = self.connect()
        report = self.report(2)
        second.message(report)
        await self.ping(second)
        first.disconnect()
        await asyncio.wait_for(reader, 1)
        self.provider.release.set()
        self.assert_correlated(await second.result(), report)
        self.assertEqual(first.sent, [])
        self.assertEqual(len(self.provider.calls), 2)

    async def test_shared_model_gate_keeps_inference_order_but_not_receive_order(self):
        instant = NS(name="fake-cognition", model="offline", context_length=16384,
            generate=AsyncMock(return_value=ModelResult(text='{"decision_kind":"NO_ACTION"}')))
        with patch.object(main, "cognitive_entry", CognitiveBackendEntry(instant, self.executor)):
            socket, _ = self.connect()
            await self.block(socket)
            socket.message(_event_request())
            await self.ping(socket)
            instant.generate.assert_not_awaited()  # Existing shared model gate remains serialized.
            self.provider.release.set()
            responses = [await socket.result(), await socket.result()]
            self.assertEqual({v["message_type"] for v in responses}, {"cognitive_response", "task_failure_response"})
            instant.generate.assert_awaited_once()

    async def test_old_connection_late_completion_cannot_send_to_new_connection(self):
        self.provider.suppress_cancel = True
        old, old_reader = self.connect()
        report = await self.block(old)
        old.disconnect()
        await asyncio.wait_for(self.provider.cancelled.wait(), 1)
        replacement = FailureProvider()
        replacement.release.set()
        self.other_providers.append(replacement)
        with patch.object(main, "failure_response_service", TaskFailureResponseService(replacement)):
            new, _ = self.connect()
            new.message(report)  # Even identical wire IDs belong to this new socket.
            self.assert_correlated(await new.result(), report)
            self.provider.release.set()
            await asyncio.wait_for(old_reader, 1)
            self.assertEqual(old.sent, [])
            await self.ping(new)
            self.assertEqual(sum(v["message_type"] == "task_failure_response" for v in new.sent), 1)

    async def test_timeout_cancels_provider_and_returns_correlated_english_fallback(self):
        with patch.object(main, "FAILURE_EXPLANATION_TIMEOUT_SECONDS", .04):
            socket, _ = self.connect()
            report = await self.block(socket, self.report(language="en-US"))
            parsed = self.assert_correlated(await socket.result(), report)
            self.assertEqual(parsed.resolution, "clarify")
            self.assertTrue(parsed.utterance.startswith("I cannot"))
            self.assertTrue(self.provider.cancelled.is_set())
            await self.ping(socket)

    async def test_provider_and_unexpected_errors_return_fallback_without_stopping_reader(self):
        for error in (ProviderError("PRIVATE_API_KEY"), RuntimeError("PRIVATE_API_KEY")):
            with self.subTest(error=type(error).__name__):
                self.provider.error = error
                self.provider.release.set()
                socket, reader = self.connect()
                report = self.report()
                with patch.object(main.logger, "warning") as logs:
                    socket.message(report)
                    response = await socket.result()
                    await self.ping(socket)
                self.assertEqual(self.assert_correlated(response, report).resolution, "clarify")
                self.assertNotIn("PRIVATE_API_KEY", json.dumps(response) + str(logs.call_args_list))
                socket.disconnect()
                await reader

    async def test_send_failure_is_observed_and_does_not_retry_explanation(self):
        socket, reader = self.connect()
        await self.block(socket)
        socket.fail_send = True
        with self.assertLogs("embodiednpc.backend", "WARNING") as logs:
            self.provider.release.set()
            await asyncio.wait_for(socket.send_entered.wait(), 1)
            await asyncio.sleep(.01)
            socket.message({"message_type": "ping"})  # Wake a reader already inside receive.
            await asyncio.wait_for(reader, 1)
        self.assertEqual(len(self.provider.calls), 1)
        self.assertTrue(any("SEND_FAILED" in line for line in logs.output))
        self.assertEqual(socket.sent, [])

    async def test_failure_reply_and_pong_share_existing_send_lock(self):
        socket, _ = self.connect()
        await self.block(socket)
        socket.send_release.clear()
        socket.message({"message_type": "ping"})
        await asyncio.wait_for(socket.send_entered.wait(), 1)
        self.provider.release.set()
        await asyncio.wait_for(self.provider.finished.wait(), 1)
        socket.send_release.set()
        self.assertEqual((await socket.result())["message_type"], "pong")
        self.assertEqual((await socket.result())["message_type"], "task_failure_response")

    async def test_invalid_failure_never_enters_model_and_reader_keeps_working(self):
        socket, _ = self.connect()
        invalid = self.report()
        invalid["grant_execution"] = True
        socket.message(invalid)
        self.assertEqual((await socket.result())["reason"], "REQUEST_SCHEMA_INVALID")
        await self.ping(socket)
        self.assertEqual(self.provider.calls, [])

    async def test_s01_ws_denial_rotation_and_authorized_dispatch(self):
        session_path = Path(self.directory.name) / "private-session.json"
        with patch.dict(os.environ, {"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE": str(session_path),
                                     "EMBODIEDNPC_KNOWLEDGE_ADMIN_URL": "http://127.0.0.1:8000"}):
            session = LocalBackendSession(NS(path=session_path))
            session.start()
            self.addCleanup(session.stop)
            async def check(headers):
                socket = SocketHarness()
                scope = dict(type="websocket", path="/ws", scheme="ws", client=("127.0.0.1", 43210),
                             server=("127.0.0.1", 8000), headers=[(b"host", b"127.0.0.1:8000"), *headers])
                raw = []
                async def dispatch(scope, receive, send):
                    from starlette.websockets import WebSocket
                    await main.websocket_endpoint(WebSocket(scope, receive, send))
                async def send(message):
                    raw.append(message)
                    await socket.send(message)
                runner = asyncio.create_task(LocalBackendAccessMiddleware(dispatch, session)(scope, socket.receive, send))
                return socket, runner, raw
            old = [(b"authorization", ("Bearer " + session.token).encode())]
            for headers in ([], [(b"authorization", b"Bearer wrong")]):
                socket, reader, raw = await check(headers)
                await reader
                self.assertEqual(raw[0], {"type": "websocket.close", "code": 1008})
            session.stop(); session.start()
            socket, reader, raw = await check(old)
            await reader
            self.assertEqual(raw[0]["code"], 1008)
            socket, reader, raw = await check([(b"authorization", ("Bearer " + session.token).encode())])
            self.connections.append((socket, reader))
            await self.ping(socket)
            self.assertEqual(raw[0]["type"], "websocket.accept")
            self.assertEqual(self.provider.calls, [])


if __name__ == "__main__":
    unittest.main()
