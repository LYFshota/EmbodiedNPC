"""T3: actual Unity hearing capture -> isolated ACKs -> Scripted cognition -> 16K wire."""
import copy
import json
import tempfile
import time
import unittest
from contextlib import closing
from dataclasses import replace
from pathlib import Path

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetPolicy
from app.consolidation_evidence_projection import ConsolidationEvidenceProjector
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions
from app.sqlite_database import SQLiteDatabase
from app.episode_assembler import EpisodeAssembler
from app.episode_repository import EpisodeRepository
from app.memory_consolidation_context import MemoryContextResolver
from app.memory_consolidation_admission import MemoryConsolidationAdmission
from app.memory_job_repository import MemoryJobRepository
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from tests.test_cross_conversation_recent_dialogue import ScriptedProvider


class ThirdPartyHearingTests(unittest.IsolatedAsyncioTestCase):
    async def test_T3_actual_capture_ack_recent_wire_and_conservative_episode(self):
        capture = Path(tempfile.gettempdir()) / 'EmbodiedNPC-Hearing-P1F-20261016/t1.json'
        self.assertTrue(capture.is_file(), 'Run only CafeSpeechHearingTests T1/T2 first; no synthetic fallback.')
        self.assertLess(time.time() - capture.stat().st_mtime, 3600)
        evidence = json.loads(capture.read_text(encoding='utf-8-sig'))
        with tempfile.TemporaryDirectory() as directory:
            db = SQLiteDatabase(Path(directory) / 'hearing.sqlite3')
            ledger = ExperienceLedger(db.connect)
            writer = ExperienceIngestionService(ledger)
            local = []
            for value in evidence['appends']:
                packet = ExperienceAppendRequest.model_validate(value)
                ack = writer.append(packet)
                self.assertEqual(ack.status, 'inserted', ack.reason)
                self.assertEqual(writer.append(packet).status, 'already_exists')
                if packet.draft.observer_id == evidence['observer'] and packet.draft.event_type == 'observation':
                    local.append(ledger.get_by_event_id(ack.event_id))
            self.assertEqual(len(local), 2)
            self.assertNotEqual(local[0].source_event_id, local[1].source_event_id)
            self.assertEqual(local[0].trace_id, local[1].trace_id)
            self.assertTrue(all(e.conversation_id is None and e.provenance == 'HEARD' for e in local))
            provider = ScriptedProvider([dict(decision_kind='NO_ACTION')]); provider.context_length = 16384
            entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=ledger,
                context_length=16384, inference_options=InferenceOptions(max_output_tokens=512))
            self.assertEqual(len(provider.requests), 0)
            result = await entry.run(CognitiveRequest.model_validate(evidence['next_request']))
            self.assertEqual(result.decision.decision_kind, 'NO_ACTION')
            self.assertEqual(len(provider.requests), 1)
            adapter = DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid', api_key='offline-only', model='offline')
            wire = adapter.budget_payload(provider.requests[0])
            user = wire['input'][-1]['content']
            items = json.JSONDecoder().raw_decode(user.split('PRIOR INTERACTION TIMELINE JSON: ')[1])[0]
            hearing = [i for i in items if i['kind'] == 'heard_speech']
            self.assertEqual(len(hearing), 2)
            self.assertEqual([i['actor_entity_id'] for i in hearing], ['player_01', 'npc_cafe_01'])
            self.assertTrue(all(i['observer_entity_id'] == evidence['observer'] for i in hearing))
            self.assertTrue(all('历史旁听' in i['history_description'] and i['model_content_is_not_world_truth'] for i in hearing))
            self.assertEqual(user.count(evidence['reply']), 2)  # Raw text + quotation in the same record.
            actual_input = local[0].structured_payload['known_fields']['text']
            self.assertIn('一杯拿铁', actual_input)
            self.assertEqual(user.count(actual_input), 2)
            self.assertEqual(len({i['occurrence_id'] for i in hearing}), 2)
            self.assertFalse(any(i['kind'] == 'speech' for i in items))
            size = sum(len(json.dumps([i], ensure_ascii=False, separators=(',', ':'))) for i in hearing)
            self.assertLessEqual(size, 2400)
            tokens = ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(',', ':')))
            self.assertLessEqual(tokens + 512 + 1536, 16384)
            # Same reader cannot import this listener's evidence into an unrelated observer.
            self.assertEqual(ledger.read_recent_interactions('npc_cafe_quiet_01', 'missing', 'missing', 'missing', {}), [])
            invalid = copy.deepcopy(next(a for a in evidence['appends']
                if a['draft']['structured_payload'].get('known_fields', {}).get('event_type') == 'heard_speech'))
            invalid['draft']['provenance'] = 'SYSTEM_CONFIRMED'
            with self.assertRaises(ValueError):
                ExperienceAppendRequest.model_validate(invalid)
            episodes = EpisodeRepository(db.connect)
            assembler = EpisodeAssembler(ledger, episodes, extractor_version=V2)
            assembler.process_observer(evidence['observer']); assembler.idle_sweep(time.time() + 181)
            closed = [e for e in episodes.list_for_observer(evidence['observer']) if e.status == 'CLOSED']
            self.assertTrue(closed)
            context = MemoryContextResolver(ledger, episodes).resolve(
                MemoryJobRepository(db.connect).get_for_episode(closed[0].episode_id, V2))
            heard_event = next(e for e in context.evidence_events if e.event_type == 'observation')
            self.assertIn(evidence['observer'], heard_event.participant_handles)
            owner = heard_event.participant_handles[evidence['observer']]
            admitted = MemoryConsolidationAdmission(ConsolidationEvidenceProjector(
                replace(ContextBudgetPolicy.consolidation(), max_context_tokens=16384,
                    input_hard_cap=13448, estimation_reserve=1536))).evaluate(context, dict(semantic_facts=[], episodic_memories=[dict(
                summary='拿铁已经制作完成。', subject_handles=[owner], evidence_event_ids=[heard_event.event_id],
                importance=.1, confidence=1)]))
            self.assertEqual(admitted.memories, ())
            self.assertEqual(admitted.rejections[0].reason, 'HEARD_SPEECH_REQUIRES_ATTRIBUTED_MEMORY_SUPPORT')
            with closing(db.connect()) as connection:
                self.assertEqual(connection.execute('SELECT COUNT(*) FROM memory_records').fetchone()[0], 0)
            print(f'T3 wire: input_estimate={tokens}, output=512, margin=1536, limit=16384; projection_chars={size}; hearing={len(hearing)}')
