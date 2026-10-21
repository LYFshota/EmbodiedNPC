"""Only P2's gate, local-time and prompt boundaries; no app.main or real inference."""
import asyncio
from datetime import datetime, timedelta, timezone
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import os

from app.builtin_embedding_provider import BuiltinEmbeddingProvider
from app.canonical_embeddings import CanonicalEmbeddingIndex
from app.embedding_index_control import EmbeddingControl, ManagedEmbeddingIndexer
from app.model_requests import CognitiveModelRequestFactory, trusted_local_time
from tests import test_memory_d1a_retrieval as fixtures
from tests.test_world_knowledge import official_merchant_request


class PlayerExperienceP2Tests(unittest.IsolatedAsyncioTestCase):
    async def test_active_builtin_worker_starts_pending_only_and_preserves_pause_and_cloud_consent(self):
        fixture = fixtures.CanonicalRetrievalTests(methodName="runTest")
        fixture.setUp(); self.addCleanup(fixture.doCleanups)
        fixture.add()
        with tempfile.TemporaryDirectory() as temp:
            adapter = BuiltinEmbeddingProvider(Path(temp))
            adapter.embed = Mock(return_value=tuple([1] + [0] * 767))
            gate = EmbeddingControl(adapter, Path(temp)/"local.json", cloud=False, auto_start_builtin=True)
            self.assertEqual("running", gate.mode)
            self.assertEqual("unloaded", adapter.provider_status)  # Not false model/index Ready.
            index = CanonicalEmbeddingIndex(fixture.fixture.database.connect)
            worker = ManagedEmbeddingIndexer(index, gate)
            worker.start()
            async def completed():
                while index.pending(gate.model_key, 8, 768):
                    await asyncio.sleep(0)
            try:
                await asyncio.wait_for(completed(), 3)
                self.assertEqual(0, await worker.index_missing_or_stale())
                adapter.embed.assert_called_once()
            finally:
                await worker.stop()
            gate.command("pause")
            self.assertEqual("paused", EmbeddingControl(adapter, Path(temp)/"local.json",
                cloud=False, auto_start_builtin=True).mode)
            cloud = EmbeddingControl(adapter, Path(temp)/"cloud.json", cloud=True, auto_start_builtin=True)
            self.assertEqual("waiting", cloud.mode)
            with self.assertRaisesRegex(ValueError, "CLOUD_CONFIRMATION_REQUIRED"):
                cloud.command("start")
            self.assertEqual("waiting", EmbeddingControl(adapter, Path(temp)/"inactive.json", cloud=False).mode)

    def test_fixed_local_time_is_fresh_and_has_explicit_timezone_and_no_world_authority(self):
        before = datetime(2026, 10, 17, 23, 59, tzinfo=timezone(timedelta(hours=8)))
        after = before + timedelta(minutes=2)
        first = trusted_local_time(before); second = trusted_local_time(after)
        self.assertIn("2026-10-17 23:59, weekday=6", first)
        self.assertIn("UTC+08:00, daypart=晚上", first)
        self.assertIn("2026-10-18 00:01, weekday=7", second)
        self.assertIn("daypart=凌晨", second)
        self.assertIn("UTC-05:30", trusted_local_time(before.astimezone(timezone(-timedelta(hours=5, minutes=30)))))
        request = official_merchant_request("现在几点？")
        with patch("app.model_requests.trusted_local_time", side_effect=[first, second]):
            a = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384)
            b = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=16384)
        self.assertIn(first, a.messages[0].content); self.assertIn(second, b.messages[0].content)
        self.assertIn("Questions and binary choices are optional", a.messages[0].content)
        self.assertIn("does not establish cafe opening hours", first)
        self.assertEqual(a.response_schema, b.response_schema)
        self.assertEqual(CognitiveModelRequestFactory.context_from(a).character, request.context.character)

    def test_four_persona_styles_keep_ages_and_boundaries(self):
        root=Path(__file__).parents[2]/"UnityClient/Assets/CafeDemo/V2/Characters/Personas"
        for name, age, style in (("Merchant",22,"轻快直率"),("RegularCustomer",14,"孩子气"),
                                ("CompanionCustomer",26,"独立观点"),("QuietCustomer",21,"冷幽默")):
            source=(root/f"CafeV2{name}Character.asset").read_text(encoding="utf-8")
            self.assertIn(str(age)+"岁",source); self.assertIn(style,source)
            self.assertIn("boundaries:",source)

    def test_portable_log_prefix_uses_fixed_utc_and_pid(self):
        from tests.test_portable_unified import entry
        fixed=datetime(2026,10,17,16,1,2,tzinfo=timezone.utc)
        for module, message in ((entry,"UNIFIED_START_FAILED"),(entry.owner,"PORTABLE_STOPPED")):
            with patch.object(module,"datetime") as clock, patch.object(module.builtins,"print") as output:
                clock.now.return_value=fixed
                with patch.object(entry,"_diagnostic_log",None):
                    module.print(message)
                text=" ".join(map(str,output.call_args.args))
                self.assertTrue(text.startswith(f"2026-10-17T16:01:02.000Z pid={os.getpid()}"))
                self.assertIn(message,text)


if __name__ == "__main__": unittest.main()
