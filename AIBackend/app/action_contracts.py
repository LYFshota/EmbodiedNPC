"""Bounded internal action descriptors and execution authorization."""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from .contracts import CognitiveActionBindingCapability, EntityId


ActionId = Annotated[str, Field(pattern=r"^[a-z][a-z0-9_]{0,63}$")]
ActionVersion = Annotated[str, Field(pattern=r"^[0-9]+\.[0-9]+$")]
BoundedLabel = Annotated[str, Field(min_length=1, max_length=80)]
BindingKind = Literal["entity_ref", "enum", "number", "boolean", "string"]
SelectionMode = Literal["exact", "any", "nearest", "random"]
ConfirmationPolicy = Literal["none", "before_execution", "on_rebind"]
InterruptionPolicy = Literal["cancel", "pause", "finish_atomic_step"]


class ActionBindingDescriptor(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    name: ActionId
    kind: BindingKind
    required: bool = True
    enum_values: tuple[BoundedLabel, ...] = Field(default=(), max_length=32)
    minimum: float | None = None
    maximum: float | None = None
    max_length: int | None = Field(default=None, ge=1, le=500)

    @model_validator(mode="after")
    def validate_kind_constraints(self) -> "ActionBindingDescriptor":
        if self.kind == "enum":
            if not self.enum_values or len(self.enum_values) != len(set(self.enum_values)):
                raise ValueError("ACTION_ENUM_VALUES_REQUIRED_AND_UNIQUE")
        elif self.enum_values:
            raise ValueError("ACTION_ENUM_VALUES_FORBIDDEN")
        if self.kind == "number":
            if self.minimum is None or self.maximum is None:
                raise ValueError("ACTION_NUMBER_RANGE_REQUIRED")
            if self.minimum > self.maximum:
                raise ValueError("ACTION_NUMBER_RANGE_INVALID")
        elif self.minimum is not None or self.maximum is not None:
            raise ValueError("ACTION_NUMBER_RANGE_FORBIDDEN")
        if self.kind == "string" and self.max_length is None:
            raise ValueError("ACTION_STRING_LENGTH_REQUIRED")
        if self.kind != "string" and self.max_length is not None:
            raise ValueError("ACTION_STRING_LENGTH_FORBIDDEN")
        return self


class ActionDescriptor(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    action_id: ActionId
    version: ActionVersion
    description: str = Field(min_length=1, max_length=240)
    binding_schema: tuple[ActionBindingDescriptor, ...] = Field(default=(), max_length=16)
    target_requirements: tuple[BoundedLabel, ...] = Field(default=(), max_length=16)
    available: bool = True
    default_timeout_seconds: float = Field(gt=0, le=600)
    interruption_policy: InterruptionPolicy
    confirmation_policy: ConfirmationPolicy

    @model_validator(mode="after")
    def validate_unique_fields(self) -> "ActionDescriptor":
        names = [binding.name for binding in self.binding_schema]
        if len(names) != len(set(names)):
            raise ValueError("ACTION_BINDING_NAMES_DUPLICATE")
        if len(self.target_requirements) != len(set(self.target_requirements)):
            raise ValueError("ACTION_TARGET_REQUIREMENTS_DUPLICATE")
        return self


@dataclass(frozen=True)
class ActionRegistration:
    """Minimal internal registration; execution stays on the existing lifecycle in Phase B."""

    descriptor: ActionDescriptor
    # Developer-owned semantic guidance, not a wire contract or capability grant.
    model_usage: str = ""


def binding_contract_signature(
    bindings: Iterable[ActionBindingDescriptor | CognitiveActionBindingCapability],
) -> dict[str, tuple[str, bool, tuple[str, ...]]]:
    """The existing Cognition validator's exact, order-independent binding comparison.

    Enum order remains significant, as before. Do not use descriptions or model
    guidance to repair a mismatched runtime contract.
    """
    return {
        binding.name: (binding.kind, binding.required, tuple(binding.enum_values))
        for binding in bindings
    }


class ExecutionAuthorization(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    action_id: ActionId
    selection_mode: SelectionMode
    authorized_entity_ids: tuple[EntityId, ...] = Field(min_length=1, max_length=16)
    confirmation_policy: ConfirmationPolicy
    proposal_revision: int = Field(ge=0)
    world_revision: int = Field(ge=0)

    @model_validator(mode="after")
    def validate_scope(self) -> "ExecutionAuthorization":
        if len(self.authorized_entity_ids) != len(set(self.authorized_entity_ids)):
            raise ValueError("ACTION_AUTHORIZATION_ENTITY_IDS_DUPLICATE")
        if self.selection_mode == "exact" and len(self.authorized_entity_ids) != 1:
            raise ValueError("ACTION_EXACT_AUTHORIZATION_REQUIRES_ONE_ENTITY")
        return self

    def authorizes(self, action_id: str, entity_id: str) -> bool:
        return action_id == self.action_id and entity_id in self.authorized_entity_ids
