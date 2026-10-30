from __future__ import annotations

import asyncio
import unittest
from unittest.mock import AsyncMock

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import MockProvider, ModelMessage, ModelRequest, ModelResult
from tests.test_cognitive_request import _event_request
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


class CognitiveSchedulerTests(unittest.IsolatedAsyncioTestCase):
    async def test_entry_uses_injected_executor_with_foreground_priority(self):
        provider = MockProvider()
        executor = AsyncMock(spec=PrioritizedModelExecutor)
        executor.execute.return_value = ModelResult(text='{"decision_kind":"NO_ACTION"}')
        entry = CognitiveBackendEntry(provider, executor, context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
        await entry.run(CognitiveRequest.model_validate(_event_request()))
        executor.execute.assert_awaited_once()
        args, kwargs = executor.execute.call_args
        self.assertIs(args[0], provider)
        self.assertEqual(args[1].response_schema["title"], "CognitiveStepResult")
        self.assertEqual(kwargs, {"priority": "foreground"})

    async def test_cognition_precedes_queued_background_without_parallel_provider_calls(self):
        executor = PrioritizedModelExecutor()
        started, release, background_queued, foreground_queued = (
            asyncio.Event() for _ in range(4)
        )
        order = []
        active = 0
        peak = 0

        class Provider:
            # Match the current ModelProvider/CapacityCapture metadata contract.
            name = "scripted"
            model = "scheduler-contract-test"
            context_length = DEFAULT_COGNITIVE_CONTEXT_LENGTH

            async def generate(self, request):
                nonlocal active, peak
                active += 1
                peak = max(peak, active)
                label = "cognition" if request.response_schema else request.messages[0].content
                order.append(label)
                try:
                    if label == "initial":
                        started.set()
                        await release.wait()
                    return ModelResult(text='{"decision_kind":"NO_ACTION"}')
                finally:
                    active -= 1

        provider = Provider()

        async def background():
            background_queued.set()
            return await executor.execute(provider, ModelRequest(messages=(
                ModelMessage(role="user", content="background"),
            )), priority="background")

        async def foreground():
            foreground_queued.set()
            return await CognitiveBackendEntry(provider, executor, context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH).run(
                CognitiveRequest.model_validate(_event_request())
            )

        initial = asyncio.create_task(executor.execute(provider, ModelRequest(messages=(
            ModelMessage(role="user", content="initial"),
        )), priority="background"))
        await asyncio.wait_for(started.wait(), 1)
        later = asyncio.create_task(background())
        await asyncio.wait_for(background_queued.wait(), 1)
        cognition = asyncio.create_task(foreground())
        await asyncio.wait_for(foreground_queued.wait(), 1)
        # Each event is set immediately before awaiting the occupied executor,
        # so both callers have reached its wait before the initial call is released.
        release.set()
        await asyncio.wait_for(asyncio.gather(initial, later, cognition), 1)
        self.assertEqual(order, ["initial", "cognition", "background"])
        self.assertEqual(peak, 1)
