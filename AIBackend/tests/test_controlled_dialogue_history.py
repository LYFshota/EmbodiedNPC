"""Real ledger/entry regression for bounded history and matching source IDs."""
import json
import tempfile
import unittest
from pathlib import Path
from app.cognitive_entry import CognitiveBackendEntry
from app.contracts import CognitiveRequest
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.sqlite_database import SQLiteDatabase
from tests.test_online_recent_dialogue_ordering import cognitive,experience,CapturingProvider


class ControlledDialogueHistoryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        directory=tempfile.TemporaryDirectory();self.addCleanup(directory.cleanup)
        self.database=SQLiteDatabase(Path(directory.name)/'memory.sqlite3')
        self.ledger=ExperienceLedger(self.database.connect)
        self.ingestion=ExperienceIngestionService(self.ledger)

    def append(self,request,text,response=False,wrong_source=False):
        value=experience(request,text,response=response)
        if wrong_source:value['draft']['source_event_id']+='__wrong'
        self.ingestion.append(ExperienceAppendRequest.model_validate(value))

    def eight_turns(self):
        for i in range(1,9):
            req=cognitive(i,f'第{i}轮：暂时喜欢淡茉莉，周末偶尔奶茶。')
            self.append(req,req['context']['current_event']['payload'][0]['value'])
            self.append(req,f'第{i}轮：明白，这是暂时和周末条件。',response=True)
        req=cognitive(9,'前面临时口味和周末条件是什么？')
        self.append(req,req['context']['current_event']['payload'][0]['value'])
        return CognitiveRequest.model_validate(req)

    async def test_known_16k_keeps_eight_complete_turns_in_actual_request(self):
        req=self.eight_turns();provider=CapturingProvider()
        entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),experience_ledger=self.ledger,context_length=16384)
        await entry.run(req)
        content=provider.prompts[-1]
        rows=json.JSONDecoder().raw_decode(content.split('PRIOR DIALOGUE TURNS JSON: ')[1])[0]
        self.assertEqual(len(rows),16)
        players=[m for m in rows if m['role']=='player']
        self.assertEqual(len(players),8)
        by_seq={m['seq']:m for m in rows}
        self.assertTrue(all(by_seq[m['reply_to_seq']]['role']=='player'
                            for m in rows if m['role']=='npc'))
        self.assertTrue(players[0]['text'].startswith('第1轮'))
        self.assertTrue(players[-1]['text'].startswith('第8轮'))
        self.assertNotIn('前面临时口味和周末条件是什么',json.dumps(rows,ensure_ascii=False))

    async def test_unknown_window_keeps_eight_message_target_with_bounded_overlap(self):
        req=self.eight_turns()
        entry=CognitiveBackendEntry(CapturingProvider(),PrioritizedModelExecutor(),experience_ledger=self.ledger)
        rows=await entry._read_recent_dialogue(req)
        self.assertEqual(rows.target_messages,8)
        self.assertEqual(len(rows),10)
        self.assertEqual(rows.expanded_messages,2)
        self.assertTrue(rows[-8]['text'].startswith('第5轮'))

    async def test_response_with_different_source_event_cannot_become_paired(self):
        first=cognitive(1,'上一句。');self.append(first,'上一句。')
        self.append(first,'我会替你确认。',response=True,wrong_source=True)
        current=cognitive(2,'你刚才说什么？');self.append(current,'你刚才说什么？')
        rows=self.ledger.read_recent_dialogue('npc_cafe_01','conversation_b1',current['request_id'],current['context']['current_event']['event_id'])
        self.assertEqual(rows[0]['evidence_status'],'input_only')
        self.assertIsNone(rows[0]['npc_text'])


if __name__=='__main__':unittest.main()
