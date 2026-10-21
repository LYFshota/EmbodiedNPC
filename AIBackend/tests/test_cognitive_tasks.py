"""Offline phase-two cognition and real WebSocket-handler contracts, private DBs only."""
import asyncio
import json
import sqlite3
import tempfile
import threading
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from pydantic import ValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_tasks import CognitiveTasks
from app.cognitive_step_models import RecallOutcome
from app.contracts import CognitiveRequest, CognitiveResponse
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import ModelResult
from app.sqlite_database import SQLiteDatabase
from app.task_service import TaskService
from app.task_repository import TaskRepository
from app.task_models import TaskError
from tests.test_action_proposal_validation import _context


def request(*, number="1", language="zh-CN", event_type="player.utterance", owner="cafe.merchant.001", player="synthetic-task-player"):
    context = _context(action_id="prepare_drink").model_dump(mode="json")
    context["capabilities"]["available_actions"][0]["bindings"] = [dict(
        name="drink_type", kind="enum", required=True, enum_values=["latte", "cappuccino", "americano"])]
    context["current_event"]["event_type"] = event_type
    context["current_event"]["payload"] = [dict(key="text", value="下次来时给我准备拿铁，可以吗？")]
    value = dict(message_type="cognitive_request", schema_version="1.1", request_id="request_"+number,
        trace_id="trace_"+number, run=dict(run_id="run_01", agent_entity_id="npc_cafe_01", trigger_id="trigger_01",
        trigger_type="event", started_at=10.0), trigger=context["trigger"], context=context, reply_language=language,
        task_runtime=dict(command_id="command_"+number, client_session_id="session_1", visit_id="session_1", scene_instance_id="scene_1"),
        recall_identity=dict(observer=dict(entity_id="npc_cafe_01", kind="npc", identity_namespace="npc", identity_type="Character", persistent_id=owner),
            requester_entity_id="player_01", current_subjects=[dict(entity_id="player_01", kind="player", identity_namespace="player", identity_type="Player", persistent_id=player)]))
    return CognitiveRequest.model_validate(value)


ACCEPT = dict(decision_kind="DIRECT_RESPONSE", utterance="可以，下次来时我会为你准备拿铁。", future_commitment=True,
              task_operation=dict(op="create_task", source="requester", goal=dict(kind="serve_drink", drink_type="latte"), trigger_kind="next_visit"))
DECLINE = dict(decision_kind="DIRECT_RESPONSE", utterance="抱歉，我不能接受这个约定。", future_commitment=False)


class Scripted:
    name = "scripted"
    model = "offline"
    def __init__(self, scripts): self.scripts, self.requests = scripts, []
    async def generate(self, model):
        self.requests.append(model)
        script = self.scripts[len(self.requests)-1]
        if callable(script): script = script(model)
        return ModelResult(text=json.dumps(script, ensure_ascii=False))


def task_data(model):
    return json.loads(model.messages[-1].content.split("TASK STORE CONTEXT JSON: ")[1].splitlines()[0])


class CognitionTaskTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.path = Path(tempfile.mkdtemp(prefix="cognitive_tasks_")) / "private.sqlite3"
        self.database = SQLiteDatabase(self.path)
        self.service = TaskService(TaskRepository(self.database.connect))
        self.tasks = CognitiveTasks(self.service, expected_player_uid="synthetic-task-player")

    def entry(self, scripts, **kwargs):
        self.provider = Scripted(scripts)
        return CognitiveBackendEntry(self.provider, PrioritizedModelExecutor(), tasks=self.tasks, context_length=16384, **kwargs)

    async def test_accept_commit_before_reply_and_trusted_envelope(self):
        req = request()
        result = await self.entry([ACCEPT]).run(req)
        self.assertTrue(result.task_receipt.committed)
        self.assertEqual(result.decision.utterance, ACCEPT["utterance"])
        self.assertNotIn("task_operation", result.decision.model_dump())
        self.assertEqual(self.service.get_task(self.tasks.prepare(req).scope, result.task_receipt.task_id).version, 1)
        envelope = CognitiveResponse(schema_version="1.1", request_id=req.request_id, trace_id=req.trace_id,
            run_id=req.run.run_id, agent_entity_id=req.run.agent_entity_id, decision=result.decision,
            task_receipt=result.task_receipt, task_result="committed")
        self.assertEqual(envelope.task_receipt.command_id, req.task_runtime.command_id)

    async def test_decline_and_hypothetical_do_not_create(self):
        result = await self.entry([DECLINE]).run(request())
        self.assertIsNone(result.task_receipt)
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(request()).scope).tasks, ())

    async def test_database_failure_suppresses_original_promise_in_both_languages(self):
        with closing(self.database.connect()) as db, db:
            db.execute("CREATE TRIGGER reject_task BEFORE INSERT ON npc_tasks BEGIN SELECT RAISE(ABORT,'SYNTHETIC_FAILURE'); END")
        for language in ("zh-CN", "en-US"):
            with self.subTest(language=language):
                result = await self.entry([ACCEPT]).run(request(language=language))
                self.assertFalse(result.task_receipt.committed)
                self.assertNotEqual(result.decision.utterance, ACCEPT["utterance"])
                self.assertIn("save" if language == "en-US" else "未能保存", result.decision.utterance)
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(request()).scope).tasks, ())

    async def test_retransmission_and_restart_recover_without_another_inference(self):
        req = request()
        first = await self.entry([ACCEPT]).run(req)
        self.tasks = CognitiveTasks(TaskService(TaskRepository(SQLiteDatabase(self.path).connect)), expected_player_uid="synthetic-task-player")
        recovered = await self.entry([]).run(req)
        self.assertEqual(self.provider.requests, [])
        self.assertTrue(recovered.task_receipt.recovered)
        self.assertEqual(recovered.task_receipt.task_id, first.task_receipt.task_id)
        self.assertEqual(len(self.service.query_tasks(self.tasks.prepare(req).scope).tasks), 1)

    async def test_same_command_changed_input_conflicts_instead_of_recovering(self):
        req = request()
        await self.entry([ACCEPT]).run(req)
        modified = req.model_dump(mode="json")
        modified["recall_identity"] = req.recall_identity.model_dump(mode="json")
        modified["task_runtime"] = req.task_runtime.model_dump(mode="json")
        modified["context"]["current_event"]["payload"][0]["value"] = "换一杯美式"
        with self.assertRaisesRegex(TaskError, "TASK_COMMAND_PAYLOAD_CONFLICT"):
            await self.entry([]).run(CognitiveRequest.model_validate(modified))

    async def test_active_read_and_answer_use_two_inferences(self):
        await self.entry([ACCEPT]).run(request())
        def answer(model):
            data = task_data(model)
            self.assertEqual(data["tasks"][0]["goal"]["drink_type"], "latte")
            self.assertNotIn("persistent_id", json.dumps(data))
            return dict(decision_kind="DIRECT_RESPONSE", utterance="你还有一个下次来时准备拿铁的约定。", future_commitment=False)
        result = await self.entry([dict(decision_kind="TASK_READ", task_query=dict(op="query_tasks", statuses=["waiting"])), answer]).run(request(number="read"))
        self.assertEqual(len(self.provider.requests), 2)
        self.assertIn("拿铁", result.decision.utterance)
        self.assertNotIn("TaskReadStep", self.provider.requests[1].response_schema["$defs"])
        self.assertNotIn("MemoryRecallStep", self.provider.requests[1].response_schema["$defs"])

    async def test_owner_and_player_read_isolation(self):
        await self.entry([ACCEPT]).run(request())
        for req in (request(number="other_owner", owner="cafe.other.001"), request(number="other_player", player="other-player")):
            if req.recall_identity.current_subjects[0].persistent_id == "other-player":
                with self.assertRaises(TaskError): self.tasks.prepare(req)
            else:
                self.assertEqual(self.tasks.read(self.tasks.prepare(req))["tasks"], [])
        context = self.tasks.prepare(request(number="read"))
        from app.cognitive_task_models import TaskReadPlan
        with self.assertRaises(TaskError):
            self.tasks.read(context, TaskReadPlan(task_ref="invented_ref"))

    async def test_task_read_then_recall_or_read_is_rejected_without_third_inference(self):
        for second in (dict(decision_kind="TASK_READ", task_query={}), dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="history"))):
            with self.subTest(second=second), self.assertRaises(ValidationError):
                await self.entry([dict(decision_kind="TASK_READ", task_query={}), second]).run(request())
            self.assertEqual(len(self.provider.requests), 2)

    async def test_recall_then_task_read_rejected_and_same_language_snapshot(self):
        class Recall:
            def prepare(self, req): return self
            def model_visible_payload(self): return {}
            def recall(self, catalog, plan): return RecallOutcome(status="NO_MEMORY_FOUND")
        with self.assertRaises(ValidationError):
            await self.entry([dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="history")),
                dict(decision_kind="TASK_READ", task_query={})], memory_recall=Recall()).run(request(language="en-US"))
        self.assertEqual(len(self.provider.requests), 2)
        for model in self.provider.requests:
            self.assertIn("reply_language = en-US", model.messages[-1].content)

    async def test_edit_defer_cancel_and_recover_current_status(self):
        first = await self.entry([ACCEPT]).run(request())
        def operation(change, **fields):
            def choose(model):
                task = task_data(model)["tasks"][0]
                return dict(decision_kind="DIRECT_RESPONSE", utterance="约定已更新。", future_commitment=False,
                    task_operation=dict(op="update_task", task_ref=task["task_ref"], expected_version=task["version"],
                                        change=change, reason="玩家修改约定", **fields))
            return choose
        edit = await self.entry([operation("edit", goal=dict(kind="serve_drink", drink_type="americano"))]).run(request(number="edit"))
        deferred = await self.entry([operation("defer", trigger_kind="when_idle")]).run(request(number="defer"))
        cancelled = await self.entry([operation("cancel")]).run(request(number="cancel"))
        self.assertEqual((edit.task_receipt.task_version, deferred.task_receipt.task_version, cancelled.task_receipt.task_status), (2, 3, "cancelled"))
        recovered = await self.entry([]).run(request())
        self.assertEqual(recovered.task_receipt.task_status, "cancelled")
        self.assertNotIn("下次来时我会", recovered.decision.utterance)
        self.assertEqual(recovered.task_receipt.task_id, first.task_receipt.task_id)

    async def test_language_hard_limits_and_ids_are_unchanged(self):
        text = "I will remember this agreement for your next visit. " * 3 + "Thank you."
        result = await self.entry([{**ACCEPT, "utterance": text}]).run(request(language="en-US"))
        self.assertEqual(result.decision.utterance, text)
        self.assertEqual(result.task_receipt.reply_language, "en-US")
        self.assertEqual(self.service.get_task(self.tasks.prepare(request(language="en-US")).scope,
                                             result.task_receipt.task_id).goal.drink_type, "latte")
        with self.assertRaises(ValidationError):
            await self.entry([{**ACCEPT, "utterance": "字"*151}]).run(request(number="long"))

    async def test_unsaved_commitment_and_injected_status_rejected(self):
        for response in ({**DECLINE, "future_commitment": True},
                         {**ACCEPT, "task_operation": {**ACCEPT["task_operation"], "task_id": "model_chosen"}},
                         {**ACCEPT, "target_daily_mode": "work"}):
            with self.subTest(response=response), self.assertRaises(ValidationError):
                await self.entry([response]).run(request())
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(request()).scope).tasks, ())

    async def test_silent_sources_and_legacy_have_no_task_permission(self):
        for event in ("npc.dialogue_continue", "entity.became_visible"):
            self.assertIsNone(self.tasks.prepare(request(event_type=event)))
        original = request()
        legacy = original.model_dump(mode="json")
        legacy["schema_version"] = "1.0"
        legacy["recall_identity"] = original.recall_identity.model_dump(mode="json")
        legacy = CognitiveRequest.model_validate(legacy)
        with self.assertRaises(ValidationError): await self.entry([ACCEPT]).run(legacy)
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(original).scope).tasks, ())

    async def test_world_action_permission_not_expanded(self):
        req = request(event_type="entity.became_visible")
        with self.assertRaises(ValueError):
            await self.entry([dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="prepare_drink", bindings=dict(drink_type="latte")))]).run(req)

    async def test_cancellation_while_commit_runs_suppresses_reply_and_recovers(self):
        entered, release = threading.Event(), threading.Event()
        real_mutate = self.tasks.mutate
        def delayed(*args):
            entered.set()
            release.wait(timeout=5)
            return real_mutate(*args)
        self.tasks.mutate = delayed
        run = asyncio.create_task(self.entry([ACCEPT]).run(request()))
        await asyncio.to_thread(entered.wait, 2)
        self.assertTrue(entered.is_set())
        run.cancel()
        with self.assertRaises(asyncio.CancelledError): await run
        release.set()
        # Synchronize via the committed command, not a timing assertion.
        for _ in range(100):
            recovered = await asyncio.to_thread(self.tasks.recover, request(), self.tasks.prepare(request()))
            if recovered is not None: break
            await asyncio.sleep(.01)
        self.assertIsNotNone(recovered)
        self.assertTrue(recovered[1].committed)

    async def test_runtime_session_reconnect_conflict_and_visit_validation(self):
        first, second = object(), object()
        req = request()
        self.tasks.bind(first, req)
        self.tasks.bind(second, req)  # Same process reconnect, same Visit.
        other = req.model_dump(mode="json")
        other["recall_identity"] = req.recall_identity.model_dump(mode="json")
        other["task_runtime"] = {**req.task_runtime.model_dump(), "client_session_id": "session_2", "visit_id": "session_2"}
        with self.assertRaises(TaskError): self.tasks.bind(first, CognitiveRequest.model_validate(other))
        self.tasks.disconnect(second)
        self.tasks.bind(first, CognitiveRequest.model_validate(other))
        other["task_runtime"]["visit_id"] = "different"
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(other)

    async def test_task_budget_drops_optional_tasks_and_preserves_input_and_capabilities(self):
        from app.model_requests import CognitiveModelRequestFactory
        from app.context_budget_allocator import ContextBudgetPolicy
        from app.context_token_estimator import ContextTokenEstimator
        req = request()
        # Oversized trusted input exercises fail-safe allocation, never bypassing it.
        projection = dict(status="OK", has_more=False, available_goal_kinds=["serve_drink"],
                          tasks=[dict(task_ref="t_budget", description="隔离压力样本" * 5000)])
        model = CognitiveModelRequestFactory.create_event(req, task_context=projection, knowledge_context_limit=16384)
        visible = task_data(model)
        self.assertEqual(visible["tasks"], [])
        self.assertEqual(visible["status"], "EVIDENCE_BUDGET_EXHAUSTED")
        self.assertTrue(visible["has_more"])
        self.assertIn("下次来时给我准备拿铁", model.messages[-1].content)
        self.assertIn("prepare_drink", model.messages[-1].content)
        wire = json.dumps(model.model_dump(mode="json"), ensure_ascii=False, separators=(",", ":"))
        self.assertLessEqual(ContextTokenEstimator().estimate_text(wire), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)

    async def test_self_plan_and_unsupported_drink_are_host_validated(self):
        req = request()
        context = req.context.model_dump(mode="json")
        context["capabilities"]["available_actions"].append(dict(action_id="clean_counter", description="Assigned counter cleaning",
            bindings=[dict(name="target", kind="entity_ref", required=True)]))
        req = req.model_copy(update={"context": type(req.context).model_validate(context)})
        result = await self.entry([{**ACCEPT, "task_operation": dict(op="create_task", source="self",
            goal=dict(kind="clean_assigned_counter"), trigger_kind="when_idle")}]).run(req)
        task = self.service.get_task(self.tasks.prepare(req).scope, result.task_receipt.task_id)
        self.assertIsNone(task.player)
        self.assertEqual(task.source_kind, "self_initiated")
        context["capabilities"]["available_actions"][0]["bindings"][0]["enum_values"] = ["americano"]
        restricted = request(number="restricted").model_copy(update={"context": type(req.context).model_validate(context)})
        rejected = await self.entry([ACCEPT]).run(restricted)
        self.assertFalse(rejected.task_receipt.committed)

    async def test_inflight_cancel_confirms_request_not_world_cancellation(self):
        from app.task_models import ExecutionRequest
        first = await self.entry([{**ACCEPT, "task_operation": {**ACCEPT["task_operation"], "trigger_kind": "when_idle"}}]).run(request())
        scope = self.tasks.prepare(request()).scope
        self.service.reserve_execution(scope, "simulate_claim", first.task_receipt.task_id, ExecutionRequest(
            expected_version=1, scene_instance_id="scene_1", proposal_id="proposal", trace_id="trace", actor_entity_id="npc_cafe_01",
            world_revision=1, player_entity_id="player_01"), is_idle=True)
        def cancel(model):
            task = task_data(model)["tasks"][0]
            return dict(decision_kind="DIRECT_RESPONSE", utterance="已经取消了。", future_commitment=False,
                task_operation=dict(op="update_task", task_ref=task["task_ref"], expected_version=task["version"], change="cancel", reason="cancel"))
        result = await self.entry([cancel]).run(request(number="cancel"))
        self.assertEqual(result.task_receipt.outcome, "cancellation_requested")
        self.assertEqual(result.task_receipt.task_status, "executing")
        self.assertEqual(result.decision.utterance, "取消请求已保存，原执行仍待核对。")

    async def test_silent_and_continuation_schemas_reject_task_fields(self):
        from app.cognitive_step_models import SystemModeStep, AutonomousWorkStep, DialogueContinuationStep
        for schema in (SystemModeStep, AutonomousWorkStep, DialogueContinuationStep):
            with self.subTest(schema=schema), self.assertRaises(ValidationError):
                schema.model_validate(ACCEPT)

    async def test_formal_mock_provider_handles_new_schema_without_task_writes(self):
        from app.model_requests import create_mock_provider
        entry = CognitiveBackendEntry(create_mock_provider(), PrioritizedModelExecutor(), tasks=self.tasks, context_length=16384)
        result = await entry.run(request(language="en-US"))
        self.assertEqual(result.decision.utterance, "Welcome! What would you like?")
        self.assertIsNone(result.task_receipt)


class TaskProtocolTests(CognitionTaskTests):
    # Only protocol methods are selected by the isolated runner, avoiding duplicate inheritance runs.
    async def test_protocol_commit_and_readonly_recovery_over_real_handler(self):
        from app import m3_main
        from tests.test_cognitive_websocket import SocketHarness
        entry = self.entry([ACCEPT])
        original = request()
        payload = original.model_dump(mode="json")
        payload["recall_identity"] = original.recall_identity.model_dump(mode="json")
        payload["task_runtime"] = original.task_runtime.model_dump(mode="json")
        with patch.object(m3_main, "cognitive_entry", entry), patch.object(m3_main, "task_cognition", self.tasks), patch.object(m3_main, "demo_player_uid", "synthetic-task-player"):
            socket = SocketHarness()
            run = asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
            try:
                socket.message(payload)
                first = await socket.result()
                self.assertEqual(first["message_type"], "cognitive_response", first)
                self.assertEqual(first["schema_version"], "1.1")
                self.assertTrue(first["task_receipt"]["committed"])
                (self.path.parent / "backend-protocol-pair.json").write_text(
                    json.dumps(dict(request=payload, response=first), ensure_ascii=False), encoding="utf-8")
                socket.message(dict(message_type="task_control", schema_version="1.1", operation="query", original_request=payload))
                second = await socket.result()
                self.assertTrue(second["task_receipt"]["recovered"])
                self.assertEqual(second["task_receipt"]["task_id"], first["task_receipt"]["task_id"])
                self.assertEqual(len(self.provider.requests), 1)
            finally:
                socket.disconnect()
                await asyncio.wait_for(run, 3)

    async def test_protocol_lost_response_reconnect_recovers_without_mutation(self):
        from app import m3_main
        from tests.test_cognitive_websocket import SocketHarness
        entry = self.entry([ACCEPT])
        req = request()
        payload = {**req.model_dump(mode="json"), "recall_identity": req.recall_identity.model_dump(mode="json"),
                   "task_runtime": req.task_runtime.model_dump(mode="json")}
        with patch.object(m3_main, "cognitive_entry", entry), patch.object(m3_main, "task_cognition", self.tasks), patch.object(m3_main, "demo_player_uid", "synthetic-task-player"):
            first = SocketHarness()
            first.fail_send = True
            running = asyncio.create_task(m3_main.websocket_endpoint(first.socket))
            first.message(payload)
            await asyncio.wait_for(first.send_entered.wait(), 3)
            first.disconnect()
            await asyncio.wait_for(running, 3)
            self.assertEqual(first.sent, [])
            second = SocketHarness()
            reconnected = asyncio.create_task(m3_main.websocket_endpoint(second.socket))
            try:
                second.message(dict(message_type="task_control", schema_version="1.1", operation="query", original_request=payload))
                result = await second.result()
                self.assertTrue(result["task_receipt"]["recovered"])
                self.assertTrue(result["task_receipt"]["committed"])
                self.assertEqual(len(self.provider.requests), 1)
                self.assertEqual(len(self.service.query_tasks(self.tasks.prepare(req).scope).tasks), 1)
            finally:
                second.disconnect()
                await asyncio.wait_for(reconnected, 3)

    async def test_protocol_legacy_has_no_new_fields(self):
        from app import m3_main
        from tests.test_cognitive_websocket import SocketHarness
        original = request()
        payload = original.model_dump(mode="json")
        payload["schema_version"] = "1.0"
        payload["recall_identity"] = original.recall_identity.model_dump(mode="json")
        with patch.object(m3_main, "cognitive_entry", self.entry([dict(decision_kind="DIRECT_RESPONSE", utterance="你好。")])):
            socket = SocketHarness()
            run = asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
            try:
                socket.message(payload)
                response = await socket.result()
                self.assertEqual(response["message_type"], "cognitive_response", response)
                self.assertNotIn("task_receipt", response)
                self.assertNotIn("task_result", response)
            finally:
                socket.disconnect()
                await asyncio.wait_for(run, 3)
