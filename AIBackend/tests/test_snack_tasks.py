"""Snack future tasks: temporary SQLite and scripted providers, never production data."""
import json
import tempfile
import unittest
from pathlib import Path

from pydantic import ValidationError
from app.action_catalog import SERVE_SNACK_REGISTRATION
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_tasks import CognitiveTasks
from app.contracts import CognitiveRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.snack_catalog import SNACK_TYPE_IDS, SNACK_QUANTITY_IDS
from app.sqlite_database import SQLiteDatabase
from app.task_models import TaskCreate, TaskError, TaskUpdate, TrustedTaskResult
from app.task_repository import TaskRepository
from app.task_service import TaskService
from tests import test_cognitive_tasks as cognition
from tests import test_task_consideration as consideration
from tests import test_task_execution as execution


def request(number='snack', snack='croissant', quantity='1'):
    raw = execution.wire(cognition.request(number=number))
    descriptor = SERVE_SNACK_REGISTRATION.descriptor
    raw['context']['capabilities']['available_actions'] = [dict(action_id=descriptor.action_id,
        description=descriptor.description, bindings=[dict(name=b.name, kind=b.kind,
            required=b.required, enum_values=b.enum_values) for b in descriptor.binding_schema])]
    raw['context']['current_event']['payload'] = [dict(key='text', value=f'下次来时给我{quantity}份{snack}。')]
    return CognitiveRequest.model_validate(raw)


def accept(snack='croissant', quantity='1'):
    return dict(decision_kind='DIRECT_RESPONSE', utterance='好，下次你来时我给你准备。',
        future_commitment=True, task_operation=dict(op='create_task', source='requester',
            goal=dict(kind='serve_snack', snack_type=snack, quantity=quantity), trigger_kind='next_visit'))


def result(task, status='succeeded', **changes):
    attempt = task.execution
    req = attempt.request
    receipt = dict(kind='snack_served', receipt_id='synthetic_snack_receipt',
        actor_entity_id=req.actor_entity_id, proposal_id=req.proposal_id, trace_id=req.trace_id,
        world_revision=req.world_revision+1, player=task.player.model_dump(),
        player_entity_id=req.player_entity_id, snack_type=task.goal.snack_type, quantity=task.goal.quantity)
    raw = dict(attempt_id=attempt.attempt_id, scene_instance_id=req.scene_instance_id,
        proposal_id=req.proposal_id, trace_id=req.trace_id, actor_entity_id=req.actor_entity_id,
        outcome_id='synthetic_snack_outcome', goal_type='serve_snack', status=status,
        effect='committed' if status=='succeeded' else 'not_committed',
        world_revision=req.world_revision+1, receipt=receipt if status=='succeeded' else None,
        failure_code=None if status=='succeeded' else 'TASK_NOT_STARTED' if status=='failed' else 'CANCELLED_BY_PLAYER')
    raw.update(changes)
    return TrustedTaskResult.model_validate(raw)


class SnackTaskTests(unittest.IsolatedAsyncioTestCase):
    sync = consideration.TaskConsiderationTests.sync
    automatic = consideration.TaskConsiderationTests.automatic
    run_scripts = consideration.TaskConsiderationTests.run_scripts
    control = execution.TaskExecutionTests.control
    task = execution.TaskExecutionTests.task

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.path = Path(temporary.name)/'tasks.sqlite3'
        self.db = SQLiteDatabase(self.path)
        self.now = 1000.
        self.service = TaskService(TaskRepository(self.db.connect), clock=lambda:self.now)
        self.tasks = CognitiveTasks(self.service, expected_player_uid='synthetic-task-player')
        self.connection = object()
        self.a = request()
        self.scope = self.tasks.prepare(self.a).scope

    def visit(self, name='session_b', **kwargs):
        raw = execution.wire(request(number=name))
        raw['task_runtime'].update(client_session_id=name, visit_id=name)
        return CognitiveRequest.model_validate(raw)

    def seed(self, snack='croissant', quantity='1', command='snack_seed'):
        return self.service.create_task(self.scope, command, TaskCreate.model_validate(dict(
            source_kind='player_request', source=dict(event_id='seed_event'),
            goal=dict(kind='serve_snack', snack_type=snack, quantity=quantity), trigger_kind='next_visit'))).task

    async def reserve(self, snack='croissant', quantity='1', *, supplied_quantity=None):
        self.seed(snack, quantity)
        req = self.automatic()
        def choose(model):
            task = cognition.task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL', task_focus=dict(task_ref=task['task_ref'], expected_version=task['version']),
                action_proposal=dict(action_id='serve_snack', bindings=dict(snack_type=snack,
                    quantity=quantity if supplied_quantity is None else supplied_quantity)))
        decision, provider = await self.run_scripts(req, [choose])
        return req, decision.task_execution, provider

    async def test_future_order_commits_before_ack_without_immediate_execution_and_recovers(self):
        decision, provider = await self.run_scripts(self.a, [accept()])
        self.assertTrue(decision.task_receipt.committed)
        self.assertIsNone(decision.validated_goal)
        self.assertIsNone(decision.task_execution)
        task = self.service.get_task(self.scope, decision.task_receipt.task_id)
        self.assertEqual((task.goal.kind, task.goal.snack_type, task.goal.quantity, task.status),
                         ('serve_snack', 'croissant', '1', 'waiting'))
        self.assertIn('serve_snack', cognition.task_data(provider.requests[0])['available_goal_kinds'])
        self.tasks = CognitiveTasks(TaskService(TaskRepository(SQLiteDatabase(self.path).connect)),
                                   expected_player_uid='synthetic-task-player')
        recovered, retry = await self.run_scripts(self.a, [])
        self.assertEqual(retry.requests, [])
        self.assertTrue(recovered.task_receipt.recovered)
        self.assertEqual(recovered.task_receipt.task_id, task.task_id)

    def test_all_menu_types_quantities_persist_and_invalid_values_are_rejected(self):
        for snack in SNACK_TYPE_IDS:
            for quantity in SNACK_QUANTITY_IDS:
                with self.subTest(snack=snack, quantity=quantity):
                    task = self.seed(snack, quantity, snack+quantity)
                    self.assertEqual((task.goal.snack_type, task.goal.quantity), (snack, quantity))
                    self.service.cancel_task(self.scope, 'cancel_'+snack+quantity, task.task_id,
                        expected_version=task.version, reason='test cleanup')
        for snack, quantity in [('cheesecake','1'), ('croissant','5'), ('croissant',1), ('croissant','01')]:
            with self.subTest(snack=snack, quantity=quantity), self.assertRaises(ValidationError):
                TaskCreate.model_validate(dict(source_kind='player_request', source=dict(event_id='x'),
                    goal=dict(kind='serve_snack',snack_type=snack,quantity=quantity),trigger_kind='next_visit'))

    def test_same_visit_does_not_trigger_and_duplicate_order_is_not_saved_twice(self):
        first = self.seed()
        second = self.seed(command='another_command')
        self.assertEqual(first.task_id, second.task_id)
        self.assertEqual(self.sync(self.a)['task_ids'], [])
        self.assertEqual(self.service.get_task(self.scope, first.task_id).status, 'waiting')
        self.tasks.disconnect(self.connection)
        self.connection = object()  # A new Visit comes from a new transport lifecycle.
        self.assertIn(first.task_id, self.sync(self.visit())['task_ids'])

    async def test_next_visit_reserves_starts_once_and_requires_exact_snack_receipt(self):
        req, ticket, provider = await self.reserve('pudding','3')
        self.assertEqual(len(provider.requests), 1)
        self.assertEqual(self.task(req,ticket).execution.phase, 'reserved')
        self.assertTrue(self.control(req,ticket,'begin_execution')['start_permitted'])
        with self.assertRaises(TaskError):
            self.control(req,ticket,'begin_execution')
        task = self.task(req,ticket)
        proof = result(task)
        for field, wrong in [('snack_type','macaron'), ('quantity','2'), ('player_entity_id','other_player'),
                             ('trace_id','other_trace'), ('world_revision',ticket.execution_request.world_revision)]:
            with self.subTest(field=field), self.assertRaises(TaskError):
                self.control(req,ticket,'settle_execution',proof.model_copy(update={
                    'receipt':proof.receipt.model_copy(update={field:wrong})}))
        self.assertEqual(self.task(req,ticket).status, 'executing')
        self.assertEqual(self.control(req,ticket,'settle_execution',proof)['task_status'], 'completed')
        version = self.task(req,ticket).version
        self.control(req,ticket,'settle_execution',proof)
        self.assertEqual(self.task(req,ticket).version, version)
        self.tasks.disconnect(self.connection)
        self.connection = object()
        self.assertEqual(self.sync(self.visit(name='session_c'))['task_ids'], [])

    async def test_saved_quantity_cannot_be_changed_by_execution_proposal(self):
        with self.assertRaisesRegex(TaskError, 'TASK_ACTION_GOAL_MISMATCH'):
            await self.reserve('croissant','2',supplied_quantity='1')

    def test_edit_defer_and_cancel_reuse_existing_task_service(self):
        task = self.seed()
        goal = TaskCreate.model_validate(dict(source_kind='player_request',source=dict(event_id='e'),
            goal=dict(kind='serve_snack',snack_type='macaron',quantity='2'),trigger_kind='next_visit')).goal
        task = self.service.update_task(self.scope,'edit',task.task_id,TaskUpdate(expected_version=task.version,
            reason='change order',goal=goal)).task
        self.assertEqual((task.goal.snack_type,task.goal.quantity),('macaron','2'))
        task = self.service.defer_task(self.scope,'defer',task.task_id,expected_version=task.version,
            trigger_kind='when_idle',reason='later').task
        self.assertEqual(task.trigger_kind,'when_idle')
        task = self.service.cancel_task(self.scope,'cancel',task.task_id,expected_version=task.version,reason='cancel').task
        self.assertEqual(task.status,'cancelled')
        self.assertEqual(self.sync(self.visit())['task_ids'],[])

    async def test_cancel_before_start_prevents_execution_and_cannot_complete(self):
        req,ticket,_ = await self.reserve()
        task = self.task(req,ticket)
        scope = self.tasks.prepare(req,recovery=True).scope
        self.service.cancel_task(scope,'cancel',task.task_id,expected_version=task.version,reason='cancel')
        with self.assertRaises(TaskError):
            self.control(req,ticket,'begin_execution')
        proof = result(self.task(req,ticket), status='failed')
        self.assertEqual(self.control(req,ticket,'not_started',proof)['task_status'],'cancelled')

    async def test_cancel_during_execution_without_effect_never_completes(self):
        req,ticket,_ = await self.reserve()
        self.control(req,ticket,'begin_execution')
        task = self.task(req,ticket)
        self.service.cancel_task(self.tasks.prepare(req,recovery=True).scope,'cancel',task.task_id,
            expected_version=task.version,reason='cancel')
        self.assertEqual(self.control(req,ticket,'settle_execution',result(self.task(req,ticket),'cancelled'))['task_status'],'cancelled')

    async def test_unadvertised_snack_cannot_be_saved_and_failed_commit_suppresses_promise(self):
        raw=execution.wire(self.a)
        raw['context']['capabilities']['available_actions'][0]['bindings'][0]['enum_values']=['pudding']
        decision, _ = await self.run_scripts(CognitiveRequest.model_validate(raw),[accept()])
        self.assertFalse(decision.task_receipt.committed)
        self.assertNotEqual(decision.decision.utterance,accept()['utterance'])
        self.assertEqual(self.service.query_tasks(self.scope).tasks,())
        scope=self.scope.model_copy(update={'allowed_goal_kinds':()})
        with self.assertRaises(TaskError):
            self.service.create_task(scope,'forbidden',TaskCreate.model_validate(dict(source_kind='player_request',
                source=dict(event_id='x'),goal=dict(kind='serve_snack',snack_type='croissant',quantity='1'),trigger_kind='next_visit')))
