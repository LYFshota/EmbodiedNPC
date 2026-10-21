"""Consume versioned jobs; finalize admitted Memory and job success atomically."""

from __future__ import annotations

import asyncio
import logging
import math
import sqlite3
import time

from .base_memory_consolidator import BaseMemoryConsolidator, MemoryConsolidationError
from .canonical_memory_repository import CanonicalMemoryFinalizationError, CanonicalMemoryRepository
from .episode_models import DurableMemoryJob, MemoryJobPolicy
from .local_model_scheduler import PreemptedByForeground
from .memory_consolidation_admission import MemoryConsolidationAdmission, MemoryConsolidationAdmissionError
from .memory_consolidation_context import MemoryConsolidationContextError, MemoryContextResolver
from .memory_job_repository import MemoryJobRepository
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as ACTIVE_VERSION


logger = logging.getLogger("embodiednpc.backend.memory_consolidation")
CONTEXT_CODES = frozenset({
    "UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION", "EPISODE_NOT_FOUND", "EPISODE_NOT_CLOSED",
    "OBSERVER_SCOPE_MISMATCH", "JOB_INPUT_HASH_MISMATCH", "EXPERIENCE_SEQUENCE_GAP",
    "CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED", "PERSISTENT_PARTICIPANT_NOT_IN_EVIDENCE",
    "PERSISTENT_PARTICIPANT_IDENTITY_CONFLICT",
})
FINALIZATION_CODES = CONTEXT_CODES | {
    "ADMISSION_SCOPE_MISMATCH", "ADMISSION_INTEGRITY_MISMATCH",
    "DETERMINISTIC_MEMORY_INTEGRITY_CONFLICT", "INVALID_FINALIZATION_TIME",
}
MODEL_CODES = frozenset({
    "CONSOLIDATION_REQUEST_BUDGET_EXCEEDED", "PROVIDER_TIMEOUT", "PROVIDER_UNAVAILABLE",
    "PROVIDER_PROTOCOL_ERROR", "CONSOLIDATION_OUTPUT_MALFORMED", "CONSOLIDATION_SCHEMA_INVALID",
})


class MemoryConsolidationWorker:
    """Keep extraction, deterministic admission and durable finalization separate.

    A model candidate is not a saved memory. Finalization rechecks job generation
    and input hash; a preempted derived request is requeued instead of treated
    as evidence failure. stop() drains the current durable attempt.
    """

    def __init__(self, jobs: MemoryJobRepository, resolver: MemoryContextResolver,
                 consolidator: BaseMemoryConsolidator, admission: MemoryConsolidationAdmission,
                 memories: CanonicalMemoryRepository, *, job_policy: MemoryJobPolicy | None = None,
                 interval_seconds: float = 1.0, clock=time.time) -> None:
        self.jobs = jobs
        self.resolver = resolver
        self.consolidator = consolidator
        self.admission = admission
        self.memories = memories
        self.job_policy = job_policy or MemoryJobPolicy(extractor_version=ACTIVE_VERSION)
        if self.job_policy.extractor_version != ACTIVE_VERSION:
            raise ValueError("UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION")
        if not math.isfinite(interval_seconds) or interval_seconds <= 0:
            raise ValueError("INVALID_CONSOLIDATION_POLL_INTERVAL")
        self.interval_seconds = interval_seconds
        self._clock = clock
        self._stop = asyncio.Event()
        self._task: asyncio.Task[None] | None = None

    def start(self) -> None:
        if self._task is None or self._task.done():
            self._stop = asyncio.Event()
            self._task = asyncio.create_task(self._run(), name="memory-consolidation-worker")

    async def stop(self) -> None:
        # Drain the current durable attempt; do not abandon an in-flight provider/write.
        self._stop.set()
        if self._task is not None:
            await self._task
            self._task = None

    async def process_once(self) -> bool:
        now = self._clock()
        await asyncio.to_thread(self.jobs.recover_stale, now, self.job_policy)
        job = await asyncio.to_thread(self.jobs.claim_next_for_version, now, ACTIVE_VERSION)
        if job is None:
            return False
        try:
            context = await asyncio.to_thread(self.resolver.resolve, job)
            projection = await asyncio.to_thread(self.consolidator.project, context)
            response = await self.consolidator.consolidate(context, projection=projection)
            batch = await asyncio.to_thread(self.admission.evaluate, context, response,
                                           visible_event_ids=projection.visible_event_ids)
            outcome = await asyncio.to_thread(
                self.memories.finalize_success, job_id=job.job_id,
                attempt_generation=job.attempt_generation, expected_input_hash=job.input_hash,
                batch=batch, now=self._clock(),
            )
            logger.info("Consolidation job_id=%s attempt=%s applied=%s candidates=%s accepted=%s rejected=%s",
                        job.job_id, job.attempt_generation, outcome.applied,
                        len(response.episodic_memories) + len(response.semantic_facts), len(batch.memories),
                        len(batch.rejections))
            for rejection in batch.rejections:
                logger.info("Consolidation job_id=%s kind=%s index=%s code=%s", job.job_id,
                            rejection.candidate_kind, rejection.candidate_index, rejection.reason)
        except PreemptedByForeground:
            requeued = await asyncio.to_thread(
                self.jobs.requeue_preempted, job.job_id, job.attempt_generation, self._clock(),
            )
            logger.info("Consolidation job_id=%s attempt=%s outcome=%s", job.job_id,
                        job.attempt_generation, "PREEMPTED_BY_FOREGROUND" if requeued else "STALE_ATTEMPT")
        except MemoryConsolidationContextError as exc:
            code = str(exc) if str(exc) in CONTEXT_CODES else "CONSOLIDATION_CONTEXT_INVALID"
            await self._fail(job, code, permanent=True)
        except MemoryConsolidationAdmissionError:
            await self._fail(job, "CONSOLIDATION_ADMISSION_INTEGRITY_FAILED", permanent=True)
        except CanonicalMemoryFinalizationError as exc:
            if str(exc) in {"STALE_ATTEMPT", "JOB_NOT_RUNNING", "JOB_NOT_FOUND"}:
                self._log_stale(job)
            else:
                code = str(exc) if str(exc) in FINALIZATION_CODES else "CONSOLIDATION_INTEGRITY_FAILED"
                await self._fail(job, code, permanent=True)
        except MemoryConsolidationError as exc:
            code = str(exc) if str(exc) in MODEL_CODES else "CONSOLIDATION_MODEL_FAILED"
            await self._fail(job, code, permanent=code == "CONSOLIDATION_REQUEST_BUDGET_EXCEEDED")
        except sqlite3.IntegrityError:
            await self._fail(job, "CONSOLIDATION_STORE_INTEGRITY_FAILED", permanent=True)
        except sqlite3.OperationalError:
            await self._fail(job, "SQLITE_TRANSIENT_WRITE_FAILED")
        except Exception:
            await self._fail(job, "CONSOLIDATION_UNEXPECTED")
        return True

    async def _fail(self, job: DurableMemoryJob, code: str, *, permanent: bool = False) -> None:
        current = await asyncio.to_thread(self.jobs.get, job.job_id)
        if current is None or current.status != "RUNNING" or current.attempt_generation != job.attempt_generation:
            self._log_stale(job)
            return
        # Repository guards still protect the race between this read and the mutation.
        if permanent:
            updated = await asyncio.to_thread(self.jobs.mark_permanent_failure, job.job_id,
                                             job.attempt_generation, self._clock(), code)
        else:
            updated = await asyncio.to_thread(self.jobs.mark_failed, job.job_id,
                                             job.attempt_generation, self._clock(), code, self.job_policy)
        logger.info("Consolidation job_id=%s attempt=%s outcome=%s code=%s", job.job_id,
                    job.attempt_generation, updated.status if updated else "STALE_ATTEMPT", code)

    @staticmethod
    def _log_stale(job: DurableMemoryJob) -> None:
        logger.info("Consolidation job_id=%s attempt=%s outcome=STALE_ATTEMPT",
                    job.job_id, job.attempt_generation)

    async def _run(self) -> None:
        while not self._stop.is_set():
            try:
                if await self.process_once():
                    continue
            except Exception:
                # Includes claim/recovery and failure-persistence errors. No raw exception
                # text or traceback: provider/evidence payloads must not enter logs.
                logger.warning("Consolidation polling outcome=CONSOLIDATION_POLL_FAILED")
            try:
                await asyncio.wait_for(self._stop.wait(), timeout=self.interval_seconds)
            except TimeoutError:
                pass
