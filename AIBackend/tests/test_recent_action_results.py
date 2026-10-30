"""Execution evidence -> bounded native read -> actual Provider HTTP body."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

import httpx
from app.drink_types import DRINK_TYPE_IDS
from app.cognitive_entry import CognitiveBackendEntry
from app.context_budget_allocator import ContextBudgetExceeded
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveRequest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.experience_ledger import ExperienceLedger
from app.experience_ingestion import ExperienceIngestionService
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_action_results import MAX_RESULTS, MAX_CHARS, RecentInteractionGroups
from app.sqlite_database import SQLiteDatabase
from tests.test_recent_dialogue_cognition import request

MARKER = 'RECENT EXECUTED ACTION RESULTS JSON: '

def view(body):
    user = body['input'][-1]['content']
    return json.JSONDecoder().raw_decode(user.split(MARKER,1)[1])[0]


class RecentActionResultsTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.db = SQLiteDatabase(Path(self.temp.name)/'test.sqlite3')
        self.ledger = ExperienceLedger(self.db.connect)
        self.writer = ExperienceIngestionService(self.ledger)
        self.count = 0
        original = request('你不是已经做好了吗？')
        raw = original.model_dump()
        raw['recall_identity'] = original.recall_identity.model_dump()
        raw['context']['current_event'].update(occurred_at=1000.,world_revision=10,has_world_revision=True)
        self.req = CognitiveRequest.model_validate(raw)
        wire = self.req.recall_identity
        self.identities = {p.entity_id:(p.identity_namespace,p.identity_type,p.persistent_id)
                           for p in (wire.observer,*wire.current_subjects)}

    def event(self, status='Succeeded', *, proposal='proposal_one', revision=0, **changes):
        observer = self.req.run.agent_entity_id
        event = dict(schema_version='experience-draft/1.1',observer_id=observer,
            event_type='goal_'+{'Succeeded':'succeeded','Failed':'failed','Cancelled':'cancelled','TimedOut':'timed_out'}[status],
            source_type='goal_outcome',source_session_id='test_session',clock_domain='unity_realtime',
            source_event_id='outcome_'+str(self.count),source_observation_ids=[],actor_entity_id=observer,
            target_entity_ids=['player_01'],persistent_participants=[dict(entity_id=k,
                identity_namespace=v[0],identity_type=v[1],persistent_id=v[2]) for k,v in self.identities.items()],
            structured_payload=dict(goal_type='prepare_drink',status=status,failure_code='',
                destination_entity_id='player_01',proposal_id=proposal,proposal_revision=revision,
                drink_type='latte',drink_receipt_id='receipt_'+proposal),
            natural_language_payload=None,occurred_at=990.,observed_at=990.,world_revision=1,
            trace_id='trace_'+proposal,provenance='EXECUTED_BY_SELF')
        event.update(changes)
        return event

    def append(self, event):
        self.count += 1
        event=copy.deepcopy(event)
        event['source_event_id']='source_'+str(self.count)
        return self.writer.append(ExperienceAppendRequest.model_validate(dict(
            request_id='append_'+str(self.count),trace_id='append_trace_'+str(self.count),draft=event)))

    def boundary(self, **changes):
        event = self.req.context.current_event
        conversation = next(x.value for x in event.payload if x.key=='conversation_id')
        data = self.event()
        data.update(event_type='dialogue_input',source_type='dialogue',source_event_id=event.event_id,
                    actor_entity_id='player_01',target_entity_ids=[self.req.run.agent_entity_id],
                    structured_payload=dict(direction='player_to_npc'),natural_language_payload='你不是已经做好了吗？',
                    occurred_at=1000.,observed_at=1000.,world_revision=10,request_id=self.req.request_id,
                    trace_id=self.req.trace_id,conversation_id=conversation,provenance='SYSTEM_CONFIRMED')
        data.update(changes)
        self.writer.append(ExperienceAppendRequest.model_validate(dict(request_id='current_append',
            trace_id='current_append_trace',draft=data)))
        return (self.req.run.agent_entity_id,conversation,self.req.request_id,event.event_id)

    def read(self, **changes):
        return self.ledger.read_recent_interactions(*self.boundary(**changes),identities=self.identities)

    def results(self, groups):
        return [x for group in groups for x in group if x.get('kind')=='action_result']

    def provider(self, bodies, *, recall_first=False):
        def capture(req):
            body=json.loads(req.content);bodies.append(body)
            text=('{"decision_kind":"MEMORY_RECALL","recall_plan":{"semantic_query":"拿铁"}}'
                  if recall_first and len(bodies)==1 else '{"decision_kind":"DIRECT_RESPONSE","utterance":"刚才那杯已经做好了。"}')
            return httpx.Response(200,json={'status':'completed','output':[{'type':'message','role':'assistant',
                'content':[{'type':'output_text','text':text}]}]})
        return DeepSeekOfficialResponsesProvider(base_url='https://api.deepseek.com',api_key='offline-placeholder',
            model='test',transport=httpx.MockTransport(capture))

    def test_all_registered_drinks_are_recognized_only_with_receipt_evidence(self):
        for drink in DRINK_TYPE_IDS:
            with self.subTest(drink=drink):
                # Each ledger is isolated so the four-result history limit cannot hide a case.
                self.setUp()
                event = self.event()
                event["structured_payload"]["drink_type"] = drink
                self.append(event)
                result = self.results(self.read())
                self.assertEqual(len(result), 1)
                self.assertEqual(result[0]["drink_type"], drink)
                self.assertTrue(result[0]["completion_confirmed"])
                self.setUp()
                event = self.event()
                event["structured_payload"]["drink_type"] = drink
                event["structured_payload"].pop("drink_receipt_id")
                self.append(event)
                self.assertEqual(self.results(self.read()), [])

    def test_unregistered_drink_with_receipt_is_rejected(self):
        event = self.event()
        event["structured_payload"]["drink_type"] = "unregistered_drink"
        self.append(event)
        self.assertEqual(self.results(self.read()), [])

    def test_success_has_receipt_and_is_not_speech(self):
        self.append(self.event());r=self.results(self.read());self.assertEqual(len(r),1)
        self.assertEqual((r[0]['status'],r[0]['receipt_id'],r[0]['completion_confirmed']),('SUCCEEDED','receipt_proposal_one',True))
        self.assertNotIn('text',r[0])

    def test_failed_cancelled_timed_out_are_not_success(self):
        for i,status in enumerate(['Failed','Cancelled','TimedOut']):self.append(self.event(status,proposal='p'+str(i)))
        r=self.results(self.read());self.assertEqual({x['status'] for x in r},{'FAILED','CANCELLED','TIMED_OUT'})
        self.assertTrue(all(not x['completion_confirmed'] for x in r))

    def test_distinct_orders_of_same_drink_survive(self):
        self.append(self.event());self.append(self.event(proposal='proposal_two',world_revision=2))
        self.assertEqual(len(self.results(self.read())),2)

    def test_duplicate_outcomes_are_one_goal(self):
        self.append(self.event());self.append(self.event());self.assertEqual(len(self.results(self.read())),1)

    def test_conflicting_results_do_not_choose_success(self):
        self.append(self.event());self.append(self.event('Failed'))
        r=self.results(self.read());self.assertEqual(r[0]['status'],'UNDETERMINED');self.assertFalse(r[0]['completion_confirmed'])

    def test_new_revision_cancellation_replaces_old_success(self):
        self.append(self.event());self.append(self.event('Cancelled',revision=1))
        r=self.results(self.read());self.assertEqual(r[0]['status'],'CANCELLED');self.assertTrue(r[0]['earlier_revision_present'])

    def test_invalid_status_pair_is_not_success(self):
        e=self.event();e['structured_payload']['status']='Running';self.append(e)
        self.assertEqual(self.results(self.read()),[])

    def test_missing_receipt_does_not_certify_drink(self):
        e=self.event();e['structured_payload'].pop('drink_receipt_id');self.append(e)
        self.assertEqual(self.results(self.read()),[])

    def test_wrong_receipt_destination_is_rejected(self):
        e=self.event();e['structured_payload']['destination_entity_id']='player_02';self.append(e)
        self.assertEqual(self.results(self.read()),[])

    def test_other_npc_is_not_visible(self):
        e=self.event(observer_id='npc_other',actor_entity_id='npc_other')
        e['persistent_participants'][0].update(entity_id='npc_other',persistent_id='other_npc')
        self.append(e);self.assertEqual(self.results(self.read()),[])

    def test_player_persistent_identity_reuse_is_not_visible(self):
        e=self.event();e['persistent_participants'][1]['persistent_id']='another_player';self.append(e)
        self.assertEqual(self.results(self.read()),[])

    def test_npc_persistent_identity_reuse_is_not_visible(self):
        e=self.event();e['persistent_participants'][0]['persistent_id']='another_npc';self.append(e)
        self.assertEqual(self.results(self.read()),[])

    def test_other_session_or_clock_is_not_visible(self):
        self.append(self.event(source_session_id='old_session'));self.append(self.event(clock_domain='backend_utc'))
        self.assertEqual(self.results(self.read()),[])

    def test_expired_future_time_and_revision_are_not_success(self):
        self.append(self.event(occurred_at=1.,observed_at=1.));self.append(self.event(occurred_at=1001.,observed_at=1001.))
        self.append(self.event(world_revision=11));self.assertEqual(self.results(self.read()),[])

    def test_old_occurrence_cannot_be_refreshed_by_recent_observation(self):
        self.append(self.event(occurred_at=1.,observed_at=990.))
        self.assertEqual(self.results(self.read()),[])

    def test_boundary_requires_accepted_system_input(self):
        self.append(self.event());self.assertEqual(self.results(self.read(provenance='REPORTED')),[])

    def test_untrusted_execution_rejected_by_original_schema(self):
        with self.assertRaises(ValueError):self.append(self.event(provenance='REPORTED'))

    def skill(self):
        e=self.event();e.update(event_type='skill_succeeded',source_type='skill_result')
        e['structured_payload']=dict(skill_id='prepare_drink:proposal_one',skill_type='PrepareDrink',status='Succeeded',failure_code='')
        return e

    def test_skill_and_goal_are_not_two_completions(self):
        self.append(self.skill());self.append(self.event());r=self.results(self.read())
        self.assertEqual(len(r),1);self.assertEqual(r[0]['result_level'],'goal')

    def test_skill_only_does_not_assert_whole_goal_running_or_done(self):
        self.append(self.skill());groups=self.read();r=self.results(groups)
        self.assertEqual(r[0]['goal_status'],'UNCONFIRMED');self.assertFalse(r[0]['completion_confirmed'])
        self.assertEqual(groups.action_scope['ongoing_execution_state'],'NOT_OBSERVED')

    def test_scan_result_and_text_bounds(self):
        for i in range(70):self.append(self.event(proposal='p'+str(i)))
        groups=self.read();self.assertTrue(groups.action_scope['read_limited'])
        self.assertLessEqual(len(self.results(groups)),MAX_RESULTS)
        self.assertLessEqual(sum(len(json.dumps(g,ensure_ascii=False,separators=(',',':'))) for g in groups),MAX_CHARS)
        self.assertEqual([x['proposal_id'] for x in self.results(groups)],['p66','p67','p68','p69'])

    def test_mixed_player_audience_is_not_projected(self):
        e=self.event();e['target_entity_ids'].append('player_02')
        e['persistent_participants'].append(dict(entity_id='player_02',identity_namespace='player',identity_type='Player',persistent_id='private_other_player'))
        self.append(e);self.assertEqual(self.results(self.read()),[])

    def test_same_proposal_different_trace_is_uncertain_not_two_successes(self):
        self.append(self.event());self.append(self.event(trace_id='other_trace'))
        r=self.results(self.read());self.assertEqual(len(r),1);self.assertEqual(r[0]['status'],'UNDETERMINED')

    def test_backwards_world_revision_is_uncertain(self):
        self.append(self.event(world_revision=2));self.append(self.event(world_revision=1))
        self.assertEqual(self.results(self.read())[0]['status'],'UNDETERMINED')

    def test_other_action_type_retains_target_and_failure(self):
        e=self.event('Failed');e['structured_payload'].update(goal_type='bring_object',failure_code='TARGET_UNAVAILABLE')
        e['target_entity_ids'].append('cup_01');self.append(e)
        r=self.results(self.read());self.assertEqual(r[0]['action_type'],'bring_object')
        self.assertIn('cup_01',r[0]['target_entity_ids']);self.assertEqual(r[0]['failure_code'],'TARGET_UNAVAILABLE')
        self.assertFalse(r[0]['completion_confirmed'])

    def test_result_committed_after_input_is_excluded(self):
        args=self.boundary();self.append(self.event())
        self.assertEqual(self.ledger.read_recent_interactions(*args,identities=self.identities),[])

    async def test_native_entry_sends_actual_http_body_and_reuses_after_recall(self):
        self.append(self.event());self.boundary();bodies=[];provider=self.provider(bodies,recall_first=True)
        entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),experience_ledger=self.ledger,
            context_length=32768,inference_options=InferenceOptions(max_output_tokens=512))
        await entry.run(self.req)
        self.assertEqual(len(bodies),2)
        for b in bodies:self.assertEqual(view(b)['results'][0]['receipt_id'],'receipt_proposal_one')

    async def test_budget_excludes_whole_groups_and_reports_omission(self):
        for i in range(4):self.append(self.event(proposal='p'+str(i)))
        groups=self.read();provider=self.provider([]);options=InferenceOptions(max_output_tokens=512)
        empty=RecentInteractionGroups(action_scope=groups.action_scope)
        base=CognitiveModelRequestFactory.create_event(self.req,recent_interactions=empty,
            inference_options=options,knowledge_context_limit=32768,budget_payload_builder=provider.budget_payload)
        estimator=ContextTokenEstimator();used=estimator.estimate_text(json.dumps(provider.budget_payload(base),ensure_ascii=False,separators=(',',':')))
        capacity=int((used+512+80)*32/29)+1
        model=CognitiveModelRequestFactory.create_event(self.req,recent_interactions=groups,
            inference_options=options,knowledge_context_limit=capacity,budget_payload_builder=provider.budget_payload)
        result=view(provider.budget_payload(model));self.assertTrue(result['budget_limited'])
        self.assertTrue(all(x in self.results(groups) for x in result['results']))
        self.assertLessEqual(estimator.estimate_text(json.dumps(provider.budget_payload(model),ensure_ascii=False,separators=(',',':')))+512+max(768,(capacity*3+31)//32),capacity)

    def test_tiny_capacity_fails_before_generation(self):
        self.append(self.event());groups=self.read()
        with self.assertRaises(ContextBudgetExceeded):
            CognitiveModelRequestFactory.create_event(self.req,recent_interactions=groups,knowledge_context_limit=2048)


if __name__=='__main__':unittest.main()
