"""C1: trusted real ledger speech, bounded window and final provider wire."""

import json
import logging
import unittest
from unittest.mock import patch

from app.cognitive_entry import CognitiveBackendEntry
from app.context_budget_allocator import ContextBudgetExceeded
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_dialogue_context import RecentDialoguePolicy, RecentDialogueTimeline, dependencies
from tests import test_cross_conversation_recent_dialogue as fixtures
from tests.test_context_budget_p1 import AllocationCapture, WireProvider, encode
from app.context_token_estimator import ContextTokenEstimator


def speeches(model):
    return json.JSONDecoder().raw_decode(model.messages[-1].content.split(
        "PRIOR DIALOGUE TURNS JSON: ", 1)[1])[0]


class RecentDialogueTimelineTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.fixture = fixtures.CrossConversationRecentDialogueTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.capture = AllocationCapture()
        logger = logging.getLogger("embodiednpc.backend.context_budget")
        level = logger.level
        logger.setLevel(logging.INFO)
        logger.addHandler(self.capture)
        self.addCleanup(logger.setLevel, level)
        self.addCleanup(logger.removeHandler, self.capture)

    def current(self, text="不喜欢。"):
        value = self.fixture.request(999, text=text)
        self.fixture.append(value, text, session="new_session", clock=.5)
        return CognitiveRequest.model_validate(value)

    def entry(self, capacity=16384, policy=None, two=False, output=512):
        return CognitiveBackendEntry(WireProvider(capacity, two), PrioritizedModelExecutor(),
            experience_ledger=self.fixture.ledger, context_length=capacity,
            inference_options=InferenceOptions(max_output_tokens=output), recent_dialogue_policy=policy)

    async def build(self, *, capacity=16384, n=None, overlap=2, request=None, output=512):
        # Large output reserve stresses optional-history trimming inside a 16K window.
        entry = self.entry(capacity, RecentDialoguePolicy(n, overlap), output=output)
        request = request or self.current()
        reader = await entry._read_recent_dialogue(request)
        self.assertIsInstance(reader, RecentDialogueTimeline)
        await entry.run(request)
        self.assertEqual(1, len(entry.provider.requests))
        model = entry.provider.requests[0]
        allocation = self.capture.records[-1]
        tokens = ContextTokenEstimator().estimate_text(encode(entry.provider.budget_payload(model)))
        self.assertEqual(tokens, allocation["estimated_total"])
        self.assertLessEqual(tokens, allocation["input_hard_cap"])
        self.assertLessEqual(tokens + allocation["reserved_output_tokens"] +
                             allocation["reserved_estimation_tokens"], capacity)
        self.assertEqual(768 if capacity == 8192 else 1536, allocation["reserved_estimation_tokens"])
        self.assertEqual(request.context, CognitiveModelRequestFactory.context_from(model))
        final = speeches(model)
        self.assertEqual(sorted(m["seq"] for m in final), [m["seq"] for m in final])
        self.assertEqual(len(final), len({m["seq"] for m in final}))
        available = {m["seq"] for m in final}
        for message in final:
            if set(dependencies(message)) - available:
                self.assertTrue(message["context_incomplete"])
        return reader, final, model

    def continuation(self, player="谈一点", normal="要加糖吗？", extra="不急，想好再说。"):
        _, parent, anchor = self.fixture.pair(1, text=player, reply=normal,
            conversation="old", session="old_session", clock=100)
        follow = self.fixture.request(2, conversation="old")
        self.fixture.append(follow, extra, response=True, session="old_session", clock=100.2,
            changes={"structured_payload": dict(initiative="dialogue_continuation", continuation_index=1,
                parent_request_id=parent.request_id, parent_event_id=parent.source_event_id,
                last_response_event_id=anchor.event_id, last_response_seq=anchor.experience_seq)})

    async def test_input_reply_continuation_are_three_distinct_speeches(self):
        self.continuation()
        reader, final, _ = await self.build()
        self.assertEqual(["player", "npc", "npc"], [m["role"] for m in final])
        self.assertEqual([1, 2, 3], [m["seq"] for m in final])
        self.assertEqual(2, final[-1]["anchor_seq"])
        self.assertEqual(1, final[1]["reply_to_seq"])
        self.assertEqual("old_session", reader.sources[3]["source_session_id"])
        self.assertEqual(100.2, reader.sources[3]["occurred_at"])
        self.assertEqual(reader.sources[1]["request_id"], reader.sources[3]["parent_request_id"])
        self.assertEqual(reader.sources[1]["source_event_id"], reader.sources[3]["parent_event_id"])
        self.assertEqual(reader.sources[2]["event_id"], reader.sources[3]["last_response_event_id"])
        self.assertNotIn("source_session_id", encode(final))

    async def test_seq_orders_interleaved_commit_and_cross_session_clocks(self):
        first = self.fixture.request(1, text="第一输入", conversation="old")
        second = self.fixture.request(2, text="第二输入", conversation="old")
        self.fixture.append(first, "第一输入", session="old", clock=100)
        self.fixture.append(second, "第二输入", session="old", clock=101)
        self.fixture.append(first, "较晚展示第一回复", response=True, session="old", clock=102)
        self.fixture.append(second, "第二回复", response=True, session="old", clock=103)
        self.fixture.pair(3, text="重置时钟的输入", reply="仍排在后面", session="another", clock=.1)
        reader, final, _ = await self.build()
        self.assertEqual([1, 2, 3, 4, 5, 6], [m["seq"] for m in final])
        self.assertEqual("较晚展示第一回复", final[2]["text"])
        self.assertEqual(1, final[2]["reply_to_seq"])
        self.assertLess(final[4]["occurred_at"], final[0]["occurred_at"])
        self.assertEqual("another", reader.sources[5]["source_session_id"])

    async def test_manual_n_8_16_32_and_capacity_defaults(self):
        for i in range(1, 21):
            self.fixture.pair(i, text=f"输入{i}", reply=f"回复{i}")
        request = self.current()
        for capacity, n in ((8192, None), (16384, None), (16384, 8), (8192, 16), (16384, 32)):
            with self.subTest(capacity=capacity, n=n):
                entry = self.entry(capacity, RecentDialoguePolicy(n))
                reader = await entry._read_recent_dialogue(request)
                target = n or (8 if capacity == 8192 else 16)
                self.assertEqual(target, reader.target_messages)
                self.assertLessEqual(len(reader), target + 2)
                self.assertLessEqual(reader.expanded_messages, 2)
                self.assertTrue(set(range(41-target, 41)) <= {m["seq"] for m in reader})
        manual = await self.entry(16384, RecentDialoguePolicy(8, 0))._read_recent_dialogue(request)
        self.assertEqual(8, len(manual))

    async def test_small_window_fails_before_call_and_16k_keeps_requested_history(self):
        for i in range(1, 21):
            self.fixture.pair(i, text=f"输入{i}", reply=f"回复{i}")
        request = self.current()
        small = self.entry(8192, RecentDialoguePolicy(32))
        with self.assertRaises(ContextBudgetExceeded) as failure:
            await small.run(request)
        self.assertEqual([], small.provider.requests)
        self.assertTrue(failure.exception.allocation.safe_failure)
        self.assertGreater(failure.exception.allocation.estimated_total,
                           failure.exception.allocation.input_hard_cap)
        reader, final, _ = await self.build(capacity=16384, n=32, request=request)
        self.assertEqual(34, len(reader))
        self.assertGreaterEqual(len(final), 32)

    async def test_latest_npc_question_is_available_to_current_short_answer(self):
        self.fixture.pair(1, text="甲" * 1000, reply="旧的简短回复")
        self.fixture.pair(2, text="我的习惯", reply="你喜欢加糖吗？")
        # Keep pressure below the mandatory contract; over-reservation is
        # independently required to fail before inference in test_context_budget_p1.
        reader, final, model = await self.build(capacity=16384, n=1, output=6144)
        self.assertIn("你喜欢加糖吗？", [m["text"] for m in final])
        self.assertLessEqual(len(reader), 3)
        self.assertNotIn("不喜欢。", [m["text"] for m in final])
        self.assertIn('"current_player_text":"不喜欢。"', model.messages[-1].content)

    async def test_two_boundary_slots_protect_npc_predecessor_of_input_only(self):
        self.fixture.pair(1, text="谈习惯", reply="需要加糖吗？", conversation="old")
        for number, text in ((2, "不要。"), (3, "补充一句。")):
            req = self.fixture.request(number, text=text, conversation="old")
            self.fixture.append(req, text, session="session_old")
        request = self.current()
        reader, final, _ = await self.build(n=1, request=request)
        self.assertEqual(2, reader.expanded_messages)
        self.assertEqual([2, 3, 4], [m["seq"] for m in final])
        no_overlap = await self.entry(16384, RecentDialoguePolicy(1, 0))._read_recent_dialogue(request)
        self.assertEqual([4], [m["seq"] for m in no_overlap])
        self.assertTrue(no_overlap.visible([4])[0]["context_incomplete"])

    async def test_continuation_boundary_expands_to_anchor_and_accepted_parent(self):
        self.continuation()
        reader, final, _ = await self.build(n=1)
        self.assertEqual(2, reader.expanded_messages)
        self.assertEqual([1, 2, 3], [m["seq"] for m in final])
        self.assertFalse(any(m.get("context_incomplete") for m in final))

    async def test_unaffordable_continuation_anchor_is_explicitly_incomplete(self):
        self.continuation(player="谈习惯", normal="甲" * 1000, extra="补充一句。")
        _, final, model = await self.build(capacity=16384, n=1, output=6144)
        self.assertIn(3, [m["seq"] for m in final])
        self.assertNotIn(2, [m["seq"] for m in final])
        self.assertTrue(next(m for m in final if m["seq"] == 3)["context_incomplete"])
        self.assertIn("PRIOR DIALOGUE COVERAGE: partial", model.messages[-1].content)

    async def test_long_old_message_cannot_evict_short_latest_question(self):
        self.fixture.pair(1, text="旧输入", reply="甲" * 1000)
        self.fixture.pair(2, text="谈现在", reply="要加糖吗？")
        _, final, _ = await self.build(capacity=16384, output=6144)
        self.assertIn("要加糖吗？", [m["text"] for m in final])
        self.assertNotIn("甲" * 1000, [m["text"] for m in final])
        self.assertTrue(any(m.get("context_incomplete") for m in final))

    async def test_identical_displayed_text_is_not_deduplicated(self):
        self.continuation(normal="同一句", extra="同一句")
        _, final, _ = await self.build()
        self.assertEqual(2, sum(m["text"] == "同一句" for m in final))
        self.assertNotIn("npc_utterances", encode(final))
        self.assertNotIn("npc_text", encode(final))

    async def test_unshown_reply_is_absent_and_ui_reader_is_never_used(self):
        value = self.fixture.request(1, text="已接受输入", conversation="old")
        self.fixture.append(value, "已接受输入", session="old")
        with patch.object(self.fixture.ledger, "read_dialogue_history", side_effect=AssertionError("UI used")):
            _, final, _ = await self.build()
        self.assertEqual(["player"], [m["role"] for m in final])
        self.assertEqual("已接受输入", final[0]["text"])

    async def test_cross_conversation_and_uid_npc_isolation(self):
        self.fixture.pair(1, text="A的历史", conversation="a", session="old", clock=100)
        self.fixture.pair(2, text="B私人", npc="npc_other", owner=("npc", "Character", "other"))
        self.fixture.pair(3, text="其他玩家私人", player=("player", "Player", "foreign"))
        _, final, model = await self.build()
        self.assertIn("A的历史", [m["text"] for m in final])
        self.assertNotIn("私人", encode(final))
        self.assertNotIn("merchant.private", model.messages[-1].content)

    async def test_untrusted_timeline_request_fails_closed(self):
        self.fixture.pair(1)
        request = self.current()
        entry = self.entry()
        self.assertEqual([], await entry._read_recent_dialogue(request.model_copy(update={"recall_identity": None})))
        self.assertEqual([], self.fixture.ledger.read_recent_dialogue("npc_cafe_01", "old", "x", "y", timeline=True))

    async def test_first_accepted_input_has_empty_timeline_with_configured_target(self):
        reader = await self.entry(16384, RecentDialoguePolicy(32))._read_recent_dialogue(self.current())
        self.assertIsInstance(reader, RecentDialogueTimeline)
        self.assertEqual([], reader)
        self.assertEqual(32, reader.target_messages)

    async def test_blank_legacy_input_does_not_create_an_independent_npc_speech_carrier(self):
        self.fixture.pair(1, text="   ", reply="不能提升为独立NPC发言")
        reader, final, _ = await self.build()
        self.assertEqual([], reader)
        self.assertEqual([], final)

    async def test_b_long_history_and_continuation_budget_fix_still_hold(self):
        self.continuation(player="甲" * 400, normal="乙" * 1000, extra="丙" * 1000)
        _, final, model = await self.build()
        self.assertEqual(3, len(final))
        self.assertEqual(1, encode(final).count("乙" * 1000))
        self.assertIn("NPC speech is not world fact/action completion", model.messages[-1].content)

    async def test_mandatory_overflow_still_happens_before_provider_call(self):
        self.fixture.pair(1)
        entry = self.entry(4096)
        with self.assertRaises(ContextBudgetExceeded):
            await entry.run(self.current())
        self.assertEqual([], entry.provider.requests)

    def test_policy_is_immutable_and_bounded_without_settings_persistence(self):
        self.assertEqual(8, RecentDialoguePolicy().limits(8192)["limit"])
        self.assertEqual(16, RecentDialoguePolicy().limits(16384)["limit"])
        self.assertEqual(64, RecentDialoguePolicy(64).limits(8192)["limit"])
        for invalid in (0, 65, True, 8.5):
            with self.assertRaises(ValueError):
                RecentDialoguePolicy(invalid)
        with self.assertRaises(AttributeError):
            RecentDialoguePolicy().target_messages = 32
