"""Shared local startup warmup, native lifecycle readiness and resource ownership."""
import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import threading
import hashlib
from pathlib import Path
import tempfile
import numpy as np
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app import builtin_embedding_provider as builtin
from app.embedding_index_control import EmbeddingControl

from tests.app_test_isolation import get_test_app


def adapter_fixture(test):
    directory = tempfile.TemporaryDirectory()
    test.addCleanup(directory.cleanup)
    root = Path(directory.name)
    stack = ExitStack()
    test.addCleanup(stack.close)
    files = {}
    for name in builtin.RESOURCE_SPEC['files']:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'local warmup fixture')
        files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    stack.enter_context(patch.object(builtin, 'RESOURCE_SPEC', {**builtin.RESOURCE_SPEC, 'files': files}))
    tokenizer = Mock()
    tokenizer.encode.return_value = SimpleNamespace(ids=[1, 2], attention_mask=[1, 1])
    tokenizer_factory = stack.enter_context(patch('tokenizers.Tokenizer.from_file', return_value=tokenizer))
    session = Mock()
    session.get_providers.return_value = ['CPUExecutionProvider']
    session.get_inputs.return_value = [SimpleNamespace(name=name) for name in ('input_ids', 'attention_mask')]
    session.get_outputs.return_value = [SimpleNamespace(name='sentence_embedding')]
    session.run.return_value = [np.ones((1, 768), dtype=np.float32)]
    session_factory = stack.enter_context(patch('onnxruntime.InferenceSession', return_value=session))
    provider = builtin.BuiltinEmbeddingProvider(root)
    test.addCleanup(provider.close)
    return SimpleNamespace(directory=root, provider=provider, tokenizer=tokenizer, session=session,
                           tokenizer_factory=tokenizer_factory, session_factory=session_factory)

class BuiltinWarmupTests(unittest.TestCase):
    def setUp(self):
        self.fixture = adapter_fixture(self)
        self.provider = self.fixture.provider

    def test_concurrent_warmups_initialize_and_encode_once_then_queries_reuse(self):
        identity = self.provider.model_key
        with ThreadPoolExecutor(max_workers=4) as pool:
            list(pool.map(lambda _: self.provider.warmup(), range(8)))
        self.fixture.session_factory.assert_called_once()
        self.fixture.session.run.assert_called_once()
        self.assertEqual('task: search result | query: warmup', self.fixture.tokenizer.encode.call_args.args[0])
        session = self.provider._session
        self.provider.embed_query('咖啡')
        self.provider.embed('已有记忆')
        self.assertIs(session, self.provider._session)
        self.assertEqual(identity, self.provider.model_key)
        self.assertEqual(3, self.fixture.session.run.call_count)

    def test_failed_inference_is_not_marked_warm(self):
        self.fixture.session.run.side_effect = RuntimeError('private runtime failure')
        with self.assertRaisesRegex(builtin.BuiltinEmbeddingError, '^BUILTIN_EMBEDDING_INFERENCE_FAILED$'):
            self.provider.warmup()
        self.assertFalse(self.provider._warmed)
        self.assertEqual('failed', self.provider.provider_status)

    def test_close_is_terminal_after_warmup(self):
        self.provider.warmup()
        self.provider.close()
        self.assertIsNone(self.provider._session)
        self.assertIsNone(self.provider._tokenizer)
        with self.assertRaisesRegex(builtin.BuiltinEmbeddingError, '^BUILTIN_EMBEDDING_CLOSED$'):
            self.provider.warmup()


class BuiltinWarmupLifespanTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.backend = get_test_app()
        self.fixture = adapter_fixture(self)
        self.provider = self.fixture.provider
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.episode = SimpleNamespace(start=Mock(), request_stop=Mock(), stop=AsyncMock(), _task=None)
        auth = SimpleNamespace(start=Mock(), invalidate=Mock(), stop=Mock())
        self.stack.enter_context(patch.object(self.backend.knowledge_management, 'auth', auth))
        self.stack.enter_context(patch.object(self.backend.knowledge_resolver.store, 'initialize'))
        self.stack.enter_context(patch.object(self.backend, 'model_executor', SimpleNamespace(open=Mock(), close=AsyncMock())))
        self.stack.enter_context(patch.object(self.backend, 'episode_processing_loop', self.episode))
        for name in ('canonical_embedding_indexer', 'knowledge_embedding_indexer', 'memory_maintenance_worker',
                     'relationship_reflection_worker', 'memory_consolidation_worker'):
            self.stack.enter_context(patch.object(self.backend, name, None))
        self.stack.enter_context(patch.object(self.backend, 'provider', SimpleNamespace(name='mock')))

    def select(self, provider):
        self.stack.enter_context(patch.object(self.backend, 'canonical_embedding_provider', provider))
        return self.backend.lifespan(self.backend.app)

    async def test_startup_does_not_yield_or_start_workers_until_real_encoding_finishes(self):
        entered, release = threading.Event(), threading.Event()
        def create(*args, **kwargs):
            entered.set()
            if not release.wait(3):
                raise RuntimeError('test barrier timed out')
            return self.fixture.session
        self.fixture.session_factory.side_effect = create
        scope = self.select(self.provider)
        startup = asyncio.create_task(scope.__aenter__())
        try:
            self.assertTrue(await asyncio.to_thread(entered.wait, 2))
            self.assertFalse(startup.done())
            self.episode.start.assert_not_called()
            release.set()
            await asyncio.wait_for(startup, 3)
            self.assertTrue(self.provider._warmed)
            self.assertEqual('ready', self.provider.provider_status)
            self.fixture.session.run.assert_called_once()
            self.episode.start.assert_called_once()
        finally:
            release.set()
            if not startup.done():
                await startup
            await scope.__aexit__(None, None, None)
        self.assertEqual('closed', self.provider.provider_status)
        self.episode.stop.assert_awaited_once()

    async def test_missing_file_fails_startup_clearly_without_starting_workers(self):
        (self.fixture.directory / 'onnx/model_q4.onnx_data').unlink()
        with self.assertLogs('embodiednpc.backend', level='ERROR') as captured:
            with self.assertRaisesRegex(RuntimeError, '^BUILTIN_EMBEDDING_FILE_MISSING$'):
                async with self.select(self.provider):
                    self.fail('Failed warmup cannot declare the app ready')
        self.assertTrue(any('LOCAL_EMBEDDING_WARMUP_FAILED' in line for line in captured.output))
        self.episode.start.assert_not_called()
        self.assertEqual('closed', self.provider.provider_status)
        self.fixture.session_factory.assert_not_called()

    async def test_inference_failure_fails_startup_and_releases_session(self):
        self.fixture.session.run.side_effect = RuntimeError('private details')
        with self.assertRaisesRegex(RuntimeError, '^BUILTIN_EMBEDDING_INFERENCE_FAILED$'):
            async with self.select(self.provider):
                self.fail('Failed inference must not pass readiness')
        self.episode.start.assert_not_called()
        self.assertIsNone(self.provider._session)
        self.assertEqual('closed', self.provider.provider_status)

    async def test_remote_or_disabled_embedding_is_not_probed(self):
        remote = SimpleNamespace(embed_query=Mock(side_effect=AssertionError('remote request')), close=Mock())
        async with self.select(remote):
            remote.embed_query.assert_not_called()
        remote.close.assert_called_once()
        async with self.select(None):
            self.fixture.session_factory.assert_not_called()

    async def test_existing_pause_or_failure_gate_is_respected(self):
        for mode in ('paused', 'failed', 'waiting'):
            with self.subTest(mode=mode):
                provider = builtin.BuiltinEmbeddingProvider(self.fixture.directory)
                gate = EmbeddingControl(provider, self.fixture.directory / (mode + '.json'), cloud=False)
                gate.mode = mode
                async with self.select(gate):
                    self.assertEqual('unloaded', provider.provider_status)
                    self.fixture.session_factory.assert_not_called()
                    self.assertEqual(mode, gate.mode)
                self.assertEqual('closed', provider.provider_status)

    async def test_startup_cancellation_joins_native_load_before_close(self):
        entered, release = threading.Event(), threading.Event()
        def create(*args, **kwargs):
            entered.set()
            if not release.wait(3):
                raise RuntimeError('test barrier timed out')
            return self.fixture.session
        self.fixture.session_factory.side_effect = create
        scope = self.select(self.provider)
        startup = asyncio.create_task(scope.__aenter__())
        try:
            self.assertTrue(await asyncio.to_thread(entered.wait, 2))
            startup.cancel()
            await asyncio.sleep(.02)
            self.assertFalse(startup.done())
            self.assertNotEqual('closed', self.provider.provider_status)
        finally:
            release.set()
        with self.assertRaises(asyncio.CancelledError):
            await asyncio.wait_for(startup, 3)
        self.assertEqual('closed', self.provider.provider_status)
        self.assertIsNone(self.provider._session)
        self.episode.start.assert_not_called()
