from __future__ import annotations

from contextlib import closing
from pathlib import Path
import tempfile
import time
import unittest

from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_consolidation_context import MemoryContextResolver, TrustedPersistentIdentity
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.sqlite_database import SQLiteDatabase
from app.relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION as POLICY,
    RelationshipReflectionCandidateBuilder,
)
from app.relationship_reflection_models import RelationshipReflectionOutcome, RelationshipReflectionRecord
from app.relationship_reflection_repository import RelationshipReflectionRepository
from app.relationship_reflection_scanner import RelationshipReflectionScanner


MERCHANT = TrustedPersistentIdentity("npc", "Character", "cafe.merchant.001")
PLAYER_A = TrustedPersistentIdentity("player", "Player", "player-a")
PLAYER_B = TrustedPersistentIdentity("player", "Player", "player-b")
IDENTITIES = {"npc_01": MERCHANT, "player_a": PLAYER_A, "player_b": PLAYER_B}


class RelationshipReflectionCandidateTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        database = SQLiteDatabase(Path(directory.name) / "memory.sqlite3")
        self.connect = database.connect
        self.ledger = ExperienceLedger(self.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.connect)
        self.jobs = MemoryJobRepository(self.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.builder = RelationshipReflectionCandidateBuilder()
        self.reflections = RelationshipReflectionRepository(self.connect)
        self.scanner = RelationshipReflectionScanner(
            self.jobs, self.resolver, self.builder, self.reflections,
        )
        self.counter = 0

    def event(self, kind: str, actor: str, targets: tuple[str, ...],
              *, identities: tuple[str, ...] | None = None) -> dict:
        return dict(kind=kind, actor=actor, targets=targets, identities=identities)

    def episode(self, specs: tuple[dict, ...], *, created_at: float,
                status: str = "SUCCEEDED", version: str = V2):
        self.counter += 1
        episode_number = self.counter
        episode = None
        ids = []
        for spec in specs:
            self.counter += 1
            serial = self.counter
            kind, actor, targets = spec["kind"], spec["actor"], spec["targets"]
            identity_entities = spec["identities"]
            if identity_entities is None:
                identity_entities = tuple(dict.fromkeys((actor, *targets)))
            participants = [dict(
                entity_id=entity_id, identity_namespace=IDENTITIES[entity_id].identity_namespace,
                identity_type=IDENTITIES[entity_id].identity_type,
                persistent_id=IDENTITIES[entity_id].persistent_id,
            ) for entity_id in identity_entities]
            is_dialogue = kind.startswith("dialogue_")
            draft = ExperienceDraft(
                schema_version="experience-draft/1.1", observer_id="npc_01",
                event_type=kind, source_type="dialogue" if is_dialogue else "observation",
                source_session_id=f"session_{episode_number}", clock_domain="backend_utc",
                source_event_id=f"source_{serial}",
                source_observation_ids=() if is_dialogue else (f"observation_{serial}",),
                actor_entity_id=actor, target_entity_ids=targets,
                persistent_participants=participants,
                structured_payload={} if is_dialogue else {"perception_revision": 1},
                natural_language_payload=f"utterance_{serial}" if is_dialogue else None,
                occurred_at=float(serial), observed_at=float(serial),
                request_id=f"request_{serial}" if is_dialogue else None,
                conversation_id=f"conversation_{episode_number}" if is_dialogue else None,
                provenance="EXECUTED_BY_SELF" if actor == "npc_01" else "HEARD",
            )
            result = self.ingestion.append(ExperienceAppendRequest(
                request_id=f"append_{serial}", trace_id=f"trace_{serial}", draft=draft,
            ))
            self.assertEqual(result.status, "inserted")
            event = self.ledger.get_by_event_id(result.event_id)
            ids.append(event.event_id)
            if episode is None:
                episode = self.episodes.open_for_event(event, "episode-boundary/1.0")
            else:
                episode = self.episodes.append_event(episode.episode_id, event)
        _, job = self.episodes.close_and_enqueue(
            episode.episode_id, "test_end", time.time() + 1, version,
        )
        with closing(self.connect()) as connection:
            with connection:
                connection.execute(
                    "UPDATE memory_jobs SET status=?, created_at_utc=? WHERE job_id=?",
                    (status, created_at, job.job_id),
                )
        return self.jobs.get(job.job_id), tuple(ids)

    def test_single_pair_keeps_direct_dialogue_sequence(self) -> None:
        job, ids = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
            self.event("dialogue_response", "npc_01", ("player_a",)),
        ), created_at=1)
        batch = self.scanner.scan_next_batch()
        self.assertEqual(batch.scanned_jobs, 1)
        self.assertEqual(batch.issues, ())
        self.assertEqual(len(batch.candidates), 1)
        candidate = batch.candidates[0]
        self.assertEqual((candidate.observer, candidate.subject), (MERCHANT, PLAYER_A))
        self.assertEqual((candidate.episode_id, candidate.episode_input_hash),
                         (job.episode_id, job.input_hash))
        self.assertEqual(candidate.evidence_event_ids, ids)
        self.assertEqual(tuple(event.experience_seq for event in candidate.evidence_events), (1, 2))

    def test_two_players_and_third_party_dialogue_remain_pair_scoped(self) -> None:
        _, ids = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
            self.event("dialogue_input", "player_b", ("npc_01",)),
            self.event("dialogue_response", "npc_01", ("player_a",)),
            self.event("dialogue_input", "player_a", ("player_b",)),
            self.event("observation", "npc_01", ("player_a", "player_b")),
            self.event("dialogue_response", "npc_01", ("player_b",)),
        ), created_at=1)
        candidates = self.scanner.scan_next_batch().candidates
        self.assertEqual(tuple(candidate.subject for candidate in candidates), (PLAYER_A, PLAYER_B))
        self.assertEqual(candidates[0].evidence_event_ids, (ids[0], ids[2]))
        self.assertEqual(candidates[1].evidence_event_ids, (ids[1], ids[5]))

    def test_missing_observer_or_subject_identity_fails_closed(self) -> None:
        self.episode((self.event("dialogue_input", "player_a", ("npc_01",),
                                 identities=("player_a",)),), created_at=1)
        self.episode((self.event("dialogue_input", "player_b", ("npc_01",),
                                 identities=("npc_01",)),), created_at=2)
        batch = self.scanner.scan_next_batch()
        self.assertEqual(batch.scanned_jobs, 2)
        self.assertEqual(batch.candidates, ())

    def test_player_only_dialogue_is_eligible_but_npc_only_is_not(self) -> None:
        _, first_ids = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
        ), created_at=1)
        _, second_ids = self.episode((
            self.event("dialogue_response", "npc_01", ("player_b",)),
        ), created_at=2)
        candidates = self.scanner.scan_next_batch().candidates
        self.assertEqual(tuple(value.evidence_event_ids for value in candidates),
                         (first_ids,))

    def test_interpretation_and_no_change_both_count_as_processed(self) -> None:
        for index, outcome in enumerate((
            RelationshipReflectionOutcome.INTERPRETATION,
            RelationshipReflectionOutcome.NO_CHANGE,
        ), start=1):
            job, ids = self.episode((
                self.event("dialogue_input", "player_a", ("npc_01",)),
            ), created_at=index)
            self.reflections.insert(RelationshipReflectionRecord(
                reflection_id=f"reflection_{index}", observer=MERCHANT, subject=PLAYER_A,
                episode_id=job.episode_id, episode_input_hash=job.input_hash,
                outcome=outcome,
                interpretation="这次交流友善。" if outcome is RelationshipReflectionOutcome.INTERPRETATION else None,
                evidence_event_ids=ids, policy_version=POLICY, generated_at=float(index),
            ))
        batch = self.scanner.scan_next_batch()
        self.assertEqual(batch.scanned_jobs, 2)
        self.assertEqual(batch.candidates, ())

    def test_zero_candidate_episode_does_not_block_next_job(self) -> None:
        first, _ = self.episode((
            self.event("observation", "npc_01", ("player_a",)),
        ), created_at=1)
        second, ids = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
        ), created_at=2)
        empty = self.scanner.scan_next_batch(limit=1)
        self.assertEqual(empty.candidates, ())
        self.assertEqual(self.scanner.cursor, (1, first.job_id))
        found = self.scanner.scan_next_batch(limit=1)
        self.assertEqual(found.candidates[0].evidence_event_ids, ids)
        self.assertEqual(self.scanner.cursor, (2, second.job_id))

    def test_same_creation_time_uses_job_id_tie_break(self) -> None:
        first, _ = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
        ), created_at=5)
        second, _ = self.episode((
            self.event("dialogue_input", "player_b", ("npc_01",)),
        ), created_at=5)
        observed = []
        for _ in range(2):
            batch = self.scanner.scan_next_batch(limit=1)
            self.assertEqual(batch.scanned_jobs, 1)
            observed.append(self.scanner.cursor[1])
        self.assertEqual(observed, sorted((first.job_id, second.job_id)))
        self.assertTrue(self.scanner.scan_next_batch(limit=1).end_of_round)

    def test_late_success_is_discovered_after_explicit_round_reset(self) -> None:
        early, _ = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
        ), created_at=1, status="PENDING")
        late, _ = self.episode((
            self.event("dialogue_input", "player_b", ("npc_01",)),
        ), created_at=2)
        first_round = self.scanner.scan_next_batch()
        self.assertEqual(tuple(value.episode_id for value in first_round.candidates),
                         (late.episode_id,))
        self.assertTrue(first_round.end_of_round)
        with closing(self.connect()) as connection:
            with connection:
                connection.execute("UPDATE memory_jobs SET status='SUCCEEDED' WHERE job_id=?",
                                   (early.job_id,))
        self.assertEqual(self.scanner.scan_next_batch().candidates, ())
        self.scanner.begin_new_round()
        second_round = self.scanner.scan_next_batch()
        self.assertEqual(tuple(value.episode_id for value in second_round.candidates),
                         (early.episode_id, late.episode_id))

    def test_bad_job_reports_reason_and_does_not_block_next(self) -> None:
        bad, _ = self.episode((
            self.event("dialogue_input", "player_a", ("npc_01",)),
        ), created_at=1)
        good, _ = self.episode((
            self.event("dialogue_input", "player_b", ("npc_01",)),
        ), created_at=2)
        with closing(self.connect()) as connection:
            with connection:
                connection.execute("UPDATE memory_jobs SET input_hash=? WHERE job_id=?",
                                   ("f" * 64, bad.job_id))
        batch = self.scanner.scan_next_batch()
        self.assertEqual(batch.issues[0].job_id, bad.job_id)
        self.assertEqual(batch.issues[0].reason, "JOB_INPUT_HASH_MISMATCH")
        self.assertEqual(tuple(value.episode_id for value in batch.candidates), (good.episode_id,))

    def test_query_excludes_non_success_and_other_pipeline(self) -> None:
        self.episode((self.event("dialogue_input", "player_a", ("npc_01",)),),
                     created_at=1, status="PENDING")
        self.episode((self.event("dialogue_input", "player_a", ("npc_01",)),),
                     created_at=2, version="memory-extractor/1.0")
        eligible, _ = self.episode((
            self.event("dialogue_input", "player_b", ("npc_01",)),
        ), created_at=3)
        batch = self.scanner.scan_next_batch()
        self.assertEqual(batch.scanned_jobs, 1)
        self.assertEqual(batch.candidates[0].episode_id, eligible.episode_id)
