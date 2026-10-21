"""A minimal, cooperative foreground-before-background model execution gate."""

from __future__ import annotations

import asyncio
from typing import Literal

from .providers import ModelProvider, ModelRequest, ModelResult


ModelPriority = Literal["foreground", "background"]


class PreemptedByForeground(RuntimeError):
    pass


class PrioritizedModelExecutor:
    """Serialize local model calls and prefer queued foreground work.

    Ordinary execute() calls are not preempted. Work explicitly submitted via
    execute_preemptible_background() may be cooperatively cancelled when a
    foreground caller arrives; its owner must requeue/discard derived work.
    The active slot is released after the provider task unwinds, not when
    cancellation is requested. This is an admission gate, not a GPU scheduler.
    """

    def __init__(self) -> None:
        self._condition = asyncio.Condition()
        self._active = False
        self._waiting_foreground = 0
        self._active_preemptible = False
        self._active_provider_task: asyncio.Task[ModelResult] | None = None
        self._preempt_requested = False

    async def execute(
        self, provider: ModelProvider, request: ModelRequest, *, priority: ModelPriority
    ) -> ModelResult:
        return await self._execute(provider, request, priority=priority, preemptible=False)

    async def execute_preemptible_background(
        self, provider: ModelProvider, request: ModelRequest
    ) -> ModelResult:
        """Run derived work that a newly queued foreground call may cancel."""

        return await self._execute(provider, request, priority="background", preemptible=True)

    async def _execute(
        self,
        provider: ModelProvider,
        request: ModelRequest,
        *,
        priority: ModelPriority,
        preemptible: bool,
    ) -> ModelResult:
        foreground = priority == "foreground"
        async with self._condition:
            if foreground:
                self._waiting_foreground += 1
                if self._active_preemptible and self._active_provider_task is not None:
                    self._preempt_requested = True
                    self._active_provider_task.cancel()
            try:
                await self._condition.wait_for(
                    lambda: not self._active and (foreground or self._waiting_foreground == 0)
                )
                self._active = True
                self._active_preemptible = preemptible
                self._preempt_requested = False
                provider_task = asyncio.create_task(provider.generate(request))
                self._active_provider_task = provider_task
            finally:
                if foreground:
                    self._waiting_foreground -= 1
        try:
            return await provider_task
        except asyncio.CancelledError as exc:
            if preemptible and self._preempt_requested:
                raise PreemptedByForeground("PREEMPTED_BY_FOREGROUND") from exc
            raise
        finally:
            async with self._condition:
                self._active = False
                self._active_preemptible = False
                self._active_provider_task = None
                self._preempt_requested = False
                self._condition.notify_all()
