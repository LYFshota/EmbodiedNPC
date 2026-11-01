"""Measured concurrency, real repository ownership, and shutdown regression tests."""
import asyncio
from contextlib import closing
from dataclasses import replace
import json
import threading
import time
import unittest
from unittest.mock import patch
from pydantic import ValidationError
from app.local_model_scheduler import (PrioritizedModelExecutor, PreemptedByForeground,
    ModelQueueFull, ModelExecutorClosed)
from app.worker_runtime import store_call
from app.model_api_config import MainModelConfig
from app.model_config_runtime import restart_required
from app.providers import ModelRequest, ModelMessage, ModelResult
from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.base_memory_consolidator import BaseMemoryConsolidator
from app.memory_consolidation_worker import MemoryConsolidationWorker
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.memory_maintenance_worker import MemoryMaintenanceWorker
from app.memory_maintenance_repository import MemoryMaintenanceRepository
from app.memory_reflection_repository import ReflectionTasks
from app.memory_maintenance_models import MaintenancePolicy
from app.memory_reflection_models import ReflectionPolicy
from app.relationship_reflection_worker import RelationshipReflectionWorker
from app.relationship_reflection_model import RelationshipReflectionModel
from app.relationship_reflection_scanner import RelationshipReflectionScanner
from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from app.relationship_reflection_repository import RelationshipReflectionRepository
from tests.test_cognitive_request import _event_request
from tests import test_memory_c2b as c2b, test_memory_c2a as c2a


def request(label):
    return ModelRequest(messages=(ModelMessage(role="user",content=label),))

async def until(predicate, timeout=3):
    async with asyncio.timeout(timeout):
        while not predicate(): await asyncio.sleep(.002)

class Meter:
    name="fake"
    model="concurrency-test"
    context_length=32768
    def __init__(self, delay=.03, output=None):
        self.delay,self.output=delay,output
        self.active=self.peak=self.calls=self.cancelled=0
        self.started=[]; self.ended=[]; self.release=None; self.gate=None
        self.by_class={"foreground":0,"background":0}
        self.peaks=dict(self.by_class)
    async def generate(self, req):
        index=self.calls;self.calls+=1
        label=req.messages[-1].content
        kind="background" if label.startswith("background") else "foreground"
        self.active+=1;self.peak=max(self.peak,self.active)
        self.by_class[kind]+=1;self.peaks[kind]=max(self.peaks[kind],self.by_class[kind])
        self.started.append((index,label,time.monotonic()))
        try:
            if self.gate is not None: await self.gate(index,req)
            if self.release is not None: await self.release.wait()
            delay=self.delay[index] if isinstance(self.delay,list) else self.delay
            await asyncio.sleep(delay)
            if isinstance(self.output,Exception): raise self.output
            if self.output is not None: return ModelResult(text=self.output)
            schema=req.response_schema or {}
            props=schema.get("properties",{})
            if "decisions" in props:
                payload=json.loads(label.split("\n",1)[1])
                text=json.dumps({"decisions":[dict(pair_ref=p["pair_ref"],relation="NO_RELATION",direction="LEFT_TO_RIGHT",reason="independent") for p in payload["pairs"]]})
            elif "insights" in props: text='{"insights":[]}'
            elif "episodic_memories" in props: text=c2b.EMPTY
            elif "interpretation" in props:
                text='{"outcome":"NO_CHANGE","interpretation":null,"state_update":null}'
            else: text='{"decision_kind":"NO_ACTION"}'
            return ModelResult(text=text)
        except asyncio.CancelledError:
            self.cancelled+=1
            raise
        finally:
            self.active-=1;self.by_class[kind]-=1
            self.ended.append((index,time.monotonic()))

class ExecutorConcurrencyTests(unittest.IsolatedAsyncioTestCase):
    async def test_four_cognitive_runs_overlap_and_fast_returns_independently(self):
        p=Meter(delay=[.15,.03,.05,.07]);e=PrioritizedModelExecutor(6,6,2,preempt_background=False)
        slow=asyncio.Event()
        async def gate(index,req):
            if index==0: await slow.wait()
        p.gate=gate
        tasks=[];start=time.monotonic()
        for i in range(4):
            data=_event_request()
            for obj in (data['run'],data['trigger'],data['context'],data['context']['trigger']):obj['agent_entity_id']=f'npc_{i}'
            data['context']['perception']['observer_entity_id']=f'npc_{i}'
            data['recall_identity']['observer']['entity_id']=f'npc_{i}'
            data['recall_identity']['observer']['persistent_id']=f'character_{i}'
            tasks.append(asyncio.create_task(CognitiveBackendEntry(p,e,context_length=32768).run(CognitiveRequest.model_validate(data))))
        done,pending=await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
        self.assertEqual(p.peak,4);self.assertFalse(tasks[0].done());self.assertTrue(pending)
        slow.set();await asyncio.gather(*tasks)
        print("MEASURE cognition",json.dumps(dict(peak=p.peak,elapsed=round(time.monotonic()-start,3),finish_order=[x[0] for x in p.ended])))

    async def test_shared_and_class_limits_and_configurable_parallelism(self):
        for limits in ((1,1,1),(3,2,2),(6,6,2)):
            e=PrioritizedModelExecutor(*limits,preempt_background=False);p=Meter();start=time.monotonic()
            await asyncio.gather(*(e.execute(p,request(f"{kind}-{i}"),priority=kind)
                for i in range(8) for kind in ("foreground","background")))
            self.assertLessEqual(p.peak,limits[0]);self.assertLessEqual(p.peaks["foreground"],limits[1]);self.assertLessEqual(p.peaks["background"],limits[2])
            self.assertEqual(p.peak,limits[0]);self.assertEqual(p.active,0)
            print("MEASURE limits",limits,"peak",p.peak,p.peaks,"elapsed",round(time.monotonic()-start,3))

    async def test_cloud_background_is_not_preempted_and_foreground_has_free_capacity(self):
        e=PrioritizedModelExecutor(6,6,2,preempt_background=False);p=Meter(.08)
        bg=[asyncio.create_task(e.execute_preemptible_background(p,request("background"))) for _ in range(2)]
        await until(lambda:p.active==2);start=time.monotonic()
        await e.execute(p,request("foreground"),priority="foreground")
        await asyncio.gather(*bg)
        self.assertEqual(p.peak,3);self.assertEqual(p.cancelled,0)
        print("MEASURE foreground_with_background",round(time.monotonic()-start,3),"peak",p.peak)

    async def test_burst_fairness_under_foreground_backlog(self):
        e=PrioritizedModelExecutor(1,1,1,preempt_background=False);p=Meter(.004)
        initial=asyncio.create_task(e.execute(p,request("initial"),priority="foreground"))
        await until(lambda:p.calls==1)
        fg=[asyncio.create_task(e.execute(p,request(f"fg-{i}"),priority="foreground")) for i in range(16)]
        bg=asyncio.create_task(e.execute_preemptible_background(p,request("background")))
        await asyncio.gather(initial,*fg,bg)
        order=[x[1] for x in p.started];self.assertLessEqual(order.index("background"),4)
        print("MEASURE fairness",order)

    async def test_local_preemption_then_protected_background_progress(self):
        e=PrioritizedModelExecutor();p=Meter(.04)
        bg=asyncio.create_task(e.execute_preemptible_background(p,request("background-old")))
        await until(lambda:p.calls==1)
        fg=asyncio.create_task(e.execute(p,request("fg"),priority="foreground"))
        with self.assertRaises(PreemptedByForeground):await bg
        retry=asyncio.create_task(e.execute_preemptible_background(p,request("background-retry")))
        await fg;await until(lambda:p.calls==3)
        later=asyncio.create_task(e.execute(p,request("fg-later"),priority="foreground"))
        await asyncio.gather(retry,later)
        self.assertEqual(p.cancelled,1);self.assertEqual(p.peak,1)

    async def test_queue_full_timeout_cancel_failure_and_slot_release(self):
        e=PrioritizedModelExecutor(queue_capacity=1,queue_timeout=.025,execution_timeout=1)
        p=Meter(.15)
        first=asyncio.create_task(e.execute(p,request("first"),priority="foreground"))
        await until(lambda:p.active==1)
        queued=asyncio.create_task(e.execute(p,request("queued"),priority="foreground"))
        await asyncio.sleep(.002)
        with self.assertRaises(ModelQueueFull):await e.execute(p,request("full"),priority="foreground")
        with self.assertRaises(TimeoutError):await queued
        first.cancel()
        with self.assertRaises(asyncio.CancelledError):await first
        p.delay=0;p.output=RuntimeError("fake failure")
        with self.assertRaises(RuntimeError):await e.execute(p,request("failure"),priority="foreground")
        p.output="ok"
        self.assertEqual((await e.execute(p,request("ok"),priority="foreground")).text,"ok")
        self.assertEqual(p.active,0)

    async def test_execution_deadline_and_close_drains_issued_request(self):
        e=PrioritizedModelExecutor(execution_timeout=.02);p=Meter(.08)
        with self.assertRaises(TimeoutError):await e.execute(p,request("timeout"),priority="foreground")
        e.execution_timeout=1
        running=asyncio.create_task(e.execute(p,request("running"),priority="foreground"))
        await until(lambda:p.active==1)
        queued=asyncio.create_task(e.execute(p,request("queued"),priority="foreground"))
        await asyncio.sleep(.002);closing=asyncio.create_task(e.close())
        with self.assertRaises(ModelExecutorClosed):await queued
        self.assertFalse(closing.done());await asyncio.gather(running,closing)
        self.assertEqual(p.active,0);e.open()

    async def test_store_cancellation_waits_for_native_operation(self):
        entered,release,finished=(threading.Event() for _ in range(3))
        def write():entered.set();release.wait(2);finished.set()
        task=asyncio.create_task(store_call(write));await until(entered.is_set);task.cancel();await asyncio.sleep(.01)
        self.assertFalse(task.done());release.set()
        with self.assertRaises(asyncio.CancelledError):await task
        self.assertTrue(finished.is_set())

    async def test_cancelled_queue_waiter_is_removed(self):
        p=Meter(.04);e=PrioritizedModelExecutor(queue_capacity=1)
        first=asyncio.create_task(e.execute(p,request("first"),priority="foreground"))
        await until(lambda:p.calls==1)
        queued=asyncio.create_task(e.execute(p,request("cancel"),priority="foreground"))
        await asyncio.sleep(.002);queued.cancel()
        with self.assertRaises(asyncio.CancelledError):await queued
        next_call=asyncio.create_task(e.execute(p,request("next"),priority="foreground"))
        await asyncio.gather(first,next_call);self.assertEqual(p.calls,2)

    async def test_full_background_wait_queue_does_not_waste_idle_foreground_capacity(self):
        e=PrioritizedModelExecutor(2,2,1,preempt_background=False,queue_capacity=1)
        p=Meter(.03);p.release=asyncio.Event()
        first=asyncio.create_task(e.execute_preemptible_background(p,request("background-first")))
        await until(lambda:p.calls==1)
        waiting=asyncio.create_task(e.execute_preemptible_background(p,request("background-waiting")))
        await asyncio.sleep(.005)
        foreground=asyncio.create_task(e.execute(p,request("foreground"),priority="foreground"))
        await until(lambda:p.active==2);p.release.set()
        await asyncio.gather(first,waiting,foreground);self.assertEqual(p.peak,2)

    async def test_idle_worker_lanes_wake_on_completion_without_thirty_second_delay(self):
        from app.worker_runtime import run_pool
        queue=["first"];stop=asyncio.Event();release=asyncio.Event();active=peak=done=0
        async def process():
            nonlocal active,peak,done
            if not queue:return False
            label=queue.pop(0);active+=1;peak=max(peak,active)
            try:
                if label=="first":await release.wait()
                else:await asyncio.sleep(.03)
                done+=1
                return True
            finally:active-=1
        worker=asyncio.create_task(run_pool(process,stop,concurrency=2,interval=30,on_error=lambda:None))
        await until(lambda:active==1);await asyncio.sleep(.01)
        queue.extend(("second","third"));start=time.monotonic();release.set()
        await until(lambda:done==3,timeout=1);stop.set();await worker
        self.assertEqual(peak,2);print("MEASURE refill",round(time.monotonic()-start,3),"peak",peak)

class ConcurrencyConfigTests(unittest.TestCase):
    def config(self,kind="openai_compatible",**kwargs):
        return MainModelConfig(display_name="test",provider_type=kind,base_url="https://fake.invalid",model="fake",**kwargs)
    def test_defaults_and_validation(self):
        self.assertEqual(self.config().concurrency_limits(),(6,6,2))
        self.assertEqual(self.config("ollama").concurrency_limits(),(1,1,1))
        self.assertEqual(self.config("ollama",model_total_concurrency=4).concurrency_limits(),(4,4,1))
        for values in (dict(model_total_concurrency=0),dict(model_total_concurrency=33),dict(model_total_concurrency=True),dict(model_total_concurrency=2,model_foreground_concurrency=3)):
            with self.assertRaises(ValidationError):self.config(**values)

    def test_saved_profile_limits_require_restart_and_survive_activation(self):
        import tempfile
        from pathlib import Path
        from tests.test_model_config_restart import saved_document
        with tempfile.TemporaryDirectory() as directory:
            store, document = saved_document(Path(directory))
            changed_main = document.main_model.model_copy(update={"model_total_concurrency":8,
                "model_foreground_concurrency":5,"model_background_concurrency":3})
            from app.model_api_config import ModelApiConfig
            saved = store.save(ModelApiConfig(main_model=changed_main,embedding=document.embedding),expected_revision=document.revision)
            self.assertEqual(store.load().main_model.concurrency_limits(),(8,5,3))
            self.assertTrue(restart_required(document,saved))

    def test_backend_startup_uses_saved_limits_for_executor_and_all_workers(self):
        import os,subprocess,sys,tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            env={k:v for k,v in os.environ.items() if not k.startswith(("EMBODIEDNPC_","CANONICAL_EMBEDDING_","OLLAMA_","DEEPSEEK_","OPENAI_","ANTHROPIC_","GEMINI_","GOOGLE_"))}
            names=("memory.sqlite3","settings.json","models.json","knowledge.json","knowledge.sqlite3","session.json")
            keys=("EMBODIEDNPC_MEMORY_DB","EMBODIEDNPC_SETTINGS_FILE","EMBODIEDNPC_MODEL_CONFIG_FILE","EMBODIEDNPC_KNOWLEDGE_FILE","EMBODIEDNPC_KNOWLEDGE_INDEX_DB","EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE")
            env.update({k:str(root/n) for k,n in zip(keys,names)})
            env.update(EMBODIEDNPC_PROVIDER="mock",CANONICAL_EMBEDDING_ENABLED="false",PYTHONDONTWRITEBYTECODE="1")
            code = """
import asyncio,os
from pathlib import Path
from app.model_api_config import MainModelConfig,EmbeddingConfig,ModelApiConfig
from app.model_api_config_store import ModelApiConfigStore
store=ModelApiConfigStore(Path(os.environ['EMBODIEDNPC_MODEL_CONFIG_FILE']))
store.save(ModelApiConfig(main_model=MainModelConfig(display_name='isolated',provider_type='openai_compatible',
 base_url='https://never-called.invalid',model='fake',model_total_concurrency=8,
 model_foreground_concurrency=5,model_background_concurrency=3),embedding=EmbeddingConfig(enabled=False)),expected_revision=0)
from app import m3_main as m
assert m.model_limits==(8,5,3)
assert (m.model_executor.total_limit,m.model_executor.foreground_limit,m.model_executor.background_limit)==(8,5,3)
workers=(m.memory_consolidation_worker,m.relationship_reflection_worker,m.memory_maintenance_worker)
assert all(w.concurrency==3 for w in workers)
async def check():
 async with m.lifespan(m.app):
  await asyncio.sleep(.03)
 assert all(w._task is None for w in workers)
asyncio.run(check())
print('isolated-start-stop-ok limits=8/5/3 workers=3')
"""
            result=subprocess.run([sys.executable,"-B","-c",code],cwd=Path(__file__).resolve().parents[1],
                env=env,capture_output=True,text=True,timeout=30)
            self.assertEqual(result.returncode,0,result.stderr[-2000:])
            self.assertIn("isolated-start-stop-ok",result.stdout)

class WorkerConcurrencyTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.f=c2b.MemoryC2BTests(methodName="runTest");self.f.setUp();self.addCleanup(self.f.doCleanups)
    def worker(self,p,**options):
        e=PrioritizedModelExecutor(6,6,2,preempt_background=False)
        return MemoryConsolidationWorker(self.f.jobs,self.f.resolver,BaseMemoryConsolidator(p,e),
            self.f.worker.admission,self.f.store,job_policy=self.f.policy,clock=lambda:self.f.now,
            concurrency=2,interval_seconds=.01,**options)
    async def test_episode_overlap_out_of_order_watermark_and_no_duplicate(self):
        first,second=self.f.pending(),self.f.pending();p=Meter(.01);w=self.worker(p)
        release=asyncio.Event()
        async def gate(index,req):
            if first.episode_id in req.messages[-1].content: await release.wait()
            else: await until(lambda:p.calls>=2)
        p.gate=gate
        w.start();await until(lambda:self.f.jobs.get(second.job_id).status=="SUCCEEDED")
        self.assertEqual(self.f.jobs.get(first.job_id).status,"RUNNING")
        self.assertEqual(self.f.state.get("npc_01",V2),0)
        release.set();await w.stop();self.assertEqual(self.f.state.get("npc_01",V2),2)
        self.assertEqual(p.peak,2);self.assertEqual(p.calls,2)
        self.assertFalse(await w.process_once());print("MEASURE consolidation",p.peak,[i for i,t in p.ended])
    async def test_active_attempt_survives_recovery_and_orphan_is_recovered(self):
        job=self.f.pending();p=Meter(.08);w=self.worker(p);w.start()
        await until(lambda:p.calls==1)
        self.assertEqual(self.f.jobs.recover_stale(self.f.now+100,self.f.policy),())
        await w.stop();self.assertEqual(self.f.jobs.get(job.job_id).status,"SUCCEEDED")
        orphan=self.f.pending();running=self.f.jobs.claim_next_for_version(self.f.now,V2)
        restarted=MemoryJobRepository(self.f.database.connect)
        self.assertEqual(len(restarted.recover_stale(self.f.now+100,self.f.policy)),1)
        self.assertEqual(restarted.get(orphan.job_id).status,"RETRY")
    async def test_attempt_deadline_requeues_and_releases_active_ownership(self):
        job=self.f.pending();p=Meter(.2);w=self.worker(p,attempt_timeout=.03)
        await w.process_once();state=self.f.jobs.get(job.job_id)
        self.assertEqual(state.status,"RETRY");self.assertEqual(p.active,0)
        self.assertFalse(self.f.jobs.active_attempts.contains(job.job_id,state.attempt_generation))
    async def test_cancelled_owner_waits_for_finalization_thread(self):
        job=self.f.pending();p=Meter(0);w=self.worker(p)
        entered,release=(threading.Event() for _ in range(2));original=self.f.store.finalize_success
        def finalize(**kwargs):entered.set();release.wait(2);return original(**kwargs)
        with patch.object(self.f.store,"finalize_success",side_effect=finalize):
            work=asyncio.create_task(w.process_once());await until(entered.is_set);work.cancel();await asyncio.sleep(.01)
            self.assertFalse(work.done());self.assertEqual(self.f.jobs.recover_stale(self.f.now+100,self.f.policy),())
            release.set()
            with self.assertRaises(asyncio.CancelledError):await work
        self.assertEqual(self.f.jobs.get(job.job_id).status,"SUCCEEDED")

class DerivedWorkerConcurrencyTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.f=c2a.MemoryC2ATests(methodName="runTest");self.f.setUp();self.addCleanup(self.f.doCleanups)
        self.now=time.time()+1000
        self.repo=MemoryMaintenanceRepository(self.f.database.connect)
    def add(self,owner="merchant.001",text="I like blue."):
        job,context=self.f.work(owner="owner.test."+owner,observer="npc_"+owner.replace(".","_"),text=text)
        response=self.f.response(context);response["semantic_facts"][0].update(fact=text,source_excerpt=text)
        self.f.finalize(job,context,response)
    def seed_reflection(self):
        from app.memory_reflection_repository import enqueue_reflection
        with closing(self.f.database.connect()) as db, db:
            for row in db.execute("SELECT * FROM memory_records").fetchall():
                enqueue_reflection(db,dict(row),self.now)
    def executor(self):return PrioritizedModelExecutor(6,6,2,preempt_background=False)
    def maintenance(self,p):
        return MemoryMaintenanceWorker(self.repo,p,self.executor(),policy=MaintenancePolicy(backlog_threshold=1,max_wait_seconds=0),
            reflection_policy=ReflectionPolicy(min_episodes=2,min_wait_seconds=0),concurrency=2,clock=lambda:self.now,interval_seconds=1)
    async def test_maintenance_scopes_overlap_and_reflection_does_not_starve(self):
        for owner in ("a","b","c"):
            self.add(owner,"I like blue.");self.add(owner,"I also like green.")
        self.seed_reflection()
        p=Meter(.08);w=self.maintenance(p)
        w.start()
        await until(lambda:len(p.ended)>=6)
        await w.stop()
        self.assertEqual(p.peak,2)
        schemas=[r for r in self.f.rows("SELECT j.task_kind,COUNT(*) n FROM memory_maintenance_runs r JOIN memory_maintenance_jobs j USING(job_id) GROUP BY j.task_kind")]
        self.assertEqual({r["task_kind"] for r in schemas},{"MAINTENANCE","REFLECTION"})
        print("MEASURE maintenance",p.peak,"calls",p.calls,"kinds",schemas)
    async def test_same_scope_claim_serializes_both_kinds_and_stale_protection(self):
        self.add();self.add(text="I also like green.")
        self.seed_reflection()
        policy=MaintenancePolicy(backlog_threshold=1,max_wait_seconds=0);reflection=ReflectionTasks(self.repo)
        job=self.repo.claim(self.now,policy,activate=True);self.assertIsNotNone(job)
        self.assertIsNone(reflection.claim(self.now+1000,ReflectionPolicy(min_episodes=2,min_wait_seconds=0),180,activate=True))
        self.assertIsNone(self.repo.claim(self.now+1000,policy,activate=True))
        self.assertTrue(self.repo.active_attempts.contains(job["job_id"],job["attempt_generation"]))
        self.repo.requeue(job["job_id"],job["attempt_generation"],self.now,"TEST");self.repo.active_attempts.discard(job)
        other=reflection.claim(self.now,ReflectionPolicy(min_episodes=2,min_wait_seconds=0),180,activate=True)
        self.assertIsNotNone(other);self.repo.active_attempts.discard(other)
    async def test_maintenance_orphan_recovers_but_live_attempt_does_not(self):
        self.add(text="I like blue.")
        policy=MaintenancePolicy(backlog_threshold=1,max_wait_seconds=0)
        live=self.repo.claim(self.now,policy,activate=True)
        self.assertIsNone(self.repo.claim(self.now+1000,policy,activate=True))
        self.assertEqual(self.f.rows("SELECT attempt_generation FROM memory_maintenance_jobs")[0]["attempt_generation"],1)
        self.repo.active_attempts.discard(live)
        recovered=MemoryMaintenanceRepository(self.f.database.connect).claim(self.now+1000,policy,activate=True)
        self.assertEqual(recovered['job_id'],live['job_id']);self.assertEqual(recovered['attempt_generation'],2)

    async def test_maintenance_stop_waits_for_native_commit_and_does_not_claim_more(self):
        self.add(text="I like blue.");self.add(text="I like green.")
        p=Meter(0);w=self.maintenance(p)
        entered,release=(threading.Event() for _ in range(2));original=self.repo.finalize
        def finalize(*args):entered.set();release.wait(2);return original(*args)
        with patch.object(self.repo,"finalize",side_effect=finalize):
            w.start();await until(entered.is_set);stop=asyncio.create_task(w.stop());await asyncio.sleep(.01)
            self.assertFalse(stop.done());release.set();await stop
        self.assertIsNone(w._task);self.assertEqual(p.active,0)
        self.assertFalse(self.repo.active_attempts._attempts)

    async def test_all_workers_share_background_cap_and_foreground_can_enter(self):
        for owner in ("merchant_a","merchant_b"):
            self.add(owner);self.add(owner,text="I also like green.")
        pending,context=self.f.work(owner="owner.pending",observer="npc_pending",text="I like red.")
        self.f.jobs.requeue_cancelled(pending.job_id,pending.attempt_generation,self.now)
        e=self.executor();p=Meter(.04);p.release=asyncio.Event()
        maintenance=self.maintenance(p);maintenance.executor=e
        repo=RelationshipReflectionRepository(self.f.database.connect)
        relationship=RelationshipReflectionWorker(RelationshipReflectionScanner(self.f.jobs,self.f.resolver,
            RelationshipReflectionCandidateBuilder(),repo),RelationshipReflectionModel(p,e,context_length=32768),
            repo,concurrency=2,interval_seconds=.01)
        consolidation=MemoryConsolidationWorker(self.f.jobs,self.f.resolver,BaseMemoryConsolidator(p,e),
            self.f.admission,self.f.store,concurrency=2,interval_seconds=.01,clock=lambda:self.now)
        workers=(maintenance,relationship,consolidation)
        for worker in workers:worker.start()
        try:
            await until(lambda:p.active==2)
            await asyncio.sleep(.02);self.assertEqual(p.peak,2)
            foreground=asyncio.create_task(e.execute(p,request("foreground"),priority="foreground"))
            await until(lambda:p.active==3);p.release.set();await foreground
            for worker in workers:worker.request_stop()
            await asyncio.gather(*(worker.stop() for worker in workers))
        finally:
            p.release.set()
            for worker in workers:worker.request_stop()
            await asyncio.gather(*(worker.stop() for worker in workers))
        self.assertEqual(p.peak,3);self.assertEqual(p.active,0);self.assertEqual(p.cancelled,0)
        self.assertFalse(self.repo.active_attempts._attempts);self.assertFalse(self.f.jobs.active_attempts._attempts)
        print("MEASURE combined workers foreground",p.peak,"calls",p.calls)

    async def test_relationship_different_pairs_overlap_same_pair_runs_sequentially(self):
        self.add("a");self.add("a",text="I also like green.");self.add("b")
        repo=RelationshipReflectionRepository(self.f.database.connect)
        scanner=RelationshipReflectionScanner(self.f.jobs,self.f.resolver,RelationshipReflectionCandidateBuilder(),repo)
        p=Meter(.03);w=RelationshipReflectionWorker(scanner,RelationshipReflectionModel(p,self.executor(),context_length=32768),repo,concurrency=2,interval_seconds=.01)
        active=set();overlap=[];original=w._execute_candidate
        async def check(candidate):
            key=w._pair_key(candidate);self.assertNotIn(key,active);active.add(key);overlap.append(len(active))
            try:return await original(candidate)
            finally:active.remove(key)
        with patch.object(w,"_execute_candidate",side_effect=check):
            w.start();await until(lambda:len(p.ended)>=3);await w.stop()
        self.assertEqual(p.calls,3);self.assertEqual(p.peak,2);self.assertEqual(max(overlap),2)
        self.assertEqual(len(self.f.rows("SELECT * FROM relationship_reflections")),3)
        print("MEASURE relationship",p.peak,"calls",p.calls)
