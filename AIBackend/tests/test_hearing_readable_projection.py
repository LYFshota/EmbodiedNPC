"""Only the hearing display projection: isolated Ledger, Scripted entry and 16K wire."""
import json
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.context_token_estimator import ContextTokenEstimator
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions
from app.sqlite_database import SQLiteDatabase
from tests.test_cross_conversation_recent_dialogue import ScriptedProvider
from tests.test_online_recent_dialogue_ordering import cognitive, experience

OBSERVER = 'npc_cafe_companion_01'
SPEAKER = 'npc_cafe_regular_01'
REPLY = '早。我挺好的，坐着慢慢喝点东西。你呢，今天过得怎么样？'


class HearingReadableProjectionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.ledger = ExperienceLedger(SQLiteDatabase(Path(directory.name) / 'hearing.sqlite3').connect)
        self.writer = ExperienceIngestionService(self.ledger)
        self.request = cognitive(10, '你有听到我和知意在聊什么吗', agent=OBSERVER)
        self.request['context']['current_event']['target_entity_id'] = OBSERVER

    def append(self, value):
        ack = self.writer.append(ExperienceAppendRequest.model_validate(value))
        self.assertEqual(ack.status, 'inserted', ack.reason)
        self.assertEqual(self.writer.append(ExperienceAppendRequest.model_validate(value)).status, 'already_exists')

    def hearing(self, number, actor, target, text, status='identified_from_speech_event'):
        value = experience(self.request, text)
        source = f'speech_{number}'
        fields = dict(event_type='heard_speech', observer_entity_id=OBSERVER, text=text,
            social_role='bystander', occurrence_id=source, parent_source_event_id='speech_parent',
            source_channel='Audio', model_content_is_not_world_truth='true', actor_entity_id=actor)
        if target:
            fields.update(target_entity_id=target, target_epistemic_status=status)
        draft = value['draft']
        draft.update(event_type='observation', source_type='observation', source_event_id=source,
            source_observation_ids=[source + '_audio'], actor_entity_id=actor,
            target_entity_ids=[target] if target else [], request_id=None, conversation_id=None,
            occurred_at=float(number), observed_at=float(number), provenance='HEARD',
            natural_language_payload=None,
            structured_payload=dict(perception_revision=number, source_channel='Audio', known_fields=fields))
        draft['persistent_participants'].append(dict(entity_id=SPEAKER, identity_namespace='npc',
            identity_type='Character', persistent_id='test_shen'))
        value['request_id'] = source
        return value

    async def wire(self, request=None):
        request = request or self.request
        self.append(experience(request, '你有听到我和知意在聊什么吗'))
        provider = ScriptedProvider([dict(decision_kind='NO_ACTION')])
        adapter = DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid', api_key='offline-only', model='offline')
        await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger,
            context_length=16384, inference_options=InferenceOptions(max_output_tokens=512)).run(
                CognitiveRequest.model_validate(request))
        wire = adapter.budget_payload(provider.requests[0])
        tokens = ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(',', ':')))
        self.assertLessEqual(tokens + 512 + 1536, 16384)
        user = wire['input'][-1]['content']
        marker = 'PRIOR INTERACTION TIMELINE JSON: '
        items = json.JSONDecoder().raw_decode(user.split(marker)[1])[0] if marker in user else []
        print(f'16K wire: input={tokens}, output=512, margin=1536, total={tokens+2048}')
        return user, items

    async def test_T1_heard_words_and_reply_are_readable_without_changing_evidence(self):
        prior = cognitive(1, '你好', agent=OBSERVER)
        self.append(experience(prior, '你好'))
        self.append(experience(prior, '你好呀', response=True))
        for number, actor, target, text in ((2, 'player_01', SPEAKER, '早啊'), (3, SPEAKER, 'player_01', REPLY)):
            self.append(self.hearing(number, actor, target, text))
        with closing(self.ledger._connection_factory()) as connection:
            original = connection.execute("SELECT event_id,payload_json FROM experience_events WHERE source_type='observation' ORDER BY experience_seq").fetchall()
            original = [tuple(row) for row in original]
        user, items = await self.wire()
        with closing(self.ledger._connection_factory()) as connection:
            self.assertEqual([tuple(row) for row in connection.execute(
                "SELECT event_id,payload_json FROM experience_events WHERE source_type='observation' ORDER BY experience_seq")], original)
        heard = [i for i in items if i['kind'] == 'heard_speech']
        self.assertEqual(len(heard), 2)
        self.assertTrue(any(i['kind'] == 'speech' for i in items))
        self.assertLessEqual(sum(len(json.dumps([i], ensure_ascii=False, separators=(',', ':'))) for i in heard), 2400)
        for index, item in enumerate(heard, 2):
            self.assertEqual(item['observer_entity_id'], OBSERVER)
            self.assertEqual(item['source_event_id'], f'speech_{index}')
            self.assertEqual(item['parent_source_event_id'], 'speech_parent')
            self.assertEqual(item['target_epistemic_status'], 'identified_from_speech_event')
            self.assertTrue(item['historical_only'] and item['model_content_is_not_world_truth'])
            self.assertNotIn(item['text'], item['history_description'])
            self.assertIn('text字段', item['history_description'])
            self.assertIn('此前实际听到', item['history_description'])
            self.assertNotIn('似乎', item['history_description'])
            self.assertEqual(user.count('"occurrence_id":"' + item['occurrence_id'] + '"'), 1)
        self.assertEqual([i['actor_entity_id'] for i in heard], ['player_01', SPEAKER])
        self.assertEqual([i['target_entity_id'] for i in heard], [SPEAKER, 'player_01'])
        self.assertEqual([i['text'] for i in heard], ['早啊', REPLY])
        self.assertEqual(user.count(REPLY), 1)

    async def test_T2_uncertain_target_and_observer_isolation(self):
        unknown = self.hearing(2, 'player_01', None, '这句话没有明确对象')
        unknown['draft']['persistent_participants'] = [
            p for p in unknown['draft']['persistent_participants'] if p['entity_id'] != SPEAKER]
        self.append(unknown)
        self.append(self.hearing(3, 'player_01', SPEAKER, '这句话对象只是推断', 'inferred_from_visible_interaction'))
        user, items = await self.wire()
        heard = [i for i in items if i['kind'] == 'heard_speech']
        self.assertEqual(len(heard), 2)
        self.assertIsNone(heard[0]['target_entity_id'])
        self.assertIn('直接对象未确认', heard[0]['history_description'])
        self.assertNotIn(SPEAKER, heard[0]['history_description'])
        self.assertIn('似乎在对', heard[1]['history_description'])
        self.assertNotIn('事件确认', heard[1]['history_description'])
        other = cognitive(11, '我刚才和谁说了什么', agent='npc_cafe_quiet_01')
        other['context']['current_event']['target_entity_id'] = 'npc_cafe_quiet_01'
        other['recall_identity']['observer']['persistent_id'] = 'different_npc'
        other_user, other_items = await self.wire(other)
        self.assertFalse(any(i['kind'] == 'heard_speech' for i in other_items))
        self.assertNotIn('这句话没有明确对象', other_user)
        self.assertNotIn('这句话对象只是推断', other_user)

