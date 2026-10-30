"""Offline two-inference context reads. Temporary catalogs, scripted provider, no model API."""
import asyncio
import json
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import (CognitiveMemoryRecall, qualify_self_statement_delivery)
from app.cognitive_step_models import (CognitiveStepResult, TaskCognitiveStep, TerminalCognitiveStep,
    RecallOutcome, MemoryRecallPlan)
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.world_knowledge import KnowledgeContext, KnowledgeDeployment
from app.world_knowledge_store import WorldKnowledgeResolver, WorldKnowledgeStore
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.recall_subjects import RecallSubjectCatalogBuilder
from tests import test_memory_d2_cognitive_steps as memory
from tests import test_cognitive_tasks as tasks
from tests.test_world_knowledge_content import bound_request

TERMINAL = dict(decision_kind="DIRECT_RESPONSE", utterance="我会根据已提供的资料回答，未知的部分仍待确认。")


def plan(knowledge=None, historical=None, task=None):
    value = dict(decision_kind="CONTEXT_READ")
    if knowledge is not None:
        value["knowledge_plan"] = dict(semantic_query=knowledge)
    if historical is not None:
        value["memory_plan"] = dict(semantic_query=historical)
    if task is not None:
        value["task_plan"] = task
    return value


def data(model, marker="Knowledge context JSON: "):
    return json.loads(model.messages[-1].content.split(marker)[1].splitlines()[0])


class ContextReadTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="context-read-")
        self.addCleanup(self.tmp.cleanup)
        self.store = WorldKnowledgeStore(Path(self.tmp.name) / "knowledge.json")
        self.store.initialize()
        self.request = memory.cognitive_request("那哪种比较适合？")
        owner = self.request.recall_identity.observer
        self.deployment = KnowledgeDeployment(deployment_id="cafedemo-v2", bindings=[dict(
            agent_entity_id=owner.entity_id, character_id=self.request.context.character.character_id,
            identity_namespace=owner.identity_namespace, identity_type=owner.identity_type,
            persistent_id=owner.persistent_id)])
        self.resolver = WorldKnowledgeResolver(self.store, self.deployment)
        self.rows = memory.MinimalRows()
        self.recall = CognitiveMemoryRecall(RecallSubjectCatalogBuilder(memory.KnownSubjects()),
                                           CanonicalMemoryRetrieval(self.rows))

    def entry(self, scripts, **kwargs):
        self.provider = memory.ScriptedProvider(scripts)
        return CognitiveBackendEntry(self.provider, PrioritizedModelExecutor(),
            knowledge_resolver=self.resolver, memory_recall=self.recall, context_length=32768, **kwargs)

    def test_strict_plan_schema_and_no_authority_knobs(self):
        for value in [plan(), plan(" "), plan(historical=" "), plan(task={}),
                      {**plan("coffee"), "top_k": 8},
                      dict(decision_kind="CONTEXT_READ", knowledge_plan=dict(semantic_query="coffee", top_k=8)),
                      dict(decision_kind="CONTEXT_READ", knowledge_plan=dict(semantic_query="coffee", character_id="other")),
                      plan("x" * 513)]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                CognitiveStepResult.model_validate(value)
        for value in [plan("coffee"), plan(historical="history"), plan("coffee", "history")]:
            CognitiveStepResult.model_validate(value)
        TaskCognitiveStep.model_validate(plan(task={}))
        for value in [plan("coffee"), dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="x")),
                      dict(decision_kind="TASK_READ", task_query={})]:
            with self.assertRaises(ValueError):
                TerminalCognitiveStep.model_validate(value)

    async def test_four_paths_constant_once_and_knowledge_does_not_trigger_memory(self):
        for knowledge, historical in [(None, None), ("馥芮白和拿铁", None),
                                       (None, "喜欢的颜色"), ("馥芮白和拿铁", "喜欢的颜色")]:
            with self.subTest(knowledge=knowledge, historical=historical):
                self.rows.calls.clear()
                backend = self.entry([plan(knowledge, historical), TERMINAL] if knowledge or historical else [TERMINAL])
                result = await backend.run(self.request)
                self.assertEqual(result.decision.utterance, TERMINAL["utterance"])
                self.assertEqual(len(self.provider.requests), 2 if knowledge or historical else 1)
                self.assertEqual(len(self.rows.calls), int(historical is not None))
                self.assertEqual(data(self.provider.requests[0])["items"], [])
                for model in self.provider.requests:
                    knowledge_data = data(model)
                    self.assertEqual([x["knowledge_id"] for x in knowledge_data["constant_items"]], ["cafe.setting"])
                    self.assertEqual(model.messages[-1].content.count('"knowledge_id":"cafe.setting"'), 1)
                if knowledge or historical:
                    schema = json.dumps(self.provider.requests[1].response_schema)
                    for name in ["ContextReadStep", "MemoryRecallStep", "TaskReadStep"]:
                        self.assertNotIn(name, schema)
                if knowledge:
                    self.assertIn("coffee.flat_white", [x["knowledge_id"] for x in data(self.provider.requests[1])["items"]])
                    self.assertLessEqual(len(data(self.provider.requests[1])["items"]), 3)
                else:
                    self.assertEqual(data(self.provider.requests[-1])["items"], [])

    async def test_no_match_and_unavailable_are_distinct_terminal_states(self):
        for fail, status in [(False, "NO_MATCH"), (True, "UNAVAILABLE")]:
            backend = self.entry([plan("zzzz_unknown_9284"), TERMINAL])
            with patch.object(self.resolver, "query", side_effect=RuntimeError("offline")) if fail else patch.object(
                    self.resolver, "query", wraps=self.resolver.query):
                await backend.run(self.request)
            self.assertEqual(data(self.provider.requests[1])["supplement_status"], status)
            self.assertEqual(len(self.provider.requests), 2)

    async def test_single_branch_failure_preserves_other_source(self):
        backend = self.entry([plan("coffee.latte", "颜色"), TERMINAL])
        with patch.object(self.recall, "recall", side_effect=RuntimeError("offline")):
            await backend.run(self.request)
        model = self.provider.requests[1]
        self.assertEqual(data(model)["items"][0]["knowledge_id"], "coffee.latte")
        self.assertEqual(data(model, "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "RECALL_UNAVAILABLE")
        backend = self.entry([plan("coffee.latte", "颜色"), TERMINAL])
        with patch.object(self.resolver, "query", side_effect=RuntimeError("offline")):
            await backend.run(self.request)
        self.assertEqual(data(self.provider.requests[1])["supplement_status"], "UNAVAILABLE")
        self.assertEqual(data(self.provider.requests[1], "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "OK")

    async def test_independent_read_branches_can_wait_together(self):
        barrier = threading.Barrier(2, timeout=3)
        def knowledge(*args):
            barrier.wait()
            return KnowledgeContext(status="NO_MATCH")
        def historical(*args):
            barrier.wait()
            return RecallOutcome(status="NO_MEMORY_FOUND")
        backend = self.entry([plan("coffee", "颜色"), TERMINAL])
        with patch.object(self.resolver, "query", side_effect=knowledge), patch.object(self.recall, "recall", side_effect=historical):
            await backend.run(self.request)
        self.assertEqual(len(self.provider.requests), 2)
        self.assertEqual(data(self.provider.requests[1])["supplement_status"], "NO_MATCH")
        self.assertEqual(data(self.provider.requests[1], "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "NO_MEMORY_FOUND")

    async def test_catalog_replaced_during_rank_discards_all_old_knowledge_not_memory(self):
        def rank(entries, query, **kwargs):
            current = self.store.snapshot()[1]
            self.store.replace(current.model_copy(update={"entries": ()}), expected_revision=current.revision)
            return tuple(entries)
        self.resolver.ranker = Mock(rank=rank)
        await self.entry([plan("coffee.latte", "颜色"), TERMINAL]).run(self.request)
        second = self.provider.requests[1]
        knowledge = data(second)
        self.assertEqual(knowledge["status"], "SNAPSHOT_CHANGED")
        self.assertEqual(knowledge["items"], [])
        self.assertEqual(knowledge["constant_items"], [])
        self.assertEqual(data(second, "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "OK")
        self.assertEqual(len(self.provider.requests), 2)

    def test_same_revision_external_replace_is_not_a_valid_snapshot(self):
        snapshot = self.resolver.capture(self.request)
        changed = snapshot.catalog.model_copy(update={"entries": ()})
        self.store.path.write_text(changed.model_dump_json(), encoding="utf-8")
        self.assertEqual(self.resolver.query(snapshot, "coffee.latte").status, "SNAPSHOT_CHANGED")

    async def test_replacement_during_final_inference_aborts_reply_no_third_call(self):
        def replace_and_answer(model):
            current = self.store.snapshot()[1]
            self.store.replace(current.model_copy(update={"entries": ()}), expected_revision=current.revision)
            return TERMINAL
        for scripts in [[replace_and_answer], [plan("coffee.latte"), replace_and_answer]]:
            with self.subTest(calls=len(scripts)):
                self.store.manage("restore-all", expected_revision=self.store.snapshot()[1].revision)
                with self.assertRaisesRegex(ValueError, "KNOWLEDGE_SNAPSHOT_CHANGED"):
                    await self.entry(scripts).run(self.request)
                self.assertEqual(len(self.provider.requests), len(scripts))

    async def test_step1_cannot_read_even_after_knowledge_only_no_match(self):
        for second in [plan("coffee"), dict(decision_kind="TASK_READ", task_query={}),
                       dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="x"))]:
            with self.subTest(second=second), self.assertRaises(ValueError):
                await self.entry([plan("unknown9284"), second]).run(self.request)
            self.assertEqual(len(self.provider.requests), 2)

    async def test_cancel_and_timeout_during_read_cannot_submit_step1(self):
        for timeout in [False, True]:
            started, release = threading.Event(), threading.Event()
            def read(*args):
                started.set()
                release.wait(3)
                return KnowledgeContext(status="NO_MATCH")
            backend = self.entry([plan("coffee"), TERMINAL])
            with patch.object(self.resolver, "query", side_effect=read):
                task = asyncio.create_task(asyncio.wait_for(backend.run(self.request), .1) if timeout else backend.run(self.request))
                self.assertTrue(await asyncio.to_thread(started.wait, 2))
                if not timeout:
                    task.cancel()
                try:
                    with self.assertRaises(asyncio.TimeoutError if timeout else asyncio.CancelledError):
                        await task
                finally:
                    release.set()
                await asyncio.sleep(.02)
            self.assertEqual(len(self.provider.requests), 1)

    async def test_unbound_character_cannot_query_and_unknown_subject_stays_scoped(self):
        request = self.request.model_copy(update={"recall_identity": None})
        await self.entry([plan("coffee.latte", "颜色"), TERMINAL]).run(request)
        second = self.provider.requests[1]
        self.assertEqual(data(second)["status"], "UNAUTHORIZED")
        self.assertEqual(data(second)["items"], [])
        self.assertEqual(data(second, "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "RECALL_UNAVAILABLE")
        value = plan(historical="颜色")
        value["memory_plan"]["subject_ref"] = "invented_player"
        await self.entry([value, TERMINAL]).run(self.request)
        self.assertEqual(data(self.provider.requests[1], "HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ")["status"], "UNKNOWN_SUBJECT")

    async def test_triple_read_uses_existing_task_refs_and_can_save_terminal_task(self):
        fixture = tasks.CognitionTaskTests()
        fixture.setUp()
        request = tasks.request(number="triple")
        owner = request.recall_identity.observer
        self.resolver.deployment = KnowledgeDeployment(deployment_id="cafedemo-v2", bindings=[dict(
            agent_entity_id=owner.entity_id, character_id=request.context.character.character_id,
            identity_namespace=owner.identity_namespace, identity_type=owner.identity_type, persistent_id=owner.persistent_id)])
        result = await self.entry([plan("coffee.latte", "颜色", {}), tasks.ACCEPT], tasks=fixture.tasks).run(request)
        self.assertTrue(result.task_receipt.committed)
        self.assertEqual(result.task_receipt.run_id, request.run.run_id)
        self.assertEqual(tasks.task_data(self.provider.requests[1])["status"], "OK")
        self.assertEqual(self.provider.requests[1].response_schema["title"], "CognitiveDecision")
        stored = fixture.service.get_task(fixture.tasks.prepare(request).scope, result.task_receipt.task_id)
        self.assertEqual(stored.goal.drink_type, "latte")
        self.assertEqual(len(self.provider.requests), 2)

    def test_explicit_phase_does_not_depend_on_any_outcome_presence(self):
        model = CognitiveModelRequestFactory.create_event(self.request, cognition_phase=1, knowledge_context_limit=32768)
        self.assertNotIn("CONTEXT_READ", json.dumps(model.response_schema))
        self.assertIn("Step 1", model.messages[0].content)
        for phase in [-1, 2, True]:
            with self.assertRaises(ValueError):
                CognitiveModelRequestFactory.create_event(self.request, cognition_phase=phase)

    async def test_fixed_scenarios_fake_ab_sources_hits_calls_and_latency(self):
        # Plans and answers are scripted. This tests execution, not model planning/naturalness.
        scenarios = [
            ("A", "馥芮白和拿铁有什么区别？", "馥芮白 拿铁", False, {"coffee.flat_white", "coffee.latte"}),
            ("B", "那哪种比较适合做拿铁？", "浅烘焙 深烘焙 拿铁 奶咖", False, {"coffee.roasting"}),
            ("C", "你还记得我上次喜欢什么颜色吗？", None, True, set()),
            ("D", "你之前跟我讲过和知意在书铺的事，后来怎么样了？", "书铺 分歧 路线 尚未成行", True, {"event.bookshop_route_choice"}),
            ("E", "青石街的纸页书铺有什么特别？", "纸页书铺", False, {"place.corner_bookshop"}),
            ("F", "你好", None, False, set()),
        ]
        deployment = WorldKnowledgeResolver.from_environment().deployment
        binding = next(x for x in deployment.bindings if x.character_id == "cafe.regular_customer")
        self.resolver.deployment = deployment
        self.ab_metrics = []
        for label, text, query, recall, expected in scenarios:
            request = bound_request(binding, text)
            # A is the unchanged resolver/factory legacy construction, one auto query.
            started = time.perf_counter()
            auto = self.resolver.resolve(request)
            a_scripts = [memory.recall, TERMINAL] if recall else [TERMINAL]
            a_provider = memory.ScriptedProvider(a_scripts)
            catalog = self.recall.prepare(request)
            first = CognitiveModelRequestFactory.create_event(request, knowledge_context=auto,
                subject_catalog=catalog.model_visible_payload(), knowledge_context_limit=32768)
            a_result = await a_provider.generate(first)
            if recall:
                legacy_plan = CognitiveStepResult.model_validate(json.loads(a_result.text)).root.recall_plan
                outcome = self.recall.recall(catalog, legacy_plan)
                await a_provider.generate(CognitiveModelRequestFactory.create_event(request, cognition_phase=1,
                    recall_outcome=outcome, knowledge_context=auto, knowledge_context_limit=32768))
            a_ms = (time.perf_counter() - started)*1000
            backend = self.entry([plan(query, "以前实际聊过的书铺内容" if label == "D" else "颜色" if recall else None), TERMINAL]
                                 if query or recall else [TERMINAL])
            recent = [{"speaker": "player", "text": "浅烘焙和深烘焙的差别是什么？"}] if label == "B" else []
            with patch.object(backend, "_read_recent_dialogue", return_value=recent):
                started = time.perf_counter()
                await backend.run(request)
                b_ms = (time.perf_counter()-started)*1000
            result = data(self.provider.requests[-1])
            ids = {x["knowledge_id"] for x in result["items"]}
            self.assertTrue(expected <= ids, (label, expected, ids))
            self.assertEqual(len(self.provider.requests), 2 if query or recall else 1)
            if label == "B":
                self.assertIn("浅烘焙和深烘焙", self.provider.requests[0].messages[-1].content)
            if label == "E":
                self.assertNotIn("coffee.latte", ids)
            if label == "F":
                self.assertEqual(result["items"], [])
            self.ab_metrics.append(dict(scenario=label, a_calls=len(a_provider.requests), b_calls=len(self.provider.requests),
                a_ids=[x["knowledge_id"] for x in auto.items], b_ids=sorted(ids), a_fake_ms=round(a_ms,2), b_fake_ms=round(b_ms,2)))
        print("FAKE_AB_EXECUTION_ONLY=" + json.dumps(self.ab_metrics, ensure_ascii=False))

    def test_self_summary_without_original_presented_pair_source_cannot_prove_telling(self):
        from app.experience_ledger import ExperienceLedger
        from app.experience_models import ExperienceDraft
        from app.sqlite_database import SQLiteDatabase
        from app.canonical_recall_models import CanonicalEventRef
        catalog = self.recall.prepare(self.request)
        outcome = self.recall.recall(catalog, MemoryRecallPlan(semantic_query="颜色"))
        item = outcome.evidence.items[0].model_copy(update={"provenance_label": "NPC_SELF_STATEMENT",
            "summary": "NPC曾自述：谈过书铺路线的第一部分，还想以后再去。"})
        outcome = outcome.model_copy(update={"evidence": outcome.evidence.model_copy(update={"items": (item,)})})
        database = SQLiteDatabase(Path(self.tmp.name) / "delivery.sqlite3")
        ledger = ExperienceLedger(database.connect)
        # Generated output without a presented Ledger event remains unknown.
        self.assertEqual(qualify_self_statement_delivery(outcome, catalog, ledger).self_statement_delivery[0].status, "UNKNOWN")
        for label, owner_uid, player_uid, expected in [
                ("other_player", memory.OWNER.persistent_id, "other-player", "UNKNOWN"),
                ("other_instance", "other-instance", memory.PLAYER.persistent_id, "UNKNOWN"),
                ("presented", memory.OWNER.persistent_id, memory.PLAYER.persistent_id, "PRESENTED_TO_REQUESTER")]:
            draft = ExperienceDraft(schema_version="experience-draft/1.1", observer_id="npc_cafe_01",
                actor_entity_id="npc_cafe_01", target_entity_ids=("player_01",),
                event_type="dialogue_response", source_type="dialogue", source_session_id="prior-session",
                clock_domain="unity_realtime", source_event_id="source_"+label, request_id="request_"+label,
                conversation_id="conversation_"+label, natural_language_payload="书铺路线那时有过分歧；后来的计划还没成行。",
                occurred_at=10, observed_at=10, provenance="EXECUTED_BY_SELF", persistent_participants=(
                    dict(entity_id="npc_cafe_01", identity_namespace="npc", identity_type="Character", persistent_id=owner_uid),
                    dict(entity_id="player_01", identity_namespace="player", identity_type="Player", persistent_id=player_uid)))
            _, event = ledger.append(draft, event_id="event_"+label, dedup_key="dedup_"+label, payload_hash="a"*64)
            linked = item.model_copy(update={"raw_evidence_refs": (CanonicalEventRef(event_id=event.event_id,
                                                                      experience_seq=event.experience_seq),)})
            qualified = qualify_self_statement_delivery(outcome.model_copy(update={
                "evidence": outcome.evidence.model_copy(update={"items": (linked,)})}), catalog, ledger)
            self.assertEqual(qualified.self_statement_delivery[0].status, expected)
            self.assertEqual(qualified.evidence.items[0].summary, item.summary)
            self.assertNotIn(owner_uid, qualified.model_dump_json())
            if expected == "PRESENTED_TO_REQUESTER":
                self.assertEqual(qualified.self_statement_delivery[0].verified_event_ids, (event.event_id,))
                # A sequence mismatch does not certify another source even with a valid event ID.
                wrong = linked.model_copy(update={"raw_evidence_refs": (CanonicalEventRef(event_id=event.event_id,
                                                                                     experience_seq=999),)})
                unknown = qualify_self_statement_delivery(outcome.model_copy(update={
                    "evidence": outcome.evidence.model_copy(update={"items": (wrong,)})}), catalog, ledger)
                self.assertEqual(unknown.self_statement_delivery[0].status, "UNKNOWN")

    async def test_cancelled_final_task_decision_never_commits(self):
        fixture = tasks.CognitionTaskTests()
        fixture.setUp()
        running = None
        def cancel(model):
            running.cancel()
            return tasks.ACCEPT
        backend = self.entry([plan(task={}), cancel], tasks=fixture.tasks)
        request = tasks.request(number="cancel_after_reads")
        running = asyncio.create_task(backend.run(request))
        with self.assertRaises(asyncio.CancelledError):
            await running
        self.assertEqual(fixture.service.query_tasks(fixture.tasks.prepare(request).scope).tasks, ())
        self.assertEqual(len(self.provider.requests), 2)

    async def test_context_read_preserves_immediate_drink_and_counter_action_admission(self):
        from tests import test_prepare_drink as drinks
        from tests import test_clean_counter as counter
        for request, decision, goal in [
                (drinks.request(), drinks.decision(dict(drink_type="mocha")), "prepare_drink"),
                (counter.request(), counter.decision(), "clean_counter")]:
            result = await self.entry([plan("coffee", "历史"), decision]).run(request)
            self.assertEqual(result.validated_goal.type, goal)
            self.assertEqual(len(self.provider.requests), 2)
            self.assertNotIn("CONTEXT_READ", json.dumps(self.provider.requests[1].response_schema))

    async def test_context_read_before_task_action_uses_original_ticket_and_receipt_settlement(self):
        from tests.test_task_execution import TaskExecutionTests
        from tests.test_task_service import synthetic_result
        for clean in [False, True]:
            fixture = TaskExecutionTests()
            fixture.setUp()
            self.addCleanup(fixture.doCleanups)
            original = fixture.run_scripts
            async def read_then_execute(request, scripts):
                return await original(request, [plan("相关背景", "历史", {}), scripts[-1]])
            fixture.run_scripts = read_then_execute
            request, ticket, provider = await fixture.reserve(clean=clean, drink="mocha")
            self.assertEqual(len(provider.requests), 2)
            self.assertEqual(ticket.agent_entity_id, request.run.agent_entity_id)
            self.assertTrue(fixture.control(request, ticket, "begin_execution")["start_permitted"])
            outcome = synthetic_result(fixture.task(request, ticket))
            self.assertEqual(fixture.control(request, ticket, "settle_execution", outcome)["task_status"], "completed")
            if not clean:
                self.assertEqual(outcome.receipt.drink_type, "mocha")
