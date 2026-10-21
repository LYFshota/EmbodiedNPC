"""Stage 3B service/entry contracts. Receipts here are synthetic, not world evidence."""
import asyncio
import json
import sqlite3
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
from app.contracts import CognitiveRequest
from app.cognitive_tasks import CognitiveTasks
from app.task_execution import TaskExecutionControl
from app.task_models import TaskCreate,TaskError
from app.sqlite_database import SQLiteDatabase
from app.task_repository import TaskRepository
from app.task_service import TaskService
from tests.test_task_consideration import TaskConsiderationTests
from tests.test_cognitive_tasks import task_data
from tests.test_task_service import synthetic_result


def wire(req):
    return {**req.model_dump(mode='json'), 'recall_identity':req.recall_identity.model_dump(mode='json'),
        'task_runtime':req.task_runtime.model_dump(mode='json'),
        'task_consideration':req.task_consideration.model_dump(mode='json') if req.task_consideration else None}


class TaskExecutionTests(unittest.IsolatedAsyncioTestCase):
    setUp=TaskConsiderationTests.setUp
    seed=TaskConsiderationTests.seed
    visit=TaskConsiderationTests.visit
    sync=TaskConsiderationTests.sync
    automatic=TaskConsiderationTests.automatic
    run_scripts=TaskConsiderationTests.run_scripts

    async def reserve(self,*,clean=False):
        if clean:
            req=self.visit()
            value=wire(req)
            value['context']['capabilities']['available_actions']=[dict(action_id='clean_counter',description='清洁分配的吧台',bindings=[dict(name='target',kind='entity_ref',required=True,enum_values=[])])]
            value['context']['counter_work']=dict(target_entity_id='counter_01',needs_cleaning=True,condition_revision=1)
            req=CognitiveRequest.model_validate(value)
            scope=self.tasks.prepare(req).scope
            self.service.create_task(scope.model_copy(update={'visit_id':'old_visit'}),'clean_seed',TaskCreate.model_validate(dict(
                source_kind='self_initiated',source=dict(event_id='seed'),goal=dict(kind='clean_assigned_counter',counter_key='assigned_counter'),trigger_kind='next_visit')))
            req=self.automatic(req)
        else:
            self.seed();req=self.automatic()
        def choose(model):
            task=task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL',task_focus=dict(task_ref=task['task_ref'],expected_version=task['version']),
                action_proposal=dict(action_id='clean_counter' if clean else 'prepare_drink',
                    bindings=dict(target='counter_01') if clean else dict(drink_type='latte')))
        scripts=([dict(decision_kind='TASK_READ',task_query=dict(op='query_tasks')),choose] if clean else [choose])
        result,provider=await self.run_scripts(req,scripts)
        return req,result.task_execution,provider

    def control(self,req,ticket,op,result=None):
        return self.tasks.executions.control(TaskExecutionControl.model_validate(dict(message_type='task_control',schema_version='1.1',
            operation=op,original_request=wire(req),ticket=ticket.model_dump(mode='json'),
            result=result.model_dump(mode='json') if result else None)))

    def task(self,req,ticket):
        return self.service.get_task(self.tasks.prepare(req,recovery=True).scope,ticket.task_id)

    async def test_two_goals_reserve_begin_receipt_settle_once_and_reopen(self):
        for clean in (False,True):
            with self.subTest(clean=clean):
                self.setUp()
                req,ticket,provider=await self.reserve(clean=clean)
                self.assertEqual(len(provider.requests),2 if clean else 1)
                self.assertEqual(self.task(req,ticket).execution.phase,'reserved')
                self.assertTrue(self.control(req,ticket,'begin_execution')['start_permitted'])
                with self.assertRaises(TaskError): self.control(req,ticket,'begin_execution')
                task=self.task(req,ticket)
                # Existing fixture is deliberately synthetic; use this attempt's revision.
                result=synthetic_result(task,world_revision=11)
                self.assertEqual(self.control(req,ticket,'settle_execution',result)['task_status'],'completed')
                version=self.task(req,ticket).version
                self.control(req,ticket,'settle_execution',result)
                self.assertEqual(self.task(req,ticket).version,version)
                self.service=TaskService(TaskRepository(SQLiteDatabase(self.path).connect),clock=lambda:self.now)
                self.tasks=CognitiveTasks(self.service,expected_player_uid='synthetic-task-player')
                self.assertEqual(self.task(req,ticket).status,'completed')
                self.assertIsNotNone(self.tasks.recover(req,self.tasks.prepare(req,recovery=True)))

    async def test_begin_race_has_one_permit_and_unknown_never_reclaims(self):
        req,ticket,_=await self.reserve()
        def begin():
            try:return self.control(req,ticket,'begin_execution')['start_permitted']
            except TaskError:return False
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sum(pool.map(lambda _:begin(),range(2))),1)
        self.control(req,ticket,'mark_execution_unknown')
        self.control(req,ticket,'mark_execution_unknown')
        self.assertEqual((self.task(req,ticket).status,self.task(req,ticket).execution.phase),('blocked','uncertain'))
        with self.assertRaises(TaskError): self.control(req,ticket,'begin_execution')
        self.assertEqual(self.tasks.synchronize(self.connection,__import__('app.contracts',fromlist=['TaskRuntimeSyncRequest']).TaskRuntimeSyncRequest.model_validate(dict(
            message_type='task_runtime_sync',schema_version='1.1',request_id='reconnect',agent_entity_id=req.run.agent_entity_id,
            recall_identity=self.visit().recall_identity.model_dump(mode='json'),task_runtime=req.task_runtime.model_dump(mode='json'),is_idle=True)))['task_ids'],[])
        known=synthetic_result(self.task(req,ticket))
        self.assertEqual(self.control(req,ticket,'settle_execution',known)['task_status'],'completed')

    async def test_ticket_identity_scene_visit_and_parameters_cannot_be_swapped(self):
        req,ticket,_=await self.reserve()
        for field,value in [('task_id','other_task'),('attempt_id','old_attempt'),('scene_instance_id','other_scene'),('visit_id','other_visit'),
                            ('owner',ticket.owner.model_copy(update={'persistent_id':'other_npc'})),('player',ticket.player.model_copy(update={'persistent_id':'other_player'}))]:
            with self.subTest(field=field),self.assertRaises(TaskError):
                self.control(req,ticket.model_copy(update={field:value}),'begin_execution')
        self.assertEqual(self.task(req,ticket).execution.phase,'reserved')

    async def test_rejected_not_started_and_cancel_effect_races(self):
        for after_effect in (False,True):
            self.setUp();req,ticket,_=await self.reserve();scope=self.tasks.prepare(req,recovery=True).scope
            self.control(req,ticket,'begin_execution')
            task=self.task(req,ticket)
            self.service.cancel_task(scope,'cancel',task.task_id,expected_version=task.version,reason='合成取消')
            task=self.task(req,ticket)
            result=synthetic_result(task,status='succeeded' if after_effect else 'cancelled')
            self.assertEqual(self.control(req,ticket,'settle_execution',result)['task_status'],'completed' if after_effect else 'cancelled')
        self.setUp();req,ticket,_=await self.reserve()
        result=synthetic_result(self.task(req,ticket),status='failed',failure_code='TASK_NOT_STARTED')
        self.assertEqual(self.control(req,ticket,'not_started',result)['task_status'],'blocked')
        with self.assertRaises(TaskError):self.control(req,ticket,'begin_execution')
        self.setUp();req,ticket,_=await self.reserve();scope=self.tasks.prepare(req,recovery=True).scope
        self.service.cancel_task(scope,'cancel_before_begin',ticket.task_id,expected_version=ticket.task_version,reason='开始前取消')
        with self.assertRaises(TaskError):self.control(req,ticket,'begin_execution')
        result=synthetic_result(self.task(req,ticket),status='failed',failure_code='TASK_NOT_STARTED')
        self.assertEqual(self.control(req,ticket,'not_started',result)['task_status'],'cancelled')

    async def test_forged_or_old_receipts_and_failure_never_complete(self):
        req,ticket,_=await self.reserve();self.control(req,ticket,'begin_execution')
        task=self.task(req,ticket);result=synthetic_result(task)
        for field,value in [('attempt_id','old_attempt'),('scene_instance_id','old_scene'),('proposal_id','other_proposal'),('actor_entity_id','other_npc')]:
            with self.subTest(field=field),self.assertRaises(TaskError):self.control(req,ticket,'settle_execution',result.model_copy(update={field:value}))
        receipt=result.receipt.model_copy(update={'drink_type':'americano'})
        with self.assertRaises(TaskError):self.control(req,ticket,'settle_execution',result.model_copy(update={'receipt':receipt}))
        self.control(req,ticket,'settle_execution',synthetic_result(task,status='failed'))
        self.assertEqual(self.task(req,ticket).status,'blocked')

    async def test_database_failure_has_no_permit_and_settle_retry_is_safe(self):
        req,ticket,_=await self.reserve()
        with patch.object(self.service._repo,'_save',side_effect=sqlite3.OperationalError('isolated fault')):
            with self.assertRaises(sqlite3.Error):self.control(req,ticket,'begin_execution')
        self.assertEqual(self.task(req,ticket).execution.phase,'reserved')
        self.control(req,ticket,'begin_execution');task=self.task(req,ticket);result=synthetic_result(task)
        with patch.object(self.service._repo,'_save',side_effect=sqlite3.OperationalError('isolated fault')):
            with self.assertRaises(sqlite3.Error):self.control(req,ticket,'settle_execution',result)
        self.assertEqual(self.task(req,ticket).status,'executing')
        from tests.app_test_isolation import get_test_app
        backend = get_test_app()
        from tests.test_cognitive_websocket import SocketHarness
        socket=SocketHarness()
        socket.socket.scope.update(client=('127.0.0.1',19001),server=('127.0.0.1',19002))
        # The configured local Demo identity must match this private TaskService fixture.
        with patch.object(backend,'task_cognition',self.tasks), \
                patch.object(backend,'demo_player_uid',self.tasks.expected_player_uid):
            running=asyncio.create_task(backend.websocket_endpoint(socket.socket))
            try:
                with patch.object(self.service._repo,'_save',side_effect=sqlite3.OperationalError('isolated wire fault')):
                    socket.message(dict(message_type='task_control',schema_version='1.1',operation='settle_execution',
                        original_request=wire(req),ticket=ticket.model_dump(mode='json'),result=result.model_dump(mode='json')))
                    response=await asyncio.wait_for(socket.outgoing.get(),3)
                self.assertFalse(response['accepted']);self.assertFalse(response['start_permitted'])
                self.assertEqual(response['error_code'],'TASK_EXECUTION_STORE_UNAVAILABLE')
                (self.path.parent/'wire-store-failure.json').write_text(json.dumps(response),encoding='utf-8')
            finally:
                socket.disconnect();await asyncio.wait_for(running,3)
        self.assertEqual(self.control(req,ticket,'settle_execution',result)['task_status'],'completed')

    async def test_model_goal_version_mismatch_no_claim_and_no_action_no_effect(self):
        for mismatch in ('version','goal','legacy_client','no_action'):
            self.setUp();task=self.seed();req=self.automatic()
            if mismatch=='legacy_client':
                req=req.model_copy(update={'task_consideration':req.task_consideration.model_copy(update={'execution_protocol':None})})
            def choose(model):
                item=task_data(model)['tasks'][0]
                if mismatch=='no_action':return dict(decision_kind='NO_ACTION')
                return dict(decision_kind='ACTION_PROPOSAL',task_focus=dict(task_ref=item['task_ref'],expected_version=item['version']+(mismatch=='version')),
                    action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='americano' if mismatch=='goal' else 'latte')))
            if mismatch=='no_action':
                result,_=await self.run_scripts(req,[choose]);self.assertIsNone(result.task_execution)
            else:
                with self.assertRaises(TaskError):await self.run_scripts(req,[choose])
            self.assertIsNone(self.service.get_task(self.scope,task.task_id).execution)

    async def test_ordinary_player_immediate_action_does_not_claim_persistent_task(self):
        task=self.seed()
        result,provider=await self.run_scripts(self.a,[dict(decision_kind='ACTION_PROPOSAL',
            action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')))])
        self.assertEqual(result.validated_goal.type,'prepare_drink')
        self.assertIsNone(result.task_execution)
        self.assertIsNone(self.service.get_task(self.scope,task.task_id).execution)
        self.assertEqual(len(provider.requests),1)
