from contextlib import closing
from dataclasses import replace
import unittest

from app.memory_consolidation_admission import semantic_id, identity_key
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.canonical_recall_models import CanonicalRecallBudget, CanonicalReadFilter
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from tests.test_dsn_singleplayer import DsnFixture, OWNER, UID


class DsnP1Tests(DsnFixture):
    def test_exact_content_cross_episode_merges_evidence_without_score_inflation(self):
        ids=[]
        for confidence in (.3,.95,.99):
            job,c,speech=self.speech_episode()
            memory=self.finalize(job,c,self.proposal(c,[speech],confidence=confidence)).memories[0]
            ids.append(memory.memory_id)
        self.assertEqual(1,len(set(ids)))
        record,=self.rows('SELECT * FROM memory_records')
        self.assertEqual(.3,record['confidence'])
        self.assertEqual(3,len(self.rows('SELECT * FROM memory_evidence')))
        self.assertEqual('NPC_SELF_STATEMENT',record['provenance_label'])
        self.assertEqual(3,len(self.read().items[0].raw_evidence_refs))

    def test_same_episode_duplicate_candidates_use_earliest_claim_scores(self):
        first=self.append('dialogue_response','旧书店经历。')
        self.last_input=None
        second=self.append('dialogue_response','旧书店经历。')
        _,c=self.close([first,second])
        a=self.proposal(c,[first],confidence=.2)['episodic_memories'][0]
        b=self.proposal(c,[second],confidence=.9)['episodic_memories'][0]
        batches=[self.admission.evaluate(c,dict(episodic_memories=values,semantic_facts=[])) for values in ([a,b],[b,a])]
        self.assertEqual(batches[0].memories,batches[1].memories)
        memory,=batches[0].memories
        self.assertEqual(.2,memory.confidence)
        self.assertEqual(2,len(memory.evidence))

    def test_four_owners_with_same_content_stay_independent(self):
        for index in range(4):
            owner=f'dsn.npc.00{index+1}'
            job,c,speech=self.speech_episode(owner=owner)
            self.finalize(job,c,self.proposal(c,[speech]))
        self.assertEqual(4,len(self.rows('SELECT * FROM memory_records')))
        for index in range(4):
            owner=TrustedPersistentIdentity('npc','Character',f'dsn.npc.00{index+1}')
            result=self.read(observer=owner,query='旧书店')
            self.assertEqual(1,len(result.items))
            self.assertEqual(owner,result.resolve_evidence_subject(result.items[0].subjects[0].subject_ref))

    def test_old_event_keyed_duplicates_group_before_topk_without_db_migration(self):
        job,c,speech=self.speech_episode(); memory=self.finalize(job,c,self.proposal(c,[speech])).memories[0]
        # Simulate the old immutable event-keyed ID, not a schema/identity rewrite in production.
        with closing(self.database.connect()) as db:
            db.execute('PRAGMA foreign_keys=OFF')
            db.execute('UPDATE memory_records SET memory_id=?, summary=? WHERE memory_id=?',('old_self_1','小时候常去旧书店读推理小说。',memory.memory_id))
            db.execute('UPDATE memory_subjects SET memory_id=? WHERE memory_id=?',('old_self_1',memory.memory_id))
            db.execute('UPDATE memory_evidence SET memory_id=? WHERE memory_id=?',('old_self_1',memory.memory_id));db.commit()
        job,c,speech=self.speech_episode(); self.finalize(job,c,self.proposal(c,[speech],confidence=.99))
        before=self.rows('SELECT * FROM memory_records ORDER BY memory_id')
        result=self.read(query='旧书店',budget=CanonicalRecallBudget(top_k=1))
        self.assertEqual(1,result.eligible_count)
        self.assertEqual(2,len(result.items[0].raw_evidence_refs))
        self.assertEqual(.6,result.items[0].confidence)
        self.assertEqual(before,self.rows('SELECT * FROM memory_records ORDER BY memory_id'))

    def test_own_semantic_claims_remain_independent_without_latest_wins(self):
        for value in ('海边','山边'):
            job,c,speech=self.speech_episode()
            proposal=self.proposal(c,[speech],profile=True)
            proposal['semantic_facts'][0].update(fact='居住在'+value)
            self.finalize(job,c,proposal)
        self.assertEqual({'ACTIVE'}, {r['status'] for r in self.rows('SELECT status FROM memory_records')})
        result=CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.database.connect,
            expected_player_uid=UID)).retrieve(observer=OWNER,semantic_query='居住在',
            filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT'))
        self.assertEqual(2,len(result.items))
        self.assertEqual({'ACTIVE'},{i.lifecycle_status for i in result.items})

    def test_correction_preserves_original_evidence_without_automatic_episodic_supersede(self):
        ids=[]
        for summary in ('本人讲述在海边长大。','本人明确纠正先前海边说法，说明是在山边长大。'):
            job,c,speech=self.speech_episode()
            ids.append(self.finalize(job,c,self.proposal(c,[speech],summary=summary)).memories[0].memory_id)
        records=self.rows('SELECT * FROM memory_records')
        self.assertEqual(2,len(records))
        self.assertEqual({'ACTIVE'},{r['status'] for r in records})
        self.assertTrue(any('明确纠正' in r['summary'] for r in records))
        self.assertEqual(2,len(self.rows('SELECT * FROM memory_evidence')))
        self.assertEqual(set(ids),{i.memory_id for i in self.read(query='海边 山边').items})
        bounded=self.read(query='海边 山边',budget=CanonicalRecallBudget(top_k=1))
        self.assertTrue(bounded.truncated)  # A budget can omit one alternative; no semantic guarantee.

    def test_action_ids_stay_stable_and_semantic_claims_keep_episode_local_identity(self):
        event=self.append('goal_succeeded',targets=())
        job,c=self.close([event]); proposal=self.proposal(c,[event],summary='完成打开门的任务。')
        memory=self.finalize(job,c,proposal).memories[0]
        expected=semantic_id('memory_', [['persistent',identity_key(OWNER)], [event.event_id],
            '完成打开门的任务。',[identity_key(OWNER)]])
        self.assertEqual(expected,memory.memory_id)
        first=self.append('dialogue_input','我喜欢卡布奇诺。'); job,c=self.close([first])
        proposal=self.proposal(c,[first],self_subject=False,profile=True,confidence=.2)
        proposal['semantic_facts'][0]['fact']='玩家喜欢卡布奇诺。'
        original=self.finalize(job,c,proposal).memories[0]
        second=self.append('dialogue_input','我喜欢卡布奇诺。'); job,c=self.close([second])
        proposal=self.proposal(c,[second],self_subject=False,profile=True,confidence=.9)
        proposal['semantic_facts'][0]['fact']='玩家喜欢卡布奇诺。'
        newer=self.finalize(job,c,proposal).memories[0]
        self.assertNotEqual(original.memory_id,newer.memory_id)
        self.assertEqual(.2,self.rows('SELECT confidence FROM memory_records WHERE memory_id=?',(original.memory_id,))[0]['confidence'])
        self.assertEqual(.9,self.rows('SELECT confidence FROM memory_records WHERE memory_id=?',(newer.memory_id,))[0]['confidence'])
        self.assertEqual(1,len(self.rows('SELECT * FROM memory_evidence WHERE memory_id=?',(original.memory_id,))))
        self.assertEqual(1,len(self.rows('SELECT * FROM memory_evidence WHERE memory_id=?',(newer.memory_id,))))

    def test_own_semantic_claims_preserve_independent_evidence_and_scores(self):
        for confidence in (.2,.9):
            job,c,speech=self.speech_episode()
            self.finalize(job,c,self.proposal(c,[speech],profile=True,confidence=confidence))
        records=self.rows('SELECT * FROM memory_records')
        self.assertEqual(2,len(records))
        self.assertEqual([.2,.9],sorted(r['confidence'] for r in records))
        self.assertTrue(all(r['memory_type']=='SEMANTIC_FACT' and r['provenance_label']=='NPC_SELF_STATEMENT' for r in records))
        self.assertEqual(2,len({r['episode_id'] for r in records}))
        self.assertEqual(2,len(self.rows('SELECT * FROM memory_evidence')))


if __name__=='__main__':unittest.main()
