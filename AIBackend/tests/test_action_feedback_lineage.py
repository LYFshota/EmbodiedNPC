"""Displayed outcome feedback is linked speech, never a fabricated new player input."""
import copy
import json
import tempfile
import unittest
from pathlib import Path
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.sqlite_database import SQLiteDatabase
from tests.test_online_recent_dialogue_ordering import cognitive, experience

class ActionFeedbackLineageTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.ledger=ExperienceLedger(SQLiteDatabase(Path(self.temp.name)/'ledger.sqlite3').connect)
        self.writer=ExperienceIngestionService(self.ledger)
        self.root=cognitive(1,'Please bring a permitted cup.')
        self.input=experience(self.root,'Please bring a permitted cup.')
        self.ordinary=experience(self.root,'I will try.',response=True)
        self.goal=copy.deepcopy(self.ordinary)
        self.goal.update(request_id='append_failed_outcome')
        self.goal['draft'].update(event_type='goal_failed',source_type='goal_outcome',request_id=None,
            conversation_id=None,source_event_id='failed_outcome_r0',natural_language_payload=None,
            occurred_at=1.3,observed_at=1.3,structured_payload=dict(status='Failed',proposal_id='proposal_root',
                proposal_revision=0,failure_code='NO_PLAN',goal_type='bring_object'))
        self.reply=copy.deepcopy(self.ordinary)
        self.reply.update(request_id='append_feedback')
        self.reply['draft'].update(request_id='feedback_request',source_event_id='failed_outcome_r0',
            occurred_at=1.4,observed_at=1.4,natural_language_payload='That attempt did not finish. Which cup would you prefer?',
            structured_payload=dict(direction='npc_to_player',initiative='action_feedback',
                parent_request_id=self.root['request_id'],parent_event_id=self.root['context']['current_event']['event_id']))
        self.next=cognitive(2,'What happened?')
        self.cutoff=experience(self.next,'What happened?')
        self.identities={p['entity_id']:(p['identity_namespace'],p['identity_type'],p['persistent_id'])
                         for p in self.input['draft']['persistent_participants']}
    def append(self,value):return self.writer.append(ExperienceAppendRequest.model_validate(value))
    def seed(self,goal=True):
        for value in (self.input,self.ordinary,*([self.goal] if goal else []),self.reply,self.cutoff):
            self.assertEqual(self.append(value).status,'inserted')
    def read(self,timeline=True):
        return self.ledger.read_recent_dialogue('npc_cafe_01','conversation_b1',self.next['request_id'],
            self.next['context']['current_event']['event_id'],identities=self.identities,timeline=timeline,limit=16,max_chars=2400)
    def test_displayed_feedback_is_one_identity_verified_speech_in_next_timeline(self):
        self.seed()
        self.assertEqual(self.append(self.reply).status,'already_exists')
        result=self.read()
        self.assertEqual([r['text'] for r in result],['Please bring a permitted cup.','I will try.',self.reply['draft']['natural_language_payload']])
        self.assertEqual(self.read(False)[0]['npc_utterances'],['I will try.',self.reply['draft']['natural_language_payload']])
        self.assertEqual(len([r for r in self.ledger.read_range('npc_cafe_01') if r.source_type=='goal_outcome']),1)
    def test_missing_outcome_does_not_certify_feedback(self):
        self.seed(goal=False)
        self.assertNotIn(self.reply['draft']['natural_language_payload'],json.dumps(self.read()))
    def test_cancelled_or_successful_outcome_cannot_certify_failed_feedback(self):
        self.goal['draft'].update(event_type='goal_cancelled',structured_payload=dict(status='Cancelled',proposal_revision=0))
        self.seed();self.assertEqual(len(self.read()),2)
    def test_trace_mismatch_cannot_attach_old_feedback_to_new_request(self):
        self.goal['draft']['trace_id']='foreign_trace';self.seed();self.assertEqual(len(self.read()),2)
    def test_identity_mismatch_cannot_attach_foreign_feedback(self):
        self.reply['draft']['persistent_participants'][0]['persistent_id']='foreign_npc'
        self.seed();self.assertEqual(len(self.read()),2)
    def test_unlinked_legacy_speech_is_not_fabricated_into_a_pair(self):
        self.reply['draft']['structured_payload']={'direction':'npc_to_player'}
        self.seed();self.assertEqual(len(self.read()),2)
    def test_feedback_requires_explicit_parent_and_distinct_request(self):
        self.reply['draft']['structured_payload'].pop('parent_event_id')
        with self.assertRaises(ValueError):ExperienceAppendRequest.model_validate(self.reply)
