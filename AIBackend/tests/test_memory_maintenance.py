"""Small B1 coverage using the real A2 fixture and isolated SQLite databases."""
import asyncio
from contextlib import closing
from dataclasses import replace
import json
import time
import unittest
from unittest.mock import patch
import httpx

from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_recall_models import CanonicalReadFilter
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_maintenance_models import MaintenancePolicy, MaintenanceResponse, MaintenanceOperation, MaintenanceStale, MaintenanceAdmissionError, MaintenanceDecisionResponse
from app.memory_maintenance_repository import MemoryMaintenanceRepository
from app.memory_maintenance_worker import MemoryMaintenanceWorker, maintenance_request, parse_maintenance_response, MaintenanceOutputError
from app.memory_reflection_models import ReflectionPolicy
from app.openai_compatible_provider import OpenAICompatibleProvider
from app.providers import ModelResult
from app.sqlite_database import SQLiteDatabase
from tests import test_memory_c2a as c2a


class Provider:
    name='fake'
    context_length=8192
    def __init__(self):
        self.calls=[]
        self.output=None
    async def generate(self,request):
        self.calls.append(request)
        if self.output is None:
            pairs=json.loads(request.messages[-1].content.split('\n',1)[1])['pairs']
            return ModelResult(text=json.dumps({'decisions':[dict(pair_ref=p['pair_ref'],relation='NO_RELATION',
                direction='LEFT_TO_RIGHT',reason='independent claims') for p in pairs]}))
        return ModelResult(text=self.output)


class MemoryMaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.f=c2a.MemoryC2ATests(methodName='runTest')
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.repo=MemoryMaintenanceRepository(self.f.database.connect)
        self.policy=MaintenancePolicy(backlog_threshold=1,max_wait_seconds=0)
        self.now=time.time()+100

    def add(self,text,**scope):
        job,context=self.f.work(text=text,**scope)
        response=self.f.response(context)
        response['semantic_facts'][0].update(fact=text,source_excerpt=text)
        self.f.finalize(job,context,response)
        return self.f.admission.evaluate(context,response).memories[0].memory_id

    def batch(self):
        job=self.repo.claim(self.now,self.policy)
        self.assertIsNotNone(job)
        return self.repo.prepare(job,self.policy)

    def op(self,context,relation,new,old,excerpt=None):
        row=next(r for r in context.records if r['memory_id']==new)
        return MaintenanceOperation(relation=relation,new_memory_id=new,old_memory_id=old,reason='source-qualified relation',
            evidence_event_ids=[row['excerpt_event_id']] if excerpt else [],source_excerpt=excerpt)

    def finish(self,context,*ops):
        return self.repo.finalize(context,MaintenanceResponse(operations=ops),self.now)

    def read(self,context,query='喜欢',history=False):
        return CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.f.database.connect)).retrieve(
            observer=context.owner,subject=context.subject,semantic_query=query,
            filters=CanonicalReadFilter(lifecycle='ALL_HISTORY' if history else 'CURRENT'))

    def test_response_root_and_provider_schema_are_preserved(self):
        a=self.add('我喜欢拿铁。')
        b=self.add('我也喜欢卡布奇诺。')
        context=self.batch()
        op=self.op(context,'COEXISTS',b,a).model_dump(mode='json')
        body=json.dumps({'operations':[op]})
        self.assertEqual(parse_maintenance_response(ModelResult(text=body)).operations[0].relation,'COEXISTS')
        self.assertEqual(parse_maintenance_response(ModelResult(text='{"operations":[]}')).operations,())
        for text in ('{}',json.dumps(op),json.dumps([op]),body+body,'{"result":'+body+'}',body[:-1]):
            with self.subTest(text=text),self.assertRaises(MaintenanceOutputError):
                parse_maintenance_response(ModelResult(text=text))
        with self.assertRaisesRegex(MaintenanceOutputError,'OUTPUT_TRUNCATED'):
            parse_maintenance_response(ModelResult(text=body,finish_reason='length'))
        request=maintenance_request(context)
        def handle(wire):
            sent=json.loads(wire.content)
            self.assertEqual(sent['response_format']['json_schema']['schema'],MaintenanceDecisionResponse.model_json_schema())
            self.assertEqual(sent['response_format']['json_schema']['schema']['required'],['decisions'])
            self.assertEqual(sent['max_tokens'],1600)
            return httpx.Response(200,json={'choices':[{'message':{'content':body},'finish_reason':'stop'}],
                                          'usage':{'completion_tokens':100}})
        provider=OpenAICompatibleProvider(base_url='http://test.invalid/v1',api_key=None,model='test',
                                          transport=httpx.MockTransport(handle),schema_profile='raw')
        result=asyncio.run(provider.generate(request))
        self.assertEqual(result.finish_reason,'stop')
        self.assertEqual(parse_maintenance_response(result).operations[0].relation,'COEXISTS')

    def test_invalid_model_envelope_never_partially_commits(self):
        a=self.add('我喜欢拿铁。')
        b=self.add('现在不喜欢拿铁了。')
        context=self.batch()
        self.repo.requeue(context.job_id,context.generation,self.now,'test')
        before=self.f.rows('SELECT * FROM memory_records ORDER BY memory_id')
        op=self.op(context,'SUPERSEDES',b,a,'现在不喜欢拿铁了').model_dump(mode='json')
        p=Provider()
        p.output=json.dumps([op])  # A valid inner operation is still an invalid root.
        worker=MemoryMaintenanceWorker(self.repo,p,PrioritizedModelExecutor(),policy=self.policy,clock=lambda:self.now)
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_records ORDER BY memory_id'))
        self.assertEqual(self.f.rows('SELECT * FROM memory_relations'),[])
        self.assertEqual(self.f.rows('SELECT * FROM memory_maintenance_runs'),[])
        self.assertTrue(all(r['processed_at'] is None for r in self.f.rows('SELECT * FROM memory_maintenance_items')))
        job=self.f.rows('SELECT * FROM memory_maintenance_jobs')[0]
        self.assertEqual(job['error_code'],'MAINTENANCE_SCHEMA_INVALID')
        p.output=None
        self.now+=100
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_records ORDER BY memory_id'))
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_maintenance_runs')),1)
        self.assertFalse(asyncio.run(worker.process_once()))

    def test_queue_frequency_no_work_no_call_and_no_change_ack(self):
        p=Provider()
        worker=MemoryMaintenanceWorker(self.repo,p,PrioritizedModelExecutor(),clock=lambda:self.now,
                                      reflection_policy=ReflectionPolicy(min_episodes=8))
        self.assertFalse(asyncio.run(worker.process_once()))
        self.add('我喜欢拿铁。')
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_maintenance_items')),1)
        self.assertFalse(asyncio.run(worker.process_once()))  # below threshold and age
        worker.policy=self.policy
        self.assertTrue(asyncio.run(worker.process_once()))  # singleton processed without a model
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(p.calls,[])
        self.add('我也喜欢卡布奇诺。')
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertEqual(len(p.calls),1)
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_maintenance_runs')),2)
        self.assertTrue(all(r['status']=='ACTIVE' for r in self.f.rows('SELECT * FROM memory_records')))
        self.add('如果有空，我想学陶艺。')
        p.output='{}'
        for _ in range(3):
            self.now+=400
            self.assertTrue(asyncio.run(worker.process_once()))
        self.assertFalse(asyncio.run(worker.process_once()))
        job=self.f.rows('SELECT * FROM memory_maintenance_jobs')[0]
        self.assertEqual(job['status'],'FAILED')
        self.assertEqual(len(p.calls),4)  # one prior NO_CHANGE and three bounded failures
        self.assertTrue(self.repo.retry_failed(job['job_id'],self.now))
        p.output=None
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertFalse(asyncio.run(worker.process_once()))

    def test_duplicate_refines_coexist_are_reversible_without_deleting_evidence(self):
        ids=[self.add(t) for t in ('我喜欢拿铁。','我喜欢拿铁咖啡。','我喜欢拿铁，尤其是热拿铁。','我也喜欢卡布奇诺。')]
        context=self.batch()
        before=self.f.rows('SELECT * FROM memory_evidence ORDER BY memory_id')
        ops=[self.op(context,'DUPLICATE',ids[1],ids[0]),self.op(context,'REFINES',ids[2],ids[0]),
             self.op(context,'COEXISTS',ids[3],ids[0])]
        self.assertTrue(self.finish(context,*ops))
        self.assertFalse(self.finish(context,*ops))
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_evidence ORDER BY memory_id'))
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_records')),4)
        self.assertTrue(all(r['status']=='ACTIVE' for r in self.f.rows('SELECT * FROM memory_records')))
        run=self.f.rows('SELECT * FROM memory_maintenance_runs')[0]
        self.assertTrue(self.repo.undo_run(run['run_id'],self.now+1))
        self.assertFalse(self.repo.undo_run(run['run_id'],self.now+2))
        self.assertEqual({r['active'] for r in self.f.rows('SELECT * FROM memory_relations')},{0})

    def test_negation_restoration_preserves_three_phases_and_recall_history(self):
        a=self.add('我喜欢拿铁。')
        b=self.add('以前喜欢拿铁，现在不喜欢了。')
        c=self.add('我现在又喜欢拿铁了。')
        context=self.batch()
        self.assertEqual(len(self.read(context).items),3)  # pending facts are immediately recalled
        self.finish(context,self.op(context,'SUPERSEDES',b,a,'现在不喜欢了'),
                    self.op(context,'SUPERSEDES',c,b,'现在又喜欢拿铁了'))
        current=self.read(context)
        self.assertEqual([i.memory_id for i in current.items],[c])
        self.assertEqual({i.memory_id for i in self.read(context,history=True).items},{a,b,c})
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_evidence')),3)
        self.assertTrue(any(r.related_lifecycle_status=='SUPERSEDED' for r in current.items[0].maintenance_relations))

    def test_conflict_and_unknown_keep_uncertainty_with_bounded_input(self):
        a=self.add('我在海边长大。')
        b=self.add('我在山边长大。')
        context=self.batch()
        self.finish(context,self.op(context,'CONFLICTS',b,a))
        result=self.read(context,'长大')
        self.assertEqual({i.lifecycle_status for i in result.items},{'CONFLICTED'})
        self.assertTrue(all(i.maintenance_relations for i in result.items))
        self.add('我小时候去过海边。')
        context=self.batch()
        self.finish(context,self.op(context,'UNDETERMINED',context.new_ids[0],a))
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_relations')),1)
        worker=MemoryMaintenanceWorker(self.repo,Provider(),PrioritizedModelExecutor())
        bounded,request=worker._bounded(context)
        self.assertLessEqual(len(bounded.records),self.policy.new_limit+self.policy.related_limit)
        self.assertNotIn('merchant.001',request.model_dump_json())
        self.assertNotIn('U10001',request.model_dump_json())

    def test_scope_anchor_and_stale_revision_fail_without_writes_then_recover(self):
        a=self.add('我喜欢拿铁。')
        b=self.add('现在不喜欢拿铁了。')
        foreign=self.add('我喜欢拿铁。',uid='OTHER_PLAYER')
        self.add('我喜欢拿铁。',observer='other_npc',owner='other.owner')
        context=self.batch()
        # A bare operation cannot bypass the strict response envelope, even if
        # its relation is valid. No permissive root-object repair is performed.
        with self.assertRaises(ValueError):
            MaintenanceResponse.model_validate(dict(relation='COEXISTS',new_memory_id=b,old_memory_id=a,reason='bare'))
        prompt=maintenance_request(context)
        self.assertIn('REQUIRED decisions array',prompt.messages[0].content)
        self.assertEqual(prompt.inference_options.max_output_tokens,1600)
        self.assertEqual(set(r['memory_id'] for r in context.records),{a,b})
        bad=MaintenanceOperation(relation='DUPLICATE',new_memory_id=b,old_memory_id=foreign,reason='bad scope')
        with self.assertRaises(MaintenanceAdmissionError):
            self.finish(context,bad)
        with self.assertRaises(MaintenanceAdmissionError):
            self.finish(context,self.op(context,'SUPERSEDES',b,a))
        with closing(self.f.database.connect()) as db,db:
            db.execute('UPDATE memory_records SET revision=revision+1 WHERE memory_id=?',(a,))
        with self.assertRaises(MaintenanceStale):
            self.finish(context,self.op(context,'SUPERSEDES',b,a,'现在不喜欢拿铁了'))
        self.assertEqual(self.f.rows('SELECT * FROM memory_maintenance_runs'),[])
        # A fresh repository recovers a process abandoned in RUNNING state.
        restarted=MemoryMaintenanceRepository(SQLiteDatabase(self.f.database.database_path).connect)
        job=restarted.claim(self.now+self.policy.stale_seconds+1,self.policy)
        fresh=restarted.prepare(job,self.policy)
        with self.assertRaises(MaintenanceStale):
            self.finish(context)
        self.assertTrue(restarted.finalize(fresh,MaintenanceResponse(operations=()),self.now+200))
        self.assertEqual({r['status'] for r in self.f.rows('SELECT * FROM memory_records')},{'ACTIVE'})

    def test_populated_schema16_additive_upgrade_preserves_canonical_and_queues(self):
        self.f.doCleanups()
        with patch('app.sqlite_database.migrate_memory_reflection',lambda db:None):
            self.f=c2a.MemoryC2ATests(methodName='runTest')
            self.f.setUp()
            self.addCleanup(self.f.doCleanups)
            self.add('如果有空，我想学陶艺。')
        before=self.f.rows('SELECT * FROM memory_records')
        # Reconstruct a populated 16 store by removing only new B1 objects in this temp DB.
        with closing(self.f.database.connect()) as db,db:
            for table in ('memory_relations','memory_maintenance_runs','memory_maintenance_items','memory_maintenance_jobs'):
                db.execute('DROP TABLE '+table)
            db.execute('DELETE FROM schema_migrations WHERE version=17')
        reopened=SQLiteDatabase(self.f.database.database_path)
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_records'))
        with closing(reopened.connect()) as db:
            self.assertEqual(db.execute('SELECT MAX(version) FROM schema_migrations').fetchone()[0],18)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM memory_maintenance_items WHERE processed_at IS NULL').fetchone()[0],1)
            self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(),[])


if __name__=='__main__':
    unittest.main()
