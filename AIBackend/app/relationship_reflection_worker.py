"""Small background consumer from discovered pair candidates to persisted results."""

from __future__ import annotations

import asyncio
import hashlib
import json
import math
import sqlite3
import time
from dataclasses import dataclass

from .local_model_scheduler import PreemptedByForeground
from .relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION, RelationshipReflectionCandidate,
)
from .relationship_reflection_model import RelationshipReflectionModel, RelationshipReflectionModelError
from .relationship_reflection_models import RelationshipReflectionRecord
from .relationship_reflection_repository import RelationshipReflectionRepository
from .relationship_reflection_scanner import RelationshipReflectionScanner


@dataclass(frozen=True)
class RelationshipExecutionIssue:
    episode_id: str
    reason: str


@dataclass(frozen=True)
class RelationshipExecutionBatch:
    scanned_jobs: int
    persisted: int
    already_processed: int
    issues: tuple[RelationshipExecutionIssue, ...]
    end_of_round: bool


class RelationshipReflectionWorker:
    """Process bounded observer/subject batches sequentially; no durable job state.

    Persisted unique episode/pair keys deduplicate rediscovery after restart.
    Interpretations remain subjective relationship evidence, not world facts;
    an uncommitted/preempted pair can be discovered again in a later round.
    """

    def __init__(self, scanner: RelationshipReflectionScanner,
                 model: RelationshipReflectionModel,
                 reflections: RelationshipReflectionRepository,
                 *, clock=time.time, interval_seconds: float = 1.0) -> None:
        if not math.isfinite(interval_seconds) or interval_seconds <= 0:
            raise ValueError("RELATIONSHIP_POLL_INTERVAL_INVALID")
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

    async def process_batch(self, *, job_limit: int = 4,
                            candidate_limit: int = 4) -> RelationshipExecutionBatch:
        if not 1 <= job_limit <= 16 or not 1 <= candidate_limit <= 16:
            raise ValueError("RELATIONSHIP_WORK_BATCH_LIMIT_INVALID")
        async with self._lock:
            scanned_jobs = 0
            issues: list[RelationshipExecutionIssue] = []
            if not self._pending:
                discovered = await asyncio.to_thread(self.scanner.scan_next_batch, limit=job_limit)
                self._pending.extend(discovered.candidates)
                scanned_jobs = discovered.scanned_jobs
                issues.extend(RelationshipExecutionIssue(issue.job_id, issue.reason)
                              for issue in discovered.issues)
            persisted = already_processed = 0
            for _ in range(min(candidate_limit, len(self._pending))):
                candidate = self._pending.pop(0)
                pair = (candidate.observer, candidate.subject, candidate.episode_id,
                        RELATIONSHIP_REFLECTION_POLICY_VERSION)
                if await asyncio.to_thread(self.reflections.exists_for_episode_pair, *pair):
                    already_processed += 1
                    continue
                try:
                    response = await self.model.interpret(candidate)
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
                        await asyncio.to_thread(self.reflections.insert, record)
                    except sqlite3.IntegrityError:
                        # A concurrent/late result can win the unique pair key; other integrity
                        # failures remain errors and must not be mistaken for successful work.
                        if not await asyncio.to_thread(self.reflections.exists_for_episode_pair, *pair):
                            raise
                        already_processed += 1
                    else:
                        persisted += 1
                except (RelationshipReflectionModelError, PreemptedByForeground) as exc:
                    issues.append(RelationshipExecutionIssue(candidate.episode_id, str(exc)))
            return RelationshipExecutionBatch(
                scanned_jobs, persisted, already_processed, tuple(issues),
                self.scanner.end_of_round and not self._pending,
            )

    def start(self) -> None:
        if self._task is None or self._task.done():
            self._stop = asyncio.Event()
            self._task = asyncio.create_task(self._run(), name="relationship-reflection-worker")

    async def stop(self) -> None:
        self._stop.set()
        if self._task is not None:
            await self._task
            self._task = None

    async def _run(self) -> None:
        while not self._stop.is_set():
            try:
                batch = await self.process_batch()
            except Exception:
                # No raw provider/evidence text in logs or a retry record. The next round
                # may rediscover any uncommitted pair from canonical C2 history.
                batch = None
            if batch is not None and not batch.end_of_round:
                continue
            try:
                await asyncio.wait_for(self._stop.wait(), timeout=self.interval_seconds)
            except TimeoutError:
                pass
            if not self._stop.is_set() and (batch is None or batch.end_of_round):
                self.scanner.begin_new_round()
