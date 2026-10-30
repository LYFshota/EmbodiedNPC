"""Stage 2 permissions and usage, isolated catalogs and trusted deployment fixtures."""
import tempfile
import unittest
from pathlib import Path
from pydantic import ValidationError
from app.world_knowledge import (KnowledgeAccess, KnowledgeEntry, KnowledgeBundle, KnowledgeDeployment,
    KnowledgeQuery, retrieve_knowledge)
from app.world_knowledge_store import WorldKnowledgeStore, WorldKnowledgeResolver
from tests.test_world_knowledge import cafe_request


def entry(key="public.reference", **changes):
    data = dict(knowledge_id=key, name="reference", content="reference fact", knowledge_type="reference",
        source="test", updated_at="2026-10-29T00:00:00Z",
        access=dict(mode="public", deployment_ids=["test"]))
    data.update(changes)
    return KnowledgeEntry.model_validate(data)


def request_for(i):
    base = cafe_request("reference")
    data = base.model_dump(mode="json")
    data["recall_identity"] = base.recall_identity.model_dump(mode="json")
    agent = f"npc_{i}"
    data["run"]["agent_entity_id"] = agent
    data["trigger"]["agent_entity_id"] = agent
    data["context"]["agent_entity_id"] = agent
    data["context"]["trigger"]["agent_entity_id"] = agent
    data["context"]["perception"]["observer_entity_id"] = agent
    data["context"]["current_event"]["target_entity_id"] = agent
    data["context"]["character"]["character_id"] = f"role_{i}"
    data["recall_identity"]["observer"]["entity_id"] = agent
    data["recall_identity"]["observer"]["persistent_id"] = f"instance_{i}"
    return type(cafe_request()).model_validate(data)


def deployment(count):
    bindings = []
    for i in range(count):
        request = request_for(i)
        owner = request.recall_identity.observer
        bindings.append(dict(agent_entity_id=request.run.agent_entity_id,
            character_id=request.context.character.character_id,
            identity_namespace=owner.identity_namespace, identity_type=owner.identity_type,
            persistent_id=owner.persistent_id))
    return KnowledgeDeployment(deployment_id="test", scene_id="scene", bindings=bindings)


def make_store(root, entries):
    template = Path(root) / "template.json"
    template.write_text(KnowledgeBundle(bundle_version="test", entries=entries).model_dump_json(), encoding="utf-8")
    store = WorldKnowledgeStore(Path(root) / "current.json", builtin_path=template)
    store.initialize()
    return store


class PermissionsUsageTests(unittest.TestCase):
    def test_legacy_permissions_are_restricted_and_usage_defaults_to_retrieval(self):
        old = entry(access=dict(character_ids=[f"role_{i}" for i in range(4)], deployment_ids=["test"]))
        self.assertEqual(old.access.mode, "restricted")
        self.assertEqual(old.usage, "retrieval")
        principal = deployment(6).resolve(request_for(5))
        self.assertEqual(retrieve_knowledge((old,), principal, KnowledgeQuery(text="reference"),
            bundle_version="test", revision=0).items, ())
        for access in (dict(mode="public", character_ids=["role_0"], deployment_ids=["test"]),
                       dict(mode="restricted", deployment_ids=["test"])):
            with self.assertRaises(ValidationError):
                KnowledgeAccess.model_validate(access)

    def test_one_four_six_npcs_public_private_and_removed_bindings(self):
        private = entry("private.reference", access=dict(character_ids=["role_0"], deployment_ids=["test"]))
        with tempfile.TemporaryDirectory() as root:
            store = make_store(root, (entry(), private))
            for count in (1, 4, 6):
                with self.subTest(npcs=count):
                    config = deployment(count)
                    resolver = WorldKnowledgeResolver(store, config)
                    for i in range(count):
                        result = resolver.resolve(request_for(i))
                        ids = {v["knowledge_id"] for v in result.items}
                        self.assertIn("public.reference", ids)
                        self.assertEqual("private.reference" in ids, i == 0)
                    removed = WorldKnowledgeResolver(store, config.model_copy(update={"bindings": config.bindings[1:]}))
                    self.assertEqual(removed.resolve(request_for(0)).status, "UNAUTHORIZED")
                    if count > 1:
                        self.assertEqual(removed.resolve(request_for(count-1)).items,
                            resolver.resolve(request_for(count-1)).items)

    def test_unbound_forged_identity_scene_and_deployment_remain_denied(self):
        with tempfile.TemporaryDirectory() as root:
            store = make_store(root, (entry(access=dict(mode="public", deployment_ids=["test"], scene_ids=["scene"])),))
            config = deployment(1)
            resolver = WorldKnowledgeResolver(store, config)
            self.assertEqual(resolver.resolve(request_for(9)).status, "UNAUTHORIZED")
            request = request_for(0)
            for forged in (request.model_copy(update={"recall_identity": None}),
                request.model_copy(update={"context": request.context.model_copy(update={"character":
                    request.context.character.model_copy(update={"character_id": "role_9"})})}),
                request.model_copy(update={"recall_identity": request.recall_identity.model_copy(update={"observer":
                    request.recall_identity.observer.model_copy(update={"persistent_id": "forged"})})})):
                self.assertEqual(resolver.resolve(forged).status, "UNAUTHORIZED")
            for change in (dict(scene_id="elsewhere"), dict(deployment_id="elsewhere")):
                self.assertFalse(WorldKnowledgeResolver(store, config.model_copy(update=change)).resolve(request).items)

    def test_constants_are_excluded_before_ranker_and_do_not_consume_topk(self):
        seen = []
        class Ranker:
            def rank(self, entries, query, **kwargs):
                seen.extend(entries)
                return entries
        principal = deployment(1).resolve(request_for(0))
        entries = (entry("constant", usage="constant"), *(entry(f"retrieval.{i}") for i in range(4)))
        result = retrieve_knowledge(entries, principal, KnowledgeQuery(text="reference", top_k=3),
            bundle_version="test", revision=0, ranker=Ranker(), defer_budget=True)
        self.assertEqual(len(result.items), 3)
        self.assertEqual(len(seen), 4)
        self.assertTrue(all(e.usage == "retrieval" for e in seen))

    def test_shared_character_id_keeps_memory_and_relationship_instances_separate(self):
        from app.memory_consolidation_context import TrustedPersistentIdentity
        from tests import test_memory_d1a_retrieval as memory
        from tests import test_relationship_summary_reader as relationships
        config = deployment(2)
        second = config.bindings[1].model_copy(update={"character_id": "role_0"})
        config = config.model_copy(update={"bindings": (config.bindings[0], second)})
        request_a = request_for(0)
        request_b = request_for(1)
        request_b = request_b.model_copy(update={"context": request_b.context.model_copy(update={"character":
            request_b.context.character.model_copy(update={"character_id": "role_0"})})})
        self.assertEqual(config.resolve(request_a), config.resolve(request_b))
        def durable(request):
            owner = request.recall_identity.observer
            return TrustedPersistentIdentity(owner.identity_namespace, owner.identity_type, owner.persistent_id)
        owner_a, owner_b = durable(request_a), durable(request_b)
        self.assertNotEqual(owner_a, owner_b)
        memories = memory.CanonicalRetrievalTests()
        memories.setUp()
        self.addCleanup(memories.doCleanups)
        first = memories.add(owner=owner_a.persistent_id, observer=request_a.run.agent_entity_id)
        second = memories.add(owner=owner_b.persistent_id, observer=request_b.run.agent_entity_id)
        self.assertEqual([i.memory_id for i in memories.read(observer=owner_a, subject=memory.A).items], [first])
        self.assertEqual([i.memory_id for i in memories.read(observer=owner_b, subject=memory.A).items], [second])
        social = relationships.RelationshipSummaryReaderTests()
        social.setUp()
        self.addCleanup(social.doCleanups)
        social.add("instance A", observer=owner_a)
        social.add("instance B", observer=owner_b)
        self.assertEqual(social.reader.get(owner_a, relationships.PLAYER_A).interpretations, ("instance A",))
        self.assertEqual(social.reader.get(owner_b, relationships.PLAYER_A).interpretations, ("instance B",))
