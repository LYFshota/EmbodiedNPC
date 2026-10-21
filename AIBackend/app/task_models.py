"""Strict storage contracts; authority is supplied by a trusted host, never model text.

Identity components match the existing persistent participant contract. This module
does not import cognition, providers, configuration, or initialize any storage.
"""

from __future__ import annotations

import json
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator


Identifier = Annotated[str, Field(pattern=r"^[A-Za-z0-9_-]+$", min_length=1, max_length=160)]
EntityId = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]*$", max_length=120)]
Text = Annotated[str, Field(min_length=1, max_length=240, pattern=r"\S")]
PositiveInt = Annotated[int, Field(ge=1)]
Revision = Annotated[int, Field(ge=0)]
Timestamp = Annotated[float, Field(ge=0, allow_inf_nan=False)]
GoalKind = Literal["serve_drink", "clean_assigned_counter"]
TriggerKind = Literal["next_visit", "when_idle"]
TaskStatus = Literal["waiting", "executing", "blocked", "completed", "cancelled"]
DrinkType = Literal["latte", "cappuccino", "americano"]


class TaskError(ValueError):
    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


class TaskModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, revalidate_instances="always")


class TaskIdentity(TaskModel):
    identity_namespace: Annotated[str, Field(min_length=1, max_length=80, pattern=r"\S")]
    identity_type: Annotated[str, Field(min_length=1, max_length=80, pattern=r"\S")]
    persistent_id: Annotated[str, Field(min_length=1, max_length=160, pattern=r"\S")]

    @property
    def key(self) -> str:
        return json.dumps([self.identity_namespace, self.identity_type, self.persistent_id],
                          ensure_ascii=False, separators=(",", ":"))

    @property
    def is_npc(self) -> bool:
        return (self.identity_namespace, self.identity_type) == ("npc", "Character")

    @property
    def is_player(self) -> bool:
        return (self.identity_namespace, self.identity_type) == ("player", "Player")


class TaskScope(TaskModel):
    """Private host authority. A player narrows access; None is the NPC's own view.

    allowed_goal_kinds is a trusted capability ceiling, not a model-selectable
    permission. Actual Unity eligibility/admission remains a later-stage concern.
    """

    owner: TaskIdentity
    visit_id: Identifier
    player: TaskIdentity | None = None
    allowed_goal_kinds: tuple[GoalKind, ...] = Field(default=(), max_length=2)

    @model_validator(mode="after")
    def identities(self):
        if not self.owner.is_npc or (self.player is not None and not self.player.is_player):
            raise ValueError("TASK_IDENTITY_ROLE_INVALID")
        if len(set(self.allowed_goal_kinds)) != len(self.allowed_goal_kinds):
            raise ValueError("TASK_CAPABILITIES_DUPLICATE")
        return self


class ServeDrinkGoal(TaskModel):
    kind: Literal["serve_drink"] = "serve_drink"
    drink_type: DrinkType


class CleanAssignedCounterGoal(TaskModel):
    kind: Literal["clean_assigned_counter"] = "clean_assigned_counter"
    counter_key: Literal["assigned_counter"] = "assigned_counter"


TaskGoal = Annotated[ServeDrinkGoal | CleanAssignedCounterGoal, Field(discriminator="kind")]


class TaskSource(TaskModel):
    event_id: Identifier
    request_id: Identifier | None = None
    experience_id: Identifier | None = None


class TaskCondition(TaskModel):
    """Host-owned world condition, never a model-selected task parameter."""
    scene_instance_id: Identifier
    target_entity_id: EntityId
    condition_revision: PositiveInt


class TaskCreate(TaskModel):
    source_kind: Literal["player_request", "self_initiated"]
    source: TaskSource
    goal: TaskGoal
    trigger_kind: TriggerKind
    condition: TaskCondition | None = None

    @model_validator(mode="after")
    def self_goal(self):
        if self.source_kind == "self_initiated" and self.goal.kind != "clean_assigned_counter":
            raise ValueError("TASK_SELF_GOAL_NOT_SUPPORTED")
        if self.condition is not None and self.goal.kind != "clean_assigned_counter":
            raise ValueError("TASK_CONDITION_GOAL_INVALID")
        return self


class TaskUpdate(TaskModel):
    expected_version: PositiveInt
    reason: Text
    goal: TaskGoal | None = None
    trigger_kind: TriggerKind | None = None

    @model_validator(mode="after")
    def change_required(self):
        if self.goal is None and self.trigger_kind is None:
            raise ValueError("TASK_UPDATE_EMPTY")
        return self


class TaskQuery(TaskModel):
    player: TaskIdentity | None = None
    statuses: tuple[TaskStatus, ...] = Field(default=("waiting", "executing", "blocked"),
                                           min_length=1, max_length=5)
    limit: Annotated[int, Field(ge=1, le=5)] = 3
    offset: Annotated[int, Field(ge=0, le=10000)] = 0

    @model_validator(mode="after")
    def filters(self):
        if self.player is not None and not self.player.is_player:
            raise ValueError("TASK_PLAYER_INVALID")
        if len(set(self.statuses)) != len(self.statuses):
            raise ValueError("TASK_STATUS_DUPLICATE")
        return self


class TaskSchedule(TaskModel):
    anchor_visit_id: Identifier
    reason: Text | None = None
    condition: TaskCondition | None = None


class TaskConsideration(TaskModel):
    """Host resource counters only; no business status or execution authority."""
    schema_version: Literal[1] = 1
    visit_id: Identifier
    wake_key: Identifier
    automatic_count: Annotated[int, Field(ge=0, le=3)]
    next_allowed_at_utc: Timestamp
    wake_keys: tuple[Identifier, ...] = Field(default=(), max_length=3)


class ExecutionRequest(TaskModel):
    expected_version: PositiveInt
    scene_instance_id: Identifier
    proposal_id: Identifier
    trace_id: Identifier
    actor_entity_id: EntityId
    world_revision: Revision
    player_entity_id: EntityId | None = None
    target_entity_id: EntityId | None = None
    condition_revision: PositiveInt | None = None


class ReceiptBase(TaskModel):
    receipt_id: Identifier
    actor_entity_id: EntityId
    proposal_id: Identifier
    trace_id: Identifier
    world_revision: PositiveInt


class DrinkReceipt(ReceiptBase):
    kind: Literal["drink_prepared"] = "drink_prepared"
    player: TaskIdentity
    player_entity_id: EntityId
    drink_type: DrinkType

    @model_validator(mode="after")
    def player_role(self):
        if not self.player.is_player:
            raise ValueError("TASK_RECEIPT_PLAYER_INVALID")
        return self


class CounterReceipt(ReceiptBase):
    kind: Literal["counter_cleaned"] = "counter_cleaned"
    target_entity_id: EntityId
    condition_revision: PositiveInt


TaskReceipt = Annotated[DrinkReceipt | CounterReceipt, Field(discriminator="kind")]


class TrustedTaskResult(TaskModel):
    """Host-only evidence envelope, not a model completion tool or world verifier."""
    attempt_id: Identifier
    scene_instance_id: Identifier
    proposal_id: Identifier
    trace_id: Identifier
    actor_entity_id: EntityId
    outcome_id: Identifier
    goal_type: Literal["prepare_drink", "clean_counter"]
    status: Literal["succeeded", "failed", "cancelled", "timed_out", "uncertain"]
    effect: Literal["committed", "not_committed", "unknown"]
    world_revision: Revision
    failure_code: Text | None = None
    receipt: TaskReceipt | None = None

    @model_validator(mode="after")
    def evidence_shape(self):
        if self.status == "succeeded":
            if self.effect != "committed" or self.receipt is None or self.failure_code is not None:
                raise ValueError("TASK_SUCCESS_EVIDENCE_REQUIRED")
        elif self.status == "uncertain":
            if self.effect != "unknown" or self.receipt is not None or self.failure_code is None:
                raise ValueError("TASK_UNCERTAIN_EVIDENCE_INVALID")
        elif self.effect != "not_committed" or self.receipt is not None or self.failure_code is None:
            raise ValueError("TASK_FAILURE_EFFECT_NOT_PROVEN_ABSENT")
        return self


class TaskExecution(TaskModel):
    schema_version: Literal[1] = 1
    attempt_id: Identifier
    attempt_no: PositiveInt
    visit_id: Identifier
    request: ExecutionRequest
    phase: Literal["reserved", "started", "succeeded", "failed", "cancelled", "timed_out", "uncertain"]
    cancel_requested: bool = False
    reason: Text | None = None
    result: TrustedTaskResult | None = None

    @model_validator(mode="after")
    def result_phase(self):
        if self.phase in ("reserved", "started"):
            if self.result is not None:
                raise ValueError("TASK_IN_FLIGHT_RESULT_INVALID")
        elif self.result is None:
            if self.phase != "uncertain":
                raise ValueError("TASK_TERMINAL_RESULT_REQUIRED")
        elif self.result.status != self.phase or self.result.attempt_id != self.attempt_id:
            raise ValueError("TASK_RESULT_PHASE_MISMATCH")
        return self


class TaskRecord(TaskModel):
    task_id: Identifier
    owner: TaskIdentity
    player: TaskIdentity | None
    source_kind: Literal["player_request", "self_initiated"]
    source: TaskSource
    goal: TaskGoal
    trigger_kind: TriggerKind
    created_visit_id: Identifier
    status: TaskStatus
    version: PositiveInt
    dedup_key: Annotated[str, Field(pattern=r"^[0-9a-f]{64}$")]
    schedule: TaskSchedule
    consideration: TaskConsideration | None = None
    execution: TaskExecution | None = None
    created_at_utc: Timestamp
    updated_at_utc: Timestamp

    @model_validator(mode="after")
    def state_shape(self):
        if not self.owner.is_npc or (self.player is not None and not self.player.is_player):
            raise ValueError("TASK_RECORD_IDENTITY_INVALID")
        if (self.source_kind == "player_request") != (self.player is not None):
            raise ValueError("TASK_SOURCE_PLAYER_MISMATCH")
        if self.goal.kind == "serve_drink" and self.player is None:
            raise ValueError("TASK_DRINK_PLAYER_REQUIRED")
        if self.source_kind == "self_initiated" and self.goal.kind != "clean_assigned_counter":
            raise ValueError("TASK_SELF_GOAL_NOT_SUPPORTED")
        if self.updated_at_utc < self.created_at_utc:
            raise ValueError("TASK_TIME_REVERSED")
        execution = self.execution
        if self.status == "executing" and (execution is None or execution.phase not in ("reserved", "started")):
            raise ValueError("TASK_EXECUTION_REQUIRED")
        if self.status == "blocked" and (execution is None or execution.phase not in ("uncertain", "failed", "cancelled", "timed_out")):
            raise ValueError("TASK_BLOCK_EVIDENCE_REQUIRED")
        if self.status == "completed" and (execution is None or execution.phase != "succeeded" or execution.result is None):
            raise ValueError("TASK_COMPLETION_EVIDENCE_REQUIRED")
        if execution is not None and execution.phase in ("reserved", "started", "uncertain") and self.status not in ("executing", "blocked"):
            raise ValueError("TASK_UNRESOLVED_EXECUTION_RETAINED")
        if execution is not None and execution.phase == "succeeded" and self.status != "completed":
            raise ValueError("TASK_SUCCESS_STATUS_MISMATCH")
        return self


class TaskCommandResult(TaskModel):
    """Immutable historical commit receipt, never a reusable world-action permit."""
    command_id: Identifier
    operation: Literal["create", "update", "cancel", "reserve", "start", "settle", "mark_unknown", "requeue"]
    outcome: Literal["created", "deduplicated", "updated", "cancelled", "cancellation_requested",
                     "reserved", "started", "settled", "already_settled", "blocked", "requeued"]
    task: TaskRecord


class TaskPage(TaskModel):
    tasks: tuple[TaskRecord, ...]
    has_more: bool


def authorize(scope: TaskScope, task: TaskRecord) -> None:
    if task.owner != scope.owner or (scope.player is not None and task.player is not None and task.player != scope.player):
        raise TaskError("TASK_NOT_FOUND_OR_NOT_AUTHORIZED")
