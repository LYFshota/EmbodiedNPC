"""Versioned, observer-local raw experience contracts for Memory v1 Mem-P1."""

from __future__ import annotations

from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


ProtocolId = Annotated[str, Field(pattern=r"^[A-Za-z0-9_-]+$", min_length=1, max_length=160)]
EntityId = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]*$", max_length=120)]
ExperienceEventType = Literal[
    "observation",
    "reported_observation",
    "dialogue_input",
    "dialogue_response",
    "social_response",
    "goal_succeeded",
    "goal_failed",
    "goal_cancelled",
    "goal_timed_out",
    "skill_succeeded",
    "skill_failed",
    "skill_cancelled",
    "skill_timed_out",
]
ExperienceSourceType = Literal["observation", "dialogue", "goal_outcome", "skill_result", "social_interaction"]
ExperienceProvenance = Literal[
    "WITNESSED",
    "HEARD",
    "REPORTED",
    "INFERRED",
    "EXECUTED_BY_SELF",
    "SYSTEM_CONFIRMED",
]
ExperienceAppendStatus = Literal[
    "inserted", "already_exists", "id_conflict", "rejected", "storage_failed"
]


class PersistentParticipantIdentity(BaseModel):
    """Trusted long-lived identity attached to an EntityId that participated in this event."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    entity_id: EntityId
    identity_namespace: Annotated[str, Field(min_length=1, max_length=80)]
    identity_type: Annotated[str, Field(min_length=1, max_length=80)]
    persistent_id: Annotated[str, Field(min_length=1, max_length=160)]

    @field_validator("identity_namespace", "identity_type", "persistent_id")
    @classmethod
    def validate_identity_component(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("PERSISTENT_IDENTITY_COMPONENT_EMPTY")
        return value


def _validate_json_value(value: Any, *, depth: int = 0) -> None:
    if depth > 4:
        raise ValueError("STRUCTURED_PAYLOAD_TOO_DEEP")
    if value is None or isinstance(value, (bool, int, float)):
        return
    if isinstance(value, str):
        if len(value) > 1000:
            raise ValueError("STRUCTURED_PAYLOAD_STRING_TOO_LONG")
        return
    if isinstance(value, list):
        if len(value) > 32:
            raise ValueError("STRUCTURED_PAYLOAD_LIST_TOO_LONG")
        for item in value:
            _validate_json_value(item, depth=depth + 1)
        return
    if isinstance(value, dict):
        if len(value) > 32:
            raise ValueError("STRUCTURED_PAYLOAD_OBJECT_TOO_LARGE")
        for key, item in value.items():
            if not isinstance(key, str) or not key or len(key) > 120:
                raise ValueError("STRUCTURED_PAYLOAD_KEY_INVALID")
            _validate_json_value(item, depth=depth + 1)
        return
    raise ValueError("STRUCTURED_PAYLOAD_VALUE_INVALID")


class ExperienceDraft(BaseModel):
    """Caller-authored source evidence. Ledger-owned fields are deliberately absent."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    schema_version: Literal["experience-draft/1.0", "experience-draft/1.1"] = "experience-draft/1.0"
    observer_id: EntityId
    event_type: ExperienceEventType
    source_type: ExperienceSourceType
    source_session_id: ProtocolId
    clock_domain: Literal["unity_realtime", "backend_utc"]
    source_event_id: ProtocolId | None = None
    source_observation_ids: tuple[ProtocolId, ...] = Field(default=(), max_length=32)
    actor_entity_id: EntityId | None = None
    target_entity_ids: tuple[EntityId, ...] = Field(default=(), max_length=32)
    persistent_participants: tuple[PersistentParticipantIdentity, ...] = Field(default=(), max_length=32)
    structured_payload: dict[str, Any] = Field(default_factory=dict)
    natural_language_payload: str | None = Field(default=None, max_length=1000)
    occurred_at: float = Field(ge=0, allow_inf_nan=False)
    observed_at: float = Field(ge=0, allow_inf_nan=False)
    world_revision: int | None = Field(default=None, ge=0)
    request_id: ProtocolId | None = None
    trace_id: ProtocolId | None = None
    conversation_id: str | None = Field(default=None, min_length=1, max_length=160)
    confidence: float | None = Field(default=None, ge=0, le=1, allow_inf_nan=False)
    provenance: ExperienceProvenance

    @field_validator("source_observation_ids", "target_entity_ids")
    @classmethod
    def validate_unique_values(cls, values: tuple[str, ...]) -> tuple[str, ...]:
        if len(values) != len(set(values)):
            raise ValueError("DUPLICATE_EXPERIENCE_COLLECTION_VALUE")
        return values

    @field_validator("structured_payload")
    @classmethod
    def validate_structured_payload(cls, value: dict[str, Any]) -> dict[str, Any]:
        _validate_json_value(value)
        return value

    @model_validator(mode="after")
    def validate_source_semantics(self) -> "ExperienceDraft":
        if self.persistent_participants and not self.schema_version.endswith("/1.1"):
            raise ValueError("PERSISTENT_PARTICIPANTS_REQUIRE_SCHEMA_1_1")
        participant_ids = {
            value for value in (self.actor_entity_id, *self.target_entity_ids) if value is not None
        }
        hearing = self.structured_payload.get('known_fields', {}).get('event_type') == 'heard_speech'
        if hearing and not is_heard_speech(self):
            raise ValueError('HEARD_SPEECH_EVIDENCE_INVALID')
        if (self.source_type == "observation" and self.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote') or hearing:
            participant_ids.add(self.observer_id)  # Identity of the witness, not a fabricated recipient.
        persistent_entity_ids = tuple(value.entity_id for value in self.persistent_participants)
        if len(persistent_entity_ids) != len(set(persistent_entity_ids)):
            raise ValueError("DUPLICATE_PERSISTENT_PARTICIPANT_ENTITY")
        if any(value not in participant_ids for value in persistent_entity_ids):
            raise ValueError("PERSISTENT_PARTICIPANT_NOT_IN_EXPERIENCE")
        if self.observed_at < self.occurred_at:
            raise ValueError("OBSERVED_BEFORE_OCCURRED")
        if self.event_type == "social_response" or self.source_type == "social_interaction":
            if (self.event_type != "social_response" or self.source_type != "social_interaction"
                    or self.actor_entity_id != self.observer_id or len(self.target_entity_ids) != 1
                    or not self.source_event_id or not self.request_id or not self.trace_id
                    or self.conversation_id is not None or not self.natural_language_payload
                    or len(self.natural_language_payload) > 120 or self.provenance != "EXECUTED_BY_SELF"
                    or len(self.source_observation_ids) != 1
                    or self.structured_payload.get("parent_source_event_id") != self.source_event_id
                    or self.structured_payload.get("actually_presented") is not True
                    or self.structured_payload.get("model_content_is_not_world_truth") is not True
                    or not self.structured_payload.get("run_id")
                    or self.structured_payload.get("emote_id") not in {"wave", "cheer", "flex"}):
                raise ValueError("SOCIAL_RESPONSE_CORRELATION_INVALID")
        if self.source_type == "observation":
            if len(self.source_observation_ids) != 1:
                raise ValueError("OBSERVATION_SOURCE_ID_REQUIRED")
            revision = self.structured_payload.get("perception_revision")
            if type(revision) is not int or revision < 1:
                raise ValueError("PERCEPTION_REVISION_REQUIRED")
        if self.source_type == "dialogue":
            if self.structured_payload.get("initiative") == "dialogue_continuation":
                payload = self.structured_payload
                if (self.event_type != "dialogue_response" or self.actor_entity_id != self.observer_id
                        or len(self.target_entity_ids) != 1 or not self.source_event_id
                        or self.provenance != "EXECUTED_BY_SELF" or not self.request_id
                        or not self.conversation_id or not self.natural_language_payload
                        or payload.get("continuation_index") != 1
                        or not payload.get("parent_request_id") or not payload.get("parent_event_id")
                        or self.request_id == payload.get("parent_request_id")
                        or self.source_event_id == payload.get("parent_event_id")
                        or not payload.get("last_response_event_id")
                        or type(payload.get("last_response_seq")) is not int
                        or payload["last_response_seq"] < 1
                        or self.schema_version.endswith("/1.0")
                        or len(self.persistent_participants) != 2):
                    raise ValueError("DIALOGUE_CONTINUATION_CORRELATION_INVALID")
            proactive = self.structured_payload.get("initiative") == "proactive"
            if proactive:
                required = (
                    "behavior_kind", "trigger_id", "decision_id", "output_id",
                    "source_memory_ids",
                )
                if (
                    self.event_type != "dialogue_response"
                    or not self.source_event_id
                    or self.actor_entity_id != self.observer_id
                    or len(self.target_entity_ids) != 1
                    or self.natural_language_payload is None
                    or self.request_id is not None
                    or self.conversation_id is not None
                    or self.provenance != "EXECUTED_BY_SELF"
                    or any(key not in self.structured_payload for key in required)
                    or self.structured_payload.get("output_id") != self.source_event_id
                    or self.structured_payload.get("behavior_kind") not in {
                        "GREET", "ACKNOWLEDGE", "MENTION_MEMORY"
                    }
                ):
                    raise ValueError("PROACTIVE_DIALOGUE_CORRELATION_INVALID")
            elif not self.request_id or not self.conversation_id or self.natural_language_payload is None:
                raise ValueError("DIALOGUE_CORRELATION_REQUIRED")
        if self.source_type in ("goal_outcome", "skill_result"):
            if not self.source_event_id:
                raise ValueError("EXECUTION_SOURCE_ID_REQUIRED")
            if self.actor_entity_id != self.observer_id or self.provenance != "EXECUTED_BY_SELF":
                raise ValueError("SELF_EXECUTION_OWNERSHIP_REQUIRED")
        if self.event_type == "reported_observation":
            required = ("reporter_entity_id", "parent_memory_id", "report_conversation_id", "report_hop")
            if self.provenance != "REPORTED" or any(key not in self.structured_payload for key in required):
                raise ValueError("REPORT_PROVENANCE_REQUIRED")
            if self.structured_payload.get("report_hop") != 1:
                raise ValueError("REPORT_HOP_INVALID")
        return self


def is_heard_speech(event) -> bool:
    """Strict observer-local speech evidence, not a general participant exception."""
    fields = event.structured_payload.get('known_fields', {})
    return bool(event.event_type == 'observation' and event.source_type == 'observation'
        and event.provenance == 'HEARD' and event.conversation_id is None
        and event.structured_payload.get('source_channel') == 'Audio'
        and fields.get('event_type') == 'heard_speech'
        and fields.get('social_role') == 'bystander'
        and fields.get('observer_entity_id') == event.observer_id
        and isinstance(fields.get('text'), str) and 0 < len(fields['text'].strip()) <= 500
        and fields.get('occurrence_id') == event.source_event_id and fields.get('parent_source_event_id')
        and fields.get('source_channel') == 'Audio' and fields.get('model_content_is_not_world_truth') == 'true'
        and event.source_event_id and event.trace_id
        and fields.get('actor_entity_id') == event.actor_entity_id
        and event.observer_id != event.actor_entity_id and event.observer_id not in event.target_entity_ids
        and event.target_entity_ids == ((fields['target_entity_id'],) if fields.get('target_entity_id') else ()))


class ObserverExperienceEvent(ExperienceDraft):
    """Immutable append result with Backend-owned identity, ordering and ingestion time."""

    schema_version: Literal["observer-experience/1.0", "observer-experience/1.1"] = \
        "observer-experience/1.0"
    event_id: ProtocolId
    experience_seq: int = Field(ge=1)
    ingested_at_utc: float = Field(ge=0, allow_inf_nan=False)
    dedup_key: ProtocolId
    payload_hash: Annotated[str, Field(pattern=r"^[a-f0-9]{64}$")]


class ExperienceAppendRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    message_type: Literal["experience_append_request"] = "experience_append_request"
    schema_version: Literal["1.0"] = "1.0"
    request_id: ProtocolId
    trace_id: ProtocolId
    draft: ExperienceDraft


class ExperienceAppendResult(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    message_type: Literal["experience_append_result"] = "experience_append_result"
    schema_version: Literal["1.0"] = "1.0"
    request_id: ProtocolId
    trace_id: ProtocolId
    event_id: str = Field(max_length=160)
    observer_id: str = Field(max_length=120)
    experience_seq: int = Field(ge=0)
    status: ExperienceAppendStatus
    reason: str = Field(max_length=120)
