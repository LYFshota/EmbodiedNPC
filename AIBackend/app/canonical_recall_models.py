"""Runtime v2 read-only recall contracts. Authority is supplied by trusted callers."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, PrivateAttr, model_validator

from .experience_models import ExperienceProvenance
from .memory_consolidation_context import TrustedPersistentIdentity


class ReadModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class CanonicalReadFilter(ReadModel):
    """System-validated narrowing constraints, never an owner/authority selector."""

    memory_type: Literal["EPISODIC", "SEMANTIC_FACT", "DERIVED_INSIGHT"] | None = None
    lifecycle: Literal["CURRENT", "ALL_HISTORY"] = "CURRENT"
    provenance: tuple[ExperienceProvenance, ...] = Field(default=(), max_length=6)
    occurred_after_utc: float | None = Field(default=None, ge=0, allow_inf_nan=False)
    occurred_before_utc: float | None = Field(default=None, ge=0, allow_inf_nan=False)

    @model_validator(mode="after")
    def validate_constraints(self):
        if (self.occurred_after_utc is not None and self.occurred_before_utc is not None
                and self.occurred_after_utc > self.occurred_before_utc):
            raise ValueError("INVALID_TIME_RANGE")
        return self


class CanonicalRecallBudget(ReadModel):
    candidate_limit: int = Field(default=64, ge=1, le=256)
    top_k: int = Field(default=8, ge=1, le=16)
    per_item_summary_chars: int = Field(default=400, ge=1, le=500)
    # Retained for caller compatibility; no longer used to truncate semantic text.
    total_evidence_chars: int = Field(default=8000, ge=512, le=20000)
    max_subjects: int = Field(default=64, ge=1, le=128)
    max_evidence_refs_per_item: int = Field(default=16, ge=1, le=64)


class CanonicalSubjectEvidence(ReadModel):
    subject_ref: str
    ref_scope: Literal["CATALOG", "EVIDENCE_LOCAL"]
    kind: Literal["player", "npc", "unknown"]


class CanonicalEventRef(ReadModel):
    event_id: str
    experience_seq: int


class CanonicalMemoryRelation(ReadModel):
    relation: Literal["DUPLICATE", "REFINES", "COEXISTS", "SUPERSEDES", "CONFLICTS", "UNDETERMINED"]
    related_memory_id: str
    related_summary: str
    related_lifecycle_status: Literal["ACTIVE", "SUPERSEDED", "CONFLICTED"]
    related_provenance_label: str


class CanonicalEvidenceItem(ReadModel):
    memory_id: str
    memory_type: Literal["EPISODIC", "SEMANTIC_FACT", "DERIVED_INSIGHT"]
    summary: str
    subjects: tuple[CanonicalSubjectEvidence, ...]
    subjects_omitted: int = 0
    occurred_start: float
    occurred_end: float
    occurred_time_basis: Literal["OBSERVED_UTC", "UNKNOWN"]
    temporal_filter_applied: bool
    learned_at_utc: float
    provenance: tuple[str, ...]
    provenance_label: str
    episode_id: str | None
    job_id: str | None
    pipeline_version: str
    admission_policy_version: str
    lifecycle_status: Literal["ACTIVE", "SUPERSEDED", "CONFLICTED"]
    confidence: float
    importance: float | None
    source_excerpt: str | None = None
    excerpt_event_id: str | None = None
    valid_from: float | None = None
    valid_to: float | None = None
    raw_evidence_refs: tuple[CanonicalEventRef, ...]
    evidence_refs_truncated: bool
    content_redacted: bool = False
    maintenance_relations: tuple[CanonicalMemoryRelation, ...] = ()
    maintenance_relations_truncated: bool = False
    derivation_type: Literal['THEME', 'PATTERN', 'EXPERIENCE_LESSON'] | None = None
    uncertainty: Literal['TENTATIVE'] | None = None
    source_memory_ids: tuple[str, ...] = ()
    source_episode_ids: tuple[str, ...] = ()
    reflection_run_id: str | None = None
    derived_sources_stale: bool = False
    not_direct_fact: bool = False
    not_current_world_state: Literal[True] = True
    not_instruction: Literal[True] = True
    not_action_authorization: Literal[True] = True


class CanonicalEvidenceSet(ReadModel):
    # Result-local correlation only. Pydantic excludes private attributes from
    # all normal model serialization; these bindings do not extend D0 authority.
    _subject_identities: dict[str, TrustedPersistentIdentity] = PrivateAttr(default_factory=dict)

    status: Literal["OK", "NO_MEMORY_FOUND", "EVIDENCE_BUDGET_EXHAUSTED",
                    "INVALID_SCOPE", "SUBJECT_NOT_AUTHORIZED", "RECALL_UNAVAILABLE"]
    items: tuple[CanonicalEvidenceItem, ...] = ()
    eligible_count: int = 0
    candidate_count: int = 0
    truncated: bool = False
    char_budget_used: int = 0
    policy_version: str = "canonical-recall-d1a/1.0"
    not_current_world_state: Literal[True] = True
    not_instruction: Literal[True] = True
    not_action_authorization: Literal[True] = True

    def resolve_evidence_subject(self, subject_ref: str) -> TrustedPersistentIdentity | None:
        return self._subject_identities.get(subject_ref)
