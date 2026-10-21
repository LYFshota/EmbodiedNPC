from __future__ import annotations

import asyncio
from contextlib import closing
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from starlette.websockets import WebSocket

from app.cognitive_entry import CognitiveBackendEntry
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.local_model_scheduler import PrioritizedModelExecutor
from app.sqlite_database import SQLiteDatabase
from app.providers import ModelMessage, ModelRequest, ModelResult, ProviderError
from tests.test_cognitive_request import _event_request


class BlockingProvider:
    name = "test"
    model = "blocked"
    # Transport concurrency tests need the supported Demo window, not the
    # conservative unknown-capacity fallback before generate() is reached.
    context_length = 16384

    def __init__(self):
        self.started = asyncio.Event()
        self.release = asyncio.Event()
        self.cancelled = asyncio.Event()
        self.finished = asyncio.Event()
        self.calls = 0
        self.tasks = []
        self.output = '{"decision_kind":"NO_ACTION"}'
        self.error = None

    async def generate(self, request):
        self.calls += 1
        self.tasks.append(asyncio.current_task())
        self.started.set()
        try:
            await self.release.wait()
            if self.error:
                raise self.error
            return ModelResult(text=self.output)
        except asyncio.CancelledError:
            self.cancelled.set()
            raise
        finally:
            self.finished.set()


class SocketHarness:
    """Exercise the real Starlette WebSocket with deterministic ASGI queues."""

    def __init__(self):
        self.incoming = asyncio.Queue()
        self.outgoing = asyncio.Queue()
        self.sent = []
        self.disconnected = False
        self.sending = False
        self.send_entered = asyncio.Event()
        self.send_release = asyncio.Event()
        self.send_release.set()
        self.fail_send = False
        self.incoming.put_nowait({"type": "websocket.connect"})
        self.socket = WebSocket({"type": "websocket", "path": "/ws"}, self.receive, self.send)

    async def receive(self):
        message = await self.incoming.get()
        if message["type"] == "websocket.disconnect":
            self.disconnected = True
        return message

    async def send(self, message):
        if message["type"] != "websocket.send":
            return
        assert not self.disconnected, "late send after disconnect"
        assert not self.sending, "concurrent WebSocket writers"
        self.sending = True
        try:
            self.send_entered.set()
            await self.send_release.wait()
            if self.fail_send:
                raise OSError("test transport closed")
            value = json.loads(message["text"])
            self.sent.append(value)
            self.outgoing.put_nowait(value)
        finally:
            self.sending = False

    def message(self, payload):
        self.incoming.put_nowait({"type": "websocket.receive", "text": json.dumps(payload)})

    def disconnect(self):
        self.incoming.put_nowait({"type": "websocket.disconnect", "code": 1000})

    async def result(self):
        return await asyncio.wait_for(self.outgoing.get(), 2)


class CognitiveWebSocketTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from app import m3_main
        self.main = m3_main
        self.provider = BlockingProvider()
        self.executor = PrioritizedModelExecutor()
        self.patch = patch.object(m3_main, "cognitive_entry",
                                  CognitiveBackendEntry(self.provider, self.executor))
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.connections = []

    async def asyncTearDown(self):
        for socket, task in self.connections:
            socket.send_release.set()
            socket.disconnect()
        await asyncio.wait_for(asyncio.gather(*(t for _, t in self.connections)), 2)
        self.assertTrue(all(task.done() for task in self.provider.tasks))

    def connect(self):
        socket = SocketHarness()
        task = asyncio.create_task(self.main.websocket_endpoint(socket.socket))
        self.connections.append((socket, task))
        return socket, task

    async def blocked_cognition(self, socket):
        socket.message(_event_request())
        await asyncio.wait_for(self.provider.started.wait(), 2)

    def assert_correlation(self, response, request=None):
        request = request or _event_request()
        self.assertEqual(response["request_id"], request["request_id"])
        self.assertEqual(response["trace_id"], request["trace_id"])
        self.assertEqual(response["run_id"], request["run"]["run_id"])
        self.assertEqual(response["agent_entity_id"], request["run"]["agent_entity_id"])

    async def test_retired_proactive_messages_are_unsupported(self):
        socket, _ = self.connect()
        for message_type in ("proactive_trigger_event", "proactive_presentation_result"):
            socket.message({"message_type": message_type, "request_id": "retired_01"})
            response = await socket.result()
            self.assertEqual(response["message_type"], "error")
            self.assertEqual(response["reason"], "UNSUPPORTED_MESSAGE_TYPE")
        self.assertEqual(self.provider.calls, 0)

    async def test_pending_cognition_allows_pong_before_model_release(self):
        socket, _ = self.connect()
        await self.blocked_cognition(socket)
        socket.message({"message_type": "ping", "request_id": "ping_01"})
        self.assertEqual((await socket.result())["message_type"], "pong")
        self.assertFalse(self.provider.finished.is_set())
        self.provider.release.set()
        response = await socket.result()
        self.assertEqual(response["message_type"], "cognitive_response")
        self.assert_correlation(response)
        self.assertEqual([v["message_type"] for v in socket.sent], ["pong", "cognitive_response"])

    async def test_pending_cognition_allows_durable_experience_ack_before_release(self):
        example = Path(__file__).resolve().parents[2] / "Contracts/examples/valid/experience_append_request.json"
        payload = json.loads(example.read_text(encoding="utf-8"))
        with tempfile.TemporaryDirectory() as directory:
            repository = SQLiteDatabase(Path(directory) / "memory.sqlite3")
            ingestion = ExperienceIngestionService(ExperienceLedger(repository.connect))
            with patch.object(self.main, "experience_ingestion_service", ingestion):
                socket, _ = self.connect()
                await self.blocked_cognition(socket)
                socket.message(payload)
                ack = await socket.result()
                self.assertEqual(ack["message_type"], "experience_append_result")
                self.assertEqual(ack["request_id"], payload["request_id"])
                self.assertEqual(ack["status"], "inserted")
                self.assertFalse(self.provider.finished.is_set())
                with closing(repository.connect()) as connection:
                    self.assertEqual(connection.execute("SELECT COUNT(*) FROM experience_events").fetchone()[0], 1)
                self.provider.release.set()
                self.assertEqual((await socket.result())["message_type"], "cognitive_response")

    async def test_disconnect_cancels_active_and_queued_owned_tasks_without_late_send(self):
        socket, task = self.connect()
        await self.blocked_cognition(socket)
        second = _event_request()
        second["request_id"] = "request_02"
        socket.message(second)
        socket.message({"message_type": "ping"})
        await socket.result()  # Reader has admitted the second cognition.
        socket.disconnect()
        await asyncio.wait_for(task, 2)
        self.assertTrue(self.provider.cancelled.is_set())
        self.assertEqual(self.provider.calls, 1)
        self.assertEqual([v["message_type"] for v in socket.sent], ["pong"])
        self.assertFalse(any(t.get_name().startswith("cognition:") for t in asyncio.all_tasks()))

    async def test_disconnect_does_not_cancel_other_connection(self):
        first, first_task = self.connect()
        await self.blocked_cognition(first)
        second, _ = self.connect()
        second.message(_event_request())
        second.message({"message_type": "ping"})
        await second.result()
        first.disconnect()
        await asyncio.wait_for(first_task, 2)
        self.provider.release.set()
        self.assertEqual((await second.result())["message_type"], "cognitive_response")
        self.assertEqual(first.sent, [])

    async def test_disconnect_of_queued_cognition_leaves_global_background_running(self):
        background = asyncio.create_task(self.executor.execute(
            self.provider, ModelRequest(messages=(ModelMessage(role="user", content="background"),)),
            priority="background",
        ))
        await asyncio.wait_for(self.provider.started.wait(), 2)
        socket, task = self.connect()
        socket.message(_event_request())
        socket.message({"message_type": "ping"})
        await socket.result()
        socket.disconnect()
        await asyncio.wait_for(task, 2)
        self.assertFalse(background.done())
        self.assertFalse(self.provider.cancelled.is_set())
        self.provider.release.set()
        await asyncio.wait_for(background, 2)
        self.assertEqual(self.provider.calls, 1)

    async def test_two_agents_share_connection_and_keep_response_ownership(self):
        socket, _ = self.connect()
        await self.blocked_cognition(socket)
        second = _event_request()
        second["request_id"], second["trace_id"] = "request_02", "trace_02"
        for value in (second["run"], second["context"]):
            value["run_id"], value["agent_entity_id"] = "run_02", "npc_cafe_02"
        second["trigger"]["agent_entity_id"] = "npc_cafe_02"
        second["context"]["perception"]["observer_entity_id"] = "npc_cafe_02"
        second["recall_identity"]["observer"]["entity_id"] = "npc_cafe_02"
        socket.message(second)
        socket.message({"message_type": "ping"})
        self.assertEqual((await socket.result())["message_type"], "pong")
        self.assertEqual(self.provider.calls, 1)
        self.provider.release.set()
        responses = {value["request_id"]: value for value in [await socket.result(), await socket.result()]}
        self.assert_correlation(responses["request_01"])
        self.assert_correlation(responses["request_02"], second)

    async def test_capacity_is_bounded_and_receive_remains_available(self):
        with patch.object(self.main, "MAX_CONNECTION_COGNITIONS", 2):
            socket, _ = self.connect()
            await self.blocked_cognition(socket)
            for index in (2, 3):
                request = _event_request()
                request["request_id"] = f"request_0{index}"
                socket.message(request)
            rejected = await socket.result()
            self.assert_correlation(rejected, request)
            self.assertEqual(rejected["reason"], "COGNITIVE_REQUEST_FAILED")
            socket.message({"message_type": "ping"})
            self.assertEqual((await socket.result())["message_type"], "pong")
            self.provider.release.set()
            accepted = [await socket.result(), await socket.result()]
            self.assertEqual({v["request_id"] for v in accepted}, {"request_01", "request_02"})
            self.assertEqual(self.provider.calls, 2)
            socket.message(request)
            self.assertEqual((await socket.result())["message_type"], "cognitive_response")

    async def test_async_errors_preserve_safe_reason_and_all_correlation(self):
        cases = [
            ("not-json", None, "COGNITIVE_REQUEST_FAILED"),
            ('{"decision_kind":"ACTION_PROPOSAL","action_proposal":'
             '{"action_id":"bring_object","bindings":{"target":"cup_01","destination":"player_01"}}}',
             None, "INVALID_ACTION_PROPOSAL"),
            ('{"decision_kind":"NO_ACTION"}', ProviderError("test-private-detail"), "COGNITIVE_REQUEST_FAILED"),
        ]
        for output, error, reason in cases:
            with self.subTest(reason=reason, error=error):
                self.provider.output, self.provider.error = output, error
                self.provider.release.set()
                socket, task = self.connect()
                socket.message(_event_request())
                response = await socket.result()
                self.assert_correlation(response)
                self.assertEqual(response["reason"], reason)
                self.assertNotIn("test-private-detail", json.dumps(response))
                socket.disconnect()
                await asyncio.wait_for(task, 2)

    async def test_concurrent_pong_and_cognition_use_one_writer(self):
        socket, _ = self.connect()
        await self.blocked_cognition(socket)
        socket.send_release.clear()
        socket.message({"message_type": "ping"})
        await asyncio.wait_for(socket.send_entered.wait(), 2)
        self.provider.release.set()
        await asyncio.wait_for(self.provider.finished.wait(), 2)
        socket.send_release.set()
        self.assertEqual((await socket.result())["message_type"], "pong")
        self.assertEqual((await socket.result())["message_type"], "cognitive_response")

    async def test_send_failure_is_observed_without_retry_or_unhandled_task_exception(self):
        socket, task = self.connect()
        await self.blocked_cognition(socket)
        socket.fail_send = True
        with self.assertLogs("embodiednpc.backend", level="WARNING") as logs:
            self.provider.release.set()
            await asyncio.wait_for(socket.send_entered.wait(), 2)
            socket.disconnect()
            await asyncio.wait_for(task, 2)
        self.assertTrue(any("Cognitive task send failed" in message for message in logs.output))
        self.assertEqual(socket.sent, [])
