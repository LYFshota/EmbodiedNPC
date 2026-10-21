import asyncio
import json
from unittest.mock import AsyncMock, Mock, patch

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.providers import ModelResult
from tests import test_dialogue_continuation as contracts
from tests.test_cognitive_request import _event_request
from tests.test_cognitive_websocket import SocketHarness


class CancellationTests(contracts.ContinuationFixture):
    async def asyncSetUp(self):
        from app import m3_main
        self.main = m3_main
        self.connections = []
        self.entered, self.cancelled, self.release = asyncio.Event(), asyncio.Event(), asyncio.Event()
        owner = self
        class Provider:
            calls = 0
            async def generate(self, request):
                self.calls += 1
                if self.calls == 1:
                    owner.entered.set()
                    try: await owner.release.wait()
                    except asyncio.CancelledError:
                        owner.cancelled.set()
                        raise
                return ModelResult(text='{"decision_kind":"NO_ACTION"}')
        self.provider = Provider()
        self.patch = patch.object(self.main, "cognitive_entry", CognitiveBackendEntry(
            self.provider, self._executor(), experience_ledger=self.ledger, context_length=16384))
        self.patch.start()
        self.addAsyncCleanup(self.patch.stop)
        # Bind the same trusted deployment UID as this isolated conversation.
        # Do not bypass the production single-player admission boundary.
        scope_patch=patch.object(self.main,'demo_player_uid','test_player')
        scope_patch.start()
        self.addAsyncCleanup(scope_patch.stop)

    def _executor(self):
        from app.local_model_scheduler import PrioritizedModelExecutor
        return PrioritizedModelExecutor()

    async def asyncTearDown(self):
        for socket, task in self.connections:
            socket.disconnect()
        await asyncio.wait_for(asyncio.gather(*(t for _, t in self.connections)), 2)
        self.patch.stop()
        self.assertFalse(any(t.get_name().startswith("cognition:") for t in asyncio.all_tasks()))

    def connect(self):
        socket = SocketHarness()
        task = asyncio.create_task(self.main.websocket_endpoint(socket.socket))
        self.connections.append((socket, task))
        return socket

    def cancel(self, request):
        return dict(message_type="cognitive_cancel", schema_version="1.0", request_id=request["request_id"],
                    trace_id=request["trace_id"], run_id=request["run"]["run_id"],
                    agent_entity_id=request["run"]["agent_entity_id"])

    async def ping(self, socket):
        socket.message(dict(message_type="ping", request_id="ping_sync"))
        self.assertEqual("pong", (await socket.result())["message_type"])

    async def test_exact_cancel_releases_provider_and_has_no_response_or_experience(self):
        socket = self.connect()
        request = self.continuation()
        socket.message(request)
        await asyncio.wait_for(self.entered.wait(), 2)
        socket.message({**self.cancel(request), "trace_id": "wrong_trace"})
        await self.ping(socket)
        self.assertFalse(self.cancelled.is_set())
        socket.message(self.cancel(request))
        await asyncio.wait_for(self.cancelled.wait(), 2)
        await self.ping(socket)
        socket.message(request)  # A late request send cannot resurrect a cancelled ticket.
        await self.ping(socket)
        self.assertEqual(1, self.provider.calls)
        self.assertTrue(all(r["message_type"] == "pong" for r in socket.sent))
        self.assertEqual(2, self.ledger.latest_seq("npc_cafe_01"))

    async def test_cancel_before_admission_never_calls_model(self):
        socket = self.connect()
        request = self.continuation()
        socket.message(self.cancel(request)); socket.message(request)
        await self.ping(socket)
        self.assertEqual(0, self.provider.calls)

    async def test_foreground_preempts_without_waiting_for_autonomous_release(self):
        socket = self.connect()
        socket.message(self.continuation())
        await asyncio.wait_for(self.entered.wait(), 2)
        foreground = _event_request()  # Perception cognition needs no dialogue input barrier.
        foreground['recall_identity']['current_subjects'][0]['persistent_id']='test_player'
        socket.message(foreground)
        await asyncio.wait_for(self.cancelled.wait(), 2)
        replies = [await socket.result(), await socket.result()]
        self.assertTrue(any(r.get("request_id") == foreground["request_id"] and
                            r["message_type"] == "cognitive_response" for r in replies))
        self.assertEqual(2, self.provider.calls)
        self.assertFalse(self.release.is_set())

    async def test_cancel_on_other_connection_cannot_cancel_owner(self):
        socket, other = self.connect(), self.connect()
        request = self.continuation(); socket.message(request)
        await asyncio.wait_for(self.entered.wait(), 2)
        other.message(self.cancel(request)); await self.ping(other)
        self.assertFalse(self.cancelled.is_set())
        self.release.set()
        self.assertEqual("cognitive_response", (await socket.result())["message_type"])
        socket.message(request); await self.ping(socket)  # Completed replay doesn't generate again.
        self.assertEqual(1, self.provider.calls)

    async def test_disconnect_cancels_autonomous_provider(self):
        socket = self.connect(); socket.message(self.continuation())
        await asyncio.wait_for(self.entered.wait(), 2)
        socket.disconnect()
        await asyncio.wait_for(self.connections[0][1], 2)
        await asyncio.wait_for(self.cancelled.wait(), 2)
        self.assertEqual([], socket.sent)
        self.connections.clear()


class ServiceZoneCancellationTests(CancellationTests):
    """Reuse the real in-process websocket/cancellation fixtures for the new bounded source."""
    async def test_system_mode_is_silent_without_dialogue_ack_or_recall_dependency(self):
        provider = Mock(generate=AsyncMock(return_value=ModelResult(text='{"decision_kind":"NO_ACTION"}')))
        ledger, recall = Mock(), Mock()
        entry = CognitiveBackendEntry(provider, self._executor(), experience_ledger=ledger,
                                      memory_recall=recall, context_length=16384)
        result = await entry.run(CognitiveRequest.model_validate(self.continuation()))
        self.assertEqual("NO_ACTION", result.decision.decision_kind)
        self.assertIsNone(result.decision.utterance)
        self.assertEqual(1, provider.generate.await_count)
        self.assertEqual([], ledger.mock_calls)
        self.assertEqual([], recall.mock_calls)
        schema = json.dumps(provider.generate.call_args.args[0].response_schema)
        self.assertNotIn("MemoryRecallStep", schema)
        self.assertNotIn("DirectResponseStep", schema)

    async def test_system_mode_rejects_dialogue_recall_and_world_action_outputs_once(self):
        for output in (dict(decision_kind="DIRECT_RESPONSE", utterance="不能作为静默模式结果"),
                       dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="旧事")),
                       dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="bring_object",
                            bindings=dict(target="cup_01", destination="player_01")))):
            with self.subTest(decision_kind=output["decision_kind"]):
                provider = Mock(generate=AsyncMock(return_value=ModelResult(text=json.dumps(output))))
                entry = CognitiveBackendEntry(provider, self._executor(), context_length=16384)
                with self.assertRaises(ValueError):
                    await entry.run(CognitiveRequest.model_validate(self.continuation()))
                self.assertEqual(1, provider.generate.await_count)

    def continuation(self):
        from tests.test_daily_behavior_cognition import service_zone_request
        value = service_zone_request().model_dump()
        value["recall_identity"] = dict(observer={**self.participants[0], "kind": "npc"},
            requester_entity_id="player_01", current_subjects=[{**self.participants[1], "kind": "player"}])
        return value
