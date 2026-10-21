"""Targeted vector-first and versioned derived-text regressions; temporary DBs."""
import asyncio
from contextlib import closing
import hashlib
import math
import struct
import unittest
from unittest.mock import patch

import app.canonical_embeddings as em
from app.canonical_memory_ranking import CanonicalMemoryRanker
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from tests import test_memory_d1a_retrieval as d1a


class Provider:
    dimensions=2
    model_key='test:vector-first'
    def __init__(self):self.vector=(1,0);self.fail=False;self.calls=0
    def embed(self,text):
        self.calls+=1
        if self.fail:raise RuntimeError('synthetic unavailable')
        return self.vector


class CanonicalVectorFirstTests(unittest.TestCase):
    def setUp(self):
        self.fixture=d1a.CanonicalRetrievalTests(methodName='runTest');self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.db=self.fixture.fixture.database;self.index=em.CanonicalEmbeddingIndex(self.db.connect)
        self.provider=Provider();self.ranker=CanonicalMemoryRanker(self.provider)
        self.service=CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.db.connect,self.ranker))
    def row(self,i,project=False):
        with closing(self.db.connect()) as db:
            row=dict(db.execute('SELECT r.* FROM memory_records r WHERE memory_id=?',(i,)).fetchone())
            return em._embedding_row(db,row) if project else row
    def index_row(self,i,v=(1,0),key=None):
        self.assertTrue(self.index.store_if_current(self.row(i),key or self.provider.model_key,v))
    def read(self,query='mango',**kw):return self.service.retrieve(observer=d1a.OWNER,semantic_query=query,**kw)
    def authority(self):
        with closing(self.db.connect()) as db:
            return tuple((t,tuple(tuple(r) for r in db.execute(f'SELECT * FROM {t} ORDER BY 1'))) for t in ('memory_records','memory_subjects','memory_evidence','memory_semantic_facts'))

    def test_complete_index_cosine_beats_lexical_conflict(self):
        gold=self.fixture.add('semantic paraphrase');other=self.fixture.add('mango mango keyword')
        self.index_row(gold);self.index_row(other,(.2,.98))
        with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:
            result=self.read()
        self.assertEqual(result.items[0].memory_id,gold)
        self.assertIn('retrieval_mode=vector_first',' '.join(log.output))
        self.assertEqual(result.eligible_count,2)

    def test_semantic_pending_index_and_vector_first_share_one_indexer(self):
        target = self.fixture.add(fact='The speaker wants to learn pottery next year.')
        self.assertEqual(self.index.statistics(self.provider.model_key)['pending'], 1)
        self.assertEqual([i.memory_id for i in self.read('pottery').items], [target])
        self.assertEqual(self.provider.calls, 0)
        before = self.authority()
        self.assertEqual(asyncio.run(em.CanonicalEmbeddingIndexer(self.index, self.provider).index_missing_or_stale()), 1)
        with self.assertLogs('embodiednpc.backend.canonical_retrieval', level='INFO') as log:
            recalled = self.read('a completely different paraphrase')
        self.assertEqual([i.memory_id for i in recalled.items], [target])
        self.assertIn('retrieval_mode=vector_first', ' '.join(log.output))
        self.assertEqual(self.authority(), before)

    def test_partial_index_keeps_unindexed_lexical_and_rebuilds(self):
        gold=self.fixture.add('mango');other=self.fixture.add('ordinary topic')
        self.index_row(other)
        before=self.authority()
        with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:
            r=self.read()
        self.assertIn(gold,[i.memory_id for i in r.items]);self.assertIn(other,[i.memory_id for i in r.items])
        self.assertIn('hybrid_fallback',' '.join(log.output));self.assertIn('index_incomplete',' '.join(log.output))
        self.assertEqual(asyncio.run(em.CanonicalEmbeddingIndexer(self.index,self.provider).index_missing_or_stale()),1)
        with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:self.read()
        self.assertIn('retrieval_mode=vector_first',' '.join(log.output));self.assertEqual(self.authority(),before)

    def test_no_index_and_foreign_space_do_not_encode_query(self):
        gold=self.fixture.add('mango');self.index_row(gold,key='old:space')
        self.assertEqual([i.memory_id for i in self.read().items],[gold]);self.assertEqual(self.provider.calls,0)

    def test_query_failure_invalid_and_zero_vectors_use_lexical(self):
        gold=self.fixture.add('mango');self.index_row(gold)
        self.provider.fail=True;self.assertEqual([i.memory_id for i in self.read().items],[gold])
        self.provider.fail=False
        for vector in ((0,0),(math.nan,1),(math.inf,1),()):
            with self.subTest(vector=vector):
                self.provider.vector=vector
                with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:self.assertEqual([i.memory_id for i in self.read().items],[gold])
                self.assertIn('lexical_fallback',' '.join(log.output))

    def test_provider_unavailable_and_empty_query_keep_exact_profile(self):
        gold=self.fixture.add(profile='mango',attribute='likes')
        self.index_row(gold)
        self.ranker.provider=None
        self.assertEqual([i.memory_id for i in self.read().items],[gold])
        self.ranker.provider=self.provider
        r=self.read('likes',filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT'))
        self.assertEqual([i.memory_id for i in r.items],[gold]);self.assertEqual(self.provider.calls,1)

    def test_mismatched_or_declared_dimension_never_vector_first(self):
        gold=self.fixture.add('mango');self.index_row(gold,(1,0,0))
        for v in ((1,0),(1,0,0)):
            self.provider.vector=v
            with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:self.assertEqual([i.memory_id for i in self.read().items],[gold])
            self.assertIn('dimension_incompatible',' '.join(log.output));self.assertNotIn('retrieval_mode=vector_first',' '.join(log.output))

    def test_zero_negative_cosine_and_illegal_blob_use_lexical(self):
        gold=self.fixture.add('mango')
        for vector in ((0,1),(-1,0)):
            self.index_row(gold,vector)
            with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:self.assertEqual([i.memory_id for i in self.read().items],[gold])
            self.assertIn('no_positive_similarity',' '.join(log.output))
        with closing(self.db.connect()) as db:
            db.execute('UPDATE memory_embeddings SET vector_blob=?',(struct.pack('<2f',math.nan,1),));db.commit()
        self.assertEqual([i.memory_id for i in self.read().items],[gold])
        self.assertEqual(self.index.statistics(self.provider.model_key,2)['pending'],1)

    def test_nonfinite_similarity_defense_and_deterministic_ties(self):
        rows=[dict(memory_id=k,memory_type='EPISODIC',summary='mango',attribute_value=None,confidence=c,importance=i,learned_at=t) for k,c,i,t in
              [('a',.8,.8,3),('b',.9,.5,1),('c',.9,.6,1),('d',.9,.6,2),('e',.9,.6,2)]]
        with patch.object(em.CanonicalEmbeddingIndex,'load_valid',return_value={r['memory_id']:(1,0) for r in rows}):
            r=self.ranker.rank(rows,None,'mango',CanonicalReadFilter(),CanonicalRecallBudget())
            self.assertEqual(r.ids,('d','e','c','b','a'))
        with patch.object(em.CanonicalEmbeddingIndex,'load_valid',return_value={r['memory_id']:(math.inf,0) for r in rows}):
            with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:
                r=self.ranker.rank(rows,None,'mango',CanonicalReadFilter(),CanonicalRecallBudget())
            self.assertEqual(r.ids,('d','e','c','b','a'));self.assertIn('invalid_similarity',' '.join(log.output))

    def test_open_claim_projection_preserves_qualifications_and_canonical(self):
        for fact in ("Previously liked tea; now does not like tea.",
                     "Wants to learn pottery next year if time permits.",
                     "A colleague introduced the speaker to model building."):
            i = self.fixture.add(fact=fact)
            source = self.row(i)
            self.assertEqual(em.embedding_text(self.row(i, True)), fact)
            self.assertEqual(self.row(i), source)
            self.assertEqual(source['memory_type'], 'SEMANTIC_FACT')


    def test_subject_ids_are_redacted_without_adding_relations(self):
        i = self.fixture.add(fact='Wants to learn pottery next year.')
        row = self.row(i, True)
        row['summary'] += ' ' + d1a.A.persistent_id
        text = em.embedding_text(row)
        self.assertNotIn(d1a.A.persistent_id, text)
        self.assertNotIn(d1a.OWNER.persistent_id, text)
        self.assertIn('[redacted]', text)
        self.assertEqual(self.row(i)['summary'], 'Wants to learn pottery next year.')


    def test_npc_self_source_qualifier_and_episodic_unchanged(self):
        from tests.test_dsn_singleplayer import DsnFixture,OWNER
        f=DsnFixture(methodName='runTest');f.setUp();self.addCleanup(f.doCleanups)
        event=f.append('dialogue_response','我喜欢推理小说。')
        # DsnFixture uses the real admission/finalization helper.
        job,context=f.close([event])
        f.finalize(job,context,f.proposal(context,[event],profile=True))
        with closing(f.database.connect()) as db:
            row=dict(db.execute('SELECT r.* FROM memory_records r').fetchone())
            projected=em._embedding_row(db,row)
        self.assertEqual(projected['_embedding_subject_kind'],'npc')
        self.assertIn('NPC曾自述(未独立证实)',em.embedding_text(projected));self.assertEqual(row['provenance_label'],'NPC_SELF_STATEMENT')
        episodic=dict(memory_type='EPISODIC',summary='NPC曾自述（未独立证实）：只说曾游过海峡。')
        self.assertEqual(em.embedding_text(episodic),'NPC曾自述(未独立证实):只说曾游过海峡。')

    def test_version_hash_rebuild_retains_old_space_and_stale_guard(self):
        i=self.fixture.add(profile='tea',attribute='likes');row=self.row(i,True)
        before=self.authority()
        with patch.object(em,'TEXT_VERSION','canonical-embedding-text/1'),patch.object(em,'embedding_text',lambda r:r['summary']):
            oldkey=em.embedding_space_key('test','http://offline.test','fixed',2,'rev')
            self.assertTrue(self.index.store_if_current(self.row(i),oldkey,(1,0)))
        newkey=em.embedding_space_key('test','http://offline.test','fixed',2,'rev')
        self.assertNotEqual(oldkey,newkey)
        with closing(self.db.connect()) as db:
            old=tuple(tuple(r) for r in db.execute('SELECT * FROM memory_embeddings WHERE model_key=?',(oldkey,)))
            self.assertEqual(em.CanonicalEmbeddingIndex.load_valid(db,[row],oldkey),{})
        self.provider.model_key=newkey
        pending=self.index.pending(newkey,8,2);self.assertEqual(len(pending),1)
        self.assertEqual(em.content_hash(pending[0]),hashlib.sha256((em.TEXT_VERSION+'\n'+em.embedding_text(pending[0])).encode()).hexdigest())
        self.assertEqual(asyncio.run(em.CanonicalEmbeddingIndexer(self.index,self.provider).index_missing_or_stale()),1)
        self.assertEqual(self.index.statistics(newkey,2)['pending'],0)
        with closing(self.db.connect()) as db:self.assertEqual(old,tuple(tuple(r) for r in db.execute('SELECT * FROM memory_embeddings WHERE model_key=?',(oldkey,))))
        self.assertEqual(self.authority(),before)
        with closing(self.db.connect()) as db:db.execute("UPDATE memory_records SET summary='changed claim' WHERE memory_id=?",(i,));db.commit()
        self.assertFalse(self.index.store_if_current(pending[0],newkey,(1,0)))

    def test_legacy_provider_keys_version_text_without_overwriting_prior_space(self):
        ollama=em.OllamaEmbeddingProvider('http://offline.test','fixed')
        cloud=em.OpenAICompatibleEmbeddingProvider('http://offline.test/v1',None,'fixed')
        self.assertTrue(ollama.model_key.endswith('|'+em.TEXT_VERSION));self.assertTrue(cloud.model_key.endswith('|'+em.TEXT_VERSION))
        with patch.object(em,'TEXT_VERSION','canonical-embedding-text/1'):
            old_ollama=ollama.model_key;old_cloud=cloud.model_key
        self.assertNotEqual(old_ollama,ollama.model_key);self.assertNotEqual(old_cloud,cloud.model_key)

    def test_vector_first_stays_after_scope_type_source_and_time_filters(self):
        good=self.fixture.add(profile='mango',attribute='likes',clock='backend_utc',occurred=100)
        others=[self.fixture.add(profile='tea',attribute='likes',clock='backend_utc',occurred=10),
                self.fixture.add(profile='pear',attribute='likes',clock='backend_utc',occurred=100,provenance='HEARD'),
                self.fixture.add(profile='mango',attribute='likes',clock='backend_utc',occurred=100,uid=d1a.B.persistent_id),
                self.fixture.add(profile='mango',attribute='likes',clock='backend_utc',occurred=100,owner='other.private',observer='other_npc'),
                self.fixture.add(profile='mango',attribute='dislikes',clock='backend_utc',occurred=100),
                self.fixture.add('mango event',clock='backend_utc',occurred=100)]
        for i in [good,*others]:self.index_row(i)
        with self.assertLogs('embodiednpc.backend.canonical_retrieval',level='INFO') as log:
            r=self.read('mango',subject=d1a.A,filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT',
                provenance=('SYSTEM_CONFIRMED',),occurred_after_utc=90,occurred_before_utc=110))
        self.assertEqual({i.memory_id for i in r.items},{good,others[4]});self.assertEqual(r.eligible_count,2)
        self.assertIn('retrieval_mode=vector_first',' '.join(log.output));self.assertTrue(r.items[0].temporal_filter_applied)
        self.assertTrue(r.not_current_world_state and r.not_instruction and r.not_action_authorization)

    def test_vector_first_keeps_lifecycle_and_evidence_budget(self):
        old=self.fixture.add(profile='Suzhou',attribute='residence',clock='backend_utc',occurred=10)
        current=self.fixture.add(profile='Hangzhou',attribute='residence',clock='backend_utc',occurred=100)
        with closing(self.db.connect()) as db:
            db.execute("UPDATE memory_records SET status='SUPERSEDED' WHERE memory_id=?", (old,));db.commit()
        self.index_row(old);self.index_row(current)
        r=self.read('city',subject=d1a.A,filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT'))
        self.assertEqual([i.memory_id for i in r.items],[current]);self.assertEqual(r.items[0].lifecycle_status,'ACTIVE')
        history=self.read('city',subject=d1a.A,filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT',lifecycle='ALL_HISTORY'))
        self.assertEqual({i.memory_id for i in history.items},{old,current});self.assertEqual({i.lifecycle_status for i in history.items},{'ACTIVE','SUPERSEDED'})
        tiny=self.read('city',subject=d1a.A,filters=CanonicalReadFilter(memory_type='SEMANTIC_FACT'),budget=CanonicalRecallBudget(total_evidence_chars=512))
        self.assertEqual(tiny.status,'EVIDENCE_BUDGET_EXHAUSTED');self.assertFalse(tiny.items);self.assertLessEqual(tiny.char_budget_used,512)


if __name__=='__main__':unittest.main()
