"""Durable Mem-P2 episode and consolidation-job state models."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal


EpisodeStatus = Literal["OPEN", "CLOSED"]
MemoryJobStatus = Literal["PENDING", "RUNNING", "RETRY", "SUCCEEDED", "DEAD_LETTER"]


@dataclass(frozen=True)
class EpisodeBoundaryPolicy:
    version: str = "episode-boundary/1.0"
    idle_timeout_seconds: float = 180.0
    max_events: int = 64
    max_duration_seconds: float = 900.0
    processing_batch: int = 64

    def __post_init__(self) -> None:
        if self.idle_timeout_seconds <= 0 or self.max_duration_seconds <= 0:
            raise ValueError("EPISODE_DURATION_POLICY_INVALID")
        if self.max_events < 1 or self.processing_batch < 1:
            raise ValueError("EPISODE_COUNT_POLICY_INVALID")


@dataclass(frozen=True)
class MemoryJobPolicy:
    extractor_version: str = "memory-extractor/1.0"
    max_retries: int = 5
    initial_backoff_seconds: float = 5.0
    max_backoff_seconds: float = 300.0
    stale_timeout_seconds: float = 120.0

    def __post_init__(self) -> None:
        if self.max_retries < 1 or self.initial_backoff_seconds <= 0:
            raise ValueError("MEMORY_JOB_RETRY_POLICY_INVALID")
        if self.max_backoff_seconds < self.initial_backoff_seconds or self.stale_timeout_seconds <= 0:
            raise ValueError("MEMORY_JOB_TIMEOUT_POLICY_INVALID")

    def backoff_seconds(self, retry_count: int) -> float:
        return min(self.max_backoff_seconds, self.initial_backoff_seconds * (2 ** max(0, retry_count - 1)))


@dataclass(frozen=True)
class Episode:
    episode_id: str
    observer_id: str
    source_session_id: str
    start_seq: int
    end_seq: int
    started_at_utc: float
    ended_at_utc: float | None
    status: EpisodeStatus
    close_reason: str | None
    conversation_id: str | None
    primary_goal_id: str | None
    primary_goal_type: str | None
    event_count: int
    policy_version: str
    schema_version: str
    created_at_utc: float
    updated_at_utc: float


@dataclass(frozen=True)
class DurableMemoryJob:
    job_id: str
    observer_id: str
    episode_id: str
    input_hash: str
    extractor_version: str
    idempotency_key: str
    status: MemoryJobStatus
    retry_count: int
    attempt_generation: int
    next_retry_at_utc: float | None
    created_at_utc: float
    updated_at_utc: float
    started_at_utc: float | None
    finished_at_utc: float | None
    error_code: str | None
    schema_version: str


@dataclass(frozen=True)
class EpisodeProcessResult:
    observer_id: str
    processed: int = 0
    closed: int = 0
    clock_anomalies: int = 0
    gap_at_seq: int | None = None


@dataclass(frozen=True)
class MemoryJobBacklog:
    pending_count: int
    retry_count: int
    oldest_pending_age_seconds: float | None
    jobs_per_observer: dict[str, int]
    last_successful_job: DurableMemoryJob | None
    last_failure_reason: str | None
