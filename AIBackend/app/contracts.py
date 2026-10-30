"""M3 后端内部与 Unity 共享的最小消息契约。"""

from __future__ import annotations

import unicodedata
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator, model_serializer
from .reply_language import ReplyLanguage, validate_utterance
from .cognitive_task_models import TaskRuntimeContext, CognitiveTaskReceipt, TaskConsiderationContext, TaskExecutionTicket
from .task_models import TaskCondition
from .context_capacity import CapacityStatus
from .drink_types import DrinkType


Emotion = Literal["happy", "neutral", "fear", "curious", "thoughtful", "concerned", "surprised"]
ExpressionIcon = Literal["none", "happy", "confused", "surprised", "thinking"]
Gesture = Literal["none", "wave", "nod"]
GoalType = Literal["bring_object", "move_to_entity", "open_door", "prepare_drink", "clean_counter"]
EntityId = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]*$")]
CognitiveDecisionKind = Literal["DIRECT_RESPONSE", "NO_ACTION", "ACTION_PROPOSAL"]
EpisodeDirective = Literal["CONTINUE", "END"]
EntityKind = Literal["npc", "player", "item", "environment", "unknown"]


class PerceivedEntity(BaseModel):
    """One observer-local semantic candidate; never a full world entity dump."""

    model_config = ConfigDict(extra="forbid")

    entity_id: EntityId
    kind: EntityKind
    display_name: str = Field(max_length=80)
    appearance_description: str | None = Field(default=None, min_length=1, max_length=160)
    aliases: tuple[str, ...] = Field(max_length=16)
    semantic_tags: tuple[str, ...] = Field(max_length=16)
    visibility: str = Field(max_length=40)
    capabilities: tuple[str, ...] = Field(max_length=16)

    @field_validator("appearance_description")
    @classmethod
    def bounded_appearance_text(cls, value: str | None) -> str | None:
        if value is not None:
            if not value.strip() or any(c != "\n" and unicodedata.category(c) in ("Cc", "Cf", "Cs", "Zl", "Zp") for c in value):
                raise ValueError("APPEARANCE_DESCRIPTION_INVALID")
        return value

    @model_validator(mode="after")
    def appearance_requires_current_vision(self):
        if self.appearance_description is not None and (self.kind != "player" or self.visibility != "current_visible"):
            raise ValueError("APPEARANCE_REQUIRES_CURRENT_PLAYER_VISION")
        return self


class PerceptionSnapshot(BaseModel):
    """Bounded structured perception supplied by the authoritative client."""

    model_config = ConfigDict(extra="forbid")

    observer_entity_id: EntityId
    perception_revision: int = Field(ge=0)
    entities: tuple[PerceivedEntity, ...] = Field(max_length=16)


class CognitiveRunContext(BaseModel):
    """Identity and correlation for one bounded Unity-owned cognition run."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    run_id: str = Field(min_length=1, max_length=160)
    agent_entity_id: EntityId
    trigger_id: str = Field(min_length=1, max_length=160)
    trigger_type: str = Field(min_length=1, max_length=80)
    started_at: float = Field(ge=0, allow_inf_nan=False)


class CognitiveTriggerContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    trigger_id: str = Field(min_length=1, max_length=160)
    trigger_type: str = Field(min_length=1, max_length=80)
    agent_entity_id: EntityId
    source_event_id: str = Field(max_length=160)
    created_at: float = Field(ge=0, allow_inf_nan=False)
    reason: str = Field(max_length=120)


class CognitiveEventContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    event_id: str = Field(min_length=1, max_length=160)
    trace_id: str | None = Field(default=None, max_length=160)
    source_observation_id: str | None = Field(default=None, max_length=160)
    event_type: str = Field(min_length=1, max_length=80)
    source_entity_id: str = Field(max_length=120)
    target_entity_id: str = Field(max_length=120)
    area_entity_id: str = Field(max_length=120)
    payload: tuple["CognitiveKeyValue", ...] = Field(default=(), max_length=16)
    occurred_at: float = Field(ge=0, allow_inf_nan=False)
    world_revision: int = Field(ge=0)
    has_world_revision: bool

    @field_validator("trace_id", "source_observation_id", mode="before")
    @classmethod
    def normalize_unity_empty_causation(cls, value):
        return None if value == "" else value


class CognitiveKeyValue(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    key: str = Field(min_length=1, max_length=80)
    value: str = Field(max_length=500)


class CognitiveCharacterContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    character_id: str = Field(min_length=1, max_length=120)
    display_name: str = Field(min_length=1, max_length=80)
    role_summary: str = Field(min_length=1, max_length=240)
    backstory: str = Field(min_length=1, max_length=1200)
    personality: str = Field(min_length=1, max_length=600)
    speaking_style: str = Field(min_length=1, max_length=480)
    boundaries: str = Field(min_length=1, max_length=600)

    @field_validator("character_id", "display_name", "role_summary", "backstory", "personality",
                     "speaking_style", "boundaries")
    @classmethod
    def require_nonblank_character_field(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("Character fields must not be blank")
        return value


class CognitiveBehaviorModeContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    mode_id: str = Field(min_length=1, max_length=80)


class CognitivePlayerContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    player_entity_id: EntityId
    display_name: str = Field(min_length=1, max_length=80)


class CognitiveActionBindingCapability(BaseModel):
    """Engine-neutral callable binding projected by the active runtime."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    name: EntityId
    kind: Literal["entity_ref", "enum"]
    required: bool
    enum_values: tuple[str, ...] = ()

    @model_validator(mode="after")
    def validate_enum_values(self):
        if self.kind == "enum":
            if not self.enum_values or len(self.enum_values) > 32 or len(set(self.enum_values)) != len(self.enum_values):
                raise ValueError("INVALID_ENUM_CAPABILITY")
        elif self.enum_values:
            raise ValueError("ENTITY_REF_ENUM_VALUES_FORBIDDEN")
        return self


class CognitiveActionCapability(BaseModel):
    """One bounded action surface; execution details deliberately stay in Unity."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    action_id: EntityId
    description: str = Field(min_length=1, max_length=240)
    bindings: tuple[CognitiveActionBindingCapability, ...] = Field(max_length=16)

    @model_validator(mode="after")
    def validate_unique_bindings(self) -> "CognitiveActionCapability":
        names = [binding.name for binding in self.bindings]
        if len(names) != len(set(names)):
            raise ValueError("COGNITIVE_CAPABILITY_BINDINGS_DUPLICATE")
        return self


class CognitiveCapabilityManifest(BaseModel):
    """Minimal E6A runtime projection of the current Agent's allowed actions."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    available_actions: tuple[CognitiveActionCapability, ...] = Field(max_length=16)

    @model_validator(mode="after")
    def validate_unique_actions(self) -> "CognitiveCapabilityManifest":
        action_ids = [action.action_id for action in self.available_actions]
        if len(action_ids) != len(set(action_ids)):
            raise ValueError("COGNITIVE_CAPABILITY_ACTIONS_DUPLICATE")
        return self


class SelfSceneState(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    posture: Literal["seated", "unknown"] = "unknown"
    seat_entity_id: EntityId | None = None

    @field_validator("posture", mode="before")
    @classmethod
    def normalize_unity_posture(cls, value):
        return "unknown" if value == "" else value

    @field_validator("seat_entity_id", mode="before")
    @classmethod
    def normalize_unity_seat(cls, value):
        return None if value == "" else value

    @model_validator(mode="after")
    def validate_posture(self):
        if (self.posture == "seated") != (self.seat_entity_id is not None):
            raise ValueError("SCENE_SELF_SEAT_MISMATCH")
        return self


class ObservedSeat(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    seat_entity_id: EntityId
    relation_to_self: Literal["self", "opposite", "unknown"] = "unknown"
    occupancy: Literal["occupied", "empty", "unknown"] = "unknown"
    occupant_entity_id: EntityId | None = None

    @field_validator("occupant_entity_id", mode="before")
    @classmethod
    def normalize_unity_occupant(cls, value):
        return None if value == "" else value

    @model_validator(mode="after")
    def validate_occupancy(self):
        if self.occupant_entity_id is not None and self.occupancy != "occupied":
            raise ValueError("SCENE_OCCUPANT_WITHOUT_OCCUPANCY")
        return self


class SceneFacts(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    self: SelfSceneState | None = None
    observed_seats: tuple[ObservedSeat, ...] = Field(default=(), max_length=4)

    @model_validator(mode="after")
    def validate_unique_seats(self):
        ids = [seat.seat_entity_id for seat in self.observed_seats]
        if len(ids) != len(set(ids)):
            raise ValueError("SCENE_SEATS_DUPLICATE")
        return self


DailyBehaviorMode = Literal["work", "rest", "leisure"]


class CognitiveDailyModeCandidate(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    target_mode: DailyBehaviorMode
    condition: str = Field(min_length=1, max_length=160)


class CognitiveDailyBehaviorContext(BaseModel):
    """Unity-owned life situation; not memory, world truth, or action authorization."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    context_token: str = Field(min_length=1, max_length=64)
    current_mode: DailyBehaviorMode
    mode_revision: int = Field(ge=0)
    profile_revision: int = Field(ge=1)
    condition_revision: int = Field(ge=0)
    objective: str = Field(min_length=1, max_length=160)
    tendency: str = Field(min_length=1, max_length=160)
    considered_activities: list[Annotated[str, Field(min_length=1, max_length=48)]] = Field(max_length=6)
    candidates: list[CognitiveDailyModeCandidate] = Field(default_factory=list, max_length=2)
    constraints: list[Annotated[str, Field(min_length=1, max_length=160)]] = Field(default_factory=list, max_length=4)

    @model_validator(mode="after")
    def distinct_targets(self):
        targets = [c.target_mode for c in self.candidates]
        if self.current_mode in targets or len(targets) != len(set(targets)):
            raise ValueError("COGNITIVE_DAILY_MODE_CANDIDATES_INVALID")
        return self


class CounterWorkContext(BaseModel):
    """Unity-owned configured work knowledge, never a visual observation."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    target_entity_id: EntityId
    needs_cleaning: bool
    condition_revision: int = Field(ge=0)
    condition_token: str | None = Field(default=None, max_length=32)
    event_id: str | None = Field(default=None, max_length=160)
    trace_id: str | None = Field(default=None, max_length=160)


class CognitiveExecutionState(BaseModel):
    """Bounded Unity snapshot; no commands, persistent identities or inferred progress."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    source: Literal["unity_execution_snapshot"]
    actor_entity_id: EntityId
    player_entity_id: EntityId | None = None
    source_session_id: str = Field(min_length=1, max_length=160)
    scene_instance_id: str | None = Field(default=None, min_length=1, max_length=160)
    captured_at: float = Field(ge=0, allow_inf_nan=False)
    world_revision: int = Field(ge=0)
    status: Literal["UNKNOWN", "IN_PROGRESS", "SUCCEEDED", "FAILED", "CANCELLED"]
    proposal_id: str | None = Field(default=None, min_length=1, max_length=160)
    proposal_revision: int = Field(default=0, ge=0)
    trace_id: str | None = Field(default=None, min_length=1, max_length=160)
    action_id: EntityId | None = None
    stage: Literal["accepted", "action", "rediscovering", "replanning", "terminal"] | None = None
    step: str | None = Field(default=None, pattern=r"^[A-Za-z]{1,40}$")
    drink_type: DrinkType | None = None
    outcome_id: str | None = Field(default=None, min_length=1, max_length=240)
    outcome_world_revision: int = Field(default=0, ge=0)
    failure_code: str | None = Field(default=None, pattern=r"^[A-Za-z0-9_]{1,120}$")
    receipt_id: str | None = Field(default=None, min_length=1, max_length=160)
    # Presence advertises the Unity control extension; old senders offer no control.
    allowed_controls: list[Literal["cancel_active_goal"]] | None = Field(default=None, max_length=1)

    @model_serializer(mode="wrap")
    def omit_absent_controls(self, handler):
        data = handler(self)
        if self.allowed_controls is None:
            data.pop("allowed_controls", None)
        return data

    @model_validator(mode="after")
    def execution_evidence(self):
        if self.allowed_controls and self.status != "IN_PROGRESS":
            raise ValueError("EXECUTION_CONTROL_REQUIRES_ACTIVE_GOAL")
        details = (self.proposal_id, self.trace_id, self.action_id, self.stage, self.step,
                   self.drink_type, self.outcome_id, self.failure_code, self.receipt_id)
        if self.status == "UNKNOWN":
            if any(x is not None for x in details) or self.proposal_revision or self.outcome_world_revision:
                raise ValueError("EXECUTION_UNKNOWN_WITH_DETAILS")
            return self
        if any(x is None for x in (self.player_entity_id, self.scene_instance_id,
                                    self.proposal_id, self.trace_id, self.action_id, self.stage)):
            raise ValueError("EXECUTION_CORRELATION_REQUIRED")
        if self.status == "IN_PROGRESS":
            if self.stage == "terminal" or self.outcome_id or self.receipt_id or self.failure_code or self.outcome_world_revision:
                raise ValueError("EXECUTION_PROGRESS_IS_NOT_OUTCOME")
        elif (self.stage != "terminal" or self.step is not None or self.outcome_id is None
              or self.outcome_world_revision > self.world_revision):
            raise ValueError("EXECUTION_TERMINAL_EVIDENCE_REQUIRED")
        if self.status != "SUCCEEDED" and self.receipt_id is not None:
            raise ValueError("EXECUTION_RECEIPT_REQUIRES_SUCCESS")
        if self.status == "SUCCEEDED" and (self.failure_code or
                self.action_id == "prepare_drink" and (not self.receipt_id or self.drink_type is None)):
            raise ValueError("EXECUTION_DRINK_RECEIPT_REQUIRED")
        return self


class CognitiveContextSnapshot(BaseModel):
    """Minimal E5A cognition input; all partitions are engine-neutral and bounded."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    run_id: str = Field(min_length=1, max_length=160)
    agent_entity_id: EntityId
    trigger: CognitiveTriggerContext
    current_event: CognitiveEventContext | None = None
    character: CognitiveCharacterContext
    behavior_mode: CognitiveBehaviorModeContext | None = None
    daily_behavior: CognitiveDailyBehaviorContext | None = None
    relevant_player: CognitivePlayerContext | None = None
    perception: PerceptionSnapshot
    counter_work: CounterWorkContext | None = None
    scene_facts: SceneFacts | None = None
    capabilities: CognitiveCapabilityManifest | None = None
    runtime_state: CognitiveExecutionState | None = None

    @model_serializer(mode="wrap")
    def omit_absent_execution_state(self, handler):
        # Legacy payload/budget parity: no empty partition or extra contract for old senders.
        data = handler(self)
        if self.runtime_state is None:
            data.pop("runtime_state", None)
        return data

    @model_validator(mode="after")
    def validate_context_correlation(self) -> "CognitiveContextSnapshot":
        if self.trigger.agent_entity_id != self.agent_entity_id:
            raise ValueError("COGNITIVE_TRIGGER_AGENT_MISMATCH")
        if self.perception.observer_entity_id != self.agent_entity_id:
            raise ValueError("COGNITIVE_PERCEPTION_OBSERVER_MISMATCH")
        if self.runtime_state is not None:
            if (self.runtime_state.actor_entity_id != self.agent_entity_id or
                    self.runtime_state.player_entity_id != (self.relevant_player.player_entity_id if self.relevant_player else None)):
                raise ValueError("EXECUTION_OBSERVER_PLAYER_MISMATCH")
            if self.current_event is not None and (
                    self.runtime_state.captured_at < self.current_event.occurred_at or
                    self.current_event.has_world_revision and self.runtime_state.world_revision < self.current_event.world_revision):
                raise ValueError("EXECUTION_SNAPSHOT_PRECEDES_INPUT")
        if self.current_event is not None and self.trigger.source_event_id != self.current_event.event_id:
            raise ValueError("COGNITIVE_SOURCE_EVENT_MISMATCH")
        if self.scene_facts is not None:
            visible = {entity.entity_id: entity for entity in self.perception.entities
                       if entity.visibility == "current_visible"}
            own_seat = self.scene_facts.self.seat_entity_id if self.scene_facts.self else None
            for seat in self.scene_facts.observed_seats:
                perceived = visible.get(seat.seat_entity_id)
                if perceived is None or perceived.kind != "environment" or "seat" not in perceived.semantic_tags:
                    raise ValueError("SCENE_SEAT_NOT_CURRENT_VISIBLE")
                occupant = visible.get(seat.occupant_entity_id)
                if seat.occupant_entity_id is not None and (occupant is None or occupant.kind != "npc"):
                    raise ValueError("SCENE_OCCUPANT_NOT_CURRENT_VISIBLE")
                if seat.relation_to_self != "unknown" and own_seat is None:
                    raise ValueError("SCENE_RELATION_WITHOUT_SELF_SEAT")
                if (seat.relation_to_self == "self") != (seat.seat_entity_id == own_seat):
                    raise ValueError("SCENE_SELF_RELATION_MISMATCH")
        return self


class CognitiveRecallSubjectIdentity(BaseModel):
    """Unity-resolved current entity identity; never serialized into a model prompt."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    entity_id: EntityId
    kind: Literal["player", "npc"]
    identity_namespace: str | None = Field(default=None, max_length=80)
    identity_type: str | None = Field(default=None, max_length=80)
    persistent_id: str | None = Field(default=None, max_length=160)

    @field_validator("identity_namespace", "identity_type", "persistent_id", mode="before")
    @classmethod
    def normalize_unity_empty_identity(cls, value):
        # JsonUtility writes null string fields as empty strings.
        return None if value == "" else value

    @model_validator(mode="after")
    def validate_identity(self) -> "CognitiveRecallSubjectIdentity":
        parts = (self.identity_namespace, self.identity_type, self.persistent_id)
        if any(value is not None for value in parts) and not all(
            value is not None and value.strip() for value in parts
        ):
            raise ValueError("RECALL_IDENTITY_INCOMPLETE")
        return self


class CognitiveRecallIdentityContext(BaseModel):
    """Private wire input from Unity; owner and current subjects are not model fields."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    observer: CognitiveRecallSubjectIdentity
    requester_entity_id: EntityId | None = None
    current_subjects: tuple[CognitiveRecallSubjectIdentity, ...] = Field(default=(), max_length=16)

    @field_validator("requester_entity_id", mode="before")
    @classmethod
    def normalize_unity_empty_requester(cls, value):
        return None if value == "" else value

    @model_validator(mode="after")
    def validate_unique_entities(self) -> "CognitiveRecallIdentityContext":
        ids = [subject.entity_id for subject in self.current_subjects]
        if len(ids) != len(set(ids)):
            raise ValueError("RECALL_SUBJECT_ENTITY_DUPLICATE")
        return self


class CognitiveActionAlternative(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    alternative_id: str = Field(min_length=1, max_length=120)
    description: str = Field(min_length=1, max_length=120)
    goal: GoalProposal


class CognitiveActionFeedback(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    original_request_id: str = Field(min_length=1, max_length=160)
    original_run_id: str = Field(min_length=1, max_length=160)
    outcome_id: str = Field(min_length=1, max_length=240)
    proposal_id: str = Field(min_length=1, max_length=120)
    proposal_revision: int = Field(ge=0, lt=2)
    trace_id: str = Field(min_length=1, max_length=160)
    world_revision: int = Field(ge=0)
    completed_actions: int = Field(ge=0)
    failure_code: str = Field(min_length=1, max_length=80)
    detail: str = Field(max_length=240)
    failed_goal: GoalProposal
    allowed_alternatives: tuple[CognitiveActionAlternative, ...] = Field(max_length=4)

    @model_validator(mode="after")
    def validate_alternatives(self):
        ids = [a.alternative_id for a in self.allowed_alternatives]
        if len(ids) != len(set(ids)):
            raise ValueError("COGNITIVE_RECOVERY_ALTERNATIVES_INVALID")
        if any(a.goal.type != self.failed_goal.type or a.goal.target_entity_id == self.failed_goal.target_entity_id or a.goal.destination_entity_id != self.failed_goal.destination_entity_id
               for a in self.allowed_alternatives):
            raise ValueError("COGNITIVE_RECOVERY_SCOPE_CHANGED")
        return self


class CognitiveRequest(BaseModel):
    """Event-capable cognition entry; world events do not require a fabricated utterance."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    message_type: Literal["cognitive_request"]
    schema_version: Literal["1.0", "1.1"]
    request_id: str = Field(min_length=1, max_length=160)
    trace_id: str = Field(min_length=1, max_length=160)
    run: CognitiveRunContext
    trigger: CognitiveTriggerContext
    context: CognitiveContextSnapshot
    recall_identity: CognitiveRecallIdentityContext | None = Field(default=None, exclude=True)
    reply_language: ReplyLanguage = "zh-CN"
    action_feedback: CognitiveActionFeedback | None = Field(default=None, exclude=True)
    context_diagnostics_version: Literal[1] | None = Field(default=None, exclude=True)
    task_runtime: TaskRuntimeContext | None = Field(default=None, exclude=True)
    task_consideration: "TaskConsiderationContext | None" = Field(default=None, exclude=True)

    @model_validator(mode="after")
    def validate_request_correlation(self) -> "CognitiveRequest":
        if (self.schema_version == "1.1") != (self.task_runtime is not None):
            raise ValueError("TASK_PROTOCOL_CONTEXT_REQUIRED")
        automatic = self.context.current_event is not None and self.context.current_event.event_type == 'npc.task_consideration'
        if automatic != (self.task_consideration is not None) or automatic and self.schema_version != '1.1':
            raise ValueError('TASK_CONSIDERATION_CONTEXT_REQUIRED')
        if self.run.run_id != self.context.run_id or self.run.agent_entity_id != self.context.agent_entity_id:
            raise ValueError("COGNITIVE_RUN_CONTEXT_MISMATCH")
        if self.run.trigger_id != self.trigger.trigger_id or self.trigger != self.context.trigger:
            raise ValueError("COGNITIVE_TRIGGER_CONTEXT_MISMATCH")
        if self.run.trigger_type != self.trigger.trigger_type:
            raise ValueError("COGNITIVE_RUN_TRIGGER_TYPE_MISMATCH")
        if self.context.current_event is not None and self.context.current_event.trace_id is not None and \
                self.context.current_event.trace_id.strip() and \
                self.trace_id != self.context.current_event.trace_id:
            raise ValueError("COGNITIVE_EVENT_TRACE_MISMATCH")
        if self.recall_identity is not None:
            if (self.recall_identity.observer.entity_id != self.run.agent_entity_id or
                    self.recall_identity.observer.kind != "npc"):
                raise ValueError("RECALL_OBSERVER_MISMATCH")
            event = self.context.current_event
            if self.recall_identity.requester_entity_id is not None and (
                event is None or self.recall_identity.requester_entity_id != event.source_entity_id
            ):
                raise ValueError("RECALL_REQUESTER_MISMATCH")
        feedback = self.action_feedback
        if feedback is not None:
            event, state = self.context.current_event, self.context.runtime_state
            if (self.schema_version != "1.0" or event is None or state is None or state.status != "FAILED" or
                    event.event_type != "action.failed" or event.event_id != feedback.outcome_id or
                    event.source_entity_id != self.run.agent_entity_id or event.target_entity_id != self.run.agent_entity_id or
                    state.outcome_id != feedback.outcome_id or state.proposal_id != feedback.proposal_id or
                    state.proposal_revision != feedback.proposal_revision or state.trace_id != feedback.trace_id or
                    state.world_revision != feedback.world_revision or self.trace_id != feedback.trace_id):
                raise ValueError("COGNITIVE_OUTCOME_CORRELATION_INVALID")
        state = self.context.runtime_state
        if state is not None and state.status != "UNKNOWN":
            wire = self.recall_identity
            player = next((p for p in wire.current_subjects if p.entity_id == state.player_entity_id and p.kind == "player"), None) if wire else None
            if wire is None or not wire.observer.persistent_id or player is None or not player.persistent_id:
                raise ValueError("EXECUTION_CURRENT_IDENTITIES_REQUIRED")
            if self.task_runtime is not None and self.task_runtime.scene_instance_id != state.scene_instance_id:
                raise ValueError("EXECUTION_SCENE_MISMATCH")
        return self


class TaskRuntimeSyncRequest(BaseModel):
    """Read-only host synchronization, never a model tool or a cognitive Run."""
    model_config = ConfigDict(extra='forbid', frozen=True, strict=True)
    message_type: Literal['task_runtime_sync']
    schema_version: Literal['1.1']
    request_id: str = Field(pattern=r'^[A-Za-z0-9_-]{1,160}$')
    agent_entity_id: EntityId
    recall_identity: CognitiveRecallIdentityContext
    task_runtime: TaskRuntimeContext
    is_idle: bool
    offer_tasks: bool = True
    counter_condition: 'TaskCondition | None' = None
    counter_observed: bool = False
    player_visible: bool = True

    @model_validator(mode='after')
    def condition_scope(self):
        if self.counter_condition is not None and (not self.counter_observed or
                self.counter_condition.scene_instance_id != self.task_runtime.scene_instance_id):
            raise ValueError('TASK_COUNTER_SNAPSHOT_INVALID')
        return self


SERVICE_ZONE_EVENT = "player.dwelled_in_service_zone"


def is_service_zone_consideration(request: CognitiveRequest) -> bool:
    """Recognize one bounded Unity condition, never player/world-action authorization."""
    import math
    event = request.context.current_event
    if event is None or event.event_type != SERVICE_ZONE_EVENT:
        return False
    payload = {item.key: item.value for item in event.payload}
    if len(payload) != len(event.payload) or set(payload) != {"zone_id", "condition_token", "condition_revision", "dwell_seconds"}:
        return False
    try:
        duration = float(payload["dwell_seconds"])
        revision = int(payload["condition_revision"])
    except (TypeError, ValueError):
        return False
    daily = request.context.daily_behavior
    player = request.context.relevant_player
    token = payload["condition_token"]
    return (request.run.trigger_type == "event" and request.trigger.source_event_id == event.event_id
            and event.target_entity_id == request.run.agent_entity_id and player is not None
            and event.source_entity_id == player.player_entity_id and bool(event.area_entity_id)
            and event.source_observation_id is None and event.trace_id == request.trace_id
            and len(token) == 32 and all(c in "0123456789abcdef" for c in token)
            and 0 <= revision <= 2147483647 and math.isfinite(duration) and .1 <= duration <= 600
            and bool(payload["zone_id"]) and daily is not None and daily.current_mode == "rest"
            and any(c.target_mode == "work" for c in daily.candidates))


class SystemDailyModeIntent(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    decision_kind: Literal["DAILY_MODE_INTENT"]
    target_daily_mode: Literal["work"]


class SystemKeepMode(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    decision_kind: Literal["NO_ACTION"]


class CognitiveActionProposal(BaseModel):
    """Untrusted high-level action and structured entity bindings chosen by the model."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    action_id: EntityId
    bindings: dict[EntityId, Annotated[str, Field(strict=True, min_length=1, max_length=120)]] = Field(min_length=1, max_length=16)


class SystemAutonomousActionIntent(BaseModel):
    """One silent formal intent; source policy restricts the actual action and target."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    decision_kind: Literal["AUTONOMOUS_ACTION_INTENT"]
    action_proposal: CognitiveActionProposal


class CognitiveActionControl(BaseModel):
    """Untrusted control of the offered execution, never a new action or a world result."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    operation: Literal["cancel_active_goal"]
    proposal_id: str = Field(min_length=1, max_length=160)
    proposal_revision: int = Field(ge=0, strict=True)
    trace_id: str = Field(min_length=1, max_length=160)


class CognitiveDecision(BaseModel):
    """Minimal Runtime v2 cognition result; tool and memory slots remain out of scope."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    decision_kind: CognitiveDecisionKind
    episode_directive: EpisodeDirective | None = None
    dialogue_turn_policy: Literal["WAIT_FOR_PLAYER", "MAY_CONTINUE"] | None = None
    utterance: str | None = Field(default=None, max_length=250)

    _utterance_limit = field_validator("utterance")(validate_utterance)
    emotion: Emotion | None = None
    expression_icon: ExpressionIcon | None = Field(default=None, description=(
        "Optional rare visual accent only. Default null/none. Use happy only for marked delight, "
        "confused for genuine puzzlement, surprised for a real surprise, thinking for a marked pause to ponder. "
        "Ordinary greetings, questions, explanations and friendly chat normally have no icon. "
        "This grants no action or factual authority."))
    gesture: Gesture | None = None
    look_at: EntityId | None = None
    action_proposal: CognitiveActionProposal | None = None
    target_daily_mode: DailyBehaviorMode | None = None
    action_control: CognitiveActionControl | None = None
    recovery_alternative_id: str | None = Field(default=None, min_length=1, max_length=120)

    @model_serializer(mode="wrap")
    def omit_absent_action_control(self, handler):
        data = handler(self)
        if self.action_control is None:
            data.pop("action_control", None)
        if self.recovery_alternative_id is None:
            data.pop("recovery_alternative_id", None)
        if self.expression_icon in (None, "none"):
            data.pop("expression_icon", None)
        return data

    @model_validator(mode="after")
    def validate_decision_shape(self) -> "CognitiveDecision":
        if self.expression_icon not in (None, "none") and self.decision_kind not in ("DIRECT_RESPONSE", "ACTION_PROPOSAL"):
            raise ValueError("COGNITIVE_NO_ACTION_PRESENTATION_FORBIDDEN")
        if self.recovery_alternative_id is not None and (self.decision_kind != "DIRECT_RESPONSE" or
                self.action_proposal is not None or self.action_control is not None or
                self.target_daily_mode is not None or self.dialogue_turn_policy == "MAY_CONTINUE"):
            raise ValueError("COGNITIVE_RECOVERY_COMBINATION_FORBIDDEN")
        if self.action_control is not None and (self.decision_kind != "DIRECT_RESPONSE" or
                self.target_daily_mode is not None or self.action_proposal is not None or
                self.dialogue_turn_policy == "MAY_CONTINUE"):
            raise ValueError("COGNITIVE_ACTION_CONTROL_COMBINATION_FORBIDDEN")
        if self.target_daily_mode is not None and self.decision_kind not in ("DIRECT_RESPONSE", "ACTION_PROPOSAL"):
            raise ValueError("COGNITIVE_DAILY_MODE_DECISION_FORBIDDEN")
        if self.dialogue_turn_policy == "MAY_CONTINUE" and (
                self.decision_kind != "DIRECT_RESPONSE" or self.episode_directive == "END"):
            raise ValueError("COGNITIVE_CONTINUATION_POLICY_INVALID")
        if self.decision_kind == "DIRECT_RESPONSE":
            if self.utterance is None or not self.utterance.strip():
                raise ValueError("COGNITIVE_DIRECT_RESPONSE_UTTERANCE_REQUIRED")
            if self.action_proposal is not None:
                raise ValueError("COGNITIVE_DIRECT_RESPONSE_ACTION_FORBIDDEN")
            return self
        if self.decision_kind == "ACTION_PROPOSAL":
            if self.action_proposal is None:
                raise ValueError("COGNITIVE_ACTION_PROPOSAL_REQUIRED")
            if self.episode_directive == "END":
                raise ValueError("COGNITIVE_ACTION_EPISODE_END_FORBIDDEN")
            if self.utterance is not None and not self.utterance.strip():
                raise ValueError("COGNITIVE_ACTION_UTTERANCE_BLANK")
            return self
        if any(value is not None for value in
               (self.utterance, self.emotion, self.gesture, self.look_at, self.action_proposal)):
            raise ValueError("COGNITIVE_NO_ACTION_PRESENTATION_FORBIDDEN")
        return self


class CognitiveResponse(BaseModel):
    """Backend-to-Unity success envelope for one Unity-owned CognitiveRun."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    message_type: Literal["cognitive_response"] = "cognitive_response"
    schema_version: Literal["1.0", "1.1"] = "1.0"
    request_id: str = Field(min_length=1, max_length=160)
    trace_id: str = Field(min_length=1, max_length=160)
    run_id: str = Field(min_length=1, max_length=160)
    agent_entity_id: EntityId
    decision: CognitiveDecision | SystemDailyModeIntent | SystemAutonomousActionIntent
    validated_goal: "GoalProposal | None" = None
    task_receipt: CognitiveTaskReceipt | None = None
    task_result: Literal["none", "committed", "failed"] | None = None
    task_execution: TaskExecutionTicket | None = None
    context_status: CapacityStatus | None = None

    @model_validator(mode="after")
    def validate_action_handoff(self) -> "CognitiveResponse":
        if self.task_execution is not None:
            ticket = self.task_execution
            if (self.schema_version != '1.1' or self.decision.decision_kind != 'ACTION_PROPOSAL'
                    or self.task_receipt is not None or self.decision.target_daily_mode is not None
                    or (ticket.request_id,ticket.trace_id,ticket.run_id,ticket.agent_entity_id) !=
                       (self.request_id,self.trace_id,self.run_id,self.agent_entity_id)):
                raise ValueError('TASK_EXECUTION_CORRELATION_INVALID')
        expected_task_result = ("committed" if self.task_receipt.committed else "failed") if self.task_receipt else "none"
        if (self.schema_version == "1.0" and (self.task_receipt is not None or self.task_result is not None) or
                self.schema_version == "1.1" and self.task_result != expected_task_result):
            raise ValueError("TASK_RESPONSE_RESULT_INVALID")
        if self.task_receipt is not None:
            receipt = self.task_receipt
            if (self.schema_version != "1.1" or self.decision.decision_kind not in ("DIRECT_RESPONSE", "NO_ACTION")
                    or self.decision.target_daily_mode is not None or
                    (receipt.request_id, receipt.trace_id, receipt.run_id, receipt.agent_entity_id) !=
                    (self.request_id, self.trace_id, self.run_id, self.agent_entity_id)):
                raise ValueError("TASK_RECEIPT_CORRELATION_INVALID")
        if self.decision.decision_kind in ("ACTION_PROPOSAL", "AUTONOMOUS_ACTION_INTENT"):
            if self.validated_goal is None:
                raise ValueError("COGNITIVE_VALIDATED_GOAL_REQUIRED")
            proposal = self.decision.action_proposal
            if (self.validated_goal.type != proposal.action_id or
                    self.validated_goal.target_entity_id != proposal.bindings.get("target") or
                    self.validated_goal.destination_entity_id != proposal.bindings.get("destination")):
                raise ValueError("COGNITIVE_VALIDATED_GOAL_MISMATCH")
        elif self.validated_goal is not None:
            raise ValueError("COGNITIVE_ACTIONLESS_VALIDATED_GOAL_FORBIDDEN")
        return self


class GoalProposal(BaseModel):
    """M4.0 中模型唯一可提出、尚未被 Unity 信任的高层目标。"""

    model_config = ConfigDict(extra="forbid")

    type: GoalType
    target_entity_id: EntityId | None = None
    destination_entity_id: EntityId | None = None
    drink_type: DrinkType | None = None

    @model_validator(mode="after")
    def validate_goal_arguments(self) -> "GoalProposal":
        if self.type == "prepare_drink":
            if self.drink_type is None or self.target_entity_id is not None or self.destination_entity_id is not None:
                raise ValueError("PREPARE_DRINK_ARGUMENTS_INVALID")
            return self
        if self.target_entity_id is None or self.drink_type is not None:
            raise ValueError("ENTITY_ACTION_ARGUMENTS_INVALID")
        if self.type == "bring_object" and self.destination_entity_id is None:
            raise ValueError("BRING_OBJECT_DESTINATION_REQUIRED")
        if self.type in ("move_to_entity", "open_door", "clean_counter") and self.destination_entity_id is not None:
            raise ValueError("TARGET_ONLY_ACTION_DESTINATION_FORBIDDEN")
        return self
