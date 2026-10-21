"""Backend-internal D2 model steps; Unity still receives CognitiveDecision only."""

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, RootModel, field_validator, model_validator

from .canonical_recall_models import CanonicalEvidenceSet, CanonicalReadFilter
from .contracts import CognitiveActionProposal, CognitiveDecision, SystemDailyModeIntent, SystemKeepMode, SystemAutonomousActionIntent
from .cognitive_task_models import TaskIntent, TaskReadPlan


class AutonomousWorkStep(RootModel[
    Annotated[SystemKeepMode | SystemAutonomousActionIntent,
              Field(discriminator="decision_kind")]
]):
    pass


class SystemModeStep(RootModel[
    Annotated[SystemKeepMode | SystemDailyModeIntent, Field(discriminator="decision_kind")]
]):
    """One silent terminal step. Does not widen player/Recall schemas."""


class MemoryRecallPlan(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    semantic_query: str = Field(min_length=1, max_length=512, description=(
        "The historical information requested by the current speaker, preserving changes and conditions. "
        "The wording of this query does not change whose history subject_ref selects."))
    subject_ref: str | None = Field(default=None, min_length=1, max_length=64, description=(
        "Whose information is requested, not the NPC who owns the memory. Select from the trusted catalog: "
        "requester_ref for the current speaker's preferences or experiences; the observer entry for the "
        "NPC's own history. Resolve the current utterance's meaning before writing semantic_query. "
        "Null is only an open-subject search within this observer's history, never broader access."))
    memory_type: Literal["EPISODIC", "SEMANTIC_FACT", "DERIVED_INSIGHT"] | None = None

    @field_validator("semantic_query")
    @classmethod
    def nonblank_query(cls, value):
        if not value.strip():
            raise ValueError("RECALL_QUERY_EMPTY")
        return value

    @model_validator(mode="after")
    def supported_filters(self):
        CanonicalReadFilter(memory_type=self.memory_type)
        return self


class MemoryRecallStep(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    decision_kind: Literal["MEMORY_RECALL"]
    recall_plan: MemoryRecallPlan


class DirectResponseStep(CognitiveDecision):
    """Model schema must require what the existing wire validator requires."""

    decision_kind: Literal["DIRECT_RESPONSE"]
    utterance: str = Field(min_length=1, max_length=250)
    action_proposal: None = None


class NoActionStep(CognitiveDecision):
    decision_kind: Literal["NO_ACTION"]
    utterance: None = None
    emotion: None = None
    gesture: None = None
    look_at: None = None
    action_proposal: None = None
    target_daily_mode: None = None


class ActionProposalStep(CognitiveDecision):
    decision_kind: Literal["ACTION_PROPOSAL"]
    action_proposal: CognitiveActionProposal
    episode_directive: Literal["CONTINUE"] | None = None


class ContinuationDirectResponseStep(DirectResponseStep):
    target_daily_mode: None = None


class TerminalCognitiveStep(RootModel[
    Annotated[DirectResponseStep | NoActionStep | ActionProposalStep, Field(discriminator="decision_kind")]
]):
    # Preserve the existing model-adapter schema marker; Unity's DTO is unchanged.
    model_config = ConfigDict(title="CognitiveDecision")


class CognitiveStepResult(RootModel[
    Annotated[DirectResponseStep | NoActionStep | ActionProposalStep | MemoryRecallStep,
              Field(discriminator="decision_kind")]
]):
    """Step 0 only. Step 1 validates CognitiveDecision directly, never this union."""


class DialogueContinuationStep(RootModel[
    Annotated[ContinuationDirectResponseStep | NoActionStep, Field(discriminator="decision_kind")]
]):
    """One terminal inference, with neither recall nor world-action proposals."""
    model_config = ConfigDict(title="CognitiveDecision")


class RecallOutcome(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    status: Literal["OK", "NO_MEMORY_FOUND", "EVIDENCE_BUDGET_EXHAUSTED", "INVALID_SCOPE",
                    "UNKNOWN_SUBJECT", "SUBJECT_NOT_DURABLE", "SUBJECT_NOT_AUTHORIZED",
                    "RECALL_UNAVAILABLE"]
    evidence: CanonicalEvidenceSet | None = None


class TaskDirectResponseStep(DirectResponseStep):
    # Required classification makes unsaved future commitments a schema-level
    # contract violation. Semantic honesty still needs real-provider evaluation.
    future_commitment: bool = Field(strict=True)
    task_operation: TaskIntent | None = None

    @model_validator(mode="after")
    def commitment(self):
        if self.future_commitment and self.task_operation is None:
            raise ValueError("TASK_UNSAVED_COMMITMENT")
        if self.task_operation is not None and self.target_daily_mode is not None:
            raise ValueError("TASK_MODE_MUTATION_COMBINATION_FORBIDDEN")
        return self


class TaskReadStep(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    decision_kind: Literal["TASK_READ"]
    task_query: TaskReadPlan


class CounterTaskChoice(SystemKeepMode):
    task_operation: TaskIntent | None = None


class CounterTaskTerminal(RootModel[
    Annotated[CounterTaskChoice | SystemAutonomousActionIntent, Field(discriminator='decision_kind')]
]):
    pass


class CounterTaskStep(RootModel[
    Annotated[CounterTaskChoice | SystemAutonomousActionIntent | TaskReadStep, Field(discriminator='decision_kind')]
]):
    pass


from .cognitive_task_models import TaskFocus


class TaskActionProposalStep(ActionProposalStep):
    task_focus: TaskFocus | None = None


class TaskTerminalStep(RootModel[
    Annotated[TaskDirectResponseStep | NoActionStep | TaskActionProposalStep, Field(discriminator="decision_kind")]
]):
    model_config = ConfigDict(title="CognitiveDecision")


class TaskCognitiveStep(RootModel[
    Annotated[TaskDirectResponseStep | NoActionStep | TaskActionProposalStep | MemoryRecallStep | TaskReadStep,
              Field(discriminator="decision_kind")]
]):
    model_config = ConfigDict(title="CognitiveStepResult")
