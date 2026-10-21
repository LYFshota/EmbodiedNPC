"""Accepted dialogue must be committed before its online cognition reads history."""

from __future__ import annotations

import asyncio
import json
import tempfile
import threading
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from app.cognitive_entry import CognitiveBackendEntry
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import ModelResult
from app.sqlite_database import SQLiteDatabase
from tests.test_cognitive_request import _event_request
from tests.test_cognitive_websocket import SocketHarness


def cognitive(turn: int, text: str, *, agent: str = "npc_cafe_01",
              conversation: str = "conversation_b1") -> dict:
    value = _event_request()
    event_id = f"event_b1_{agent}_{turn}"
    request_id = f"request_b1_{agent}_{turn}"
    trace_id = f"trace_b1_{agent}_{turn}"
    run_id = f"run_b1_{agent}_{turn}"
    trigger_id = f"trigger_b1_{agent}_{turn}"
    value.update(request_id=request_id, trace_id=trace_id)
    value["run"].update(run_id=run_id, agent_entity_id=agent,
                        trigger_id=trigger_id, started_at=float(turn) + .1)
    value["trigger"].update(trigger_id=trigger_id, agent_entity_id=agent,
                            source_event_id=event_id, created_at=float(turn),
                            reason="player.utterance")
    value["context"].update(run_id=run_id, agent_entity_id=agent)
    value["context"]["trigger"] = value["trigger"].copy()
    value["context"]["current_event"].update(
        event_id=event_id, event_type="player.utterance", occurred_at=float(turn),
        payload=[{"key": "text", "value": text},
                 {"key": "conversation_id", "value": conversation}],
    )
    value["context"]["perception"]["observer_entity_id"] = agent
    value["recall_identity"]["observer"]["entity_id"] = agent
    return value


def experience(request: dict, text: str, *, response: bool = False) -> dict:
    agent = request["run"]["agent_entity_id"]
    event = request["context"]["current_event"]
    conversation = next(item["value"] for item in event["payload"]
                        if item["key"] == "conversation_id")
    return {
        "message_type": "experience_append_request", "schema_version": "1.0",
        "request_id": "append_" + request["request_id"] + ("_reply" if response else ""),
        "trace_id": request["trace_id"],
        "draft": {
            "schema_version": "experience-draft/1.1", "observer_id": agent,
            "persistent_participants": [{key: val for key, val in p.items() if key != "kind"}
                for p in [request["recall_identity"]["observer"], *request["recall_identity"]["current_subjects"]]],
            "event_type": "dialogue_response" if response else "dialogue_input",
            "source_type": "dialogue", "source_session_id": "session_b1",
            "clock_domain": "unity_realtime", "source_event_id": event["event_id"],
            "source_observation_ids": [],
            "actor_entity_id": agent if response else "player_01",
            "target_entity_ids": ["player_01" if response else agent],
            "structured_payload": {"direction": "npc_to_player" if response else "player_to_npc"},
            "natural_language_payload": text,
            "occurred_at": event["occurred_at"] + (.15 if response else 0),
            "observed_at": event["occurred_at"] + (.15 if response else 0),
            "world_revision": 0, "request_id": request["request_id"],
            "trace_id": request["trace_id"], "conversation_id": conversation,
            "provenance": "EXECUTED_BY_SELF" if response else "SYSTEM_CONFIRMED",
        },
    }


class CapturingProvider:
    name = "scripted"
    model = "b1-ordering-test"

    def __init__(self):
        self.prompts: list[str] = []
        self.changed = asyncio.Event()

    async def generate(self, request):
        self.prompts.append(request.messages[-1].content)
        self.changed.set()
        return ModelResult(text='{"decision_kind":"DIRECT_RESPONSE","utterance":"收到。"}')


class BlockingIngestion:
    def __init__(self, actual: ExperienceIngestionService, blocked_event_id: str):
        self.actual = actual
        self.blocked_event_id = blocked_event_id
        self.started = threading.Event()
        self.release = threading.Event()

    def append(self, request):
        if request.draft.source_event_id == self.blocked_event_id and \
                request.draft.event_type == "dialogue_input":
            self.started.set()
            if not self.release.wait(3):
                raise TimeoutError("test commit barrier was not released")
        return self.actual.append(request)


class TracingLedger:
    def __init__(self, actual: ExperienceLedger, database: SQLiteDatabase):
        self.actual = actual
        self.database = database
        self.at_read = {}

    def read_recent_interactions(self, *args, **kwargs):
        return self.actual.read_recent_interactions(*args, **kwargs)

    def read_recent_dialogue(self, observer, conversation, request_id, event_id, **kwargs):
        with closing(self.database.connect()) as connection:
            current = connection.execute(
                "SELECT COUNT(*) FROM experience_events WHERE observer_id=? AND "
                "conversation_id=? AND event_type='dialogue_input' AND "
                "(request_id=? OR source_event_id=?)",
                (observer, conversation, request_id, event_id),
            ).fetchone()[0]
            previous_input = connection.execute(
                "SELECT COUNT(*) FROM experience_events WHERE observer_id=? AND "
                "conversation_id=? AND event_type='dialogue_input' AND "
                "request_id<>?",
                (observer, conversation, request_id),
            ).fetchone()[0]
            previous_response = connection.execute(
                "SELECT COUNT(*) FROM experience_events WHERE observer_id=? AND "
                "conversation_id=? AND event_type='dialogue_response' AND "
                "request_id<>?",
                (observer, conversation, request_id),
            ).fetchone()[0]
        self.at_read[request_id] = (bool(current), bool(previous_input), bool(previous_response))
        return self.actual.read_recent_dialogue(observer, conversation, request_id, event_id, **kwargs)


class OnlineRecentDialogueOrderingTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from app import m3_main
        self.main = m3_main
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.database = SQLiteDatabase(Path(self.directory.name) / "b1.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.tracing_ledger = TracingLedger(self.ledger, self.database)
        self.provider = CapturingProvider()
        self.entry = CognitiveBackendEntry(self.provider, PrioritizedModelExecutor(),
                                           experience_ledger=self.tracing_ledger, context_length=16384)
        self.patches = [
            patch.object(m3_main, "cognitive_entry", self.entry),
            patch.object(m3_main, "experience_ingestion_service",
                         ExperienceIngestionService(self.ledger)),
        ]
        for value in self.patches:
            value.start()
            self.addCleanup(value.stop)
        self.socket = SocketHarness()
        self.unmatched = []
        self.task = asyncio.create_task(m3_main.websocket_endpoint(self.socket.socket))

    async def asyncTearDown(self):
        self.socket.disconnect()
        await asyncio.wait_for(self.task, 3)

    async def receive(self, message_type: str, request_id: str) -> dict:
        for index, value in enumerate(self.unmatched):
            if value["message_type"] == message_type and value["request_id"] == request_id:
                return self.unmatched.pop(index)
        while True:
            value = await self.socket.result()
            if value["message_type"] == message_type and value["request_id"] == request_id:
                return value
            self.unmatched.append(value)

    @staticmethod
    def prior(prompt: str) -> list[dict]:
        line = next(line for line in prompt.splitlines()
                    if line.startswith("PRIOR DIALOGUE TURNS JSON: "))
        return json.loads(line.partition(": ")[2])

    async def exchange(self, request: dict, text: str) -> str:
        self.socket.message(request)
        self.socket.message(experience(request, text))
        ack = await self.receive("experience_append_result", "append_" + request["request_id"])
        self.assertIn(ack["status"], ("inserted", "already_exists"))
        response = await self.receive("cognitive_response", request["request_id"])
        self.assertEqual(request["trace_id"], response["trace_id"])
        self.assertEqual(request["run"]["agent_entity_id"], response["agent_entity_id"])
        prompt = self.provider.prompts[-1]
        self.socket.message(experience(request, "ack " + text, response=True))
        await self.receive("experience_append_result", "append_" + request["request_id"] + "_reply")
        return prompt

    async def test_current_input_commit_precedes_online_history_read(self):
        first = cognitive(1, "TOKEN_A")
        self.socket.message(first)
        self.socket.message(experience(first, "TOKEN_A"))
        self.assertEqual("inserted", (await self.receive(
            "experience_append_result", "append_" + first["request_id"]))["status"])
        await self.receive("cognitive_response", first["request_id"])
        self.socket.message(experience(first, "ack TOKEN_A", response=True))
        await self.receive("experience_append_result", "append_" + first["request_id"] + "_reply")

        second = cognitive(2, "刚才的暗号是什么？")
        blocked = BlockingIngestion(ExperienceIngestionService(self.ledger),
                                    second["context"]["current_event"]["event_id"])
        with patch.object(self.main, "experience_ingestion_service", blocked):
            self.provider.changed.clear()
            self.socket.message(second)
            self.socket.message(experience(second, "刚才的暗号是什么？"))
            self.assertTrue(await asyncio.to_thread(blocked.started.wait, 2))
            try:
                # The commit barrier, rather than a sleep, controls the race window.
                self.assertFalse(self.provider.changed.is_set(),
                                 "ModelRequest was built before current input commit")
            finally:
                blocked.release.set()
            self.assertEqual("inserted", (await self.receive(
                "experience_append_result", "append_" + second["request_id"]))["status"])
            await self.receive("cognitive_response", second["request_id"])
        prompt = self.provider.prompts[-1]
        self.assertEqual((True, True, True),
                         self.tracing_ledger.at_read[second["request_id"]],
                         "current and previous evidence must be committed at RecentDialogue read")
        prior = self.prior(prompt)
        self.assertEqual(["TOKEN_A"], [item["text"] for item in prior if item["role"] == "player"])
        self.assertNotIn("刚才的暗号是什么？", json.dumps(prior, ensure_ascii=False))

    async def test_twenty_rapid_turns_preserve_cutoff_pairing_and_order(self):
        previous = None
        for index in range(1, 21):
            text = f"TOKEN_{index:02d}"
            request = cognitive(index, text)
            prompt = await self.exchange(request, text)
            rows = self.prior(prompt)
            self.assertEqual((True, bool(previous), bool(previous)),
                             self.tracing_ledger.at_read[request["request_id"]])
            self.assertNotIn(text, [row["text"] for row in rows])
            if previous is not None:
                self.assertEqual(previous, rows[-2]["text"])
                self.assertEqual("ack " + previous, rows[-1]["text"])
                self.assertEqual(rows[-2]["seq"], rows[-1]["reply_to_seq"])
                self.assertEqual(sorted(row["seq"] for row in rows), [row["seq"] for row in rows])
            previous = text

    async def test_other_agent_and_conversation_progress_while_one_input_waits(self):
        merchant_waiting = cognitive(1, "M_WAIT", conversation="merchant_wait")
        self.socket.message(merchant_waiting)
        customer = cognitive(1, "C_TOKEN", agent="npc_cafe_regular_01",
                             conversation="customer_own")
        customer_prompt = await self.exchange(customer, "C_TOKEN")
        self.assertEqual([], self.prior(customer_prompt))
        other_conversation = cognitive(2, "M_OTHER", conversation="merchant_other")
        other_prompt = await self.exchange(other_conversation, "M_OTHER")
        self.assertEqual([], self.prior(other_prompt))
        self.assertNotIn(merchant_waiting["request_id"], self.tracing_ledger.at_read)
        self.socket.message(experience(merchant_waiting, "M_WAIT"))
        await self.receive("experience_append_result",
                           "append_" + merchant_waiting["request_id"])
        await self.receive("cognitive_response", merchant_waiting["request_id"])
        self.assertEqual((True, False, False),
                         self.tracing_ledger.at_read[merchant_waiting["request_id"]])
        self.assertEqual(0, sum("C_TOKEN" in json.dumps(self.prior(prompt)) and
                                "merchant" in prompt for prompt in self.provider.prompts))

    async def test_missing_current_cutoff_keeps_safe_empty_history(self):
        first = cognitive(1, "TOKEN_SAFE")
        await self.exchange(first, "TOKEN_SAFE")
        self.assertEqual([], self.ledger.read_recent_dialogue(
            "npc_cafe_01", "conversation_b1", "missing_request", "missing_event"))


if __name__ == "__main__":
    unittest.main()
