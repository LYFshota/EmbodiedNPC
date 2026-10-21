"""Stage 3A architecture/contracts. Runner must isolate paths before import."""
import asyncio
import json
import sqlite3
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from pydantic import ValidationError
from app.contracts import CognitiveRequest, TaskRuntimeSyncRequest
from app.cognitive_tasks import CognitiveTasks
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.sqlite_database import SQLiteDatabase
from app.task_repository import TaskRepository
from app.task_service import TaskService
from app.task_models import TaskCreate, TaskError
from tests.test_cognitive_tasks import request, Scripted, task_data


class TaskConsiderationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.path=Path(tempfile.mkdtemp(prefix='stage3a_'))/'tasks.sqlite3'
        self.db=SQLiteDatabase(self.path)
        self.now=1000.0
        self.service=TaskService(TaskRepository(self.db.connect),clock=lambda:self.now)
        self.tasks=CognitiveTasks(self.service,expected_player_uid='synthetic-task-player')
        self.connection=object()
        self.a=request(number='a')
        self.scope=self.tasks.prepare(self.a).scope

    def seed(self,scope=None,*,trigger='next_visit',drink='latte'):
        return self.service.create_task(scope or self.scope,'seed_'+drink,TaskCreate.model_validate(dict(
            source_kind='player_request',source=dict(event_id='seed_event'),goal=dict(kind='serve_drink',drink_type=drink),trigger_kind=trigger))).task

    def visit(self,name='session_b',*,owner='cafe.merchant.001',player='synthetic-task-player'):
        req=request(number=name,owner=owner,player=player)
        return req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'client_session_id':name,'visit_id':name})})

    def sync(self,req=None,*,idle=True):
        req=req or self.visit()
        wire=TaskRuntimeSyncRequest.model_validate(dict(message_type='task_runtime_sync',schema_version='1.1',
            request_id='sync_'+req.request_id,agent_entity_id=req.run.agent_entity_id,
            recall_identity=req.recall_identity.model_dump(mode='json'),task_runtime=req.task_runtime.model_dump(mode='json'),is_idle=idle))
        return self.tasks.synchronize(self.connection,wire)

    def automatic(self,req=None,*,number='auto',snapshot=None,visible=True):
        req=req or self.visit()
        snapshot=snapshot or self.sync(req)
        value=req.model_dump(mode='json')
        value['recall_identity']={**req.recall_identity.model_dump(mode='json'),'requester_entity_id':None}
        value['task_runtime']=snapshot['task_runtime']
        value['task_consideration']=dict(opportunity_token=snapshot['opportunity_token'],event_id='event_'+number,execution_protocol='1.0')
        value['context']['current_event'].update(event_id='event_'+number,event_type='npc.task_consideration',
            source_entity_id=req.run.agent_entity_id,payload=[dict(key='player_entity_id',value='player_01')])
        value['trigger']['source_event_id']='event_'+number
        value['context']['trigger']['source_event_id']='event_'+number
        value['context']['perception']['entities']=[dict(entity_id='player_01',kind='player',display_name='合成玩家',
            aliases=[],semantic_tags=[],visibility='current_visible' if visible else 'remembered',capabilities=[])]
        return CognitiveRequest.model_validate(value)

    async def run_scripts(self,req,scripts):
        provider=Scripted(scripts)
        entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),tasks=self.tasks,context_length=16384)
        result=await entry.run(req)
        return result,provider

    async def test_same_visit_no_chance_and_no_inference(self):
        self.seed(); snapshot=self.sync(self.a)
        self.assertEqual(snapshot['task_ids'],[])
        self.assertIsNone(snapshot['opportunity_token'])
        self.assertEqual(self.service.get_task(self.scope,self.service.query_tasks(self.scope).tasks[0].task_id).version,1)

    async def test_next_visit_offers_only_and_no_action_remains_waiting(self):
        task=self.seed(); req=self.automatic()
        result,provider=await self.run_scripts(req,[dict(decision_kind='NO_ACTION')])
        stored=self.service.get_task(self.scope,task.task_id)
        self.assertEqual(result.decision.decision_kind,'NO_ACTION')
        self.assertIsNone(result.decision.utterance)
        self.assertIsNone(result.validated_goal)
        self.assertEqual((stored.status,stored.version,stored.consideration.automatic_count),('waiting',2,1))
        self.assertIsNone(stored.execution)
        self.assertEqual(len(provider.requests),1)
        with self.assertRaises(TaskError): await self.run_scripts(req,[])

    async def test_no_tasks_sync_does_not_create_or_infer(self):
        self.assertEqual(self.sync()['task_ids'],[])
        self.assertEqual(self.service.query_tasks(self.scope).tasks,())

    async def test_no_live_vision_and_forged_token_rejected_before_model(self):
        self.seed()
        req=self.automatic(visible=False)
        with self.assertRaises(TaskError): await self.run_scripts(req,[])
        req=self.automatic()
        req=req.model_copy(update={'task_consideration':req.task_consideration.model_copy(update={'opportunity_token':'invented'})})
        with self.assertRaises(TaskError): await self.run_scripts(req,[])

    async def test_context_limited_to_three_eligible_and_scoped(self):
        for drink in ('latte','cappuccino','americano'): self.seed(drink=drink)
        other=self.scope.model_copy(update={'owner':self.scope.owner.model_copy(update={'persistent_id':'other-npc'})})
        self.service.create_task(other,'other_owner',TaskCreate.model_validate(dict(source_kind='player_request',source=dict(event_id='s'),goal=dict(kind='serve_drink',drink_type='latte'),trigger_kind='next_visit')))
        req=self.automatic()
        def inspect(model):
            data=task_data(model)
            self.assertEqual(len(data['tasks']),3)
            self.assertTrue(all(t['trigger_ready'] and t['world_execution_available'] for t in data['tasks']))
            self.assertNotIn('persistent_id',json.dumps(data))
            return dict(decision_kind='NO_ACTION')
        await self.run_scripts(req,[inspect])

    async def test_owner_player_and_four_owner_scope_isolation(self):
        self.seed()
        for owner in ('cafe.regular.001','cafe.companion.001','cafe.quiet.001'):
            self.assertEqual(self.sync(self.visit(owner=owner))['task_ids'],[])
        with self.assertRaises(TaskError): self.sync(self.visit(player='other-player'))

    async def test_when_idle_gate_and_current_visit_dialogue_merge(self):
        task=self.seed(trigger='when_idle')
        self.assertEqual(self.sync(self.a,idle=False)['task_ids'],[])
        self.assertEqual(len(self.sync(self.a,idle=True)['task_ids']),1)
        result,provider=await self.run_scripts(self.a,[dict(decision_kind='DIRECT_RESPONSE',utterance='你好。',future_commitment=False)])
        self.assertEqual(len(provider.requests),1)
        self.assertEqual(len(task_data(provider.requests[0])['tasks']),1)
        self.assertIsNone(self.service.get_task(self.scope,task.task_id).consideration)

    async def test_auto_model_can_defer_using_service_and_original_command_recovery(self):
        task=self.seed(); req=self.automatic()
        def defer(model):
            item=task_data(model)['tasks'][0]
            return dict(decision_kind='DIRECT_RESPONSE',utterance='我先改为空闲时再考虑。',future_commitment=True,
                task_operation=dict(op='update_task',task_ref=item['task_ref'],expected_version=item['version'],change='defer',reason='自主延期',trigger_kind='when_idle'))
        result,_=await self.run_scripts(req,[defer])
        stored=self.service.get_task(self.scope,task.task_id)
        self.assertEqual((stored.trigger_kind,stored.status,stored.version),('when_idle','waiting',3))
        self.assertTrue(result.task_receipt.committed)
        context=self.tasks.prepare(req,recovery=True)
        recovered=self.tasks.recover(req,context)
        self.assertTrue(recovered[1].recovered)

    async def test_task_action_without_focus_rejected_without_reservation(self):
        task=self.seed(); req=self.automatic()
        with self.assertRaisesRegex(TaskError,'TASK_EXECUTION_SOURCE_REQUIRED'):
            await self.run_scripts(req,[dict(decision_kind='ACTION_PROPOSAL',action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')))])
        stored=self.service.get_task(self.scope,task.task_id)
        self.assertEqual(stored.status,'waiting'); self.assertIsNone(stored.execution)

    async def test_new_commission_not_granted_by_system_event(self):
        self.seed(); req=self.automatic()
        result,_=await self.run_scripts(req,[dict(decision_kind='DIRECT_RESPONSE',utterance='我来记一件新的。',future_commitment=True,
            task_operation=dict(op='create_task',source='requester',goal=dict(kind='serve_drink',drink_type='americano'),trigger_kind='next_visit'))])
        self.assertFalse(result.task_receipt.committed)
        self.assertEqual(len(self.service.query_tasks(self.scope).tasks),1)

    async def test_cooldown_cap_dedup_and_reopen_persist(self):
        task=self.seed(); scope=self.scope.model_copy(update={'visit_id':'session_b'})
        for number in range(3):
            self.assertEqual(len(self.service.query_considerable(scope)),1)
            self.assertEqual(len(self.service.record_consideration(scope,(task.task_id,),'wake_'+str(number))),1)
            self.assertEqual(self.service.record_consideration(scope,(task.task_id,),'wake_'+str(number)),())
            self.assertEqual(self.service.query_considerable(scope),())
            self.now+=31.0
        service=TaskService(TaskRepository(SQLiteDatabase(self.path).connect),clock=lambda:self.now)
        self.assertEqual(service.query_considerable(scope),())
        self.assertEqual(service.get_task(scope,task.task_id).consideration.automatic_count,3)
        self.assertEqual(len(service.query_considerable(scope.model_copy(update={'visit_id':'session_c'}))),1)

    async def test_competing_considerations_only_one_same_event(self):
        task=self.seed(); scope=self.scope.model_copy(update={'visit_id':'session_b'})
        with ThreadPoolExecutor(2) as pool:
            outcomes=list(pool.map(lambda _:self.service.record_consideration(scope,(task.task_id,),'same_wake'),range(2)))
        self.assertEqual(sum(len(result) for result in outcomes),1)

    async def test_reconnect_keeps_visit_and_old_scene_token_cannot_admit(self):
        self.seed(); req=self.automatic(); self.tasks.disconnect(self.connection)
        with self.assertRaises(TaskError): await self.run_scripts(req,[])
        self.connection=object(); snapshot=self.sync(self.visit())
        self.assertEqual(snapshot['task_runtime']['visit_id'],'session_b')
        replacement=self.visit().model_copy(update={'task_runtime':self.visit().task_runtime.model_copy(update={'scene_instance_id':'scene_new'})})
        self.sync(replacement)
        with self.assertRaises(TaskError): await self.run_scripts(req,[])

    async def test_task_read_and_recall_share_two_step_contract_language_frozen(self):
        self.seed(); req=self.automatic(self.visit().model_copy(update={'reply_language':'en-US'}))
        result,provider=await self.run_scripts(req,[dict(decision_kind='TASK_READ',task_query=dict(op='query_tasks')),
            dict(decision_kind='DIRECT_RESPONSE',utterance='There is one saved request.',future_commitment=False)])
        self.assertEqual(len(provider.requests),2)
        for model in provider.requests: self.assertIn('reply_language = en-US',model.messages[-1].content)
        self.assertNotIn('TaskReadStep',provider.requests[1].response_schema.get('$defs',{}))

    async def test_old_silent_source_cannot_attach_new_permission(self):
        self.seed(); req=self.automatic()
        bad=req.model_dump(mode='json'); bad['task_runtime']=req.task_runtime.model_dump(mode='json')
        bad['context']['current_event']['event_type']='entity.became_visible'
        bad['task_consideration']=req.task_consideration.model_dump(mode='json')
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(bad)

    async def test_cancelled_and_executing_tasks_not_offered(self):
        task=self.seed()
        self.service.cancel_task(self.scope,'cancel_seed',task.task_id,expected_version=1,reason='玩家取消')
        self.assertEqual(self.sync()['task_ids'],[])

    async def test_reserved_execution_is_never_offered_again(self):
        from app.task_models import ExecutionRequest
        task=self.seed(); scope=self.scope.model_copy(update={'visit_id':'session_b'})
        self.service.reserve_execution(scope,'reserved_seed',task.task_id,ExecutionRequest(expected_version=1,
            scene_instance_id='scene_b',proposal_id='proposal_b',trace_id='trace_b',actor_entity_id='npc_cafe_01',
            world_revision=1,player_entity_id='player_01'))
        self.assertEqual(self.sync()['task_ids'],[])

    async def test_multi_task_counter_write_rolls_back_atomically(self):
        first=self.seed(); second=self.seed(drink='americano')
        scope=self.scope.model_copy(update={'visit_id':'session_b'})
        with self.db.connect() as db:
            db.execute("CREATE TRIGGER reject_consider BEFORE UPDATE ON npc_tasks WHEN OLD.task_id='"+second.task_id+"' BEGIN SELECT RAISE(ABORT,'SYNTHETIC_FAILURE'); END")
        with self.assertRaises(sqlite3.Error): self.service.record_consideration(scope,(first.task_id,second.task_id),'rollback_event')
        for task in (first,second):
            stored=self.service.get_task(scope,task.task_id)
            self.assertEqual(stored.version,1); self.assertIsNone(stored.consideration)

    async def test_player_foreground_preempts_automatic_model_without_retry(self):
        from app.local_model_scheduler import PreemptedByForeground
        from app.providers import ModelResult
        self.seed(); req=self.automatic(); started=asyncio.Event()
        class Provider:
            name='scripted';model='offline'
            def __init__(self): self.calls=0
            async def generate(self,request):
                self.calls+=1
                if self.calls==1:
                    started.set(); await asyncio.Event().wait()
                return ModelResult(text='{"decision_kind":"NO_ACTION"}')
        provider=Provider(); executor=PrioritizedModelExecutor()
        entry=CognitiveBackendEntry(provider,executor,tasks=self.tasks,context_length=16384)
        background=asyncio.create_task(entry.run(req)); await asyncio.wait_for(started.wait(),2)
        foreground=await asyncio.wait_for(entry.run(self.a),2)
        with self.assertRaises(PreemptedByForeground): await background
        self.assertEqual(foreground.decision.decision_kind,'NO_ACTION'); self.assertEqual(provider.calls,2)

    async def test_automatic_task_read_cannot_read_twice(self):
        self.seed(); req=self.automatic()
        with self.assertRaises(ValidationError):
            await self.run_scripts(req,[dict(decision_kind='TASK_READ',task_query={}),dict(decision_kind='TASK_READ',task_query={})])

    async def test_real_handler_requires_task_focus_before_reserving_execution(self):
        # Missing task_focus is rejected before reservation. The current handler has
        # an execution service; TASK_EXECUTION_NOT_CONNECTED is a retired expectation.
        from unittest.mock import patch
        from tests.app_test_isolation import get_test_app
        m3_main = get_test_app()
        from tests.test_cognitive_websocket import SocketHarness
        self.seed(); socket=SocketHarness(); self.connection=socket.socket
        req=self.automatic()
        provider=Scripted([dict(decision_kind='ACTION_PROPOSAL',action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')))])
        entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),tasks=self.tasks,context_length=16384)
        wire={**req.model_dump(mode='json'), 'recall_identity':req.recall_identity.model_dump(mode='json'),
            'task_runtime':req.task_runtime.model_dump(mode='json'),'task_consideration':req.task_consideration.model_dump(mode='json')}
        with patch.object(m3_main,'task_cognition',self.tasks),patch.object(m3_main,'cognitive_entry',entry), \
                patch.object(m3_main,'demo_player_uid',self.tasks.expected_player_uid):
            task=asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
            try:
                socket.message(wire); result=await socket.result()
                self.assertEqual(result['reason'],'TASK_EXECUTION_SOURCE_REQUIRED')
                self.assertEqual(result['request_id'],req.request_id)
                self.assertNotIn('task_execution',result)
                stored=self.service.query_tasks(self.tasks.prepare(req,recovery=True).scope).tasks
                self.assertEqual(len(stored),1)
                self.assertTrue(all(task.execution is None for task in stored))
                self.assertEqual(result['run_id'],req.run.run_id)
                self.assertEqual(len(provider.requests),1)
            finally:
                socket.disconnect(); await asyncio.wait_for(task,3)
