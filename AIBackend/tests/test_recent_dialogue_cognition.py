"""Recent accepted dialogue is a bounded read-only hint to one cognitive run."""

from __future__ import annotations

import json
import unittest

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveContextSnapshot, CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import ModelResult
from test_action_proposal_validation import _context


def request(text: str, *, agent_id: str = "npc_cafe_01",
            conversation_id: str = "conversation_1") -> CognitiveRequest:
    context = _context(agent_id=agent_id).model_dump()
    context["current_event"]["payload"] = [
        {"key": "text", "value": text},
        {"key": "conversation_id", "value": conversation_id},
    ]
    context = CognitiveContextSnapshot.model_validate(context)
    return CognitiveRequest.model_validate({
        "message_type": "cognitive_request", "schema_version": "1.0",
        "request_id": "request_current", "trace_id": "trace_current",
        "run": {"run_id": "run_01", "agent_entity_id": agent_id,
                "trigger_id": "trigger_01", "trigger_type": "event", "started_at": 10},
        "trigger": context.trigger.model_dump(), "context": context.model_dump(),
        "recall_identity": {
            "observer": {"entity_id": agent_id, "kind": "npc", "identity_namespace": "npc",
                         "identity_type": "Character", "persistent_id": "merchant.private"},
            "current_subjects": [{"entity_id": "player_01", "kind": "player", "identity_namespace": "player",
                                  "identity_type": "Player", "persistent_id": "cafe-local-player-001"}],
        },
    })


class RecordingLedger:
    def __init__(self, rows):
        self.rows = rows
        self.calls = []

    def read_recent_dialogue(self, *args, **kwargs):
        self.calls.append(args)
        self.identities = kwargs.get("identities")
        return self.rows

    def read_recent_interactions(self, *args, **kwargs):
        # This fixture supplies speech history only, with no world interaction.
        return []


class RecordingProvider:
    name = "scripted"
    model = "recent-dialogue-test"
    context_length = 16384

    def __init__(self):
        self.prompts = []

    async def generate(self, request):
        self.prompts.append(request.messages[-1].content)
        return ModelResult(text=json.dumps({
            "decision_kind": "DIRECT_RESPONSE", "utterance": "你刚才说喜欢拿铁，不加糖。",
        }, ensure_ascii=False))


class RecentDialogueCognitionTests(unittest.IsolatedAsyncioTestCase):
    async def test_entry_reads_same_observer_conversation_and_passes_prior_evidence(self):
        ledger = RecordingLedger([{"turn_index": 1, "input_seq": 1,
                                   "player_text": "我最喜欢喝拿铁，而且不加糖。",
                                   "npc_text": "明白了。", "evidence_status": "paired"}])
        provider = RecordingProvider()
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=ledger)
        result = await entry.run(request("我刚刚说过我最喜欢喝什么？"))
        self.assertEqual(("npc_cafe_01", "conversation_1", "request_current", "event_01"),
                         ledger.calls[0])
        self.assertEqual(("player", "Player", "cafe-local-player-001"), ledger.identities["player_01"])
        self.assertEqual("DIRECT_RESPONSE", result.decision.decision_kind)
        self.assertIn("PRIOR DIALOGUE TURNS JSON:", provider.prompts[0])
        self.assertIn("我最喜欢喝拿铁", provider.prompts[0])
        self.assertEqual(1, len(provider.prompts))

    async def test_no_evidence_prompt_forbids_invented_history(self):
        provider = RecordingProvider()
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),
                                      experience_ledger=RecordingLedger([]))
        await entry.run(request("我之前最喜欢喝什么茶？"))
        self.assertIn("PRIOR DIALOGUE TURNS JSON: []", provider.prompts[0])
        # The allocator removes duplicate user prose; inspect the policy actually sent in System.
        from app.model_requests import CognitiveModelRequestFactory
        wire = CognitiveModelRequestFactory.create_event(request("我之前最喜欢喝什么茶？"), knowledge_context_limit=16384)
        self.assertIn("Missing history means uncertainty, not proof of absence", wire.messages[0].content)
        self.assertIn("about a favorite coffee does not establish a favorite tea", wire.messages[0].content)


if __name__ == "__main__":
    unittest.main()
