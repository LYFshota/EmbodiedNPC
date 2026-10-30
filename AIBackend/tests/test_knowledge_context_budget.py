"""Only the repaired allocation boundary; reuse existing Knowledge/M3 fixtures."""
import asyncio
import json
import unittest
from unittest.mock import AsyncMock
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetExceeded, ContextBudgetPolicy
from app.model_requests import CognitiveModelRequestFactory
from app.relationship_context import RelationshipContext
from app.cognitive_step_models import MemoryRecallPlan
from app.world_knowledge import KnowledgeContext
from tests import test_world_knowledge as knowledge_fixtures
from tests import test_memory_d2_cognitive_steps as memory_fixtures


class KnowledgeContextBudgetTests(unittest.TestCase):
    @staticmethod
    def capacity_for_input(tokens, output=1024):
        # Keep the same pressure/retention contract as the production margin changes.
        capacity = tokens + output + 768
        while (missing := tokens - ContextBudgetPolicy.cognition(capacity, output).input_hard_cap) > 0:
            capacity += missing
        return capacity

    def setUp(self):
        self.knowledge = knowledge_fixtures.KnowledgeTests(); self.knowledge.setUp()
        self.addCleanup(self.knowledge.doCleanups)

    def test_long_exact_knowledge_has_distinct_budget_status_and_elastic_foreground_context(self):
        from app.world_knowledge import KnowledgeQuery, retrieve_knowledge, KnowledgePrincipal
        entry = next(e for e in self.knowledge.entries if e.knowledge_id == "coffee.latte")
        content = "配方说明与适用条件。" * 187 + "只有玩家明确同意时才适用；不代表实时库存，也不证明已经制作。"
        self.assertGreater(len(content), 1800)
        self.assertLessEqual(len(content), 2000)
        entry = entry.model_copy(update={"content": content, "version": 7})
        kwargs = dict(bundle_version="test/long", revision=6)
        query = KnowledgeQuery(knowledge_ids=(entry.knowledge_id,))
        limited = retrieve_knowledge((entry,), self.knowledge.principal, query, **kwargs)
        self.assertEqual(limited.status, "BUDGET_EXHAUSTED")
        self.assertEqual(limited.items, ())
        self.assertTrue(limited.truncated)
        candidate = retrieve_knowledge((entry,), self.knowledge.principal, query, defer_budget=True, **kwargs)
        self.assertEqual(candidate.items[0]["content"], content)
        self.assertEqual(candidate.items[0]["version"], 7)
        self.assertGreater(ContextTokenEstimator().estimate_text(candidate.serialized()), 1000)
        missing = retrieve_knowledge((entry,), self.knowledge.principal,
            KnowledgeQuery(knowledge_ids=("missing.exact",)), **kwargs)
        denied = retrieve_knowledge((entry,), KnowledgePrincipal(character_id="unauthorized",
            deployment_id="other-deployment"), query, **kwargs)
        self.assertEqual(missing, denied)
        self.assertEqual(denied.status, "NO_MATCH")
        self.assertNotIn(entry.knowledge_id, denied.serialized())
        request = knowledge_fixtures.official_merchant_request(entry.knowledge_id)
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context=candidate,
            knowledge_context_limit=16384)
        retained = self.knowledge_payload(model)
        self.assertEqual(retained["items"][0]["content"], content)
        self.assertEqual(retained["status"], "OK")
        self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        # Global space, not a fixed local cap, now decides whether the whole entry fits.
        empty = CognitiveModelRequestFactory.create_event(request, knowledge_context=limited,
            knowledge_context_limit=16384)
        capacity = self.capacity_for_input(ContextTokenEstimator().estimate_text(empty.model_dump_json()) + 20)
        tight = CognitiveModelRequestFactory.create_event(request, knowledge_context=candidate,
            knowledge_context_limit=capacity)
        self.assertEqual(self.knowledge_payload(tight)["status"], "BUDGET_EXHAUSTED")
        self.assertEqual(self.knowledge_payload(tight)["items"], [])
        self.assertNotIn(content, tight.messages[-1].content)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(tight.model_dump_json()), ContextBudgetPolicy.cognition(capacity, 1024).input_hard_cap)
        memory_fixture = memory_fixtures.MemoryD2Tests(); recall_entry = memory_fixture.entry([])
        catalog = recall_entry._memory_recall.prepare(memory_fixtures.cognitive_request())
        recalled = recall_entry._memory_recall.recall(catalog,
            MemoryRecallPlan(semantic_query="颜色", subject_ref=catalog.requester_ref))
        options = dict(subject_catalog=catalog.model_visible_payload(), recall_outcome=recalled, cognition_phase=1 if recalled else 0)
        memory_only = CognitiveModelRequestFactory.create_event(request, knowledge_context=limited,
            knowledge_context_limit=16384, **options)
        memory_capacity = self.capacity_for_input(ContextTokenEstimator().estimate_text(memory_only.model_dump_json()) + 20)
        pressure = CognitiveModelRequestFactory.create_event(request, knowledge_context=candidate,
            knowledge_context_limit=memory_capacity, **options)
        self.assertIn(recalled.evidence.items[0].summary, pressure.messages[-1].content)
        self.assertEqual(self.knowledge_payload(pressure)["status"], "BUDGET_EXHAUSTED")
        # Unknown capacity does not acquire the verified cloud's 16K allowance.
        fixture = memory_fixtures.MemoryD2Tests(); entry_unknown = fixture.entry([memory_fixtures.TERMINAL])
        from app.cognitive_entry import CognitiveBackendEntry
        # The shared positive D2 fixture explicitly configures 16K. Construct
        # an actually unspecified window for this conservative-fallback check.
        entry_unknown = CognitiveBackendEntry(fixture.provider, fixture.executor)
        from unittest.mock import Mock
        entry_unknown._knowledge_resolver = Mock(resolve=Mock(return_value=candidate))
        self.assertEqual(entry_unknown._context_length, 8192)
        try:
            asyncio.run(entry_unknown.run(request))
        except ContextBudgetExceeded:
            self.assertEqual(fixture.provider.requests, [])
        else:
            self.assertLessEqual(ContextTokenEstimator().estimate_text(fixture.provider.requests[0].model_dump_json()), ContextBudgetPolicy.cognition(8192, 1024).input_hard_cap)
        with self.assertRaises(ContextBudgetExceeded):
            CognitiveModelRequestFactory.create_event(request, knowledge_context=candidate,
                knowledge_context_limit=4096)

    @staticmethod
    def knowledge_payload(model):
        line = next(v for v in model.messages[-1].content.splitlines() if v.startswith("Knowledge context JSON: "))
        return json.loads(line.split(": ", 1)[1])

    def test_relationship_window_keeps_updates_uncertainty_and_sources_without_double_caps(self):
        from tests.test_relationship_summary_reader import RelationshipSummaryReaderTests, NPC_A, PLAYER_A, PLAYER_B
        fixture = RelationshipSummaryReaderTests(); fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        texts = tuple("此前交流的完整背景。" * 40 + tail for tail in (
            "玩家曾明确要求保持距离；这是当时的交流解释。",
            "玩家后来表示可以聊饮品，但不希望未经同意的帮助。",
            "这次交流缓和，不能由此认定已经建立亲密关系。",
            "旧的疏远解释后来被澄清，但同意边界仍需保留。",
            "最近似乎更信任我；尚未确认，不能覆盖旧的可靠承诺。"))
        for value in texts:
            fixture.add(value)
        legacy = fixture.reader.get(NPC_A, PLAYER_A)
        full = fixture.reader.get_for_cognition(NPC_A, PLAYER_A)
        self.assertLess(legacy.selected_count, 5)
        self.assertEqual(full.interpretations, texts)
        self.assertGreater(full.token_budget_used, 1200)
        self.assertEqual([s["generated_at"] for s in full.sources], [1, 2, 3, 4, 5])
        self.assertEqual(full.sources[-1]["reflection_id"], "reflection_5")
        self.assertEqual(fixture.reader.get_for_cognition(NPC_A, PLAYER_B).interpretations, ())
        social = RelationshipContext(interpretations=full.interpretations, sources=full.sources,
            truncated=full.truncated, policy_version=full.policy_version)
        request = knowledge_fixtures.official_merchant_request("我们之前的交往边界？")
        model = CognitiveModelRequestFactory.create_event(request, relationship_context=social,
            knowledge_context_limit=16384)
        self.assertGreater(ContextTokenEstimator().estimate_text(social.model_dump_json()), 800)
        for value in texts:
            self.assertIn(value, model.messages[-1].content)
        self.assertIn("Recency alone does not establish truth", model.messages[-1].content)
        self.assertNotIn(PLAYER_A.persistent_id, model.messages[-1].content)
        # A conflict/update window is atomic under pressure; never retain only the old side.
        base = CognitiveModelRequestFactory.create_event(request, relationship_context=social.model_copy(
            update={"interpretations": (), "sources": (), "truncated": True}), knowledge_context_limit=16384)
        cap = self.capacity_for_input(ContextTokenEstimator().estimate_text(base.model_dump_json()) + 350)
        trimmed = CognitiveModelRequestFactory.create_event(request, relationship_context=social,
            knowledge_context_limit=cap)
        line = next(v for v in trimmed.messages[-1].content.splitlines() if v.startswith("Relationship context JSON: "))
        projection = json.loads(line.split(": ", 1)[1])
        self.assertEqual(projection["interpretations"], [])
        self.assertEqual(projection["sources"], [])
        self.assertTrue(projection["truncated"])
        self.assertLessEqual(ContextTokenEstimator().estimate_text(trimmed.model_dump_json()), ContextBudgetPolicy.cognition(cap, 1024).input_hard_cap)

    def test_global_16k_cap_keeps_long_knowledge_and_omits_relationship_atomically(self):
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        entry._context_length = 16384
        original = next(e for e in self.knowledge.entries if e.knowledge_id == "coffee.latte")
        content = "已授权饮品的完整知识背景。" * 140 + "仅在玩家同意时适用，不证明当前库存或动作完成。"
        self.knowledge.store.manage("upsert", entry=original.model_copy(update={"content": content}), expected_revision=0)
        request = knowledge_fixtures.official_merchant_request("coffee.latte，以及我以前喜欢的颜色？")
        knowledge = self.knowledge.resolver.resolve(request)
        self.assertGreater(ContextTokenEstimator().estimate_text(knowledge.serialized()), 1000)
        social = RelationshipContext(interpretations=("以前的边界和承诺。" * 60, "后来澄清的边界，推测仍未确认。" * 60),
            truncated=False, policy_version="test/p1")
        recent = [{"speaker": "player", "text": "请保留我刚才表达的同意条件。"}, {"speaker": "npc", "text": "我会尊重这个条件。"}]
        catalog = entry._memory_recall.prepare(memory_fixtures.cognitive_request())
        outcome = entry._memory_recall.recall(catalog, MemoryRecallPlan(semantic_query="颜色", subject_ref=catalog.requester_ref))
        saw_optional_omission = False
        for recalled, known in ((outcome, None), (None, knowledge), (outcome, knowledge)):
            model = CognitiveModelRequestFactory.create_event(request, subject_catalog=catalog.model_visible_payload(),
                cognition_phase=1, recall_outcome=recalled, knowledge_context=known, relationship_context=social,
                recent_dialogue=recent, knowledge_context_limit=16384)
            text = model.messages[-1].content
            line = next(v for v in text.splitlines() if v.startswith("Relationship context JSON: "))
            relationship = json.loads(line.split(": ", 1)[1])
            self.assertIn(relationship["interpretations"], [list(social.interpretations), []])
            if not relationship["interpretations"]:
                saw_optional_omission = True
                self.assertTrue(relationship["truncated"])
                self.assertEqual(relationship["sources"], [])
            self.assertIn("请保留我刚才表达的同意条件", text)
            if recalled: self.assertIn(outcome.evidence.items[0].summary, text)
            if known: self.assertEqual(self.knowledge_payload(model)["items"][0]["content"], content)
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        self.assertTrue(saw_optional_omission, "This stress fixture must exercise explicit whole-window omission.")
        entry._knowledge_resolver = self.knowledge.resolver
        entry._read_recent_dialogue = AsyncMock(return_value=recent)
        from unittest.mock import Mock
        entry._relationship_context_resolver = Mock(resolve=Mock(return_value=social))
        fixture.rows.calls.clear()
        def dual_read(model):
            memory_step = memory_fixtures.recall(model)
            return dict(decision_kind="CONTEXT_READ", knowledge_plan=dict(semantic_query="coffee.latte"),
                        memory_plan=memory_step["recall_plan"])
        fixture.provider.scripts[0] = dual_read
        asyncio.run(entry.run(request))
        self.assertEqual(len(fixture.rows.calls), 1)
        self.assertEqual(len(fixture.provider.requests), 2)
        for index, model in enumerate(fixture.provider.requests):
            if index == 0:
                self.assertEqual(self.knowledge_payload(model)["items"], [])
            else:
                self.assertEqual(self.knowledge_payload(model)["items"][0]["content"], content)
            line = next(v for v in model.messages[-1].content.splitlines() if v.startswith("Relationship context JSON: "))
            relationship = json.loads(line.split(": ", 1)[1])
            self.assertIn(relationship["interpretations"], [list(social.interpretations), []])
            if not relationship["interpretations"]: self.assertTrue(relationship["truncated"])
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()),
                                 ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        self.assertNotIn("MEMORY_RECALL", json.dumps(fixture.provider.requests[1].response_schema))

    def test_step0_and_step1_share_bounded_sources_with_full_persona_world_and_memory(self):
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        request = knowledge_fixtures.official_merchant_request("卡布奇诺配方，以及我以前喜欢的颜色？")
        catalog = entry._memory_recall.prepare(memory_fixtures.cognitive_request())
        outcome = entry._memory_recall.recall(catalog, MemoryRecallPlan(semantic_query="颜色", subject_ref=catalog.requester_ref))
        social = RelationshipContext(interpretations=("此前交流友善。",), truncated=False, policy_version="test/1")
        knowledge = self.knowledge.resolver.resolve(request)
        recent = [{"speaker": "player", "text": "之前聊过天气。"}, {"speaker": "npc", "text": "今天阳光不错。"},
                  {"speaker": "player", "text": "不相关旧话题" * 1500}, {"speaker": "npc", "text": "旧回复" * 1500}]
        for recalled in (None, outcome):
            model = CognitiveModelRequestFactory.create_event(request, subject_catalog=catalog.model_visible_payload(),
                cognition_phase=1 if recalled else 0, recall_outcome=recalled, relationship_context=social, recent_dialogue=recent, knowledge_context=knowledge, knowledge_context_limit=16384)
            text = "\n".join(m.content for m in model.messages)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
            self.assertIn("Knowledge context JSON:", text)
            self.assertIn("Relationship context JSON:", text)
            self.assertIn("此前交流友善", text)
            self.assertIn("今天阳光不错", text)
            self.assertNotIn("不相关旧话题", text)
            if recalled:
                line = next(v for v in text.splitlines() if v.startswith("HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: "))
                rendered = json.loads(line.split(": ", 1)[1])
                from app.cognitive_step_models import RecallOutcome
                self.assertEqual(RecallOutcome.model_validate(rendered).evidence.items, outcome.evidence.items)
                self.assertNotIn("MEMORY_RECALL", json.dumps(model.response_schema))

    def test_recalled_fact_and_top_knowledge_fit_without_null_metadata_or_optional_candidates(self):
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        request = knowledge_fixtures.official_merchant_request("卡布奇诺配方，以及我以前喜欢的颜色？")
        catalog = entry._memory_recall.prepare(memory_fixtures.cognitive_request())
        outcome = entry._memory_recall.recall(catalog, MemoryRecallPlan(semantic_query="颜色", subject_ref=catalog.requester_ref))
        knowledge = self.knowledge.resolver.resolve(request)
        first = knowledge.model_copy(update={"items": knowledge.items[:1]})
        kwargs = dict(subject_catalog=catalog.model_visible_payload(), recall_outcome=outcome, cognition_phase=1)
        compact = CognitiveModelRequestFactory.create_event(request, knowledge_context=first, knowledge_context_limit=16384, **kwargs)
        estimator = ContextTokenEstimator()
        limit = self.capacity_for_input(estimator.estimate_text(compact.model_dump_json()) + 5)
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context=knowledge, knowledge_context_limit=limit, **kwargs)
        text = model.messages[-1].content
        line = next(v for v in text.splitlines() if v.startswith("HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: "))
        from app.cognitive_step_models import RecallOutcome
        self.assertEqual(RecallOutcome.model_validate_json(line.split(": ", 1)[1]).evidence.items, outcome.evidence.items)
        line = next(v for v in text.splitlines() if v.startswith("Knowledge context JSON: "))
        self.assertEqual(json.loads(line.split(": ", 1)[1])["items"], list(first.items))
        self.assertLessEqual(estimator.estimate_text("\n".join(m.content for m in model.messages)), ContextBudgetPolicy.cognition(limit, 1024).input_hard_cap)
        self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)

    def test_explicit_16k_capacity_reaches_both_steps_without_an_8k_clamp(self):
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        request = knowledge_fixtures.official_merchant_request("卡布奇诺配方，以及我以前喜欢的颜色？")
        entry._context_length = 16384
        entry._knowledge_resolver = self.knowledge.resolver
        entry._read_recent_dialogue = AsyncMock(return_value=[
            {"speaker": "player", "text": "完整近期话题" * 190}, {"speaker": "npc", "text": "完整近期回复" * 35}])
        result = asyncio.run(entry.run(request))
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertEqual(len(fixture.rows.calls), 1)
        self.assertEqual(len(fixture.provider.requests), 2)
        estimates = []
        for model in fixture.provider.requests:
            text = "\n".join(m.content for m in model.messages)
            estimates.append(ContextTokenEstimator().estimate_text(text))
            self.assertEqual(model.inference_options.max_output_tokens, 1024)
            self.assertLessEqual(estimates[-1], ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
            self.assertIn("Knowledge context JSON:", text)
            self.assertIn("完整近期话题", text)
        self.assertGreater(max(estimates), 7168)
        from app.cognitive_step_models import RecallOutcome
        text = fixture.provider.requests[1].messages[-1].content
        line = next(v for v in text.splitlines() if v.startswith("HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: "))
        recalled = RecallOutcome.model_validate_json(line.split(": ", 1)[1])
        self.assertEqual(recalled.status, "OK")
        self.assertEqual(recalled.evidence.items[0].summary, fixture.rows.summary)
        self.assertNotIn("MEMORY_RECALL", json.dumps(fixture.provider.requests[1].response_schema))

    def test_cloud_capacity_change_requires_restart_but_unknown_capacity_stays_conservative(self):
        from app.model_api_config import ModelApiConfigDocument, MainModelConfig, EmbeddingConfig
        from app.model_config_runtime import restart_required
        from datetime import datetime, timezone
        main = MainModelConfig(display_name="isolated cloud", provider_type="openai_compatible",
            base_url="https://example.invalid/v1", model="capacity-test", context_length=8192)
        document = ModelApiConfigDocument(revision=1, updated_at=datetime.now(timezone.utc),
            main_model=main, embedding=EmbeddingConfig())
        changed = document.model_copy(update={"main_model": main.model_copy(update={"context_length": 16384})})
        self.assertTrue(restart_required(document, changed))
        unknown = document.model_copy(update={"main_model": main.model_copy(update={"context_length": None})})
        self.assertFalse(restart_required(document, unknown))
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.TERMINAL])
        from app.cognitive_entry import CognitiveBackendEntry
        entry = CognitiveBackendEntry(fixture.provider, fixture.executor)
        self.assertEqual(entry._context_length, 8192)

    def test_missing_or_unauthorized_knowledge_preserves_explicit_source_status(self):
        request = knowledge_fixtures.official_merchant_request("没有定义的内容")
        original = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384)
        for status in ("NO_MATCH", "UNAUTHORIZED", "UNAVAILABLE"):
            model = CognitiveModelRequestFactory.create_event(request,
                knowledge_context=KnowledgeContext(status=status), knowledge_context_limit=16384)
            data = self.knowledge_payload(model)
            self.assertEqual(data["status"], status)
            self.assertEqual(data["items"], [])
            self.assertEqual(model.response_schema, original.response_schema)

    def test_tight_budget_keeps_a_complete_candidate_without_truncating_its_conditions(self):
        request = knowledge_fixtures.official_merchant_request("拿铁")
        knowledge = self.knowledge.resolver.resolve(request)
        original = knowledge.items[0]
        large = {**original, "knowledge_id": "large.ref", "content": "完整的长条目。" * 50}
        small = {**original, "knowledge_id": "small.ref", "content": "桂花和薄荷；仅为静态配方，不证明库存或已完成。"}
        knowledge = knowledge.model_copy(update={"items": (large, small)})
        # The local retrieval cap excludes constants; keep constants in all full-wire checks below.
        retrieval_only = knowledge.model_copy(update={"constant_items": ()})
        self.assertLessEqual(ContextTokenEstimator().estimate_text(retrieval_only.serialized()), 1000)
        small_only = CognitiveModelRequestFactory.create_event(request, knowledge_context=knowledge.model_copy(update={"items": (small,)}), knowledge_context_limit=16384)
        limit = self.capacity_for_input(ContextTokenEstimator().estimate_text(small_only.model_dump_json()) + 10)
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context=knowledge, knowledge_context_limit=limit)
        line = next(v for v in model.messages[-1].content.splitlines() if v.startswith("Knowledge context JSON: "))
        self.assertEqual(json.loads(line.split(": ", 1)[1])["items"], [small])
        self.assertLessEqual(ContextTokenEstimator().estimate_text("\n".join(v.content for v in model.messages)), ContextBudgetPolicy.cognition(limit, 1024).input_hard_cap)

    def test_smaller_capacity_and_larger_output_reserve_cannot_overflow(self):
        request = knowledge_fixtures.official_merchant_request("拿铁")
        knowledge = self.knowledge.resolver.resolve(request)
        from app.providers import InferenceOptions
        for limit, output in ((4096, 512), (8192, 4096)):
            with self.assertRaises(ContextBudgetExceeded):
                CognitiveModelRequestFactory.create_event(request, knowledge_context=knowledge,
                    knowledge_context_limit=limit, inference_options=InferenceOptions(max_output_tokens=output))

    def test_no_knowledge_and_continuation_are_guarded_before_inference(self):
        from app.contracts import CognitiveRequest
        request = knowledge_fixtures.official_merchant_request("你好")
        data = request.model_dump()
        data["context"]["current_event"].update(event_type="npc.dialogue_continue",
            source_entity_id=request.run.agent_entity_id, target_entity_id=request.run.agent_entity_id)
        continuation = CognitiveRequest.model_validate(data)
        for value in (request, continuation):
            fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.TERMINAL])
            entry._context_length = 4096
            entry._read_recent_dialogue = AsyncMock(return_value=[])
            with self.assertRaises(ContextBudgetExceeded):
                asyncio.run(entry.run(value))
            self.assertEqual(fixture.provider.requests, [])
            model = CognitiveModelRequestFactory.create_event(value, knowledge_context_limit=16384)
            self.assertEqual(CognitiveModelRequestFactory.context_from(model), value.context)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
            if value is continuation:
                self.assertIn("这是 NPC 自主机会", model.messages[-1].content)
                self.assertNotIn("Knowledge context JSON:", model.messages[-1].content)
        from app.providers import OllamaProvider, InferenceOptions
        from app.cognitive_entry import CognitiveBackendEntry
        from app.local_model_scheduler import PrioritizedModelExecutor
        local = OllamaProvider(base_url="http://unused.invalid", model="local-4k", timeout_seconds=1,
            context_length=4096)
        local_entry = CognitiveBackendEntry(local, PrioritizedModelExecutor(), context_length=16384)
        self.assertEqual(local_entry._context_length, 4096)
        with self.assertRaises(ContextBudgetExceeded):
            asyncio.run(local_entry.run(request))
        bounded = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384,
            inference_options=InferenceOptions())
        self.assertEqual(local.budget_payload(bounded)["options"]["num_predict"], 1024)

    def test_whole_memory_items_exceed_old_quota_but_respect_global_16k_cap(self):
        from app.cognitive_step_models import RecallOutcome
        from app.canonical_memory_retrieval import _measured
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        catalog = entry._memory_recall.prepare(memory_fixtures.cognitive_request())
        claim = "这是同一条记录的补充背景。" * 29 + "只在考试结束后愿意尝试；现在并不喜欢，不代表已经制作或交付。"
        self.assertGreater(len(claim), 400)
        self.assertLessEqual(len(claim), 500)
        fixture.rows.summary = claim
        original_read = fixture.rows.read
        def read(**kwargs):
            batch = original_read(**kwargs)
            batch.candidates[0].record["source_excerpt"] = claim
            return batch
        fixture.rows.read = read
        outcome = entry._memory_recall.recall(catalog, MemoryRecallPlan(semantic_query="愿意尝试", subject_ref=catalog.requester_ref))
        item = outcome.evidence.items[0]
        self.assertEqual(item.summary, claim)
        self.assertEqual(item.source_excerpt, claim)
        self.assertEqual(item.subjects[0].subject_ref, catalog.requester_ref)
        items = tuple(item.model_copy(update={"memory_id": "fixture_" + str(i), "summary": claim}) for i in range(4))
        evidence = _measured(outcome.evidence.model_copy(update={"items": items}))
        self.assertLessEqual(evidence.char_budget_used, 8000)
        outcome = outcome.model_copy(update={"evidence": evidence})
        request = knowledge_fixtures.official_merchant_request("愿意尝试的条件和卡布奇诺配方？")
        model = CognitiveModelRequestFactory.create_event(request, subject_catalog=catalog.model_visible_payload(),
            cognition_phase=1, recall_outcome=outcome, knowledge_context=self.knowledge.resolver.resolve(request), knowledge_context_limit=16384)
        line = next(v for v in model.messages[-1].content.splitlines() if v.startswith("HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: "))
        retained = RecallOutcome.model_validate_json(line.split(": ", 1)[1])
        # The old local 1800-token quota is gone; global capacity still applies.
        # Preserve multiple complete ranked items, never a clipped summary/excerpt.
        self.assertGreater(len(retained.evidence.items), 1)
        self.assertLess(len(retained.evidence.items), len(items))
        self.assertEqual(retained.evidence.items, items[:len(retained.evidence.items)])
        self.assertTrue(retained.evidence.truncated)
        self.assertGreater(ContextTokenEstimator().estimate_text(retained.evidence.model_dump_json()), 1800)
        self.assertEqual(CognitiveModelRequestFactory.context_from(model), request.context)
        self.assertIn("Knowledge context JSON:", model.messages[-1].content)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(model.model_dump_json()), ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        # A historical item too large for the upstream evidence budget is omitted whole.
        fixture.rows.summary = "完整历史事实。" * 2000
        oversized = entry._memory_recall.recall(catalog, MemoryRecallPlan(semantic_query="历史", subject_ref=catalog.requester_ref))
        self.assertEqual(oversized.status, "EVIDENCE_BUDGET_EXHAUSTED")
        self.assertEqual(oversized.evidence.items, ())

    def test_wire_payload_with_schema_is_the_allocator_measurement(self):
        from app.openai_compatible_provider import OpenAICompatibleProvider
        from unittest.mock import patch
        from app.context_budget_allocator import ContextBudgetAllocator
        request = knowledge_fixtures.official_merchant_request("你好")
        provider = OpenAICompatibleProvider(base_url="https://example.invalid/v1", api_key="synthetic",
            model="chat-model", schema_profile="openai_strict")
        records = []
        allocate = ContextBudgetAllocator.allocate
        def observe(allocator, mandatory, groups, render):
            result = allocate(allocator, mandatory, groups, render); records.append(result); return result
        with patch.object(ContextBudgetAllocator, "allocate", observe):
            model = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384,
                budget_payload_builder=provider.budget_payload)
        wire = json.dumps(provider.budget_payload(model), ensure_ascii=False, separators=(",", ":"))
        self.assertEqual(records[0].estimated_total, ContextTokenEstimator().estimate_text(wire))
        self.assertEqual(records[0].reserved_output_tokens, 1024)
        self.assertEqual(records[0].input_hard_cap, ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        self.assertLessEqual(records[0].estimated_total, records[0].input_hard_cap)
        self.assertIn("response_schema", records[0].component_costs)
        self.assertNotIn("synthetic", wire)

    def test_cognitive_run_still_reads_memory_once_and_reuses_knowledge_snapshot(self):
        fixture = memory_fixtures.MemoryD2Tests(); entry = fixture.entry([memory_fixtures.recall, memory_fixtures.TERMINAL])
        # Snapshot reuse is exercised with ample capacity; tight-window safety is
        # tested separately rather than relying on the old 512-token margin.
        entry._context_length = 16384
        from app.world_knowledge import KnowledgeDeployment
        from app.world_knowledge_store import WorldKnowledgeResolver
        request = memory_fixtures.cognitive_request("coffee.latte")
        owner = request.recall_identity.observer
        deployment = KnowledgeDeployment(deployment_id="cafedemo-v2", bindings=[dict(
            agent_entity_id=request.run.agent_entity_id, character_id=request.context.character.character_id,
            identity_namespace=owner.identity_namespace, identity_type=owner.identity_type, persistent_id=owner.persistent_id)])
        entry._knowledge_resolver = WorldKnowledgeResolver(self.knowledge.store, deployment)
        entry._read_recent_dialogue = AsyncMock(return_value=[{"text": "旧天气话题" * 2000}])
        result = asyncio.run(entry.run(request))
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertEqual(len(fixture.rows.calls), 1)
        self.assertEqual(len(fixture.provider.requests), 2)
        self.assertNotIn("MEMORY_RECALL", json.dumps(fixture.provider.requests[1].response_schema))
        for model in fixture.provider.requests:
            self.assertIn("Knowledge context JSON:", model.messages[-1].content)
            # This fixture explicitly uses 16K. Include the schema and options,
            # rather than checking only message text against the old 8K quota.
            wire = json.dumps(model.model_dump(mode="json"), ensure_ascii=False, separators=(",", ":"))
            self.assertLessEqual(ContextTokenEstimator().estimate_text(wire),
                                 ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
