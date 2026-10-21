"""Offline task observations; callers must set private paths before module import."""
import json
import logging
import os
from pathlib import Path
import tempfile
import unittest
from contextlib import closing

ROOT=Path(os.environ['EMBODIEDNPC_TEST_ROOT']).resolve()
for key in ('EMBODIEDNPC_MEMORY_DB','EMBODIEDNPC_MODEL_CONFIG_FILE','EMBODIEDNPC_SETTINGS_FILE',
            'EMBODIEDNPC_KNOWLEDGE_FILE','EMBODIEDNPC_KNOWLEDGE_INDEX_DB','EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE'):
    assert Path(os.environ[key]).resolve().is_relative_to(ROOT), 'TEST_APP_ISOLATION_REQUIRED'

from app import task_diagnostics as diagnostic
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_tasks import CognitiveTasks
from app.local_model_scheduler import PrioritizedModelExecutor
from app.sqlite_database import SQLiteDatabase
from app.task_repository import TaskRepository
from app.task_service import TaskService
from app.task_execution import TaskExecutionControl
from tests import test_cognitive_tasks as cognition
from tests import test_task_consideration as consideration
from tests import test_task_execution as execution
from tests import test_task_service as service_fixtures

class Capture(logging.Handler):
    def __init__(self):super().__init__();self.rows=[]
    def emit(self,record):
        text=record.getMessage()
        if text.startswith('TASK_DIAGNOSTIC '):self.rows.append(json.loads(text[len('TASK_DIAGNOSTIC '):]))

class TaskDiagnosticsTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory=tempfile.TemporaryDirectory(prefix='diagnostic-',dir=ROOT)
        self.addCleanup(self.directory.cleanup)
        self.database=SQLiteDatabase(Path(self.directory.name)/'private.sqlite3')
        self.service=TaskService(TaskRepository(self.database.connect),clock=lambda:1000.0)
        self.tasks=CognitiveTasks(self.service,expected_player_uid='synthetic-task-player')
        self.capture=Capture();self.level=diagnostic.logger.level
        diagnostic.logger.setLevel(logging.INFO);diagnostic.logger.addHandler(self.capture)
        diagnostic._count=diagnostic._bytes=0;diagnostic._limited=False
        self.a=cognition.request()
    def tearDown(self):
        (ROOT/(self._testMethodName+'.json')).write_text(json.dumps(self.capture.rows,ensure_ascii=False,indent=2),encoding='utf-8')
        diagnostic.logger.removeHandler(self.capture);diagnostic.logger.setLevel(self.level)
    async def run_model(self,request,scripts):
        provider=cognition.Scripted(scripts)
        result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),tasks=self.tasks,context_length=32768).run(request)
        return result,provider
    async def opportunity(self):
        saved,_=await self.run_model(self.a,[cognition.ACCEPT])
        h=consideration.TaskConsiderationTests();h.tasks=self.tasks;h.connection=object()
        visit=h.visit('new_game_visit')
        snapshot=h.sync(visit)
        return h,h.automatic(visit,snapshot=snapshot),saved.task_receipt.task_id
    def rows(self,stage):return [x for x in self.capture.rows if x.get('task_stage')==stage]

    async def test_1_decline_has_false_no_operation_and_no_mutation(self):
        result,provider=await self.run_model(self.a,[cognition.DECLINE])
        self.assertIsNone(result.task_receipt)
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(self.a).scope).tasks,())
        row=self.rows('decision')[-1]
        self.assertEqual((row['future_commitment'],row['operation']),('false','none'))
        self.assertFalse(self.rows('persistence'));self.assertEqual(len(provider.requests),1)

    async def test_2_accept_links_validated_decision_transaction_and_receipt(self):
        result,_=await self.run_model(self.a,[cognition.ACCEPT])
        self.assertTrue(result.task_receipt.committed)
        self.assertEqual(self.rows('decision')[-1]['future_commitment'],'true')
        attempted,committed=self.rows('persistence')
        self.assertTrue(attempted['persistence_attempted']);self.assertTrue(committed['receipt_committed'])
        self.assertEqual(committed['task_id'],diagnostic.correlation(result.task_receipt.task_id))
        self.assertEqual(attempted['request_id'],committed['request_id'])
        self.assertEqual(committed['task_status'],'waiting')

    async def test_3_new_visit_no_action_is_real_consideration_not_untriggered(self):
        h,req,task_id=await self.opportunity()
        result,provider=await self.run_model(req,[dict(decision_kind='NO_ACTION')])
        self.assertEqual(result.decision.decision_kind,'NO_ACTION')
        stored=self.service.get_task(self.tasks.prepare(self.a).scope,task_id)
        self.assertEqual((stored.status,stored.consideration.automatic_count),('waiting',1))
        self.assertIsNone(stored.execution);self.assertEqual(len(provider.requests),1)
        self.assertTrue(self.rows('sync')[-1]['opportunity_offered'])
        self.assertTrue(any(x['task_state']=='entered' for x in self.rows('consideration')))
        self.assertEqual(self.rows('consideration')[-1]['task_state'],'response_received')
        self.assertEqual(self.rows('decision')[-1]['future_commitment'],'na')
        self.assertEqual(self.rows('decision')[-1]['decision_kind'],'NO_ACTION')
        self.assertFalse(self.rows('execution'))

    async def test_4_proposal_reservation_begin_and_synthetic_settlement_are_distinct(self):
        h,req,task_id=await self.opportunity()
        def choose(model):
            row=cognition.task_data(model)['tasks'][0]
            return dict(decision_kind='ACTION_PROPOSAL',task_focus=dict(task_ref=row['task_ref'],expected_version=row['version']),
                action_proposal=dict(action_id='prepare_drink',bindings=dict(drink_type='latte')))
        result,_=await self.run_model(req,[choose]);ticket=result.task_execution
        self.assertEqual(self.rows('execution')[-1]['task_state'],'reserved')
        self.assertFalse(self.rows('execution')[-1]['start_permitted'])
        def control(op,value=None):
            return self.tasks.executions.control(TaskExecutionControl.model_validate(dict(message_type='task_control',schema_version='1.1',
                operation=op,original_request=execution.wire(req),ticket=ticket.model_dump(mode='json'),result=value.model_dump(mode='json') if value else None)))
        self.assertTrue(control('begin_execution')['start_permitted'])
        self.assertEqual(self.rows('execution')[-1]['task_state'],'permitted')
        stored=self.service.get_task(self.tasks.prepare(req,recovery=True).scope,task_id)
        # Explicitly synthetic Backend result; Unity world proof is a separate test.
        control('settle_execution',service_fixtures.synthetic_result(stored,world_revision=11))
        self.assertEqual(self.rows('result')[-1]['task_status'],'completed')
        self.assertEqual(self.rows('result')[-1]['phase'],'succeeded')

    async def test_5_schema_and_store_failures_are_not_declines(self):
        with self.assertRaises(ValueError):
            await self.run_model(self.a,[dict(decision_kind='DIRECT_RESPONSE',utterance='敏感原文不能进技术日志',future_commitment=True)])
        self.assertEqual(self.rows('error')[-1]['error_code'],'SCHEMA_ERROR')
        with closing(self.database.connect()) as db, db:
            db.execute("CREATE TRIGGER reject_task BEFORE INSERT ON npc_tasks BEGIN SELECT RAISE(ABORT,'SYNTHETIC_FAILURE'); END")
        result,_=await self.run_model(self.a,[cognition.ACCEPT])
        self.assertFalse(result.task_receipt.committed)
        self.assertTrue(self.rows('persistence')[-2]['persistence_attempted'])
        self.assertEqual(self.rows('persistence')[-1]['error_code'],'TASK_NOT_SAVED')
        self.assertNotIn('敏感原文',json.dumps(self.capture.rows,ensure_ascii=False))

    def test_6_privacy_finite_fields_and_limit(self):
        diagnostic.emit('decision',task_state='validated',request_id='raw-private-id',future_commitment='false',
            utterance='PRIVATE_DIALOGUE',prompt='PRIVATE_PROMPT',opportunity_token='REUSABLE_TOKEN',api_key='SECRET')
        text=json.dumps(self.capture.rows)
        for forbidden in ['PRIVATE_DIALOGUE','PRIVATE_PROMPT','REUSABLE_TOKEN','SECRET','raw-private-id']:
            self.assertNotIn(forbidden,text)
        self.assertEqual(self.capture.rows[0]['request_id'],diagnostic.correlation('raw-private-id'))
        for _ in range(600):diagnostic.emit('protocol',task_state='entered')
        self.assertLessEqual(len(self.capture.rows),diagnostic.MAX_RECORDS+1)
        self.assertEqual(sum(x.get('task_state')=='limit_reached' for x in self.capture.rows),1)
