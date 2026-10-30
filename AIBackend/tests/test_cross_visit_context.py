"""Read-time visit provenance and source-scoped Task schema; no paid calls."""
import json
import unittest
from unittest.mock import Mock

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest, CognitiveExecutionState
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory, _task_response_schema
from app.cognitive_step_models import TaskCognitiveStep, TaskTerminalStep
from app.relationship_context import RelationshipContextResolver
from app.relationship_summary_reader import (RelationshipSummaryReader, RelationshipSummary,
    RelationshipCurrentStateContext, RelationshipStateItemContext)
from tests import test_cross_conversation_recent_dialogue as speech
from tests import test_relationship_pair_state as pair
from tests import test_relationship_b2_context as social
from tests.test_execution_context import snapshot
from tests import test_task_execution as execution
from tests.test_cognitive_tasks import task_data


class VisitSpeechTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.f=speech.CrossConversationRecentDialogueTests();self.f.setUp()
        self.addCleanup(self.f.doCleanups)

    async def test_previous_visit_intent_preferences_and_agreement_remain_as_scoped_history(self):
        text='今天先随便聊，不急着点单。我喜欢少糖；下次见面做杯拿铁。'
        self.f.pair(1,text=text,reply='下次见面做拿铁。',session='old',clock=500)
        emote=self.f.append(self.f.request(50),'',session='old',clock=501,changes=dict(
            event_type='observation',source_type='observation',request_id=None,conversation_id=None,
            source_event_id=None,natural_language_payload=None,source_observation_ids=['fixture_emote'],
            structured_payload={'perception_revision':1,'emote_id':'wave'},provenance='WITNESSED'))
        self.f.pair(2,text='我现在想喝了。',reply='好。',session='session_new',clock=.1)
        req=CognitiveRequest.model_validate(self.f.current())
        provider=speech.ScriptedProvider()
        await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),experience_ledger=self.f.ledger,
                                    context_length=32768).run(req)
        turns=self.f.prompt_turns(provider.requests[0])
        self.assertEqual([x['session_scope'] for x in turns],
                         ['earlier_session','earlier_session','current_session','current_session'])
        self.assertEqual(turns[0]['text'],text)
        self.assertEqual(turns[0]['occurred_at'],500)
        self.assertGreater(turns[0]['occurred_at'],turns[2]['occurred_at'])
        self.assertEqual([x['seq'] for x in turns],sorted(x['seq'] for x in turns))
        self.assertNotIn('source_session_id',json.dumps(turns))
        self.assertIn('visit-local temporary intent is history',provider.requests[0].messages[-1].content)
        # The new read projection never rewrites history or clocks.
        reader=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),experience_ledger=self.f.ledger)._read_recent_dialogue(req)
        self.assertEqual(reader.sources[turns[0]['seq']]['source_session_id'],'old')
        self.assertEqual(reader.sources[turns[0]['seq']]['clock_domain'],'unity_realtime')
        # The shared interaction view must retain the same scope when it replaces
        # the speech-only section; no duplicate speech or implicit clock comparison.
        combined=CognitiveModelRequestFactory.create_event(req,recent_dialogue=reader,
            recent_interactions=[[dict(kind='player_emote',seq=emote.experience_seq,emote_id='wave')]],
            knowledge_context_limit=32768)
        user=combined.messages[-1].content
        self.assertIn('PRIOR INTERACTION TIMELINE JSON:',user)
        self.assertIn('visit-local temporary intent is history',user)
        entries=json.JSONDecoder().raw_decode(user.split('PRIOR INTERACTION TIMELINE JSON: ',1)[1])[0]
        self.assertEqual(entries[0]['session_scope'],'earlier_session')


class VisitRelationshipTests(unittest.TestCase):
    def setUp(self):
        self.f=pair.RelationshipPairStateTests();self.f.setUp();self.addCleanup(self.f.doCleanups)

    def req(self,session):
        req=social.request()
        state=snapshot('UNKNOWN')
        state.update(source_session_id=session,actor_entity_id=req.run.agent_entity_id)
        return req.model_copy(update={'context':req.context.model_copy(update={'runtime_state':CognitiveExecutionState.model_validate(state)})})

    def test_verified_old_pair_item_keeps_text_and_boundary_with_source_scope(self):
        self.f.commit(self.f.candidate('今天先聊天。'),self.f.response('Today only casual chat.',quote='今天先聊天。'))
        reader=RelationshipSummaryReader(self.f.repo)
        resolver=RelationshipContextResolver(reader)
        old=resolver.resolve(self.req('session_later'))
        same=resolver.resolve(self.req('session_1'))
        self.assertEqual(old.current_state.items[0].session_scope,'earlier_session')
        self.assertEqual(same.current_state.items[0].session_scope,'current_session')
        self.assertEqual(old.current_state.items[0].text,'Today only casual chat.')
        self.assertEqual(old.current_state.items[0].kind,'BOUNDARY')
        wire=old.model_dump_json(exclude_none=True)
        self.assertNotIn('source_sessions',wire)
        self.assertNotIn('session_1',wire)
        self.assertEqual(reader.get(pair.NPC,pair.PLAYER).current_state.items[0].session_scope,None)

    def test_missing_and_mixed_source_provenance_never_becomes_current_intent(self):
        for sources,expected in (((),'unknown'),(('session_old',),'earlier_session'),
                                 (('session_new',),'current_session'),(('session_old','session_new'),'mixed_sessions')):
            state=RelationshipCurrentStateContext(items=(RelationshipStateItemContext(kind='CONFLICT',text='新理解',alternative='旧理解',source_sessions=sources),),has_direct_interaction=True,coverage='SINCE_B1',late_episode_skipped=False)
            summary=RelationshipSummary(interpretations=(),current_state=state,source_count=1,selected_count=1,
                truncated=False,char_budget_used=1,token_budget_used=1)
            context=RelationshipContextResolver(Mock(get_for_cognition=Mock(return_value=summary))).resolve(self.req('session_new'))
            self.assertEqual(context.current_state.items[0].session_scope,expected)
            self.assertEqual(context.current_state.items[0].alternative,'旧理解')


class TaskSourceSchemaTests(unittest.IsolatedAsyncioTestCase):
    setUp=execution.TaskExecutionTests.setUp
    seed=execution.TaskExecutionTests.seed
    visit=execution.TaskExecutionTests.visit
    sync=execution.TaskExecutionTests.sync
    automatic=execution.TaskExecutionTests.automatic
    run_scripts=execution.TaskExecutionTests.run_scripts

    def test_task_execution_focus_is_advertised_only_for_automatic_source_in_both_steps(self):
        for model in (TaskCognitiveStep,TaskTerminalStep):
            for language in ('zh-CN','en-US'):
                manual=_task_response_schema(model,language)
                automatic=_task_response_schema(model,language,allow_task_execution=True)
                self.assertNotIn('task_focus',json.dumps(manual))
                self.assertIn('task_focus',json.dumps(automatic))
                self.assertIn('ACTION_PROPOSAL',json.dumps(manual))

    async def test_player_action_factory_does_not_offer_autonomous_focus_and_guard_still_rejects_it(self):
        self.seed()
        def illegal(model):
            self.assertNotIn('task_focus',json.dumps(model.response_schema))
            row=task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL',utterance='开始制作。',
                action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')),
                task_focus=dict(task_ref=row['task_ref'],expected_version=row['version']))
        with self.assertRaisesRegex(ValueError,'TASK_EXECUTION_SOURCE_REQUIRED'):
            await self.run_scripts(self.visit(),[illegal])
        self.assertIsNone(self.service.query_tasks(self.tasks.prepare(self.visit()).scope).tasks[0].execution)

    async def test_automatic_focus_still_creates_real_service_reservation(self):
        self.seed()
        def choose(model):
            self.assertIn('task_focus',json.dumps(model.response_schema))
            row=task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL',utterance='我来做约好的拿铁。',
                action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')),
                task_focus=dict(task_ref=row['task_ref'],expected_version=row['version']))
        result,_=await self.run_scripts(self.automatic(),[choose])
        self.assertIsNotNone(result.task_execution)
        self.assertEqual(result.task_execution.goal.drink_type,'latte')
