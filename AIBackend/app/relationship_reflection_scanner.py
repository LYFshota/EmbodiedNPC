"""Bounded, read-only discovery of pending Relationship Reflection candidates."""

from __future__ import annotations

from dataclasses import dataclass

from .memory_consolidation_context import MemoryConsolidationContextError, MemoryContextResolver
from .memory_job_repository import MemoryJobRepository
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
from .relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION, RelationshipReflectionCandidate,
    RelationshipReflectionCandidateBuilder, RelationshipReflectionCandidateError,
)
from .relationship_reflection_repository import RelationshipReflectionRepository


@dataclass(frozen=True)
class RelationshipScanIssue:
    job_id: str
    reason: str


@dataclass(frozen=True)
class RelationshipScanBatch:
    candidates: tuple[RelationshipReflectionCandidate, ...]
    issues: tuple[RelationshipScanIssue, ...]
    scanned_jobs: int
    end_of_round: bool


class RelationshipReflectionScanner:
    """One caller-driven round; the caller starts a new round after reaching the end."""

    def __init__(
        self, jobs: MemoryJobRepository, resolver: MemoryContextResolver,
        builder: RelationshipReflectionCandidateBuilder,
        reflections: RelationshipReflectionRepository,
    ) -> None:
        self.jobs = jobs
        self.resolver = resolver
        self.builder = builder
        self.reflections = reflections
        self.cursor: tuple[float, str] | None = None
        self._end_of_round = False

    @property
    def end_of_round(self) -> bool:
        return self._end_of_round

    def begin_new_round(self) -> None:
        """Rescan history so an earlier-created job that succeeded late is visible."""
        self.cursor = None
        self._end_of_round = False

    def scan_next_batch(self, *, limit: int = 16) -> RelationshipScanBatch:
        if not 1 <= limit <= 64:
            raise ValueError("RELATIONSHIP_SCAN_LIMIT_OUT_OF_RANGE")
        if self._end_of_round:
            return RelationshipScanBatch((), (), 0, True)
        jobs = self.jobs.list_succeeded_after(
            SEMANTIC_MEMORY_CONSOLIDATOR_VERSION, self.cursor, limit=limit,
        )
        pending: list[RelationshipReflectionCandidate] = []
        issues: list[RelationshipScanIssue] = []
        for job in jobs:
            self.cursor = (job.created_at_utc, job.job_id)
            try:
                context = self.resolver.resolve(job)
                candidates = self.builder.build(context)
            except (MemoryConsolidationContextError, RelationshipReflectionCandidateError) as exc:
                issues.append(RelationshipScanIssue(job.job_id, str(exc)))
                continue
            for candidate in candidates:
                if not self.reflections.exists_for_episode_pair(
                    candidate.observer, candidate.subject, candidate.episode_id,
                    RELATIONSHIP_REFLECTION_POLICY_VERSION,
                ):
                    pending.append(candidate)
        self._end_of_round = len(jobs) < limit
        return RelationshipScanBatch(tuple(pending), tuple(issues), len(jobs), self._end_of_round)
