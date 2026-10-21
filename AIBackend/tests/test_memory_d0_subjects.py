from __future__ import annotations

import json
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest

from pydantic import ValidationError

from app.canonical_memory_repository import CanonicalMemoryRepository
from app.contracts import CognitiveRequest, CognitiveRecallIdentityContext, CognitiveRecallSubjectIdentity
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.model_requests import CognitiveModelRequestFactory
from app.recall_subjects import RecallSubjectCatalogBuilder, RecallSubjectCatalogError


def identity(entity: str, kind: str, persistent: str | None, *, owner: str | None = None) -> dict:
    durable = owner is not None or persistent is not None
    return {
        "entity_id": entity, "kind": kind,
        "identity_namespace": ("npc" if kind == "npc" else "player") if durable else None,
        "identity_type": ("Character" if kind == "npc" else "Player") if durable else None,
        "persistent_id": owner if owner is not None else persistent,
    }


def request(*, observer="npc_n1", owner="n1.uid", requester="player_a",
            current=(), perceived=(), requester_uid="A.uid") -> CognitiveRequest:
    trigger = {
        "trigger_id": "trigger_1", "trigger_type": "event", "agent_entity_id": observer,
        "source_event_id": "event_1", "created_at": 1, "reason": "player.utterance",
    }
    subjects = [identity(requester, "player", requester_uid), *current]
    return CognitiveRequest.model_validate({
        "message_type": "cognitive_request", "schema_version": "1.0",
        "request_id": "request_1", "trace_id": "trace_1",
        "run": {"run_id": "run_1", "agent_entity_id": observer, "trigger_id": "trigger_1",
                "trigger_type": "event", "started_at": 1},
        "trigger": trigger,
        "context": {
            "run_id": "run_1", "agent_entity_id": observer, "trigger": trigger,
            "current_event": {
                "event_id": "event_1", "event_type": "player.utterance", "source_entity_id": requester,
                "target_entity_id": observer, "area_entity_id": "", "payload": [],
                "occurred_at": 1, "world_revision": 0, "has_world_revision": False,
            },
            "character": {"character_id": "cafe.n1", "display_name": "店员",
                          "role_summary": "服务", "speaking_style": "简洁",
                          "backstory": "咖啡馆工作。", "personality": "温和。", "boundaries": "不编造事实。"},
            "relevant_player": {"player_entity_id": requester, "display_name": "小明"},
            "perception": {"observer_entity_id": observer, "perception_revision": 1,
                           "entities": [
                               {"entity_id": entity_id, "kind": kind, "display_name": name,
                                "aliases": [], "semantic_tags": [], "visibility": "current_visible",
                                "capabilities": []}
                               for entity_id, kind, name in perceived
                           ]},
        },
        "recall_identity": {
            "observer": identity(observer, "npc", owner),
            "requester_entity_id": requester,
            "current_subjects": subjects,
        },
    })


class MemoryD0SubjectTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "memory.sqlite3"

        def connect():
            connection = sqlite3.connect(path)
            connection.row_factory = sqlite3.Row
            return connection

        self.connect = connect
        with closing(connect()) as db:
            db.executescript("""
                CREATE TABLE memory_records (
                    memory_id TEXT PRIMARY KEY, identity_namespace TEXT, identity_type TEXT,
                    persistent_id TEXT, scope TEXT, status TEXT
                );
                CREATE TABLE memory_subjects (
                    memory_id TEXT, identity_namespace TEXT, identity_type TEXT, persistent_id TEXT
                );
            """)
            db.commit()
        self.repo = CanonicalMemoryRepository(connect)
        self.builder = RecallSubjectCatalogBuilder(self.repo)

    def add_known(self, owner: str, subject: str, *, kind="player", status="ACTIVE") -> None:
        with closing(self.connect()) as db:
            memory_id = f"memory_{owner}_{subject}_{status}"
            db.execute("INSERT INTO memory_records VALUES(?,?,?,?,?,?)",
                       (memory_id, "npc", "Character", owner, "NPC_PRIVATE", status))
            db.execute("INSERT INTO memory_subjects VALUES(?,?,?,?)",
                       (memory_id, "player" if kind == "player" else "npc",
                        "Player" if kind == "player" else "Character", subject))
            db.commit()

    def test_requester_self_resolves_without_equating_all_subjects_to_requester(self) -> None:
        catalog = self.builder.build(request())
        result = catalog.resolve_ref(catalog.requester_ref)
        self.assertEqual(result.status, "RESOLVED")
        self.assertEqual(result.identity.persistent_id, "A.uid")
        self.assertNotEqual(result.identity, catalog.observer)

    def test_player_a_can_select_player_b_and_npc_n2_from_observer_context(self) -> None:
        catalog = self.builder.build(request(
            current=(identity("player_b", "player", "B.uid"),
                     identity("npc_n2", "npc", "N2.uid")),
            perceived=(("player_b", "player", "小明"), ("npc_n2", "npc", "顾客")),
        ))
        self.assertEqual(catalog.resolve_current_entity("player_b").identity.persistent_id, "B.uid")
        self.assertEqual(catalog.resolve_current_entity("npc_n2").identity.persistent_id, "N2.uid")
        self.assertNotEqual(catalog.resolve_ref(catalog.requester_ref).identity.persistent_id, "B.uid")

    def test_duplicate_display_names_have_distinct_refs_and_identities(self) -> None:
        catalog = self.builder.build(request(
            current=(identity("player_b", "player", "B.uid"),),
            perceived=(("player_b", "player", "小明"),),
        ))
        a = catalog.resolve_current_entity("player_a")
        b = catalog.resolve_current_entity("player_b")
        self.assertNotEqual(a.subject_ref, b.subject_ref)
        self.assertNotEqual(a.identity, b.identity)

    def test_historical_subject_survives_runtime_entity_change(self) -> None:
        self.add_known("n1.uid", "B.uid")
        old = self.builder.build(request())
        new = self.builder.build(request(
            current=(identity("player_b_new", "player", "B.uid"),),
            perceived=(("player_b_new", "player", "玩家B"),),
        ))
        prior = old.resolve_identity(TrustedPersistentIdentity("player", "Player", "B.uid"))
        current = new.resolve_current_entity("player_b_new")
        self.assertEqual(prior.status, "RESOLVED")
        self.assertEqual(prior.identity, current.identity)

    def test_non_durable_unknown_and_unauthorized_are_distinct(self) -> None:
        catalog = self.builder.build(request(
            current=(identity("npc_temp", "npc", None),),
            perceived=(("npc_temp", "npc", "路人"),),
        ))
        self.assertEqual(catalog.resolve_current_entity("npc_temp").status, "SUBJECT_NOT_DURABLE")
        self.assertEqual(catalog.resolve_ref("npc_temp").status, "UNKNOWN_SUBJECT")
        self.assertEqual(catalog.resolve_ref("U10001").status, "UNKNOWN_SUBJECT")
        self.assertIsNone(catalog.resolve_ref("U10001").subject_ref)
        self.assertEqual(catalog.resolve_identity(
            TrustedPersistentIdentity("npc", "Character", "stranger.uid")
        ).status, "SUBJECT_NOT_AUTHORIZED")

    def test_registry_only_entity_cannot_enter_observer_catalog(self) -> None:
        with self.assertRaisesRegex(RecallSubjectCatalogError, "NOT_OBSERVER_LOCAL"):
            self.builder.build(request(current=(identity("npc_unseen", "npc", "N2.uid"),)))

    def test_owner_scope_isolated_between_merchant_and_regular_customer(self) -> None:
        self.add_known("n1.uid", "B.uid")
        merchant = self.builder.build(request())
        regular = self.builder.build(request(observer="npc_regular", owner="regular.uid"))
        b = TrustedPersistentIdentity("player", "Player", "B.uid")
        self.assertEqual(merchant.resolve_identity(b).status, "RESOLVED")
        self.assertEqual(regular.resolve_identity(b).status, "SUBJECT_NOT_AUTHORIZED")

    def test_private_uid_never_enters_catalog_or_model_prompt(self) -> None:
        value = request(requester_uid="U10001", owner="merchant.secret")
        catalog = self.builder.build(value)
        rendered = json.dumps(catalog.model_visible_payload(), ensure_ascii=False)
        model_prompt = "\n".join(message.content for message in
                                 CognitiveModelRequestFactory.create_event(value, knowledge_context_limit=16384).messages)
        for secret in ("U10001", "merchant.secret", "persistent_id", "identity_namespace"):
            self.assertNotIn(secret, rendered)
            self.assertNotIn(secret, model_prompt)
            self.assertNotIn(secret, value.model_dump_json())

    def test_missing_owner_identity_fails_closed(self) -> None:
        with self.assertRaisesRegex(RecallSubjectCatalogError, "OBSERVER_NOT_DURABLE"):
            self.builder.build(request(owner=None))

    def test_unity_empty_strings_are_absent_identity_and_requester(self) -> None:
        # Actual JsonUtility output observed in the Editor for a temporary NPC.
        value = CognitiveRecallSubjectIdentity.model_validate_json(
            '{"entity_id":"npc_temp","kind":"npc","identity_namespace":"",'
            '"identity_type":"","persistent_id":""}')
        self.assertIsNone(value.persistent_id)
        envelope = CognitiveRecallIdentityContext(observer=value, requester_entity_id="")
        self.assertIsNone(envelope.requester_entity_id)
        with self.assertRaises(ValidationError):
            CognitiveRecallSubjectIdentity.model_validate({
                **value.model_dump(), "identity_namespace": "npc"})

    def test_refs_from_another_catalog_are_not_authorized_handles(self) -> None:
        first = self.builder.build(request())
        second = self.builder.build(request(observer="npc_regular", owner="regular.uid"))
        self.assertEqual(second.resolve_ref(first.requester_ref).status, "UNKNOWN_SUBJECT")
        self.assertIsNone(second.resolve_ref(first.requester_ref).identity)

    def test_historical_enumeration_uses_owner_and_lifecycle_scope(self) -> None:
        self.add_known("n1.uid", "known.uid", status="SUPERSEDED")
        self.add_known("other.uid", "secret.uid")
        known = self.repo.list_known_subjects(TrustedPersistentIdentity("npc", "Character", "n1.uid"))
        self.assertEqual([value.persistent_id for value in known], ["known.uid"])


if __name__ == "__main__":
    unittest.main()
