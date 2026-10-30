"""Phase-one service tests: temporary SQLite only, synthetic trusted receipts.

No Backend entry point, provider, Unity process, or production path is imported.
The caller must isolate paths before importing tests (see stage-one test report).
"""

from __future__ import annotations

import sqlite3
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from pydantic import ValidationError

from app.drink_types import DRINK_TYPE_IDS
from app.sqlite_database import LATEST_SCHEMA_VERSION, SQLiteDatabase
from app.task_models import (
    CleanAssignedCounterGoal, CounterReceipt, DrinkReceipt, ExecutionRequest,
    ServeDrinkGoal, TaskCreate, TaskError, TaskIdentity, TaskQuery, TaskRecord,
    TaskScope, TaskSource, TaskUpdate, TrustedTaskResult,
)
from app.task_repository import TaskRepository
from app.task_schema import TASK_SCHEMA_VERSION, migrate_tasks
from app.task_service import TaskService
from tests.test_schema_retirement import fingerprint, objects, seed_row


OWNER = TaskIdentity(identity_namespace="npc", identity_type="Character", persistent_id="cafe.barista.001")
PLAYER = TaskIdentity(identity_namespace="player", identity_type="Player", persistent_id="cafe-local-player-001")
OTHER_PLAYER = PLAYER.model_copy(update={"persistent_id": "another-player"})
OTHER_OWNER = OWNER.model_copy(update={"persistent_id": "cafe.customer.001"})
CAPABILITIES = ("serve_drink", "clean_assigned_counter")


def scope(visit="visit_1", *, owner=OWNER, player=PLAYER, capabilities=CAPABILITIES):
    return TaskScope(owner=owner, player=player, visit_id=visit, allowed_goal_kinds=capabilities)


def create_request(*, drink="latte", trigger="next_visit", source="player_request", event="event_1", clean=False):
    return TaskCreate(source_kind=source, source=TaskSource(event_id=event),
                      goal=CleanAssignedCounterGoal() if clean else ServeDrinkGoal(drink_type=drink),
                      trigger_kind=trigger)


def execution_request(task, suffix="1"):
    return ExecutionRequest(expected_version=task.version, scene_instance_id="scene_1",
                            proposal_id="proposal_" + suffix, trace_id="trace_" + suffix,
                            actor_entity_id="barista", world_revision=10,
                            **({"player_entity_id": "player"} if task.goal.kind == "serve_drink"
                               else {"target_entity_id": "counter", "condition_revision": 3}))


def synthetic_result(task, *, status="succeeded", receipt_id="receipt_1", **changes):
    execution = task.execution
    request = execution.request
    receipt_common = dict(receipt_id=receipt_id, actor_entity_id=request.actor_entity_id,
                          proposal_id=request.proposal_id, trace_id=request.trace_id, world_revision=11)
    receipt = (DrinkReceipt(**receipt_common, player=task.player, player_entity_id=request.player_entity_id,
                            drink_type=task.goal.drink_type) if task.goal.kind == "serve_drink" else
               CounterReceipt(**receipt_common, target_entity_id=request.target_entity_id,
                              condition_revision=request.condition_revision))
    value = dict(attempt_id=execution.attempt_id, scene_instance_id=request.scene_instance_id,
                 proposal_id=request.proposal_id, trace_id=request.trace_id, actor_entity_id=request.actor_entity_id,
                 outcome_id="outcome_" + status, goal_type="prepare_drink" if task.goal.kind == "serve_drink" else "clean_counter",
                 status=status, effect="committed" if status == "succeeded" else "unknown" if status == "uncertain" else "not_committed",
                 world_revision=11, receipt=receipt if status == "succeeded" else None,
                 failure_code=None if status == "succeeded" else "SYNTHETIC_" + status.upper())
    value.update(changes)
    return TrustedTaskResult.model_validate(value)


class TaskServiceTests(unittest.TestCase):
    def setUp(self):
        # Intentionally retained under the runner's isolated temp root for audit.
        self.path = Path(tempfile.mkdtemp(prefix="task_service_")) / "tasks.sqlite3"
        self.db = SQLiteDatabase(self.path)
        self.service = TaskService(TaskRepository(self.db.connect), clock=lambda: 100.0)
        self.context = scope()
        self.next_context = scope("visit_2")

    def create(self, command="create_1", context=None, **kwargs):
        return self.service.create_task(context or self.context, command, create_request(**kwargs)).task

    def running(self, *, clean=False, suffix="1"):
        task = self.create("create_" + suffix, clean=clean)
        task = self.service.reserve_execution(self.next_context, "reserve_" + suffix, task.task_id,
                                              execution_request(task, suffix)).task
        return self.service.start_execution(self.next_context, "start_" + suffix, task.task_id,
                                            expected_version=task.version, attempt_id=task.execution.attempt_id).task

    def error(self, code, call):
        with self.assertRaises(TaskError) as raised:
            call()
        self.assertEqual(raised.exception.code, code)

    def test_every_registered_drink_persists_executes_and_requires_matching_receipt(self):
        for number, drink in enumerate(DRINK_TYPE_IDS):
            with self.subTest(drink=drink):
                suffix = str(number)
                task = self.create("create_drink_" + suffix, drink=drink, event="drink_" + suffix)
                reopened = TaskService(TaskRepository(self.db.connect), clock=lambda: 100.0)
                self.assertEqual(reopened.get_task(self.context, task.task_id).goal.drink_type, drink)
                task = self.service.reserve_execution(self.next_context, "reserve_drink_" + suffix, task.task_id,
                    execution_request(task, suffix)).task
                task = self.service.start_execution(self.next_context, "start_drink_" + suffix, task.task_id,
                    expected_version=task.version, attempt_id=task.execution.attempt_id).task
                result = synthetic_result(task, receipt_id="receipt_drink_" + suffix)
                wrong = "cappuccino" if drink == "latte" else "latte"
                mismatch = result.model_copy(update={"receipt": result.receipt.model_copy(update={"drink_type": wrong})})
                self.error("TASK_DRINK_RECEIPT_MISMATCH", lambda: self.service.settle_execution(
                    self.next_context, "bad_drink_" + suffix, task.task_id, mismatch))
                self.assertEqual(self.service.get_task(self.next_context, task.task_id).status, "executing")
                settled = self.service.settle_execution(self.next_context, "settle_drink_" + suffix, task.task_id, result).task
                self.assertEqual(settled.status, "completed")
                self.assertEqual(reopened.get_task(self.context, task.task_id).execution.result.receipt.drink_type, drink)

    def test_create_query_update_defer_cancel_and_historical_receipt(self):
        task = self.create()
        self.assertEqual((task.version, task.status), (1, "waiting"))
        self.assertEqual(self.service.get_task(self.context, task.task_id), task)
        self.assertEqual(self.service.query_tasks(self.context).tasks, (task,))
        updated = self.service.update_task(self.context, "update", task.task_id, TaskUpdate(
            expected_version=1, reason="改成美式", goal=ServeDrinkGoal(drink_type="americano"))).task
        self.assertEqual((updated.version, updated.goal.drink_type), (2, "americano"))
        deferred = self.service.defer_task(self.next_context, "defer", task.task_id,
                                          expected_version=2, trigger_kind="next_visit", reason="再等一次来访").task
        self.assertEqual((deferred.version, deferred.schedule.anchor_visit_id), (3, "visit_2"))
        self.assertFalse(self.service.is_eligible(self.next_context, task.task_id))
        self.assertTrue(self.service.is_eligible(scope("visit_3"), task.task_id))
        cancelled = self.service.cancel_task(self.next_context, "cancel", task.task_id,
                                             expected_version=3, reason="玩家取消").task
        self.assertEqual((cancelled.version, cancelled.status), (4, "cancelled"))
        self.assertEqual(self.service.query_tasks(self.context).tasks, ())
        self.assertEqual(self.service.query_tasks(self.context, TaskQuery(statuses=("cancelled",))).tasks, (cancelled,))
        self.assertEqual(self.service.get_command(self.context, "create_1").task, task)
        self.assertEqual(self.service.create_task(self.context, "create_1", create_request()).task, task)
        self.assertEqual(self.service.get_task(self.context, task.task_id), cancelled)
        self.error("TASK_ALREADY_TERMINAL", lambda: self.service.cancel_task(
            self.context, "cancel_again", task.task_id, expected_version=4, reason="重复取消"))

    def test_owner_player_and_command_isolation(self):
        task = self.create()
        for context in (scope(owner=OTHER_OWNER), scope(player=OTHER_PLAYER)):
            with self.subTest(context=context):
                self.assertEqual(self.service.query_tasks(context).tasks, ())
                self.error("TASK_NOT_FOUND_OR_NOT_AUTHORIZED", lambda: self.service.get_task(context, task.task_id))
                self.error("TASK_NOT_FOUND_OR_NOT_AUTHORIZED", lambda: self.service.cancel_task(
                    context, "bad_cancel", task.task_id, expected_version=1, reason="不可跨主体"))
                with self.assertRaises(TaskError):
                    self.service.get_command(context, "create_1")
        self.error("TASK_PLAYER_FILTER_NOT_AUTHORIZED", lambda: self.service.query_tasks(
            self.context, TaskQuery(player=OTHER_PLAYER)))
        own = scope(player=None)
        self.assertEqual(self.service.query_tasks(own, TaskQuery(player=PLAYER)).tasks, (task,))
        self.assertFalse(self.service.is_eligible(scope("visit_2", player=None), task.task_id))
        other = self.create("other_player", context=scope(player=OTHER_PLAYER))
        self.assertNotEqual(other.task_id, task.task_id)
        self.assertEqual(self.service.query_tasks(scope(player=OTHER_PLAYER)).tasks, (other,))

    def test_strict_dtos_reject_state_sql_unknown_fields_and_type_coercion(self):
        valid = create_request().model_dump()
        for mutation in ({"status": "completed"}, {"owner": OWNER.model_dump()},
                         {"goal": {"kind": "arbitrary_sql", "sql": "DROP TABLE memory_records"}},
                         {"trigger_kind": "tomorrow"}, {"source": {"event_id": " ", "sql": "x"}}):
            with self.subTest(mutation=mutation), self.assertRaises(ValidationError):
                self.service.create_task(self.context, "bad_create", {**valid, **mutation})
        task = self.create()
        for version in (True, "1", 1.0):
            with self.subTest(version=version), self.assertRaises(ValidationError):
                self.service.cancel_task(self.context, "bad_version", task.task_id,
                                         expected_version=version, reason="bad")
        forged = create_request().model_copy(update={"goal": {"kind": "unrecognized"}})
        with self.assertRaises(ValidationError):
            self.service.create_task(self.context, "forged", forged)
        with self.assertRaises(ValidationError):
            scope(owner=PLAYER)
        with self.assertRaises(ValidationError):
            create_request(source="self_initiated")
        self.error("TASK_GOAL_NOT_AUTHORIZED", lambda: self.service.create_task(
            scope(capabilities=()), "no_capability", create_request()))
        self.error("TASK_PLAYER_REQUIRED", lambda: self.service.create_task(
            scope(player=None), "no_player", create_request()))

    def test_command_idempotency_conflict_and_active_dedup(self):
        first = self.service.create_task(self.context, "command", create_request())
        self.assertEqual(self.service.create_task(self.context, "command", create_request()), first)
        self.error("TASK_COMMAND_PAYLOAD_CONFLICT", lambda: self.service.create_task(
            self.context, "command", create_request(drink="americano")))
        duplicate = self.service.create_task(self.context, "duplicate", create_request(event="event_2"))
        self.assertEqual((duplicate.outcome, duplicate.task), ("deduplicated", first.task))
        # Another Visit's "next time" is a different future obligation.
        later = self.create("later", context=self.next_context)
        self.assertNotEqual(later.task_id, first.task.task_id)
        self.service.cancel_task(self.context, "cancel", first.task.task_id, expected_version=1, reason="cancel")
        replacement = self.create("replacement")
        self.assertNotEqual(replacement.task_id, first.task.task_id)
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_tasks").fetchone()[0], 3)

    def test_cas_update_and_duplicate_edit_roll_back(self):
        task = self.create()
        other = self.create("other", drink="americano")
        self.error("TASK_ACTIVE_DUPLICATE", lambda: self.service.update_task(self.context, "duplicate_edit", task.task_id,
            TaskUpdate(expected_version=1, reason="edit", goal=other.goal)))
        self.assertEqual(self.service.get_task(self.context, task.task_id), task)
        updated = self.service.defer_task(self.context, "edit", task.task_id, expected_version=1,
                                          trigger_kind="when_idle", reason="等空闲").task
        self.error("TASK_VERSION_CONFLICT", lambda: self.service.cancel_task(
            self.context, "stale", task.task_id, expected_version=1, reason="stale"))
        self.assertEqual(self.service.get_task(self.context, task.task_id), updated)

    def test_query_page_limits_and_owner_self_scope(self):
        tasks = [self.create("create_" + str(i), context=scope("visit_" + str(i))) for i in range(4)]
        page = self.service.query_tasks(scope(player=None))
        self.assertEqual(len(page.tasks), 3)
        self.assertTrue(page.has_more)
        last = self.service.query_tasks(scope(player=None), TaskQuery(offset=3))
        self.assertEqual(len(last.tasks), 1)
        self.assertFalse(last.has_more)
        self.assertEqual({t.task_id for t in page.tasks + last.tasks}, {t.task_id for t in tasks})
        with self.assertRaises(ValidationError):
            TaskQuery(limit=6)

    def test_capacity_includes_blocked_and_self_limit(self):
        tasks = []
        for i in range(8):
            tasks.append(self.create("create_" + str(i), context=scope("visit_" + str(i))))
        reserved = self.service.reserve_execution(self.next_context, "reserve", tasks[0].task_id,
                                                   execution_request(tasks[0])).task
        self.service.mark_execution_unknown(self.next_context, "unknown", reserved.task_id,
            expected_version=reserved.version, attempt_id=reserved.execution.attempt_id, reason="unknown")
        self.error("TASK_CAPACITY_EXCEEDED", lambda: self.create("overflow", context=scope("visit_9")))
        for i in range(3):
            self.create("self_" + str(i), context=scope("self_visit_" + str(i), owner=OTHER_OWNER, player=None),
                        clean=True, source="self_initiated")
        self.error("TASK_SELF_CAPACITY_EXCEEDED", lambda: self.create("self_overflow",
            context=scope("self_visit_4", owner=OTHER_OWNER, player=None), clean=True, source="self_initiated"))

    def test_next_visit_and_when_idle_gates(self):
        task = self.create()
        self.assertFalse(self.service.is_eligible(self.context, task.task_id, is_idle=True))
        self.error("TASK_NOT_ELIGIBLE", lambda: self.service.reserve_execution(
            self.context, "same_visit", task.task_id, execution_request(task), is_idle=True))
        self.assertTrue(self.service.is_eligible(self.next_context, task.task_id))
        idle = self.create("idle", trigger="when_idle", clean=True, source="self_initiated")
        self.assertFalse(self.service.is_eligible(self.context, idle.task_id))
        self.assertTrue(self.service.is_eligible(self.context, idle.task_id, is_idle=True))
        with self.assertRaises(ValueError):
            self.service.is_eligible(self.context, idle.task_id, is_idle=1)

    def test_two_executors_compete_only_one_claims(self):
        task = self.create()
        barrier = threading.Barrier(2)

        def claim(number):
            service = TaskService(TaskRepository(self.db.connect))
            barrier.wait(timeout=5)
            try:
                return service.reserve_execution(self.next_context, "claim_" + str(number), task.task_id,
                                                 execution_request(task, str(number)))
            except TaskError as error:
                return error.code

        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(claim, (1, 2)))
        self.assertEqual(sum(not isinstance(value, str) for value in results), 1)
        self.assertEqual([value for value in results if isinstance(value, str)], ["TASK_VERSION_CONFLICT"])
        current = self.service.get_task(self.next_context, task.task_id)
        self.assertEqual((current.status, current.version, current.execution.attempt_no), ("executing", 2, 1))
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_task_commands WHERE operation='reserve'").fetchone()[0], 1)

    def test_duplicate_create_concurrently_is_one_commit(self):
        barrier = threading.Barrier(2)

        def create(_):
            barrier.wait(timeout=5)
            return self.service.create_task(self.context, "concurrent", create_request())

        with ThreadPoolExecutor(max_workers=2) as pool:
            first, second = pool.map(create, (1, 2))
        self.assertEqual(first, second)
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_tasks").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_task_commands").fetchone()[0], 1)

    def test_same_action_cannot_reserve_two_tasks(self):
        first = self.create()
        second = self.create("second", drink="americano")
        self.service.reserve_execution(self.next_context, "claim", first.task_id, execution_request(first))
        self.error("TASK_EXECUTION_KEY_CONFLICT", lambda: self.service.reserve_execution(
            self.next_context, "claim_other", second.task_id, execution_request(second)))
        self.assertEqual(self.service.get_task(self.context, second.task_id), second)

    def test_success_receipt_idempotency_and_terminal_conflict(self):
        task = self.running()
        result = synthetic_result(task)
        command = self.service.settle_execution(self.next_context, "settle", task.task_id, result)
        self.assertEqual((command.task.status, command.task.version), ("completed", 4))
        self.assertEqual(self.service.settle_execution(self.next_context, "settle", task.task_id, result), command)
        replay = self.service.settle_execution(self.next_context, "settle_other_id", task.task_id, result)
        self.assertEqual((replay.outcome, replay.task), ("already_settled", command.task))
        self.error("TASK_RESULT_CONFLICT", lambda: self.service.settle_execution(
            self.next_context, "different_result", task.task_id, synthetic_result(task, status="failed")))
        self.assertFalse(self.service.is_eligible(scope("visit_3"), task.task_id, is_idle=True))

    def test_result_and_receipt_binding_mismatch_do_not_mutate_task(self):
        task = self.running()
        valid = synthetic_result(task)
        for field, value in (("scene_instance_id", "other_scene"), ("attempt_id", "other_attempt"),
                             ("actor_entity_id", "other_actor"), ("proposal_id", "other_proposal"),
                             ("trace_id", "other_trace"), ("goal_type", "clean_counter"), ("world_revision", 9)):
            with self.subTest(field=field), self.assertRaises(TaskError):
                self.service.settle_execution(self.next_context, "bad_result", task.task_id,
                                               TrustedTaskResult.model_validate({**valid.model_dump(), field: value}))
        for field, value in (("player", OTHER_PLAYER.model_dump()), ("player_entity_id", "other_player"),
                             ("drink_type", "americano"), ("actor_entity_id", "other_actor"),
                             ("world_revision", 10), ("trace_id", "other_trace")):
            receipt = {**valid.receipt.model_dump(), field: value}
            with self.subTest(receipt_field=field), self.assertRaises(TaskError):
                self.service.settle_execution(self.next_context, "bad_receipt", task.task_id,
                                               TrustedTaskResult.model_validate({**valid.model_dump(), "receipt": receipt}))
        self.assertEqual(self.service.get_task(self.context, task.task_id), task)
        with self.assertRaises(ValidationError):
            synthetic_result(task, receipt=None)

    def test_counter_success_requires_target_and_condition_receipt(self):
        task = self.running(clean=True)
        result = synthetic_result(task)
        for changes in ({"target_entity_id": "wrong_counter"}, {"condition_revision": 4}):
            with self.subTest(changes=changes), self.assertRaises(TaskError):
                self.service.settle_execution(self.next_context, "bad", task.task_id,
                    TrustedTaskResult.model_validate({**result.model_dump(), "receipt": {**result.receipt.model_dump(), **changes}}))
        self.assertEqual(self.service.settle_execution(self.next_context, "done", task.task_id, result).task.status, "completed")

    def test_receipt_cannot_be_consumed_by_another_task(self):
        first = self.running()
        self.service.settle_execution(self.next_context, "done_1", first.task_id, synthetic_result(first))
        second = self.running(suffix="2")
        self.error("TASK_EXECUTION_KEY_CONFLICT", lambda: self.service.settle_execution(
            self.next_context, "done_2", second.task_id, synthetic_result(second)))
        self.assertEqual(self.service.get_task(self.context, second.task_id), second)

    def test_cancel_in_flight_retains_slot_then_late_success_wins(self):
        task = self.running()
        cancelled = self.service.cancel_task(self.next_context, "cancel", task.task_id,
                                             expected_version=task.version, reason="取消请求").task
        self.assertEqual(cancelled.status, "executing")
        self.assertTrue(cancelled.execution.cancel_requested)
        final = self.service.settle_execution(self.next_context, "late_success", task.task_id, synthetic_result(task)).task
        self.assertEqual((final.status, final.version), ("completed", 5))

    def test_reserved_cancel_prevents_start_and_accepts_no_effect_terminal(self):
        task = self.create()
        reserved = self.service.reserve_execution(self.next_context, "reserve", task.task_id, execution_request(task)).task
        self.error("TASK_SUCCESS_BEFORE_START", lambda: self.service.settle_execution(
            self.next_context, "premature_success", task.task_id, synthetic_result(reserved)))
        cancelled = self.service.cancel_task(self.next_context, "cancel", task.task_id,
                                             expected_version=2, reason="cancel before start").task
        self.error("TASK_START_NOT_ALLOWED", lambda: self.service.start_execution(self.next_context, "start", task.task_id,
            expected_version=cancelled.version, attempt_id=cancelled.execution.attempt_id))
        final = self.service.settle_execution(self.next_context, "not_started", task.task_id,
                                              synthetic_result(cancelled, status="cancelled")).task
        self.assertEqual(final.status, "cancelled")

    def test_unknown_blocks_reclaim_requeue_and_cancel_release(self):
        task = self.running()
        blocked = self.service.mark_execution_unknown(self.next_context, "unknown", task.task_id,
            expected_version=task.version, attempt_id=task.execution.attempt_id, reason="连接中断").task
        self.assertEqual((blocked.status, blocked.execution.phase), ("blocked", "uncertain"))
        self.error("TASK_REQUEUE_NOT_SAFE", lambda: self.service.requeue_task(self.next_context, "unsafe", task.task_id,
            expected_version=blocked.version, reason="不能猜测未执行"))
        self.error("TASK_NOT_ELIGIBLE", lambda: self.service.reserve_execution(scope("visit_3"), "reclaim", task.task_id,
            execution_request(blocked, "2")))
        cancelled = self.service.cancel_task(self.next_context, "cancel_unknown", task.task_id,
                                             expected_version=blocked.version, reason="取消也不能释放未知动作").task
        self.assertEqual((cancelled.status, cancelled.execution.attempt_id), ("blocked", task.execution.attempt_id))
        final = self.service.settle_execution(scope("visit_3"), "late_result", task.task_id, synthetic_result(task)).task
        self.assertEqual(final.status, "completed")

    def test_uncertain_result_can_be_resolved_but_not_automatically_retried(self):
        task = self.running()
        blocked = self.service.settle_execution(self.next_context, "uncertain", task.task_id,
                                                synthetic_result(task, status="uncertain")).task
        self.error("TASK_REQUEUE_NOT_SAFE", lambda: self.service.requeue_task(self.next_context, "retry", task.task_id,
            expected_version=blocked.version, reason="unsafe"))
        final = self.service.settle_execution(self.next_context, "resolved", task.task_id, synthetic_result(task)).task
        self.assertEqual(final.status, "completed")
        self.assertEqual(self.service.get_command(self.next_context, "uncertain").task, blocked)

    def test_proven_no_effect_failure_allows_only_bounded_explicit_retry(self):
        task = self.running()
        failed = self.service.settle_execution(self.next_context, "fail_1", task.task_id,
                                               synthetic_result(task, status="failed")).task
        self.assertEqual(failed.status, "blocked")
        waiting = self.service.requeue_task(self.next_context, "retry_1", task.task_id,
                                            expected_version=failed.version, reason="可信无效果结果").task
        reserved = self.service.reserve_execution(self.next_context, "reserve_2", task.task_id,
                                                   execution_request(waiting, "2")).task
        self.assertEqual(reserved.execution.attempt_no, 2)
        self.assertNotEqual(reserved.execution.attempt_id, task.execution.attempt_id)
        second_failure = self.service.settle_execution(self.next_context, "fail_2", task.task_id,
                                                        synthetic_result(reserved, status="timed_out")).task
        self.error("TASK_ATTEMPT_LIMIT", lambda: self.service.requeue_task(self.next_context, "retry_2", task.task_id,
            expected_version=second_failure.version, reason="third attempt denied"))
        self.assertEqual(self.service.get_command(self.next_context, "fail_1").task, failed)
        cancelled = self.service.cancel_task(self.next_context, "cancel_failed", task.task_id,
                                             expected_version=second_failure.version, reason="停止重试").task
        self.assertEqual(cancelled.status, "cancelled")

    def test_sqlite_reopen_retains_version_execution_idempotency_and_history(self):
        task = self.running()
        reopened_running = TaskService(TaskRepository(SQLiteDatabase(self.path).connect))
        self.assertEqual(reopened_running.get_task(self.context, task.task_id), task)
        self.assertFalse(reopened_running.is_eligible(scope("visit_3"), task.task_id, is_idle=True))
        blocked = self.service.mark_execution_unknown(self.next_context, "unknown", task.task_id,
            expected_version=task.version, attempt_id=task.execution.attempt_id, reason="restart").task
        reopened = TaskService(TaskRepository(SQLiteDatabase(self.path).connect))
        self.assertEqual(reopened.get_task(self.context, task.task_id), blocked)
        self.assertEqual(reopened.get_command(self.next_context, "start_1").task, task)
        self.assertEqual(reopened.create_task(self.context, "create_1", create_request()).task.version, 1)
        self.error("TASK_NOT_ELIGIBLE", lambda: reopened.reserve_execution(self.next_context, "reclaim", task.task_id,
            execution_request(blocked, "2")))
        completed = reopened.settle_execution(self.next_context, "final", task.task_id, synthetic_result(task)).task
        again = TaskService(TaskRepository(SQLiteDatabase(self.path).connect))
        self.assertEqual(again.get_task(self.context, task.task_id), completed)
        self.assertEqual(again.get_command(self.next_context, "unknown").task, blocked)

    def test_command_insert_failure_rolls_back_task_and_version(self):
        task = self.create()
        with closing(self.db.connect()) as db, db:
            db.execute("CREATE TRIGGER inject_command_error BEFORE INSERT ON npc_task_commands "
                       "BEGIN SELECT RAISE(ABORT,'INJECTED_COMMAND_FAILURE'); END")
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INJECTED_COMMAND_FAILURE"):
            self.service.cancel_task(self.context, "cancel", task.task_id, expected_version=1, reason="fail")
        self.assertEqual(self.service.get_task(self.context, task.task_id), task)
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INJECTED_COMMAND_FAILURE"):
            self.create("new", drink="americano")
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_tasks").fetchone()[0], 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_task_commands").fetchone()[0], 1)

    def test_commit_failure_never_returns_success_and_rolls_back_both_rows(self):
        class FailingCommit(sqlite3.Connection):
            def commit(self):
                raise sqlite3.OperationalError("INJECTED_COMMIT_FAILURE")

        def connect():
            db = sqlite3.connect(self.path, factory=FailingCommit)
            db.execute("PRAGMA foreign_keys=ON")
            return db

        service = TaskService(TaskRepository(connect))
        with self.assertRaisesRegex(sqlite3.OperationalError, "INJECTED_COMMIT_FAILURE"):
            service.create_task(self.context, "failed_commit", create_request())
        self.assertEqual(self.service.query_tasks(self.context).tasks, ())
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM npc_task_commands").fetchone()[0], 0)

    def test_history_and_identity_immutable_and_corrupt_dto_visible(self):
        task = self.create()
        for sql in ("DELETE FROM npc_task_commands", "UPDATE npc_task_commands SET operation='cancel'",
                    "UPDATE npc_tasks SET owner_key='other'"):
            with closing(self.db.connect()) as db, self.assertRaises(sqlite3.IntegrityError), db:
                db.execute(sql)
        with closing(self.db.connect()) as db, db:
            db.execute("UPDATE npc_tasks SET goal_json='{}'")
        with self.assertRaises(ValidationError):
            self.service.get_task(self.context, task.task_id)
        with self.assertRaises(ValidationError):
            self.service.query_tasks(self.context)


class TaskMigrationTests(unittest.TestCase):
    def setUp(self):
        self.path = Path(tempfile.mkdtemp(prefix="task_migration_")) / "tasks.sqlite3"

    def old_database(self):
        # Genuine old migrations 1..18; do not construct v19 then subtract tables.
        with patch("app.sqlite_database.migrate_tasks", lambda db: None), patch("app.sqlite_database.migrate_relationship_state", lambda db: None):
            database = SQLiteDatabase(self.path)
        with closing(database.connect()) as db, db:
            seed_row(db, "experience_events", event_id="event", observer_id="npc", payload_json="{}")
            seed_row(db, "episodes", episode_id="episode", observer_id="npc", status="CLOSED",
                     close_reason="test", started_at_utc=1, ended_at_utc=2, created_at_utc=1, updated_at_utc=2)
            seed_row(db, "memory_jobs", job_id="job", episode_id="episode", observer_id="npc", status="SUCCEEDED")
            seed_row(db, "memory_records", memory_id="memory", observer_entity_id="npc", identity_namespace="npc",
                     identity_type="Character", persistent_id="cafe.barista.001", memory_type="EPISODIC",
                     scope="NPC_PRIVATE", summary="隔离的历史样本", provenance_set_json='["event"]',
                     episode_id="episode", job_id="job", status="ACTIVE")
            seed_row(db, "memory_evidence", memory_id="memory", experience_event_id="event", experience_seq=1)
            seed_row(db, "memory_subjects", memory_id="memory", identity_namespace="player",
                     identity_type="Player", persistent_id="cafe-local-player-001")
            # An unrelated user-owned table must survive too.
            db.execute("CREATE TABLE user_fixture(key TEXT PRIMARY KEY,value BLOB)")
            db.execute("INSERT INTO user_fixture VALUES('preferences',?)", (b"preserve\x00exactly",))
        return database

    def snapshot(self, database):
        with closing(database.connect()) as db:
            return {table: fingerprint(db, table) for table in objects(db, "table")}

    def test_empty_initialization_and_idempotent_reopen(self):
        database = SQLiteDatabase(self.path)
        self.assertEqual((LATEST_SCHEMA_VERSION, TASK_SCHEMA_VERSION), (20, 19))
        first = self.snapshot(database)
        SQLiteDatabase(self.path)
        self.assertEqual(self.snapshot(database), first)
        with closing(database.connect()) as db:
            self.assertEqual(db.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], LATEST_SCHEMA_VERSION)
            self.assertEqual(db.execute("PRAGMA foreign_key_check").fetchall(), [])
            self.assertIn("npc_tasks_active_dedup", objects(db, "index"))

    def test_populated_18_upgrade_preserves_every_old_table_and_user_data(self):
        database = self.old_database()
        before = self.snapshot(database)
        with closing(database.connect()) as db:
            old_objects = list(db.execute("SELECT type,name,sql FROM sqlite_master ORDER BY type,name"))
        self.assertNotIn("npc_tasks", before)
        with closing(database.connect()) as db:
            migrate_tasks(db)
        after = self.snapshot(database)
        self.assertEqual({key: value for key, value in before.items() if key != "schema_migrations"},
                         {key: after[key] for key in before if key != "schema_migrations"})
        self.assertEqual(set(after) - set(before), {"npc_tasks", "npc_task_commands"})
        with closing(database.connect()) as db:
            new_objects = {row[1]: tuple(row) for row in db.execute("SELECT type,name,sql FROM sqlite_master")}
            self.assertEqual([tuple(row) for row in old_objects], [new_objects[row[1]] for row in old_objects])
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchone()[0], "ok")
            self.assertEqual(db.execute("PRAGMA foreign_key_check").fetchall(), [])

    def test_migration_journal_failure_rolls_back_all_task_ddl_and_old_data(self):
        database = self.old_database()
        with closing(database.connect()) as db, db:
            db.execute("CREATE TRIGGER reject_task_migration BEFORE INSERT ON schema_migrations "
                       "WHEN NEW.version=19 BEGIN SELECT RAISE(ABORT,'INJECTED_MIGRATION_FAILURE'); END")
        before = self.snapshot(database)
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INJECTED_MIGRATION_FAILURE"):
            SQLiteDatabase(self.path)
        self.assertEqual(self.snapshot(database), before)
        with closing(database.connect()) as db:
            self.assertFalse(any(name.startswith("npc_task") for name in objects(db, "index") | objects(db, "trigger")))
            db.execute("DROP TRIGGER reject_task_migration")
            db.commit()
        SQLiteDatabase(self.path)
        self.assertIn("npc_tasks", self.snapshot(database))

    def test_partial_ddl_failure_rolls_back_and_future_schema_rejected(self):
        database = self.old_database()
        before = self.snapshot(database)
        with patch("app.task_schema._STATEMENTS", ("CREATE TABLE npc_tasks(task_id TEXT)", "INVALID SQL")):
            with self.assertRaises(sqlite3.OperationalError):
                database.migrate()
        self.assertEqual(self.snapshot(database), before)
        with closing(database.connect()) as db, db:
            db.execute("INSERT INTO schema_migrations VALUES(?,1)", (LATEST_SCHEMA_VERSION+1,))
        future = self.snapshot(database)
        with self.assertRaisesRegex(sqlite3.IntegrityError, "UNSUPPORTED_FUTURE_MEMORY_SCHEMA"):
            database.migrate()
        with closing(database.connect()) as db, self.assertRaisesRegex(sqlite3.IntegrityError, "UNSUPPORTED_FUTURE_TASK_SCHEMA"):
            migrate_tasks(db)
        self.assertEqual(self.snapshot(database), future)

    def test_fresh_initialization_failure_keeps_previous_migrations_recoverable(self):
        with patch("app.task_schema._STATEMENTS", ("CREATE TABLE npc_tasks(task_id TEXT)", "INVALID SQL")):
            with self.assertRaises(sqlite3.OperationalError):
                SQLiteDatabase(self.path)
        with closing(sqlite3.connect(self.path)) as db:
            self.assertEqual(db.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], 18)
            self.assertNotIn("npc_tasks", objects(db, "table"))
        database = SQLiteDatabase(self.path)
        with closing(database.connect()) as db:
            self.assertEqual(db.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], LATEST_SCHEMA_VERSION)
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchone()[0], "ok")


if __name__ == "__main__":
    unittest.main()
