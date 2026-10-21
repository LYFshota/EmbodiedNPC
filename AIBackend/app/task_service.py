"""Durable task transitions and idempotent commands; no model or world execution.

Scope/capabilities and TrustedTaskResult must come from a trusted host. Validation
here checks evidence consistency, not whether an untrusted sender is truthful.
"""

from __future__ import annotations

import time
from collections.abc import Callable
from uuid import uuid4

from pydantic import TypeAdapter

from .task_models import (
    CounterReceipt, DrinkReceipt, ExecutionRequest, Identifier, PositiveInt, TaskCommandResult,
    TaskCreate, TaskError, TaskExecution, TaskModel, TaskPage, TaskQuery, TaskRecord,
    TaskSchedule, TaskScope, TaskUpdate, Text, TrustedTaskResult,
)
from .task_repository import TaskRepository, digest


def _checked(model_type, value):
    # Revalidate even an instance created with model_construct/model_copy; callers
    # do not get to bypass invariants by handing us an already allocated DTO.
    if isinstance(value, TaskModel):
        value = value.model_dump(warnings=False)
    return model_type.model_validate(value)


class TaskService:
    """Keep task status distinct from one attempt's execution phase.

    waiting/executing/blocked/completed/cancelled describe the durable task;
    reserved/started/uncertain/terminal phases describe its immutable attempt.
    A reservation is not permission to start or proof of a world effect.
    """

    def __init__(self, repository: TaskRepository, *, clock: Callable[[], float] = time.time):
        self._repo = repository
        self._clock = clock

    @staticmethod
    def _id(value):
        return TypeAdapter(Identifier).validate_python(value, strict=True)

    @staticmethod
    def _version(task, expected):
        expected = TypeAdapter(PositiveInt).validate_python(expected, strict=True)
        if task.version != expected:
            raise TaskError("TASK_VERSION_CONFLICT")

    @staticmethod
    def _capability(scope, goal):
        if goal.kind not in scope.allowed_goal_kinds:
            raise TaskError("TASK_GOAL_NOT_AUTHORIZED")

    @staticmethod
    def _fingerprint(goal, player, source_kind, trigger, anchor, condition=None):
        value = {"goal": goal.model_dump(mode="json"), "player": player.key if player else None,
                 "source_kind": source_kind, "trigger": trigger, "anchor_visit_id": anchor}
        if condition is not None:
            # One live condition is one obligation, even if it was deferred or a Visit changed.
            value.update(condition=condition.model_dump(mode='json'), anchor_visit_id=None, trigger=None)
        return digest(value)

    @staticmethod
    def _evolve(task, now, **changes):
        value = task.model_dump()
        value.update(changes, version=task.version + 1, updated_at_utc=max(float(now), task.updated_at_utc))
        return TaskRecord.model_validate(value)

    def _command(self, scope, command_id, operation, payload, handler, binding_hash=None):
        # The repository transaction binds owner/player, command ID and payload.
        # A retry recovers that command; a changed payload is not the same command.
        scope = _checked(TaskScope, scope)
        command_id = self._id(command_id)
        if binding_hash is not None:
            import re
            if not isinstance(binding_hash, str) or re.fullmatch(r"[0-9a-f]{64}", binding_hash) is None:
                raise ValueError("TASK_BINDING_HASH_INVALID")
            payload = {**payload, "binding_hash": binding_hash}
        now = float(self._clock())
        return self._repo._run_command(scope, command_id, operation, payload, now,
                                       lambda db: handler(db, scope, now))

    def create_task(self, scope: TaskScope, command_id: str, request: TaskCreate, *, binding_hash: str | None = None) -> TaskCommandResult:
        request = _checked(TaskCreate, request)

        def apply(db, context, now):
            self._capability(context, request.goal)
            player = context.player if request.source_kind == "player_request" else None
            if request.source_kind == "player_request" and player is None:
                raise TaskError("TASK_PLAYER_REQUIRED")
            key = self._fingerprint(request.goal, player, request.source_kind, request.trigger_kind, context.visit_id, request.condition)
            existing = self._repo._duplicate(db, context.owner.key, key)
            if existing is not None:
                return existing, "deduplicated", None
            if self._repo._count_open(db, context.owner.key) >= 8:
                raise TaskError("TASK_CAPACITY_EXCEEDED")
            if request.source_kind == "self_initiated" and self._repo._count_open(db, context.owner.key, "self_initiated") >= 3:
                raise TaskError("TASK_SELF_CAPACITY_EXCEEDED")
            task = TaskRecord(task_id="task_" + uuid4().hex, owner=context.owner, player=player,
                              source_kind=request.source_kind, source=request.source, goal=request.goal,
                              trigger_kind=request.trigger_kind, created_visit_id=context.visit_id,
                              status="waiting", version=1, dedup_key=key,
                              schedule=TaskSchedule(anchor_visit_id=context.visit_id, condition=request.condition),
                              created_at_utc=now, updated_at_utc=now)
            self._repo._insert(db, task)
            return task, "created", None

        payload = request.model_dump(mode="json")
        if request.condition is None: payload.pop("condition")  # Keep historical command hashes compatible.
        return self._command(scope, command_id, "create", payload, apply, binding_hash)

    def get_task(self, scope: TaskScope, task_id: str) -> TaskRecord:
        return self._repo.get(_checked(TaskScope, scope), self._id(task_id))

    def query_tasks(self, scope: TaskScope, query: TaskQuery | None = None) -> TaskPage:
        return self._repo.query(_checked(TaskScope, scope), _checked(TaskQuery, query if query is not None else TaskQuery()))

    def query_considerable(self, scope: TaskScope, *, is_idle: bool = False):
        if type(is_idle) is not bool:
            raise ValueError('TASK_IDLE_FLAG_INVALID')
        return self._repo.considerable(_checked(TaskScope,scope),is_idle=is_idle,now=float(self._clock()))

    def record_consideration(self, scope: TaskScope, task_ids: tuple[str, ...], wake_key: str, *, is_idle: bool = False):
        scope = _checked(TaskScope,scope)
        if type(is_idle) is not bool or type(task_ids) is not tuple or not 1 <= len(task_ids) <= 3 or len(set(task_ids)) != len(task_ids):
            raise ValueError('TASK_CONSIDERATION_INVALID')
        checked = tuple(self._id(value) for value in task_ids)
        return self._repo.record_consideration(scope,checked,self._id(wake_key),float(self._clock()),
            lambda task: self._eligible(scope,task,is_idle))

    def get_command(self, scope: TaskScope, command_id: str, *, binding_hash: str | None = None) -> TaskCommandResult:
        """Historical commit receipt, not current action permission; query the task too."""
        return self._repo.get_command(_checked(TaskScope, scope), self._id(command_id), binding_hash=binding_hash)

    def update_task(self, scope: TaskScope, command_id: str, task_id: str, update: TaskUpdate, *, binding_hash: str | None = None) -> TaskCommandResult:
        task_id, update = self._id(task_id), _checked(TaskUpdate, update)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, update.expected_version)
            if task.status != "waiting":
                raise TaskError("TASK_UPDATE_REQUIRES_WAITING")
            goal = update.goal if update.goal is not None else task.goal
            if goal.kind != task.goal.kind:
                raise TaskError("TASK_GOAL_KIND_IMMUTABLE")
            if update.goal is not None:
                self._capability(context, goal)
            trigger = update.trigger_kind if update.trigger_kind is not None else task.trigger_kind
            anchor = context.visit_id if update.trigger_kind is not None else task.schedule.anchor_visit_id
            key = self._fingerprint(goal, task.player, task.source_kind, trigger, anchor, task.schedule.condition)
            other = self._repo._duplicate(db, context.owner.key, key)
            if other is not None and other.task_id != task_id:
                raise TaskError("TASK_ACTIVE_DUPLICATE")
            updated = self._evolve(task, now, goal=goal.model_dump(), trigger_kind=trigger,
                                   dedup_key=key, schedule=TaskSchedule(anchor_visit_id=anchor, reason=update.reason,
                                       condition=task.schedule.condition).model_dump())
            self._repo._save(db, updated, task.version)
            return updated, "updated", None

        return self._command(scope, command_id, "update", {"task_id": task_id, "update": update.model_dump(mode="json")}, apply, binding_hash)

    def defer_task(self, scope: TaskScope, command_id: str, task_id: str, *, expected_version: int,
                   trigger_kind: str, reason: str) -> TaskCommandResult:
        return self.update_task(scope, command_id, task_id,
                                TaskUpdate(expected_version=expected_version, trigger_kind=trigger_kind, reason=reason))

    def cancel_task(self, scope: TaskScope, command_id: str, task_id: str, *,
                    expected_version: int, reason: str, binding_hash: str | None = None, terminal_ack: bool = False) -> TaskCommandResult:
        if type(terminal_ack) is not bool: raise ValueError('TASK_CANCEL_ACK_INVALID')
        task_id = self._id(task_id)
        expected_version = TypeAdapter(PositiveInt).validate_python(expected_version, strict=True)
        reason = TypeAdapter(Text).validate_python(reason, strict=True)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, expected_version)
            if task.status in ("completed", "cancelled"):
                if terminal_ack:
                    return task, 'already_settled' if task.status=='completed' else 'cancelled', None
                raise TaskError("TASK_ALREADY_TERMINAL")
            execution = task.execution
            if execution is not None and execution.phase in ("reserved", "started", "uncertain"):
                data = execution.model_dump()
                data.update(cancel_requested=True, reason=reason)
                updated = self._evolve(task, now, execution=data)
                outcome = "cancellation_requested"
            else:
                updated = self._evolve(task, now, status="cancelled", schedule=task.schedule.model_copy(update={"reason": reason}).model_dump())
                outcome = "cancelled"
            self._repo._save(db, updated, task.version)
            return updated, outcome, None

        payload={"task_id": task_id, "version": expected_version, "reason": reason}
        if terminal_ack: payload['terminal_ack']=True
        return self._command(scope, command_id, "cancel", payload, apply, binding_hash)

    @staticmethod
    def _eligible(scope, task, is_idle):
        if task.status != "waiting":
            return False
        if task.player is not None and task.player != scope.player:
            return False  # Self view may query everything, but cannot serve an absent player.
        if task.trigger_kind == "next_visit":
            return scope.visit_id not in (task.created_visit_id, task.schedule.anchor_visit_id)
        return is_idle

    def is_eligible(self, scope: TaskScope, task_id: str, *, is_idle: bool = False) -> bool:
        """Pure storage-level gate, NOT perception, a wakeup or Unity admission."""
        if type(is_idle) is not bool:
            raise ValueError("TASK_IDLE_FLAG_INVALID")
        scope = _checked(TaskScope, scope)
        return self._eligible(scope, self.get_task(scope, task_id), is_idle)

    def reserve_execution(self, scope: TaskScope, command_id: str, task_id: str,
                          request: ExecutionRequest, *, is_idle: bool = False, binding_hash=None) -> TaskCommandResult:
        task_id, request = self._id(task_id), _checked(ExecutionRequest, request)
        if type(is_idle) is not bool:
            raise ValueError("TASK_IDLE_FLAG_INVALID")

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, request.expected_version)
            self._capability(context, task.goal)
            if not self._eligible(context, task, is_idle):
                raise TaskError("TASK_NOT_ELIGIBLE")
            number = task.execution.attempt_no + 1 if task.execution else 1
            if number > 2:
                raise TaskError("TASK_ATTEMPT_LIMIT")
            if task.goal.kind == "serve_drink":
                if request.player_entity_id is None or request.target_entity_id is not None or request.condition_revision is not None:
                    raise TaskError("TASK_DRINK_BINDINGS_INVALID")
            elif request.target_entity_id is None or request.condition_revision is None or request.player_entity_id is not None:
                raise TaskError("TASK_COUNTER_BINDINGS_INVALID")
            condition = task.schedule.condition
            if condition is not None and (condition.scene_instance_id != request.scene_instance_id or
                    condition.target_entity_id != request.target_entity_id or condition.condition_revision != request.condition_revision):
                raise TaskError('TASK_COUNTER_CONDITION_STALE')
            execution = TaskExecution(attempt_id="task_attempt_" + uuid4().hex, attempt_no=number,
                                      visit_id=context.visit_id, request=request, phase="reserved")
            updated = self._evolve(task, now, status="executing", execution=execution.model_dump())
            self._repo._save(db, updated, task.version)
            key = "execution:" + digest([request.scene_instance_id, request.actor_entity_id, request.proposal_id, request.trace_id])
            return updated, "reserved", key

        return self._command(scope, command_id, "reserve", {"task_id": task_id, "request": request.model_dump(mode="json"),
                                                             "is_idle": is_idle}, apply, binding_hash)

    def start_execution(self, scope: TaskScope, command_id: str, task_id: str, *,
                         expected_version: int, attempt_id: str) -> TaskCommandResult:
        """Persist start confirmation; a replay is history, so recheck current state before handoff."""
        task_id, attempt_id = self._id(task_id), self._id(attempt_id)
        expected_version = TypeAdapter(PositiveInt).validate_python(expected_version, strict=True)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, expected_version)
            execution = task.execution
            if (task.status != "executing" or execution is None or execution.attempt_id != attempt_id
                    or execution.phase != "reserved" or execution.cancel_requested or execution.visit_id != context.visit_id):
                raise TaskError("TASK_START_NOT_ALLOWED")
            if task.player is not None and task.player != context.player:
                raise TaskError("TASK_PLAYER_REQUIRED")
            self._capability(context, task.goal)
            data = execution.model_dump()
            data["phase"] = "started"
            updated = self._evolve(task, now, execution=data)
            self._repo._save(db, updated, task.version)
            return updated, "started", None

        return self._command(scope, command_id, "start", {"task_id": task_id, "version": expected_version, "attempt_id": attempt_id}, apply)

    @staticmethod
    def _verify_result(task, result):
        execution = task.execution
        if execution is None or execution.attempt_id != result.attempt_id:
            raise TaskError("TASK_ATTEMPT_MISMATCH")
        request = execution.request
        expected_goal = "prepare_drink" if task.goal.kind == "serve_drink" else "clean_counter"
        if (result.scene_instance_id != request.scene_instance_id or result.proposal_id != request.proposal_id
                or result.trace_id != request.trace_id or result.actor_entity_id != request.actor_entity_id
                or result.goal_type != expected_goal or result.world_revision < request.world_revision):
            raise TaskError("TASK_OUTCOME_MISMATCH")
        receipt = result.receipt
        if receipt is None:
            return
        if (receipt.actor_entity_id != request.actor_entity_id or receipt.proposal_id != request.proposal_id
                or receipt.trace_id != request.trace_id
                or not request.world_revision < receipt.world_revision <= result.world_revision):
            raise TaskError("TASK_RECEIPT_MISMATCH")
        if task.goal.kind == "serve_drink":
            if (not isinstance(receipt, DrinkReceipt) or receipt.player != task.player
                    or receipt.player_entity_id != request.player_entity_id or receipt.drink_type != task.goal.drink_type):
                raise TaskError("TASK_DRINK_RECEIPT_MISMATCH")
        elif (not isinstance(receipt, CounterReceipt) or receipt.target_entity_id != request.target_entity_id
              or receipt.condition_revision != request.condition_revision):
            raise TaskError("TASK_COUNTER_RECEIPT_MISMATCH")

    def settle_execution(self, scope: TaskScope, command_id: str, task_id: str,
                          result: TrustedTaskResult) -> TaskCommandResult:
        # Receipt identity, attempt and advancing world revision are checked before
        # settlement. Verified success wins a late cancellation request; failure
        # alone blocks the task. No model explanation can supply this evidence.
        task_id, result = self._id(task_id), _checked(TrustedTaskResult, result)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._verify_result(task, result)
            execution = task.execution
            if execution.result == result:
                return task, "already_settled", None
            if task.status not in ("executing", "blocked") or execution.phase not in ("reserved", "started", "uncertain"):
                raise TaskError("TASK_RESULT_CONFLICT")
            if result.status == "succeeded" and execution.phase == "reserved":
                raise TaskError("TASK_SUCCESS_BEFORE_START")
            status = ("completed" if result.status == "succeeded" else
                      "cancelled" if result.status == "cancelled" and execution.cancel_requested else "blocked")
            data = execution.model_dump()
            data.update(phase=result.status, result=result.model_dump(), reason=result.failure_code)
            updated = self._evolve(task, now, status=status, execution=data)
            self._repo._save(db, updated, task.version)
            key = ("receipt:" + digest([result.scene_instance_id, result.receipt.kind, result.receipt.receipt_id])
                   if result.receipt is not None else None)
            return updated, "settled", key

        return self._command(scope, command_id, "settle", {"task_id": task_id, "result": result.model_dump(mode="json")}, apply)

    def mark_execution_unknown(self, scope: TaskScope, command_id: str, task_id: str, *,
                               expected_version: int, attempt_id: str, reason: str) -> TaskCommandResult:
        # Lost world/transport evidence is uncertainty, not proven no-effect.
        # Preserve the attempt and block; safe requeue requires separate proof.
        task_id, attempt_id = self._id(task_id), self._id(attempt_id)
        expected_version = TypeAdapter(PositiveInt).validate_python(expected_version, strict=True)
        reason = TypeAdapter(Text).validate_python(reason, strict=True)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, expected_version)
            execution = task.execution
            if (execution is None or execution.attempt_id != attempt_id or task.status not in ("executing", "blocked")
                    or execution.phase not in ("reserved", "started", "uncertain")):
                raise TaskError("TASK_UNKNOWN_TRANSITION_INVALID")
            data = execution.model_dump()
            data.update(phase="uncertain", reason=reason)
            updated = self._evolve(task, now, status="blocked", execution=data)
            self._repo._save(db, updated, task.version)
            return updated, "blocked", None

        return self._command(scope, command_id, "mark_unknown", {"task_id": task_id, "version": expected_version,
                              "attempt_id": attempt_id, "reason": reason}, apply)

    def requeue_task(self, scope: TaskScope, command_id: str, task_id: str, *,
                      expected_version: int, reason: str) -> TaskCommandResult:
        """Explicit reconsideration after a proven no-effect terminal result only."""
        task_id = self._id(task_id)
        expected_version = TypeAdapter(PositiveInt).validate_python(expected_version, strict=True)
        reason = TypeAdapter(Text).validate_python(reason, strict=True)

        def apply(db, context, now):
            task = self._repo._load(db, context, task_id)
            self._version(task, expected_version)
            execution = task.execution
            if (task.status != "blocked" or execution is None or execution.cancel_requested
                    or execution.phase not in ("failed", "cancelled", "timed_out") or execution.result is None
                    or execution.result.effect != "not_committed"):
                raise TaskError("TASK_REQUEUE_NOT_SAFE")
            if execution.attempt_no >= 2:
                raise TaskError("TASK_ATTEMPT_LIMIT")
            updated = self._evolve(task, now, status="waiting", schedule=TaskSchedule(
                anchor_visit_id=task.schedule.anchor_visit_id, reason=reason, condition=task.schedule.condition).model_dump())
            self._repo._save(db, updated, task.version)
            return updated, "requeued", None

        return self._command(scope, command_id, "requeue", {"task_id": task_id, "version": expected_version, "reason": reason}, apply)
