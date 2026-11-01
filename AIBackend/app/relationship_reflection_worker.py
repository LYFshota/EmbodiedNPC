"""Small background consumer from discovered pair candidates to persisted results."""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import math
import sqlite3
import time
from dataclasses import dataclass

from .worker_runtime import store_call, drain_task, run_pool
from .local_model_scheduler import PreemptedByForeground
from .relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION, RelationshipReflectionCandidate,
)
from .relationship_reflection_model import RelationshipReflectionModel, RelationshipReflectionModelError
from .relationship_reflection_models import RelationshipReflectionRecord
from .relationship_reflection_repository import RelationshipReflectionRepository, PairUpdateRejected
from .relationship_reflection_scanner import RelationshipReflectionScanner

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class RelationshipExecutionIssue:
    episode_id: str
    reason: str


@dataclass(frozen=True)
class RelationshipExecutionBatch:
    """Capacity diagnostics are durable but excluded from normal persisted results.

    They also appear in issues so callers cannot mistake them for applied updates.
    """
    scanned_jobs: int
    persisted: int
    already_processed: int
    issues: tuple[RelationshipExecutionIssue, ...]
    end_of_round: bool
    capacity_limited: int = 0


class RelationshipReflectionWorker:
    """Process bounded observer/subject batches sequentially; no durable job state.

    Persisted unique episode/pair keys, including capacity diagnostics,
    deduplicate rediscovery after restart.
    Interpretations remain subjective relationship evidence, not world facts;
    an uncommitted/preempted pair can be discovered again in a later round.
    """

    def __init__(self, scanner: RelationshipReflectionScanner,
                 model: RelationshipReflectionModel,
                 reflections: RelationshipReflectionRepository,
                 *, clock=time.time, interval_seconds: float = 1.0, concurrency: int = 1, attempt_timeout: float = 900) -> None:
        if not math.isfinite(interval_seconds) or interval_seconds <= 0:
            raise ValueError("RELATIONSHIP_POLL_INTERVAL_INVALID")
        if not 1 <= concurrency <= 32 or attempt_timeout <= 0:
            raise ValueError("WORKER_CONCURRENCY_INVALID")
        self.concurrency, self.attempt_timeout = concurrency, attempt_timeout
        self._slots = asyncio.Semaphore(concurrency)
        self._active_pairs = set()
        self.scanner = scanner
        self.model = model
        self.reflections = reflections
        self.clock = clock
        self.interval_seconds = interval_seconds
        self._pending: list[RelationshipReflectionCandidate] = []
        self._lock = asyncio.Lock()
        self._stop = asyncio.Event()
        self._task: asyncio.Task[None] | None = None

    @staticmethod
    def reflection_id_for(candidate: RelationshipReflectionCandidate) -> str:
        components = (
            candidate.observer.identity_namespace, candidate.observer.identity_type,
            candidate.observer.persistent_id, candidate.subject.identity_namespace,
            candidate.subject.identity_type, candidate.subject.persistent_id,
            candidate.episode_id, RELATIONSHIP_REFLECTION_POLICY_VERSION,
        )
        payload = json.dumps(components, ensure_ascii=False, separators=(",", ":"))
        return "relationship_reflection_" + hashlib.sha256(payload.encode("utf-8")).hexdigest()

    @staticmethod
    def _pair_key(candidate):
        def identity(value):
            return value.identity_namespace, value.identity_type, value.persistent_id
        return identity(candidate.observer), identity(candidate.subject)

    async def process_batch(self, *, job_limit: int = 4,
                            candidate_limit: int = 4) -> RelationshipExecutionBatch:
        if not 1 <= job_limit <= 16 or not 1 <= candidate_limit <= 16:
            raise ValueError("RELATIONSHIP_WORK_BATCH_LIMIT_INVALID")
        return await self._batch(job_limit, candidate_limit)

    async def _batch(self, job_limit, candidate_limit):
        # Production lanes request one item and refill independently. The public
        # bounded batch API remains compatible with deterministic callers.
        results = []
        for _ in range(candidate_limit):
            item = await self._one(job_limit)
            results.append(item)
            if item.end_of_round or not (item.scanned_jobs or item.persisted or item.already_processed or item.issues):
                break
        return RelationshipExecutionBatch(sum(x.scanned_jobs for x in results),
            sum(x.persisted for x in results),sum(x.already_processed for x in results),
            tuple(i for x in results for i in x.issues),
            self.scanner.end_of_round and not self._pending and not self._active_pairs,
            sum(x.capacity_limited for x in results))

    async def _one(self, job_limit):
        async with self._slots:
            scanned, issues, candidate = 0, [], None
            async with self._lock:
                if not self._pending:
                    discovered = await store_call(self.scanner.scan_next_batch, limit=job_limit)
                    self._pending.extend(discovered.candidates)
                    scanned = discovered.scanned_jobs
                    issues.extend(RelationshipExecutionIssue(i.job_id,i.reason) for i in discovered.issues)
                for index, item in enumerate(self._pending):
                    key = self._pair_key(item)
                    if key not in self._active_pairs:
                        candidate = self._pending.pop(index)
                        self._active_pairs.add(key)
                        break
            if candidate is None:
                return RelationshipExecutionBatch(scanned,0,0,tuple(issues),
                    self.scanner.end_of_round and not self._pending and not self._active_pairs)
            try:
                async with asyncio.timeout(self.attempt_timeout):
                    persisted, already, errors, limited = await self._execute_candidate(candidate)
                issues.extend(errors)
            except TimeoutError:
                persisted, already, limited = 0, 0, 0
                issues.append(RelationshipExecutionIssue(candidate.episode_id,"RELATIONSHIP_ATTEMPT_TIMEOUT"))
            finally:
                self._active_pairs.discard(key)
            return RelationshipExecutionBatch(scanned,persisted,already,tuple(issues),
                self.scanner.end_of_round and not self._pending and not self._active_pairs,limited)

    async def _execute_candidate(self, candidate):
        persisted = already_processed = capacity_limited = 0
        issues = []
        pair = (candidate.observer, candidate.subject, candidate.episode_id,
                RELATIONSHIP_REFLECTION_POLICY_VERSION)
        if await store_call(self.reflections.exists_for_episode_pair, *pair):
            already_processed += 1
            return 0, 1, (), 0
        try:
            previous = await store_call(self.reflections.read_current_pair_state,
                candidate.observer, candidate.subject)
            response = await self.model.interpret(candidate, previous)
            record = RelationshipReflectionRecord(
                reflection_id=self.reflection_id_for(candidate),
                observer=candidate.observer, subject=candidate.subject,
                episode_id=candidate.episode_id,
                episode_input_hash=candidate.episode_input_hash,
                outcome=response.outcome, interpretation=response.interpretation,
                evidence_event_ids=candidate.evidence_event_ids,
                policy_version=RELATIONSHIP_REFLECTION_POLICY_VERSION,
                generated_at=self.clock(),
            )
            try:
                state = await store_call(self.reflections.finalize_pair_update,
                    record, candidate, response, previous)
            except sqlite3.IntegrityError:
                # A concurrent/late result can win the unique pair key; other integrity
                # failures remain errors and must not be mistaken for successful work.
                if not await store_call(self.reflections.exists_for_episode_pair, *pair):
                    raise
                already_processed += 1
            else:
                if state.capacity_limited is not None:
                    capacity_limited += 1
                    issues.append(RelationshipExecutionIssue(candidate.episode_id,
                        "RELATIONSHIP_STATE_CAPACITY_LIMITED"))
                    logger.warning("relationship_state_capacity_limited")
                else:
                    persisted += 1
        except (RelationshipReflectionModelError, PreemptedByForeground, PairUpdateRejected) as exc:
            issues.append(RelationshipExecutionIssue(candidate.episode_id, str(exc)))
            if not isinstance(exc, PreemptedByForeground):
                # No durable failure row exists: later scans can rediscover this pair.
                # Log only backend metadata and stable codes, never provider/evidence text.
                logger.warning("relationship_reflection_failed reflection_id=%s reason=%s",
                               self.reflection_id_for(candidate), str(exc))
        return persisted, already_processed, tuple(issues), capacity_limited

    def start(self) -> None:
        if self._task is None or self._task.done():
            self._stop = asyncio.Event()
            self._task = asyncio.create_task(self._run(), name="relationship-reflection-worker")

    def request_stop(self) -> None:
        self._stop.set()

    async def stop(self) -> None:
        self._stop.set()
        if self._task is not None:
            await drain_task(self._task)
            self._task = None

    async def _run(self) -> None:
        async def process():
            batch = await self.process_batch(candidate_limit=1)
            if batch.end_of_round:
                # Only the scanner cursor is serialized; no model call owns it.
                try: await asyncio.wait_for(self._stop.wait(),self.interval_seconds)
                except TimeoutError: pass
                async with self._lock:
                    if not self._stop.is_set() and not self._pending and not self._active_pairs:
                        self.scanner.begin_new_round()
            return bool(batch.scanned_jobs or batch.persisted or batch.already_processed or batch.issues)
        await run_pool(process,self._stop,concurrency=self.concurrency,interval=self.interval_seconds,
                       on_error=lambda: logger.warning("RELATIONSHIP_POLL_FAILED"))
