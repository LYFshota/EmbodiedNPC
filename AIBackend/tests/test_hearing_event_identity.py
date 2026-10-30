"""T1 continuation: actual EventIdentity capture through the unchanged recent reader, offline 16K."""
import json
import tempfile
import time
import unittest
from pathlib import Path

from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.context_token_estimator import ContextTokenEstimator
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions
from app.sqlite_database import SQLiteDatabase
from tests.test_cross_conversation_recent_dialogue import ScriptedProvider


class HearingEventIdentityTests(unittest.IsolatedAsyncioTestCase):
    async def test_T1_event_identity_capture_reaches_following_16k_wire(self):
        path = Path(tempfile.gettempdir()) / 'EmbodiedNPC-Hearing-P1F-20261016/event_identity_t1.json'
        self.assertTrue(path.is_file(), 'Run the targeted Unity EventIdentity T1 first.')
        self.assertLess(time.time() - path.stat().st_mtime, 3600)
        evidence = json.loads(path.read_text(encoding='utf-8-sig'))
        with tempfile.TemporaryDirectory() as directory:
            database = SQLiteDatabase(Path(directory) / 'event-identity.sqlite3')
            ledger = ExperienceLedger(database.connect)
            ingestion = ExperienceIngestionService(ledger)
            heard = []
            for value in evidence['appends']:
                packet = ExperienceAppendRequest.model_validate(value)
                ack = ingestion.append(packet)
                self.assertEqual(ack.status, 'inserted', ack.reason)
                self.assertEqual(ingestion.append(packet).status, 'already_exists')
                if packet.draft.event_type == 'observation':
                    self.assertEqual(packet.draft.provenance, 'HEARD')
                    self.assertIsNone(packet.draft.conversation_id)
                    heard.append(packet.draft)
            self.assertEqual(len(heard), 2)
            provider = ScriptedProvider([dict(decision_kind='NO_ACTION')]); provider.context_length = 16384
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), experience_ledger=ledger,
                context_length=16384, inference_options=InferenceOptions(max_output_tokens=512)).run(
                    CognitiveRequest.model_validate(evidence['next_request']))
            self.assertEqual(len(provider.requests), 1)
            adapter = DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid', api_key='offline-only', model='offline')
            wire = adapter.budget_payload(provider.requests[0])
            user = wire['input'][-1]['content']
            items = json.JSONDecoder().raw_decode(user.split('PRIOR INTERACTION TIMELINE JSON: ')[1])[0]
            self.assertEqual([i['kind'] for i in items], ['heard_speech', 'heard_speech'])
            self.assertEqual([i['actor_entity_id'] for i in items], ['player_01', evidence['speaker']])
            self.assertEqual([i['target_entity_id'] for i in items], [evidence['speaker'], 'player_01'])
            for item in items:
                self.assertEqual(item['observer_entity_id'], evidence['observer'])
                self.assertEqual(item['target_epistemic_status'], 'identified_from_speech_event')
                self.assertIn('历史旁听', item['history_description'])
                self.assertTrue(item['model_content_is_not_world_truth'])
                # Source wording appears once; the description retains identity/uncertainty only.
                self.assertEqual(user.count(item['text']), 1)
                self.assertNotIn(item['text'], item['history_description'])
                self.assertEqual(user.count('"occurrence_id":"' + item['occurrence_id'] + '"'), 1)
            chars = sum(len(json.dumps([i], ensure_ascii=False, separators=(',', ':'))) for i in items)
            self.assertLessEqual(chars, 2400)
            tokens = ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(',', ':')))
            self.assertLessEqual(tokens + 512 + 1536, 16384)
            print(f'T1: input_estimate={tokens}, output=512, margin=1536, window=16384; chars={chars}/2400; heard={len(items)}')
