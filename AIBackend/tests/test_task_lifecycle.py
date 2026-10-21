"""Stage 3C targeted contracts. Runner isolates every path before importing this module."""
import asyncio
import sqlite3
import unittest
from unittest.mock import patch
from app.contracts import CognitiveRequest, CognitiveResponse, TaskRuntimeSyncRequest
from app.cognitive_tasks import CognitiveTasks
from app.task_models import TaskCreate, TaskError, ExecutionRequest
from app.task_lifecycle import TaskCancelControl, cancel
from tests.test_task_execution import TaskExecutionTests, wire
from tests.test_clean_counter import system_request
from tests.test_cognitive_tasks import task_data
from tests.test_task_service import synthetic_result


class TaskLifecycleTests(unittest.IsolatedAsyncioTestCase):
    setUp=TaskExecutionTests.setUp
    seed=TaskExecutionTests.seed
    visit=TaskExecutionTests.visit
    sync=TaskExecutionTests.sync
    automatic=TaskExecutionTests.automatic
    run_scripts=TaskExecutionTests.run_scripts
    reserve=TaskExecutionTests.reserve
    control=TaskExecutionTests.control
    task=TaskExecutionTests.task

    def modern(self, req):
        return req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'lifecycle_protocol':'1.0','player_entity_id':'player_01'})})

    def counter(self):
        value=wire(self.modern(self.a))
        base=system_request()
        value.update(request_id=base.request_id,trace_id=base.trace_id,run=base.run.model_dump(),
                     trigger=base.trigger.model_dump(),context=base.context.model_dump())
        value['recall_identity']['requester_entity_id']=None
        return CognitiveRequest.model_validate(value)

    def sync_wire(self, req, *, offer=True, observed=False, condition=None, visible=True):
        identity=req.recall_identity.model_dump(mode='json')
        identity['requester_entity_id']='player_01'
        return TaskRuntimeSyncRequest.model_validate(dict(message_type='task_runtime_sync',schema_version='1.1',
            request_id='sync_'+req.request_id,agent_entity_id=req.run.agent_entity_id,recall_identity=identity,
            task_runtime={**req.task_runtime.model_dump(mode='json'),'command_id':'sync_'+req.task_runtime.command_id},is_idle=True,offer_tasks=offer,
            counter_observed=observed,counter_condition=condition,player_visible=visible))

    def cancellation(self, req, task):
        return TaskCancelControl(message_type='task_control',schema_version='1.1',operation='cancel_task',
            runtime_request=self.sync_wire(self.modern(req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'command_id':'cancel_'+task.task_id})})),offer=False),task_id=task.task_id,
            expected_version=task.version,attempt_id=task.execution.attempt_id if task.execution else None)

    async def test_self_choice_persist_or_silence_and_duplicate_immediate_guard(self):
        req=self.counter()
        result,provider=await self.run_scripts(req,[dict(decision_kind='NO_ACTION')])
        self.assertIsNone(result.task_receipt);self.assertEqual(self.service.query_tasks(self.scope).tasks,())
        for drink in ('latte','cappuccino','americano'): self.seed(drink=drink,trigger='when_idle')
        intent=dict(op='create_task',source='self',goal=dict(kind='clean_assigned_counter',counter_key='assigned_counter'),trigger_kind='when_idle')
        result,provider=await self.run_scripts(req,[dict(decision_kind='NO_ACTION',task_operation=intent)])
        self.assertTrue(result.task_receipt.committed);self.assertEqual(result.decision.decision_kind,'NO_ACTION')
        CognitiveResponse(schema_version='1.1',request_id=req.request_id,trace_id=req.trace_id,run_id=req.run.run_id,
            agent_entity_id=req.run.agent_entity_id,decision=result.decision,task_receipt=result.task_receipt,task_result='committed')
        task=self.service.get_task(self.scope,result.task_receipt.task_id)
        self.assertEqual(len(self.tasks.read(self.tasks.prepare(req))['tasks']),1)
        self.assertIsNone(task.player);self.assertEqual(task.schedule.condition,self.tasks.condition(req))
        recovered,_=await self.run_scripts(req,[]);self.assertEqual(recovered.task_receipt.task_id,task.task_id)
        req=req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'command_id':'other_counter_choice'})})
        with self.assertRaisesRegex(ValueError,'TASK_COUNTER_ALREADY_MANAGED'):
            await self.run_scripts(req,[dict(decision_kind='AUTONOMOUS_ACTION_INTENT',action_proposal=dict(
                action_id='clean_counter',bindings=dict(target=req.context.counter_work.target_entity_id)))])
        self.assertIsNone(self.service.get_task(self.scope,task.task_id).execution)
        # The durable anchor rejects a different condition before any attempt exists.
        scope=self.tasks.prepare(req).scope
        with self.assertRaisesRegex(TaskError,'TASK_COUNTER_CONDITION_STALE'):
            self.service.reserve_execution(scope,'bad_condition',task.task_id,ExecutionRequest(expected_version=task.version,
                scene_instance_id=req.task_runtime.scene_instance_id,proposal_id='bad_proposal',trace_id='bad_trace',
                actor_entity_id=req.run.agent_entity_id,world_revision=1,target_entity_id='counter_cafe_clean_01',condition_revision=2),is_idle=True)
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(req,observed=True,condition=self.tasks.condition(req).model_dump(),visible=False))
        auto=self.automatic(self.modern(self.a),snapshot=snapshot,visible=False)
        auto=auto.model_copy(update={'context':auto.context.model_copy(update={'capabilities':req.context.capabilities,'counter_work':req.context.counter_work})})
        def choose(model):
            row=task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL',task_focus=dict(task_ref=row['task_ref'],expected_version=row['version']),
                action_proposal=dict(action_id='clean_counter',bindings=dict(target='counter_cafe_clean_01')))
        result,p=await self.run_scripts(auto,[choose])
        self.assertEqual(result.task_execution.task_id,task.task_id)
        self.assertEqual(self.service.get_task(scope,task.task_id).execution.phase,'reserved')

    async def test_self_query_defer_cancel_two_steps_and_stale_condition(self):
        req=self.counter();context=self.tasks.prepare(req)
        task=self.service.create_task(context.scope,'self_seed',TaskCreate.model_validate(dict(source_kind='self_initiated',
            source=dict(event_id='seed'),goal=dict(kind='clean_assigned_counter',counter_key='assigned_counter'),
            trigger_kind='when_idle',condition=self.tasks.condition(req).model_dump()))).task
        def edit(change):
            def choose(model):
                row=task_data(model)['tasks'][0]
                return dict(decision_kind='NO_ACTION',task_operation=dict(op='update_task',task_ref=row['task_ref'],
                    expected_version=row['version'],change=change,reason='合成自主决定',**({'trigger_kind':'when_idle'} if change=='defer' else {})))
            return choose
        result,p=await self.run_scripts(req,[dict(decision_kind='TASK_READ',task_query=dict(op='query_tasks')),edit('defer')])
        self.assertEqual(len(p.requests),2);self.assertEqual(result.task_receipt.task_status,'waiting')
        # Same private task with no visible player: offer only self work, never a drink commission.
        sync=self.sync_wire(req,observed=True,condition=self.tasks.condition(req).model_dump(),visible=False)
        snapshot=self.tasks.synchronize(self.connection,sync)
        self.assertEqual(snapshot['task_ids'],[task.task_id])
        auto=self.automatic(self.modern(self.visit(req.task_runtime.visit_id)),snapshot=snapshot,visible=False)
        # Keep actual cleaning capability/facts; the event still remains npc.task_consideration.
        auto=auto.model_copy(update={'context':auto.context.model_copy(update={'capabilities':req.context.capabilities,'counter_work':req.context.counter_work})})
        result,p=await self.run_scripts(auto,[dict(decision_kind='NO_ACTION')])
        self.assertIsNone(result.task_execution);self.assertEqual(len(p.requests),1)
        self.assertEqual(self.service.get_task(context.scope,task.task_id).status,'waiting')
        req=req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'command_id':'cancel_self'})})
        result,p=await self.run_scripts(req,[edit('cancel')]);self.assertEqual(result.task_receipt.task_status,'cancelled')
        # A distinct stale condition is cancelled deterministically, never executed or marked completed.
        self.setUp();req=self.counter();context=self.tasks.prepare(req)
        task=self.service.create_task(context.scope,'stale',TaskCreate.model_validate(dict(source_kind='self_initiated',source=dict(event_id='seed'),
            goal=dict(kind='clean_assigned_counter',counter_key='assigned_counter'),trigger_kind='when_idle',condition=self.tasks.condition(req).model_dump()))).task
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(req,observed=True))
        self.assertEqual(snapshot['task_ids'],[]);self.assertEqual(snapshot['tasks'][0]['diagnostic'],'COUNTER_CONDITION_STALE')
        self.assertEqual(self.service.get_task(context.scope,task.task_id).status,'cancelled')

    async def test_explicit_cancel_waiting_and_attempt_success_races(self):
        for order in ('waiting','cancel_first','effect_first','stopped_first'):
            with self.subTest(order=order):
                self.setUp()
                if order=='waiting':
                    req=self.modern(self.a);task=self.seed();control=self.cancellation(req,task)
                else:
                    req,ticket,_=await self.reserve();self.control(req,ticket,'begin_execution')
                    task=self.task(req,ticket);control=self.cancellation(req,task)
                    if order in ('effect_first','stopped_first'):
                        self.control(req,ticket,'settle_execution',synthetic_result(task,status='succeeded' if order=='effect_first' else 'cancelled'))
                response=cancel(self.tasks,control)
                if order=='cancel_first':
                    self.control(req,ticket,'settle_execution',synthetic_result(self.task(req,ticket)))
                response=cancel(self.tasks,control)
                self.assertEqual(response['task']['status'],'completed' if order in ('cancel_first','effect_first') else 'cancelled')
                self.assertEqual(response['outcome'],'too_late' if order in ('cancel_first','effect_first') else 'cancelled')
                saved=self.service.get_command(self.tasks.prepare(req,recovery=True).scope,control.runtime_request.task_runtime.command_id)
                self.assertEqual(saved.operation,'cancel')
                with self.assertRaisesRegex(TaskError,'TASK_COMMAND_PAYLOAD_CONFLICT'):
                    cancel(self.tasks,control.model_copy(update={'expected_version':99}))

    async def test_reconnect_scene_loss_delayed_receipt_and_new_visit(self):
        req,ticket,_=await self.reserve();self.control(req,ticket,'begin_execution');task=self.task(req,ticket)
        evidence=synthetic_result(task)
        req=self.modern(req)
        self.tasks.disconnect(self.connection)
        self.tasks=CognitiveTasks(self.service,expected_player_uid='synthetic-task-player')
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(req,offer=False))
        self.assertEqual(snapshot['tasks'][0]['status'],'executing');self.assertEqual(snapshot['task_ids'],[])
        new=req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'scene_instance_id':'new_scene'})})
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(new))
        self.assertEqual(snapshot['tasks'][0]['phase'],'uncertain');self.assertEqual(snapshot['task_ids'],[])
        self.assertEqual(self.control(req.model_copy(update={'task_runtime':req.task_runtime.model_copy(update={'lifecycle_protocol':None,'player_entity_id':None})}),ticket,'settle_execution',evidence)['task_status'],'completed')
        # Completed never reappears; waiting next_visit does, with no action automatically started.
        self.tasks.disconnect(self.connection);new=self.modern(self.visit('new_process'))
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(new))
        self.assertEqual(snapshot['task_ids'],[]);self.assertEqual(snapshot['tasks'][0]['status'],'completed')
        self.setUp();task=self.seed();new=self.modern(self.visit('new_process'))
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(new));self.assertEqual(snapshot['task_ids'],[task.task_id])
        result,p=await self.run_scripts(self.automatic(new,snapshot=snapshot),[dict(decision_kind='NO_ACTION')])
        self.assertIsNone(result.task_execution);self.assertEqual(self.service.get_task(self.scope,task.task_id).status,'waiting')

    async def test_cancel_identity_cas_storage_failure_and_status_read_token(self):
        task=self.seed();req=self.modern(self.visit());control=self.cancellation(req,task)
        snapshot=self.tasks.synchronize(self.connection,self.sync_wire(req))
        self.tasks.synchronize(self.connection,self.sync_wire(req,offer=False))
        self.assertIsNotNone(self.tasks.prepare(self.automatic(req,snapshot=snapshot)).opportunity)
        with patch.object(self.service._repo,'_save',side_effect=sqlite3.OperationalError('isolated cancel fault')):
            with self.assertRaises(sqlite3.Error):cancel(self.tasks,control)
        self.assertEqual(self.service.get_task(self.scope,task.task_id).status,'waiting')
        for mutation in ('player','owner','version','attempt'):
            value=control.model_dump(mode='json')
            if mutation=='player':value['runtime_request']['recall_identity']['current_subjects'][0]['persistent_id']='someone_else'
            if mutation=='owner':value['runtime_request']['recall_identity']['observer']['persistent_id']='someone_else'
            if mutation=='version':value['expected_version']=99
            if mutation=='attempt':value['attempt_id']='other_attempt'
            with self.subTest(mutation=mutation),self.assertRaises(TaskError):cancel(self.tasks,TaskCancelControl.model_validate(value))


        from app import m3_main as backend
        from tests.test_cognitive_websocket import SocketHarness
        socket=SocketHarness()
        socket.socket.scope.update(client=('127.0.0.1',19001),server=('127.0.0.1',19002))
        with patch.object(backend,'task_cognition',self.tasks):
            running=asyncio.create_task(backend.websocket_endpoint(socket.socket))
            try:
                with patch.object(self.service._repo,'_save',side_effect=sqlite3.OperationalError('isolated cancel wire fault')):
                    socket.message(control.model_dump(mode='json'))
                    reply=await asyncio.wait_for(socket.outgoing.get(),3)
                self.assertFalse(reply['accepted']);self.assertEqual(reply['error_code'],'TASK_STORE_UNAVAILABLE')
                socket.message(control.model_dump(mode='json'))
                reply=await asyncio.wait_for(socket.outgoing.get(),3)
                self.assertTrue(reply['accepted']);self.assertEqual(reply['task']['status'],'cancelled')
            finally:
                socket.disconnect();await running


# Reused helpers do not duplicate the historical test suite.
