"""B1 integration tests on isolated real SQLite/Ledger/Episode data."""
import asyncio
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import closing
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch

from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_consolidation_context import MemoryContextResolver, TrustedPersistentIdentity
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as VERSION
from app.relationship_pair_state import PairState
from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from app.relationship_reflection_model import RelationshipReflectionModel, RelationshipReflectionModelResponse
from app.relationship_reflection_models import RelationshipReflectionRecord
from app.relationship_reflection_repository import PairUpdateRejected, RelationshipReflectionRepository
from app.relationship_reflection_scanner import RelationshipReflectionScanner
from app.relationship_reflection_worker import RelationshipReflectionWorker
from app.memory_job_repository import MemoryJobRepository
from app.sqlite_database import SQLiteDatabase

NPC = TrustedPersistentIdentity("npc", "Character", "npc-a")
PLAYER = TrustedPersistentIdentity("player", "Player", "player-a")


class RelationshipPairStateTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "b1.sqlite3"
        self.db = SQLiteDatabase(self.path)
        self.repo = RelationshipReflectionRepository(self.db.connect)
        self.ledger = ExperienceLedger(self.db.connect)
        self.episodes = EpisodeRepository(self.db.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.serial = 0

    def candidate(self, text="Please call me Alex.", *, npc=NPC, player=PLAYER,
                  npc_entity="npc_01", player_entity="player_01", npc_text="Understood."):
        self.serial += 1
        participants = [dict(entity_id=entity, identity_namespace=identity.identity_namespace,
            identity_type=identity.identity_type, persistent_id=identity.persistent_id)
            for entity, identity in ((npc_entity, npc), (player_entity, player))]
        episode = None
        for index, words in enumerate((text, npc_text)):
            draft = ExperienceDraft(schema_version="experience-draft/1.1", observer_id=npc_entity,
                event_type="dialogue_input" if index == 0 else "dialogue_response", source_type="dialogue",
                source_session_id=f"session_{self.serial}", clock_domain="backend_utc",
                source_event_id=f"source_{self.serial}_{index}", request_id=f"request_{self.serial}_{index}",
                conversation_id=f"conversation_{self.serial}",
                actor_entity_id=player_entity if index == 0 else npc_entity,
                target_entity_ids=(npc_entity,) if index == 0 else (player_entity,),
                persistent_participants=participants, natural_language_payload=words,
                occurred_at=self.serial, observed_at=self.serial,
                provenance="HEARD" if index == 0 else "EXECUTED_BY_SELF")
            result = ExperienceIngestionService(self.ledger).append(ExperienceAppendRequest(
                request_id=f"append_{self.serial}_{index}", trace_id=f"trace_{self.serial}_{index}", draft=draft))
            event = self.ledger.get_by_event_id(result.event_id)
            episode = (self.episodes.open_for_event(event, "episode-boundary/1.0") if episode is None
                       else self.episodes.append_event(episode.episode_id, event))
        _, job = self.episodes.close_and_enqueue(episode.episode_id, "test_end", time.time()+1, VERSION)
        with closing(self.db.connect()) as connection, connection:
            connection.execute("UPDATE memory_jobs SET status='SUCCEEDED' WHERE job_id=?", (job.job_id,))
        return RelationshipReflectionCandidateBuilder().build(self.resolver.resolve(job))[0]

    def response(self, text=None, *, target=None, operation="ADD", quote="Please call me Alex.",
                 kind="BOUNDARY", event="e0"):
        return RelationshipReflectionModelResponse.model_validate(dict(
            outcome="INTERPRETATION" if text else "NO_CHANGE",
            interpretation="A local social impression." if text else None,
            state_updates=[dict(operation=operation, target=target, kind=kind, text=text,
                                event=event, quote=quote)] if text else []))

    def wire_response(self, response):
        payload = response.model_dump(mode="json")
        updates = payload.pop("state_updates")
        payload["state_update"] = updates[0] if updates else None
        return json.dumps(payload)

    def record(self, candidate, response):
        return RelationshipReflectionRecord(reflection_id=RelationshipReflectionWorker.reflection_id_for(candidate),
            observer=candidate.observer, subject=candidate.subject, episode_id=candidate.episode_id,
            episode_input_hash=candidate.episode_input_hash, outcome=response.outcome,
            interpretation=response.interpretation, evidence_event_ids=candidate.evidence_event_ids,
            policy_version="relationship-reflection/2.0", generated_at=123)

    def commit(self, candidate, response, previous=None):
        previous = previous or self.repo.read_current_pair_state(candidate.observer, candidate.subject)
        return self.repo.finalize_pair_update(self.record(candidate, response), candidate, response, previous)

    def test_unknown_pair_has_no_invented_relationship(self):
        state = self.repo.read_current_pair_state(NPC, PLAYER)
        self.assertEqual((state.revision, state.items, state.interaction_evidence), (0, (), None))

    def test_two_by_two_identity_matrix_and_runtime_entity_changes(self):
        npc_b = replace(NPC, persistent_id="npc-b")
        player_b = replace(PLAYER, persistent_id="player-b")
        for npc in (NPC, npc_b):
            for player in (PLAYER, player_b):
                candidate = self.candidate(npc=npc, player=player)
                state = self.commit(candidate, self.response(f"Boundary for {npc.persistent_id}/{player.persistent_id}"))
                self.assertEqual(state.revision, 1)
        for npc in (NPC, npc_b):
            for player in (PLAYER, player_b):
                self.assertIn(f"{npc.persistent_id}/{player.persistent_id}",
                              self.repo.read_current_pair_state(npc, player).items[0].text)
        # New runtime EntityIds/session and the same durable pair continue revision 1.
        candidate = self.candidate("Hello again.", npc_entity="npc_reloaded", player_entity="player_reloaded")
        state = self.commit(candidate, self.response())
        self.assertEqual((state.revision, len(state.items)), (2, 1))
        self.assertEqual(state.interaction_evidence.event_id, candidate.evidence_event_ids[0])

    def test_correction_conflict_and_lineage_preserve_both_sides(self):
        first = self.commit(self.candidate(), self.response("Use Alex when speaking."))
        conflict = self.commit(self.candidate("Do not call me Alex at work."), self.response(
            "Avoid Alex at work; the scope of the earlier request is unclear.", operation="CONFLICT",
            target="s0", quote="Do not call me Alex at work."))
        item = conflict.items[0]
        self.assertEqual((item.kind, item.predecessor_id), ("CONFLICT", first.items[0].item_id))
        self.assertEqual(item.predecessor_text, first.items[0].text)
        self.assertEqual(len(item.evidence), 2)
        corrected = self.commit(self.candidate("To clarify: Alex only outside work."), self.response(
            "Use Alex only outside work.", operation="CORRECT", target="s0",
            quote="To clarify: Alex only outside work."))
        self.assertEqual(corrected.items[0].predecessor_id, item.item_id)
        self.assertEqual(len(corrected.items[0].evidence), 1)
        self.assertEqual(corrected.items[0].change_type, "CORRECT")
        self.assertEqual(corrected.items[0].kind, "BOUNDARY")
        self.assertIsNotNone(self.repo.get(first.reflection_id))

    def test_no_change_survives_more_than_twenty_episodes(self):
        first = self.commit(self.candidate(), self.response("Use Alex when speaking."))
        for _ in range(21):
            state = self.commit(self.candidate("Hello."), self.response())
            self.assertEqual(state.items, first.items)
            self.assertFalse(state.changed)
        self.assertEqual(state.revision, 22)
        self.assertIsNotNone(state.interaction_evidence)

    def test_duplicate_delivery_is_rejected_without_revision_growth(self):
        candidate = self.candidate()
        response = self.response("Use Alex.")
        self.commit(candidate, response)
        with self.assertRaises(sqlite3.IntegrityError):
            self.commit(candidate, response)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).revision, 1)

    def test_compare_and_swap_discards_stale_writer(self):
        a, b = self.candidate(), self.candidate("Please be formal.")
        base = self.repo.read_current_pair_state(NPC, PLAYER)
        self.commit(a, self.response("Use Alex."), base)
        with self.assertRaisesRegex(PairUpdateRejected, "BASE_CHANGED"):
            self.commit(b, self.response("Be formal.", quote="Please be formal."), base)
        self.assertIsNone(self.repo.get(self.record(b, self.response()).reflection_id))
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).revision, 1)

    def test_late_episode_is_processed_without_rolling_back_current_state(self):
        old = self.candidate("Please be formal.")
        new = self.candidate("Please call me Alex.")
        first = self.commit(new, self.response("Use Alex."))
        late = self.commit(old, self.response("Be formal.", quote="Please be formal."))
        self.assertEqual(late.items, first.items)
        self.assertFalse(late.changed)
        self.assertEqual(late.watermark, first.watermark)
        self.assertTrue(late.late_episode_skipped)
        self.assertTrue(self.repo.exists_for_episode_pair(NPC, PLAYER, old.episode_id, "relationship-reflection/2.0"))

    def test_forged_handle_quote_npc_narration_and_pair_fail_closed(self):
        candidate = self.candidate(npc_text="We are best friends.")
        for response in (
            self.response("Close friends.", quote="We are best friends.", event="e1"),
            self.response("Close friends.", quote="unseen claim"),
            self.response("Close friends.", event="e9"),
            self.response("Change.", operation="CORRECT", target="s9"),
        ):
            with self.subTest(response=response), self.assertRaises(PairUpdateRejected):
                self.commit(candidate, response)
        forged = replace(candidate, subject=replace(PLAYER, persistent_id="someone-else"))
        with self.assertRaisesRegex(PairUpdateRejected, "CANDIDATE_SOURCE_CHANGED"):
            self.commit(forged, self.response("Close friends."))
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).revision, 0)

    def test_revalidate_source_removes_entire_conflict_and_reports_limited_coverage(self):
        self.commit(self.candidate(), self.response("Use Alex."))
        state = self.commit(self.candidate("Do not call me Alex."), self.response(
            "Address is disputed.", operation="CONFLICT", target="s0", quote="Do not call me Alex."))
        original = self.repo._valid_source
        missing = state.items[0].evidence[0].event_id
        # Simulate a source no longer admitted without violating the immutable Ledger.
        with patch.object(self.repo, "_valid_source", side_effect=lambda c, r, o, s:
                          r.event_id != missing and original(c, r, o, s)):
            read = self.repo.read_current_pair_state(NPC, PLAYER)
        self.assertEqual((read.items, read.coverage), ((), "SOURCE_UNAVAILABLE"))
        self.assertEqual(read.revision, state.revision)

    def fill_capacity(self):
        for words in ("Please call me Alex.", "Do not discuss my family.",
                      "No jokes at work.", "Please avoid health topics."):
            state = self.commit(self.candidate(words), self.response(words, quote=words))
        return state

    def test_capacity_three_to_four_keeps_normal_add(self):
        for index in range(3):
            self.commit(self.candidate(), self.response(f"Boundary {index}."))
        state = self.commit(self.candidate(), self.response("A fourth boundary."))
        self.assertEqual(len(state.items), 4)
        self.assertTrue(state.changed)
        self.assertIsNone(state.capacity_limited)

    def test_full_state_retains_unapplied_add_as_explicit_capacity_result(self):
        previous = self.fill_capacity()
        words = "Do not ask about my finances."
        candidate = self.candidate(words)
        response = self.response("Do not ask about finances.", quote=words)
        state = self.commit(candidate, response)
        self.assertEqual(state.items, previous.items)
        self.assertFalse(state.changed)
        self.assertEqual(state.capacity_limited.text, response.state_updates[0].text)
        self.assertEqual(state.capacity_limited.evidence[0].quote, words)
        record = self.repo.get(state.reflection_id)
        self.assertEqual(record.outcome.value, "INTERPRETATION")
        self.assertEqual(record.interpretation, response.interpretation)
        self.assertTrue(self.repo.exists_for_episode_pair(NPC, PLAYER, candidate.episode_id,
                                                       "relationship-reflection/2.0"))
        from app.relationship_summary_reader import RelationshipSummaryReader
        projected = RelationshipSummaryReader(self.repo).get(NPC, PLAYER)
        self.assertEqual(tuple(item.text for item in projected.current_state.items),
                         tuple(item.text for item in previous.items))
        self.assertNotIn("finances", projected.model_dump_json())
        self.assertEqual(self.repo.read_current_pair_state(
            NPC, replace(PLAYER, persistent_id="other-player")).revision, 0)

    def test_full_state_allows_evidenced_correction_and_conflict_without_eviction(self):
        previous = self.fill_capacity()
        words = "Correction: only call me Alex outside work."
        corrected = self.commit(self.candidate(words), self.response("Use Alex only outside work.",
            operation="CORRECT", target="s0", quote=words))
        self.assertEqual(corrected.items[1:], previous.items[1:])
        self.assertEqual(corrected.items[0].predecessor_id, previous.items[0].item_id)
        self.assertIsNone(corrected.capacity_limited)
        words = "Sometimes call me Alex at work."
        conflicted = self.commit(self.candidate(words), self.response("Work naming scope is disputed.",
            operation="CONFLICT", target="s0", quote=words))
        self.assertEqual(conflicted.items[1:], previous.items[1:])
        self.assertEqual(len(conflicted.items[0].evidence), 2)
        self.assertEqual(conflicted.items[0].predecessor_text, corrected.items[0].text)
        self.assertEqual(conflicted.items[0].kind, "CONFLICT")
        self.assertIsNone(conflicted.capacity_limited)

    def test_full_state_duplicate_and_no_change_do_not_create_capacity_result(self):
        previous = self.fill_capacity()
        repeated = self.commit(self.candidate(), self.response("Please call me Alex."))
        self.assertEqual(repeated.items, previous.items)
        self.assertFalse(repeated.changed)
        self.assertIsNone(repeated.capacity_limited)
        unchanged = self.commit(self.candidate("See you."), self.response())
        self.assertEqual(unchanged.items, previous.items)
        self.assertIsNone(unchanged.capacity_limited)

    def test_capacity_result_cannot_bypass_source_handle_or_cas_checks(self):
        previous = self.fill_capacity()
        candidate = self.candidate(npc_text="We are best friends.")
        for response in (self.response("Friends.", event="e1", quote="We are best friends."),
                         self.response("Friends.", event="e9"),
                         self.response("Friends.", quote="Not in the source."),
                         self.response("Friends.", operation="CORRECT", target="s9")):
            with self.subTest(response=response), self.assertRaises(PairUpdateRejected):
                self.commit(candidate, response)
            self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER), previous)
        forged = replace(candidate, subject=replace(PLAYER, persistent_id="another-player"))
        with self.assertRaisesRegex(PairUpdateRejected, "CANDIDATE_SOURCE_CHANGED"):
            self.commit(forged, self.response("Friends."))
        self.commit(self.candidate("See you."), self.response())
        with self.assertRaisesRegex(PairUpdateRejected, "BASE_CHANGED"):
            self.commit(candidate, self.response("New boundary."), previous)
        self.assertIsNone(self.repo.get(self.record(candidate, self.response()).reflection_id))

    def test_capacity_result_rollback_is_atomic_and_duplicate_submission_is_rejected(self):
        previous = self.fill_capacity()
        candidate = self.candidate()
        response = self.response("A new boundary.")
        with closing(self.db.connect()) as connection, connection:
            connection.execute("CREATE TRIGGER reject_capacity BEFORE INSERT ON relationship_reflections "
                "WHEN json_extract(NEW.pair_state_json,'$.capacity_limited') IS NOT NULL "
                "BEGIN SELECT RAISE(ABORT,'INJECTED_CAPACITY_FAILURE'); END")
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INJECTED_CAPACITY_FAILURE"):
            self.commit(candidate, response)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER), previous)
        with closing(self.db.connect()) as connection, connection:
            connection.execute("DROP TRIGGER reject_capacity")
        state = self.commit(candidate, response)
        with self.assertRaises(sqlite3.IntegrityError):
            self.commit(candidate, response)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER), state)

    def test_full_worker_diagnostic_deduplicates_new_round_and_backend_restart(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor
        previous = self.fill_capacity()
        candidate = self.candidate("Please avoid my finances.")
        response = self.response("Avoid finances.", quote="Please avoid my finances.")
        provider = FakeProvider([self.wire_response(response)])
        def make_worker():
            repository = RelationshipReflectionRepository(self.db.connect)
            scanner = RelationshipReflectionScanner(MemoryJobRepository(self.db.connect), self.resolver,
                RelationshipReflectionCandidateBuilder(), repository)
            return RelationshipReflectionWorker(scanner, RelationshipReflectionModel(provider, RecordingExecutor()), repository)
        async def drain(worker):
            batches = []
            while True:
                batch = await worker.process_batch()
                batches.append(batch)
                if batch.end_of_round:
                    return batches
        worker = make_worker()
        batches = asyncio.run(drain(worker))
        self.assertEqual(sum(b.capacity_limited for b in batches), 1)
        self.assertEqual(sum(b.persisted for b in batches), 0)
        self.assertEqual([i.reason for b in batches for i in b.issues], ["RELATIONSHIP_STATE_CAPACITY_LIMITED"])
        worker.scanner.begin_new_round()
        self.assertFalse(any(b.issues for b in asyncio.run(drain(worker))))
        self.assertFalse(any(b.issues for b in asyncio.run(drain(make_worker()))))
        self.assertEqual(len(provider.requests), 1)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).items, previous.items)
        # A separate process sees the durable Episode result and never needs a model.
        script = ("import sys; from app.sqlite_database import SQLiteDatabase; "
            "from app.relationship_reflection_repository import RelationshipReflectionRepository; "
            "from app.memory_consolidation_context import TrustedPersistentIdentity as I; "
            "r=RelationshipReflectionRepository(SQLiteDatabase(sys.argv[1]).connect); "
            "print(r.exists_for_episode_pair(I('npc','Character','npc-a'),I('player','Player','player-a'),sys.argv[2],'relationship-reflection/2.0')); "
            "print(r.read_current_pair_state(I('npc','Character','npc-a'),I('player','Player','player-a')).capacity_limited.text)")
        result = subprocess.run([sys.executable, "-B", "-c", script, str(self.path), candidate.episode_id],
            check=True, text=True, capture_output=True, env=os.environ.copy())
        self.assertEqual(result.stdout.splitlines(), ["True", "Avoid finances."])
        visible = provider.requests[0].messages[1].content
        self.assertIn('"limit":4,"used":4,"remaining":0', visible)

    def test_full_state_late_episode_remains_late_not_a_capacity_rejection(self):
        old = self.candidate("An old greeting.")
        previous = self.fill_capacity()
        late = self.commit(old, self.response("An old reading.", quote="An old greeting."))
        self.assertEqual(late.items, previous.items)
        self.assertTrue(late.late_episode_skipped)
        self.assertIsNone(late.capacity_limited)

    def test_restart_in_new_process_reads_same_pair_and_isolates_other_uid(self):
        state = self.commit(self.candidate(), self.response("Use Alex."))
        script = ("import sys; from app.sqlite_database import SQLiteDatabase; "
            "from app.relationship_reflection_repository import RelationshipReflectionRepository; "
            "from app.memory_consolidation_context import TrustedPersistentIdentity as I; "
            "r=RelationshipReflectionRepository(SQLiteDatabase(sys.argv[1]).connect); "
            "print(r.read_current_pair_state(I('npc','Character','npc-a'),I('player','Player','player-a')).model_dump_json()); "
            "print(r.read_current_pair_state(I('npc','Character','npc-a'),I('player','Player','other')).revision)")
        result = subprocess.run([sys.executable, "-c", script, str(self.path)], check=True,
                                text=True, capture_output=True, env=os.environ.copy())
        lines = result.stdout.splitlines()
        self.assertEqual(PairState.model_validate_json(lines[0]), state)
        self.assertEqual(lines[1], "0")

    def test_old_schema_data_migrates_without_reprocessing_legacy_policy(self):
        # Build an actual v19 predecessor, then open it with the production migration chain.
        old_path = self.path.parent / "old.sqlite3"
        with patch("app.sqlite_database.migrate_relationship_state"), patch("app.sqlite_database.LATEST_SCHEMA_VERSION", 19):
            old_db = SQLiteDatabase(old_path)
        with closing(old_db.connect()) as connection:
            self.assertNotIn("state_revision", [r[1] for r in connection.execute("PRAGMA table_info(relationship_reflections)")])
        self.db = old_db
        self.repo = RelationshipReflectionRepository(old_db.connect)
        self.ledger = ExperienceLedger(old_db.connect)
        self.episodes = EpisodeRepository(old_db.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        candidate = self.candidate()
        record = self.record(candidate, self.response()).model_copy(update={"policy_version":"relationship-reflection/1.0"})
        self.repo.insert(record)
        with closing(old_db.connect()) as connection:
            old_row = tuple(connection.execute("SELECT * FROM relationship_reflections").fetchone())
        self.db = SQLiteDatabase(old_path)
        self.db.migrate()
        self.repo = RelationshipReflectionRepository(self.db.connect)
        with closing(self.db.connect()) as connection:
            migrated_row = tuple(connection.execute("SELECT * FROM relationship_reflections").fetchone())
            self.assertEqual(migrated_row[:len(old_row)], old_row)
            self.assertEqual(migrated_row[len(old_row):], (None, None, None))
            self.assertEqual(connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], 20)
        scanner = RelationshipReflectionScanner(MemoryJobRepository(self.db.connect), self.resolver,
                                               RelationshipReflectionCandidateBuilder(), self.repo)
        self.assertEqual(scanner.scan_next_batch().candidates, ())
        legacy = self.repo.read_current_pair_state(NPC, PLAYER)
        self.assertEqual(legacy.coverage, "LEGACY_PARTIAL")
        self.assertIsNotNone(legacy.interaction_evidence)
        self.assertEqual(legacy.items, ())
        state = self.commit(self.candidate(), self.response("Use Alex."))
        self.assertEqual(state.coverage, "LEGACY_PARTIAL")
        self.assertEqual(self.repo.get(record.reflection_id), record)

    def test_snapshot_is_immutable(self):
        state = self.commit(self.candidate(), self.response("Use Alex."))
        with closing(self.db.connect()) as connection:
            for operation in ("UPDATE relationship_reflections SET pair_state_json='{}'", "DELETE FROM relationship_reflections"):
                with self.assertRaisesRegex(sqlite3.IntegrityError, "SNAPSHOT_IMMUTABLE"):
                    connection.execute(operation + " WHERE reflection_id=?", (state.reflection_id,))

    def test_worker_uses_one_call_and_passes_previous_state_separately(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner
        self.commit(self.candidate(), self.response("Use Alex."))
        candidate = self.candidate("Please be formal at work.")
        response = self.response("Use formal address at work.", operation="CORRECT", target="s0",
                                 quote="Please be formal at work.")
        provider = FakeProvider([self.wire_response(response)])
        executor = RecordingExecutor()
        worker = RelationshipReflectionWorker(StubScanner(candidate), RelationshipReflectionModel(provider, executor), self.repo)
        batch = asyncio.run(worker.process_batch())
        self.assertEqual((batch.persisted, batch.issues, executor.background_calls), (1, (), 1))
        visible = provider.requests[0].messages[1].content
        self.assertIn("PREVIOUS DERIVED STATE", visible)
        self.assertIn("Use Alex.", visible)
        self.assertIn("Please call me Alex.", visible)
        self.assertIn('"limit":4,"used":1,"remaining":3', visible)
        self.assertNotIn(PLAYER.persistent_id, visible)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).items[0].text, "Use formal address at work.")

    def test_single_object_wire_preserves_add_correct_conflict_and_old_snapshot(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner
        previous = self.commit(self.candidate(), self.response("Use Alex."))
        old_json = previous.model_dump_json()
        cases = (
            ("Please be formal at work.", "Use formal address at work.", "CORRECT", "s0"),
            ("Sometimes call me Alex at work.", "Work naming scope is disputed.", "CONFLICT", "s0"),
            ("Do not discuss my family.", "Do not discuss family.", "ADD", None),
        )
        for words, text, operation, target in cases:
            candidate = self.candidate(words)
            response = self.response(text, operation=operation, target=target, quote=words)
            provider = FakeProvider([self.wire_response(response)])
            model = RelationshipReflectionModel(provider, RecordingExecutor())
            translated = asyncio.run(model.interpret(candidate, previous))
            self.assertEqual(translated, response)
            worker = RelationshipReflectionWorker(StubScanner(candidate),
                RelationshipReflectionModel(FakeProvider([self.wire_response(response)]), RecordingExecutor()), self.repo)
            batch = asyncio.run(worker.process_batch())
            self.assertEqual((batch.persisted, batch.issues), (1, ()))
            previous = self.repo.read_current_pair_state(NPC, PLAYER)
        self.assertEqual(len(previous.items), 2)
        self.assertEqual(previous.items[0].kind, "CONFLICT")
        self.assertEqual(len(previous.items[0].evidence), 2)
        self.assertEqual(PairState.model_validate_json(old_json).items[0].text, "Use Alex.")

    def test_single_object_wire_still_rejects_invalid_evidence_and_pair(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner
        candidate = self.candidate(npc_text="We are best friends.")
        for response in (self.response("Friends.", event="e1", quote="We are best friends."),
                         self.response("Friends.", event="e9"),
                         self.response("Friends.", quote="Not in source."),
                         self.response("Friends.", operation="CORRECT", target="s9")):
            worker = RelationshipReflectionWorker(StubScanner(candidate),
                RelationshipReflectionModel(FakeProvider([self.wire_response(response)]), RecordingExecutor()), self.repo)
            with self.assertLogs("app.relationship_reflection_worker", level="WARNING"):
                batch = asyncio.run(worker.process_batch())
            self.assertEqual(batch.persisted, 0)
            self.assertEqual(len(batch.issues), 1)
            self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).revision, 0)
        forged = replace(candidate, subject=replace(PLAYER, persistent_id="another-player"))
        worker = RelationshipReflectionWorker(StubScanner(forged),
            RelationshipReflectionModel(FakeProvider([self.wire_response(self.response("A boundary."))]),
                                        RecordingExecutor()), self.repo)
        with self.assertLogs("app.relationship_reflection_worker", level="WARNING"):
            batch = asyncio.run(worker.process_batch())
        self.assertEqual(batch.persisted, 0)
        self.assertEqual(batch.issues[0].reason, "RELATIONSHIP_CANDIDATE_SOURCE_CHANGED")
        self.assertEqual(self.repo.read_current_pair_state(NPC, forged.subject).revision, 0)

    def test_no_change_cannot_carry_state_delta(self):
        from pydantic import ValidationError
        payload = self.response("Invented change.").model_dump()
        payload.update(outcome="NO_CHANGE", interpretation=None)
        with self.assertRaises(ValidationError):
            RelationshipReflectionModelResponse.model_validate(payload)

    def test_explicit_corrections_can_continue_beyond_eight_revisions(self):
        self.commit(self.candidate(), self.response("Use Alex."))
        for index in range(12):
            words = f"Correction: please use name {index}."
            state = self.commit(self.candidate(words), self.response(f"Use name {index}.",
                operation="CORRECT", target="s0", quote=words))
            self.assertEqual(len(state.items[0].evidence), 1)
        self.assertEqual(state.revision, 13)
        self.assertEqual(state.items[0].text, "Use name 11.")

    def test_same_item_is_not_counted_as_relationship_change(self):
        first = self.commit(self.candidate(), self.response("Use Alex."))
        repeated = self.commit(self.candidate(), self.response("Use Alex.", operation="CORRECT", target="s0"))
        self.assertFalse(repeated.changed)
        self.assertEqual(first.items, repeated.items)

    def test_long_real_player_utterance_still_proves_direct_interaction(self):
        text = "A whole long statement. " * 20
        candidate = self.candidate(text)
        state = self.commit(candidate, self.response())
        self.assertEqual(state.interaction_evidence.quote, text)
        self.assertFalse(state.changed)

    def test_background_failure_or_preemption_retains_previous_state(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner
        from app.providers import ProviderError
        from app.local_model_scheduler import PreemptedByForeground
        state = self.commit(self.candidate(), self.response("Use Alex."))
        candidate = self.candidate("Please be formal at work.")
        for failure in (ProviderError("unavailable"), PreemptedByForeground()):
            worker = RelationshipReflectionWorker(StubScanner(candidate),
                RelationshipReflectionModel(FakeProvider([failure]), RecordingExecutor()), self.repo)
            batch = asyncio.run(worker.process_batch())
            self.assertEqual(batch.persisted, 0)
            self.assertEqual(len(batch.issues), 1)
            self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER), state)

    def test_short_chinese_delta_fits_unchanged_background_output_reserve(self):
        from app.context_token_estimator import ContextTokenEstimator
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor
        response = self.response("工作时保持正式称呼。", operation="CORRECT", target="s0",
                                 quote="上班时请用正式称呼。")
        response = response.model_copy(update={"interpretation":"玩家明确限定工作时的称呼边界。"})
        model = RelationshipReflectionModel(FakeProvider([]), RecordingExecutor())
        self.assertEqual(model.policy.response_reserve, 256)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(response.model_dump_json()), 256)

    def test_schema20_failure_rolls_back_columns_index_triggers_and_can_retry(self):
        path = self.path.parent / "rollback.sqlite3"
        with patch("app.sqlite_database.migrate_relationship_state"):
            predecessor = SQLiteDatabase(path)
        with closing(predecessor.connect()) as connection, connection:
            before = [tuple(row) for row in connection.execute("SELECT type,name,sql FROM sqlite_master ORDER BY type,name")]
            connection.execute("CREATE TRIGGER reject_b1 BEFORE INSERT ON schema_migrations "
                "WHEN NEW.version=20 BEGIN SELECT RAISE(ABORT,'INJECTED_B1_FAILURE'); END")
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INJECTED_B1_FAILURE"):
            SQLiteDatabase(path)
        with closing(predecessor.connect()) as connection:
            self.assertEqual(connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], 19)
            self.assertEqual([tuple(row) for row in connection.execute(
                "SELECT type,name,sql FROM sqlite_master WHERE name!='reject_b1' ORDER BY type,name")], before)
            connection.execute("DROP TRIGGER reject_b1")
            connection.commit()
        SQLiteDatabase(path)
        with closing(predecessor.connect()) as connection:
            self.assertEqual(connection.execute("PRAGMA integrity_check").fetchone()[0], "ok")
            self.assertEqual(connection.execute("PRAGMA foreign_key_check").fetchall(), [])
            self.assertEqual(connection.execute("SELECT MAX(version) FROM schema_migrations").fetchone()[0], 20)

    def test_real_scanner_worker_and_restarted_scanner_do_not_repeat_inference(self):
        from test_relationship_reflection_execution import FakeProvider, RecordingExecutor
        candidate = self.candidate()
        provider = FakeProvider([self.wire_response(self.response("Use Alex."))])
        def make_worker():
            repository = RelationshipReflectionRepository(self.db.connect)
            scanner = RelationshipReflectionScanner(MemoryJobRepository(self.db.connect), self.resolver,
                RelationshipReflectionCandidateBuilder(), repository)
            return RelationshipReflectionWorker(scanner, RelationshipReflectionModel(provider, RecordingExecutor()), repository)
        first = asyncio.run(make_worker().process_batch())
        self.assertEqual((first.persisted, first.issues), (1, ()))
        restarted = asyncio.run(make_worker().process_batch())
        self.assertEqual((restarted.persisted, restarted.scanned_jobs), (0, 1))
        self.assertEqual(len(provider.requests), 1)
        self.assertEqual(self.repo.read_current_pair_state(NPC, PLAYER).items[0].text, "Use Alex.")
