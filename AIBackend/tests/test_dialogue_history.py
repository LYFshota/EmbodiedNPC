import tempfile
import unittest
from pathlib import Path

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.dialogue_history import create_dialogue_history_router
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceDraft, ExperienceAppendRequest
from app.sqlite_database import SQLiteDatabase


class DialogueHistoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repository = SQLiteDatabase(Path(self.temp.name) / "history.sqlite3")
        self.writer = ExperienceIngestionService(ExperienceLedger(self.repository.connect))

        def read_only_connection():
            connection = self.repository.connect()
            connection.execute("PRAGMA query_only = ON")
            return connection

        self.ledger = ExperienceLedger(read_only_connection)
        app = FastAPI()
        app.include_router(create_dialogue_history_router(self.ledger))
        self.client = TestClient(app)
        self.addCleanup(self.client.close)

    def append(self, key, observer="merchant", conversation="conversation_1",
               event_type="dialogue_input", text="hello", occurred=1, turn_ref=None):
        turn_ref = turn_ref or key
        result = self.writer.append(ExperienceAppendRequest(
            request_id="request_" + key, trace_id="trace_" + key,
            draft=ExperienceDraft(
                observer_id=observer, conversation_id=conversation, event_type=event_type,
                source_type="dialogue", source_session_id="test_session",
                source_event_id="source_" + turn_ref, clock_domain="unity_realtime",
                actor_entity_id="player" if event_type == "dialogue_input" else observer,
                target_entity_ids=(observer,), structured_payload={},
                natural_language_payload=text, occurred_at=occurred, observed_at=occurred,
                world_revision=1, provenance="SYSTEM_CONFIRMED",
                request_id="request_" + turn_ref, trace_id="trace_" + turn_ref,
            ),
        ))
        self.assertEqual("inserted", result.status)

    def get(self, **kwargs):
        return self.client.get("/v1/dialogue-history", params={
            "observer_id": "merchant", "conversation_id": "conversation_1", **kwargs,
        })

    def test_observer_conversation_and_event_filter_with_sequence_order(self):
        self.append("first", text="<b>literal user text</b>", occurred=20)
        self.append("other_npc", observer="regular_customer", text="private customer")
        self.append("other_conversation", conversation="conversation_2", text="old conversation")
        self.append("goal", event_type="goal_succeeded", text="not dialogue")
        self.append("reply", event_type="dialogue_response", text="welcome", occurred=2)
        response = self.get()
        self.assertEqual(200, response.status_code)
        data = response.json()
        self.assertEqual([1, 4], [item["experience_seq"] for item in data["items"]])
        self.assertEqual(["player", "npc"], [item["role"] for item in data["items"]])
        self.assertEqual(["<b>literal user text</b>", "welcome"], [item["text"] for item in data["items"]])
        self.assertEqual({"event_id", "experience_seq", "role", "actor", "text", "timestamp"},
                         set(data["items"][0]))
        self.assertEqual(4, self.ledger.latest_seq("merchant"))

    def test_incremental_pages_no_duplicates_and_later_reply(self):
        for i in range(3):
            self.append(str(i))
        first = self.get(limit=2).json()
        self.assertTrue(first["has_more"])
        second = self.get(limit=2, after_seq=first["next_after_seq"]).json()
        self.assertEqual([3], [item["experience_seq"] for item in second["items"]])
        self.assertFalse(second["has_more"])
        self.assertEqual([], self.get(after_seq=3).json()["items"])
        self.append("later", event_type="dialogue_response")
        self.assertEqual([4], [item["experience_seq"] for item in self.get(after_seq=3).json()["items"]])

    def test_empty_and_invalid_filters(self):
        self.assertEqual([], self.get().json()["items"])
        for invalid in ({"observer_id": ""}, {"conversation_id": ""}, {"after_seq": -1},
                        {"limit": 101}, {"limit": 0}):
            self.assertEqual(422, self.get(**invalid).status_code)
        self.assertEqual(422, self.client.get("/v1/dialogue-history").status_code)
        self.assertEqual([], self.get(conversation_id="' OR 1=1 --").json()["items"])

    def test_recent_dialogue_is_bounded_scoped_and_excludes_current_request(self):
        self.append("preference", text="我最喜欢喝拿铁，而且不加糖")
        self.append("reply", event_type="dialogue_response", text="明白了，拿铁不加糖",
                    turn_ref="preference")
        self.append("other_npc", observer="regular_customer", text="别人的秘密")
        self.append("other_current", observer="regular_customer", text="现在的问题")
        self.append("other_conversation", conversation="conversation_2", text="另一段对话")
        self.append("current", text="我刚刚说过什么")
        rows = self.ledger.read_recent_dialogue("merchant", "conversation_1",
                                                "request_current", "source_current")
        self.assertEqual(["我最喜欢喝拿铁，而且不加糖"], [row["player_text"] for row in rows])
        self.assertEqual(["明白了，拿铁不加糖"], [row["npc_text"] for row in rows])
        self.assertEqual([1], [row["input_seq"] for row in rows])
        self.assertEqual(["别人的秘密"], [row["player_text"] for row in
            self.ledger.read_recent_dialogue("regular_customer", "conversation_1",
                                             "request_other_current", "source_other_current")])

    def test_recent_dialogue_keeps_newest_with_character_budget(self):
        for index in range(12):
            self.append(str(index), text=f"accepted-{index}")
        self.append("current", text="current")
        rows = self.ledger.read_recent_dialogue("merchant", "conversation_1",
                                                "request_current", "source_current",
                                                limit=8, max_chars=32)
        self.assertLessEqual(len(rows), 8)
        self.assertLessEqual(sum(len(row["player_text"]) for row in rows), 32)
        self.assertEqual("accepted-11", rows[-1]["player_text"])
        self.assertEqual(sorted(row["input_seq"] for row in rows),
                         [row["input_seq"] for row in rows])


if __name__ == "__main__":
    unittest.main()
