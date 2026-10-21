from __future__ import annotations

from contextlib import closing
from pathlib import Path
import asyncio
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_consolidation_context import ConsolidationEvidenceEvent, TrustedPersistentIdentity
from app.sqlite_database import SQLiteDatabase
from app.providers import ModelResult, ProviderError
from app.relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION as POLICY, RelationshipReflectionCandidate,
)
from app.relationship_reflection_model import RelationshipReflectionModel
from app.relationship_reflection_models import RelationshipReflectionOutcome
from app.relationship_reflection_repository import RelationshipReflectionRepository
from app.relationship_reflection_scanner import RelationshipScanBatch
from app.relationship_reflection_worker import RelationshipReflectionWorker


class FakeProvider:
    name = "fake"
    model = "relationship-test"
    context_length = 8192

    def __init__(self, outputs: list[str | Exception]) -> None:
        self.outputs = outputs
        self.requests = []

    async def generate(self, request):
        self.requests.append(request)
        output = self.outputs.pop(0)
        if isinstance(output, Exception):
            raise output
        return ModelResult(text=output)


class RecordingExecutor(PrioritizedModelExecutor):
    def __init__(self) -> None:
        super().__init__()
        self.background_calls = 0

    async def execute_preemptible_background(self, provider, request):
        self.background_calls += 1
        return await super().execute_preemptible_background(provider, request)


class StubScanner:
    """The Slice 2 scanner owns real discovery; this seam isolates execution."""

    def __init__(self, candidate: RelationshipReflectionCandidate) -> None:
        self.candidate = candidate
        self.end_of_round = False

    def scan_next_batch(self, *, limit):
        self.end_of_round = True
        return RelationshipScanBatch((self.candidate,), (), 1, True)

    def begin_new_round(self):
        self.end_of_round = False


class RelationshipReflectionExecutionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.database = SQLiteDatabase(Path(directory.name) / "memory.sqlite3")
        self.reflections = RelationshipReflectionRepository(self.database.connect)
        with closing(self.database.connect()) as connection:
            with connection:
                connection.execute(
                    """
                    INSERT INTO episodes (
                        episode_id, observer_id, source_session_id, start_seq, end_seq,
                        started_at_utc, ended_at_utc, status, close_reason, event_count,
                        policy_version, schema_version, created_at_utc, updated_at_utc
                    ) VALUES ('episode_secret', 'npc_01', 'session_1', 1, 2,
                              1, 2, 'CLOSED', 'test_end', 2,
                              'episode-boundary/1.0', 'episode/1.0', 1, 2)
                    """
                )
        observer = TrustedPersistentIdentity("npc", "Character", "npc-secret-uid")
        subject = TrustedPersistentIdentity("player", "Player", "player-secret-uid")
        events = (
            ConsolidationEvidenceEvent(
                experience_seq=1, event_id="secret_event_1", event_type="dialogue_input",
                source_type="dialogue", actor_entity_id="player_01",
                target_entity_ids=("npc_01",),
                participant_handles={"player_01": "participant_2", "npc_01": "participant_1"},
                structured_payload={}, natural_language_payload="你好，今天忙吗？",
                occurred_at=1, observed_at=1, world_revision=None, confidence=None,
                provenance="HEARD",
            ),
            ConsolidationEvidenceEvent(
                experience_seq=2, event_id="secret_event_2", event_type="dialogue_response",
                source_type="dialogue", actor_entity_id="npc_01",
                target_entity_ids=("player_01",),
                participant_handles={"player_01": "participant_2", "npc_01": "participant_1"},
                structured_payload={}, natural_language_payload="有点忙，但很高兴见到你。",
                occurred_at=2, observed_at=2, world_revision=None, confidence=None,
                provenance="EXECUTED_BY_SELF",
            ),
        )
        self.candidate = RelationshipReflectionCandidate(
            observer=observer, subject=subject, episode_id="episode_secret",
            episode_input_hash="a" * 64, evidence_events=events,
        )

    def worker(self, outputs):
        provider = FakeProvider(list(outputs))
        executor = RecordingExecutor()
        scanner = StubScanner(self.candidate)
        worker = RelationshipReflectionWorker(
            scanner, RelationshipReflectionModel(provider, executor), self.reflections,
            clock=lambda: 123.0,
        )
        return worker, scanner, provider, executor

    async def test_interpretation_persists_backend_owned_metadata_and_bounded_input(self) -> None:
        worker, scanner, provider, executor = self.worker([
            '{"outcome":"INTERPRETATION","interpretation":"这次交流比较友善，增加了熟悉感。"}'
        ])
        batch = await worker.process_batch()
        self.assertEqual((batch.persisted, batch.issues), (1, ()))
        self.assertEqual(executor.background_calls, 1)
        record = self.reflections.get(worker.reflection_id_for(self.candidate))
        self.assertEqual(record.observer, self.candidate.observer)
        self.assertEqual(record.subject, self.candidate.subject)
        self.assertEqual(record.episode_id, self.candidate.episode_id)
        self.assertEqual(record.episode_input_hash, self.candidate.episode_input_hash)
        self.assertEqual(record.evidence_event_ids, self.candidate.evidence_event_ids)
        self.assertEqual(record.policy_version, POLICY)
        self.assertEqual(record.generated_at, 123.0)
        self.assertEqual(record.outcome, RelationshipReflectionOutcome.INTERPRETATION)
        request = provider.requests[0]
        visible = " ".join(message.content for message in request.messages)
        self.assertIn("你好，今天忙吗？", visible)
        self.assertLess(visible.index("你好，今天忙吗？"), visible.index("有点忙，但很高兴见到你。"))
        for secret in ("npc-secret-uid", "player-secret-uid", "episode_secret",
                       "secret_event_1", "secret_event_2", "participant_1", "participant_2"):
            self.assertNotIn(secret, visible)
        self.assertEqual(set(request.response_schema["properties"]), {"outcome", "interpretation"})
        scanner.begin_new_round()
        again = await worker.process_batch()
        self.assertEqual((again.persisted, again.already_processed), (0, 1))
        self.assertEqual(len(provider.requests), 1)

    async def test_no_change_persists_null_and_is_processed(self) -> None:
        worker, scanner, provider, _ = self.worker([
            '{"outcome":"NO_CHANGE","interpretation":null}'
        ])
        self.assertEqual((await worker.process_batch()).persisted, 1)
        record = self.reflections.get(worker.reflection_id_for(self.candidate))
        self.assertEqual(record.outcome, RelationshipReflectionOutcome.NO_CHANGE)
        self.assertIsNone(record.interpretation)
        self.assertTrue(self.reflections.exists_for_episode_pair(
            self.candidate.observer, self.candidate.subject, self.candidate.episode_id, POLICY,
        ))
        scanner.begin_new_round()
        self.assertEqual((await worker.process_batch()).already_processed, 1)
        self.assertEqual(len(provider.requests), 1)

    async def test_invalid_outputs_fail_closed(self) -> None:
        invalid = (
            '{"outcome":"UNKNOWN","interpretation":"x"}',
            '{"outcome":"INTERPRETATION","interpretation":null}',
            '{"outcome":"NO_CHANGE","interpretation":"x"}',
            '{"outcome":"INTERPRETATION","interpretation":"x","subject":"forged"}',
            '{"outcome":"INTERPRETATION","interpretation":',
            'Here is JSON: {"outcome":"NO_CHANGE","interpretation":null}',
        )
        for raw in invalid:
            with self.subTest(raw=raw):
                worker, _, _, _ = self.worker([raw])
                result = await worker.process_batch()
                self.assertEqual(result.persisted, 0)
                self.assertEqual(result.issues[0].reason, "RELATIONSHIP_OUTPUT_INVALID")
                self.assertFalse(self.reflections.exists_for_episode_pair(
                    self.candidate.observer, self.candidate.subject, self.candidate.episode_id, POLICY,
                ))

    async def test_model_cannot_smuggle_backend_identity_into_interpretation(self) -> None:
        worker, _, _, _ = self.worker([
            '{"outcome":"INTERPRETATION","interpretation":"player-secret-uid"}'
        ])
        batch = await worker.process_batch()
        self.assertEqual(batch.persisted, 0)
        self.assertEqual(batch.issues[0].reason, "RELATIONSHIP_OUTPUT_AUTHORITY_LEAK")
        self.assertIsNone(self.reflections.get(worker.reflection_id_for(self.candidate)))

    async def test_provider_failure_leaves_candidate_for_next_round(self) -> None:
        worker, scanner, provider, _ = self.worker([
            ProviderError("OLLAMA_UNAVAILABLE"),
            '{"outcome":"INTERPRETATION","interpretation":"这次交流友善。"}',
        ])
        failed = await worker.process_batch()
        self.assertEqual(failed.persisted, 0)
        self.assertEqual(failed.issues[0].reason, "RELATIONSHIP_PROVIDER_FAILED")
        self.assertIsNone(self.reflections.get(worker.reflection_id_for(self.candidate)))
        scanner.begin_new_round()
        retried = await worker.process_batch()
        self.assertEqual(retried.persisted, 1)
        self.assertEqual(len(provider.requests), 2)

    async def test_oversized_input_fails_before_provider(self) -> None:
        worker, _, provider, _ = self.worker([])
        event = self.candidate.evidence_events[0].model_copy(
            update={"natural_language_payload": "x" * 1001}
        )
        worker.scanner.candidate = RelationshipReflectionCandidate(
            observer=self.candidate.observer, subject=self.candidate.subject,
            episode_id=self.candidate.episode_id,
            episode_input_hash=self.candidate.episode_input_hash,
            evidence_events=(event,),
        )
        batch = await worker.process_batch()
        self.assertEqual(batch.persisted, 0)
        self.assertEqual(batch.issues[0].reason, "RELATIONSHIP_DIALOGUE_TEXT_INVALID")
        self.assertEqual(provider.requests, [])

    async def test_unique_race_is_treated_as_already_processed(self) -> None:
        worker, _, _, _ = self.worker([
            '{"outcome":"NO_CHANGE","interpretation":null}'
        ])
        original_insert = self.reflections.insert

        def concurrent_insert(record):
            original_insert(record)
            raise sqlite3.IntegrityError("UNIQUE constraint failed")

        with patch.object(self.reflections, "insert", side_effect=concurrent_insert):
            batch = await worker.process_batch()
        self.assertEqual((batch.persisted, batch.already_processed), (0, 1))
        self.assertIsNotNone(self.reflections.get(worker.reflection_id_for(self.candidate)))

    async def test_start_and_stop_run_the_bounded_background_loop(self) -> None:
        worker, _, provider, _ = self.worker([
            '{"outcome":"NO_CHANGE","interpretation":null}'
        ])
        worker.start()
        try:
            for _ in range(100):
                if self.reflections.get(worker.reflection_id_for(self.candidate)) is not None:
                    break
                await asyncio.sleep(0.01)
            self.assertIsNotNone(self.reflections.get(worker.reflection_id_for(self.candidate)))
        finally:
            await worker.stop()
        self.assertEqual(len(provider.requests), 1)
