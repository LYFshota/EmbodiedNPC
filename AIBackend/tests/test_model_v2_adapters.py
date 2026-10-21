import asyncio
import json
import unittest
from copy import deepcopy

import httpx
from jsonschema import Draft202012Validator

from app.model_api_config import MainModelConfig, EmbeddingConfig
from app.model_config_runtime import build_main_provider, build_embedding_provider
from app.model_provider_presets import MAIN_PRESETS, EMBEDDING_PRESETS
from app.model_schema_projection import project_schema, unwrap_text
from app.model_profile_probes import contract_probe, discover_profile
from app.cognitive_step_models import CognitiveStepResult, TerminalCognitiveStep, DialogueContinuationStep
from app.providers import InferenceOptions, ModelRequest, ModelMessage, ProviderError
from app.memory_consolidation_models import MemoryConsolidationResponse
from app.relationship_reflection_model import RelationshipReflectionModelResponse
from app.failure_service import ModelTaskFailureResponse


class AdapterTests(unittest.TestCase):
    def config(self, protocol, **kwargs):
        kwargs.setdefault("context_length", 16384)
        return MainModelConfig(display_name="Synthetic", provider_type=protocol, base_url="https://fixture.invalid/v1",
                               api_key="synthetic-adapter-key", model="[full]model/id", **kwargs)

    def test_presets_complete_cloud_defaults_without_keys_or_model_guesses(self):
        self.assertEqual({p.preset_id for p in MAIN_PRESETS}, {"deepseek","qwen","glm","kimi","openai","gemini","claude","openrouter","siliconflow","ollama","custom"})
        self.assertEqual({p.preset_id for p in EMBEDDING_PRESETS}, {"aliyun","openai","gemini","zhipu","ollama","custom","builtin_gemma"})
        self.assertTrue(all(not hasattr(p, "api_key") and not hasattr(p, "model") for p in MAIN_PRESETS))

    def test_formal_schemas_project_without_mutating_internal_and_roundtrip(self):
        for validator in (CognitiveStepResult, TerminalCognitiveStep, DialogueContinuationStep):
            schema = validator.model_json_schema()
            original = deepcopy(schema)
            for profile in ("portable_union", "openai_strict", "anthropic"):
                outbound, wrapped = project_schema(schema, profile)
                Draft202012Validator.check_schema(outbound)
                # Include current nullable fields (including target_daily_mode)
                # for strict transports, while validating the same NO_ACTION.
                response = validator.model_validate({"decision_kind": "NO_ACTION"}).model_dump(mode="json")
                self.assertTrue(Draft202012Validator(outbound).is_valid({"result":response} if wrapped else response))
                validator.model_validate_json(unwrap_text(json.dumps({"result":response} if wrapped else response), wrapped, schema, profile))
            self.assertEqual(schema, original)

    def test_map_transport_restores_and_rejects_duplicate_and_invalid_values(self):
        schema = {"type":"object", "properties":{"bindings":{"type":"object","additionalProperties":{"type":"string","maxLength":4}}},
                  "required":["bindings"],"additionalProperties":False}
        projected, wrapped = project_schema(schema, "openai_strict")
        self.assertEqual(projected["properties"]["bindings"]["type"], "array")
        valid = '{"bindings":[{"key":"a","value":"ok"}]}'
        self.assertEqual(json.loads(unwrap_text(valid, wrapped, schema, "openai_strict")), {"bindings":{"a":"ok"}})
        for value in ('{"bindings":[{"key":"a","value":"longgg"}]}',
                      '{"bindings":[{"key":"a","value":"ok"},{"key":"a","value":"ok"}]}'):
            with self.assertRaises(ProviderError):
                unwrap_text(value, wrapped, schema, "openai_strict")

    def test_remaining_consumer_schemas_project_without_claiming_live_compatibility(self):
        for validator in (MemoryConsolidationResponse,RelationshipReflectionModelResponse,ModelTaskFailureResponse):
            for profile in ("portable_union","openai_strict","anthropic"):
                projected,_=project_schema(validator.model_json_schema(),profile)
                Draft202012Validator.check_schema(projected)

    def test_native_auth_payload_usage_and_unmodified_id(self):
        for protocol, schema_profile in (("gemini_native","portable_union"),("anthropic_native","anthropic")):
            captured = []
            def handler(request):
                captured.append(request)
                if protocol == "gemini_native":
                    return httpx.Response(200,json={"candidates":[{"content":{"parts":[{"text":'{"ok":true}'}]},"finishReason":"STOP"}],
                                                  "usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2}})
                return httpx.Response(200,json={"content":[{"type":"text","text":'{"ok":true}'}],"stop_reason":"end_turn",
                                               "usage":{"input_tokens":4,"output_tokens":2}})
            provider = build_main_provider(self.config(protocol,schema_profile=schema_profile,send_temperature=False), httpx.MockTransport(handler))
            schema = {"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":False}
            result = asyncio.run(provider.generate(ModelRequest(messages=(ModelMessage(role="system",content="Synthetic"),
                ModelMessage(role="user",content="Probe")),response_schema=schema,inference_options=InferenceOptions(max_output_tokens=128))))
            self.assertEqual(json.loads(result.text), {"ok":True})
            self.assertEqual(result.usage.prompt_tokens, 4)
            payload = json.loads(captured[0].content)
            self.assertNotIn("temperature", payload)
            if protocol == "gemini_native":
                self.assertEqual(captured[0].headers["x-goog-api-key"], "synthetic-adapter-key")
                self.assertIn("model%2Fid", str(captured[0].url))
                self.assertIn("responseJsonSchema", payload["generationConfig"])
            else:
                self.assertEqual(payload["model"], "[full]model/id")
                self.assertIn("output_config", payload)
                self.assertEqual(captured[0].headers["x-api-key"], "synthetic-adapter-key")

    def test_http_failure_redacted_and_no_retry(self):
        calls = []
        def handler(request):
            calls.append(request)
            return httpx.Response(429, text="private remote key material")
        provider = build_main_provider(self.config("gemini_native"), httpx.MockTransport(handler))
        with self.assertRaisesRegex(ProviderError, "^GEMINI_RATE_LIMITED$"):
            asyncio.run(provider.generate(ModelRequest(messages=(ModelMessage(role="user",content="Probe"),))))
        self.assertEqual(len(calls),1)

    def test_generation_health_is_explicit_single_call_cached_even_on_failure(self):
        for status in (200,429):
            calls=[]
            def handler(request):
                calls.append(request)
                return httpx.Response(status,json={"choices":[{"message":{"content":'{"ok":true}'}}]})
            provider=build_main_provider(self.config("openai_compatible",health_strategy="generation_once"),httpx.MockTransport(handler))
            async def run():
                first,second=await asyncio.gather(provider.health(),provider.health())
                return first,second,await provider.health()
            results=asyncio.run(run())
            self.assertEqual(len(calls),1)
            self.assertTrue(all(r.available == (status==200) for r in results))

    def test_schema_error_is_classified_without_echoing_upstream_message(self):
        provider=build_main_provider(self.config("openai_compatible"),httpx.MockTransport(lambda r:httpx.Response(400,
            json={"error":{"message":"unsupported discriminator in schema; synthetic-secret-echo"}})))
        with self.assertRaisesRegex(ProviderError,"^OPENAI_COMPATIBLE_SCHEMA_REJECTED$"):
            asyncio.run(provider.generate(ModelRequest(messages=(ModelMessage(role="user",content="Synthetic"),))))

    def test_gemini_health_retrieves_exact_resource_not_first_catalog_page(self):
        seen=[]
        def handler(request):
            seen.append(request)
            return httpx.Response(200,json={"name":"models/canonical-alias-target","supportedGenerationMethods":["generateContent"]})
        provider=build_main_provider(self.config("gemini_native"),httpx.MockTransport(handler))
        self.assertTrue(asyncio.run(provider.health()).available)
        self.assertIn("model%2Fid",str(seen[0].url))

    def test_contract_probe_three_calls_and_remaining_not_run(self):
        calls = []
        def handler(request):
            calls.append(request)
            return httpx.Response(200,json={"choices":[{"message":{"content":'{"decision_kind":"NO_ACTION"}'},"finish_reason":"stop"}]})
        result = asyncio.run(contract_probe(self.config("openai_compatible"), httpx.MockTransport(handler)))
        self.assertTrue(result["success"])
        self.assertEqual(len(calls),3)
        self.assertEqual([r["status"] for r in result["results"]], ["PASS"]*3+["NOT_RUN"]*3)

    def test_discovery_failure_and_manual_do_not_prevent_entry(self):
        config = self.config("openai_compatible")
        result = asyncio.run(discover_profile(config,"main",MAIN_PRESETS[2], httpx.MockTransport(lambda r: self.fail("manual must not call"))))
        self.assertTrue(result["manual_entry_allowed"])
        result = asyncio.run(discover_profile(config,"main",MAIN_PRESETS[-1],httpx.MockTransport(lambda r: httpx.Response(404))))
        self.assertFalse(result["success"])
        self.assertTrue(result["manual_entry_allowed"])

    def test_embedding_native_dimensions_identity_and_invalid_vector(self):
        seen = []
        def handler(request):
            seen.append(json.loads(request.content))
            return httpx.Response(200,json={"embedding":{"values":[3,4]}})
        config = EmbeddingConfig(enabled=True,provider_type="gemini_native",base_url="https://fixture.invalid/v1beta",
                                 api_key="synthetic-adapter-key",model="models/embedding-id",dimensions=2)
        provider = build_embedding_provider(config,httpx.MockTransport(handler))
        self.assertEqual(provider.embed("Synthetic"),(.6,.8))
        self.assertEqual(seen[0]["outputDimensionality"],2)
        for changes in ({"base_url":"https://other.invalid/v1beta"},{"dimensions":3},{"model_version":"digest2"}):
            other = build_embedding_provider(config.model_copy(update=changes),httpx.MockTransport(handler))
            self.assertNotEqual(provider.model_key,other.model_key)
        with self.assertRaises(ValueError):
            build_embedding_provider(config.model_copy(update={"dimensions":3}),httpx.MockTransport(handler)).embed("Synthetic")

