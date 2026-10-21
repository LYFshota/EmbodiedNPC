"""P1-C: exact offline Unity evidence -> temporary Ledger -> Scripted Cognition/Episode."""
import asyncio
import copy
import json
import os
import tempfile
import time
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

import httpx

from app.cognitive_entry import CognitiveBackendEntry
from app.base_memory_consolidator import BaseMemoryConsolidator
from app.contracts import CognitiveRequest
from app.episode_assembler import EpisodeAssembler
from app.episode_repository import EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_consolidation_admission import MemoryConsolidationAdmission
from app.memory_consolidation_context import MemoryContextResolver
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.sqlite_database import SQLiteDatabase
from tests.test_cross_conversation_recent_dialogue import ScriptedProvider
from tests.test_online_recent_dialogue_ordering import cognitive, experience
from tests.test_cognitive_websocket import SocketHarness


class EmoteRecentEpisodeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        path = Path(os.environ.get('EMBODIEDNPC_P1C_EVIDENCE',
                    str(Path(tempfile.gettempdir()) / 'EmbodiedNPC-P1C-20261016/evidence.json')))
        if not path.is_file() or time.time() - path.stat().st_mtime >= 3600:
            self.skipTest('Requires fresh isolated Unity PlayerEmoteEvidenceTests capture; no synthetic fallback.')
        self.evidence = json.loads(path.read_text(encoding='utf-8-sig'))
        directory = tempfile.TemporaryDirectory(); self.addCleanup(directory.cleanup)
        self.database = SQLiteDatabase(Path(directory.name) / 'p1c.sqlite3')
        self.ledger = ExperienceLedger(self.database.connect)
        self.writer = ExperienceIngestionService(self.ledger)
        self.request = self.evidence['next_request']

    def append(self, value):
        result = self.writer.append(ExperienceAppendRequest.model_validate(value))
        self.assertIn(result.status, ('inserted', 'already_exists'))
        return result

    def ingest_capture(self):
        for value in self.evidence['appends']:
            first = self.append(value)
            second = self.append(value)
            self.assertEqual(second.status, 'already_exists')
            self.assertEqual(first.experience_seq, second.experience_seq)

    def npc_request(self, npc, number=30):
        value = cognitive(number, '我刚才做了什么动作？', agent=npc, conversation='p1c-followup-' + npc)
        wire = self.request['recall_identity']
        owner = next(p for p in (wire['observer'], *wire['current_subjects']) if p['entity_id'] == npc)
        player = next(p for p in wire['current_subjects'] if p['kind'] == 'player')
        value['recall_identity'] = dict(observer=owner, current_subjects=[player])
        value['context']['current_event']['target_entity_id'] = npc
        return value

    def speech(self, value, text, response=False):
        draft = experience(value, text, response=response)
        draft['draft']['source_session_id'] = self.evidence['appends'][0]['draft']['source_session_id']
        self.append(draft)

    @staticmethod
    def timeline(request):
        marker = 'PRIOR INTERACTION TIMELINE JSON: '
        return json.JSONDecoder().raw_decode(request.messages[-1].content.split(marker)[1])[0]

    async def test_T1_direct_actual_evidence_in_next_cognition_once_and_not_speech(self):
        prior = self.npc_request(self.request['run']['agent_entity_id'], number=1)
        self.speech(prior, '之前的普通文字')
        self.speech(prior, '普通文字的实际回复', response=True)
        self.ingest_capture()
        provider = ScriptedProvider()
        provider.context_length = 16384
        await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
            CognitiveRequest.model_validate(self.request))
        items = self.timeline(provider.requests[0])
        self.assertEqual([i['seq'] for i in items], sorted(i['seq'] for i in items))
        self.assertEqual([i['kind'] for i in items], ['speech', 'speech', 'player_emote', 'npc_social_reply'])
        action, reply = items[-2:]
        self.assertEqual(action['action'], 'wave'); self.assertNotIn('text', action)
        self.assertEqual(reply['text'], self.evidence['actually_presented'])
        self.assertEqual(reply['source_event_id'], action['source_event_id'])
        self.assertEqual(reply['trace_id'], action['trace_id'])
        self.assertEqual(len(provider.requests), 1)
        self.assertEqual(provider.requests[0].messages[-1].content.count(self.evidence['actually_presented']), 1)
        self.assertNotIn('表情[挥手]', provider.requests[0].messages[-1].content)
        with closing(self.database.connect()) as connection:
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM experience_events WHERE event_type='social_response'").fetchone()[0], 1)
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM experience_events WHERE event_type='dialogue_input' AND json_extract(payload_json,'$.natural_language_payload') LIKE '%表情[%'").fetchone()[0], 0)

    async def test_core_websocket_ack_to_offline_production_provider_wire(self):
        # Importing the application bootstraps storage: require explicit external isolation.
        path = os.environ.get('EMBODIEDNPC_MEMORY_DB')
        if not path or Path(__file__).resolve().parents[2] in Path(path).resolve().parents:
            self.skipTest('Run in a fresh subprocess with external DB/config isolation.')
        from app import m3_main
        from app.deepseek_provider import DeepSeekOfficialResponsesProvider
        from app.cognitive_memory_recall import CognitiveMemoryRecall
        from app.recall_subjects import RecallSubjectCatalogBuilder
        from app.canonical_memory_repository import CanonicalMemoryRepository

        wires = []
        def capture_http(request):
            wires.append(json.loads(request.content))
            return httpx.Response(200, json={'status': 'completed', 'output': [
                {'type': 'message', 'role': 'assistant', 'content': [
                    {'type': 'output_text', 'text': '{"decision_kind":"NO_ACTION"}'}]}]})
        provider = DeepSeekOfficialResponsesProvider(base_url='https://api.deepseek.com',
            api_key='isolated-placeholder', model='offline', transport=httpx.MockTransport(capture_http))
        provider.context_length = 16384
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger,
            context_length=16384, memory_recall=CognitiveMemoryRecall(
                RecallSubjectCatalogBuilder(CanonicalMemoryRepository(self.database.connect)), None))
        socket = SocketHarness()
        with patch.object(m3_main, 'cognitive_entry', entry), patch.object(
                m3_main, 'experience_ingestion_service', self.writer):
            task = asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
            try:
                for value in self.evidence['appends']:
                    socket.message(value)
                    ack = await socket.result()
                    self.assertEqual(ack['status'], 'inserted')
                    socket.message(value)
                    replay = await socket.result()
                    self.assertEqual(replay['status'], 'already_exists')
                    self.assertEqual(replay['experience_seq'], ack['experience_seq'])
                socket.message(self.request)
                response = await socket.result()
                self.assertEqual(response['message_type'], 'cognitive_response')
            finally:
                socket.disconnect()
                await asyncio.wait_for(task, 3)
        self.assertEqual(len(wires), 1)
        user = wires[0]['input'][-1]['content']
        items = json.JSONDecoder().raw_decode(user.split('PRIOR INTERACTION TIMELINE JSON: ')[1])[0]
        self.assertEqual([i['kind'] for i in items], ['player_emote', 'npc_social_reply'])
        self.assertEqual(items[0]['action'], 'wave')
        self.assertEqual(items[1]['text'], self.evidence['actually_presented'])
        self.assertEqual(items[1]['actor_entity_id'], self.request['run']['agent_entity_id'])
        self.assertEqual(items[0]['source_event_id'], items[1]['source_event_id'])
        self.assertEqual(items[0]['trace_id'], items[1]['trace_id'])
        self.assertNotIn('text', items[0])
        self.assertNotIn('表情[挥手]', user)
        self.assertEqual(user.count(self.evidence['actually_presented']), 1)

    async def test_T2_unseen_npc_has_nothing_visible_bystander_has_no_foreign_reply(self):
        self.ingest_capture()
        for npc, visible in ((self.evidence['unseen_npc'], False), (self.evidence['witness_npc'], True)):
            request = self.npc_request(npc)
            self.speech(request, '我刚才做了什么动作？')
            provider = ScriptedProvider()
            provider.context_length = 16384
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=self.ledger, context_length=16384).run(
                CognitiveRequest.model_validate(request))
            prompt = provider.requests[0].messages[-1].content
            self.assertNotIn(self.evidence['actually_presented'], prompt)
            if visible:
                items = self.timeline(provider.requests[0])
                self.assertEqual([i['kind'] for i in items], ['player_emote'])
                self.assertEqual(items[0]['social_role'], 'bystander')
            else:
                self.assertNotIn('PRIOR INTERACTION TIMELINE JSON:', prompt)
            # A reply cannot be reassigned to a bystander or absent NPC through a new append.
            forged = copy.deepcopy(next(a for a in self.evidence['appends'] if a['draft']['event_type'] == 'social_response'))
            forged['draft']['observer_id'] = forged['draft']['actor_entity_id'] = npc
            forged['draft']['persistent_participants'] = []
            rejected = self.writer.append(ExperienceAppendRequest.model_validate(forged))
            self.assertEqual(rejected.status, 'id_conflict')

    async def test_T3_episode_atomic_optional_evidence_identity_and_no_forced_memory(self):
        self.ingest_capture()
        owner = self.request['run']['agent_entity_id']
        episodes = EpisodeRepository(self.database.connect)
        resolver = MemoryContextResolver(self.ledger, episodes)
        provider = ScriptedProvider(); provider.context_length = 16384
        projector = BaseMemoryConsolidator(provider, PrioritizedModelExecutor(), context_length=16384).projector
        self.assertEqual(projector.allocator.policy.max_context_tokens, 16384)
        assembler = EpisodeAssembler(self.ledger, episodes, extractor_version=V2,
            capacity_probe=lambda events: bool(projector.project(resolver.preview(events))))
        assembler.process_observer(owner)
        assembler.idle_sweep(time.time() + 181)
        closed = [e for e in episodes.list_for_observer(owner) if e.status == 'CLOSED']
        self.assertTrue(closed)
        job = MemoryJobRepository(self.database.connect).get_for_episode(closed[0].episode_id, V2)
        context = resolver.resolve(job)
        events = context.evidence_events
        self.assertEqual([e.event_type for e in events], ['observation', 'social_response'])
        self.assertIsNone(closed[0].conversation_id)
        self.assertTrue(context.observer_persistent_identity)
        self.assertTrue(all(e.participant_handles for e in events))
        projected = projector.project(context)
        self.assertEqual(set(projected.visible_event_ids), {e.event_id for e in events})
        reply = events[1]
        self.assertEqual(reply.structured_payload['parent_source_event_id'], events[0].structured_payload['source_event_id'])
        self.assertTrue(reply.structured_payload['model_content_is_not_world_truth'])
        admission = MemoryConsolidationAdmission(projector)
        batch = admission.evaluate(context, dict(episodic_memories=[], semantic_facts=[]), visible_event_ids=projected.visible_event_ids)
        self.assertEqual(batch.memories, ())
        owner_handle = reply.participant_handles[owner]
        batch = admission.evaluate(context, dict(episodic_memories=[dict(summary='我回应了玩家的挥手。',
            subject_handles=[owner_handle], evidence_event_ids=[reply.event_id], importance=.1, confidence=1)], semantic_facts=[]),
            visible_event_ids=projected.visible_event_ids)
        self.assertEqual(len(batch.memories), 1)
        self.assertIn('NPC曾自述', batch.memories[0].summary)
        player_handle = reply.participant_handles[reply.target_entity_ids[0]]
        false_fact = admission.evaluate(context, dict(episodic_memories=[dict(summary='玩家已经获得成就或拥有强壮体格。',
            subject_handles=[player_handle], evidence_event_ids=[reply.event_id], importance=.1, confidence=1)], semantic_facts=[]),
            visible_event_ids=projected.visible_event_ids)
        self.assertEqual(false_fact.memories, ())
        self.assertEqual(false_fact.rejections[0].reason, 'NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD')
        with closing(self.database.connect()) as connection:
            self.assertEqual(connection.execute('SELECT COUNT(*) FROM memory_records').fetchone()[0], 0)
