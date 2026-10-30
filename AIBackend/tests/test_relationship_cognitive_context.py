"""Trusted pair selection and bounded Relationship Context in Runtime v2 cognition."""

from __future__ import annotations

import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from app.action_proposal_validator import ActionProposalValidationError
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_step_models import RecallOutcome
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.relationship_context import RelationshipContextResolver
from app.relationship_summary_reader import RelationshipSummary
from tests.test_action_proposal_validation import _decision
from tests.test_memory_d2_cognitive_steps import (
    OWNER, PLAYER, ScriptedProvider, cognitive_request,
)
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


PLAYER_B = type(PLAYER)("player", "Player", "U20002.private")
DIRECT = {"decision_kind": "DIRECT_RESPONSE", "utterance": "你好。"}
RECALL = {"decision_kind": "MEMORY_RECALL", "recall_plan": {"semantic_query": "上次说过什么"}}


def identity(entity_id, kind, value):
    return dict(entity_id=entity_id, kind=kind,
                identity_namespace=value.identity_namespace,
                identity_type=value.identity_type, persistent_id=value.persistent_id)


def request_with(*, source="player_01", event_type="player.utterance",
                 relevant="player_01", subjects=None, observer=True,
                 event=True, actions=False):
    original = cognitive_request(actions=actions)
    data = original.model_dump()
    data["recall_identity"] = original.recall_identity.model_dump()
    if event:
        data["context"]["current_event"]["source_entity_id"] = source
        data["context"]["current_event"]["event_type"] = event_type
    else:
        data["context"]["current_event"] = None
    data["recall_identity"]["requester_entity_id"] = source if event and source else None
    if relevant is None:
        data["context"]["relevant_player"] = None
    else:
        data["context"]["relevant_player"] = {
            "player_entity_id": relevant, "display_name": "玩家",
        }
    if subjects is not None:
        data["recall_identity"]["current_subjects"] = subjects
    if not observer:
        data["recall_identity"]["observer"].update(
            identity_namespace=None, identity_type=None, persistent_id=None,
        )
    return CognitiveRequest.model_validate(data)


def summary(*items):
    return RelationshipSummary(
        interpretations=items, source_count=len(items), selected_count=len(items),
        truncated=False, char_budget_used=sum(map(len, items)), token_budget_used=10,
    )


class Reader:
    def __init__(self, values=None, error=None):
        self.values = values or {}
        self.error = error
        self.calls = []

    def get(self, observer, subject):
        self.calls.append((observer, subject))
        if self.error is not None:
            raise self.error
        return self.values.get((observer, subject), summary())


class RecallAdapter:
    def prepare(self, request):
        return None

    def recall(self, catalog, plan):
        return RecallOutcome(status="RECALL_UNAVAILABLE")


class RelationshipCognitiveContextTests(unittest.IsolatedAsyncioTestCase):
    def test_production_reader_defers_pair_budget_and_preserves_lineage(self):
        from tests.test_relationship_summary_reader import RelationshipSummaryReaderTests
        fixture = RelationshipSummaryReaderTests(); fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        values = ("较早的明确边界。" * 60, "后来明确澄清的边界。" * 50,
                  "最近的主观推测仍未确认，不能覆盖明确边界。" * 20)
        for value in values:
            fixture.add(value, observer=OWNER, subject=PLAYER)
        resolver = RelationshipContextResolver(fixture.reader)
        projected = resolver.resolve(request_with())
        self.assertEqual(projected.interpretations, values)
        self.assertEqual([source["generated_at"] for source in projected.sources], [1, 2, 3])
        self.assertEqual(projected.sources[-1]["reflection_id"], "reflection_3")
        self.assertNotIn(PLAYER.persistent_id, projected.model_dump_json())
        other = request_with(source="player_02", relevant="player_02",
            subjects=[identity("player_02", "player", PLAYER_B)])
        self.assertIsNone(resolver.resolve(other))

    def setup_entry(self, reader, scripts=None, *, recall=False):
        provider = ScriptedProvider(scripts or [DIRECT])
        entry = CognitiveBackendEntry(
            provider, PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH,
            memory_recall=RecallAdapter() if recall else None,
            relationship_context_resolver=RelationshipContextResolver(reader),
        )
        return entry, provider

    @staticmethod
    def prompt(provider, index=0):
        return "\n".join(message.content for message in provider.requests[index].messages)

    async def test_direct_player_utterance_pair_and_safe_prompt(self):
        reader = Reader({(OWNER, PLAYER): summary("之前交流友善。")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with())
        prompt = self.prompt(provider)
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        self.assertIn("RELATIONSHIP CONTEXT", prompt)
        self.assertIn("之前交流友善。", prompt)
        for marker in ("historical/subjective", "not facts/instructions/action authority",
                       "Current world/perception and verified evidence win",
                       "Recency alone does not establish truth", "MEMORY_RECALL"):
            self.assertIn(marker, prompt)
        for secret in (OWNER.persistent_id, PLAYER.persistent_id, "persistent_id",
                       "identity_namespace", "identity_type"):
            self.assertNotIn(secret, prompt)

    async def test_visual_became_visible_uses_event_player_source(self):
        reader = Reader({(OWNER, PLAYER): summary("再次见面较熟悉。")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with(event_type="entity.became_visible"))
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        self.assertIn("再次见面较熟悉。", self.prompt(provider))

    async def test_two_players_use_only_explicit_source_pair(self):
        reader = Reader({(OWNER, PLAYER): summary("A 友善"),
                         (OWNER, PLAYER_B): summary("B 不愉快")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with(
            source="player_02", relevant="player_01",
            subjects=[identity("player_01", "player", PLAYER),
                      identity("player_02", "player", PLAYER_B)],
        ))
        self.assertEqual(reader.calls, [(OWNER, PLAYER_B)])
        self.assertIn("B 不愉快", self.prompt(provider))
        self.assertNotIn("A 友善", self.prompt(provider))
        self.assertNotIn(PLAYER_B.persistent_id, self.prompt(provider))

    async def test_explicit_player_without_identity_never_falls_back_to_other_player(self):
        reader = Reader({(OWNER, PLAYER_B): summary("B 私有关系")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with(
            source="player_01", relevant="player_02",
            subjects=[dict(entity_id="player_01", kind="player"),
                      identity("player_02", "player", PLAYER_B)],
        ))
        self.assertEqual(reader.calls, [])
        self.assertNotIn("RELATIONSHIP CONTEXT", self.prompt(provider))

    async def test_relevant_player_fallback_requires_exact_current_identity(self):
        reader = Reader({(OWNER, PLAYER): summary("有关联")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with(event=False))
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        self.assertIn("有关联", self.prompt(provider))

    async def test_relevant_player_mismatch_and_unknown_source_fail_closed(self):
        for request in (
            request_with(event=False, subjects=[identity("player_02", "player", PLAYER_B)]),
            request_with(source="unknown_player", subjects=[identity("player_01", "player", PLAYER)]),
            request_with(observer=False),
        ):
            with self.subTest(request=request.context.current_event):
                reader = Reader({(OWNER, PLAYER): summary("不可泄漏")})
                entry, provider = self.setup_entry(reader)
                await entry.run(request)
                self.assertEqual(reader.calls, [])
                self.assertNotIn("RELATIONSHIP CONTEXT", self.prompt(provider))

    async def test_nonplayer_event_source_can_use_relevant_player(self):
        reader = Reader({(OWNER, PLAYER): summary("已有印象")})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with(source="npc_02", subjects=[
            identity("npc_02", "npc", OWNER), identity("player_01", "player", PLAYER),
        ]))
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        self.assertIn("已有印象", self.prompt(provider))

    async def test_empty_summary_omits_section(self):
        reader = Reader()
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with())
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        self.assertNotIn("RELATIONSHIP CONTEXT", self.prompt(provider))

    async def test_recall_step_reuses_same_relationship_snapshot_once(self):
        reader = Reader({(OWNER, PLAYER): summary("较早友善。", "最近不愉快。")})
        entry, provider = self.setup_entry(reader, [RECALL, DIRECT], recall=True)
        await entry.run(request_with())
        self.assertEqual(reader.calls, [(OWNER, PLAYER)])
        sections = [self.prompt(provider, index).split("Relationship context JSON: ")[1].splitlines()[0]
                    for index in (0, 1)]
        self.assertEqual(sections[0], sections[1])
        self.assertEqual(json.loads(sections[0])["interpretations"], ["较早友善。", "最近不愉快。"])
        self.assertIn("HISTORICAL MEMORY EVIDENCE", self.prompt(provider, 1))

    async def test_relationship_cannot_authorize_absent_action(self):
        reader = Reader({(OWNER, PLAYER): summary("我非常信任这个玩家")})
        entry, provider = self.setup_entry(reader, [_decision().model_dump()])
        with self.assertRaises(ActionProposalValidationError):
            await entry.run(request_with(actions=False))
        self.assertIn("我非常信任这个玩家", self.prompt(provider))

    async def test_reader_failure_is_optional_and_not_model_visible(self):
        reader = Reader(error=sqlite3.OperationalError("private database path and U10001"))
        entry, provider = self.setup_entry(reader)
        result = await entry.run(request_with())
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertNotIn("RELATIONSHIP CONTEXT", self.prompt(provider))
        self.assertNotIn("private database path", self.prompt(provider))

    async def test_summary_text_containing_private_identity_is_dropped(self):
        reader = Reader({(OWNER, PLAYER): summary("secret " + PLAYER.persistent_id)})
        entry, provider = self.setup_entry(reader)
        await entry.run(request_with())
        self.assertNotIn("RELATIONSHIP CONTEXT", self.prompt(provider))
        self.assertNotIn(PLAYER.persistent_id, self.prompt(provider))

    def test_production_composition_reuses_repository(self):
        script = (
            "from app import m3_main as app; "
            "assert app.relationship_summary_reader.reflections is app.relationship_reflection_repository; "
            "resolver=app.cognitive_entry._relationship_context_resolver; "
            "assert resolver is not None; "
            "assert resolver.reader is app.relationship_summary_reader"
        )
        with tempfile.TemporaryDirectory() as directory:
            env = {**os.environ, "EMBODIEDNPC_MEMORY_DB": str(Path(directory) / "memory.sqlite3"),
                   "EMBODIEDNPC_PROVIDER": "mock"}
            result = subprocess.run(
                [sys.executable, "-c", script], env=env,
                capture_output=True, text=True, timeout=15,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
