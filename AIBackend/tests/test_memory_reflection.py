"""Minimal B2 closed-loop checks on real ledger/Episode/Canonical fixtures."""
import asyncio
from contextlib import closing
from dataclasses import replace
import json
import time
import unittest
from unittest.mock import patch

from app.canonical_embeddings import CanonicalEmbeddingIndex, CanonicalEmbeddingIndexer, embedding_text
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_recall_models import CanonicalReadFilter
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_maintenance_models import MaintenancePolicy, MaintenanceResponse, MaintenanceStale, MaintenanceAdmissionError
from app.memory_maintenance_repository import MemoryMaintenanceRepository
from app.memory_maintenance_worker import MemoryMaintenanceWorker, parse_maintenance_response, MaintenanceOutputError
from app.memory_reflection_models import ReflectionPolicy, ReflectionResponse, InsightCandidate
from app.memory_reflection_repository import ReflectionTasks
from app.providers import ModelResult
from app.sqlite_database import SQLiteDatabase
from tests import test_memory_c2a as c2a


class ReflectionProvider:
    name='fake'
    context_length=8192
    def __init__(self, output=None):
        self.calls=[]
        self.output=output
    async def generate(self, request):
        self.calls.append(request)
        if 'operations' in request.response_schema['properties']:
            return ModelResult(text='{"operations":[]}')
        records=json.loads(request.messages[1].content.split('\n',1)[1])['memories']
        result=self.output if self.output is not None else dict(insights=[dict(
            insight='根据几次交流，玩家似乎会结合当天状态选择饮品，疲惫时倾向咖啡，需要休息时选择清淡饮品。',
            source_memory_ids=[r['memory_id'] for r in records],
            evidence_event_ids=list(dict.fromkeys(e['event_id'] for r in records for e in r['evidence'])),
            derivation_type='PATTERN',uncertainty='TENTATIVE')])
        return ModelResult(text=json.dumps(result,ensure_ascii=False))


class MemoryReflectionTests(unittest.TestCase):
    def setUp(self):
        self.f=c2a.MemoryC2ATests(methodName='runTest')
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.repo=MemoryMaintenanceRepository(self.f.database.connect)
        self.reflection=ReflectionTasks(self.repo)
        self.policy=ReflectionPolicy(min_wait_seconds=0)
        self.maintenance_policy=MaintenancePolicy(backlog_threshold=1,max_wait_seconds=0)
        self.now=time.time()+1000

    def add(self,text,episodic=False,**scope):
        job,context=self.f.work(text=text,**scope)
        response=self.f.response(context,episodic=episodic,summary=text)
        if not episodic:
            response['semantic_facts'][0].update(fact=text,source_excerpt=text)
        self.f.finalize(job,context,response)
        return self.f.admission.evaluate(context,response).memories[0].memory_id

    def sample(self):
        self.add('今天很累，我选择拿铁提神。',occurred=10)
        self.add('今天需要专注学习，我选择美式咖啡。',episodic=True,occurred=20)
        self.add('今天想休息，我选择清淡的茶，不需要提神。',occurred=30)
        self.drain_maintenance()

    def drain_maintenance(self):
        while (job:=self.repo.claim(self.now,self.maintenance_policy)) is not None:
            context=self.repo.prepare(job,self.maintenance_policy)
            self.repo.finalize(context,MaintenanceResponse(operations=()),self.now)

    def batch(self):
        job=self.reflection.claim(self.now,self.policy,180)
        self.assertIsNotNone(job)
        return self.reflection.prepare(job,self.policy)

    def response(self,context):
        return ReflectionResponse(insights=[InsightCandidate(insight='几次交流显示，玩家似乎按当天状态选择饮品。',
            source_memory_ids=context.new_ids,evidence_event_ids=list(dict.fromkeys(e['event_id'] for r in context.records for e in r['evidence'])),
            derivation_type='PATTERN',uncertainty='TENTATIVE')])

    def read(self,context,history=False,observer=None,subject=None):
        return CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.f.database.connect)).retrieve(
            observer=observer or context.owner,subject=subject or context.subject,semantic_query='饮品',
            filters=CanonicalReadFilter(memory_type='DERIVED_INSIGHT',lifecycle='ALL_HISTORY' if history else 'CURRENT'))

    def test_worker_multiepisode_restart_recall_and_embedding(self):
        self.add('今天很累，我选择拿铁提神。',occurred=10)
        self.add('今天需要专注学习，我选择美式咖啡。',episodic=True,occurred=20)
        self.add('今天想休息，我选择清淡的茶，不需要提神。',occurred=30)
        before=self.f.rows('SELECT * FROM memory_records ORDER BY memory_id')
        provider=ReflectionProvider()
        worker=MemoryMaintenanceWorker(self.repo,provider,PrioritizedModelExecutor(),policy=self.maintenance_policy,
                                      reflection_policy=self.policy,clock=lambda:self.now)
        self.assertTrue(asyncio.run(worker.process_once()))  # B1 first, then B2
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(len(provider.calls),2)
        self.assertEqual(before,self.f.rows("SELECT * FROM memory_records WHERE memory_type<>'DERIVED_INSIGHT' ORDER BY memory_id"))
        self.f.database=SQLiteDatabase(self.f.database.database_path)  # new connection owner/restart
        job=self.f.rows("SELECT * FROM memory_maintenance_jobs WHERE task_kind='REFLECTION'")[0]
        owner,subject=self.repo.identities(job)
        result=CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.f.database.connect)).retrieve(observer=owner,
            subject=subject,semantic_query='饮品',filters=CanonicalReadFilter(memory_type='DERIVED_INSIGHT'))
        self.assertEqual(len(result.items),1)
        item=result.items[0]
        self.assertTrue(item.not_direct_fact and item.not_action_authorization and item.not_current_world_state)
        self.assertEqual((item.episode_id,item.job_id,item.provenance_label,item.uncertainty),(None,None,'DERIVED_INSIGHT','TENTATIVE'))
        self.assertEqual(len(item.source_episode_ids),3)
        self.assertEqual(len(item.raw_evidence_refs),3)
        self.assertEqual(len(self.f.rows('SELECT * FROM memory_reflection_items')),3)  # no Insight enqueue
        # Late enrollment of a second direct memory from an already processed
        # Episode must not trigger a paraphrased reflection on the same evidence.
        from app.memory_reflection_repository import enqueue_reflection
        with closing(self.f.database.connect()) as db,db:
            source=db.execute("SELECT * FROM memory_records WHERE memory_type='EPISODIC'").fetchone()
            late=dict(source)
            late['memory_id']='late_direct_memory'
            columns=','.join(late)
            db.execute('INSERT INTO memory_records('+columns+') VALUES('+','.join('?' for _ in late)+')',tuple(late.values()))
            db.execute('INSERT INTO memory_subjects SELECT ?,identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?',(late['memory_id'],source['memory_id']))
            db.execute('INSERT INTO memory_evidence SELECT ?,experience_event_id,experience_seq FROM memory_evidence WHERE memory_id=?',(late['memory_id'],source['memory_id']))
            enqueue_reflection(db,late,self.now)
            self.assertIsNotNone(db.execute('SELECT processed_at FROM memory_reflection_items WHERE memory_id=?',(late['memory_id'],)).fetchone()[0])
        class Embedding:
            model_key='test-b2'
            def embed(self,text):
                return (1,0)
        index=CanonicalEmbeddingIndex(self.f.database.connect)
        self.assertEqual(asyncio.run(CanonicalEmbeddingIndexer(index,Embedding()).index_missing_or_stale(8)),5)
        self.assertEqual(index.statistics('test-b2')['completed'],5)
        row=next(r for r in index.pending('unindexed',8) if r['memory_type']=='DERIVED_INSIGHT')
        self.assertIn('not a direct fact',embedding_text(row))

    def test_insufficient_sources_and_no_insight_ack_are_not_retried(self):
        self.add('这次选择拿铁。',episodic=True)
        self.add('这次选择茶。',episodic=True)
        p=ReflectionProvider({'insights':[]})
        worker=MemoryMaintenanceWorker(self.repo,p,PrioritizedModelExecutor(),reflection_policy=self.policy,clock=lambda:self.now)
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(p.calls,[])
        self.add('这次选择清水。',episodic=True)
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(len(p.calls),1)
        self.assertEqual(self.f.rows('SELECT * FROM memory_derived_insights'),[])
        self.assertTrue(all(r['processed_at'] for r in self.f.rows('SELECT * FROM memory_reflection_items')))
        for bad in ('{}','{"insights":[{}]}','{"insights":[]}extra'):
            with self.assertRaises(MaintenanceOutputError):
                parse_maintenance_response(ModelResult(text=bad),ReflectionResponse)
        with self.assertRaises(MaintenanceOutputError) as captured:
            parse_maintenance_response(ModelResult(text='{}'),ReflectionResponse)
        self.assertEqual(captured.exception.schema_errors,((('insights',),'missing'),))
        from app.memory_reflection_models import reflection_request
        from app.memory_reflection_models import ReflectionInput
        from app.memory_consolidation_context import TrustedPersistentIdentity
        identity=TrustedPersistentIdentity('npc','Character','owner')
        request=reflection_request(ReflectionInput('job',1,identity,identity,(),()))
        self.assertEqual(request.response_schema,ReflectionResponse.model_json_schema())
        self.assertIn('EXACTLY these five required fields',request.messages[0].content)

    def test_admission_revision_scope_and_restart_generation(self):
        self.sample()
        # A known historical phase may support a qualified temporal insight;
        # only a subsequent source change makes that insight stale.
        with closing(self.f.database.connect()) as db,db:
            db.execute("UPDATE memory_records SET status='SUPERSEDED',revision=revision+1 WHERE memory_type='SEMANTIC_FACT' AND occurred_start=10")
        context=self.batch()
        response=self.response(context)
        op=response.insights[0]
        for bad in (op.model_copy(update={'evidence_event_ids':('foreign','missing')}),
                    op.model_copy(update={'source_memory_ids':('foreign',context.new_ids[0])})):
            with self.assertRaises(MaintenanceAdmissionError):
                self.reflection.finalize(context,ReflectionResponse(insights=[bad]),self.now)
            self.assertEqual(self.f.rows('SELECT * FROM memory_derived_insights'),[])
        foreign=self.add('别的玩家今天选择茶。',uid='U20002',episodic=True)
        from app.memory_consolidation_context import TrustedPersistentIdentity
        foreign_subject=TrustedPersistentIdentity('player','Player','U20002')
        with closing(self.f.database.connect()) as db:
            with self.assertRaises(MaintenanceAdmissionError):
                self.reflection.direct(db,foreign,context.owner,context.subject)
        with closing(self.f.database.connect()) as db,db:
            db.execute('UPDATE memory_records SET revision=revision+1 WHERE memory_id=?',(context.new_ids[0],))
        with self.assertRaises(MaintenanceStale):
            self.reflection.finalize(context,response,self.now)
        # A stale running attempt is reclaimed with a new generation on restart.
        job=self.reflection.claim(self.now+181,self.policy,180)
        self.assertEqual(job['attempt_generation'],context.generation+1)
        fresh=self.reflection.prepare(job,self.policy)
        self.assertTrue(self.reflection.finalize(fresh,self.response(fresh),self.now+181))
        self.assertFalse(self.reflection.finalize(fresh,self.response(fresh),self.now+181))
        self.assertEqual(len(self.read(fresh).items),1)
        self.assertEqual(self.read(fresh,subject=foreign_subject).items,())
        self.assertEqual(self.read(fresh,observer=TrustedPersistentIdentity('npc','Character','other')).items,())
        with closing(self.f.database.connect()) as db,db:
            db.execute("UPDATE memory_records SET status='SUPERSEDED',revision=revision+1 WHERE memory_id=?",(fresh.new_ids[0],))
        self.assertEqual(self.read(fresh).items,())
        self.assertTrue(self.read(fresh,history=True).items[0].derived_sources_stale)
        run=self.f.rows('SELECT run_id FROM memory_derived_insights')[0]['run_id']
        self.assertTrue(self.repo.undo_run(run,self.now+200))
        self.assertEqual(self.read(fresh).items,())
        self.assertEqual(self.read(fresh,history=True).items[0].lifecycle_status,'SUPERSEDED')

    def test_populated_schema17_upgrades_without_deleting_sources(self):
        # Bootstrap the genuine frozen Schema 17, rather than editing a marker on
        # a Schema 18 database with different parent constraints.
        self.f.doCleanups()
        with patch('app.sqlite_database.migrate_memory_reflection',lambda db:None):
            self.f=c2a.MemoryC2ATests(methodName='runTest')
            self.f.setUp()
            self.addCleanup(self.f.doCleanups)
            self.add('已保存的开放式事实。')
            before=self.f.rows('SELECT * FROM memory_records')
            evidence=self.f.rows('SELECT * FROM memory_evidence')
        self.f.database=SQLiteDatabase(self.f.database.database_path)
        self.assertEqual(before,self.f.rows('SELECT * FROM memory_records'))
        self.assertEqual(evidence,self.f.rows('SELECT * FROM memory_evidence'))
        self.assertEqual(self.f.rows('SELECT MAX(version) AS v FROM schema_migrations')[0]['v'],18)
        with closing(self.f.database.connect()) as db:
            self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(),[])
            self.assertTrue(db.execute('PRAGMA foreign_keys').fetchone()[0])

    def test_npc_self_statements_can_reflect_without_becoming_player_history(self):
        from tests import test_dsn_singleplayer as dsn
        f=dsn.DsnFixture(methodName='runTest');f.setUp();self.addCleanup(f.doCleanups)
        for text in ('我喜欢温热的拿铁。','我喝咖啡通常不加糖。','我觉得少糖咖啡更能保留咖啡香味。'):
            e=f.append('dialogue_response',text)
            job,context=f.close([e]);f.finalize(job,context,f.proposal(context,[e],profile=True))
        repo=MemoryMaintenanceRepository(f.database.connect,expected_player_uid=dsn.UID)
        while (job:=repo.claim(self.now,self.maintenance_policy)) is not None:
            repo.finalize(repo.prepare(job,self.maintenance_policy),MaintenanceResponse(operations=()),self.now)
        tasks=ReflectionTasks(repo);job=tasks.claim(self.now,self.policy,180)
        context=tasks.prepare(job,self.policy)
        self.assertEqual(context.subject,dsn.OWNER)
        response=self.response(context)
        response=ReflectionResponse(insights=[response.insights[0].model_copy(update={'insight':'NPC多次自述偏向温热、少糖的咖啡，尚未独立证实。'})])
        self.assertTrue(tasks.finalize(context,response,self.now))
        out=CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(f.database.connect,expected_player_uid=dsn.UID))
        self.assertEqual(out.retrieve(observer=dsn.OWNER,subject=dsn.PLAYER,semantic_query='咖啡',filters=CanonicalReadFilter(memory_type='DERIVED_INSIGHT')).items,())
        own=out.retrieve(observer=dsn.OWNER,subject=dsn.OWNER,semantic_query='咖啡',filters=CanonicalReadFilter(memory_type='DERIVED_INSIGHT'))
        self.assertEqual(len(own.items),1)
        self.assertTrue(own.items[0].not_direct_fact)
