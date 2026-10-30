"""Bounded current social interpretation; backend-owned identity and raw anchors."""
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field, model_validator

PAIR_STATE_MAX_ITEMS = 4


class StateUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    operation: Literal["ADD", "CORRECT", "CONFLICT"]
    target: str | None = None
    kind: Literal["EXPLICIT", "BOUNDARY", "TENTATIVE"]
    text: str = Field(min_length=1, max_length=160)
    event: str = Field(pattern=r"^e[0-9]+$")
    quote: str = Field(min_length=1, max_length=160)

    @model_validator(mode="after")
    def check_target(self):
        if (self.operation == "ADD") != (self.target is None):
            raise ValueError("STATE_UPDATE_TARGET_INVALID")
        if not self.text.strip() or not self.quote.strip():
            raise ValueError("STATE_UPDATE_EMPTY")
        return self


class StateEvidence(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    event_id: str
    episode_id: str
    payload_hash: str
    quote: str = Field(min_length=1, max_length=1000)


class PairStateItem(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    item_id: str
    kind: Literal["EXPLICIT", "BOUNDARY", "TENTATIVE", "CONFLICT"]
    text: str = Field(min_length=1, max_length=160)
    evidence: tuple[StateEvidence, ...] = Field(min_length=1, max_length=8)
    change_type: Literal["ADD", "CORRECT", "CONFLICT"] = "ADD"
    predecessor_id: str | None = None
    # Conflict keeps both readings; correction keeps its superseded wording for audit.
    predecessor_text: str | None = Field(default=None, max_length=160)


class PairState(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    content_version: Literal["relationship-pair-state/1.0"] = "relationship-pair-state/1.0"
    revision: int = Field(default=0, ge=0)
    reflection_id: str | None = None
    items: tuple[PairStateItem, ...] = Field(default=(), max_length=PAIR_STATE_MAX_ITEMS)
    # Audit-only proposal, never a fifth current item or cognition evidence.
    # Keeping it in the immutable Episode result also deduplicates rediscovery.
    capacity_limited: PairStateItem | None = None
    interaction_evidence: StateEvidence | None = None
    coverage: Literal["SINCE_B1", "LEGACY_PARTIAL", "SOURCE_UNAVAILABLE"] = "SINCE_B1"
    # Ledger ingestion order, not model generation time, governs late Episodes.
    late_episode_skipped: bool = False
    watermark: int = 0
    changed: bool = False
