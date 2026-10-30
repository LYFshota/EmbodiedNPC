"""Bounded semantic operations, independent of open fact content."""
from dataclasses import dataclass
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from .memory_consolidation_context import TrustedPersistentIdentity

MAINTENANCE_VERSION = "memory-maintenance/1.0"
Relation = Literal["DUPLICATE", "REFINES", "COEXISTS", "SUPERSEDES", "CONFLICTS", "UNDETERMINED"]


class MaintenanceOperation(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    relation: Relation
    new_memory_id: str = Field(min_length=1, max_length=160)
    old_memory_id: str = Field(min_length=1, max_length=160)
    reason: str = Field(min_length=1, max_length=300)
    evidence_event_ids: tuple[str, ...] = Field(default=(), max_length=4)
    source_excerpt: str | None = Field(default=None, min_length=1, max_length=1000)

    @field_validator('reason','source_excerpt')
    @classmethod
    def nonblank(cls,value):
        if value is not None and not value.strip():
            raise ValueError('MAINTENANCE_TEXT_BLANK')
        return value


class MaintenanceResponse(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    operations: tuple[MaintenanceOperation, ...] = Field(max_length=8)


class MaintenanceDecision(BaseModel):
    """Wire-only judgment. Record identity and pending membership belong to code."""
    model_config = ConfigDict(extra="forbid", frozen=True)
    pair_ref: str = Field(pattern=r"^p[0-7]$")
    relation: Literal["DUPLICATE", "REFINES", "COEXISTS", "SUPERSEDES", "CONFLICTS", "UNDETERMINED", "NO_RELATION"]
    direction: Literal["LEFT_TO_RIGHT", "RIGHT_TO_LEFT"]
    reason: str = Field(min_length=1, max_length=300)
    evidence_event_ids: tuple[str, ...] = Field(default=(), max_length=4)
    source_excerpt: str | None = Field(default=None, min_length=1, max_length=1000)

    @field_validator('reason', 'source_excerpt')
    @classmethod
    def nonblank(cls, value):
        if value is not None and not value.strip():
            raise ValueError('MAINTENANCE_TEXT_BLANK')
        return value


class MaintenanceDecisionResponse(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    decisions: tuple[MaintenanceDecision, ...] = Field(max_length=8)


@dataclass(frozen=True)
class MaintenancePolicy:
    backlog_threshold: int = 4
    max_wait_seconds: float = 300
    new_limit: int = 4
    scan_limit: int = 64
    related_limit: int = 12
    stale_seconds: float = 180
    max_failures: int = 3

    def __post_init__(self):
        if (not 1 <= self.backlog_threshold <= 64 or not 1 <= self.new_limit <= 8
                or not 1 <= self.related_limit <= 16 or not self.related_limit <= self.scan_limit <= 128
                or not 0 <= self.max_wait_seconds <= 86400 or not 1 <= self.stale_seconds <= 3600
                or not 1 <= self.max_failures <= 5):
            raise ValueError("MAINTENANCE_POLICY_INVALID")


@dataclass(frozen=True)
class MaintenanceInput:
    job_id: str
    generation: int
    owner: TrustedPersistentIdentity
    subject: TrustedPersistentIdentity
    new_ids: tuple[str, ...]
    records: tuple[dict, ...]
    # None is a legacy/direct repository caller, not a model-selected pair set.
    comparison_pairs: tuple[tuple[str, str], ...] | None = None
    candidate_pairs: tuple[tuple[str, str], ...] = ()
    comparison_decisions: tuple[dict, ...] = ()

    def model_payload(self):
        # Durable owner/subject keys never enter the model request.
        payload = dict(new_memory_ids=self.new_ids, memories=self.records,
                       candidate_search_is_exhaustive=False)
        if self.comparison_pairs is not None:
            payload['comparison_scope'] = dict(
                contract='maintenance-pairs/1.0', compared_pairs=self.comparison_pairs,
                candidate_pairs=self.candidate_pairs, decisions=self.comparison_decisions,
                omitted_pair_count=len(self.candidate_pairs)-len(self.comparison_pairs),
                completion_means='bounded_sample_only')
        return payload


class MaintenanceStale(ValueError):
    pass


class MaintenanceAdmissionError(ValueError):
    pass
