"""Bounded model proposals and internal admission values for Memory C2-A."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field

from .memory_consolidation_context import MemoryConsolidationContext, TrustedPersistentIdentity
from .semantic_memory_models import SemanticFactCandidate


Handle = Annotated[str, Field(pattern=r"^participant_[1-9][0-9]*$", max_length=40)]
EvidenceId = Annotated[str, Field(min_length=1, max_length=160)]
UnitInterval = Annotated[float, Field(ge=0, le=1, allow_inf_nan=False)]


class EpisodicMemoryCandidate(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    summary: str = Field(min_length=1, max_length=500)
    subject_handles: tuple[Handle, ...] = Field(max_length=32)
    evidence_event_ids: tuple[EvidenceId, ...] = Field(min_length=1, max_length=64)
    importance: UnitInterval
    confidence: UnitInterval


class MemoryConsolidationResponse(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    episodic_memories: tuple[EpisodicMemoryCandidate, ...] = Field(max_length=8)
    semantic_facts: tuple[SemanticFactCandidate, ...] = Field(max_length=8)


@dataclass(frozen=True)
class AdmittedCanonicalMemory:
    memory_id: str
    memory_type: str
    owner: TrustedPersistentIdentity | None
    subjects: tuple[TrustedPersistentIdentity, ...]
    summary: str
    importance: float | None
    confidence: float
    evidence: tuple[tuple[str, int], ...]
    source_excerpt: str | None = None
    excerpt_event_id: str | None = None


@dataclass(frozen=True)
class ConsolidationCandidateRejection:
    """Internal diagnostics: at most one code per bounded response candidate, no payload."""

    candidate_kind: Literal["episodic", "semantic_fact"]
    candidate_index: int
    reason: str


@dataclass(frozen=True)
class ConsolidationAdmissionBatch:
    context: MemoryConsolidationContext
    response: MemoryConsolidationResponse
    memories: tuple[AdmittedCanonicalMemory, ...]
    policy_version: str = "memory-consolidation-admission/2.0"
    rejections: tuple[ConsolidationCandidateRejection, ...] = ()
    visible_event_ids: tuple[str, ...] = ()


@dataclass(frozen=True)
class CanonicalFinalizationOutcome:
    applied: bool
    watermark: int
