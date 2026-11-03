"""Explanation/delta independence; fake outputs test mechanics, not LLM judgment."""
import asyncio
import json
import unittest

from pydantic import ValidationError

from app.relationship_reflection_model import RelationshipReflectionModel, RelationshipReflectionModelResponse
from app.relationship_reflection_repository import RelationshipReflectionRepository
from app.relationship_reflection_worker import RelationshipReflectionWorker
from app.relationship_summary_reader import RelationshipSummaryReader
from tests import test_relationship_pair_state as pair
from tests.test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner


# Exact structured output retained from the previous real DeepSeek drink_only failure.
DRINK_EXPLANATION = '{"outcome":"INTERPRETATION","interpretation":"玩家自述饮品偏好，属具体记忆而非关系风格","state_updates":[]}'


class RelationshipP1ContractTests(unittest.TestCase):
    def setUp(self):
        self.f = pair.RelationshipPairStateTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)

    def execute(self, candidate, raw, repo=None):
        provider = FakeProvider([raw])
        worker = RelationshipReflectionWorker(StubScanner(candidate),
            RelationshipReflectionModel(provider, RecordingExecutor()), repo or self.f.repo)
        return worker, provider, asyncio.run(worker.process_batch())

    def test_schema_and_parser_allow_explanation_without_delta(self):
        candidate = self.f.candidate('我平时喜欢无糖热茶。', npc_text='我平时也爱清淡的茶。')
        provider = FakeProvider([DRINK_EXPLANATION])
        response = asyncio.run(RelationshipReflectionModel(provider, RecordingExecutor()).interpret(candidate))
        self.assertEqual((response.outcome, response.state_updates), ('INTERPRETATION', ()))
        schema = provider.requests[0].response_schema
        self.assertEqual(schema['properties']['state_updates']['maxItems'], 3)
        self.assertEqual(schema['properties']['state_updates'].get('minItems', 0), 0)
        self.assertIn('state_updates', schema['required'])
        self.assertIn('audit-only', schema['properties']['state_updates']['description'])

    def test_audit_only_and_no_change_preserve_items_and_are_deduplicated_after_restart(self):
        for seeded in (False, True):
            for raw in (DRINK_EXPLANATION,
                        '{"outcome":"NO_CHANGE","interpretation":null,"state_updates":[]}'):
                with self.subTest(seeded=seeded, raw=raw):
                    if seeded and not self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER).items:
                        self.f.commit(self.f.candidate('别追问我的家庭。'),
                            self.f.response('不追问家庭话题。', quote='别追问我的家庭。'))
                    previous = self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER)
                    candidate = self.f.candidate('我平时喜欢无糖热茶。', npc_text='我平时也爱清淡的茶。')
                    worker, provider, batch = self.execute(candidate, raw)
                    self.assertEqual((batch.persisted, batch.issues), (1, ()))
                    state = self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER)
                    self.assertEqual(state.items, previous.items)  # including item IDs and source anchors
                    self.assertFalse(state.changed)
                    self.assertEqual(state.capacity_limited, ())
                    self.assertEqual(state.revision, previous.revision + 1)
                    self.assertGreater(state.watermark, previous.watermark)
                    self.assertIsNotNone(state.interaction_evidence)
                    record = self.f.repo.get(worker.reflection_id_for(candidate))
                    self.assertEqual(record.interpretation, json.loads(raw)['interpretation'])
                    self.assertNotIn('饮品偏好', RelationshipSummaryReader(self.f.repo)
                                     .get_for_cognition(pair.NPC, pair.PLAYER).model_dump_json())
                    worker.scanner.begin_new_round()
                    self.assertEqual(asyncio.run(worker.process_batch()).already_processed, 1)
                    self.assertEqual(len(provider.requests), 1)
                    reopened = RelationshipReflectionRepository(self.f.db.connect)
                    _, new_provider, again = self.execute(candidate, raw, reopened)
                    self.assertEqual((again.already_processed, again.persisted), (1, 0))
                    self.assertEqual(new_provider.requests, [])
                    self.assertEqual(reopened.read_current_pair_state(pair.NPC, pair.PLAYER), state)

    def test_empty_deltas_do_not_make_invalid_outputs_valid(self):
        for payload in (
            dict(outcome='INTERPRETATION', interpretation=None, state_updates=[]),
            dict(outcome='INTERPRETATION', interpretation='   ', state_updates=[]),
            dict(outcome='NO_CHANGE', interpretation='有解释', state_updates=[]),
            dict(outcome='UNKNOWN', interpretation='有解释', state_updates=[]),
            dict(outcome='INTERPRETATION', interpretation='有解释', state_updates=None),
            dict(outcome='INTERPRETATION', interpretation='有解释', state_updates=[], extra=True),
        ):
            with self.subTest(payload=payload), self.assertRaises(ValidationError):
                RelationshipReflectionModelResponse.model_validate(payload)

    def test_subjective_add_then_new_support_can_refine_without_explicit_player_confirmation(self):
        # Scripted social judgments demonstrate that source/CAS rules permit these deltas.
        # They do not assert that a real model will choose them; live smoke evaluates that.
        words = '你的看法我没全想通，但想听听你为什么这样想。'
        candidate = self.f.candidate(words, npc_text='我们可以慢慢讨论分歧。')
        initial = self.f.response('玩家似乎愿意和我交换不同看法，了解仍有限。',
            quote=words, kind='TENTATIVE')
        _, _, batch = self.execute(candidate, initial.model_dump_json())
        self.assertEqual(batch.persisted, 1)
        previous = self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER)
        words = '我回去琢磨了你的理由，还想听你怎么看另一面，我也有不同的想法。'
        response = self.f.response('玩家会继续思考并主动交流分歧，彼此讨论逐渐自然。',
            quote=words, operation='CORRECT', target='s0', kind='TENTATIVE')
        _, _, batch = self.execute(self.f.candidate(words), response.model_dump_json())
        state = self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER)
        self.assertEqual(batch.persisted, 1)
        self.assertEqual(len(state.items), 1)
        self.assertEqual(state.items[0].predecessor_id, previous.items[0].item_id)
        self.assertEqual(state.items[0].text, response.state_updates[0].text)
        self.assertNotEqual(state.items[0].evidence, previous.items[0].evidence)

    def test_repeated_explanations_do_not_upgrade_tentative_state(self):
        self.f.commit(self.f.candidate('有空聊聊。'), self.f.response(
            '愿意进行日常交流，了解仍有限。', quote='有空聊聊。', kind='TENTATIVE'))
        previous = self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER)
        raw = RelationshipReflectionModelResponse(outcome='INTERPRETATION',
            interpretation='一次普通寒暄，没有新的长期关系变化。', state_updates=()).model_dump_json()
        for _ in range(3):
            _, _, batch = self.execute(self.f.candidate('你好。'), raw)
            self.assertEqual(batch.persisted, 1)
            self.assertEqual(self.f.repo.read_current_pair_state(pair.NPC, pair.PLAYER).items, previous.items)


if __name__ == '__main__':
    unittest.main()
