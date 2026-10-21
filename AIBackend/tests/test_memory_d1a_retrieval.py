from __future__ import annotations

from contextlib import closing
import unittest

from pydantic import ValidationError

from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests import test_memory_c2a as c2a
from tests.test_memory_d0_subjects import request


OWNER = TrustedPersistentIdentity("npc", "Character", "merchant.001")
A = TrustedPersistentIdentity("player", "Player", "U10001")
B = TrustedPersistentIdentity("player", "Player", "U10002")


class CanonicalRetrievalTests(unittest.TestCase):
    def setUp(self):
        # Use the real schema and admitted C2 writes, not a reduced mock schema.
        self.fixture = c2a.MemoryC2ATests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.statements = []

        def connect():
            db = self.fixture.database.connect()
            db.set_trace_callback(self.statements.append)
            return db

        self.service = CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(connect))

    def add(self, summary="玩家请求取来杯子，店员完成交付后玩家表示感谢。", *, profile=None,
            attribute="favorite_color", fact=None, **kwargs):
        job, context = self.fixture.work(**({"text": fact} if fact else {}), **kwargs)
        response = self.fixture.response(context, episodic=profile is None,
            summary=summary, value=profile, attribute=attribute)
        if fact is not None:
            response = self.fixture.response(context)
            response["semantic_facts"][0].update(fact=fact, source_excerpt=fact)
        batch = self.fixture.admission.evaluate(context, response)
        self.assertEqual(len(batch.memories), 1, batch.rejections)
        self.fixture.finalize(job, context, response)
        return batch.memories[0].memory_id

    def read(self, query="杯子", **kwargs):
        return self.service.retrieve(observer=kwargs.pop("observer", OWNER), semantic_query=query, **kwargs)

    def test_open_semantic_and_episodic_scope_and_lifecycle(self):
        fact = "以前喜欢茶，现在不喜欢茶了。"
        wanted = self.add(fact=fact)
        episodic = self.add("玩家曾请求取来茶杯，店员完成交付。")
        self.add(fact=fact, uid=B.persistent_id)
        self.add(fact=fact, observer="npc_other", owner="other.private")
        result = self.read("喜欢 茶杯", subject=A)
        self.assertEqual({i.memory_id for i in result.items}, {wanted, episodic})
        self.assertEqual({i.memory_type for i in result.items}, {"EPISODIC", "SEMANTIC_FACT"})
        item = next(i for i in result.items if i.memory_id == wanted)
        self.assertEqual((item.summary, item.source_excerpt), (fact, fact))
        self.assertTrue(item.not_current_world_state and item.not_action_authorization and item.not_instruction)
        # Exercise read lifecycle independently of future M3-B maintenance.
        with closing(self.fixture.database.connect()) as db:
            db.execute("UPDATE memory_records SET status='SUPERSEDED' WHERE memory_id=?", (wanted,))
            db.commit()
        self.assertEqual([i.memory_id for i in self.read("喜欢 茶杯", subject=A).items], [episodic])
        history = self.read("喜欢 茶杯", subject=A, filters=CanonicalReadFilter(lifecycle="ALL_HISTORY"))
        self.assertEqual({i.memory_id for i in history.items}, {wanted, episodic})

    def test_owner_subject_and_type_are_intersected_by_durable_identity(self):
        wanted = self.add()
        self.add(uid=B.persistent_id)
        self.add(observer="npc_02", owner="regular.002")
        self.add(profile="blue")
        result = self.read(subject=A, filters=CanonicalReadFilter(memory_type="EPISODIC"))
        self.assertEqual([i.memory_id for i in result.items], [wanted])
        self.assertEqual(result.eligible_count, 1)

    def test_open_subject_returns_only_owner_history_with_distinct_subject_refs(self):
        first = self.add()
        second = self.add(uid=B.persistent_id)
        self.add(owner="regular.002", observer="npc_02")
        result = self.read(subject=None)
        self.assertEqual({i.memory_id for i in result.items}, {first, second})
        self.assertEqual(len({i.subjects[0].subject_ref for i in result.items}), 2)
        self.assertTrue(all(i.subjects[0].ref_scope == "EVIDENCE_LOCAL" for i in result.items))
        self.assertEqual({result.resolve_evidence_subject(i.subjects[0].subject_ref)
                          for i in result.items}, {A, B})
        self.assertIsNone(result.resolve_evidence_subject("invented"))
        self.assertNotIn("_subject_identities", result.model_dump_json())

    def test_player_a_b_queries_do_not_cross(self):
        a = self.add()
        b = self.add(uid=B.persistent_id)
        self.assertEqual([i.memory_id for i in self.read(subject=A).items], [a])
        self.assertEqual([i.memory_id for i in self.read(subject=B).items], [b])

    def test_runtime_observer_and_player_changes_preserve_durable_scope(self):
        first = self.add()
        second = self.add(observer="npc_respawn", player="player_respawn")
        self.assertEqual({i.memory_id for i in self.read(subject=A).items}, {first, second})

    def test_profile_attribute_exact_does_not_require_text_overlap(self):
        target = self.add(profile="blue")
        self.add(profile="teacher", attribute="occupation")
        result = self.read("blue", subject=A,
            filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"))
        self.assertEqual([i.memory_id for i in result.items], [target])
        self.assertEqual(result.items[0].summary, "favorite_color: blue")

    def test_episodic_partial_lexical_overlap_without_concepts(self):
        target = self.add()
        self.add("客人在窗边安静阅读报纸。")
        result = self.read("以前取来杯子的事情", filters=CanonicalReadFilter(memory_type="EPISODIC"))
        self.assertEqual([i.memory_id for i in result.items], [target])
        # Pure lexical is not an embedding or a paraphrase engine.
        self.assertEqual(self.read("galactic telescope").status, "NO_MEMORY_FOUND")

    def test_relevance_precedes_candidate_cap_and_sql_is_parameterized(self):
        target = self.add("Rare telescope observation")
        for index in range(6):
            self.add(f"Ordinary coffee order number {index}")
        result = self.read("telescope", budget=CanonicalRecallBudget(candidate_limit=1))
        self.assertEqual([i.memory_id for i in result.items], [target])
        self.assertEqual(result.eligible_count, 7)
        self.assertEqual(self.read("' OR 1=1 --", subject=B).status, "NO_MEMORY_FOUND")

    def test_missing_or_blank_durable_owner_fails_without_database_read(self):
        for owner in (None, TrustedPersistentIdentity("npc", "Character", "")):
            result = self.read(observer=owner)
            self.assertEqual(result.status, "INVALID_SCOPE")
        self.assertEqual(self.statements, [])

    def test_catalog_subject_authorization_is_rechecked_and_existing_ref_reused(self):
        self.add()
        catalog = RecallSubjectCatalogBuilder(self.fixture.store).build(
            request(owner=OWNER.persistent_id, requester_uid=A.persistent_id))
        result = self.read(subject=A, catalog=catalog)
        self.assertEqual(result.items[0].subjects[0].subject_ref, catalog.requester_ref)
        self.assertEqual(result.items[0].subjects[0].ref_scope, "CATALOG")
        self.assertEqual(self.read(subject=B, catalog=catalog).status, "SUBJECT_NOT_AUTHORIZED")
        self.assertEqual(self.read(observer=TrustedPersistentIdentity("npc", "Character", "other"),
                                   catalog=catalog).status, "SUBJECT_NOT_AUTHORIZED")

    def test_superseded_excluded_by_default_and_explicit_history_is_labeled(self):
        old = self.add(profile="blue", occurred=10)
        self.add(profile="red", occurred=20)
        with closing(self.fixture.database.connect()) as db:
            db.execute("UPDATE memory_records SET status='SUPERSEDED' WHERE memory_id=?", (old,))
            db.commit()
        result = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"))
        self.assertEqual([(i.summary, i.lifecycle_status) for i in result.items], [("favorite_color: red", "ACTIVE")])
        history = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT", lifecycle="ALL_HISTORY"))
        self.assertEqual({i.lifecycle_status for i in history.items}, {"ACTIVE", "SUPERSEDED"})

    def test_conflicts_remain_explicit_even_when_top_k_omits_an_alternative(self):
        self.add(profile="blue", session="session_one")
        self.add(profile="red", session="session_two")
        with closing(self.fixture.database.connect()) as db:
            db.execute("UPDATE memory_records SET status='CONFLICTED'")
            db.commit()
        result = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"))
        self.assertEqual([i.lifecycle_status for i in result.items], ["CONFLICTED", "CONFLICTED"])
        self.assertEqual(len({i.memory_id for i in result.items}), 2)
        bounded = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"),
                            budget=CanonicalRecallBudget(top_k=1))
        self.assertTrue(bounded.truncated)
        self.assertEqual(bounded.items[0].lifecycle_status, "CONFLICTED")

    def test_time_filter_uses_only_certified_utc_and_keeps_unknown_clocks(self):
        self.add(profile="blue", uid="UTC_OLD", clock="backend_utc", occurred=10)
        valid = self.add(profile="red", uid="UTC_NEW", clock="backend_utc", occurred=100)
        unknown = self.add(profile="green", uid="UNITY_ONE", clock="unity_realtime", occurred=1)
        unknown2 = self.add(profile="pink", uid="UNITY_TWO", clock="unity_realtime", occurred=99999,
                            session="session_other")
        result = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT",
            occurred_after_utc=90, occurred_before_utc=110))
        self.assertEqual({i.memory_id for i in result.items}, {valid, unknown, unknown2})
        self.assertEqual(sum(i.temporal_filter_applied for i in result.items), 1)
        episodic = self.add(clock="backend_utc", occurred=1)
        result = self.read(filters=CanonicalReadFilter(memory_type="EPISODIC", occurred_after_utc=90))
        self.assertEqual(result.items[0].memory_id, episodic)
        self.assertEqual(result.items[0].occurred_time_basis, "UNKNOWN")

    def test_provenance_narrows_and_real_lineage_survives(self):
        wanted = self.add(provenance="HEARD")
        self.add(provenance="WITNESSED")
        result = self.read(filters=CanonicalReadFilter(provenance=("HEARD",)))
        self.assertEqual([i.memory_id for i in result.items], [wanted])
        item = result.items[0]
        self.assertEqual(item.provenance, ("HEARD",))
        self.assertTrue(item.episode_id and item.job_id and item.pipeline_version)
        expected = self.fixture.rows("SELECT * FROM memory_evidence WHERE memory_id=?", (wanted,))
        self.assertEqual(item.raw_evidence_refs[0].event_id, expected[0]["experience_event_id"])

    def test_top_k_candidate_summary_and_total_serialization_budgets(self):
        for index in range(5):
            self.add(f"取来杯子然后表示感谢，历史片段序号{index}。" * 8)
        budget = CanonicalRecallBudget(candidate_limit=3, top_k=2, per_item_summary_chars=18,
                                      total_evidence_chars=4000)
        result = self.read(budget=budget)
        self.assertEqual(result.candidate_count, 3)
        self.assertEqual(len(result.items), 2)
        self.assertTrue(result.truncated)
        # The legacy per-item length setting must not turn qualified facts into prefixes.
        self.assertTrue(all(len(i.summary) > 18 for i in result.items))
        self.assertEqual(result.char_budget_used, len(result.model_dump_json()))
        self.assertLessEqual(result.char_budget_used, budget.total_evidence_chars)
        tiny = self.read(budget=CanonicalRecallBudget(total_evidence_chars=512))
        self.assertEqual(tiny.status, "EVIDENCE_BUDGET_EXHAUSTED")
        self.assertEqual(tiny.items, ())
        self.assertLessEqual(len(tiny.model_dump_json()), 512)

    def test_subject_and_event_ref_budgets_are_explicit(self):
        for index in range(3):
            self.add(profile="blue", occurred=index + 1)
        self.add(profile="red", uid=B.persistent_id)
        result = self.read("favorite_color", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"),
            budget=CanonicalRecallBudget(max_subjects=1, max_evidence_refs_per_item=1))
        self.assertTrue(result.truncated)
        # Ranking can choose either player's ref first. Check the authority
        # budget rather than assume which subject wins a latest-memory tie.
        self.assertEqual(len({s.subject_ref for i in result.items for s in i.subjects}), 1)
        self.assertEqual(sum(len(i.subjects)+i.subjects_omitted for i in result.items), 4)
        self.assertGreater(sum(i.subjects_omitted for i in result.items), 0)
        blue = next(i for i in result.items if i.summary == "favorite_color: blue")
        self.assertFalse(blue.evidence_refs_truncated)
        self.assertEqual(len(blue.raw_evidence_refs), 1)

    def test_no_memory_is_normal_for_subject_and_open_queries(self):
        self.add()
        for subject in (None, B):
            result = self.read("unrelated", subject=subject)
            self.assertEqual(result.status, "NO_MEMORY_FOUND")
            self.assertEqual(result.items, ())

    def test_uid_absent_in_serialization_including_source_excerpt(self):
        job, context = self.fixture.work(text=A.persistent_id + ": My preferred name is Alice.")
        response = self.fixture.response(context)
        response["semantic_facts"][0]["fact"] = "My preferred name is Alice."
        self.fixture.finalize(job, context, response)
        result = self.read("Alice", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"))
        serialized = result.model_dump_json()
        for secret in (A.persistent_id, OWNER.persistent_id, "persistent_id", "identity_namespace"):
            self.assertNotIn(secret, serialized)
        self.assertTrue(result.items[0].content_redacted)
        for item in (result, *result.items):
            self.assertTrue(item.not_current_world_state and item.not_instruction and item.not_action_authorization)


    def test_read_only_canonical_queries_and_linked_raw_source_certificate(self):
        self.add()
        with closing(self.fixture.database.connect()) as db:
            before = tuple(db.iterdump())
        self.read()
        with closing(self.fixture.database.connect()) as db:
            self.assertEqual(before, tuple(db.iterdump()))
        selects = "\n".join(s for s in self.statements if s.lstrip().upper().startswith(("SELECT", "WITH")))
        for table in ("memory_v1_", "semantic_memory_", "episodes", "memory_jobs"):
            self.assertNotIn(table, selects)
        raw_reads = [s for s in self.statements if "JOIN experience_events" in s]
        self.assertTrue(raw_reads)
        self.assertTrue(all("memory_evidence" in s and "WHERE m.memory_id=" in s for s in raw_reads))
        self.assertTrue(any("query_only=ON" in s for s in self.statements))

    def test_filters_and_budget_reject_invalid_values(self):
        for kwargs in ({"attribute_key": "help_event"}, {"lifecycle": "INACTIVE"},
                       {"provenance": ("FORGED",)}, {"occurred_after_utc": 10, "occurred_before_utc": 1}):
            with self.assertRaises(ValidationError):
                CanonicalReadFilter(**kwargs)
        with self.assertRaises(ValidationError):
            CanonicalRecallBudget(candidate_limit=10000)


if __name__ == "__main__":
    unittest.main()
