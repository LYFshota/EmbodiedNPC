from __future__ import annotations

import json
import os
import tempfile
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator
from pydantic import ValidationError

from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.sqlite_database import SQLiteDatabase


ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "Contracts"


class ExperienceIngestionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.repository = SQLiteDatabase(Path(self.directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.repository.connect)
        self.service = ExperienceIngestionService(self.ledger)

    def tearDown(self) -> None:
        self.directory.cleanup()

    def load_request(self) -> dict:
        return json.loads((CONTRACTS / "examples/valid/experience_append_request.json").read_text(encoding="utf-8"))

    def test_contract_schemas_and_examples(self) -> None:
        request_schema = json.loads((CONTRACTS / "experience-append-request.schema.json").read_text(encoding="utf-8"))
        result_schema = json.loads((CONTRACTS / "experience-append-result.schema.json").read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(request_schema)
        Draft202012Validator.check_schema(result_schema)
        valid_request = self.load_request()
        valid_result = json.loads((CONTRACTS / "examples/valid/experience_append_result_inserted.json").read_text(encoding="utf-8"))
        invalid = json.loads((CONTRACTS / "examples/invalid/experience_append_request_unknown_event.json").read_text(encoding="utf-8"))
        self.assertEqual([], list(Draft202012Validator(request_schema).iter_errors(valid_request)))
        self.assertEqual([], list(Draft202012Validator(result_schema).iter_errors(valid_result)))
        self.assertNotEqual([], list(Draft202012Validator(request_schema).iter_errors(invalid)))

    def test_transport_append_ack_preserves_provenance_clock_and_correlation(self) -> None:
        payload = self.load_request()
        payload["draft"].update({
            "event_type": "dialogue_input", "source_type": "dialogue",
            "source_event_id": "dialogue-request-01", "source_observation_ids": [],
            "actor_entity_id": "player_01", "target_entity_ids": ["npc_ybot_01"],
            "structured_payload": {"input_mode": "text"},
            "natural_language_payload": "你好", "request_id": "dialogue-request-01",
            "trace_id": "dialogue-trace-01",
            "conversation_id": "player_01:npc_ybot_01:conversation_01",
            "confidence": None, "provenance": "SYSTEM_CONFIRMED",
        })
        result = self.service.append(ExperienceAppendRequest.model_validate(payload))
        event = self.ledger.get_by_event_id(result.event_id)
        self.assertEqual("inserted", result.status)
        self.assertEqual("SYSTEM_CONFIRMED", event.provenance)
        self.assertEqual("unity_realtime", event.clock_domain)
        self.assertEqual("dialogue-request-01", event.request_id)
        self.assertEqual("dialogue-trace-01", event.trace_id)
        self.assertEqual("player_01:npc_ybot_01:conversation_01", event.conversation_id)
        self.assertGreater(event.ingested_at_utc, 1_000_000_000)

    def test_ordinary_dialogue_requires_conversation_correlation(self) -> None:
        for event_type in ("dialogue_input", "dialogue_response"):
            payload = self.load_request()
            is_response = event_type == "dialogue_response"
            payload["draft"].update({
                "event_type": event_type, "source_type": "dialogue",
                "actor_entity_id": "npc_ybot_01" if is_response else "player_01",
                "target_entity_ids": ["player_01" if is_response else "npc_ybot_01"],
                "structured_payload": {"input_mode": "text"},
                "natural_language_payload": "你好", "request_id": "dialogue-request-01",
                "trace_id": "dialogue-trace-01", "conversation_id": "conversation_01",
                "confidence": None,
                "provenance": "EXECUTED_BY_SELF" if is_response else "SYSTEM_CONFIRMED",
            })
            ExperienceAppendRequest.model_validate(payload)
            for case in ("missing", "null", "empty"):
                invalid = json.loads(json.dumps(payload))
                if case == "missing":
                    invalid["draft"].pop("conversation_id")
                else:
                    invalid["draft"]["conversation_id"] = None if case == "null" else ""
                with self.subTest(event_type=event_type, case=case):
                    with self.assertRaises(ValidationError):
                        ExperienceAppendRequest.model_validate(invalid)

    def test_persistent_participant_round_trips_through_ledger_and_reopen(self) -> None:
        payload = self.load_request()
        payload["draft"].update({
            "schema_version": "experience-draft/1.1",
            "event_type": "dialogue_input", "source_type": "dialogue",
            "source_event_id": "dialogue-request-identity-01", "source_observation_ids": [],
            "actor_entity_id": "player_01", "target_entity_ids": ["npc_ybot_01"],
            "persistent_participants": [{
                "entity_id": "player_01", "identity_namespace": "player",
                "identity_type": "Player", "persistent_id": "U10001",
            }],
            "structured_payload": {"input_mode": "text"},
            "natural_language_payload": "你好", "request_id": "dialogue-request-identity-01",
            "trace_id": "dialogue-trace-identity-01",
            "conversation_id": "player_01:npc_ybot_01:identity",
            "confidence": None, "provenance": "SYSTEM_CONFIRMED",
        })
        schema = json.loads((CONTRACTS / "experience-append-request.schema.json").read_text(encoding="utf-8"))
        self.assertEqual([], list(Draft202012Validator(schema).iter_errors(payload)))

        result = self.service.append(ExperienceAppendRequest.model_validate(payload))
        reopened = ExperienceLedger(SQLiteDatabase(self.repository.database_path).connect)
        event = reopened.get_by_event_id(result.event_id)

        self.assertEqual("observer-experience/1.1", event.schema_version)
        self.assertEqual("player_01", event.actor_entity_id)
        self.assertEqual(("npc_ybot_01",), event.target_entity_ids)
        self.assertEqual(
            ("player_01", "player", "Player", "U10001"),
            (event.persistent_participants[0].entity_id,
             event.persistent_participants[0].identity_namespace,
             event.persistent_participants[0].identity_type,
             event.persistent_participants[0].persistent_id),
        )

    def test_invalid_persistent_participant_metadata_is_rejected(self) -> None:
        base = self.load_request()["draft"]
        base.update({
            "schema_version": "experience-draft/1.1",
            "actor_entity_id": "player_01",
            "target_entity_ids": ["cup_01"],
            "persistent_participants": [{
                "entity_id": "player_01", "identity_namespace": "player",
                "identity_type": "Player", "persistent_id": "U10001",
            }],
        })
        cases = []
        partial = json.loads(json.dumps(base))
        del partial["persistent_participants"][0]["persistent_id"]
        cases.append(partial)
        duplicate = json.loads(json.dumps(base))
        duplicate["persistent_participants"].append(dict(duplicate["persistent_participants"][0]))
        cases.append(duplicate)
        unrelated = json.loads(json.dumps(base))
        unrelated["persistent_participants"][0]["entity_id"] = "player_02"
        cases.append(unrelated)
        wrong_version = json.loads(json.dumps(base))
        wrong_version["schema_version"] = "experience-draft/1.0"
        cases.append(wrong_version)
        for invalid in cases:
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValidationError):
                    ExperienceDraft.model_validate(invalid)

    def test_missing_observer_malformed_payload_and_unknown_fields_are_rejected(self) -> None:
        payload = self.load_request()
        payload["draft"]["observer_id"] = ""
        with self.assertRaises(ValidationError):
            ExperienceAppendRequest.model_validate(payload)
        payload = self.load_request()
        payload["draft"]["structured_payload"] = {"nested": {"a": {"b": {"c": {"d": {"e": 1}}}}}}
        with self.assertRaises(ValidationError):
            ExperienceAppendRequest.model_validate(payload)
        payload = self.load_request()
        payload["unexpected"] = True
        with self.assertRaises(ValidationError):
            ExperienceAppendRequest.model_validate(payload)

    def test_canonical_hash_is_stable_for_mapping_and_set_order(self) -> None:
        payload = self.load_request()["draft"]
        first = ExperienceDraft.model_validate(payload)
        payload["structured_payload"] = {"unknown_fields": [], "known_fields": {"entity_kind": "Item"},
                                         "lifecycle_state": "Tracked", "perception_revision": 7}
        payload["target_entity_ids"] = ["book_01", "cup_01"]
        second = ExperienceDraft.model_validate(payload)
        payload["target_entity_ids"] = ["cup_01", "book_01"]
        third = ExperienceDraft.model_validate(payload)
        self.assertNotEqual(self.service.prepare_identity(first)[2], self.service.prepare_identity(second)[2])
        self.assertEqual(self.service.prepare_identity(second), self.service.prepare_identity(third))

    def test_reported_observation_requires_and_preserves_lineage(self) -> None:
        payload = self.load_request()
        payload["draft"].update({
            "event_type": "reported_observation", "provenance": "REPORTED",
            "source_event_id": "report_01",
            "structured_payload": {
                "perception_revision": 8, "reporter_entity_id": "npc_ybot_02",
                "parent_memory_id": "mem_parent_01", "report_conversation_id": "conversation_01",
                "report_hop": 1,
            },
        })
        result = self.service.append(ExperienceAppendRequest.model_validate(payload))
        event = self.ledger.get_by_event_id(result.event_id)
        self.assertEqual("REPORTED", event.provenance)
        self.assertEqual("mem_parent_01", event.structured_payload["parent_memory_id"])

    def test_websocket_transport_returns_append_ack_and_rejects_unknown_fields(self) -> None:
        from fastapi.testclient import TestClient
        from app import m3_main

        previous = m3_main.experience_ingestion_service
        m3_main.experience_ingestion_service = self.service
        try:
            base = os.environ["EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"]
            m3_main.knowledge_management.auth.start()
            headers = {"Authorization": "Bearer " + m3_main.knowledge_management.auth.token}
            client = TestClient(m3_main.app, base_url=base, client=("127.0.0.1", 12345))
            self.addCleanup(client.close)
            with client.websocket_connect(base.replace("http", "ws", 1) + "/ws", headers=headers) as websocket:
                websocket.send_json(self.load_request())
                result = websocket.receive_json()
                self.assertEqual("experience_append_result", result["message_type"])
                self.assertEqual("inserted", result["status"])
                self.assertEqual(1, result["experience_seq"])

                invalid = self.load_request()
                invalid["draft"]["unexpected"] = True
                websocket.send_json(invalid)
                error = websocket.receive_json()
                self.assertEqual("EXPERIENCE_APPEND_SCHEMA_INVALID", error["reason"])
        finally:
            m3_main.knowledge_management.auth.stop()
            m3_main.experience_ingestion_service = previous


if __name__ == "__main__":
    unittest.main()
