"""Production lifespan gates for the Relationship Reflection worker."""

import importlib
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch


class RelationshipReflectionLifecycleTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.directory.cleanup)
        with patch.dict(os.environ, {
            "EMBODIEDNPC_MEMORY_DB": str(Path(cls.directory.name) / "memory.sqlite3"),
            "EMBODIEDNPC_PROVIDER": "mock",
            "CANONICAL_EMBEDDING_ENABLED": "false",
        }):
            cls.entry = importlib.import_module("app.main")
            cls.backend = importlib.import_module("app.m3_main")

    async def exercise_lifespan(self, provider):
        backend = self.backend
        calls = []
        names = (
            "episode_processing_loop", "memory_consolidation_worker", "relationship_reflection_worker",
        )
        workers = {
            name: SimpleNamespace(
                start=Mock(side_effect=lambda name=name: calls.append(f"{name}.start")),
                stop=AsyncMock(side_effect=lambda name=name: calls.append(f"{name}.stop")),
            )
            for name in names
        }
        with patch.multiple(
            backend, **workers,
            provider=SimpleNamespace(name=provider),
            canonical_embedding_indexer=None,
        ):
            async with self.entry.app.router.lifespan_context(self.entry.app):
                pass
        return calls

    async def test_v2_real_provider_starts_and_stops_in_dependency_order(self):
        backend = self.backend
        self.assertIs(backend.memory_consolidation_worker.resolver, backend.memory_context_resolver)
        self.assertIs(backend.relationship_reflection_worker.scanner.resolver, backend.memory_context_resolver)
        self.assertIs(backend.relationship_reflection_worker.model.provider, backend.provider)
        self.assertIs(backend.relationship_reflection_worker.model.executor, backend.model_executor)
        self.assertIs(backend.relationship_reflection_worker.reflections, backend.relationship_reflection_repository)
        self.assertEqual(await self.exercise_lifespan("ollama"), [
            "episode_processing_loop.start",
            "memory_consolidation_worker.start",
            "relationship_reflection_worker.start",
            "relationship_reflection_worker.stop",
            "memory_consolidation_worker.stop",
            "episode_processing_loop.stop",
        ])

    async def test_mock_provider_does_not_start_reflection(self):
        self.assertEqual(await self.exercise_lifespan("mock"), [
            "episode_processing_loop.start", "episode_processing_loop.stop",
        ])
