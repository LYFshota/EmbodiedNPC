import asyncio
import tempfile
import threading
import unittest
import httpx
from pathlib import Path
from unittest.mock import patch

from app.canonical_embeddings import CanonicalEmbeddingIndex
from app.embedding_index_control import EmbeddingControl, ManagedEmbeddingIndexer
from app.embedding_index_control import create_embedding_index_router
from app.model_profile_api import activate_profile, save_profile, SaveProfileCommand, ActivateProfileCommand
from app.model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from app.model_api_config import ModelApiConfig, MainModelConfig, EmbeddingConfig
from fastapi import FastAPI
from fastapi.testclient import TestClient
from tests import test_memory_d1a_retrieval as d1a


class FakeProvider:
    model_key="space_v2:synthetic"
    model="synthetic-embedding"
    dimensions=2
    def __init__(self):
        self.calls=[]
        self.fail=False
        self.started=threading.Event()
        self.release=threading.Event()
        self.release.set()
    def embed(self,text):
        self.calls.append(text)
        self.started.set()
        if not self.release.wait(5):
            raise ValueError("timeout")
        if self.fail:
            raise ValueError("synthetic 429")
        return (1,0)


class EmbeddingControlTests(unittest.TestCase):
    def setUp(self):
        self.fixture=d1a.CanonicalRetrievalTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.temp=tempfile.TemporaryDirectory(prefix="model-v2-control-")
        self.addCleanup(self.temp.cleanup)
        self.path=Path(self.temp.name)/"control.json"
        self.index=CanonicalEmbeddingIndex(self.fixture.fixture.database.connect)
        self.inner=FakeProvider()
        self.gate=EmbeddingControl(self.inner,self.path,cloud=True)
        self.worker=ManagedEmbeddingIndexer(self.index,self.gate,interval_seconds=1)

    def test_waiting_consent_pause_resume_and_restart(self):
        self.fixture.add()
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),0)
        self.assertEqual(self.inner.calls,[])
        with self.assertRaises(ValueError): self.gate.command("start")
        self.gate.command("start",True)
        self.gate.command("start")
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),1)
        self.assertEqual(self.index.statistics(self.gate.model_key)["completed"],1)
        self.gate.command("pause")
        self.gate.command("pause")
        self.fixture.add("Synthetic new memory while paused")
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),0)
        with self.assertRaises(ValueError): self.gate.embed("query")
        restored=EmbeddingControl(self.inner,self.path,cloud=True)
        self.assertEqual(restored.mode,"paused")
        restored.command("resume")
        self.assertEqual(asyncio.run(ManagedEmbeddingIndexer(self.index,restored).index_missing_or_stale()),1)
        self.assertEqual(len(self.inner.calls),2)
        self.assertEqual(EmbeddingControl(self.inner,self.path,cloud=True).mode,"running")

    def test_pause_in_flight_settles_one_and_stops_next(self):
        self.fixture.add("First synthetic memory")
        self.fixture.add("Second synthetic memory")
        self.inner.release.clear()
        self.gate.command("start",True)
        async def run():
            task=asyncio.create_task(self.worker.index_missing_or_stale())
            self.assertTrue(await asyncio.to_thread(self.inner.started.wait,3))
            self.gate.command("pause")
            self.inner.release.set()
            self.assertEqual(await task,1)
        asyncio.run(run())
        self.assertEqual(len(self.inner.calls),1)
        self.assertEqual(self.index.statistics(self.gate.model_key)["pending"],1)
        self.assertEqual(self.gate.mode,"paused")

    def test_failure_persists_stops_retries_and_does_not_count_success(self):
        self.fixture.add()
        self.inner.fail=True
        self.gate.command("start",True)
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),0)
        self.assertEqual(self.gate.mode,"failed")
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),0)
        self.assertEqual(len(self.inner.calls),1)
        self.assertEqual(self.index.statistics(self.gate.model_key)["completed"],0)
        self.assertEqual(EmbeddingControl(self.inner,self.path,cloud=True).mode,"failed")
        self.inner.fail=False
        self.gate.command("resume")
        self.assertEqual(asyncio.run(self.worker.index_missing_or_stale()),1)

    def test_corrupt_control_never_overwritten_or_starts_requests(self):
        self.path.write_text('{"schema_version":999}',encoding="utf-8")
        gate=EmbeddingControl(self.inner,self.path,cloud=True)
        with self.assertRaises(ValueError): gate.command("resume",True)
        self.assertEqual(self.path.read_text(),'{"schema_version":999}')
        self.assertEqual(self.inner.calls,[])

    def test_corrupt_control_status_blocks_false_recovery_and_explains_rejection(self):
        self.path.write_text('{"schema_version":999}', encoding="utf-8")
        gate=EmbeddingControl(self.inner,self.path,cloud=True)
        worker=ManagedEmbeddingIndexer(self.index,gate)
        store=ModelApiConfigStore(Path(self.temp.name)/"models.json")
        active=store.save(ModelApiConfig(main_model=MainModelConfig(display_name="Synthetic",provider_type="mock",model="synthetic"),
            embedding=EmbeddingConfig(enabled=True,provider_type="openai_compatible",base_url="https://fixture.invalid/v1",model="synthetic-embedding")),expected_revision=0)
        app=FastAPI(); app.include_router(create_embedding_index_router(store,active,worker))
        with TestClient(app) as client:
            status=client.get("/v1/embedding-index/status").json()
            self.assertIs(status.get("control_allowed"),False)
            self.assertEqual(status["error_code"],"EMBEDDING_CONTROL_STATE_INVALID")
            response=client.post("/v1/embedding-index/control",json=dict(action="resume",model_key=gate.model_key,confirm_cloud_transfer=True))
            self.assertEqual(response.json()["error"]["code"],"EMBEDDING_CONTROL_STATE_INVALID")
        self.assertEqual(self.path.read_text(),'{"schema_version":999}')
        self.assertEqual(self.inner.calls,[])

    def test_new_space_cannot_reuse_old_vectors(self):
        self.fixture.add()
        self.gate.command("start",True)
        asyncio.run(self.worker.index_missing_or_stale())
        self.inner.model_key="space_v2:other"
        other=EmbeddingControl(self.inner,self.path,cloud=True)
        self.assertEqual(other.mode,"waiting")
        self.assertEqual(self.index.statistics(other.model_key)["completed"],0)
        self.assertEqual(self.index.statistics("space_v2:synthetic")["completed"],1)

    def feedback_router(self, worker):
        store=ModelApiConfigStore(Path(self.temp.name)/"models.json")
        active=store.save(ModelApiConfig(main_model=MainModelConfig(display_name="Synthetic",provider_type="mock",model="synthetic"),
            embedding=EmbeddingConfig(enabled=True,provider_type="openai_compatible",base_url="https://fixture.invalid/v1",model="synthetic-embedding")),expected_revision=0)
        app=FastAPI(); app.include_router(create_embedding_index_router(store,active,worker))
        return app, store

    def test_status_failure_pause_and_manual_recovery_clear_only_actual_error(self):
        self.fixture.add()
        app,_=self.feedback_router(self.worker)
        with TestClient(app) as client:
            self.assertEqual(client.get("/v1/embedding-index/status").json()["status"],"waiting")
            self.gate.command("start",True); self.inner.fail=True
            asyncio.run(self.worker.index_missing_or_stale())
            failed=client.get("/v1/embedding-index/status").json()
            self.assertEqual(failed["status"],"failed")
            self.assertEqual(failed["error_code"],"EMBEDDING_REQUEST_FAILED")
            self.assertEqual(failed["pending"],1)
            self.assertEqual(failed["completed"],0)
            self.gate.command("pause"); self.gate.error_code="EMBEDDING_INDEX_FAILED"
            paused=client.get("/v1/embedding-index/status").json()
            self.assertEqual(paused["status"],"paused")
            self.assertEqual(paused["error_code"],"EMBEDDING_INDEX_FAILED")
            self.inner.fail=False
            resumed=client.post("/v1/embedding-index/control",json=dict(action="resume",model_key=self.gate.model_key)).json()
            self.assertIsNone(resumed["error_code"])
            self.assertEqual(resumed["status"],"running")
            asyncio.run(self.worker.index_missing_or_stale())
            self.assertEqual(client.get("/v1/embedding-index/status").json()["status"],"completed")

    def test_control_write_failure_has_safe_recovery_code_without_mutating_gate(self):
        app,_=self.feedback_router(self.worker)
        with TestClient(app) as client, patch("app.embedding_index_control.atomic_json_write",side_effect=ModelConfigStoreError("MODEL_CONFIG_WRITE_FAILED")):
            response=client.post("/v1/embedding-index/control",json=dict(action="start",model_key=self.gate.model_key,confirm_cloud_transfer=True))
            self.assertEqual(response.status_code,422)
            self.assertEqual(response.json()["error"]["code"],"EMBEDDING_CONTROL_WRITE_FAILED")
        self.assertEqual(self.gate.mode,"waiting")
        self.assertEqual(self.inner.calls,[])
        self.assertFalse(self.path.exists())

    def test_without_indexer_pending_config_cannot_claim_known_counts_or_active_model(self):
        _,store=self.feedback_router(None)
        app=FastAPI(); app.include_router(create_embedding_index_router(store,None,None))
        with TestClient(app) as client:
            value=client.get("/v1/embedding-index/status").json()
            self.assertEqual(value["status"],"restart_required")
            self.assertIs(value["counts_known"],False)
            self.assertIs(value["control_allowed"],False)
            self.assertIs(value["applied"],False)

    def test_api_rejects_pending_space_and_main_staging_does_not_block_active_embedding(self):
        store=ModelApiConfigStore(Path(self.temp.name)/"models.json")
        main=dict(display_name="Synthetic",provider_type="mock",model="synthetic")
        embedding=dict(enabled=True,provider_type="openai_compatible",base_url="https://fixture.invalid/v1",model="synthetic-embedding")
        for revision,kind,config in ((0,"main",main),(1,"embedding",embedding)):
            command=SaveProfileCommand(expected_revision=revision,kind=kind,profile_id=kind+"-custom",preset_id="custom",config=config)
            store.update_profiles(expected_revision=revision,transform=lambda d:save_profile(d,command))
        for revision,kind in ((2,"main"),(3,"embedding")):
            command=ActivateProfileCommand(expected_revision=revision,kind=kind,profile_id=kind+"-custom",embedding_enabled=kind=="embedding")
            store.update_profiles(expected_revision=revision,transform=lambda d:activate_profile(d,command))
        active=store.load()
        app=FastAPI();app.include_router(create_embedding_index_router(store,active,self.worker));client=TestClient(app)
        response=client.get("/v1/embedding-index/status")
        self.assertEqual(response.json()["status"],"waiting")
        self.assertEqual(client.post("/v1/embedding-index/control",json=dict(action="start",model_key="wrong",confirm_cloud_transfer=True)).status_code,409)
        self.assertEqual(client.post("/v1/embedding-index/control",json=dict(action="start",model_key=self.gate.model_key,confirm_cloud_transfer=True)).status_code,200)
        command=SaveProfileCommand(expected_revision=4,kind="main",profile_id="main-custom",preset_id="custom",config={**main,"model":"synthetic-next"})
        store.update_profiles(expected_revision=4,transform=lambda d:save_profile(d,command))
        command=ActivateProfileCommand(expected_revision=5,kind="main",profile_id="main-custom")
        store.update_profiles(expected_revision=5,transform=lambda d:activate_profile(d,command))
        self.assertTrue(client.get("/v1/embedding-index/status").json()["applied"])
        command=SaveProfileCommand(expected_revision=6,kind="embedding",profile_id="embedding-custom",preset_id="custom",config={**embedding,"model":"different"})
        store.update_profiles(expected_revision=6,transform=lambda d:save_profile(d,command))
        command=ActivateProfileCommand(expected_revision=7,kind="embedding",profile_id="embedding-custom",embedding_enabled=True)
        store.update_profiles(expected_revision=7,transform=lambda d:activate_profile(d,command))
        self.assertEqual(client.get("/v1/embedding-index/status").json()["status"],"restart_required")
        self.assertEqual(client.post("/v1/embedding-index/control",json=dict(action="pause",model_key=self.gate.model_key)).status_code,409)

    def test_client_close_does_not_stop_single_backend_worker(self):
        self.fixture.add()
        store=ModelApiConfigStore(Path(self.temp.name)/"models.json")
        active=store.save(ModelApiConfig(main_model=MainModelConfig(display_name="Synthetic",provider_type="mock",model="synthetic"),
            embedding=EmbeddingConfig(enabled=True,provider_type="openai_compatible",base_url="https://fixture.invalid/v1",model="synthetic-embedding")),expected_revision=0)
        app=FastAPI();app.include_router(create_embedding_index_router(store,active,self.worker))
        async def run():
            self.worker.start()
            task=self.worker._task
            self.worker.start()
            self.assertIs(task,self.worker._task)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url="http://synthetic-client") as client:
                response=await client.post("/v1/embedding-index/control",json=dict(action="start",model_key=self.gate.model_key,confirm_cloud_transfer=True))
                self.assertEqual(response.status_code,200)
            for _ in range(100):
                if (await asyncio.to_thread(self.index.statistics,self.gate.model_key))["completed"] == 1: break
                await asyncio.sleep(.01)
            self.assertEqual(self.index.statistics(self.gate.model_key)["completed"],1)
            # The HTTP client has closed; the lifespan-owned task still indexes new records.
            self.fixture.add("Synthetic memory after settings closed")
            self.worker.notify()
            for _ in range(100):
                if (await asyncio.to_thread(self.index.statistics,self.gate.model_key))["completed"] == 2: break
                await asyncio.sleep(.01)
            self.assertEqual(self.index.statistics(self.gate.model_key)["completed"],2)
            await self.worker.stop()
        asyncio.run(run())
