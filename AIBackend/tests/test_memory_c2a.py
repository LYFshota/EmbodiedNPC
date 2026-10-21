from __future__ import annotations

from contextlib import closing
from dataclasses import replace
from pathlib import Path
import sqlite3
import tempfile
import time
import unittest

from pydantic import ValidationError

from app.canonical_memory_repository import CanonicalMemoryFinalizationError, CanonicalMemoryRepository
from app.consolidation_state import ConsolidationStateRepository
from app.episode_models import MemoryJobPolicy
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_consolidation_admission import MemoryConsolidationAdmission, MemoryConsolidationAdmissionError
from app.memory_consolidation_context import MemoryContextResolver
from app.memory_consolidation_models import MemoryConsolidationResponse
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.sqlite_database import SQLiteDatabase
from app.semantic_memory_models import SemanticFactCandidate, locate_source_excerpt


class SemanticFactContractTests(unittest.TestCase):
    def candidate(self, **changes):
        values = dict(subject_handle="participant_1", fact="以前喜欢茶，现在不喜欢了。",
                      evidence_event_ids=["source"], source_excerpt="以前喜欢茶，现在不喜欢了",
                      confidence=.8)
        return SemanticFactCandidate.model_validate({**values, **changes})

    def test_open_contract_bounds_and_forbids_authority(self):
        self.assertEqual(self.candidate().fact, "以前喜欢茶，现在不喜欢了。")
        # Content is open; neither attributes nor a handwritten semantic parser
        # decide whether a claim is allowed. The model must preserve its meaning.
        self.assertEqual(self.candidate(fact="希望明年学习陶艺。").fact, "希望明年学习陶艺。")
        for changes in (
            {"subject_handle": "player_uid"}, {"fact": " "}, {"fact": "x" * 501},
            {"source_excerpt": "\n"}, {"source_excerpt": "x" * 1001},
            {"evidence_event_ids": []}, {"evidence_event_ids": ["source", "source"]},
            {"evidence_event_ids": [" "]}, {"evidence_event_ids": ["x"] * 65},
            {"confidence": float("nan")}, {"confidence": 1.1},
            {"observer_id": "npc"}, {"status": "ACTIVE"}, {"attribute_key": "likes"},
        ):
            with self.subTest(changes=changes), self.assertRaises(ValidationError):
                self.candidate(**changes)

    def test_excerpt_must_be_literal_and_in_cited_evidence_without_truth_claim(self):
        candidate = self.candidate()
        self.assertEqual(locate_source_excerpt(candidate,
            {"source": "玩家说：以前喜欢茶，现在不喜欢了。"}), "source")
        for evidence, error in (
            ({"other": candidate.source_excerpt}, "EVIDENCE_OUTSIDE_CONTEXT"),
            ({"source": None}, "SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE"),
            ({"source": "以前喜欢茶", "uncited": candidate.source_excerpt},
             "SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE"),
        ):
            with self.subTest(evidence=evidence), self.assertRaisesRegex(ValueError, error):
                locate_source_excerpt(candidate, evidence)
        # A false semantic rewrite can still quote real text. This helper must
        # not pretend to be a semantic correctness or independent truth check.
        wrong_rewrite = self.candidate(fact="现在最喜欢茶。")
        self.assertEqual(locate_source_excerpt(wrong_rewrite,
            {"source": candidate.source_excerpt}), "source")


class MemoryC2ATests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.database = SQLiteDatabase(Path(self.directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.admission = MemoryConsolidationAdmission()
        self.store = CanonicalMemoryRepository(self.database.connect)
        self.state = ConsolidationStateRepository(self.database.connect)
        self.counter = 0

    def assert_rejected(self, context, response, reason):
        batch = self.admission.evaluate(context, response)
        self.assertEqual(batch.memories, ())
        self.assertEqual([r.reason for r in batch.rejections], [reason])
        return batch

    def rows(self, query, args=()):
        with closing(self.database.connect()) as connection:
            return [dict(row) for row in connection.execute(query, args)]

    def work(self, *, observer="npc_01", player="player_01", uid="U10001", owner="merchant.001",
             occurred=10, provenance="SYSTEM_CONFIRMED", session="session_01", clock="unity_realtime",
             actor=None, structured=None, subject_identity=True, version=V2, text=None):
        self.counter += 1
        actor = actor or player
        target = observer if actor == player else player
        participants = []
        if subject_identity:
            participants.append(dict(entity_id=player, identity_namespace="player", identity_type="Player", persistent_id=uid))
        if owner:
            participants.append(dict(entity_id=observer, identity_namespace="npc", identity_type="Character", persistent_id=owner))
        draft = ExperienceDraft(
            schema_version="experience-draft/1.1", observer_id=observer,
            event_type="dialogue_input" if structured is None else "observation",
            source_type="dialogue" if structured is None else "observation",
            source_session_id=session, clock_domain=clock, source_event_id=f"source_{self.counter}",
            source_observation_ids=() if structured is None else (f"obs_{self.counter}",),
            actor_entity_id=actor, target_entity_ids=(target,), persistent_participants=participants,
            structured_payload={} if structured is None else {"perception_revision": 1, **structured},
            natural_language_payload=text or "我最喜欢的颜色是Blue。",
            occurred_at=occurred, observed_at=occurred, request_id=f"request_{self.counter}",
            conversation_id=f"conversation_{self.counter}", provenance=provenance,
        )
        result = self.ingestion.append(ExperienceAppendRequest(
            request_id=f"append_{self.counter}", trace_id=f"trace_{self.counter}", draft=draft,
        ))
        self.assertEqual(result.status, "inserted")
        event = self.ledger.get_by_event_id(result.event_id)
        episode = self.episodes.open_for_event(event, "episode-boundary/1.0")
        self.episodes.close_and_enqueue(episode.episode_id, "test_close", time.time(), version)
        job = self.jobs.claim_next(time.time())
        self.assertEqual(job.episode_id, episode.episode_id)
        return job, self.resolver.resolve(job) if version == V2 else None

    def response(self, context, *, value="Blue", attribute="favorite_color", episodic=False,
                 summary="Was asked to bring a blue cup and later received thanks.", handles=None):
        handle = next((h for h, identity in context.identity_by_handle.items()
                       if identity.identity_namespace == "player"), "participant_99")
        evidence = [event.event_id for event in context.evidence_events]
        if episodic:
            return dict(episodic_memories=[dict(summary=summary,
                subject_handles=[handle] if handles is None else handles,
                evidence_event_ids=evidence, importance=.6, confidence=.9)], semantic_facts=[])
        return dict(episodic_memories=[], semantic_facts=[dict(subject_handle=handle,
                    fact=f"{attribute}: {value}", source_excerpt=context.evidence_events[0].natural_language_payload,
                    evidence_event_ids=evidence, confidence=.9)])

    def finalize(self, job, context, response=None, **changes):
        batch = self.admission.evaluate(context, response if response is not None else self.response(context))
        args = dict(job_id=job.job_id, attempt_generation=job.attempt_generation,
                    expected_input_hash=job.input_hash, batch=batch, now=time.time())
        args.update(changes)
        return self.store.finalize_success(**args)

    def assert_empty_result(self, job):
        self.assertEqual(self.rows("SELECT * FROM memory_records"), [])
        self.assertEqual(self.jobs.get(job.job_id).status, "RUNNING")
        self.assertEqual(self.state.get(job.observer_id, V2), 0)

    def test_contract_forbids_authority_fields_and_bounds(self):
        _, context = self.work()
        response = self.response(context)
        forbidden = ["memory_id", "fact_key", "observer_id", "persistent_id", "episode_id", "scope",
                     "timestamps", "provenance", "status", "player_uid"]
        for key in forbidden:
            with self.subTest(key=key), self.assertRaises(ValidationError):
                MemoryConsolidationResponse.model_validate({**response, key: "injected"})
            changed = self.response(context)
            changed["semantic_facts"][0][key] = "injected"
            with self.assertRaises(ValidationError):
                MemoryConsolidationResponse.model_validate(changed)
        for changes in ({"confidence": float("nan")}, {"confidence": 1.01},
                        {"fact": "x" * 501}, {"evidence_event_ids": []}):
            changed = self.response(context)
            changed["semantic_facts"][0].update(changes)
            with self.assertRaises(ValidationError):
                self.admission.evaluate(context, changed)
        for kind in ("episodic_memories", "semantic_facts"):
            changed = self.response(context, episodic=kind == "episodic_memories")
            changed[kind] *= 9
            with self.assertRaises(ValidationError):
                self.admission.evaluate(context, changed)

    def test_evidence_outside_context_rejected(self):
        _, first = self.work()
        _, second = self.work()
        response = self.response(first)
        response["semantic_facts"][0]["evidence_event_ids"] = [second.evidence_events[0].event_id]
        self.assert_rejected(first, response, "EVIDENCE_OUTSIDE_CONTEXT")

    def test_fake_handle_and_duplicate_evidence_rejected(self):
        _, context = self.work()
        response = self.response(context)
        response["semantic_facts"][0]["subject_handle"] = "participant_99"
        self.assert_rejected(context, response, "UNKNOWN_SUBJECT_HANDLE")
        response = self.response(context)
        response["semantic_facts"][0]["evidence_event_ids"] *= 2
        with self.assertRaises(ValidationError):
            self.admission.evaluate(context, response)


    def test_subject_must_participate_in_every_cited_event(self):
        _, first = self.work(uid="U10001")
        _, second = self.work(player="player_02", uid="U10002")
        # A known handle elsewhere in an exact-context-shaped input cannot own this evidence.
        mapping = dict(second.identity_by_handle)
        mapping["participant_99"] = next(i for i in first.identity_by_handle.values() if i.persistent_id == "U10001")
        context = replace(second, identity_by_handle=mapping)
        response = self.response(second)
        response["semantic_facts"][0]["subject_handle"] = "participant_99"
        self.assert_rejected(context, response, "SUBJECT_OUTSIDE_CITED_EVIDENCE")

    def test_mixed_candidates_preserve_episodics_and_reject_unanchored_fact(self):
        job, context = self.work()
        first = self.response(context, episodic=True)["episodic_memories"][0]
        response = self.response(context)
        response["semantic_facts"][0]["source_excerpt"] = "not in evidence"
        response["episodic_memories"] = [first, {**first, "summary": "Received thanks for completed help."}]
        batch = self.admission.evaluate(context, response)
        self.assertEqual(len(batch.memories), 2)
        self.assertEqual([(r.candidate_kind, r.reason) for r in batch.rejections],
                         [("semantic_fact", "SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE")])
        self.finalize(job, context, response)
        self.assertEqual(self.rows("SELECT * FROM memory_semantic_facts"), [])


    def test_bad_candidate_does_not_discard_independent_valid_candidate(self):
        _, context = self.work()
        good = self.response(context, episodic=True)["episodic_memories"][0]
        for changes, reason in (({"subject_handles": ["participant_99"]}, "UNKNOWN_SUBJECT_HANDLE"),
                               ({"evidence_event_ids": ["fake_event"]}, "EVIDENCE_OUTSIDE_CONTEXT"),
                               ({"summary": "player_01 thanked me."}, "SUMMARY_IDENTIFIER_LEAKAGE"),
                               ({"subject_handles": good["subject_handles"] * 2}, "DUPLICATE_SUBJECT_HANDLE")):
            with self.subTest(reason=reason):
                batch = self.admission.evaluate(context, dict(
                    episodic_memories=[{**good, **changes}, good], semantic_facts=[]))
                self.assertEqual(batch.memories, self.admission.evaluate(context, dict(
                    episodic_memories=[good], semantic_facts=[])).memories)
                self.assertEqual([(r.candidate_index, r.reason) for r in batch.rejections], [(0, reason)])

    def test_all_rejected_finalizes_zero_result_and_is_idempotent(self):
        job, context = self.work()
        response = self.response(context)
        response["semantic_facts"][0]["source_excerpt"] = "missing excerpt"
        batch = self.assert_rejected(context, response, "SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE")
        outcome = self.finalize(job, context, response, batch=batch)
        self.assertTrue(outcome.applied)
        self.assertEqual(outcome.watermark, context.end_seq)
        self.assertEqual(self.jobs.get(job.job_id).status, "SUCCEEDED")
        self.assertEqual(self.rows("SELECT * FROM memory_records"), [])
        self.assertFalse(self.finalize(job, context, response).applied)

    def test_batch_integrity_still_fatal_before_candidate_filtering(self):
        job, context = self.work()
        response = self.response(context)
        for invalid in (replace(context, pipeline_version="unsupported"),
                        replace(context, end_seq=context.end_seq + 1),
                        replace(context, evidence_events=()),
                        replace(context, identity_by_handle={}), None):
            with self.subTest(context=type(invalid).__name__), self.assertRaises(MemoryConsolidationAdmissionError):
                self.admission.evaluate(invalid, response)
        self.assert_empty_result(job)

    def test_independent_sources_stay_separate_within_batch(self):
        _, first = self.work()
        _, second = self.work(occurred=20)
        context = replace(first, end_seq=second.end_seq,
                          evidence_events=first.evidence_events + second.evidence_events)
        a = self.response(first)["semantic_facts"][0]
        b = self.response(second)["semantic_facts"][0]
        batches = [self.admission.evaluate(context, dict(episodic_memories=[], semantic_facts=values))
                   for values in ([a, b, a], [b, a, a])]
        self.assertEqual(batches[0].memories, batches[1].memories)
        self.assertEqual(len(batches[0].memories), 2)


    def test_finalization_rechecks_filtered_memories_and_rejection_metadata(self):
        job, context = self.work()
        valid = self.admission.evaluate(context, self.response(context))
        response = self.response(context)
        response["semantic_facts"][0]["source_excerpt"] = "missing excerpt"
        filtered = self.admission.evaluate(context, response)
        for forged in (replace(filtered, memories=valid.memories),
                       replace(filtered, rejections=()),
                       replace(filtered, policy_version="memory-consolidation-admission/1.0")):
            with self.assertRaisesRegex(CanonicalMemoryFinalizationError, "ADMISSION_INTEGRITY_MISMATCH"):
                self.finalize(job, context, response, batch=forged)
            self.assert_empty_result(job)

    def test_runtime_entity_changes_keep_durable_fact_and_subject(self):
        a, first = self.work()
        self.finalize(a, first)
        b, second = self.work(observer="npc_71", player="player_99", occurred=20)
        before = self.admission.evaluate(first, self.response(first)).memories[0]
        after = self.admission.evaluate(second, self.response(second)).memories[0]
        self.assertEqual(before.subjects, after.subjects)
        self.assertNotEqual(before.memory_id, after.memory_id)
        self.finalize(b, second)
        self.assertEqual(len(self.rows("SELECT * FROM memory_records")), 2)
        self.assertEqual(len(self.rows("SELECT * FROM memory_evidence")), 2)

    def test_same_display_name_different_uid_isolated(self):
        memories = []
        for uid in ("U10001", "U10002"):
            job, context = self.work(uid=uid)
            memories.append(self.admission.evaluate(context, self.response(context)).memories[0])
            self.finalize(job, context)
        self.assertNotEqual(memories[0].memory_id, memories[1].memory_id)
        self.assertEqual({r["persistent_id"] for r in self.rows("SELECT * FROM memory_subjects")}, {"U10001", "U10002"})

    def test_episodic_identifier_leakage_rejected(self):
        _, context = self.work()
        for identifier in ("participant_1", "participant_999", "player_01", "npc_01", "U10001", "merchant.001", "ｐｌａｙｅｒ_01"):
            with self.subTest(identifier=identifier):
                self.assert_rejected(context, self.response(context, episodic=True, summary=identifier + " thanked me."),
                                     "SUMMARY_IDENTIFIER_LEAKAGE")

    def test_episodic_without_durable_owner_or_subject_preserves_evidence(self):
        job, context = self.work(owner=None, subject_identity=False)
        response = self.response(context, episodic=True, handles=[], summary="  Ａ cup\n was brought.  ")
        response["episodic_memories"] *= 2
        batch = self.admission.evaluate(context, response)
        self.assertEqual(len(batch.memories), 1)
        self.assertEqual(batch.memories[0].summary, "A cup was brought.")
        self.finalize(job, context, response)
        self.assertEqual(self.rows("SELECT * FROM memory_subjects"), [])
        record = self.rows("SELECT * FROM memory_records")[0]
        self.assertIsNone(record["persistent_id"])
        self.assertEqual((record["occurred_start"], record["occurred_end"]), (10, 10))

    def test_profile_requires_durable_owner_and_subject(self):
        for changes, reason in (({"owner": None}, "SEMANTIC_DURABLE_OWNER_REQUIRED"),
                                ({"subject_identity": False}, "UNKNOWN_SUBJECT_HANDLE")):
            _, context = self.work(**changes)
            self.assert_rejected(context, self.response(context), reason)

    def test_open_fact_text_and_literal_anchor(self):
        for fact in ("我想明年学习陶艺。", "以前喜欢茶，现在不喜欢了。", "只有周末有空时我才去攀岩。"):
            job, context = self.work(text=fact)
            response = self.response(context)
            response["semantic_facts"][0].update(fact=fact, source_excerpt=fact)
            self.finalize(job, context, response)
        records = self.rows("SELECT * FROM memory_records")
        self.assertEqual(len(records), 3)
        self.assertEqual({r["memory_type"] for r in records}, {"SEMANTIC_FACT"})
        self.assertEqual({r["schema_version"] for r in records}, {"memory-record/3.0"})
        self.assertEqual(self.rows("SELECT * FROM memory_profile_facts"), [])
        self.assertEqual({r["status"] for r in records}, {"ACTIVE"})


    def test_npc_speech_cannot_certify_other_subject(self):
        _, context = self.work(actor="npc_01", provenance="EXECUTED_BY_SELF")
        context = replace(context, evidence_events=tuple(e.model_copy(update={"event_type": "dialogue_response"})
                                                        for e in context.evidence_events))
        self.assert_rejected(context, self.response(context), "NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD")


    def test_structured_content_anchor_cannot_span_fields(self):
        _, context = self.work(structured={"detail": "书店每周三举办读书会。"})
        response = self.response(context)
        response["semantic_facts"][0].update(fact="书店每周三举办读书会。", source_excerpt="每周三举办读书会")
        self.assertEqual(len(self.admission.evaluate(context, response).memories), 1)
        response["semantic_facts"][0]["source_excerpt"] = "detail"
        self.assert_rejected(context, response, "SOURCE_EXCERPT_NOT_IN_CITED_EVIDENCE")


    def test_same_fact_in_new_episode_is_independent_and_job_retry_is_idempotent(self):
        for occurred in (10, 30):
            job, context = self.work(occurred=occurred)
            self.assertTrue(self.finalize(job, context).applied)
            self.assertFalse(self.finalize(job, context).applied)
        self.assertEqual(len(self.rows("SELECT * FROM memory_records")), 2)
        self.assertEqual(len(self.rows("SELECT * FROM memory_evidence")), 2)


    def test_time_changes_do_not_automatically_replace_old_facts(self):
        for fact, occurred in (("以前喜欢茶。", 10), ("现在不喜欢茶了。", 20)):
            job, context = self.work(occurred=occurred, text=fact)
            response = self.response(context)
            response["semantic_facts"][0].update(fact=fact, source_excerpt=fact)
            self.finalize(job, context, response)
        self.assertEqual([r["status"] for r in self.rows("SELECT * FROM memory_records")], ["ACTIVE", "ACTIVE"])
        self.assertTrue(all(r["supersedes_memory_id"] is None for r in self.rows("SELECT * FROM memory_semantic_facts")))




    def test_multi_value_allows_distinct_active_values(self):
        for value in ("tea", "coffee"):
            job, context = self.work()
            self.finalize(job, context, self.response(context, attribute="likes", value=value))
        self.assertEqual([r["status"] for r in self.rows("SELECT status FROM memory_records")], ["ACTIVE", "ACTIVE"])



    def test_backend_utc_is_observation_time_not_claim_validity(self):
        job, context = self.work(occurred=10, clock="backend_utc")
        self.finalize(job, context)
        row, = self.rows("SELECT * FROM memory_semantic_facts")
        self.assertEqual(row["temporal_precision"], "OBSERVED_UTC")
        self.assertIsNone(row["valid_from"])
        self.assertIsNone(row["valid_to"])




    def test_input_hash_mismatch_and_stale_attempt_have_no_mutation(self):
        job, context = self.work()
        for changes, reason in (({"expected_input_hash": "wrong"}, "JOB_INPUT_HASH_MISMATCH"),
                                ({"attempt_generation": job.attempt_generation + 1}, "STALE_ATTEMPT")):
            with self.assertRaisesRegex(CanonicalMemoryFinalizationError, reason):
                self.finalize(job, context, **changes)
            self.assert_empty_result(job)

    def test_forged_admitted_memory_is_not_trusted(self):
        job, context = self.work()
        batch = self.admission.evaluate(context, self.response(context))
        forged = replace(batch, memories=(replace(batch.memories[0], memory_id="injected"),))
        with self.assertRaisesRegex(CanonicalMemoryFinalizationError, "ADMISSION_INTEGRITY_MISMATCH"):
            self.finalize(job, context, batch=forged)
        self.assert_empty_result(job)

    def test_duplicate_finalize_including_empty_result_is_idempotent(self):
        for response in (None, {"episodic_memories": [], "semantic_facts": []}):
            job, context = self.work()
            first = self.finalize(job, context, response)
            snapshot = self.rows("SELECT * FROM memory_records")
            second = self.finalize(job, context, response)
            self.assertTrue(first.applied)
            self.assertFalse(second.applied)
            self.assertEqual(first.watermark, second.watermark)
            self.assertEqual(snapshot, self.rows("SELECT * FROM memory_records"))

    def test_failure_at_watermark_rolls_back_then_retry_commits_once(self):
        job, context = self.work()
        with closing(self.database.connect()) as connection:
            connection.execute("CREATE TRIGGER fail_c2a BEFORE INSERT ON memory_consolidation_state "
                               "BEGIN SELECT RAISE(ABORT,'forced_failure'); END")
            connection.commit()
        with self.assertRaisesRegex(sqlite3.IntegrityError, "forced_failure"):
            self.finalize(job, context)
        self.assert_empty_result(job)
        for table in ("memory_subjects", "memory_evidence", "memory_semantic_facts"):
            self.assertEqual(self.rows(f"SELECT * FROM {table}"), [])
        with closing(self.database.connect()) as connection:
            connection.execute("DROP TRIGGER fail_c2a")
            connection.commit()
        policy = MemoryJobPolicy(extractor_version=V2, stale_timeout_seconds=1)
        future = time.time() + 10
        self.assertEqual(len(self.jobs.recover_stale(future, policy)), 1)
        retry = self.jobs.claim_next(future + 10)
        with self.assertRaisesRegex(CanonicalMemoryFinalizationError, "STALE_ATTEMPT"):
            self.finalize(job, context)
        restarted = CanonicalMemoryRepository(self.database.connect)
        self.store = restarted
        self.assertTrue(self.finalize(retry, context, now=future + 11).applied)
        self.assertFalse(self.finalize(retry, context, now=future + 12).applied)
        self.assertEqual(len(self.rows("SELECT * FROM memory_records")), 1)

    def test_watermark_is_contiguous_observer_and_version_scoped(self):
        first, c1 = self.work()
        second, c2 = self.work()
        self.assertEqual(self.finalize(second, c2).watermark, 0)
        self.assertEqual(self.finalize(first, c1).watermark, 2)
        self.assertEqual(self.state.get("npc_01", "memory-extractor/1.0"), 0)
        self.assertEqual(self.state.get("npc_other", V2), 0)
        other, context = self.work(observer="npc_other")
        self.assertEqual(self.finalize(other, context).watermark, 1)
        self.assertEqual(self.state.get("npc_01", V2), 2)

    def test_legacy_job_rejected_without_legacy_memory_writes(self):
        legacy, _ = self.work(version="memory-extractor/1.0")
        job, context = self.work()
        batch = self.admission.evaluate(context, self.response(context))
        with self.assertRaisesRegex(CanonicalMemoryFinalizationError, "UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION"):
            self.store.finalize_success(job_id=legacy.job_id, attempt_generation=legacy.attempt_generation,
                expected_input_hash=legacy.input_hash, batch=batch, now=time.time())
        self.finalize(job, context)
        self.assertEqual(self.rows("SELECT name FROM sqlite_master WHERE name LIKE 'memory_v1_%'"), [])

    def test_migration_repeatable_and_evidence_schema_is_reference_only(self):
        job, context = self.work()
        self.assertEqual(self.jobs.get(job.job_id), job)
        self.finalize(job, context)
        before = self.rows("SELECT * FROM memory_records")
        self.database.migrate()
        self.database.migrate()
        self.assertEqual(before, self.rows("SELECT * FROM memory_records"))
        self.assertEqual({r["name"] for r in self.rows("PRAGMA table_info(memory_evidence)")},
                         {"memory_id", "experience_event_id", "experience_seq"})
        self.assertEqual({r["name"] for r in self.rows("PRAGMA table_info(memory_subjects)")},
                         {"memory_id", "identity_namespace", "identity_type", "persistent_id"})
        self.assertEqual(len(self.rows("SELECT * FROM schema_migrations WHERE version=12")), 1)
        self.assertEqual(self.rows("SELECT name FROM sqlite_master WHERE name='memory_v2_job_results'"), [])


if __name__ == "__main__":
    unittest.main()
