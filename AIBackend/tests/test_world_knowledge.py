from __future__ import annotations

import asyncio
from contextlib import closing
import json
import re
import sqlite3
import tempfile
import unittest
from pathlib import Path

from app.action_proposal_validator import ActionProposalValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetExceeded, ContextBudgetPolicy
from app.contracts import CognitiveRequest, CognitiveCharacterContext
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.world_action_eligibility import WorldActionNotEligibleError
from app.world_knowledge import (KnowledgeAccess, KnowledgeBundle, KnowledgeEntry, KnowledgeDeployment,
                                 KnowledgePrincipal, KnowledgeQuery, retrieve_knowledge)
from app.world_knowledge_store import (KnowledgeStoreError, WorldKnowledgeResolver, WorldKnowledgeStore,
                                      read_document)
from app.knowledge_embeddings import (KnowledgeEmbeddingIndex, KnowledgeEmbeddingIndexer,
                                     KnowledgeHybridRanker, knowledge_space, embedding_text)
from tests.test_cognitive_request import _event_request
from tests import test_memory_d2_cognitive_steps as memory_fixtures
from tests.test_memory_d2_cognitive_steps import cognitive_request, ScriptedProvider, TERMINAL, recall
from tests.test_action_proposal_validation import _decision


def cafe_request(text="卡布奇诺和拿铁有什么区别？", role="merchant"):
    data = _event_request()
    data["recall_identity"]["observer"]["persistent_id"] = f"cafe.{role}.001"
    data["context"]["character"]["character_id"] = f"cafe.{role}"
    if role != "merchant":
        agent = "npc_cafe_regular_01"
        data["run"]["agent_entity_id"] = agent
        data["trigger"]["agent_entity_id"] = agent
        data["context"]["agent_entity_id"] = agent
        data["context"]["perception"]["observer_entity_id"] = agent
        data["recall_identity"]["observer"]["entity_id"] = agent
    data["context"]["current_event"]["event_type"] = "player.utterance"
    data["context"]["current_event"]["target_entity_id"] = data["run"]["agent_entity_id"]
    data["context"]["current_event"]["payload"] = [dict(key="text", value=text)]
    return CognitiveRequest.model_validate(data)


def official_merchant_request(text):
    request = cafe_request(text)
    source = (Path(__file__).resolve().parents[2] / "UnityClient/Assets/CafeDemo/V2/Characters/Personas"
              / "CafeV2MerchantCharacter.asset").read_text(encoding="utf-8")
    fields = {"characterId": "character_id", "displayName": "display_name", "roleSummary": "role_summary",
              "backstory": "backstory", "personality": "personality", "speakingStyle": "speaking_style",
              "boundaries": "boundaries"}
    profile = {}
    for yaml_key, key in fields.items():
        raw = re.search(r"^  " + yaml_key + r": (.+)$", source, re.M).group(1)
        profile[key] = json.loads(raw) if raw.startswith('"') else raw
    return request.model_copy(update={"context": request.context.model_copy(update={
        "character": CognitiveCharacterContext.model_validate(profile)})})


class KnowledgeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "knowledge.json"
        self.store = WorldKnowledgeStore(self.path)
        self.bundle, self.overrides, self.entries = self.store.snapshot()
        default = WorldKnowledgeResolver.from_environment()
        self.resolver = WorldKnowledgeResolver(self.store, default.deployment)
        self.principal = self.resolver.deployment.resolve(cafe_request())

    def search(self, principal=None, **kwargs):
        return retrieve_knowledge(self.entries, principal or self.principal, KnowledgeQuery(**kwargs),
                                  bundle_version=self.bundle.bundle_version, revision=0)

    def test_p4b_personas_ages_preferences_and_limits(self):
        root = Path(__file__).resolve().parents[2] / "UnityClient/Assets/CafeDemo/V2/Characters/Personas"
        keys = {"characterId": "character_id", "displayName": "display_name", "roleSummary": "role_summary",
                "backstory": "backstory", "personality": "personality", "speakingStyle": "speaking_style", "boundaries": "boundaries"}
        for asset, role, age, preference in (
                ("Merchant", "merchant", 22, "少糖"), ("RegularCustomer", "regular_customer", 14, "奶茶"),
                ("CompanionCustomer", "companion_customer", 26, "红茶"), ("QuietCustomer", "quiet_customer", 21, "乌龙")):
            source = (root / f"CafeV2{asset}Character.asset").read_text(encoding="utf-8")
            profile = {}
            for yaml_key, key in keys.items():
                raw = re.search(r"^  " + yaml_key + r": (.+)$", source, re.M).group(1)
                profile[key] = json.loads(raw) if raw.startswith('"') else raw
            character = CognitiveCharacterContext.model_validate(profile)
            self.assertEqual(character.character_id, f"cafe.{role}")
            self.assertIn(f"{age}岁", character.role_summary)
            self.assertIn(preference, character.backstory)
            self.assertIn("Capability", character.boundaries)
            self.assertIn("玩家", character.boundaries)
            self.assertLessEqual(len(character.backstory), 1200)
            self.assertNotRegex(json.dumps(profile, ensure_ascii=False), r"19岁|23岁")

    def test_p4b_catalog_count_references_and_access_sets(self):
        roles = {b.character_id for b in self.resolver.deployment.bindings}
        ids = {e.knowledge_id for e in self.entries}
        self.assertEqual(len(self.entries), 25)
        self.assertEqual(len(ids), len(self.entries))
        self.assertTrue({"cafe.setting", "coffee.latte", "coffee.cappuccino", "cafe.staff.taste_guidance"} <= ids)
        personal = [e for e in self.entries if e.knowledge_type == "personal_background"]
        self.assertEqual(len(personal), 12)
        for role in roles:
            self.assertEqual(sum(e.access.character_ids == (role,) for e in personal), 3)
        events = [e for e in self.entries if e.knowledge_type == "shared_event"]
        self.assertEqual(len(events), 3)
        for entry in self.entries:
            self.assertTrue(set(entry.access.character_ids) <= roles)
            self.assertEqual(entry.access.deployment_ids, ("cafedemo-v2",))
            self.assertFalse(entry.access.scene_ids)
            self.assertTrue(set(entry.related_ids) <= ids)
        self.assertLess(self.store.builtin_path.stat().st_size, 256 * 1024)

    def test_p4b_representative_queries_and_private_exclusion(self):
        deployment = self.resolver.deployment
        principals = {b.character_id: KnowledgePrincipal(character_id=b.character_id,
            deployment_id=deployment.deployment_id, scene_id=deployment.scene_id) for b in deployment.bindings}
        for principal in principals.values():
            self.assertIn("cafe.background.name", {e["knowledge_id"] for e in self.search(principal,
                text="栖光咖啡馆的名字与来历").items})
        samples = (("cafe.merchant", "merchant.cookie_pairing", "柠檬饼搭配试验"),
                   ("cafe.regular_customer", "regular.six_panel_story", "六格故事结尾"),
                   ("cafe.companion_customer", "companion.stitched_notebook", "歪针脚摘录册"),
                   ("cafe.quiet_customer", "quiet.one_line_notebook", "每页一句小册"))
        for role, knowledge_id, query in samples:
            self.assertIn(knowledge_id, {e["knowledge_id"] for e in self.search(principals[role], text=query).items})
            for other, principal in principals.items():
                if other != role:
                    self.assertNotIn(knowledge_id, {e["knowledge_id"] for e in self.search(principal, text=query).items})
                    self.assertFalse(self.search(principal, knowledge_ids=(knowledge_id,)).items)
        for role, principal in principals.items():
            visible = self.search(principal, knowledge_ids=("event.bookshop_route_choice",)).items
            self.assertEqual(bool(visible), role in {"cafe.regular_customer", "cafe.companion_customer"})

    def test_load_save_version_delete_and_restore_across_store_instances(self):
        self.assertFalse(self.path.exists())
        original = next(e for e in self.entries if e.knowledge_id == "coffee.latte")
        edited = original.model_copy(update={"content": "开发者本地更新的一般拿铁说明。"})
        self.store.manage("upsert", entry=edited, expected_revision=0)
        _, overrides, saved = WorldKnowledgeStore(self.path).snapshot()
        changed = next(e for e in saved if e.knowledge_id == original.knowledge_id)
        self.assertEqual(changed.version, 2)
        self.assertEqual(overrides.revision, 1)
        self.assertNotEqual(changed.updated_at, original.updated_at)
        self.store.manage("delete", knowledge_id=original.knowledge_id, expected_revision=1)
        self.assertNotIn(original.knowledge_id, {e.knowledge_id for e in self.store.snapshot()[2]})
        self.store.manage("restore", knowledge_id=original.knowledge_id, expected_revision=2)
        restored = next(e for e in self.store.snapshot()[2] if e.knowledge_id == original.knowledge_id)
        self.assertEqual(restored, original)
        custom = original.model_copy(update={"knowledge_id": "custom.example"})
        self.store.manage("upsert", entry=custom, expected_revision=3)
        self.assertIn("custom.example", {e.knowledge_id for e in self.store.snapshot()[2]})
        self.store.manage("restore-all", expected_revision=4)
        self.assertEqual(self.store.snapshot()[2], self.entries)
        self.assertEqual(self.store.snapshot()[1].revision, 5)

    def test_illegal_modification_does_not_write(self):
        for operation, revision in (("upsert", 0), ("restore-all", 99), ("invent", 0)):
            with self.assertRaises(KnowledgeStoreError):
                self.store.manage(operation, expected_revision=revision)
        with self.assertRaises(ValueError):
            KnowledgeEntry.model_validate({**self.entries[0].model_dump(), "content": " "})
        malicious = _event_request()
        malicious["knowledge_operations"] = [{"operation": "delete", "knowledge_id": "cafe.setting"}]
        with self.assertRaises(ValueError):
            CognitiveRequest.model_validate(malicious)
        self.assertFalse(self.path.exists())

    def test_role_deployment_scene_and_forged_persona_do_not_expand_scope(self):
        customer = self.resolver.deployment.resolve(cafe_request(role="regular_customer"))
        ids = ("cafe.staff.taste_guidance",)
        self.assertEqual(len(self.search(knowledge_ids=ids).items), 1)
        self.assertEqual(self.search(customer, knowledge_ids=ids).items, ())
        other = self.principal.model_copy(update={"deployment_id": "other"})
        self.assertEqual(self.search(other, knowledge_ids=ids).items, ())
        scoped = self.entries[0].model_copy(update={"access": self.entries[0].access.model_copy(
            update={"scene_ids": ("cafe-v2",)})})
        for scene, count in ((None, 0), ("elsewhere", 0), ("cafe-v2", 1)):
            result = retrieve_knowledge((scoped,), self.principal.model_copy(update={"scene_id": scene}),
                KnowledgeQuery(knowledge_ids=(scoped.knowledge_id,)), bundle_version="test", revision=0)
            self.assertEqual(len(result.items), count)
        forged = cafe_request(role="regular_customer")
        forged = forged.model_copy(update={"context": forged.context.model_copy(update={"character":
            forged.context.character.model_copy(update={"character_id": "cafe.merchant"})})})
        self.assertEqual(self.resolver.resolve(forged).status, "UNAUTHORIZED")
        missing = cafe_request().model_copy(update={"recall_identity": None})
        self.assertEqual(self.resolver.resolve(missing).items, ())

    def test_id_structured_filter_text_topk_and_complete_payload_budget(self):
        result = self.search(knowledge_ids=("coffee.latte",), attributes={"milk": True}, knowledge_type="drink")
        self.assertEqual(result.items[0]["knowledge_id"], "coffee.latte")
        self.assertEqual(self.search(knowledge_ids=("coffee.latte",), attributes={"milk": False}).items, ())
        result = self.search(text="卡布奇诺", top_k=1, token_budget=600)
        self.assertEqual(result.items[0]["knowledge_id"], "coffee.cappuccino")
        self.assertLessEqual(ContextTokenEstimator().estimate_text(result.serialized()), 600)
        self.assertEqual(self.search(text="卡布奇诺", token_budget=1).items, ())
        # Default backgrounds now mention weather as an explicit knowledge boundary.
        # Lexical retrieval may match that bigram; this case tests an absent term.
        self.assertEqual(self.search(text="qxzunmatchedtoken").items, ())
        self.assertEqual(self.search().items, ())

    def test_hidden_related_id_is_not_exposed(self):
        visible = self.entries[0].model_copy(update={"related_ids": ("cafe.staff.taste_guidance",)})
        customer = self.resolver.deployment.resolve(cafe_request(role="regular_customer"))
        result = retrieve_knowledge((visible, *self.entries[1:]), customer,
            KnowledgeQuery(knowledge_ids=(visible.knowledge_id,)), bundle_version="test", revision=0)
        self.assertEqual(result.items[0]["related_ids"], [])

    def test_invalid_documents_fail_closed_and_do_not_fallback_to_deleted_defaults(self):
        for raw in ('{"revision":0,"revision":1}', '{"schema_version":99}', '{broken',
                    '{"entries":' + "[" * 1100):
            self.path.write_text(raw, encoding="utf-8")
            self.assertEqual(self.resolver.resolve(cafe_request()).status, "UNAVAILABLE")
            with self.assertRaises(ValueError):
                self.store.manage("restore-all", expected_revision=0)
            self.assertEqual(self.path.read_text(encoding="utf-8"), raw)
        duplicate = KnowledgeBundle.model_dump(self.bundle)
        duplicate["entries"] = (duplicate["entries"][0], duplicate["entries"][0])
        with self.assertRaises(ValueError):
            KnowledgeBundle.model_validate(duplicate)

    def test_new_run_reads_local_edits_but_old_context_remains_snapshot(self):
        request = cafe_request("coffee.latte")
        before = self.resolver.resolve(request)
        entry = next(e for e in self.entries if e.knowledge_id == "coffee.latte")
        self.store.manage("upsert", entry=entry.model_copy(update={"content": "本机新版本说明。"}), expected_revision=0)
        after = self.resolver.resolve(request)
        self.assertNotEqual(before.items[0]["content"], after.items[0]["content"])
        self.assertEqual(after.revision, 1)

    def test_related_knowledge_can_replace_optional_history_but_keeps_current_authority(self):
        request = cafe_request()
        context = self.resolver.resolve(request)
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context=context, knowledge_context_limit=16384)
        self.assertIn("WORLD KNOWLEDGE CONTEXT", model.messages[0].content)
        self.assertIn('"not_personal_memory":true', model.messages[-1].content)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(model.model_dump(mode="json"), ensure_ascii=False, separators=(",", ":"))),
                             ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        large_dialogue = [{"text": "大量对话" * 3000}]
        base = CognitiveModelRequestFactory.create_event(request, recent_dialogue=large_dialogue, knowledge_context_limit=16384)
        extended = CognitiveModelRequestFactory.create_event(request, recent_dialogue=large_dialogue,
                                                             knowledge_context=context, knowledge_context_limit=16384)
        self.assertIn("Knowledge context JSON:", extended.messages[-1].content)
        self.assertNotIn("大量对话", extended.messages[-1].content)
        self.assertEqual(CognitiveModelRequestFactory.context_from(base), CognitiveModelRequestFactory.context_from(extended))
        self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(extended.model_dump(mode="json"), ensure_ascii=False, separators=(",", ":"))),
                             ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)
        with self.assertRaises(ContextBudgetExceeded):
            CognitiveModelRequestFactory.create_event(request, knowledge_context=context, knowledge_context_limit=4096)

    def test_official_cafev2_persona_has_room_for_drink_knowledge(self):
        request = official_merchant_request("卡布奇诺和拿铁的奶泡有什么区别？我们店现在有库存吗，做好了吗？")
        context = self.resolver.resolve(request)
        model = CognitiveModelRequestFactory.create_event(request, knowledge_context=context, knowledge_context_limit=16384)
        self.assertIn("Knowledge context JSON:", model.messages[-1].content)
        self.assertIn("coffee.cappuccino", model.messages[-1].content)
        self.assertIn("coffee.latte", model.messages[-1].content)
        self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(model.model_dump(mode="json"), ensure_ascii=False, separators=(",", ":"))),
                             ContextBudgetPolicy.cognition(16384, 1024).input_hard_cap)


class KnowledgeCognitionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "knowledge.json"
        self.resolver = WorldKnowledgeResolver(WorldKnowledgeStore(self.path),
            WorldKnowledgeResolver.from_environment().deployment)

    async def test_one_inference_uses_knowledge_and_failure_still_answers(self):
        def answer(request):
            self.assertIn("coffee.cappuccino", request.messages[-1].content)
            return {"decision_kind": "DIRECT_RESPONSE", "utterance": "卡布奇诺通常奶泡较丰富；这是饮品说明，不代表本店现在有货。"}
        provider = ScriptedProvider([answer, TERMINAL])
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), knowledge_resolver=self.resolver, context_length=16384)
        result = await entry.run(cafe_request())
        self.assertIn("奶泡", result.decision.utterance)
        self.path.write_text("invalid", encoding="utf-8")
        result = await entry.run(cafe_request())
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertEqual(len(provider.requests), 2)
        self.assertNotIn("Knowledge context JSON:", provider.requests[1].messages[-1].content)

    async def test_existing_memory_once_contract_keeps_same_knowledge_snapshot(self):
        # Existing M3 fixture exercises the canonical retrieval adapter with one read.
        fixture = memory_fixtures.MemoryD2Tests()
        entry = fixture.entry([recall, TERMINAL])
        request = cognitive_request("coffee.latte")
        owner = request.recall_identity.observer
        deployment = KnowledgeDeployment(deployment_id="cafedemo-v2", bindings=[dict(
            agent_entity_id=request.run.agent_entity_id, character_id=request.context.character.character_id,
            identity_namespace=owner.identity_namespace, identity_type=owner.identity_type, persistent_id=owner.persistent_id)])
        embeddings = TinyEmbeddings()
        index = KnowledgeEmbeddingIndex(self.resolver.store, Path(self.temporary.name) / "knowledge.sqlite3")
        await KnowledgeEmbeddingIndexer(index, embeddings).index_missing_or_stale()
        entry._knowledge_resolver = WorldKnowledgeResolver(self.resolver.store, deployment,
                                                         KnowledgeHybridRanker(index, embeddings))
        await entry.run(request)
        self.assertEqual(len(fixture.rows.calls), 1)
        self.assertEqual(len(fixture.provider.requests), 2)
        self.assertIn("MEMORY_RECALL", json.dumps(fixture.provider.requests[0].response_schema))
        self.assertNotIn("MEMORY_RECALL", json.dumps(fixture.provider.requests[1].response_schema))
        # Step 1 may trim optional knowledge when memory/current context consumes the cap.
        for model in fixture.provider.requests:
            self.assertNotIn(owner.persistent_id, "\n".join(m.content for m in model.messages))

    async def test_knowledge_cannot_authorize_action_or_supply_runtime_entities(self):
        proposal = _decision().model_dump()
        entry = CognitiveBackendEntry(ScriptedProvider([proposal]), PrioritizedModelExecutor(),
                                      knowledge_resolver=self.resolver, context_length=16384)
        with self.assertRaises(ActionProposalValidationError):
            await entry.run(cafe_request("请给我一杯拿铁"))
        request = cafe_request()
        event = request.context.current_event.model_copy(update={"event_type": "entity.became_visible", "payload": ()})
        request = request.model_copy(update={"context": request.context.model_copy(update={"current_event": event})})
        entry = CognitiveBackendEntry(ScriptedProvider([proposal]), PrioritizedModelExecutor(),
                                      knowledge_resolver=self.resolver, context_length=16384)
        with self.assertRaises(WorldActionNotEligibleError):
            await entry.run(request)


class TinyEmbeddings:
    model_key = "test-model-A"
    dimensions = 3

    def __init__(self):
        self.documents, self.queries = [], []
        self.fail = False

    def embed(self, text):
        self.documents.append(text)
        if self.fail:
            raise OSError("test unavailable")
        if "Knowledge ID: coffee.cappuccino\n" in text:
            return (1, 0, 0)
        if "Knowledge ID: coffee.latte\n" in text:
            return (0.6, 0.8, 0)
        return (0, 0, 1)

    def embed_query(self, text):
        self.queries.append(text)
        if self.fail:
            raise OSError("test unavailable")
        return (1, 0, 0)


class KnowledgeHybridTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.store = WorldKnowledgeStore(root / "knowledge.json")
        # Keep the existing four-document index fixture independent of content expansion.
        # P4-B's full catalog and access scopes are checked by KnowledgeTests above.
        bundle, _, entries = self.store.snapshot()
        baseline_ids = {"cafe.setting", "coffee.latte", "coffee.cappuccino", "cafe.staff.taste_guidance"}
        builtin = root / "four-document-fixture.json"
        builtin.write_text(bundle.model_copy(update={"entries": tuple(e for e in entries
            if e.knowledge_id in baseline_ids)}).model_dump_json(), encoding="utf-8")
        self.store = WorldKnowledgeStore(root / "knowledge.json", builtin_path=builtin)
        self.index = KnowledgeEmbeddingIndex(self.store, root / "knowledge-index.sqlite3")
        self.provider = TinyEmbeddings()
        self.indexer = KnowledgeEmbeddingIndexer(self.index, self.provider)
        self.ranker = KnowledgeHybridRanker(self.index, self.provider)
        self.deployment = WorldKnowledgeResolver.from_environment().deployment
        self.principal = self.deployment.resolve(cafe_request())

    def search(self, principal=None, **kwargs):
        bundle, overrides, entries = self.store.snapshot()
        return retrieve_knowledge(entries, principal or self.principal, KnowledgeQuery(**kwargs),
            bundle_version=bundle.bundle_version, revision=overrides.revision, ranker=self.ranker)

    async def test_semantic_candidates_fuse_with_lexical_exact_filters_and_budget(self):
        self.assertEqual(await self.indexer.index_missing_or_stale(), 4)
        result = self.search(text="an airy cushion", top_k=1)
        self.assertEqual(result.items[0]["knowledge_id"], "coffee.cappuccino")
        self.assertEqual(len(self.provider.documents), 4)  # Not final Top-K=1.
        fused = self.search(text="拿铁 an airy cushion")
        self.assertEqual(fused.items[0]["knowledge_id"], "coffee.latte")
        exact = self.search(text="unrelated words", knowledge_ids=("coffee.latte",), attributes={"milk": True})
        self.assertEqual([i["knowledge_id"] for i in exact.items], ["coffee.latte"])
        self.assertEqual(self.search(text="an airy cushion", attributes={"milk": False}).items, ())
        self.assertEqual(self.search(text="an airy cushion", token_budget=1).items, ())
        self.assertLessEqual(ContextTokenEstimator().estimate_text(fused.serialized()), 1000)

    async def test_permissions_precede_vector_reads_and_hidden_results(self):
        bundle, _, entries = self.store.snapshot()
        staff = next(e for e in entries if e.knowledge_id == "cafe.staff.taste_guidance")
        scoped = staff.model_copy(update={"knowledge_id": "private.scene",
            "access": staff.access.model_copy(update={"scene_ids": ("private-room",)})})
        self.store.manage("upsert", expected_revision=0, entry=scoped)
        await self.indexer.index_missing_or_stale()
        # Give both restricted entries the closest possible vector.
        bundle, _, entries = self.store.snapshot()
        for entry in entries:
            if entry.knowledge_id in (staff.knowledge_id, scoped.knowledge_id):
                self.index.store_if_current(entry, bundle.bundle_version, knowledge_space(self.provider), (1, 0, 0))
        seen = []
        load = self.index.load_valid
        def recording_load(allowed, *args):
            seen.extend(e.knowledge_id for e in allowed)
            return load(allowed, *args)
        self.index.load_valid = recording_load
        customer = self.deployment.resolve(cafe_request(role="regular_customer"))
        result = self.search(customer, text="an airy cushion")
        for hidden in (staff.knowledge_id, scoped.knowledge_id):
            self.assertNotIn(hidden, seen)
            self.assertNotIn(hidden, result.serialized())
        self.assertNotIn(scoped.knowledge_id, [i["knowledge_id"] for i in self.search(text="an airy cushion").items])
        other_deployment = customer.model_copy(update={"deployment_id": "other"})
        self.assertEqual(self.search(other_deployment, text="an airy cushion").items, ())

    async def test_incremental_add_edit_delete_restore_and_no_per_request_document_encodes(self):
        await self.indexer.index_missing_or_stale()
        self.assertEqual(await self.indexer.index_missing_or_stale(), 0)
        self.search(text="an airy cushion")
        self.search(text="an airy cushion")
        self.assertEqual(len(self.provider.documents), 4)
        entry = next(e for e in self.store.snapshot()[2] if e.knowledge_id == "coffee.cappuccino")
        added = entry.model_copy(update={"knowledge_id": "coffee.custom"})
        self.store.manage("upsert", expected_revision=0, entry=added)
        self.assertEqual(await self.indexer.index_missing_or_stale(), 1)
        self.store.manage("upsert", expected_revision=1, entry=entry.model_copy(update={"content": "更新后的说明。"}))
        self.assertNotIn(entry.knowledge_id, [i["knowledge_id"] for i in self.search(text="an airy cushion").items])
        self.assertEqual(await self.indexer.index_missing_or_stale(), 1)
        self.assertEqual(self.search(text="an airy cushion").items[0]["content"], "更新后的说明。")
        self.store.manage("delete", expected_revision=2, knowledge_id=entry.knowledge_id)
        self.assertNotIn(entry.knowledge_id, [i["knowledge_id"] for i in self.search(text="an airy cushion").items])
        await self.indexer.index_missing_or_stale()
        self.store.manage("restore", expected_revision=3, knowledge_id=entry.knowledge_id)
        self.assertEqual(await self.indexer.index_missing_or_stale(), 1)
        self.assertEqual(self.search(text="an airy cushion").items[0]["content"], entry.content)
        self.store.manage("restore-all", expected_revision=4)
        await self.indexer.index_missing_or_stale()
        with closing(self.index.connect()) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM knowledge_embeddings").fetchone()[0], 4)

    async def test_partial_index_provider_failure_model_switch_and_dimension_fallback(self):
        await self.indexer.index_missing_or_stale(limit=1)
        self.assertEqual(self.search(text="拿铁").items[0]["knowledge_id"], "coffee.latte")
        await self.indexer.index_missing_or_stale()
        self.provider.fail = True
        self.assertEqual(self.search(text="拿铁").items[0]["knowledge_id"], "coffee.latte")
        self.assertEqual(self.search(text="an airy cushion").items, ())
        self.provider.fail = False
        self.provider.model_key = "test-model-B"
        self.assertEqual(self.search(text="an airy cushion").items, ())
        self.assertEqual(await self.indexer.index_missing_or_stale(), 4)
        self.assertEqual(self.search(text="an airy cushion").items[0]["knowledge_id"], "coffee.cappuccino")
        self.provider.dimensions = 4
        self.assertEqual(self.search(text="an airy cushion").items, ())
        self.assertEqual(await self.indexer.index_missing_or_stale(), 0)  # Encoder dimension mismatch.

    async def test_stale_background_result_and_wrong_database_cannot_be_published(self):
        entry = self.store.snapshot()[2][0]
        version = self.store.snapshot()[0].bundle_version
        self.store.manage("delete", expected_revision=0, knowledge_id=entry.knowledge_id)
        self.assertFalse(self.index.store_if_current(entry, version, knowledge_space(self.provider), (1, 0, 0)))
        with closing(sqlite3.connect(self.index.path)) as db, db:
            db.execute("CREATE TABLE memory_records(id TEXT)")
        before = self.index.path.read_bytes()
        self.assertEqual(self.search(text="拿铁").items[0]["knowledge_id"], "coffee.latte")
        self.assertEqual(before, self.index.path.read_bytes())

    async def test_shared_embedding_gate_and_worker_lifecycle(self):
        from app.embedding_index_control import EmbeddingControl
        gate = EmbeddingControl(self.provider, Path(self.temporary.name) / "gate.json", cloud=True)
        worker = KnowledgeEmbeddingIndexer(self.index, gate, interval_seconds=1)
        self.assertEqual(await worker.index_missing_or_stale(), 0)
        self.assertEqual(self.provider.documents, [])
        with self.assertRaises(ValueError):
            gate.command("start")
        gate.command("start", confirm=True)
        self.assertEqual(await worker.index_missing_or_stale(), 4)
        gate.command("pause")
        query_count = len(self.provider.queries)
        ranker = KnowledgeHybridRanker(self.index, gate)
        self.assertEqual(ranker.rank(self.store.snapshot()[2], KnowledgeQuery(text="an airy cushion"),
            bundle_version=self.store.snapshot()[0].bundle_version), ())
        self.assertEqual(query_count, len(self.provider.queries))
        gate.command("resume")
        worker.start()
        await asyncio.sleep(0)
        await worker.stop()
        self.assertIsNone(worker._task)

    async def test_long_documents_do_not_fail_shared_gate_or_starve_short_entries(self):
        from app.builtin_embedding_provider import BuiltinEmbeddingProvider
        from app.embedding_index_control import EmbeddingControl
        class CountingBuiltin(BuiltinEmbeddingProvider):
            calls = 0
            def embed(self, text):
                self.calls += 1
                return (1,) * 768
        raw = CountingBuiltin(Path(self.temporary.name))
        gate = EmbeddingControl(raw, Path(self.temporary.name) / "long-gate.json", cloud=False)
        gate.command("start")
        original = self.store.snapshot()[2][0]
        for revision in range(9):
            entry = original.model_copy(update={"knowledge_id": f"a.long.{revision}",
                "content": "长" * 2000, "attributes": {"detail": "长" * 1900}})
            self.store.manage("upsert", expected_revision=revision, entry=entry)
        worker = KnowledgeEmbeddingIndexer(self.index, gate)
        self.assertEqual(await worker.index_missing_or_stale(), 4)
        self.assertEqual(raw.calls, 4)
        self.assertEqual(gate.mode, "running")


if __name__ == "__main__":
    unittest.main()
