"""P2-A: isolated ledger lineage and one terminal autonomous inference."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, Mock

from app.contracts import CognitiveDecision, CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.experience_ingestion import ExperienceIngestionService
from app.experience_models import ExperienceAppendRequest
from app.experience_ledger import ExperienceLedger
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import ModelResult
from app.sqlite_database import SQLiteDatabase
from tests.test_online_recent_dialogue_ordering import cognitive, experience


class ContinuationFixture(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.ledger = ExperienceLedger(SQLiteDatabase(Path(self.directory.name) / "test.sqlite3").connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.parent = cognitive(1, "你喜欢看什么书？")
        self.participants = [dict(entity_id="npc_cafe_01", identity_namespace="npc",
                                  identity_type="Character", persistent_id="test_npc"),
                             dict(entity_id="player_01", identity_namespace="player",
                                  identity_type="Player", persistent_id="test_player")]
        for response, text in ((False, "你喜欢看什么书？"), (True, "推理小说吧。")):
            value = experience(self.parent, text, response=response)
            value["draft"].update(schema_version="experience-draft/1.1",
                                  persistent_participants=self.participants)
            ack = self.ingestion.append(ExperienceAppendRequest.model_validate(value))
            self.assertEqual("inserted", ack.status)
        self.anchor = ack
        self.metadata = dict(initiative="dialogue_continuation", continuation_index=1,
                             parent_request_id=self.parent["request_id"],
                             parent_event_id=self.parent["context"]["current_event"]["event_id"],
                             last_response_event_id=ack.event_id, last_response_seq=ack.experience_seq)

    def tearDown(self):
        self.directory.cleanup()

    def continuation(self):
        value = cognitive(2, "unused")
        event = value["context"]["current_event"]
        event.update(event_type="npc.dialogue_continue", source_entity_id="npc_cafe_01",
                     target_entity_id="npc_cafe_01")
        payload = {**self.metadata, "conversation_id": "conversation_b1",
                   "player_entity_id": "player_01", "source_session_id": "session_b1"}
        payload.pop("initiative")
        event["payload"] = [dict(key=k, value=str(v)) for k, v in payload.items()]
        value["trigger"]["reason"] = "npc.dialogue_continue"
        value["context"]["trigger"] = value["trigger"].copy()
        value["recall_identity"] = dict(observer={**self.participants[0], "kind": "npc"},
                                        current_subjects=[{**self.participants[1], "kind": "player"}])
        return value

    def autonomous_draft(self):
        value = experience(cognitive(2, "unused"), "最近还找到一本不错的。", response=True)
        value["draft"].update(schema_version="experience-draft/1.1", persistent_participants=self.participants,
                             structured_payload=self.metadata.copy())
        return value

class ContinuationTests(ContinuationFixture):
    def test_policy_default_and_episode_semantics(self):
        self.assertIsNone(CognitiveDecision(decision_kind="DIRECT_RESPONSE", utterance="好。",
                                            episode_directive="CONTINUE").dialogue_turn_policy)
        for kind in ("NO_ACTION", "DIRECT_RESPONSE"):
            with self.assertRaises(ValueError):
                CognitiveDecision(decision_kind=kind, utterance="再见。" if kind == "DIRECT_RESPONSE" else None,
                                  episode_directive="END", dialogue_turn_policy="MAY_CONTINUE")
        with self.assertRaises(ValueError):
            CognitiveDecision(decision_kind="NO_ACTION", dialogue_turn_policy="CONTINUE")

    async def test_full_order_independent_identity_and_replay(self):
        value = self.autonomous_draft()
        value["draft"]["persistent_participants"] = list(reversed(self.participants))
        auto = ExperienceAppendRequest.model_validate(value)
        first, replay = self.ingestion.append(auto), self.ingestion.append(auto)
        self.assertEqual(("inserted", "already_exists", 3, 3),
                         (first.status, replay.status, first.experience_seq, replay.experience_seq))
        followup = cognitive(3, "哪一本？")
        followup["recall_identity"] = dict(observer={**self.participants[0], "kind": "npc"},
                                         current_subjects=[{**self.participants[1], "kind": "player"}])
        following_input = experience(followup, "哪一本？")
        following_input["draft"].update(schema_version="experience-draft/1.1", persistent_participants=self.participants)
        self.ingestion.append(ExperienceAppendRequest.model_validate(following_input))
        recent = self.ledger.read_recent_dialogue("npc_cafe_01", "conversation_b1", followup["request_id"],
                                                  followup["context"]["current_event"]["event_id"])
        self.assertEqual(["推理小说吧。", "最近还找到一本不错的。"], recent[0]["npc_utterances"])
        history = self.ledger.read_dialogue_history("npc_cafe_01", "conversation_b1")
        self.assertEqual(["player", "npc", "npc", "player"], [r["role"] for r in history])
        self.assertEqual([1, 2, 3, 4], [r["experience_seq"] for r in history])
        provider = Mock(generate=AsyncMock(return_value=ModelResult(text='{"decision_kind":"NO_ACTION"}')))
        await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
            CognitiveRequest.model_validate(followup))
        content = provider.generate.call_args.args[0].messages[-1].content
        projection = json.loads(next(line.split(": ", 1)[1] for line in content.splitlines()
                                     if line.startswith("PRIOR DIALOGUE TURNS JSON: ")))
        self.assertEqual(["推理小说吧。", "最近还找到一本不错的。"],
                         [message["text"] for message in projection if message["role"] == "npc"])
        self.assertEqual(projection[1]["seq"], projection[2]["anchor_seq"])

    def test_foreign_uid_anchor_session_and_second_attempt_rejected(self):
        for mutation in (dict(conversation_id="foreign"), dict(source_session_id="foreign"),
                         dict(persistent_participants=[{**p, "persistent_id": "wrong"} for p in self.participants])):
            value = self.autonomous_draft()
            value["draft"].update(mutation)
            self.assertEqual("id_conflict", self.ingestion.append(ExperienceAppendRequest.model_validate(value)).status)
        self.assertEqual(2, self.ledger.latest_seq("npc_cafe_01"))
        value = self.autonomous_draft()
        self.assertEqual("inserted", self.ingestion.append(ExperienceAppendRequest.model_validate(value)).status)
        value["draft"]["request_id"] = "another_autonomous_request"
        self.assertEqual("id_conflict", self.ingestion.append(ExperienceAppendRequest.model_validate(value)).status)

    async def test_single_terminal_inference_without_recall(self):
        for output in (dict(decision_kind="DIRECT_RESPONSE", utterance="最近在读一本推理小说。"),
                       dict(decision_kind="NO_ACTION")):
            provider = Mock()
            provider.generate = AsyncMock(return_value=ModelResult(text=json.dumps(output)))
            recall = Mock()
            entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), memory_recall=recall,
                                           experience_ledger=self.ledger, context_length=16384)
            result = await entry.run(CognitiveRequest.model_validate(self.continuation()))
            self.assertEqual(output["decision_kind"], result.decision.decision_kind)
            self.assertEqual(1, provider.generate.await_count)
            recall.prepare.assert_not_called()
            recall.recall.assert_not_called()
            schema = provider.generate.call_args.args[0].response_schema
            from app.model_requests import NPC_REPLY_LANGUAGE_RULES
            model = provider.generate.call_args.args[0]
            self.assertIn(NPC_REPLY_LANGUAGE_RULES, model.messages[0].content)
            self.assertIn("你喜欢看什么书？", model.messages[-1].content)
            self.assertIn("推理小说吧。", model.messages[-1].content)
            self.assertNotIn("ActionProposalStep", json.dumps(schema))
            self.assertNotIn("MemoryRecallStep", json.dumps(schema))
        self.assertEqual(2, self.ledger.latest_seq("npc_cafe_01"))  # Generation never appends.

    async def test_action_and_recall_outputs_fail_without_retry(self):
        for output in (dict(decision_kind="MEMORY_RECALL", recall_plan=dict(semantic_query="old")),
                       dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="bring_object",
                            bindings=dict(target="cup_01", destination="player_01")))):
            provider = Mock(generate=AsyncMock(return_value=ModelResult(text=json.dumps(output))))
            entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384)
            with self.assertRaises(ValueError):
                await entry.run(CognitiveRequest.model_validate(self.continuation()))
            self.assertEqual(1, provider.generate.await_count)

    async def test_missing_ack_anchor_fails_before_inference(self):
        value = self.continuation()
        for pair in value["context"]["current_event"]["payload"]:
            if pair["key"] == "last_response_event_id": pair["value"] = "not_committed"
        provider = Mock(generate=AsyncMock())
        with self.assertRaises(ValueError):
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
                CognitiveRequest.model_validate(value))
        provider.generate.assert_not_called()

    async def test_rebound_player_uid_fails_before_inference(self):
        value = self.continuation()
        value["recall_identity"]["current_subjects"][0]["persistent_id"] = "new_player_uid"
        provider = Mock(generate=AsyncMock())
        with self.assertRaises(ValueError):
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
                CognitiveRequest.model_validate(value))
        provider.generate.assert_not_called()

    async def test_uncommitted_latest_displayed_response_blocks_next_player_inference(self):
        value = cognitive(3, "哪一本？")
        value["context"]["current_event"]["payload"].extend([
            dict(key="required_response_request_id", value="uncommitted_auto_request"),
            dict(key="required_response_session_id", value="session_b1")])
        provider = Mock(generate=AsyncMock())
        with self.assertRaises(ValueError):
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
                CognitiveRequest.model_validate(value))
        provider.generate.assert_not_called()
