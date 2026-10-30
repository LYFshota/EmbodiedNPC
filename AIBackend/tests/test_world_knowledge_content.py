"""Default content acceptance; native Store/Resolver, private UserData, no real model."""
import json
import re
import tempfile
import unittest
from pathlib import Path

from app.contracts import CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.language_projection import english_character
from tests.test_memory_d2_cognitive_steps import ScriptedProvider, TERMINAL
from app.context_budget_allocator import ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.model_requests import CognitiveModelRequestFactory
from app.world_knowledge import KnowledgeBundle, KnowledgeDeployment, KnowledgeQuery, retrieve_knowledge
from app.world_knowledge_store import WorldKnowledgeStore, WorldKnowledgeResolver, read_document
from tests.test_world_knowledge import cafe_request, TinyEmbeddings
from app.knowledge_embeddings import KnowledgeEmbeddingIndex, KnowledgeEmbeddingIndexer, KnowledgeHybridRanker

ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / "AIBackend/app/knowledge/cafedemo_v2.json"
DEPLOYMENT = ROOT / "AIBackend/app/knowledge/cafedemo_v2_deployment.json"
# Independent content expectations, not generated from DrinkType or the catalog under test.
DRINKS = {
    "latte": ("coffee.latte", "拿铁"), "cappuccino": ("coffee.cappuccino", "卡布奇诺"),
    "americano": ("coffee.americano", "美式"), "espresso": ("coffee.espresso", "浓缩咖啡"),
    "flat_white": ("coffee.flat_white", "馥芮白"), "mocha": ("coffee.mocha", "摩卡"),
    "caramel_macchiato": ("coffee.caramel_macchiato", "焦糖玛奇朵"),
    "hot_chocolate": ("drink.hot_chocolate", "热巧克力"),
    "matcha_latte": ("drink.matcha_latte", "抹茶拿铁"), "black_tea": ("drink.black_tea", "红茶"),
}
COFFEE_TOPICS = {"coffee.species", "coffee.origins", "coffee.processing", "coffee.roasting",
    "coffee.grind_extraction", "coffee.brew_methods", "coffee.flavor_language", "coffee.acidity",
    "coffee.milk_texture", "coffee.caffeine_myths", "coffee.strength_vs_extraction"}


def bound_request(binding, text):
    original = cafe_request(text)
    data = original.model_dump(mode="json")
    data["recall_identity"] = original.recall_identity.model_dump(mode="json")
    agent = binding.agent_entity_id
    data["run"]["agent_entity_id"] = agent
    data["trigger"]["agent_entity_id"] = agent
    data["context"]["agent_entity_id"] = agent
    data["context"]["trigger"]["agent_entity_id"] = agent
    data["context"]["perception"]["observer_entity_id"] = agent
    data["context"]["current_event"]["target_entity_id"] = agent
    data["context"]["character"]["character_id"] = binding.character_id
    data["recall_identity"]["observer"].update(entity_id=agent,
        identity_namespace=binding.identity_namespace, identity_type=binding.identity_type,
        persistent_id=binding.persistent_id)
    # Exercise budgets using the actual four default personas, not abbreviated test text.
    fields = {"characterId": "character_id", "displayName": "display_name", "roleSummary": "role_summary",
        "backstory": "backstory", "personality": "personality", "speakingStyle": "speaking_style", "boundaries": "boundaries"}
    for asset in (ROOT / "UnityClient/Assets/CafeDemo/V2/Characters/Personas").glob("*.asset"):
        source = asset.read_text(encoding="utf-8-sig")
        profile = {}
        for key, dest in fields.items():
            raw = re.search(r"^  " + key + r": (.+)$", source, re.M)[1]
            profile[dest] = json.loads(raw) if raw.startswith('"') else raw
        if profile["character_id"] == binding.character_id:
            data["context"]["character"] = profile
            break
    return CognitiveRequest.model_validate(data)


class ContentTests(unittest.IsolatedAsyncioTestCase):
    def test_internal_sources_preserve_semantic_embedding_fingerprints(self):
        from app.knowledge_embeddings import content_hash
        entries = read_document(TEMPLATE, KnowledgeBundle).entries
        self.assertEqual(108, len(entries))
        for entry in entries:
            self.assertEqual("builtin", entry.source)
            archived = entry.model_copy(update={"source": "Authoring reference"})
            self.assertEqual(content_hash(entry, "current"), content_hash(archived, "previous"))

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = WorldKnowledgeStore(Path(self.tmp.name) / "current.json", builtin_path=TEMPLATE)
        self.store.initialize()
        self.bundle, self.current, self.entries = self.store.snapshot()
        self.by_id = {e.knowledge_id: e for e in self.entries}
        self.deployment = read_document(DEPLOYMENT, KnowledgeDeployment)
        self.resolver = WorldKnowledgeResolver(self.store, self.deployment)

    def search(self, query, role="cafe.merchant"):
        binding = next(b for b in self.deployment.bindings if b.character_id == role)
        return self.resolver.resolve(bound_request(binding, query))

    def test_defaults_validate_ids_references_and_distinct_coffee_topics(self):
        self.assertEqual(self.current.schema_version, 2)
        self.assertEqual(len(self.entries), len(self.by_id))
        self.assertTrue(COFFEE_TOPICS <= self.by_id.keys())
        for entry in self.entries:
            self.assertTrue(set(entry.related_ids) <= self.by_id.keys(), entry.knowledge_id)
        for key in COFFEE_TOPICS:
            entry = self.by_id[key]
            self.assertEqual((entry.access.mode, entry.usage), ("public", "retrieval"))
            self.assertFalse(entry.access.character_ids)
            self.assertEqual("builtin", entry.source)

    def test_ten_drinks_retrieve_complete_actual_records_by_name_and_stable_id(self):
        for drink, (key, name) in DRINKS.items():
            entry = self.by_id[key]
            self.assertEqual(entry.attributes["drink"], drink)
            self.assertEqual((entry.access.mode, entry.usage), ("public", "retrieval"))
            self.assertIn("咖啡因", entry.content)
            for query in (name, drink):
                with self.subTest(query=query):
                    result = self.search(query)
                    found = {item["knowledge_id"]: item for item in result.items}
                    self.assertIn(key, found)
                    self.assertEqual(found[key]["content"], entry.content)
                    self.assertTrue(result.not_action_authorization and result.not_current_world_state)

    def test_followup_coffee_questions_select_the_relevant_complete_facts(self):
        cases = [
            ("我不太喜欢苦的，有什么适合我的？", {"drink.preference_guidance"}),
            ("馥芮白和拿铁有什么区别？", {"coffee.flat_white", "coffee.latte"}),
            ("喝咖啡一定会很酸吗？", {"coffee.acidity"}),
            ("水洗和日晒哪个更甜？", {"coffee.processing"}),
            ("那蜜处理是加了蜂蜜吗？", {"coffee.processing"}),
            ("罗布斯塔就一定比阿拉比卡差吗？", {"coffee.species"}),
            ("那深烘的咖啡因是不是更多？", {"coffee.roasting", "coffee.caffeine_myths"}),
            ("冷萃就是加冰的手冲吗？", {"coffee.brew_methods"}),
        ]
        for query, expected in cases:
            with self.subTest(query=query):
                result = self.search(query)
                found = {item["knowledge_id"]: item for item in result.items}
                self.assertTrue(expected <= found.keys(), (query, list(found)))
                for key in expected:
                    self.assertEqual(found[key]["content"], self.by_id[key].content)
                self.assertLessEqual(len(result.items), 3)

    def test_private_histories_keep_every_original_character_scope(self):
        for binding in self.deployment.bindings:
            principal = self.deployment.resolve(bound_request(binding, "背景"))
            for entry in self.entries:
                if entry.access.mode != "restricted":
                    continue
                result = retrieve_knowledge(self.entries, principal,
                    KnowledgeQuery(knowledge_ids=(entry.knowledge_id,)),
                    bundle_version=self.bundle.bundle_version, revision=self.current.revision, defer_budget=True)
                self.assertEqual(bool(result.items), binding.character_id in entry.access.character_ids,
                    (binding.character_id, entry.knowledge_id))
        # Four witnesses do not make their limited shared history public.
        event = self.by_id["event.bookmark_afternoon"]
        self.assertEqual(event.access.mode, "restricted")
        self.assertEqual(len(event.access.character_ids), 4)

    def test_new_trusted_npcs_see_public_drinks_without_inheriting_private_history(self):
        raw = self.deployment.model_dump(mode="json")
        raw["bindings"] += [dict(agent_entity_id=f"npc_new_{i}", character_id=f"cafe.new_{i}",
            identity_namespace="npc", identity_type="Character", persistent_id=f"phase4.new_{i}.001") for i in (5, 6)]
        deployment = KnowledgeDeployment.model_validate(raw)
        resolver = WorldKnowledgeResolver(self.store, deployment)
        for binding in deployment.bindings[-2:]:
            for key, name in DRINKS.values():
                result = resolver.resolve(bound_request(binding, name))
                self.assertIn(key, {i["knowledge_id"] for i in result.items})
                self.assertTrue(all(self.by_id[i["knowledge_id"]].access.mode == "public" for i in result.items))
            principal = deployment.resolve(bound_request(binding, "书铺"))
            for entry in self.entries:
                if entry.access.mode == "restricted":
                    self.assertFalse(retrieve_knowledge(self.entries, principal,
                        KnowledgeQuery(knowledge_ids=(entry.knowledge_id,)),
                        bundle_version=self.bundle.bundle_version, revision=0).items)
            # Same textual CharacterId without trusted identity binding grants nothing.
            original = bound_request(binding, "拿铁")
            forged = original.model_dump(mode="json")
            forged["recall_identity"] = original.recall_identity.model_dump(mode="json")
            forged["recall_identity"]["observer"]["persistent_id"] = "unregistered-instance"
            self.assertEqual(resolver.resolve(CognitiveRequest.model_validate(forged)).status, "UNAUTHORIZED")
            self.assertEqual(self.resolver.resolve(bound_request(binding, "拿铁")).status, "UNAUTHORIZED")

    def test_new_defaults_do_not_merge_into_an_existing_full_player_catalog(self):
        template = Path(self.tmp.name) / "old-template.json"
        old = (self.by_id["coffee.latte"].model_copy(update={"related_ids": ()}),)
        template.write_text(KnowledgeBundle(bundle_version="old-install", entries=old).model_dump_json(), encoding="utf-8")
        current = Path(self.tmp.name) / "old-player.json"
        store = WorldKnowledgeStore(current, builtin_path=template)
        store.initialize()
        custom = old[0].model_copy(update={"knowledge_id": "player.custom", "content": "玩家自己编辑的知识"})
        store.manage("upsert", expected_revision=0, entry=custom)
        store.manage("delete", expected_revision=1, knowledge_id="coffee.latte")
        before = current.read_bytes()
        template.write_bytes(TEMPLATE.read_bytes())
        restarted = WorldKnowledgeStore(current, builtin_path=template)
        restarted.initialize()
        self.assertEqual([e.knowledge_id for e in restarted.snapshot()[2]], ["player.custom"])
        self.assertEqual(current.read_bytes(), before)
        template.unlink()
        restarted.initialize()
        self.assertEqual(current.read_bytes(), before)
        self.assertEqual(restarted.snapshot()[1].revision, 2)
        self.assertEqual(len(self.entries), len(read_document(TEMPLATE, KnowledgeBundle).entries))

    def test_world_followups_and_private_bookshop_disagreement(self):
        query = "你们这条街有什么特别的地方？"
        result = self.search(query)
        self.assertIn("neighborhood.qingshi_street", {i["knowledge_id"] for i in result.items})
        question = "予安和知意以前为什么在书铺那边有分歧？"
        for role in ("cafe.regular_customer", "cafe.companion_customer"):
            result = self.search(question, role)
            found = {i["knowledge_id"]: i for i in result.items}
            self.assertIn("event.bookshop_route_choice", found)
            self.assertEqual(found["event.bookshop_route_choice"]["content"], self.by_id["event.bookshop_route_choice"].content)
        for role in ("cafe.merchant", "cafe.quiet_customer"):
            result = self.search(question, role)
            self.assertNotIn("event.bookshop_route_choice", result.serialized())
            self.assertNotIn("event.route_landmark", result.serialized())
            self.assertNotIn("event.route_return_plan", result.serialized())

    async def test_short_constant_full_personas_and_fake_cognition_stay_within_normal_budget(self):
        constants = [e for e in self.entries if e.usage == "constant"]
        self.assertEqual([e.knowledge_id for e in constants], ["cafe.setting"])
        self.assertLess(len(constants[0].content), 120)
        self.assertEqual(constants[0].access.mode, "public")
        for binding in self.deployment.bindings:
            request = bound_request(binding, "馥芮白和拿铁有什么区别？")
            context = self.resolver.resolve(request)
            self.assertEqual([i["knowledge_id"] for i in context.constant_items], ["cafe.setting"])
            self.assertNotIn("cafe.setting", {i["knowledge_id"] for i in context.items})
            self.assertEqual(len(context.items), 3)
            base = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=32768)
            constant_model = CognitiveModelRequestFactory.create_event(request,
                knowledge_context=context.model_copy(update={"items": ()}), knowledge_context_limit=32768)
            self.assertEqual(base.messages[0], constant_model.messages[0])
            self.assertEqual(base.response_schema, constant_model.response_schema)
            provider = ScriptedProvider([TERMINAL])
            backend = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                knowledge_resolver=self.resolver, context_length=32768)
            await backend.run(request)
            self.assertEqual(len(provider.requests), 1)
            model = provider.requests[0]
            wire = model.model_dump(mode="json")
            self.assertLessEqual(ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(",", ":"))),
                ContextBudgetPolicy.cognition(32768, 1024).input_hard_cap)
            knowledge = json.loads(next(line.split(": ", 1)[1] for line in model.messages[-1].content.splitlines()
                if line.startswith("Knowledge context JSON: ")))
            self.assertEqual(knowledge["constant_items"][0]["content"], constants[0].content)
            self.assertEqual(len(knowledge["items"]), 3)
            self.assertTrue(knowledge["not_action_authorization"] and knowledge["not_personal_memory"])

    def test_canonical_english_place_names_project_without_overwriting_custom_personas(self):
        for binding in self.deployment.bindings:
            profile = bound_request(binding, "hello").context.character.model_dump(mode="json")
            projected = english_character(profile)
            self.assertNotEqual(projected, profile)
            self.assertIn("Stilllight Café", projected["backstory"])
            self.assertNotIn("Qiguang Café", projected["backstory"])
            self.assertNotIn("Paper Page Bookshop", projected["backstory"])
            if binding.character_id != "cafe.merchant":
                self.assertIn("Paperleaf Bookshop", projected["backstory"])
            custom = {**profile, "backstory": profile["backstory"] + "这是用户自己的补充。"}
            self.assertEqual(english_character(custom), custom)

    async def test_fake_index_encodes_retrieval_only_and_provider_failure_keeps_lexical_access(self):
        index = KnowledgeEmbeddingIndex(self.store, Path(self.tmp.name) / "index.sqlite3")
        provider = TinyEmbeddings()
        worker = KnowledgeEmbeddingIndexer(index, provider)
        total = 0
        while count := await worker.index_missing_or_stale(limit=16):
            total += count
        self.assertEqual(total, sum(e.usage == "retrieval" for e in self.entries))
        self.assertFalse(any("Knowledge ID: cafe.setting\n" in s for s in provider.documents))
        provider.fail = True
        resolver = WorldKnowledgeResolver(self.store, self.deployment, KnowledgeHybridRanker(index, provider))
        for drink, (key, name) in DRINKS.items():
            result = resolver.resolve(cafe_request(name))
            self.assertIn(key, {i["knowledge_id"] for i in result.items})
