"""Hybrid read boundaries, real temporary catalogs and scripted providers only."""
import asyncio
import json
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from app.cognitive_memory_recall import qualify_shared_experience
from app.cognitive_step_models import MemoryRecallPlan
from app.model_requests import CognitiveModelRequestFactory
from app.recent_dialogue_context import RecentDialogueTimeline
from app.world_knowledge import KnowledgeContext
from app.world_knowledge_store import WorldKnowledgeResolver
from tests.test_context_read import ContextReadTests, plan, TERMINAL, data
from tests import test_memory_d2_cognitive_steps as memory


class HybridContextTests(unittest.IsolatedAsyncioTestCase):
    setUp = ContextReadTests.setUp
    entry = ContextReadTests.entry

    def scoped_catalog(self, *ids):
        current = self.store.snapshot()[1]
        entries = tuple(e.model_copy(update={"related_ids": ()}) for e in current.entries
                        if e.knowledge_id in {"cafe.setting", *ids})
        self.store.replace(current.model_copy(update={"entries": entries}), expected_revision=current.revision)

    async def test_automatic_knowledge_enters_step0_without_an_extra_inference(self):
        self.request = memory.cognitive_request("馥芮白和拿铁有什么区别？")
        with patch.object(self.resolver, "prefetch", wraps=self.resolver.prefetch) as automatic:
            await self.entry([TERMINAL]).run(self.request)
        self.assertEqual(automatic.call_count, 1)
        self.assertEqual(len(self.provider.requests), 1)
        view = data(self.provider.requests[0])
        self.assertTrue({"coffee.flat_white", "coffee.latte"} <= {x["knowledge_id"] for x in view["items"]})
        self.assertEqual([x["knowledge_id"] for x in view["constant_items"]], ["cafe.setting"])
        self.assertEqual(self.rows.calls, [])

    async def test_dense_only_irrelevant_greeting_candidates_are_not_forced_into_context(self):
        self.request = memory.cognitive_request("你好，谢谢你。")
        self.resolver.ranker = Mock(rank=lambda entries, query, **kw: tuple(
            e for e in entries if e.knowledge_id in {"coffee.latte", "coffee.mocha", "coffee.espresso"}))
        await self.entry([TERMINAL]).run(self.request)
        self.assertEqual(data(self.provider.requests[0])["items"], [])
        self.assertEqual(len(self.provider.requests), 1)

    def test_followup_carries_only_nearest_same_session_topic_and_switch_drops_it(self):
        timeline = RecentDialogueTimeline([
            dict(seq=1, role="player", text="浅烘焙和深烘焙有什么区别？"),
            dict(seq=2, role="npc", text="浅烘偏明亮，深烘偏醇厚。")],
            sources={1: {"source_session_id": "now"}, 2: {"source_session_id": "now"}}, current_session_id="now")
        query = self.resolver.automatic_query(memory.cognitive_request("那哪种更适合拿铁？"), timeline)
        self.assertIn("浅烘焙和深烘焙", query)
        self.assertIn("拿铁", query)
        self.assertLessEqual(len(query), 512)
        for question in ["换个话题，纸页书铺在哪里？", "那书铺在哪里呢？"]:
            query = self.resolver.automatic_query(memory.cognitive_request(question), timeline)
            self.assertEqual(query, question)
        timeline.current_session_id = "new-session"
        self.assertEqual(self.resolver.automatic_query(memory.cognitive_request("那哪种更适合？"), timeline), "那哪种更适合？")

    async def test_real_prefetch_uses_followup_topic(self):
        request = memory.cognitive_request("那哪种更适合拿铁？")
        timeline = RecentDialogueTimeline([dict(seq=1, role="player", text="浅烘焙和深烘焙有什么区别？")],
            sources={1: {"source_session_id": "now"}}, current_session_id="now")
        result = self.resolver.prefetch(self.resolver.capture(request), request, timeline)
        self.assertIn("coffee.roasting", {x["knowledge_id"] for x in result.items})

    async def test_supplement_fills_vacancies_and_deduplicates_constant_and_retrieval(self):
        self.scoped_catalog("coffee.latte", "coffee.mocha", "coffee.cappuccino")
        self.request = memory.cognitive_request("coffee.latte")
        await self.entry([plan("拿铁 摩卡 卡布奇诺"), TERMINAL]).run(self.request)
        first = data(self.provider.requests[0]); second = data(self.provider.requests[1])
        self.assertEqual([x["knowledge_id"] for x in first["items"]], ["coffee.latte"])
        self.assertEqual({x["knowledge_id"] for x in second["items"]}, {"coffee.latte", "coffee.mocha", "coffee.cappuccino"})
        for payload in [first, second]:
            ids = [x["knowledge_id"] for x in payload["items"] + payload["constant_items"]]
            self.assertEqual(len(ids), len(set(ids)))
            self.assertEqual(ids.count("cafe.setting"), 1)

    async def test_supplement_cannot_expose_a_fourth_id_across_inferences(self):
        self.scoped_catalog("coffee.latte", "coffee.mocha", "coffee.cappuccino", "coffee.espresso")
        self.request = memory.cognitive_request("拿铁 摩卡 卡布奇诺")
        await self.entry([plan("coffee.espresso"), TERMINAL]).run(self.request)
        views = [data(m) for m in self.provider.requests]
        ids = {x["knowledge_id"] for view in views for x in view["items"]}
        self.assertEqual(ids, {"coffee.latte", "coffee.mocha", "coffee.cappuccino"})
        self.assertEqual(views[1]["supplement_status"], "BUDGET_EXHAUSTED")

    async def test_failed_supplement_keeps_valid_automatic_evidence_and_its_own_status(self):
        self.request = memory.cognitive_request("coffee.latte")
        with patch.object(self.resolver, "query", side_effect=RuntimeError("unavailable")):
            await self.entry([plan("coffee.espresso", "颜色"), TERMINAL]).run(self.request)
        view = data(self.provider.requests[1])
        self.assertIn("coffee.latte", {x["knowledge_id"] for x in view["items"]})
        self.assertEqual(view["supplement_status"], "UNAVAILABLE")
        self.assertEqual(data(self.provider.requests[1], "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "OK")

    async def test_cancel_during_automatic_read_cannot_submit_step0(self):
        started, release = threading.Event(), threading.Event()
        def automatic(*args):
            started.set(); release.wait(3)
            return KnowledgeContext(status="NO_MATCH")
        with patch.object(self.resolver, "prefetch", side_effect=automatic):
            task = asyncio.create_task(self.entry([TERMINAL]).run(self.request))
            try:
                self.assertTrue(await asyncio.to_thread(started.wait, 2))
                task.cancel()
                with self.assertRaises(asyncio.CancelledError): await task
            finally: release.set()
            await asyncio.sleep(.02)
        self.assertEqual(self.provider.requests, [])

    def test_roles_bind_trusted_requester_observer_and_explicit_catalog_without_new_ids(self):
        catalog = self.recall.prepare(self.request)
        for role, identity in [("requester", memory.PLAYER), ("observer", memory.OWNER)]:
            self.rows.calls.clear()
            self.recall.recall(catalog, MemoryRecallPlan(semantic_query="以前说好下次聊什么", subject_role=role))
            self.assertEqual(self.rows.calls[-1]["subject"], identity)
        self.rows.calls.clear()
        observer_ref = next(c.subject_ref for c in catalog.candidates if c.source == "observer")
        result = self.recall.recall(catalog, MemoryRecallPlan(semantic_query="历史", subject_role="requester", subject_ref=observer_ref))
        self.assertEqual(result.status, "UNKNOWN_SUBJECT")
        self.assertEqual(self.rows.calls, [])
        for ref in ["invented_uid", memory.PLAYER.persistent_id]:
            self.assertEqual(self.recall.recall(catalog, MemoryRecallPlan(semantic_query="历史", subject_role="catalog", subject_ref=ref)).status, "UNKNOWN_SUBJECT")
        from app.recall_subjects import RecallSubjectCatalogBuilder
        from app.memory_consolidation_context import TrustedPersistentIdentity
        other = TrustedPersistentIdentity("player", "Player", "authorized-other")
        known = Mock(list_known_subjects=lambda owner, limit: (other,))
        known_catalog = RecallSubjectCatalogBuilder(known).build(self.request)
        other_ref = next(c.subject_ref for c in known_catalog.candidates if c.identity == other)
        self.recall.recall(known_catalog, MemoryRecallPlan(semantic_query="另一位已知者的历史", subject_role="catalog", subject_ref=other_ref))
        self.assertEqual(self.rows.calls[-1]["subject"], other)
        with self.assertRaises(ValueError): MemoryRecallPlan(semantic_query="历史", subject_role="catalog")
        with self.assertRaises(ValueError): MemoryRecallPlan(semantic_query="历史", player_id="invented")

    def test_shared_scope_requires_raw_direct_pair_evidence_and_cannot_widen_the_result(self):
        catalog = self.recall.prepare(self.request)
        outcome = self.recall.recall(catalog, MemoryRecallPlan(semantic_query="我们聊过什么", subject_role="shared"))
        self.assertIsNone(self.rows.calls[-1]["subject"])
        def participant(entity, value):
            return SimpleNamespace(entity_id=entity, identity_namespace=value.identity_namespace,
                identity_type=value.identity_type, persistent_id=value.persistent_id)
        event = SimpleNamespace(experience_seq=1, observer_id="npc_cafe_01", actor_entity_id="player_01",
            target_entity_ids=("npc_cafe_01",), provenance="SYSTEM_CONFIRMED", persistent_participants=(
                participant("npc_cafe_01", memory.OWNER), participant("player_01", memory.PLAYER)))
        ledger = Mock(get_by_event_id=lambda key: event)
        self.assertEqual(qualify_shared_experience(outcome, catalog, ledger).evidence.items, outcome.evidence.items)
        event.persistent_participants = (participant("npc_cafe_01", memory.OWNER),
            participant("player_01", memory.TrustedPersistentIdentity("player", "Player", "another-player")))
        self.assertEqual(qualify_shared_experience(outcome, catalog, ledger).status, "NO_MEMORY_FOUND")
        event.provenance = "HEARD"
        self.assertEqual(qualify_shared_experience(outcome, catalog, ledger).status, "NO_MEMORY_FOUND")
        self.assertEqual(qualify_shared_experience(outcome, catalog, None).status, "RECALL_UNAVAILABLE")

    def test_presentation_view_contains_only_retained_committed_speech_not_generated_text(self):
        timeline = RecentDialogueTimeline([dict(seq=1, role="player", text="你能讲讲书铺的事吗？")],
            sources={1: {"source_session_id": "now"}}, current_session_id="now")
        hidden_generated = "这是一段已经生成但没有呈现的独特故事。"
        first = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline, knowledge_context_limit=32768)
        second = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline, cognition_phase=1, knowledge_context_limit=32768)
        for model in [first, second]:
            proof = data(model, "PRESENTED SPEECH EVIDENCE JSON: ")
            self.assertEqual(proof["current_session_npc_seqs"], [])
            self.assertEqual(proof["verified_memory_deliveries"], [])
            self.assertFalse(proof["complete_history"])
            self.assertNotIn(hidden_generated, model.messages[-1].content)
        timeline.append(dict(seq=2, role="npc", text="已向当前玩家呈现的内容。", reply_to_seq=1))
        timeline.sources[2] = {"source_session_id": "now"}
        presented = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline, knowledge_context_limit=32768)
        self.assertEqual(data(presented, "PRESENTED SPEECH EVIDENCE JSON: ")["current_session_npc_seqs"], [2])


    def test_latest_exchange_attributes_player_question_without_inventing_an_npc_opening(self):
        timeline = RecentDialogueTimeline([
            dict(seq=1, role="player", text="路上好吗？"),
            dict(seq=2, role="npc", text="还好。", reply_to_seq=1),
            dict(seq=3, role="player", text="能讲讲你和知意的分歧吗？")],
            sources={1: {}, 2: {"input_seq": 1}, 3: {}}, current_session_id="now")
        for phase in (0, 1):
            model = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline,
                cognition_phase=phase, knowledge_context_limit=32768)
            proof = data(model, "PRESENTED SPEECH EVIDENCE JSON: ")
            self.assertEqual(proof["latest_exchange"], dict(
                player_said_seq=3, npc_said_to_player_seqs=[], reply_evidence="NOT_RETAINED"))
            user = model.messages[-1].content
            self.assertGreater(user.index("PRESENTED SPEECH EVIDENCE JSON:"), user.index("CURRENT TASK BINDING JSON:"))
            self.assertIn("not proof you never told it", user)

    def test_latest_exchange_reuses_only_exact_retained_linked_npc_speech(self):
        timeline = RecentDialogueTimeline([
            dict(seq=1, role="player", text="那次怎么了？"),
            dict(seq=2, role="npc", text="我们只是商量了计划。", reply_to_seq=1),
            dict(seq=3, role="npc", text="之后怎么执行还没确定。", anchor_seq=2)],
            sources={1: {}, 2: {"input_seq": 1}, 3: {"input_seq": 1}})
        model = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline,
            knowledge_context_limit=32768)
        proof = data(model, "PRESENTED SPEECH EVIDENCE JSON: ")["latest_exchange"]
        self.assertEqual(proof["npc_said_to_player_seqs"], [2, 3])
        self.assertEqual(proof["reply_evidence"], "RETAINED")
        # A source existing in the reader is insufficient if its speech was not retained.
        with patch.object(timeline, "budget_groups", return_value=(((1,), True),)):
            model = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=timeline,
                knowledge_context_limit=32768)
        proof = data(model, "PRESENTED SPEECH EVIDENCE JSON: ")["latest_exchange"]
        self.assertEqual(proof["npc_said_to_player_seqs"], [])
        self.assertEqual(proof["reply_evidence"], "NOT_RETAINED")

    def test_plain_and_legacy_recent_views_remain_supported(self):
        for turns in ([dict(seq=1, role="player", text="你好")],
                      [dict(input_seq=1, player_text="你好", npc_text=None, evidence_status="input_only")]):
            model = CognitiveModelRequestFactory.create_event(self.request, recent_dialogue=turns,
                knowledge_context_limit=32768)
            proof = data(model, "PRESENTED SPEECH EVIDENCE JSON: ")
            self.assertEqual(proof["verified_memory_deliveries"], [])
            self.assertFalse(proof["complete_history"])


if __name__ == "__main__": unittest.main()
