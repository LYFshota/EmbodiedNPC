"""Persisted, pair-scoped Relationship Reflection results for Runtime v2."""

from __future__ import annotations

from enum import Enum

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from .memory_consolidation_context import TrustedPersistentIdentity


class RelationshipReflectionOutcome(str, Enum):
    INTERPRETATION = "INTERPRETATION"
    NO_CHANGE = "NO_CHANGE"


class RelationshipReflectionRecord(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    reflection_id: str = Field(min_length=1, max_length=160)
    observer: TrustedPersistentIdentity
    subject: TrustedPersistentIdentity
    episode_id: str = Field(min_length=1, max_length=160)
    episode_input_hash: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    outcome: RelationshipReflectionOutcome
    interpretation: str | None = Field(default=None, max_length=500)
    evidence_event_ids: tuple[str, ...] = Field(min_length=1, max_length=64)
    policy_version: str = Field(min_length=1, max_length=160)
    generated_at: float = Field(ge=0, allow_inf_nan=False)

    @field_validator("observer", "subject")
    @classmethod
    def validate_identity(cls, value: TrustedPersistentIdentity) -> TrustedPersistentIdentity:
        for part, limit in zip(
            (value.identity_namespace, value.identity_type, value.persistent_id), (80, 80, 160)
        ):
            if not isinstance(part, str) or not part.strip() or len(part) > limit:
                raise ValueError("DURABLE_IDENTITY_REQUIRED")
        return value

    @field_validator("evidence_event_ids")
    @classmethod
    def validate_evidence(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        if any(not item.strip() or len(item) > 160 for item in value):
            raise ValueError("EVIDENCE_ID_INVALID")
        if len(set(value)) != len(value):
            raise ValueError("DUPLICATE_EVIDENCE_ID")
        return value

    @model_validator(mode="after")
    def validate_outcome(self) -> "RelationshipReflectionRecord":
        if self.outcome is RelationshipReflectionOutcome.INTERPRETATION:
            if self.interpretation is None or not self.interpretation.strip():
                raise ValueError("INTERPRETATION_REQUIRED")
        elif self.interpretation is not None:
            raise ValueError("NO_CHANGE_INTERPRETATION_MUST_BE_NULL")
        return self
