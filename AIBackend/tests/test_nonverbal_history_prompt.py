"""16K projection checks using frozen failure evidence and temporary storage; no model calls."""
import copy
import json
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.context_token_estimator import ContextTokenEstimator
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_dialogue_context import RecentDialogueTimeline
from app.recent_interactions import read_recent_interactions
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.sqlite_database import SQLiteDatabase
from tests.test_online_recent_dialogue_ordering import experience
from tests.test_recent_dialogue_cognition import request


# Interaction fragment captured from the actual HTTP POST at 2026-10-16T03:31:29Z.
# Other current-context fields use the existing validated request fixture.
SOURCE = 'observation_source_20230d4063153a021a6085bc3ce26b6c225900d3200539be1be3f6c67ca6c4be'
TRACE = 'cognitive-trace-player-emote-14b1b68b011b441b9b4d035ae321aab4'
SHOWN_REPLY = '你好呀，欢迎光临！今天想喝点什么，还是先坐一会儿？'
REAL_FAILURE_INTERACTION = [
    dict(kind='player_emote', seq=3283, actor_entity_id='player_01', observer_entity_id='npc_cafe_01',
         action='wave', social_role='addressed', meaning='waving_greeting', historical_only=True,
         target_entity_id='npc_cafe_01', target_epistemic_status='inferred_from_visible_facing',
         source_event_id=SOURCE, event_id='exp_ed8bf1f1bcb66a07b67752b16fe7a1d923c2ac9f4946e16ea54893deb2c2cf6f',
         trace_id=TRACE, occurred_at=15.062323604705853),
    dict(kind='npc_social_reply', seq=3284, actor_entity_id='npc_cafe_01', reply_to_seq=3283, text=SHOWN_REPLY,
         event_id='exp_39b685022687e16f3b8656ab6bdac00563d9208d9342f521f3adc676d7292ba1',
         source_event_id=SOURCE, trace_id=TRACE, request_id='cognitive-request-c4027961b9d04267bef1d0c5fd00168a',
         run_id='e762229a2dc94494b7d54a6751e0e688', occurred_at=17.8832373047062,
         model_content_is_not_world_truth=True),
]


class NonverbalHistoryPromptTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.database = SQLiteDatabase(Path(directory.name) / 'p1e.sqlite3')
        self.ledger = ExperienceLedger(self.database.connect)
        self.writer = ExperienceIngestionService(self.ledger)

    def append(self, value):
        result = self.writer.append(ExperienceAppendRequest.model_validate(value))
        self.assertEqual(result.status, 'inserted')
        return result

    @staticmethod
    def current_request(observer):
        value = request('我刚刚做了什么动作', agent_id=observer)
        # This trusted identity is intentionally excluded from ordinary model dumps.
        return dict(value.model_dump(), recall_identity=value.recall_identity.model_dump())

    def store_interaction(self, fragment):
        """Reconstruct typed drafts from the frozen fragment; Ledger owns IDs and seq."""
        action, *replies = copy.deepcopy(fragment)
        observer = action['observer_entity_id']
        common = experience(self.current_request(observer), '后续追问')['draft']
        common.update(event_type='observation', source_type='observation', conversation_id=None,
            request_id=None, source_event_id=action['source_event_id'], trace_id=action['trace_id'],
            source_observation_ids=['observation_' + observer],
            target_entity_ids=[action['target_entity_id']] if action['target_entity_id'] else [],
            natural_language_payload=None, occurred_at=action['occurred_at'],
            observed_at=action['occurred_at'], provenance='WITNESSED',
            structured_payload=dict(perception_revision=1, known_fields=dict(
                event_type='player.emote', emote_id=action['action'], emote_meaning=action['meaning'],
                social_observer_entity_id=observer, social_role=action['social_role'],
                social_response_eligible='true' if replies else 'false',
                **({key: action[key] for key in ('target_entity_id', 'target_epistemic_status')}
                   if action['target_entity_id'] else {}))))
        stored = self.append(dict(request_id='append_action_' + observer,
            trace_id=action['trace_id'], draft=common))
        action.update(seq=stored.experience_seq, event_id=stored.event_id)
        for reply in replies:
            draft = dict(common, event_type='social_response', source_type='social_interaction',
                actor_entity_id=observer, target_entity_ids=['player_01'],
                request_id=reply['request_id'], natural_language_payload=reply['text'],
                occurred_at=reply['occurred_at'], observed_at=reply['occurred_at'],
                provenance='EXECUTED_BY_SELF', structured_payload=dict(
                    parent_source_event_id=action['source_event_id'], emote_id=action['action'],
                    run_id=reply['run_id'], actually_presented=True, model_content_is_not_world_truth=True))
            stored = self.append(dict(request_id='append_reply_' + observer,
                trace_id=action['trace_id'], draft=draft))
            reply.update(seq=stored.experience_seq, event_id=stored.event_id, reply_to_seq=action['seq'])
        return [action, *replies]

    def project(self, observer='npc_cafe_01'):
        current = self.current_request(observer)
        draft = experience(current, '我刚刚做了什么动作')
        self.append(draft)
        identities = {p['entity_id']: (p['identity_namespace'], p['identity_type'], p['persistent_id'])
                      for p in draft['draft']['persistent_participants']}
        return read_recent_interactions(self.ledger, observer, draft['draft']['conversation_id'],
            current['request_id'], current['context']['current_event']['event_id'], identities)

    def build(self, *, interactions=None, speech=None, observer='npc_cafe_01'):
        adapter = DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid',
            api_key='test-only', model='prompt-contract')
        model = CognitiveModelRequestFactory.create_event(
            request('我刚刚做了什么动作', agent_id=observer),
            recent_interactions=interactions, recent_dialogue=speech,
            inference_options=InferenceOptions(max_output_tokens=512), knowledge_context_limit=16384,
            budget_payload_builder=adapter.budget_payload)
        wire = adapter.budget_payload(model)  # Same pure body builder used by the production HTTP POST.
        tokens = ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(',', ':')))
        self.assertLessEqual(tokens + 512 + 1536, 16384)
        self.wire_tokens = tokens
        return wire['input'][0]['content'], wire['input'][-1]['content']

    @staticmethod
    def section(user, marker):
        return json.JSONDecoder().raw_decode(user.split(marker, 1)[1])[0]

    def test_T1_real_failure_evidence_has_explicit_nonverbal_history_contract_in_16k_wire(self):
        # Preserve the captured ordering while IDs are generated by the isolated Ledger.
        with closing(self.database.connect()) as connection, connection:
            connection.execute("INSERT INTO experience_observer_sequences VALUES(?, ?)", ('npc_cafe_01', 3282))
        original = self.store_interaction(REAL_FAILURE_INTERACTION)
        projected = self.project()
        self.assertEqual([{k: v for k, v in item.items() if k != 'history_description'}
                          for item in projected[0]], original)
        self.assertIn('此前亲眼看到玩家（player_01）做出挥手动作', projected[0][0]['history_description'])
        self.assertIn('npc_cafe_01', projected[0][0]['history_description'])
        self.assertIn('历史回复：随后', projected[0][1]['history_description'])
        self.assertNotIn('向你', projected[0][0]['history_description'])
        speech = RecentDialogueTimeline([
            dict(seq=3280, role='player', text='我喜欢拿铁，不加糖。'),
            dict(seq=3281, role='npc', text='我这边没有你刚才动作的记录。', reply_to_seq=3280),
        ])
        system, user = self.build(interactions=projected, speech=speech)
        self.assertIn('Use sufficient recent speech or typed interaction evidence directly', system)
        self.assertIn('player_emote records an action actually performed by actor_entity_id and perceived by observer_entity_id, not player speech', system)
        self.assertIn("npc_social_reply records the NPC's actually displayed reply linked to that action", system)
        self.assertIn("it proves the reply, not the player's action on its own", system)
        self.assertIn('equally valid historical evidence for the facts their types support', system)
        self.assertIn('it is not required to confirm an event in trusted observer-local history', system)
        self.assertIn('missing speech or long-term Memory does not invalidate an observed interaction', system)
        self.assertIn('PRIOR INTERACTION TIMELINE: observer_entity_id=npc_cafe_01', user)
        self.assertIn('all entries precede the current player input', user)
        self.assertIn('ascending seq is Ledger commit order; times may reset across sessions', user)
        self.assertIn('Action/reply links use source_event_id and reply_to_seq', user)
        entries = self.section(user, 'PRIOR INTERACTION TIMELINE JSON: ')
        self.assertEqual(entries, [dict(kind='speech', **item) for item in speech.visible([3280, 3281])] + projected[0])
        self.assertNotIn('text', entries[2])
        self.assertEqual(user.count(SHOWN_REPLY), 1)
        self.assertNotIn('PRIOR DIALOGUE TURNS JSON:', user)
        self.assertNotIn('表情[挥手]', user)
        serialized_size = lambda group: len(json.dumps(group, ensure_ascii=False, separators=(',', ':')))
        self.assertLessEqual(sum(map(serialized_size, projected)), 2400)
        # Extra description bytes must count against the existing cap: omit the whole pair.
        from unittest.mock import patch
        original_size = serialized_size(original)
        with patch('app.recent_interactions.json.dumps', side_effect=lambda group, **kw:
                   'x' * (2401 if 'history_description' in group[0] else original_size)):
            identities = {'npc_cafe_01': ('npc', 'Character', 'merchant.private'),
                          'player_01': ('player', 'Player', 'cafe-local-player-001')}
            self.assertEqual(read_recent_interactions(self.ledger, 'npc_cafe_01', 'conversation_1',
                'request_current', 'event_01', identities), [])

    def test_T2_speech_semantics_and_observer_local_uncertainty_are_preserved(self):
        speech = RecentDialogueTimeline([
            dict(seq=3280, role='player', text='我喜欢拿铁，不加糖。'),
            dict(seq=3281, role='npc', text='好，记住啦。', reply_to_seq=3280),
        ])
        system, plain = self.build(speech=speech)
        expected = speech.visible([3280, 3281])
        self.assertEqual(self.section(plain, 'PRIOR DIALOGUE TURNS JSON: '), expected)
        self.assertNotIn('PRIOR INTERACTION TIMELINE JSON:', plain)
        self.assertIn('Use only your own observer-local history', system)
        self.assertIn('Preserve inferred/unknown target uncertainty', system)
        self.assertIn('action labels do not prove feelings, traits or achievements', system)
        self.assertIn('Missing history means uncertainty, not proof of absence', system)

        # No interaction supplied for an NPC that did not see the action: do not invent or import one.
        self.store_interaction(REAL_FAILURE_INTERACTION)
        self.assertEqual(self.project('npc_cafe_quiet_01'), [])
        _, unseen = self.build(observer='npc_cafe_quiet_01', interactions=[])
        self.assertNotIn('PRIOR INTERACTION TIMELINE JSON:', unseen)
        self.assertEqual(self.section(unseen, 'PRIOR DIALOGUE TURNS JSON: '), [])
        self.assertNotIn(SHOWN_REPLY, unseen)
        self.assertNotIn(TRACE, unseen)
        self.assertNotIn(SOURCE, unseen)

        # The already-authorized projection of a different observer contains only its own observation.
        bystander = dict(REAL_FAILURE_INTERACTION[0], observer_entity_id='npc_cafe_regular_01',
            social_role='bystander', target_entity_id=None, target_epistemic_status='unknown',
            source_event_id='observation_source_bystander', event_id='exp_bystander')
        original = self.store_interaction([bystander])
        projected = self.project('npc_cafe_regular_01')
        self.assertEqual([{k: v for k, v in item.items() if k != 'history_description'}
                          for item in projected[0]], original)
        _, other = self.build(observer='npc_cafe_regular_01', interactions=projected)
        self.assertEqual(self.section(other, 'PRIOR INTERACTION TIMELINE JSON: '), projected[0])
        self.assertIn('observer_entity_id=npc_cafe_regular_01', other)
        self.assertNotIn(SHOWN_REPLY, other)
        self.assertNotIn('npc_social_reply', json.dumps([bystander]))
        description = projected[0][0]['history_description']
        self.assertIn('npc_cafe_regular_01', description)
        self.assertNotIn('npc_cafe_01', description)
        self.assertNotIn('向你', description)
        for action, label in (('cheer', '庆祝'), ('flex', '展示肌肉')):
            observer = 'npc_witness_' + action
            fragment = dict(bystander, observer_entity_id=observer, action=action,
                meaning='celebrating' if action == 'cheer' else 'showing_off',
                source_event_id='observation_source_' + action)
            self.store_interaction([fragment])
            projected = self.project(observer)
            _, wire = self.build(observer=observer, interactions=projected)
            items = self.section(wire, 'PRIOR INTERACTION TIMELINE JSON: ')
            self.assertEqual(len(items), 1)
            self.assertEqual(items[0]['action'], action)
            self.assertIn(label, items[0]['history_description'])
            self.assertNotIn('npc_cafe_01', items[0]['history_description'])
            self.assertIsNone(items[0]['target_entity_id'])
            self.assertEqual(items[0]['target_epistemic_status'], 'unknown')
            self.assertNotIn(SHOWN_REPLY, wire)
