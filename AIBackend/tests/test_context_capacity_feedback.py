"""Small capacity-feedback contracts; runner must isolate paths before imports."""
import asyncio
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient
from app.context_capacity import (CapacityCapture, CapacitySigner, context_error, create_capacity_router, recheck_capacity)
from app.context_budget_allocator import AtomicContextGroup, ContextBudgetAllocator, ContextBudgetExceeded, ContextBudgetPolicy
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions, ProviderError
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from tests.test_cognitive_tasks import request, Scripted


class ContextCapacityFeedbackTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.private = Path(tempfile.mkdtemp(prefix="capacity_"))
        self.signer = CapacitySigner(self.private/"capacity.key")
        self.options = InferenceOptions(max_output_tokens=1024)

    async def test_hard_error_recheck_restart_still_small_then_resolved_and_tamper_rejected(self):
        provider = Scripted([dict(decision_kind="NO_ACTION")])
        entry = CognitiveBackendEntry(provider, PrioritizedModelExecutor(),context_length=8192)
        entry._capacity_signer = self.signer
        with self.assertRaises(ContextBudgetExceeded) as failed: await entry.run(request())
        check = failed.exception.context_status.checks[-1]
        self.assertEqual((check.state,len(provider.requests)), ("error",0))
        self.assertEqual(len(check.proof),64)
        self.assertNotIn("拿铁",check.model_dump_json())
        reopened = CapacitySigner(self.signer.path)
        reordered = check.model_copy(update={"counts":dict(reversed(tuple(check.counts.items())))})
        self.assertTrue(reopened.verifies(reordered),"JSON object ordering across C# must not invalidate a diagnostic proof")
        for capacity, resolved in ((8193,False),(32768,True)):
            entry._context_length = capacity
            entry._capacity_signer = reopened
            app=FastAPI();app.include_router(create_capacity_router(entry))
            answer=TestClient(app).post('/v1/context-budget/recheck',json={"checks":[check.model_dump(mode="json")]})
            self.assertEqual(answer.status_code,200,answer.text)
            self.assertEqual(answer.json()['checks'][0]['resolved'],resolved)
        forged=check.model_copy(update={"required_estimate":1})
        self.assertFalse(recheck_capacity([forged],provider,self.options,32768,reopened)[0]['resolved'])
        other=Scripted([]);other.model="different-model"
        self.assertFalse(recheck_capacity([check],other,self.options,32768,reopened)[0]['resolved'])
        provider_error=self.signer.sign(check.model_copy(update={"cause":"provider","proof":""}))
        self.assertFalse(recheck_capacity([provider_error],provider,self.options,32768,reopened)[0]['resolved'])

    async def test_success_is_content_free_scoped_and_does_not_add_model_calls(self):
        provider=Scripted([dict(decision_kind="NO_ACTION")])
        entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=32768)
        result=await entry.run(request())
        self.assertEqual(len(provider.requests),1)
        check=result.context_status.checks[0]
        self.assertEqual((check.state,check.output_reserve,check.estimation_reserve),("ok",1024,3072))
        self.assertTrue(check.provider_verified)
        self.assertNotIn('context_diagnostics',str(provider.requests[0].response_schema))
        other=request(number="2").model_copy(update={"reply_language":"en-US"})
        a=CapacityCapture(other,provider,self.options,32768,self.signer)
        b=CapacityCapture(request(),provider,self.options,32768,self.signer)
        self.assertNotEqual(a.source,b.source)

    def test_only_actual_important_token_drops_warn_not_relevance_or_low_priority(self):
        for kind,priority,warning in (("memory",9,True),("recent",20,True),("task",12,True),("knowledge",10,True),("knowledge",31,False),("relationship",18,False)):
            with self.subTest(kind=kind,priority=priority):
                capture=CapacityCapture(request(),Scripted([]),self.options,8192,self.signer);token=capture.start()
                try:
                    group=AtomicContextGroup(kind+":0","other","中"*2000,priority,0)
                    allocator=ContextBudgetAllocator(ContextBudgetPolicy.cognition(8192,1024))
                    result=allocator.allocate({"mandatory":"中"*5000},(group,),lambda ids:"中"*(5000+(2000 if ids else 0)))
                    self.assertFalse(result.safe_failure)
                    self.assertEqual(capture.status().checks[0].state,"warning" if warning else "ok")
                    self.assertEqual(capture.status().checks[0].counts[kind].dropped,1)
                finally:capture.finish(token)
        capture=CapacityCapture(request(),Scripted([]),self.options,8192,self.signer);token=capture.start()
        try:
            ContextBudgetAllocator(ContextBudgetPolicy.cognition(8192,1024)).allocate({"mandatory":"safe"},(),lambda _:"safe")
            self.assertEqual(capture.status().checks[0].state,"ok")
        finally:capture.finish(token)

    def test_provider_context_codes_are_exact_not_timeout_network_schema_or_body_keywords(self):
        for code,cause in (("DEEPSEEK_CONTEXT_LENGTH_EXCEEDED","provider"),("OPENAI_COMPATIBLE_OUTPUT_RESERVE_EXCEEDS_CONTEXT","output_reserve"),("DEEPSEEK_TIMEOUT",None),("DEEPSEEK_UNAVAILABLE",None),("DEEPSEEK_SCHEMA_UNSUPPORTED",None)):
            with self.subTest(code=code):self.assertEqual(context_error(ProviderError(code)),cause)
        for body,expected in (({"error":{"code":"context_length_exceeded"}},"DEEPSEEK_CONTEXT_LENGTH_EXCEEDED"),({"error":{"message":"context_length_exceeded"}},"DEEPSEEK_BAD_REQUEST")):
            self.assertEqual(DeepSeekOfficialResponsesProvider._http_error(httpx.Response(400,json=body)),expected)

    def test_legacy_api_new_defaults_32k_but_omitted_update_preserves_saved_16k(self):
        from app.model_api_config_api import create_model_api_config_router
        from app.model_api_config_store import ModelApiConfigStore
        from tests.test_model_api_config import main
        store=ModelApiConfigStore(self.private/'models.json');app=FastAPI();app.include_router(create_model_api_config_router(store))
        client=TestClient(app);values=main();values.pop('context_length');values.pop('max_output_tokens')
        def save(revision,settings):return client.put('/v1/model-config',json={'expected_revision':revision,'config':{'main_model':settings,'embedding':{'enabled':False}}})
        self.assertEqual(save(0,values).status_code,200)
        self.assertEqual((store.load().main_model.context_length,store.load().main_model.max_output_tokens),(32768,1024))
        self.assertEqual(save(1,{**values,'context_length':16384}).status_code,200)
        self.assertEqual(save(2,values).status_code,200)
        self.assertEqual(store.load().main_model.context_length,16384)

    async def test_actual_websocket_error_success_and_legacy_negotiation(self):
        from app import m3_main
        from tests.test_cognitive_websocket import SocketHarness
        from tests.test_cognitive_request import _event_request
        for capacity,negotiated in ((8192,True),(32768,True),(32768,False)):
            with self.subTest(capacity=capacity,negotiated=negotiated):
                provider=Scripted([dict(decision_kind="NO_ACTION")])
                entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=capacity)
                entry._capacity_signer=self.signer
                raw=_event_request()
                if negotiated:raw['context_diagnostics_version']=1
                with patch.object(m3_main,'cognitive_entry',entry):
                    socket=SocketHarness();running=asyncio.create_task(m3_main.websocket_endpoint(socket.socket))
                    try:
                        socket.message(raw);result=await socket.result()
                        self.assertEqual('context_status' in result,negotiated,result)
                        if capacity==8192:
                            self.assertEqual(result['diagnostic_category'],'CONTEXT_BUDGET_EXCEEDED')
                            self.assertEqual(result['context_status']['checks'][-1]['state'],'error')
                        else:self.assertEqual(result['message_type'],'cognitive_response',result)
                    finally:socket.disconnect();await asyncio.wait_for(running,3)
