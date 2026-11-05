"""Synthetic transport regression; never calls a real service."""
import json
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
import httpx
from fastapi import FastAPI
from fastapi.testclient import TestClient
from app.model_api_config import MainModelConfig
from app.model_compatibility import _CACHE, ProbeBudget, initial_policy, policy_for, remember
from app.model_config_runtime import build_main_provider
from app.model_api_config_api import _test_main
from app.model_profile_api import create_model_profile_router
from app.model_api_config_store import ModelApiConfigStore
from app.providers import ModelRequest, ModelMessage, InferenceOptions, ProviderError
SCHEMA={"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":False}
REQUEST=ModelRequest(messages=(ModelMessage(role="user",content='Return JSON: {"ok":true}'),),response_schema=SCHEMA,inference_options=InferenceOptions(max_output_tokens=128))
def config(**changes):
    return MainModelConfig(**dict(dict(display_name="Synthetic",provider_type="openai_compatible",base_url="https://synthetic.test/v1",api_key="synthetic-secret",model="unfamiliar",schema_profile="auto",token_parameter="auto",thinking_parameter="auto",send_temperature=None,context_length=32768,temperature=0.37),**changes))
def good(content='{"ok":true}',**extra):
    return httpx.Response(200,json={"choices":[{"message":{"content":content,**extra}}],"usage":{"completion_tokens":8}})
def rejection(param,message,status=400):
    return httpx.Response(status,json={"error":{"param":param,"message":message}})
class CompatibilityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):_CACHE.clear()
    async def test_schema_supported_single_call(self):
        seen=[]
        def send(r):seen.append(json.loads(r.content));return good()
        result=await _test_main(config(),httpx.MockTransport(send),probe_budget=ProbeBudget())
        self.assertTrue(result["success"]);self.assertEqual(result["compatibility"]["api_calls"],1)
        self.assertEqual(seen[0]["response_format"]["type"],"json_schema");self.assertEqual(seen[0]["max_tokens"],32)
    async def test_three_explicit_rejections_four_calls_and_runtime_reuse(self):
        seen=[]
        def send(r):
            b=json.loads(r.content);seen.append(b)
            if b["response_format"]["type"]=="json_schema":return rejection("response_format","Unsupported json_schema; use json_object")
            if "max_tokens" in b:return rejection("max_tokens","Unsupported max_tokens, use max_completion_tokens")
            if "temperature" in b:return rejection("temperature","temperature is not supported")
            return good()
        c=config();budget=ProbeBudget();p=build_main_provider(c,httpx.MockTransport(send),probe_budget=budget)
        await p.generate(REQUEST);self.assertEqual(budget.calls,4)
        self.assertEqual(seen[-1]["max_completion_tokens"],128);self.assertNotIn("temperature",seen[-1])
        self.assertEqual(c.temperature,0.37);self.assertEqual(c.max_output_tokens,1024)
        runtime=build_main_provider(c,httpx.MockTransport(send));await runtime.generate(REQUEST)
        self.assertEqual(len(seen),5);self.assertIn("additionalProperties",runtime.budget_payload(REQUEST)["messages"][0]["content"])
    async def test_invalid_json_object_rejected_and_not_cached(self):
        for content in ('{"ok":"yes"}','{"ok":true,"extra":1}','not json','[]'):
            seen=[]
            def send(r):
                b=json.loads(r.content);seen.append(b)
                return rejection("response_format","Unsupported json_schema; use json_object") if b["response_format"]["type"]=="json_schema" else good(content)
            c=config(model=content)
            with self.assertRaises(ProviderError):await build_main_provider(c,httpx.MockTransport(send),probe_budget=ProbeBudget()).generate(REQUEST)
            self.assertEqual(len(seen),2);self.assertEqual(policy_for(c).response_format,"json_schema")
    async def test_rejected_full_schema_tries_json_object_once_then_stops(self):
        seen=[]
        def send(r):seen.append(json.loads(r.content));return rejection("response_format.json_schema.schema.properties.x","Unsupported schema keyword pattern")
        with self.assertRaises(ProviderError):await build_main_provider(config(),httpx.MockTransport(send),probe_budget=ProbeBudget()).generate(REQUEST)
        self.assertEqual(len(seen),2)
        self.assertEqual(seen[-1]["response_format"],{"type":"json_object"})

    async def test_normal_generation_and_manual_overrides_never_retry(self):
        for c,budget in ((config(),None),(config(schema_profile="raw",token_parameter="max_tokens",send_temperature=True),ProbeBudget())):
            seen=[]
            def send(r):seen.append(r);return rejection("response_format","Unsupported json_schema; use json_object")
            with self.assertRaises(ProviderError):await build_main_provider(c,httpx.MockTransport(send),probe_budget=budget).generate(REQUEST)
            self.assertEqual(len(seen),1)
    async def test_auth_limit_context_schema_constraint_and_protocol_errors_stop(self):
        responses=[rejection("response_format","Unsupported json_schema; use json_object",401),rejection("response_format","Unsupported json_schema; use json_object",429),rejection("context","maximum context length is 100"),rejection("other","Bad request"),httpx.Response(200,text="bad json")]
        for response in responses:
            seen=[]
            def send(r):seen.append(r);return response
            with self.assertRaises(ProviderError):await build_main_provider(config(),httpx.MockTransport(send),probe_budget=ProbeBudget()).generate(REQUEST)
            self.assertEqual(len(seen),1)
        for error in (httpx.ReadTimeout("synthetic"),httpx.ConnectError("synthetic")):
            seen=[]
            def send(r):seen.append(r);raise error
            with self.assertRaises(ProviderError):await build_main_provider(config(),httpx.MockTransport(send),probe_budget=ProbeBudget()).generate(REQUEST)
            self.assertEqual(len(seen),1)
    def test_cache_key_invalidates_and_is_bounded(self):
        c=config();p=replace(initial_policy(c),response_format="json_object");remember(c,p);self.assertEqual(policy_for(c),p)
        for key,value in dict(api_key="new",model="new",base_url="https://other.test/v1",temperature=0.9,max_output_tokens=2048,context_length=16384,thinking=True,send_temperature=True).items():self.assertEqual(policy_for(config(**{key:value})).response_format,"json_schema")
        for i in range(150):remember(config(model=str(i)),p)
        self.assertLessEqual(len(_CACHE),128)
    def test_known_rules_do_not_guess_by_model_name(self):
        self.assertEqual(initial_policy(config(base_url="https://api.deepseek.com",model="deepseek-flash")).thinking_parameter,"thinking")
        for url in ("https://gateway.test/v1","http://api.deepseek.com","https://api.deepseek.com.evil.test","https://api.deepseek.com:444"):
            self.assertEqual(initial_policy(config(base_url=url,model="deepseek-flash")).thinking_parameter,"none")
    async def test_reasoning_and_output_bound_enforced(self):
        for style in ("enable_thinking","thinking","reasoning_effort","auto"):
            p=build_main_provider(config(thinking_parameter=style),httpx.MockTransport(lambda r:good(reasoning_content="hidden")))
            with self.assertRaisesRegex(ProviderError,"THINKING_"):await p.generate(REQUEST)
        p=build_main_provider(config())
        with self.assertRaisesRegex(ProviderError,"TOTAL_GENERATION_BOUND_UNVERIFIED"):p.cognitive_options(InferenceOptions(think=True,max_output_tokens=128),32768)
        c=config(thinking=True,token_parameter="max_completion_tokens",thinking_parameter="reasoning_effort")
        p=build_main_provider(c,httpx.MockTransport(lambda r:good(reasoning_content="allowed")))
        opts=p.cognitive_options(InferenceOptions(think=True,max_output_tokens=128),32768);self.assertEqual(opts.output_token_reserve,128)
        await p.generate(REQUEST.model_copy(update={"inference_options":opts}))
        def send(r):return httpx.Response(200,json={"choices":[{"message":{"content":'{"ok":true}'}}],"usage":{"completion_tokens":129}})
        with self.assertRaisesRegex(ProviderError,"OUTPUT_RESERVE_VIOLATED"):await build_main_provider(c,httpx.MockTransport(send)).generate(REQUEST.model_copy(update={"inference_options":opts}))
    async def test_reasoning_only_and_truncated_output_have_safe_distinct_codes(self):
        p=build_main_provider(config(),httpx.MockTransport(lambda r:good(None,reasoning_content="private")))
        with self.assertRaisesRegex(ProviderError,"THINKING_CONTROL_UNVERIFIED"):await p.generate(REQUEST)
        def send(r):return httpx.Response(200,json={"choices":[{"message":{"content":"{"},"finish_reason":"length"}],"usage":{"completion_tokens":128}})
        with self.assertRaisesRegex(ProviderError,"OUTPUT_TRUNCATED"):await build_main_provider(config(),httpx.MockTransport(send)).generate(REQUEST)

    async def test_measured_request_keeps_confirmed_policy_after_cache_eviction(self):
        c=config();remember(c,replace(initial_policy(c),response_format="json_object"))
        seen=[]
        def send(r):seen.append(json.loads(r.content));return good()
        provider=build_main_provider(c,httpx.MockTransport(send))
        request=ModelRequest(messages=REQUEST.messages,response_schema=SCHEMA,inference_options=REQUEST.inference_options)
        measured=provider.budget_payload(request);_CACHE.clear()
        await provider.generate(request)
        self.assertEqual(measured,seen[0]);self.assertNotIn("_transport_state",request.model_dump())

    async def test_missing_models_still_allows_manual_generation(self):
        p=build_main_provider(config(health_strategy="generation_once"),httpx.MockTransport(lambda r:httpx.Response(404) if r.method=="GET" else good()))
        self.assertTrue((await p.health()).available);await p.generate(REQUEST)
class ProfileTests(unittest.TestCase):
    def test_save_reload_activation_and_consent(self):
        with tempfile.TemporaryDirectory() as tmp:
            store=ModelApiConfigStore(Path(tmp)/"config.json");seen=[]
            def send(r):seen.append(r);return good()
            app=FastAPI();app.include_router(create_model_profile_router(store,async_transport=httpx.MockTransport(send)));client=TestClient(app)
            command=dict(expected_revision=0,kind="main",profile_id="main-custom",preset_id="custom",key_action="replace",config=config().model_dump(mode="json"))
            self.assertEqual(client.get("/v2/model-config").json()["default_main_preset_id"],"custom")
            self.assertEqual(client.post("/v2/model-config/probe",json={**command,"operation":"connection"}).status_code,422);self.assertFalse(seen)
            result=client.post("/v2/model-config/probe",json={**command,"operation":"connection","confirm_paid_requests":True})
            self.assertTrue(result.json()["success"]);self.assertNotIn("synthetic-secret",result.text)
            saved=client.post("/v2/model-config/profile",json=command)
            self.assertEqual(saved.status_code,200)
            self.assertIsNone(saved.json()["main_profiles"][0]["config"]["send_temperature"])
            self.assertIsNone(store.load().main_profiles[0].config.send_temperature);self.assertIsNone(store.load().activation)
            self.assertEqual(client.post("/v2/model-config/activate",json={"expected_revision":1,"kind":"main","profile_id":"main-custom"}).status_code,200)
            loaded=ModelApiConfigStore(store.path).load();self.assertEqual(loaded.main_model.schema_profile,"auto");self.assertEqual(loaded.main_model.api_key,"synthetic-secret")
