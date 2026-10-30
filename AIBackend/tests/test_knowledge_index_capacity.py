"""Temporary JSON/SQLite with fake encoders for the expanded catalog."""
from contextlib import closing
from pathlib import Path
import sqlite3
import struct
import tempfile
import unittest

from app.world_knowledge import KnowledgeBundle
from app.world_knowledge_store import KnowledgeOverrides, WorldKnowledgeStore, read_document
from app.knowledge_embeddings import KnowledgeEmbeddingIndex, KnowledgeEmbeddingIndexer, content_hash, knowledge_space
from tests.test_world_knowledge import TinyEmbeddings


class KnowledgeIndexCapacityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.template = self.root / "template.json"
        self.template.write_bytes((Path(__file__).parents[1] / "app/knowledge/cafedemo_v2.json").read_bytes())
        self.bundle = read_document(self.template, KnowledgeBundle)
        self.store = WorldKnowledgeStore(self.root / "current.json", self.template)
        self.index = KnowledgeEmbeddingIndex(self.store, self.root / "index.sqlite3")
        self.provider = TinyEmbeddings()

    def seed_index(self, entries, version, space):
        with closing(self.index.connect()) as db, db:
            db.executemany("INSERT INTO knowledge_embeddings VALUES(?,?,?,?,?)",
                [(e.knowledge_id, space, content_hash(e, version), 3, struct.pack("<3f", 1, 0, 0)) for e in entries])

    def test_migration_reuses_existing_valid_vectors_without_encoding(self):
        edited = self.bundle.entries[0].model_copy(update={"content": "legacy local version", "version": 8})
        self.store.path.write_text(KnowledgeOverrides(revision=9, entries=(edited,)).model_dump_json(), encoding="utf-8")
        space = knowledge_space(self.provider)
        self.seed_index((edited,), self.bundle.bundle_version, space)
        self.store.initialize()
        bundle, current, entries = self.store.snapshot()
        self.assertEqual(current.revision, 9)
        self.assertEqual(self.index.load_valid(entries, bundle.bundle_version, space, 3)[edited.knowledge_id], (1, 0, 0))
        self.assertEqual(self.provider.documents, [])

    def test_3000_ids_under_32_parameter_limit_and_cleanup_preserves_survivors(self):
        current = self.store.initialize()
        entries = tuple(current.entries[0].model_copy(update={"knowledge_id": f"item.{i:05}"}) for i in range(3000))
        self.store.replace(current.model_copy(update={"entries": entries}), expected_revision=0)
        space = knowledge_space(self.provider)
        obsolete = entries[0].model_copy(update={"knowledge_id": "removed"})
        self.seed_index((*entries, obsolete), current.bundle_version, space)
        connect = self.index.connect
        def limited(*, readonly=False):
            db = connect(readonly=readonly)
            db.setlimit(sqlite3.SQLITE_LIMIT_VARIABLE_NUMBER, 32)
            return db
        self.index.connect = limited
        self.assertEqual(set(self.index.load_valid(entries, current.bundle_version, space, 3)), {e.knowledge_id for e in entries})
        subset = (entries[0], entries[-1])
        self.assertEqual(set(self.index.load_valid(subset, current.bundle_version, space, 3)), {e.knowledge_id for e in subset})
        self.assertEqual(self.index.pending(space, None, 3), [])
        with closing(self.index.connect(readonly=True)) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM knowledge_embeddings").fetchone()[0], 3000)
            self.assertIsNone(db.execute("SELECT 1 FROM knowledge_embeddings WHERE knowledge_id='removed'").fetchone())

    async def test_128_long_entries_do_not_starve_later_short_entry(self):
        from app.builtin_embedding_provider import BuiltinEmbeddingProvider
        class CountingBuiltin(BuiltinEmbeddingProvider):
            calls = 0
            def embed(self, text):
                self.calls += 1
                return (1,) * 768
        current = self.store.initialize()
        seed = current.entries[0]
        long_entries = tuple(seed.model_copy(update={"knowledge_id": f"a.long.{i:03}", "content": "长" * 2000,
            "attributes": {"detail": "长" * 1900}}) for i in range(128))
        short = seed.model_copy(update={"knowledge_id": "z.short", "content": "A short reference.", "attributes": {}})
        self.store.replace(current.model_copy(update={"entries": (*long_entries, short)}), expected_revision=0)
        provider = CountingBuiltin(self.root)
        worker = KnowledgeEmbeddingIndexer(self.index, provider)
        self.assertEqual(await worker.index_missing_or_stale(limit=1), 1)
        self.assertEqual(provider.calls, 1)
        self.assertEqual(set(self.index.load_valid((*long_entries, short), current.bundle_version,
            knowledge_space(provider), 768)), {"z.short"})
        self.assertEqual(await worker.index_missing_or_stale(limit=1), 0)
        self.assertEqual(provider.calls, 1)

    def test_foreground_reads_never_create_or_modify_index(self):
        current = self.store.initialize()
        space = knowledge_space(self.provider)
        self.assertEqual(self.index.load_valid(current.entries, current.bundle_version, space), {})
        self.assertFalse(self.index.path.exists())
        self.seed_index((current.entries[0],), current.bundle_version, space)
        before = self.index.path.read_bytes()
        self.index.load_valid(current.entries, current.bundle_version, space, 3)
        self.assertEqual(self.index.path.read_bytes(), before)
        with closing(self.index.connect(readonly=True)) as db:
            with self.assertRaises(sqlite3.OperationalError): db.execute("DELETE FROM knowledge_embeddings")

    async def test_result_deleted_during_encoding_cannot_be_published(self):
        current = self.store.initialize()
        seed = current.entries[0]
        self.store.replace(current.model_copy(update={"entries": (seed,)}), expected_revision=0)
        store = self.store
        class DeletingEncoder(TinyEmbeddings):
            def embed(self, text):
                store.manage("delete", knowledge_id=seed.knowledge_id, expected_revision=1)
                return (1, 0, 0)
        self.assertEqual(await KnowledgeEmbeddingIndexer(self.index, DeletingEncoder()).index_missing_or_stale(), 0)
        self.assertEqual(self.store.snapshot()[2], ())
        with closing(self.index.connect(readonly=True)) as db:
            self.assertEqual(db.execute("SELECT COUNT(*) FROM knowledge_embeddings").fetchone()[0], 0)
