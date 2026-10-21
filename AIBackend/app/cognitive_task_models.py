"""Bounded task intents and private wire receipts; no model-selected identities."""
from typing import Annotated, Literal
from pydantic import Field, model_validator, field_validator
from .task_models import TaskModel, Identifier, EntityId, TaskIdentity, TaskGoal, TriggerKind, PositiveInt, Text, TaskStatus, ExecutionRequest


class TaskRuntimeContext(TaskModel):
    command_id: Identifier
    client_session_id: Identifier
    visit_id: Identifier
    scene_instance_id: Identifier
    lifecycle_protocol: Literal['1.0'] | None = None
    player_entity_id: EntityId | None = None

    @model_validator(mode="after")
    def session_visit(self):
        if self.client_session_id != self.visit_id:
            raise ValueError("TASK_VISIT_SESSION_MISMATCH")
        if self.lifecycle_protocol == '1.0' and self.player_entity_id is None:
            raise ValueError('TASK_RUNTIME_PLAYER_REQUIRED')
        return self


class TaskConsiderationContext(TaskModel):
    opportunity_token: Identifier
    event_id: Identifier
    execution_protocol: Literal['1.0'] | None = None


class TaskFocus(TaskModel):
    task_ref: Identifier
    expected_version: PositiveInt


class TaskExecutionTicket(TaskModel):
    """Host reservation; never a reusable world-action start permission."""
    schema_version: Literal['1.1'] = '1.1'
    request_id: str
    trace_id: str
    run_id: str
    agent_entity_id: str
    command_id: Identifier
    owner: TaskIdentity
    player: TaskIdentity
    visit_id: Identifier
    scene_instance_id: Identifier
    task_id: Identifier
    task_version: PositiveInt
    attempt_id: Identifier
    goal: TaskGoal
    execution_request: ExecutionRequest


class CreateTaskIntent(TaskModel):
    op: Literal["create_task"]
    source: Literal["requester", "self"]
    goal: TaskGoal
    trigger_kind: TriggerKind


class UpdateTaskIntent(TaskModel):
    op: Literal["update_task"]
    task_ref: Identifier
    expected_version: PositiveInt
    change: Literal["edit", "defer", "cancel"] = Field(description=(
        "edit changes the goal and requires goal; defer changes only the trigger and requires trigger_kind, "
        "with no goal; cancel has neither goal nor trigger_kind."))
    reason: Text
    goal: TaskGoal | None = None
    trigger_kind: TriggerKind | None = None

    @model_validator(mode="after")
    def changes(self):
        if self.change == "cancel" and (self.goal is not None or self.trigger_kind is not None):
            raise ValueError("TASK_CANCEL_FIELDS_FORBIDDEN")
        if self.change == "defer" and (self.goal is not None or self.trigger_kind is None):
            raise ValueError("TASK_DEFER_FIELDS_INVALID")
        if self.change == "edit" and self.goal is None:
            raise ValueError("TASK_EDIT_GOAL_REQUIRED")
        return self


TaskIntent = Annotated[CreateTaskIntent | UpdateTaskIntent, Field(discriminator="op")]


class TaskReadPlan(TaskModel):
    op: Literal["query_tasks"] = "query_tasks"
    task_ref: Identifier | None = None
    statuses: tuple[TaskStatus, ...] = Field(default=("waiting", "executing", "blocked"), min_length=1, max_length=5)
    limit: Annotated[int, Field(ge=1, le=5)] = 3
    offset: Annotated[int, Field(ge=0, le=10000)] = 0

    @field_validator("statuses", mode="before")
    @classmethod
    def json_array(cls, value):
        return tuple(value) if isinstance(value, list) else value


class CognitiveTaskReceipt(TaskModel):
    schema_version: Literal["1.1"] = "1.1"
    request_id: str
    trace_id: str
    run_id: str
    agent_entity_id: str
    command_id: Identifier
    owner: TaskIdentity
    player: TaskIdentity
    visit_id: Identifier
    scene_instance_id: Identifier
    reply_language: Literal["zh-CN", "en-US"]
    operation: Literal["create", "update", "cancel"]
    outcome: Literal["created", "deduplicated", "updated", "cancelled", "cancellation_requested", "recovered", "failed"]
    committed: bool
    recovered: bool = False
    task_id: Identifier | None = None
    task_version: PositiveInt | None = None
    task_status: TaskStatus | None = None
    error_code: Literal["TASK_NOT_SAVED"] | None = None

    @model_validator(mode="after")
    def commit_shape(self):
        fields = (self.task_id, self.task_version, self.task_status)
        if (self.committed and any(value is None for value in fields) or
                not self.committed and any(value is not None for value in fields)):
            raise ValueError("TASK_RECEIPT_COMMIT_INVALID")
        if not self.owner.is_npc or not self.player.is_player:
            raise ValueError("TASK_RECEIPT_IDENTITY_INVALID")
        if self.committed == (self.error_code is not None):
            raise ValueError("TASK_RECEIPT_ERROR_INVALID")
        allowed = {"create": ("created", "deduplicated"), "update": ("updated",),
                   "cancel": ("cancelled", "cancellation_requested")}
        if self.recovered:
            valid = self.committed and self.outcome == "recovered"
        elif not self.committed:
            valid = self.outcome == "failed"
        else:
            valid = self.outcome in allowed[self.operation]
        if not valid:
            raise ValueError("TASK_RECEIPT_OPERATION_INVALID")
        return self
