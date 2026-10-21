"""R4-C-C-B contracts: current task and bounded, owned prior dialogue."""

import json
import tempfile
import unittest
from pathlib import Path

from app.cognitive_step_models import RecallOutcome
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.model_requests import CognitiveModelRequestFactory
from app.sqlite_database import SQLiteDatabase
from test_recent_dialogue_cognition import request


class PriorDialogueProjectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.db = SQLiteDatabase(Path(self.temp.name) / "prior.sqlite3")
        self.ledger = ExperienceLedger(self.db.connect)
        self.writer = ExperienceIngestionService(self.ledger)
        self.index = 0
        self.last_draft = None

    def add(self, turn, kind, text, *, observer="npc_cafe_01", conversation="conversation_1",
            request_id=None, source_event_id=None, occurred=None, observed=None, session="session_1"):
        self.index += 1
        request_id = request_id or f"request_{turn}"
        source_event_id = source_event_id or f"event_{turn}"
        occurred = float(self.index) if occurred is None else occurred
        observed = occurred if observed is None else observed
        response = kind == "dialogue_response"
        draft = ExperienceDraft(
            observer_id=observer, event_type=kind, source_type="dialogue",
            source_session_id=session, clock_domain="unity_realtime",
            source_event_id=source_event_id,
            actor_entity_id=observer if response else "player_01",
            target_entity_ids=("player_01",) if response else (observer,),
            natural_language_payload=text, occurred_at=occurred, observed_at=observed,
            request_id=request_id, trace_id=f"trace_{turn}", conversation_id=conversation,
            provenance="EXECUTED_BY_SELF" if response else "SYSTEM_CONFIRMED",
        )
        self.last_draft = draft
        result = self.writer.append(ExperienceAppendRequest(
            request_id=f"append_{self.index}", trace_id=f"trace_{turn}", draft=draft))
        self.assertEqual("inserted", result.status)
        return result

    def prior(self, *, observer="npc_cafe_01", conversation="conversation_1",
              request_id="request_current", event_id="event_current", **kwargs):
        return self.ledger.read_recent_dialogue(observer, conversation, request_id, event_id, **kwargs)

    def test_red_pairs_by_correlation_and_orders_by_input_anchor_not_response_ingest(self):
        self.add("a", "dialogue_input", "input A")
        self.add("b", "dialogue_input", "input B")
        self.add("a", "dialogue_response", "response A", occurred=2.2)
        self.add("b", "dialogue_response", "response B", occurred=3.2)
        self.add("current", "dialogue_input", "current", occurred=4, observed=4)
        self.assertEqual([
            ("input A", "response A", 1), ("input B", "response B", 2),
        ], [(turn["player_text"], turn["npc_text"], turn["input_seq"]) for turn in self.prior()])

    def test_red_current_input_and_response_are_excluded_even_with_one_id_mismatch(self):
        self.add("a", "dialogue_input", "prior")
        self.add("current", "dialogue_input", "current input", request_id="fallback_current")
        self.add("current", "dialogue_response", "current response", request_id="fallback_current")
        self.assertEqual(["prior"], [turn["player_text"] for turn in self.prior()])

    def test_red_delayed_response_after_current_input_is_not_history_tail(self):
        self.add("a", "dialogue_input", "input A", occurred=1)
        self.add("current", "dialogue_input", "current", occurred=2)
        self.add("a", "dialogue_response", "late A", occurred=3)
        turns = self.prior()
        self.assertEqual(1, len(turns))
        self.assertEqual("input A", turns[0]["player_text"])
        self.assertIsNone(turns[0]["npc_text"])

    def test_red_response_ingested_before_current_but_presented_after_boundary_is_excluded(self):
        self.add("a", "dialogue_input", "input A", occurred=1)
        self.add("a", "dialogue_response", "late A", occurred=5)
        self.add("current", "dialogue_input", "current", occurred=2, observed=2)
        self.assertIsNone(self.prior()[0]["npc_text"])

    def test_red_missing_current_input_fails_closed(self):
        self.add("a", "dialogue_input", "input A")
        self.add("a", "dialogue_response", "response A")
        self.assertEqual([], self.prior())

    def test_red_orphan_prior_player_input_remains_evidence(self):
        self.add("a", "dialogue_input", "input only")
        self.add("current", "dialogue_input", "current")
        turns = self.prior()
        self.assertEqual("input only", turns[0]["player_text"])
        self.assertIsNone(turns[0]["npc_text"])

    def test_red_observer_and_conversation_isolation(self):
        self.add("a", "dialogue_input", "merchant latte")
        self.add("a", "dialogue_input", "regular tea", observer="npc_cafe_regular_01")
        self.add("other", "dialogue_input", "other conversation", conversation="conversation_2")
        self.add("current", "dialogue_input", "current merchant")
        self.add("current", "dialogue_input", "current regular", observer="npc_cafe_regular_01")
        self.assertEqual(["merchant latte"], [t["player_text"] for t in self.prior()])
        self.assertEqual(["regular tea"], [t["player_text"] for t in self.prior(observer="npc_cafe_regular_01")])

    def test_red_max_eight_source_messages_and_1200_chars_without_split_pairs(self):
        for n in range(8):
            self.add(str(n), "dialogue_input", "p" * 400)
            self.add(str(n), "dialogue_response", "n" * 400)
        self.add("current", "dialogue_input", "current")
        turns = self.prior()
        self.assertLessEqual(sum(1 + (turn["npc_text"] is not None) for turn in turns), 8)
        self.assertLessEqual(sum(len(turn["player_text"]) + len(turn["npc_text"] or "") for turn in turns), 1200)
        # Whole statements replaced the old 300-character per-message clipping.
        # Only one complete 800-character pair fits the 1200-character window.
        self.assertEqual(1, len(turns))
        self.assertEqual(("p" * 400, "n" * 400), (turns[0]["player_text"], turns[0]["npc_text"]))
        self.assertTrue(turns.truncated)

    def test_red_duplicate_append_does_not_duplicate_prior_turn(self):
        self.add("a", "dialogue_input", "input A")
        duplicate = self.writer.append(ExperienceAppendRequest(
            request_id="append_replay", trace_id="trace_a", draft=self.last_draft))
        self.assertEqual("already_exists", duplicate.status)
        self.add("current", "dialogue_input", "current")
        self.assertEqual(1, len(self.prior()))


class CurrentTaskPromptTests(unittest.TestCase):
    def test_red_step0_has_explicit_current_task_with_event_text(self):
        model_request = CognitiveModelRequestFactory.create_event(request("你好。"), knowledge_context_limit=16384)
        self.assertIn('CURRENT TASK: Cognitive context JSON.current_event', model_request.messages[1].content)
        self.assertEqual(request("你好。").context.current_event,
                         CognitiveModelRequestFactory.context_from(model_request).current_event)
        self.assertIn('"current_player_text":"你好。"', model_request.messages[1].content)

    def test_red_step1_keeps_same_current_task_identity(self):
        current = request("我以前最喜欢什么颜色？")
        step0 = CognitiveModelRequestFactory.create_event(current, knowledge_context_limit=16384)
        step1 = CognitiveModelRequestFactory.create_event(current, recall_outcome=RecallOutcome(status="NO_MEMORY_FOUND"), knowledge_context_limit=16384)
        for step in (step0, step1):
            # The event is serialized once in the authoritative context now;
            # its identity, payload, and the final binding must all survive.
            self.assertEqual(current.context.current_event,
                             CognitiveModelRequestFactory.context_from(step).current_event)
            binding = json.JSONDecoder().raw_decode(step.messages[1].content.split("CURRENT TASK BINDING JSON: ", 1)[1])[0]
            self.assertEqual("我以前最喜欢什么颜色？", binding["current_player_text"])
            self.assertEqual("player_01", binding["source_entity_id"])
        self.assertGreater(step1.messages[1].content.index("CURRENT TASK BINDING JSON:"),
                           step1.messages[1].content.index("HISTORICAL MEMORY EVIDENCE"))

    def test_red_non_player_event_keeps_event_type_without_player_text(self):
        from app.contracts import CognitiveRequest
        wire = request("unused").model_dump()
        wire["context"]["current_event"]["event_type"] = "entity.became_visible"
        wire["context"]["current_event"]["payload"] = [{"key": "entity_id", "value": "cup_01"}]
        model_request = CognitiveModelRequestFactory.create_event(CognitiveRequest.model_validate(wire), knowledge_context_limit=16384)
        self.assertIn('"event_type":"entity.became_visible"', model_request.messages[1].content)
        self.assertIn('"current_player_text":null', model_request.messages[1].content)


if __name__ == "__main__":
    unittest.main()
