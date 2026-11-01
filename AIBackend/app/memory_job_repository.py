"""Durable consolidation jobs: atomic claims, attempt generations and recovery."""

from __future__ import annotations

import sqlite3
from collections.abc import Callable
from contextlib import closing

from .worker_runtime import ActiveAttempts
from .episode_models import DurableMemoryJob, MemoryJobBacklog, MemoryJobPolicy


class MemoryJobRepository:
    """Own job transactions through fresh caller-closed connections.

    Attempt generation guards late results after recovery/requeue; retry_count
    is a durable model-job budget, distinct from Episode storage BUSY retries.
    """

    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]) -> None:
        self._connection_factory = connection_factory
        self.active_attempts = ActiveAttempts()

    @staticmethod
    def _job(row: sqlite3.Row) -> DurableMemoryJob:
        return DurableMemoryJob(**dict(row))

    def get(self, job_id: str) -> DurableMemoryJob | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute("SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)).fetchone()
        return None if row is None else self._job(row)

    def get_for_episode(self, episode_id: str, extractor_version: str) -> DurableMemoryJob | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT * FROM memory_jobs WHERE episode_id = ? AND extractor_version = ?",
                (episode_id, extractor_version),
            ).fetchone()
        return None if row is None else self._job(row)

    def list_succeeded_after(
        self, extractor_version: str, cursor: tuple[float, str] | None,
        *, limit: int = 16,
    ) -> tuple[DurableMemoryJob, ...]:
        """Read a bounded succeeded-job page without claiming or mutating jobs."""
        if not 1 <= limit <= 64:
            raise ValueError("MEMORY_JOB_READ_LIMIT_OUT_OF_RANGE")
        after_clause = ""
        parameters: tuple[object, ...] = (extractor_version,)
        if cursor is not None:
            after_clause = (
                " AND (created_at_utc > ? OR "
                "(created_at_utc = ? AND job_id > ?))"
            )
            parameters += (cursor[0], cursor[0], cursor[1])
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT * FROM memory_jobs "
                "WHERE extractor_version = ? AND status = 'SUCCEEDED'" + after_clause +
                " ORDER BY created_at_utc ASC, job_id ASC LIMIT ?",
                (*parameters, limit),
            ).fetchall()
        return tuple(self._job(row) for row in rows)

    def claim_next(
        self, now: float, excluded_extractor_versions: tuple[str, ...] = ()
    ) -> DurableMemoryJob | None:
        version_filter = ""
        if excluded_extractor_versions:
            placeholders = ",".join("?" for _ in excluded_extractor_versions)
            version_filter = f" AND extractor_version NOT IN ({placeholders})"
        return self._claim_next(now, version_filter, excluded_extractor_versions)

    def claim_next_for_version(self, now: float, extractor_version: str, *, activate: bool = False) -> DurableMemoryJob | None:
        """Claim only this pipeline's jobs, using the existing atomic attempt transition."""
        with self.active_attempts.lock:
            job = self._claim_next(now, " AND extractor_version = ?", (extractor_version,))
            if job is not None and activate:
                self.active_attempts.add(job)
            return job

    def _claim_next(
        self, now: float, version_filter: str, versions: tuple[str, ...]
    ) -> DurableMemoryJob | None:
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                row = connection.execute(
                    f"""
                    SELECT * FROM memory_jobs
                    WHERE (status = 'PENDING'
                       OR (status = 'RETRY' AND next_retry_at_utc <= ?))
                       {version_filter}
                    ORDER BY created_at_utc, job_id LIMIT 1
                    """,
                    (now, *versions),
                ).fetchone()
                if row is None:
                    connection.commit()
                    return None
                connection.execute(
                    """
                    UPDATE memory_jobs SET status = 'RUNNING', attempt_generation = attempt_generation + 1,
                        started_at_utc = ?, updated_at_utc = ?, next_retry_at_utc = NULL
                    WHERE job_id = ? AND status IN ('PENDING', 'RETRY')
                    """,
                    (now, now, row["job_id"]),
                )
                claimed = connection.execute(
                    "SELECT * FROM memory_jobs WHERE job_id = ?", (row["job_id"],)
                ).fetchone()
                connection.commit()
                return self._job(claimed)
            except Exception:
                connection.rollback()
                raise

    def requeue_preempted(self, job_id: str, attempt_generation: int, now: float):
        return self._requeue_attempt(job_id, attempt_generation, now, "PREEMPTED_BY_FOREGROUND")

    def requeue_cancelled(self, job_id: str, attempt_generation: int, now: float):
        return self._requeue_attempt(job_id, attempt_generation, now, "ATTEMPT_CANCELLED")

    def _requeue_attempt(
        self, job_id: str, attempt_generation: int, now: float, error_code: str
    ) -> DurableMemoryJob | None:
        """Yield owned work without consuming the failure budget or changing generation."""
        with closing(self._connection_factory()) as connection:
            with connection:
                changed = connection.execute(
                    """
                    UPDATE memory_jobs SET status = 'RETRY', next_retry_at_utc = ?,
                        updated_at_utc = ?, finished_at_utc = NULL,
                        error_code = ?
                    WHERE job_id = ? AND status = 'RUNNING' AND attempt_generation = ?
                    """,
                    (now, now, error_code, job_id, attempt_generation),
                ).rowcount
                if not changed:
                    return None
                row = connection.execute("SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)).fetchone()
        return self._job(row)

    def mark_failed(
        self, job_id: str, attempt_generation: int, now: float, error_code: str, policy: MemoryJobPolicy
    ) -> DurableMemoryJob | None:
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                row = connection.execute(
                    "SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)
                ).fetchone()
                if row is None or row["status"] != "RUNNING" or row["attempt_generation"] != attempt_generation:
                    connection.rollback()
                    return None
                retries = int(row["retry_count"]) + 1
                exhausted = retries >= policy.max_retries
                status = "DEAD_LETTER" if exhausted else "RETRY"
                next_retry = None if exhausted else now + policy.backoff_seconds(retries)
                finished = now if exhausted else None
                connection.execute(
                    """
                    UPDATE memory_jobs SET status = ?, retry_count = ?, next_retry_at_utc = ?,
                        updated_at_utc = ?, finished_at_utc = ?, error_code = ?
                    WHERE job_id = ? AND status = 'RUNNING' AND attempt_generation = ?
                    """,
                    (status, retries, next_retry, now, finished, error_code, job_id, attempt_generation),
                )
                updated = connection.execute(
                    "SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)
                ).fetchone()
                connection.commit()
                return self._job(updated)
            except Exception:
                connection.rollback()
                raise

    def mark_succeeded(
        self, job_id: str, attempt_generation: int, now: float
    ) -> DurableMemoryJob | None:
        """Future Mem-P3 completion hook; Mem-P2 runtime never calls it."""
        with closing(self._connection_factory()) as connection:
            with connection:
                changed = connection.execute(
                    """
                    UPDATE memory_jobs SET status = 'SUCCEEDED', updated_at_utc = ?, finished_at_utc = ?,
                        next_retry_at_utc = NULL, error_code = NULL
                    WHERE job_id = ? AND status = 'RUNNING' AND attempt_generation = ?
                    """,
                    (now, now, job_id, attempt_generation),
                ).rowcount
                if not changed:
                    return None
                row = connection.execute("SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)).fetchone()
        return self._job(row)

    def mark_permanent_failure(
        self, job_id: str, attempt_generation: int, now: float, error_code: str
    ) -> DurableMemoryJob | None:
        with closing(self._connection_factory()) as connection:
            with connection:
                changed = connection.execute(
                    """
                    UPDATE memory_jobs SET status = 'DEAD_LETTER', updated_at_utc = ?,
                        finished_at_utc = ?, next_retry_at_utc = NULL, error_code = ?
                    WHERE job_id = ? AND status = 'RUNNING' AND attempt_generation = ?
                    """,
                    (now, now, error_code, job_id, attempt_generation),
                ).rowcount
                if not changed:
                    return None
                row = connection.execute("SELECT * FROM memory_jobs WHERE job_id = ?", (job_id,)).fetchone()
        return self._job(row)

    def recover_stale(self, now: float, policy: MemoryJobPolicy) -> tuple[DurableMemoryJob, ...]:
        cutoff = now - policy.stale_timeout_seconds
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """
                SELECT job_id, attempt_generation FROM memory_jobs
                WHERE status = 'RUNNING' AND started_at_utc < ? AND extractor_version = ?
                ORDER BY started_at_utc
                """,
                (cutoff, policy.extractor_version),
            ).fetchall()
        recovered: list[DurableMemoryJob] = []
        for row in rows:
            with self.active_attempts.lock:
                if self.active_attempts.contains(row["job_id"], int(row["attempt_generation"])):
                    continue
                value = self.mark_failed(
                    row["job_id"], int(row["attempt_generation"]), now, "STALE_RUNNING", policy
                )
            if value is not None:
                recovered.append(value)
        return tuple(recovered)

    def backlog(self, now: float) -> MemoryJobBacklog:
        with closing(self._connection_factory()) as connection:
            counts = {
                row["status"]: int(row["count"])
                for row in connection.execute(
                    "SELECT status, COUNT(*) AS count FROM memory_jobs GROUP BY status"
                ).fetchall()
            }
            oldest = connection.execute(
                "SELECT MIN(created_at_utc) AS value FROM memory_jobs WHERE status IN ('PENDING', 'RETRY')"
            ).fetchone()["value"]
            per_observer = {
                str(row["observer_id"]): int(row["count"])
                for row in connection.execute(
                    """
                    SELECT observer_id, COUNT(*) AS count FROM memory_jobs
                    WHERE status IN ('PENDING', 'RUNNING', 'RETRY') GROUP BY observer_id ORDER BY observer_id
                    """
                ).fetchall()
            }
            success = connection.execute(
                "SELECT * FROM memory_jobs WHERE status = 'SUCCEEDED' ORDER BY finished_at_utc DESC LIMIT 1"
            ).fetchone()
            failure = connection.execute(
                """
                SELECT error_code FROM memory_jobs WHERE error_code IS NOT NULL
                ORDER BY updated_at_utc DESC LIMIT 1
                """
            ).fetchone()
        return MemoryJobBacklog(
            pending_count=counts.get("PENDING", 0),
            retry_count=counts.get("RETRY", 0),
            oldest_pending_age_seconds=None if oldest is None else max(0.0, now - float(oldest)),
            jobs_per_observer=per_observer,
            last_successful_job=None if success is None else self._job(success),
            last_failure_reason=None if failure is None else str(failure["error_code"]),
        )
