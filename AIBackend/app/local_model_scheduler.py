"""Bounded, work-conserving model admission shared by foreground and workers."""
from __future__ import annotations
import asyncio
from dataclasses import dataclass
from typing import Literal
from .providers import ModelProvider, ModelRequest, ModelResult

ModelPriority = Literal["foreground", "background"]
class PreemptedByForeground(RuntimeError): pass
class ModelQueueFull(RuntimeError): pass
class ModelExecutorClosed(RuntimeError): pass

@dataclass(eq=False)
class _Call:
    priority: ModelPriority
    preemptible: bool
    task: asyncio.Task | None = None
    preempted: bool = False
    protected: bool = False

class PrioritizedModelExecutor:
    """FIFO within classes; after four foreground admissions, serve eligible background.

    Limits apply only to generate(), never context reads. Local preemption is
    opt-in; a fairness turn is protected so derived work cannot starve.
    """
    def __init__(self, total_limit=1, foreground_limit=1, background_limit=1, *,
                 preempt_background=True, queue_capacity=64, queue_timeout=30.,
                 execution_timeout=600., foreground_burst=4):
        if (any(type(v) is not int or not 1 <= v <= 32 for v in
                (total_limit, foreground_limit, background_limit)) or
                max(foreground_limit, background_limit) > total_limit):
            raise ValueError("MODEL_CONCURRENCY_INVALID")
        if queue_capacity < 1 or queue_timeout <= 0 or execution_timeout <= 0 or foreground_burst < 1:
            raise ValueError("MODEL_QUEUE_POLICY_INVALID")
        self.total_limit, self.foreground_limit, self.background_limit = total_limit, foreground_limit, background_limit
        self.preempt_background = preempt_background
        self.queue_capacity, self.queue_timeout = queue_capacity, queue_timeout
        self.execution_timeout, self.foreground_burst = execution_timeout, foreground_burst
        self._condition = asyncio.Condition()
        self._queues = {"foreground": [], "background": []}
        self._running: list[_Call] = []
        self._foreground_streak = 0
        self._closed = False

    async def execute(self, provider: ModelProvider, request: ModelRequest, *, priority: ModelPriority) -> ModelResult:
        return await self._execute(provider, request, priority=priority, preemptible=False)

    async def execute_preemptible_background(self, provider, request):
        return await self._execute(provider, request, priority="background", preemptible=True)

    def _next(self):
        if len(self._running) >= self.total_limit: return None
        eligible = {p: bool(q) and sum(c.priority == p for c in self._running) < limit
                    for (p, q), limit in zip(self._queues.items(), (self.foreground_limit, self.background_limit))}
        if eligible["background"] and (not eligible["foreground"] or self._foreground_streak >= self.foreground_burst):
            return self._queues["background"][0]
        if eligible["foreground"]: return self._queues["foreground"][0]
        return None

    async def _execute(self, provider, request, *, priority, preemptible):
        if priority not in self._queues: raise ValueError("MODEL_PRIORITY_INVALID")
        call = _Call(priority, preemptible)
        async with self._condition:
            if self._closed: raise ModelExecutorClosed("MODEL_EXECUTOR_CLOSED")
            queued = sum(map(len, self._queues.values()))
            self._queues[priority].append(call)
            # A full waiting queue must not reject work that can run now in
            # otherwise idle capacity (for example a foreground behind BG caps).
            if queued >= self.queue_capacity and self._next() is not call:
                self._queues[priority].remove(call)
                raise ModelQueueFull("MODEL_QUEUE_FULL")
            if (priority == "foreground" and self.preempt_background
                    and len(self._running) >= self.total_limit
                    and sum(c.priority == "foreground" for c in self._running) < self.foreground_limit):
                for active in self._running:
                    if active.preemptible and not active.protected and not active.preempted:
                        active.preempted = True
                        active.task.cancel()
                        # The next background admission is a protected fairness turn.
                        self._foreground_streak = self.foreground_burst - 1
                        break
            self._condition.notify_all()
            try:
                async with asyncio.timeout(self.queue_timeout):
                    await self._condition.wait_for(lambda: self._closed or self._next() is call)
                if self._closed: raise ModelExecutorClosed("MODEL_EXECUTOR_CLOSED")
                self._queues[priority].remove(call)
                call.protected = priority == "background" and self._foreground_streak >= self.foreground_burst
                self._foreground_streak = min(self._foreground_streak + 1, self.foreground_burst) if priority == "foreground" else 0
                call.task = asyncio.create_task(provider.generate(request))
                self._running.append(call)
                self._condition.notify_all()
            except BaseException:
                if call in self._queues[priority]: self._queues[priority].remove(call)
                self._condition.notify_all()
                raise
        try:
            async with asyncio.timeout(self.execution_timeout):
                return await call.task
        except asyncio.CancelledError as exc:
            if call.preempted: raise PreemptedByForeground("PREEMPTED_BY_FOREGROUND") from exc
            raise
        finally:
            # generate must unwind before its capacity is made available.
            from .worker_runtime import drain_task
            if not call.task.done():
                call.task.cancel()
                try: await drain_task(call.task)
                except asyncio.CancelledError: pass
            async def release():
                async with self._condition:
                    self._running.remove(call)
                    self._condition.notify_all()
            await drain_task(asyncio.create_task(release()))

    def open(self):
        if self._running or any(self._queues.values()):
            raise RuntimeError("MODEL_EXECUTOR_NOT_QUIESCENT")
        self._closed = False

    async def close(self):
        """Reject/wake queued callers, then join providers and admission cleanup."""
        from .worker_runtime import drain_task
        await drain_task(asyncio.create_task(self._close()))

    async def _close(self):
        async with self._condition:
            self._closed = True
            self._condition.notify_all()
            running = [c.task for c in self._running]
        if running: await asyncio.gather(*running, return_exceptions=True)
        async with self._condition:
            await self._condition.wait_for(lambda: not self._running and not any(self._queues.values()))
