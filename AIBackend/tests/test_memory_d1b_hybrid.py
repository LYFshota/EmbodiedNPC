from __future__ import annotations

import asyncio
from contextlib import closing
import json
import math
import struct
import unittest
from unittest.mock import patch

import httpx

from app.canonical_embeddings import (
    CanonicalEmbeddingIndex, CanonicalEmbeddingIndexer, OllamaEmbeddingProvider,
    content_hash, embedding_text, normalized,
)
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository, lexical_terms
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_memory_ranking import CanonicalMemoryRanker, rrf
from app.canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from tests import test_memory_d1a_retrieval as d1a


class FakeEmbedding:
    model_key = "test:semantic-v1"

    def __init__(self):
        self.calls = []
        self.fail = False

    def embed(self, text):
        self.calls.append(text)
        if self.fail:
            raise RuntimeError("offline")
        return (3.0, 0.0)


class CanonicalHybridTests(unittest.TestCase):
    def setUp(self):
        self.fixture = d1a.CanonicalRetrievalTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.db = self.fixture.fixture.database
        self.index = CanonicalEmbeddingIndex(self.db.connect)
        self.provider = FakeEmbedding()
        self.service = CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(
            self.db.connect, CanonicalMemoryRanker(self.provider)))

    def read(self, query="assistance", **kwargs):
        return self.service.retrieve(observer=d1a.OWNER, semantic_query=query, **kwargs)

    def source(self, memory_id):
        with closing(self.db.connect()) as db:
            return dict(db.execute("SELECT r.* FROM memory_records r "
                "WHERE memory_id=?", (memory_id,)).fetchone())

    def index_row(self, memory_id, vector=(1, 0), model_key=None):
        self.assertTrue(self.index.store_if_current(self.source(memory_id), model_key or self.provider.model_key, vector))

    def test_semantic_only_hit_not_limited_to_lexical_top_n(self):
        summary = "玩家请求送杯子到桌边，店员完成交付后收到感谢。"
        target = self.fixture.add(summary)
        for index in range(4):
            self.fixture.add(f"客人在窗边阅读报纸，片段{index}。")
        self.assertFalse(lexical_terms(summary) & lexical_terms("assistance"))
        self.index_row(target)
        result = self.read(budget=CanonicalRecallBudget(candidate_limit=1, top_k=1))
        self.assertEqual([item.memory_id for item in result.items], [target])
        self.assertEqual(result.eligible_count, 5)
        self.assertEqual(self.provider.calls, ["assistance"])  # No query-time document backfill.

    def test_dense_owner_subject_and_open_scope_isolation(self):
        target = self.fixture.add()
        other_subject = self.fixture.add(uid=d1a.B.persistent_id)
        outsider = self.fixture.add(observer="npc_other", owner="other.private")
        for memory_id, vector in ((target, (.8, .6)), (other_subject, (.9, .4)), (outsider, (1, 0))):
            self.index_row(memory_id, vector)
        scoped = self.read(subject=d1a.A)
        self.assertEqual([i.memory_id for i in scoped.items], [target])
        opened = self.read(subject=None)
        self.assertEqual({i.memory_id for i in opened.items}, {target, other_subject})
        self.assertNotIn(outsider, {i.memory_id for i in opened.items})

    def test_embedding_failure_preserves_lexical_and_profile_exact(self):
        episodic = self.fixture.add()
        profile = self.fixture.add(profile="blue")
        for memory_id in (episodic, profile):
            self.index_row(memory_id)
        self.provider.fail = True
        self.assertEqual([i.memory_id for i in self.read("杯子").items], [episodic])
        exact = self.read("blue", filters=CanonicalReadFilter(memory_type="SEMANTIC_FACT"))
        self.assertEqual([i.memory_id for i in exact.items], [profile])

    def test_missing_stale_wrong_model_corrupt_and_dimension_mismatch_skip(self):
        ids = [self.fixture.add(f"历史杯子记录编号{index}。") for index in range(5)]
        self.index_row(ids[1], model_key="test:other-model")
        self.index_row(ids[2])
        self.index_row(ids[3])
        self.index_row(ids[4], (1, 0, 0))
        with closing(self.db.connect()) as db:
            db.execute("UPDATE memory_embeddings SET content_hash='stale' WHERE memory_id=?", (ids[2],))
            db.execute("UPDATE memory_embeddings SET vector_blob=? WHERE memory_id=?", (b"broken", ids[3]))
            db.commit()
        # Eligible memories exist; missing/corrupt indexes cannot establish absence.
        self.assertEqual(self.read().status, "RECALL_UNAVAILABLE")
        # Missing/invalid vectors still participate in lexical retrieval.
        self.assertEqual({i.memory_id for i in self.read("杯子").items}, set(ids))

    def test_rrf_union_equal_weights_and_stable_ties(self):
        rows = {key: dict(memory_id=key, confidence=.9, importance=.5, learned_at=1) for key in "abc"}
        self.assertEqual(rrf(["a", "b"], ["c", "b"], rows), ["b", "a", "c"])
        self.assertEqual(rrf(["c"], ["a"], rows), ["a", "c"])
        self.assertEqual(rrf(["a"], [], rows), ["a"])

    def test_index_missing_stale_and_failure_never_changes_canonical(self):
        memory_id = self.fixture.add()
        worker = CanonicalEmbeddingIndexer(self.index, self.provider)
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 1)
        with closing(self.db.connect()) as db:
            first_hash = db.execute("SELECT content_hash FROM memory_embeddings").fetchone()[0]
            db.execute("UPDATE memory_records SET summary='新的历史片段' WHERE memory_id=?", (memory_id,))
            db.commit()
        self.assertEqual(asyncio.run(worker.index_missing_or_stale()), 1)
        with closing(self.db.connect()) as db:
            self.assertNotEqual(first_hash, db.execute("SELECT content_hash FROM memory_embeddings").fetchone()[0])
        new_id = self.fixture.add("还有一条尚未索引的记录。")
        before = self.source(new_id)
        self.provider.fail = True
        with self.assertRaises(RuntimeError):
            asyncio.run(worker.index_missing_or_stale())
        self.assertEqual(self.source(new_id), before)
        with closing(self.db.connect()) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM memory_embeddings WHERE memory_id=?", (new_id,)).fetchone()[0], 0)

    def test_embedding_text_hash_and_stale_commit_guard(self):
        memory_id = self.fixture.add(profile="blue")
        row = self.source(memory_id)
        text = embedding_text(row)
        for private in ("merchant.001", "U10001", "npc_01", row["episode_id"], row["job_id"]):
            self.assertNotIn(private, text)
        self.assertIn("favorite_color: blue", text)
        with closing(self.db.connect()) as db:
            db.execute("UPDATE memory_records SET summary='changed' WHERE memory_id=?", (memory_id,))
            db.commit()
        self.assertNotEqual(content_hash(row), content_hash(self.source(memory_id)))
        self.assertFalse(self.index.store_if_current(row, self.provider.model_key, (1, 0)))

    def test_evidence_json_does_not_expose_embedding_details_or_identity(self):
        target = self.fixture.add()
        self.index_row(target)
        output = self.read().model_dump_json()
        for forbidden in ("vector", "cosine", "model_key", "content_hash", "dimension", "persistent_id", "U10001", "merchant.001"):
            self.assertNotIn(forbidden, output)
        self.assertIn('"not_action_authorization":true', output)

    def test_vector_validation_http_adapter_and_float32_corruption(self):
        for vector in ((), (0, 0), (math.nan, 1), (math.inf, 1)):
            with self.assertRaises(ValueError):
                normalized(vector)
        self.assertEqual(normalized((3, 4)), (.6, .8))
        captured = []

        def respond(request):
            captured.append(json.loads(request.content))
            return httpx.Response(200, json={"embeddings": [[3, 4]]})

        real_client = httpx.Client
        with patch("app.canonical_embeddings.httpx.Client", side_effect=lambda **kwargs:
                   real_client(transport=httpx.MockTransport(respond), **kwargs)):
            provider = OllamaEmbeddingProvider("http://local.test", "test-model")
            self.assertEqual(provider.embed("hello"), (.6, .8))
        self.assertEqual(captured, [{"model": "test-model", "input": "hello", "truncate": False}])
        from app.canonical_embeddings import decode
        self.assertIsNone(decode(struct.pack("<f", math.nan), 1))
        self.assertIsNone(decode(b"x", 2))

    def test_indexer_lifecycle_cancels_and_failure_does_not_busy_loop(self):
        async def scenario():
            worker = CanonicalEmbeddingIndexer(self.index, self.provider, interval_seconds=30)
            entered = asyncio.Event()
            calls = []

            async def failing(limit):
                calls.append(limit)
                entered.set()
                raise RuntimeError("offline")

            worker.index_missing_or_stale = failing
            worker.start()
            await asyncio.wait_for(entered.wait(), 1)
            await asyncio.sleep(0)
            await worker.stop()
            self.assertEqual(calls, [8])
            self.assertIsNone(worker._task)
        asyncio.run(scenario())


if __name__ == "__main__":
    unittest.main()
