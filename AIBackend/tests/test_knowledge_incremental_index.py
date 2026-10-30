"""Vector identity depends on the actual document projection, not management metadata."""
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from app.knowledge_embeddings import (KnowledgeEmbeddingIndex, KnowledgeEmbeddingIndexer,
    KnowledgeHybridRanker, content_hash, embedding_text, knowledge_space)
from app.world_knowledge import KnowledgeAccess, KnowledgeQuery, retrieve_knowledge, compact
from tests.test_knowledge_permissions_usage import entry, make_store, deployment, request_for
from tests.test_world_knowledge import TinyEmbeddings


class IncrementalIndexTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = make_store(self.tmp.name, (entry(),))
        self.index = KnowledgeEmbeddingIndex(self.store, Path(self.tmp.name) / "index.sqlite3")
        self.provider = TinyEmbeddings()
        def query(text):
            self.provider.queries.append(text)
            if self.provider.fail:
                raise OSError("offline")
            return (0, 0, 1)
        self.provider.embed_query = query
        self.worker = KnowledgeEmbeddingIndexer(self.index, self.provider)
        self.ranker = KnowledgeHybridRanker(self.index, self.provider)
        self.principal = deployment(2).resolve(request_for(0))

    def current(self):
        return self.store.snapshot()[2][0]

    def edit(self, **changes):
        current = self.store.snapshot()[1]
        self.store.manage("upsert", expected_revision=current.revision, entry=self.current().model_copy(update=changes))

    def search(self, text="reference", principal=None):
        bundle, current, entries = self.store.snapshot()
        return retrieve_knowledge(entries, principal or self.principal, KnowledgeQuery(text=text),
            bundle_version=bundle.bundle_version, revision=current.revision, ranker=self.ranker, defer_budget=True)

    async def test_management_metadata_and_catalog_version_never_reencode(self):
        self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        original = self.current()
        for changes in (dict(version=25), dict(source="edited source"), dict(updated_at=datetime.now(timezone.utc)),
                        dict(keywords=("other alias",)), dict(related_ids=()),
                        dict(access=KnowledgeAccess(character_ids=["role_0"], deployment_ids=["test"], scene_ids=["scene"]))):
            self.edit(**changes)
            self.assertEqual(content_hash(original, "old"), content_hash(self.current(), "new"))
            self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        catalog = self.store.snapshot()[1]
        self.store.replace(catalog.model_copy(update={"bundle_version": "renamed"}), expected_revision=catalog.revision)
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.assertEqual(len(self.provider.documents), 1)

    async def test_actual_encoder_projection_changes_reencode(self):
        await self.worker.index_missing_or_stale()
        for changes in (dict(name="new name"), dict(content="new reference content"),
                        dict(knowledge_type="new semantic type"), dict(attributes={"flavor": "sweet"})):
            old = self.current()
            self.edit(**changes)
            self.assertNotEqual(embedding_text(old), embedding_text(self.current()))
            self.assertNotEqual(content_hash(old, "test"), content_hash(self.current(), "test"))
            self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        self.assertEqual(len(self.provider.documents), 5)
        unordered = self.current().model_copy(update={"attributes": {"b": 1, "a": {"y": 2, "x": 1}}})
        ordered = unordered.model_copy(update={"attributes": {"a": {"x": 1, "y": 2}, "b": 1}})
        self.assertEqual(content_hash(unordered, "a"), content_hash(ordered, "b"))
        self.assertNotIn("usage", embedding_text(self.current()))

    async def test_constant_toggle_reuses_cache_or_indexes_missing_or_changed_content(self):
        self.edit(usage="constant")
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.assertEqual(self.provider.documents, [])
        self.edit(usage="retrieval")
        self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        self.edit(usage="constant")
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.assertEqual(self.search().items, ())
        self.edit(usage="retrieval")
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.edit(usage="constant", content="changed while constant")
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.edit(usage="retrieval")
        self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        self.assertEqual(len(self.provider.documents), 2)

    async def test_cached_vector_cannot_bypass_current_acl_usage_or_deletion(self):
        await self.worker.index_missing_or_stale()
        self.assertTrue(self.search(text="semantic only").items)
        self.edit(access=KnowledgeAccess(character_ids=["role_1"], deployment_ids=["test"]))
        queries = len(self.provider.queries)
        self.assertEqual(self.search(text="semantic only").items, ())
        self.assertEqual(len(self.provider.queries), queries)
        other = deployment(2).resolve(request_for(1))
        self.assertTrue(self.search(text="semantic only", principal=other).items)
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        self.edit(usage="constant")
        self.assertEqual(self.search(text="semantic only", principal=other).items, ())
        current = self.store.snapshot()[1]
        self.store.manage("delete", expected_revision=current.revision, knowledge_id=self.current().knowledge_id)
        self.assertEqual(self.search(text="semantic only", principal=other).items, ())
        await self.worker.index_missing_or_stale()
        with closing(self.index.connect(readonly=True)) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM knowledge_embeddings").fetchone()[0], 0)

    async def test_legacy_hash_model_projection_and_failed_provider_use_lexical_fallback(self):
        await self.worker.index_missing_or_stale()
        current = self.current()
        legacy = hashlib.sha256(compact(dict(projection_version="knowledge-embedding-text/1",
            bundle_version="test", entry=current.model_dump(mode="json"))).encode()).hexdigest()
        with closing(self.index.connect()) as db, db:
            db.execute("UPDATE knowledge_embeddings SET content_hash=?", (legacy,))
        before = self.index.path.read_bytes()
        self.assertTrue(self.search().items)
        self.assertEqual(self.search("semantic only").items, ())
        self.assertEqual(before, self.index.path.read_bytes())
        self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        self.provider.fail = True
        self.assertTrue(self.search().items)
        self.assertEqual(self.search("semantic only").items, ())
        self.provider.fail = False
        self.provider.model_key = "new-model"
        self.assertEqual(self.search("semantic only").items, ())
        self.assertEqual(await self.worker.index_missing_or_stale(), 1)
        with patch("app.knowledge_embeddings.TEXT_VERSION", "incompatible/2"):
            self.assertEqual(self.search("semantic only").items, ())
            self.assertTrue(self.search().items)
            self.assertEqual(await self.worker.index_missing_or_stale(), 1)

    async def test_switch_to_constant_while_encoding_does_not_publish(self):
        original_embed = self.provider.embed
        def embed(text):
            vector = original_embed(text)
            self.edit(usage="constant")
            return vector
        self.provider.embed = embed
        self.assertEqual(await self.worker.index_missing_or_stale(), 0)
        with closing(self.index.connect(readonly=True)) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM knowledge_embeddings").fetchone()[0], 0)
