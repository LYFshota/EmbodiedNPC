"""Official endpoint parity and strict local validation; no live API calls."""
import json
import unittest
from dataclasses import replace
import httpx
from app.model_compatibility import _CACHE, initial_policy, policy_for, remember
from app.model_config_runtime import build_main_provider
from app.model_provider_presets import preset_for
from app.providers import InferenceOptions, ModelRequest, ProviderError
from tests.test_model_compatibility import config, good, rejection, REQUEST

MAP = {"type": "object", "patternProperties": {"^[a-z]+$": {"type": "integer"}}, "additionalProperties": False}
BRANCH = {"type": "object", "properties": {"kind": {"const": "a"}, "values": MAP}, "required": ["kind", "values"], "additionalProperties": False}
UNION = {"oneOf": [BRANCH, {**BRANCH, "properties": {**BRANCH["properties"], "kind": {"const": "b"}}}], "discriminator": {"propertyName": "kind"}}

class OfficialCustomTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        _CACHE.clear()

    async def test_official_common_json_object_works_without_probe_or_extra_request(self):
        for url, field, value in (
                ("https://api.deepseek.com", "thinking", {"type": "disabled"}),
                ("https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "enable_thinking", False),
                ("https://open.bigmodel.cn/api/paas/v4", "thinking", {"type": "disabled"}),
                ("https://api.z.ai/api/paas/v4", "thinking", {"type": "disabled"})):
            with self.subTest(url=url):
                seen = []
                def send(r):
                    b = json.loads(r.content); seen.append(b)
                    if b["response_format"] != {"type": "json_object"}:
                        return rejection("response_format", "Unsupported json_schema")
                    return good()
                c = config(base_url=url, model="arbitrary-model-id")
                self.assertEqual(json.loads((await build_main_provider(c, httpx.MockTransport(send)).generate(REQUEST)).text), {"ok": True})
                self.assertEqual(len(seen), 1)
                self.assertEqual(seen[0][field], value)
                self.assertEqual(seen[0]["max_tokens"], 128)
                self.assertEqual(seen[0]["temperature"], 0)
                self.assertEqual(c.base_url, url)
                self.assertEqual(seen[0]["model"], c.model)

    def test_official_regions_and_default_https_port(self):
        for host in ("dashscope.aliyuncs.com", "dashscope-intl.aliyuncs.com", "dashscope-us.aliyuncs.com", "cn-hongkong.dashscope.aliyuncs.com",
                     "ws-123.cn-beijing.maas.aliyuncs.com", "ws-123.ap-southeast-1.maas.aliyuncs.com", "ws.eu-central-1.maas.aliyuncs.com", "trial.cn-hongkong.maas.aliyuncs.com"):
            for port in ("", ":443"):
                self.assertEqual(initial_policy(config(base_url="https://" + host + port + "/compatible-mode/v1/")).thinking_parameter, "enable_thinking")
        for url in ("https://api.deepseek.com:443", "https://api.deepseek.com:443/v1"):
            self.assertEqual(initial_policy(config(base_url=url)).response_format, "json_object")
        self.assertEqual(initial_policy(config(base_url="https://api.openai.com:443/v1")).token_parameter, "max_completion_tokens")

    def test_unknown_gateways_native_paths_and_spoofed_official_hosts_are_not_inferred(self):
        for url in ("https://api.deepseek.com:444", "http://api.deepseek.com", "https://api.deepseek.com.evil.test/v1",
                    "https://ws.cn-beijing.maas.aliyuncs.com.evil.test/compatible-mode/v1", "https://dashscope.aliyuncs.com/api/v1",
                    "https://a.b.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "https://ws.unknown.maas.aliyuncs.com/compatible-mode/v1",
                    "https://open.bigmodel.cn/api/coding/paas/v4", "https://generativelanguage.googleapis.com/v1beta", "https://sta1n.ai/v1"):
            self.assertEqual(initial_policy(config(base_url=url, model="deepseek-v4.1-flash")).thinking_parameter, "none")

    async def test_auto_strict_wire_envelope_matches_preset_and_restores_internal_maps(self):
        c = config(base_url="https://api.openai.com/v1")
        request = ModelRequest(messages=REQUEST.messages, response_schema=UNION, inference_options=REQUEST.inference_options)
        seen = []
        wire = {"result": {"kind": "a", "values": [{"key": "valid", "value": 2}]}}
        def send(r):
            seen.append(json.loads(r.content)); return good(json.dumps(wire))
        provider = build_main_provider(c, httpx.MockTransport(send))
        measured = provider.budget_payload(request)
        result = await provider.generate(request)
        self.assertEqual(json.loads(result.text), {"kind": "a", "values": {"valid": 2}})
        self.assertEqual(measured, seen[0])
        self.assertIn("follow that wire schema", measured["messages"][0]["content"])
        self.assertNotIn("Return only a JSON object conforming exactly", measured["messages"][0]["content"])
        preset = preset_for("main", "openai")
        manual = build_main_provider(config(base_url=preset.base_url, schema_profile=preset.schema_profile,
            token_parameter=preset.token_parameter, thinking_parameter=preset.thinking_parameter, send_temperature=preset.send_temperature)).budget_payload(request)
        for field in ("response_format", "max_completion_tokens", "temperature", "model"):
            self.assertEqual(measured[field], manual[field])
        self.assertIn("result", measured["response_format"]["json_schema"]["schema"]["properties"])

    async def test_json_object_pattern_maps_are_validated_without_transport_decoding(self):
        for schema in (MAP, UNION):
            valid = {"valid": 2} if schema is MAP else {"kind": "a", "values": {"valid": 2}}
            invalid = {"INVALID": 2} if schema is MAP else {"kind": "a", "values": {"valid": "wrong type"}}
            for body, succeeds in ((valid, True), (invalid, False)):
                c = config(base_url="https://api.deepseek.com")
                provider = build_main_provider(c, httpx.MockTransport(lambda r: good(json.dumps(body))))
                request = ModelRequest(messages=REQUEST.messages, response_schema=schema, inference_options=REQUEST.inference_options)
                if succeeds:
                    self.assertEqual(json.loads((await provider.generate(request)).text), valid)
                else:
                    with self.assertRaisesRegex(ProviderError, "MODEL_SCHEMA_ENVELOPE_INVALID"):
                        await provider.generate(request)

    async def test_strict_projected_map_constraints_still_apply_after_restoration(self):
        c = config(base_url="https://api.openai.com/v1")
        for entries in ([{"key": "INVALID", "value": 1}], [{"key": "a", "value": 1}, {"key": "a", "value": 2}]):
            provider = build_main_provider(c, httpx.MockTransport(lambda r: good(json.dumps({"result": {"kind": "a", "values": entries}}))))
            with self.assertRaisesRegex(ProviderError, "MODEL_SCHEMA_ENVELOPE_INVALID"):
                await provider.generate(ModelRequest(messages=REQUEST.messages, response_schema=UNION, inference_options=REQUEST.inference_options))

    async def test_explicit_official_overrides_and_preset_transport_stay_unchanged(self):
        for name in ("qwen", "glm", "openai"):
            preset = preset_for("main", name)
            c = config(base_url=preset.base_url, schema_profile=preset.schema_profile, token_parameter=preset.token_parameter,
                       thinking_parameter=preset.thinking_parameter, send_temperature=preset.send_temperature)
            p = build_main_provider(c)
            self.assertEqual(p.budget_payload(REQUEST)["response_format"]["type"], "json_schema")
            self.assertEqual(p._policy().schema_profile, preset.schema_profile)
        c = config(base_url="https://api.deepseek.com", schema_profile="raw", thinking_parameter="none", token_parameter="max_completion_tokens", send_temperature=False)
        b = build_main_provider(c).budget_payload(REQUEST)
        self.assertEqual(b["response_format"]["type"], "json_schema")
        self.assertNotIn("thinking", b); self.assertNotIn("temperature", b)
        self.assertEqual(b["max_completion_tokens"], 128)

    async def test_enabled_bounded_reasoning_remains_usable_on_next_turn(self):
        c = config(base_url="https://api.openai.com/v1", thinking=True)
        p = build_main_provider(c, httpx.MockTransport(lambda r: good(reasoning_content="synthetic reasoning")))
        options = InferenceOptions(think=True, max_output_tokens=128)
        for _ in range(2):
            prepared = p.cognitive_options(options, 32768)
            await p.generate(REQUEST.model_copy(update={"inference_options": prepared}))
            self.assertEqual(prepared.output_token_reserve, 128)
        with self.assertRaisesRegex(ProviderError, "THINKING_CONTROL_UNVERIFIED"):
            p.cognitive_options(InferenceOptions(think=False, max_output_tokens=128), 32768)

    def test_official_thinking_with_unverified_total_bound_still_stops(self):
        for url in ("https://api.deepseek.com", "https://open.bigmodel.cn/api/paas/v4", "https://dashscope.aliyuncs.com/compatible-mode/v1"):
            with self.assertRaisesRegex(ProviderError, "TOTAL_GENERATION_BOUND_UNVERIFIED"):
                build_main_provider(config(base_url=url)).cognitive_options(InferenceOptions(think=True, max_output_tokens=128), 32768)

    async def test_official_probe_cache_is_exact_and_does_not_override_manual_choice(self):
        c = config(base_url="https://api.openai.com/v1")
        adapted = replace(initial_policy(c), send_temperature=False, response_format="json_object", schema_profile="raw")
        remember(c, adapted)
        self.assertEqual(policy_for(c), adapted)
        for field, value in (("api_key", "changed"), ("model", "changed"), ("base_url", "https://api.openai.com:443/v1"), ("schema_profile", "openai_strict"), ("send_temperature", True)):
            self.assertNotEqual(policy_for(c.model_copy(update={field: value})), adapted)
        self.assertEqual(build_main_provider(c)._policy(), adapted)
        _CACHE.clear()
        self.assertEqual(build_main_provider(c)._policy(), initial_policy(c))

    async def test_official_custom_cognitive_entry_uses_real_factory_and_strict_validation(self):
        from app.cognitive_entry import CognitiveBackendEntry
        from app.model_profile_probes import synthetic_request
        from app.local_model_scheduler import PrioritizedModelExecutor
        from app.cognitive_step_models import DirectResponseStep
        decision = DirectResponseStep.model_validate(dict(decision_kind="DIRECT_RESPONSE", utterance="Hello.", episode_directive="END")).model_dump(mode="json")
        for url in ("https://api.deepseek.com", "https://api.openai.com/v1", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://open.bigmodel.cn/api/paas/v4"):
            with self.subTest(url=url):
                calls = []
                def send(r):
                    body = json.loads(r.content); calls.append(body)
                    value = {"result": decision} if body["response_format"]["type"] == "json_schema" else decision
                    return good(json.dumps(value))
                provider = build_main_provider(config(base_url=url), httpx.MockTransport(send))
                executor = PrioritizedModelExecutor()
                try:
                    result = await CognitiveBackendEntry(provider, executor, context_length=32768,
                        inference_options=InferenceOptions(think=False, max_output_tokens=512)).run(synthetic_request())
                    self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
                    self.assertEqual(len(calls), 1)
                    self.assertEqual(calls[0].get("max_tokens", calls[0].get("max_completion_tokens")), 512)
                finally:
                    await executor.close()

    async def test_official_custom_consolidation_uses_real_request_and_temporary_evidence(self):
        from app.base_memory_consolidator import BaseMemoryConsolidator
        from app.local_model_scheduler import PrioritizedModelExecutor
        from tests.test_consolidation_evidence_projection import ConsolidationEvidenceProjectionTests
        fixture = ConsolidationEvidenceProjectionTests(); fixture.setUp()
        try:
            context = fixture.resolver.resolve(fixture.fixture(observations=0, kinds=("dialogue_input",)))
            for url in ("https://api.deepseek.com", "https://api.openai.com/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://open.bigmodel.cn/api/paas/v4"):
                with self.subTest(url=url):
                    calls = []
                    def send(r):
                        calls.append(json.loads(r.content))
                        return good('{"episodic_memories":[],"semantic_facts":[]}')
                    provider = build_main_provider(config(base_url=url), httpx.MockTransport(send))
                    executor = PrioritizedModelExecutor()
                    try:
                        result = await BaseMemoryConsolidator(provider, executor, context_length=32768).consolidate(context)
                        self.assertEqual(result.semantic_facts, ())
                        self.assertEqual(len(calls), 1)
                        self.assertIn("episodic_memories", json.dumps(calls[0]))
                    finally:
                        await executor.close()
        finally:
            fixture.doCleanups()
