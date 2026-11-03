"""Food knowledge reaches all four NPCs without granting service authority; offline only."""
import json
import tempfile
import unittest
from pathlib import Path

from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.snack_catalog import SNACKS
from app.world_knowledge import KnowledgeDeployment
from app.world_knowledge_store import WorldKnowledgeResolver, WorldKnowledgeStore, read_document
from tests.test_memory_d2_cognitive_steps import ScriptedProvider, TERMINAL
from tests.test_world_knowledge_content import bound_request, TEMPLATE, DEPLOYMENT


class SnackKnowledgeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.store = WorldKnowledgeStore(Path(temporary.name) / "knowledge.json", builtin_path=TEMPLATE)
        self.store.initialize()
        self.entries = {e.knowledge_id: e for e in self.store.snapshot()[2]}
        self.deployment = read_document(DEPLOYMENT, KnowledgeDeployment)
        self.resolver = WorldKnowledgeResolver(self.store, self.deployment)

    def test_food_records_match_the_single_service_catalog(self):
        food = {e.attributes.get("snack_type"): e for e in self.entries.values()
                if "snack_type" in e.attributes}
        self.assertEqual(set(food), {s["id"] for s in SNACKS})
        for snack in SNACKS:
            entry = food[snack["id"]]
            self.assertIn(snack["chinese"], entry.name)
            self.assertIn(snack["english"].casefold(), {s.casefold() for s in entry.keywords})
            self.assertEqual((entry.access.mode, entry.usage), ("public", "retrieval"))
            self.assertIn(snack["chinese"], self.entries["cafe.setting"].content)
        self.assertLess(len(self.entries["cafe.setting"].content), 120)

    def test_each_food_can_be_retrieved_by_all_four_characters(self):
        for binding in self.deployment.bindings:
            for snack in SNACKS:
                with self.subTest(character=binding.character_id, snack=snack["id"]):
                    context = self.resolver.resolve(bound_request(binding, snack["chinese"]))
                    self.assertIn("food." + snack["id"], {i["knowledge_id"] for i in context.items})
                    self.assertTrue(context.not_current_world_state and context.not_action_authorization)

    async def test_menu_and_recommendations_reach_actual_provider_payload_without_actions(self):
        for binding in self.deployment.bindings:
            for question in ("店里有什么点心？", "有什么推荐的点心吗", "有没有不太甜又柔软的吃的？"):
                with self.subTest(character=binding.character_id, question=question):
                    request = bound_request(binding, question)
                    original = request.context.capabilities
                    provider = ScriptedProvider([TERMINAL])
                    result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                        knowledge_resolver=self.resolver, context_length=32768).run(request)
                    self.assertEqual(len(provider.requests), 1)
                    wire = provider.requests[0].messages[-1].content
                    context = json.loads(next(line.split(": ", 1)[1] for line in wire.splitlines()
                        if line.startswith("Knowledge context JSON: ")))
                    menu = next(e for e in context["constant_items"] if e["knowledge_id"] == "cafe.setting")
                    for snack in SNACKS:
                        self.assertIn(snack["chinese"], menu["content"])
                    self.assertTrue(context["not_current_world_state"] and context["not_action_authorization"])
                    self.assertIsNone(result.validated_goal)
                    self.assertEqual(request.context.capabilities, original)

    def test_unknown_recipe_and_supply_boundaries_are_available_with_recommendations(self):
        for binding in self.deployment.bindings:
            context = self.resolver.resolve(bound_request(binding, "点心推荐和搭配，有价格、库存和过敏原说明吗？"))
            food = next(i for i in context.items if i["knowledge_id"] == "food.recommendation")
            for term in ("实时余量", "过敏原", "未知", "顾客", "已交付"):
                self.assertIn(term, food["content"])

    def test_existing_user_catalog_is_never_replaced_when_template_changes(self):
        baseline = self.store.path.read_bytes()
        WorldKnowledgeStore(self.store.path, builtin_path=Path("nonexistent-template.json")).initialize()
        self.assertEqual(self.store.path.read_bytes(), baseline)
