"""Synthetic real-schema export acceptance; no production data, models or migrations on reads."""
import asyncio
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from zipfile import ZipFile

import httpx
from fastapi import FastAPI

from app.experience_export import ExperienceExport, ExportLimitError, create_experience_export_router
from app.experience_models import ExperienceDraft, ExperienceAppendRequest
from app.local_access import LocalBackendSession, LocalBackendAccessMiddleware
from app.relationship_reflection_models import RelationshipReflectionRecord
from app.relationship_reflection_repository import RelationshipReflectionRepository
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.task_models import TaskIdentity, TaskScope
from app.task_repository import TaskRepository
from app.task_service import TaskService
from app.world_knowledge_store import WorldKnowledgeStore, KnowledgeCatalog


class ExperienceExportTests(unittest.TestCase):
    def setUp(self):
        from tests.test_memory_c2a import MemoryC2ATests
        self.fixture = MemoryC2ATests(methodName="runTest"); self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = Path(self.fixture.directory.name)
        self.store = WorldKnowledgeStore(self.root / "knowledge.json"); self.store.initialize()
        self.db = self.fixture.database
        self.exporter = ExperienceExport(self.db.database_path, self.store, "U10001", runtime={"model":"test", "api_key":"DO_NOT_EXPORT"})

    def read(self, exporter=None):
        p = (exporter or self.exporter).build(); self.addCleanup(lambda: p.unlink(missing_ok=True))
        with ZipFile(p) as z:
            self.assertIsNone(z.testzip())
            data = {name: (json.loads(z.read(name)) if name.endswith(".json") else
                [json.loads(line) for line in z.read(name).decode().splitlines()] if name.endswith(".jsonl") else z.read(name).decode()) for name in z.namelist()}
        return data

    def seed(self):
        f = self.fixture
        job, context = f.work(text="我喜欢拿铁，也希望下次喝摩卡。")
        f.finalize(job, context, f.response(context, episodic=True, summary="玩家表达了拿铁偏好和未来饮品意愿。"))
        self.own_event = context.evidence_events[0].event_id
        RelationshipReflectionRepository(self.db.connect).insert(RelationshipReflectionRecord(
            reflection_id="relationship-own", observer=TrustedPersistentIdentity("npc","Character","merchant.001"),
            subject=TrustedPersistentIdentity("player","Player","U10001"), episode_id=context.episode_id,
            episode_input_hash=context.input_hash, outcome="INTERPRETATION", interpretation="玩家愿意在下次来时继续交流。",
            evidence_event_ids=(self.own_event,), policy_version="test", generated_at=time.time()))
        job, context = f.work(uid="FOREIGN_PLAYER", text="FOREIGN_PRIVATE_TEXT")
        f.finalize(job, context, f.response(context, episodic=True, summary="FOREIGN_MEMORY"))
        RelationshipReflectionRepository(self.db.connect).insert(RelationshipReflectionRecord(
            reflection_id="relationship-foreign", observer=TrustedPersistentIdentity("npc","Character","merchant.001"),
            subject=TrustedPersistentIdentity("player","Player","FOREIGN_PLAYER"), episode_id=context.episode_id,
            episode_input_hash=context.input_hash, outcome="INTERPRETATION", interpretation="FOREIGN_RELATIONSHIP",
            evidence_event_ids=(context.evidence_events[0].event_id,), policy_version="test", generated_at=time.time()))
        participants=[dict(entity_id="player_01",identity_namespace="player",identity_type="Player",persistent_id="U10001"),
            dict(entity_id="npc_01",identity_namespace="npc",identity_type="Character",persistent_id="merchant.001")]
        for i in range(125):
            draft=ExperienceDraft(schema_version="experience-draft/1.1",observer_id="npc_01",source_type="dialogue",
                event_type="dialogue_response" if i%2 else "dialogue_input",source_session_id="session-all",clock_domain="unity_realtime",
                source_event_id="all-source-"+str(i),actor_entity_id="npc_01" if i%2 else "player_01",
                target_entity_ids=("player_01",) if i%2 else ("npc_01",),persistent_participants=participants,
                structured_payload={"api_key":"STRUCTURAL_SECRET", "file_path":"C:\\private\\user", "request_id":"request-"+str(i)},
                natural_language_payload="完整原文第"+str(i)+"条：\n第二行，https://my-authored.example/ 保留。",
                occurred_at=float(i),observed_at=float(i),conversation_id="conversation-all",request_id="request-all-"+str(i),
                provenance="EXECUTED_BY_SELF" if i%2 else "HEARD")
            f.ingestion.append(ExperienceAppendRequest(request_id="append-all-"+str(i),trace_id="trace-all-"+str(i),draft=draft))
        from tests.test_task_service import create_request, execution_request, synthetic_result
        service=TaskService(TaskRepository(self.db.connect)); owner=TaskIdentity(identity_namespace="npc",identity_type="Character",persistent_id="merchant.001")
        player=TaskIdentity(identity_namespace="player",identity_type="Player",persistent_id="U10001")
        scope=TaskScope(owner=owner,player=player,visit_id="visit-1",allowed_goal_kinds=("serve_drink","clean_assigned_counter"))
        service.create_task(scope,"future-mocha",create_request(drink="mocha"))
        cancelled=service.create_task(scope,"cancel-me",create_request(drink="americano")).task
        service.cancel_task(scope,"cancel",cancelled.task_id,expected_version=1,reason="玩家取消")
        task=service.create_task(scope,"complete-me",create_request(drink="latte")).task
        later=scope.model_copy(update={"visit_id":"visit-2"})
        task=service.reserve_execution(later,"reserve",task.task_id,execution_request(task)).task
        task=service.start_execution(later,"start",task.task_id,expected_version=task.version,attempt_id=task.execution.attempt_id).task
        service.settle_execution(later,"settle",task.task_id,synthetic_result(task))
        foreign=scope.model_copy(update={"player":player.model_copy(update={"persistent_id":"FOREIGN_PLAYER"})})
        service.create_task(foreign,"foreign-task",create_request(drink="hot_chocolate"))
        f.work(subject_identity=False, text="UNBOUND_PRIVATE_TEXT")

    def test_full_retained_data_and_current_knowledge_roundtrip(self):
        self.seed(); d=self.read()
        self.assertEqual(len(d["conversations.jsonl"]),126)  # Beyond the history API's 100-row cap.
        self.assertIn("完整原文第124条",d["conversations.md"])
        self.assertEqual(len(d["memories.json"]["records"]),1)
        self.assertEqual(len(d["episodes.json"]),1)
        self.assertEqual(len(d["relationships.json"]),1)
        self.assertEqual({t["status"] for t in d["tasks.json"]["records"]},{"waiting","cancelled","completed"})
        self.assertEqual(d["actions.json"]["task_executions"][0]["execution"]["result"]["receipt"]["drink_type"],"latte")
        catalog=KnowledgeCatalog.model_validate(d["world_knowledge.json"])
        self.assertEqual(len(catalog.entries),108)
        self.assertEqual(catalog.model_dump(),self.store.snapshot()[1].model_dump())
        self.assertTrue(d["backend_manifest.json"]["complete"])
        projections=d["persona_language_projections.json"]["entries"]
        self.assertEqual(len(projections),4)
        self.assertIn('Stilllight',json.dumps(projections))

    def test_foreign_and_unbound_players_do_not_leak_with_same_entity_id(self):
        self.seed(); d=self.read(); text=json.dumps(d,ensure_ascii=False)
        for forbidden in ("FOREIGN_PLAYER","FOREIGN_PRIVATE_TEXT","UNBOUND_PRIVATE_TEXT","FOREIGN_MEMORY","FOREIGN_RELATIONSHIP","STRUCTURAL_SECRET","DO_NOT_EXPORT","C:\\\\private"):
            self.assertNotIn(forbidden,text)
        self.assertIn("https://my-authored.example/",text)
        self.assertEqual(d["backend_manifest.json"]["sections"]["experiences"]["excluded_other_or_unbound_player_scope"],2)

    def test_live_wal_snapshot_keeps_data_unchanged_and_writer_operational(self):
        self.seed()
        with closing(self.db.connect()) as writer:
            writer.execute("PRAGMA journal_mode=WAL");writer.execute("INSERT INTO schema_migrations VALUES(12345,1)");writer.commit()
            before=hashlib.sha256(Path(self.db.database_path).read_bytes()).hexdigest()
            logical=writer.execute("SELECT count(*) FROM experience_events").fetchone()[0]
            self.read()
            self.assertEqual(hashlib.sha256(Path(self.db.database_path).read_bytes()).hexdigest(),before)
            self.assertEqual(writer.execute("SELECT count(*) FROM experience_events").fetchone()[0],logical)
            writer.execute("INSERT INTO schema_migrations VALUES(12346,1)");writer.commit()
            self.assertEqual(writer.execute("PRAGMA integrity_check").fetchone()[0],"ok")

    def test_missing_or_broken_category_is_explicit_and_other_data_survives(self):
        self.seed()
        with closing(self.db.connect()) as db:
            db.execute("DROP TABLE memory_semantic_facts");db.commit()
        d=self.read()
        self.assertFalse(d["backend_manifest.json"]["complete"])
        self.assertEqual(d["backend_manifest.json"]["sections"]["memories"]["status"],"unavailable")
        self.assertEqual(len(d["conversations.jsonl"]),126)
        missing=ExperienceExport(self.root/'absent.sqlite3',self.store,"U10001")
        d=self.read(missing)
        self.assertFalse((self.root/'absent.sqlite3').exists())
        self.assertEqual(d["backend_manifest.json"]["sections"]["world_knowledge"]["status"],"ok")

    def test_export_does_not_initialize_or_migrate_knowledge(self):
        self.store.path.unlink()
        d=self.read()
        self.assertEqual(d["backend_manifest.json"]["sections"]["world_knowledge"]["status"],"unavailable")
        self.assertFalse(self.store.path.exists())

    def test_actions_report_partial_when_task_receipts_cannot_be_read(self):
        self.seed()
        with patch.object(TaskRepository, '_record', side_effect=ValueError('broken task')):
            d=self.read()
        self.assertEqual(d['backend_manifest.json']['sections']['actions']['status'],'partial')
        self.assertEqual(d['backend_manifest.json']['sections']['actions']['dependencies']['tasks'],'unavailable')
        self.assertFalse(d['backend_manifest.json']['complete'])

    def test_semantic_facts_keep_their_literal_source_and_consolidation_status(self):
        f=self.fixture;job,context=f.work(text="我最喜欢的颜色是Blue。")
        f.finalize(job,context)
        d=self.read();memory=d['memories.json']
        self.assertEqual(memory['records'][0]['memory_type'],'SEMANTIC_FACT')
        self.assertEqual(len(memory['memory_semantic_facts']),1)
        self.assertEqual(memory['memory_semantic_facts'][0]['excerpt_event_id'],context.evidence_events[0].event_id)
        self.assertEqual(memory['consolidation_jobs'][0]['status'],'SUCCEEDED')

    def test_cancelled_action_without_recipient_needs_own_unambiguous_dialogue_trace(self):
        participants=lambda uid:[dict(entity_id='npc_01',identity_namespace='npc',identity_type='Character',persistent_id='merchant.001'),
                                 dict(entity_id='player_01',identity_namespace='player',identity_type='Player',persistent_id=uid)]
        for uid,trace in [('U10001','own-trace'),('FOREIGN_PLAYER','foreign-trace')]:
            for kind in ('input','action'):
                draft=ExperienceDraft(schema_version='experience-draft/1.1',observer_id='npc_01',
                    event_type='dialogue_input' if kind=='input' else 'goal_cancelled',source_type='dialogue' if kind=='input' else 'goal_outcome',
                    source_session_id='session-trace',clock_domain='unity_realtime',source_event_id=trace+kind,
                    request_id=trace,trace_id=trace,conversation_id='conversation-trace',
                    actor_entity_id='player_01' if kind=='input' else 'npc_01',target_entity_ids=('npc_01',) if kind=='input' else (),
                    persistent_participants=participants(uid) if kind=='input' else participants(uid)[:1],
                    natural_language_payload='请取消制作' if kind=='input' else None,
                    structured_payload={'goal_type':'prepare_drink','status':'Cancelled'},
                    occurred_at=1.,observed_at=1.,provenance='HEARD' if kind=='input' else 'EXECUTED_BY_SELF')
                self.fixture.ingestion.append(ExperienceAppendRequest(request_id=trace+'append'+kind,trace_id=trace,draft=draft))
        d=self.read();actions=d['actions.json']['ledger_results']
        self.assertEqual(len(actions),1)
        self.assertEqual(actions[0]['trace_id'],'own-trace')
        self.assertEqual(actions[0]['export_player_attribution'],'persisted_dialogue_trace')
        self.assertNotIn('foreign-trace',json.dumps(d))

    def test_local_authorization_and_host_player_scope(self):
        async def run():
            with patch.dict(os.environ,{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE":str(self.root/'session.json'),"EMBODIEDNPC_KNOWLEDGE_ADMIN_URL":"http://127.0.0.1:18976"}):
                session=LocalBackendSession(SimpleNamespace(path=self.store.path));session.start()
                try:
                    app=FastAPI();app.add_middleware(LocalBackendAccessMiddleware,session=session)
                    app.include_router(create_experience_export_router(self.exporter,session))
                    transport=httpx.ASGITransport(app=app,client=("127.0.0.1",1234))
                    async with httpx.AsyncClient(transport=transport,base_url="http://127.0.0.1:18976") as client:
                        self.assertEqual((await client.get('/v1/experience-export')).status_code,403)
                        response=await client.get('/v1/experience-export?player_uid=FOREIGN_PLAYER',headers={"Authorization":"Bearer "+session.token})
                        self.assertEqual(response.status_code,200)
                        self.assertIn('no-store',response.headers['cache-control'])
                        import io
                        with ZipFile(io.BytesIO(response.content)) as z:
                            self.assertEqual(json.loads(z.read('backend_manifest.json'))['player_identity']['persistent_id'],'U10001')
                            self.assertNotIn(session.token.encode(),b''.join(z.read(n) for n in z.namelist()))
                finally:session.stop()
        asyncio.run(run())

    def test_resource_limit_fails_without_publishing_a_truncated_package(self):
        self.seed()
        with patch('app.experience_export.MAX_SCAN_ROWS', 2):
            with self.assertRaisesRegex(ExportLimitError, 'SNAPSHOT_LIMIT'):
                self.exporter.build()
        before=set(Path(__import__('tempfile').gettempdir()).glob('embodiednpc-experience-*.zip'))
        with patch('app.experience_export.MAX_ARCHIVE_BYTES', 100):
            with self.assertRaisesRegex(ExportLimitError, 'ARCHIVE_LIMIT'):
                self.exporter.build()
        self.assertEqual(before,set(Path(__import__('tempfile').gettempdir()).glob('embodiednpc-experience-*.zip')))

    def test_wal_writer_can_commit_while_all_categories_share_one_snapshot(self):
        self.seed()
        from app.experience_ledger import ExperienceLedger
        original=ExperienceLedger._row_to_event
        wrote=False
        with closing(self.db.connect()) as writer:
            writer.execute('PRAGMA journal_mode=WAL')
            count=writer.execute('SELECT count(*) FROM experience_events').fetchone()[0]
        def decode(row):
            nonlocal wrote
            if not wrote:
                wrote=True
                # Writer commits in the middle of the read transaction, rather than after export.
                with closing(self.db.connect()) as writer:
                    writer.execute("UPDATE npc_tasks SET version=version+1 WHERE status='waiting'");writer.commit()
            return original(row)
        with patch.object(ExperienceLedger,'_row_to_event',side_effect=decode):
            d=self.read()
        self.assertTrue(wrote)
        self.assertEqual(len(d['tasks.json']['records']),3)
        self.assertEqual(next(t for t in d['tasks.json']['records'] if t['status']=='waiting')['version'],1)
        self.assertEqual(d['backend_manifest.json']['sections']['experiences']['count'],count-2)
        with closing(self.db.connect()) as writer:
            self.assertEqual(writer.execute("SELECT min(version) FROM npc_tasks WHERE status='waiting'").fetchone()[0],2)
            self.assertEqual(writer.execute('PRAGMA integrity_check').fetchone()[0],'ok')

    def test_structural_session_token_and_cookie_are_removed_without_redacting_authored_text(self):
        from app.experience_export import safe_metadata
        self.assertEqual(safe_metadata({'session_token':'secret','cookie':'secret','text':'玩家写下 token 这个词'}),{'text':'玩家写下 token 这个词'})

    def test_disconnect_during_download_removes_private_temporary_zip(self):
        from app.experience_export import _ExportFileResponse
        path=self.exporter.build()
        async def run():
            async def send(message): raise OSError('disconnected')
            async def receive(): return {'type':'http.disconnect'}
            response=_ExportFileResponse(path)
            with self.assertRaises(OSError):
                await response({'type':'http','method':'GET','headers':[], 'asgi':{'spec_version':'2.4'}},receive,send)
        asyncio.run(run())
        self.assertFalse(path.exists())
