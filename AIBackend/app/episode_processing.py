"""Episode catch-up: bounded storage retries and one owned, drained worker thread."""

from __future__ import annotations

import asyncio
import logging
import sqlite3
import threading
import time
from collections.abc import Callable
from typing import TypeVar

from .episode_assembler import EpisodeAssembler
from .episode_models import MemoryJobPolicy
from .memory_job_repository import MemoryJobRepository


logger = logging.getLogger("embodiednpc.backend.episode_processing")
T = TypeVar("T")


class EpisodeStorageGuard:
    """Retry only rolled-back BUSY operations, never a partially committed observer.

    This budget is independent of durable Memory Job retries. Callers must supply
    one repository operation (or a read-only preview), preserving its arguments.
    """

    def __init__(self, *, sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.time) -> None:
        self._sleep, self._clock = sleep, clock
        self._lock = threading.Lock()
        self._status: dict[str, object] = dict(
            state="STOPPED", stopping=False, phase=None, attempt=0,
            next_retry_at=None, last_success_at=None, last_failure_at=None,
            error_code=None, sqlite_code=None,
        )

    def snapshot(self) -> dict[str, object]:
        with self._lock:
            return dict(self._status)

    def update(self, **values: object) -> None:
        with self._lock:
            # Stopping must not erase the fault that stopped processing.
            if self._status["state"] == "FAULTED":
                values.pop("state", None)
            self._status.update(values)

    def fault(self, error: Exception, code: str = "EPISODE_STORAGE_FAILED") -> None:
        with self._lock:
            if self._status["state"] == "FAULTED":
                return
            sqlite_code = getattr(error, "sqlite_errorcode", None)
            self._status.update(state="FAULTED", error_code=code,
                                sqlite_code=sqlite_code, last_failure_at=self._clock(),
                                next_retry_at=None)
            phase = self._status["phase"]
        # Exception messages/tracebacks may contain SQL, paths or raw evidence.
        logger.error("EPISODE_WORKER_FAULTED phase=%s code=%s sqlite_code=%s",
                     phase, code, sqlite_code)

    def call(self, phase: str, operation: Callable[[], T]) -> T:
        for attempt in range(1, 4):
            self.update(phase=phase, attempt=attempt)
            try:
                result = operation()
            except Exception as error:
                sqlite_code = getattr(error, "sqlite_errorcode", None)
                busy = (isinstance(error, sqlite3.OperationalError)
                        and isinstance(sqlite_code, int)
                        and sqlite_code & 0xff == sqlite3.SQLITE_BUSY)
                if not busy or attempt == 3:
                    self.fault(error, "EPISODE_STORAGE_BUSY_EXHAUSTED" if busy
                               else "EPISODE_STORAGE_FAILED")
                    raise
                delay = (0.25, 1.0)[attempt - 1]
                self.update(state="RETRYING", next_retry_at=self._clock() + delay,
                            last_failure_at=self._clock(), error_code="SQLITE_BUSY",
                            sqlite_code=sqlite_code)
                logger.warning("EPISODE_STORAGE_RETRY phase=%s attempt=%s sqlite_code=%s",
                               phase, attempt, sqlite_code)
                # Finish this operation even during stop: abandoning the assembler
                # frame here could lose a previously committed terminal boundary.
                self._sleep(delay)
            else:
                if attempt > 1:
                    logger.info("EPISODE_STORAGE_RECOVERED phase=%s", phase)
                self.update(state="RUNNING", next_retry_at=None)
                return result
        raise AssertionError("EPISODE_RETRY_BUDGET_INVALID")


class EpisodeProcessingLoop:
    """Poll persisted experiences into Episodes/Jobs, not model-generated Memory.

    Cancelling an asyncio waiter cannot stop synchronous SQLite work. The worker
    owner must drain in-flight storage before releasing shared resources; lost
    transport or shutdown alone cannot justify deleting/rebuilding durable data.
    """

    def __init__(
        self,
        assembler: EpisodeAssembler,
        jobs: MemoryJobRepository,
        *,
        interval_seconds: float = 1.0,
        job_policy: MemoryJobPolicy | None = None,
        storage_guard: EpisodeStorageGuard | None = None,
    ) -> None:
        if interval_seconds <= 0:
            raise ValueError("EPISODE_POLL_INTERVAL_INVALID")
        self.assembler = assembler
        self.jobs = jobs
        self.interval_seconds = interval_seconds
        self.job_policy = job_policy or MemoryJobPolicy()
        self.storage_guard = storage_guard or EpisodeStorageGuard()
        self._task: asyncio.Task[None] | None = None
        self._inflight: asyncio.Task[None] | None = None
        self._stop_task: asyncio.Task[None] | None = None
        self._stop = asyncio.Event()

    def snapshot(self) -> dict[str, object]:
        return self.storage_guard.snapshot()

    @property
    def is_quiescent(self) -> bool:
        return self._inflight is None and (self._task is None or self._task.done())

    def start(self) -> None:
        if (self.snapshot()["state"] == "FAULTED"
                or self._stop_task is not None and not self._stop_task.done()
                or self._task is not None and not self._task.done()):
            return
        self._stop = asyncio.Event()
        self._stop_task = None
        self.storage_guard.update(state="RUNNING", stopping=False)
        self._task = asyncio.create_task(self._run(), name="episode-processing-loop")
        logger.info("EPISODE_WORKER_STARTED")

    async def stop(self) -> None:
        self._stop.set()
        if self._stop_task is None:
            self.storage_guard.update(stopping=True)
            self._stop_task = asyncio.create_task(self._finish_stop())
        # Cancelling a waiter must not cancel the owner of a synchronous DB call.
        cancelled = False
        while not self._stop_task.done():
            try:
                await asyncio.shield(self._stop_task)
            except asyncio.CancelledError:
                cancelled = True
        self._stop_task.result()
        if cancelled:
            raise asyncio.CancelledError

    async def _finish_stop(self) -> None:
        if self._task is not None:
            try:
                await self._task
            except asyncio.CancelledError:
                pass  # The worker has already drained its in-flight thread.
            except Exception as error:
                self.storage_guard.fault(error, "EPISODE_WORKER_FAILED")
            self._task = None
        self.storage_guard.update(state="STOPPED", stopping=False)
        logger.info("EPISODE_WORKER_STOPPED state=%s", self.snapshot()["state"])

    def process_once(self, now: float | None = None) -> None:
        current = time.time() if now is None else now
        call = self.storage_guard.call
        recovered = call("recover_stale", lambda: self.jobs.recover_stale(current, self.job_policy))
        for observer_id in call("list_observer_heads", self.assembler.ledger.list_observer_heads):
            self.storage_guard.update(phase="process_observer")
            self.assembler.process_observer(observer_id)
        self.storage_guard.update(phase="idle_sweep")
        self.assembler.idle_sweep(current)
        backlog = call("backlog", lambda: self.jobs.backlog(current))
        logger.info(
            "Episode backlog pending=%s retry=%s oldest_age=%s observers=%s stale_recovered=%s last_failure=%s",
            backlog.pending_count, backlog.retry_count, backlog.oldest_pending_age_seconds,
            backlog.jobs_per_observer, len(recovered), backlog.last_failure_reason,
        )

    async def _run(self) -> None:
        try:
            while not self._stop.is_set():
                self._inflight = asyncio.create_task(asyncio.to_thread(self.process_once))
                cancelled = False
                # Keep the thread task referenced and uncancelled until its result
                # is observed, including repeated external cancellation requests.
                while not self._inflight.done():
                    try:
                        await asyncio.shield(self._inflight)
                    except asyncio.CancelledError:
                        cancelled = True
                        self._stop.set()
                    except Exception:
                        break
                try:
                    self._inflight.result()
                except Exception as error:
                    self.storage_guard.fault(error, "EPISODE_WORKER_FAILED")
                finally:
                    self._inflight = None
                if cancelled:
                    raise asyncio.CancelledError
                if self.snapshot()["state"] == "FAULTED":
                    return
                self.storage_guard.update(last_success_at=time.time())
                try:
                    await asyncio.wait_for(self._stop.wait(), timeout=self.interval_seconds)
                except TimeoutError:
                    pass
        finally:
            self.storage_guard.update(state="STOPPED")
