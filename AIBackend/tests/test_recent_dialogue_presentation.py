"""Clock presentation only: no semantic continuity or correction classification."""
import copy
import json
import unittest

from app.contracts import CognitiveRequest
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_dialogue_context import RecentDialogueTimeline, render_dialogue_timeline
from tests import test_cross_conversation_recent_dialogue as fixtures


class RecentDialoguePresentationTests(unittest.TestCase):
    def timeline(self, *, reference=None, source=None, message=None):
        row = dict(seq=1, role='npc', text='原文："米色"。\n下一行', occurred_at=100.125, reply_to_seq=0)
        row.update(message or {})
        metadata = dict(source_session_id='visit', clock_domain='unity_realtime', occurred_at=row['occurred_at'])
        metadata.update(source or {})
        return RecentDialogueTimeline([row], sources={1: metadata}, current_session_id='visit',
            reference_clock=reference or dict(source_session_id='visit', clock_domain='unity_realtime', occurred_at=140.25))

    def test_elapsed_clock_and_original_fields_round_trip_without_mutation(self):
        timeline = self.timeline()
        before = copy.deepcopy((list(timeline), timeline.sources))
        visible = timeline.visible([1])
        self.assertEqual(40.125, visible[0]['seconds_before_current_input'])
        self.assertEqual(timeline[0], {key: visible[0][key] for key in timeline[0]})
        self.assertEqual(visible, json.loads(render_dialogue_timeline(visible)))
        self.assertEqual(before, (list(timeline), timeline.sources))
        self.assertTrue(visible[0]['context_incomplete'])

    def test_missing_uncomparable_or_invalid_time_never_gets_an_age(self):
        variations = [
            dict(source=dict(source_session_id='other')), dict(source=dict(clock_domain='backend_utc')),
            dict(source=dict(clock_domain=None)), dict(source=dict(occurred_at=None)),
            dict(source=dict(occurred_at=float('nan'))), dict(source=dict(occurred_at=141)),
            dict(source=dict(occurred_at=True)), dict(source=dict(occurred_at=-1)),
            dict(source=dict(occurred_at=100)),
            dict(reference=dict(source_session_id='visit', clock_domain='unknown', occurred_at=140.25)),
            dict(reference=dict(source_session_id='visit', clock_domain='unity_realtime', occurred_at=float('inf'))),
        ]
        for case in variations:
            with self.subTest(case=case):
                self.assertNotIn('seconds_before_current_input', self.timeline(**case).visible([1])[0])
        timeline = self.timeline(); timeline.reference_clock = None
        self.assertNotIn('seconds_before_current_input', timeline.visible([1])[0])

    def test_clock_distance_does_not_use_conversation_or_reorder_equal_text(self):
        timeline = self.timeline(source=dict(conversation_id='closed_ui'))
        later = dict(seq=2, role='npc', text=timeline[0]['text'], occurred_at=101.0, anchor_seq=1)
        timeline.append(later)
        timeline.sources[2] = dict(timeline.sources[1], occurred_at=101.0, conversation_id='reopened_ui')
        visible = timeline.visible([1, 2])
        self.assertEqual([1, 2], [row['seq'] for row in visible])
        self.assertEqual([40.125, 39.25], [row['seconds_before_current_input'] for row in visible])
        self.assertEqual([row['text'] for row in timeline], [row['text'] for row in json.loads(render_dialogue_timeline(visible))])
        self.assertEqual(2, render_dialogue_timeline(visible).count('"role":"npc"'))
        self.assertEqual('[]', render_dialogue_timeline([]))

    def test_readable_projection_handles_backend_clock_without_wall_time_invention(self):
        timeline = self.timeline(source=dict(clock_domain='backend_utc'),
            reference=dict(source_session_id='visit', clock_domain='backend_utc', occurred_at=140.25))
        self.assertEqual(40.125, timeline.visible([1])[0]['seconds_before_current_input'])
        # No conversion of a monotonic clock to a fabricated clock-of-day.
        self.assertNotIn('18:', render_dialogue_timeline(timeline.visible([1])))


class RecentDialoguePresentationLedgerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.fixture = fixtures.CrossConversationRecentDialogueTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)

    def history(self):
        self.fixture.pair(1, text='旧会话同主题', reply='旧会话回复', session='previous', clock=1)
        self.fixture.pair(2, text='你喜欢什么颜色？', reply='偏暖的米色。', session='visit', conversation='closed_ui', clock=100)
        value = self.fixture.request(3, text='上次？不是刚说过吗？', conversation='reopened_ui')
        value['context']['current_event']['occurred_at'] = 140
        self.fixture.append(value, '上次？不是刚说过吗？', session='visit', clock=140)
        return value, self.fixture.read(value, timeline=True, limit=16, max_chars=2400)

    def test_accepted_cutoff_clock_ui_crossing_and_earlier_session(self):
        value, history = self.history()
        visible = history.visible(row['seq'] for row in history)
        self.assertEqual([1, 2, 3, 4], [row['seq'] for row in visible])
        self.assertEqual([40, 39.9], [row['seconds_before_current_input'] for row in visible[2:]])
        self.assertTrue(all('seconds_before_current_input' not in row for row in visible[:2]))
        self.assertEqual(140, history.reference_clock['occurred_at'])
        self.assertEqual('你喜欢什么颜色？', visible[2]['text'])
        self.assertEqual('偏暖的米色。', visible[3]['text'])
        self.assertEqual(3, visible[3]['reply_to_seq'])
        self.assertNotIn(value['request_id'], [src['request_id'] for src in history.sources.values()])

    def test_final_16k_32k_single_timeline_preserves_sources_and_current_input(self):
        value, history = self.history()
        request = CognitiveRequest.model_validate(value)
        for capacity in (16384, 32768):
            with self.subTest(capacity=capacity):
                model = CognitiveModelRequestFactory.create_event(request, recent_dialogue=history,
                    knowledge_context_limit=capacity, inference_options=InferenceOptions(max_output_tokens=512))
                body = model.messages[-1].content
                visible = json.JSONDecoder().raw_decode(body.split('PRIOR DIALOGUE TURNS JSON: ', 1)[1])[0]
                self.assertEqual(history.visible(row['seq'] for row in history), visible)
                self.assertEqual(1, body.count('偏暖的米色。'))
                self.assertNotIn('上次？不是刚说过吗？', [row['text'] for row in visible])
                self.assertIn('[\n{"seq":', body)
                self.assertNotIn('source_session_id', json.dumps(visible))
                self.assertIn('MemoryRecallPlan', model.response_schema['$defs'])

    async def test_identity_isolation_still_precedes_projection(self):
        value, history = self.history()
        self.assertEqual(4, len(history))
        value['recall_identity']['current_subjects'][0]['persistent_id'] = 'unrelated-player'
        self.assertEqual([], self.fixture.read(value, timeline=True))
        other = self.fixture.request(3, npc='npc_cafe_02')
        self.assertEqual([], self.fixture.read(other, timeline=True))
