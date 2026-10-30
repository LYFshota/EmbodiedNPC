import asyncio
import json
import unittest
from copy import deepcopy
from functools import lru_cache
from unittest.mock import patch

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
from app.relationship_reflection_model import RelationshipReflectionModelOutput, RelationshipReflectionModelResponse
from app.contracts import CognitiveDecision
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


def wire_value(value, schema, definitions=None):
    """Encode a synthetic valid value in the *outbound* shape, including required nulls."""
    definitions = schema.get('$defs', {}) if definitions is None else definitions
    if '$ref' in schema:
        return wire_value(value, {**definitions[schema['$ref'].removeprefix('#/$defs/')],
            **{k:v for k,v in schema.items() if k != '$ref'}}, definitions)
    if 'anyOf' in schema:
        for branch in schema['anyOf']:
            try:
                candidate = wire_value(value, branch, definitions)
                if Draft202012Validator({**branch,'$defs':definitions}).is_valid(candidate):
                    return candidate
            except (ValueError, TypeError):
                pass
        raise ValueError('No outbound branch')
    if schema.get('type') == 'array' and isinstance(value, dict):
        return [{'key': k, 'value': wire_value(v, schema['items']['properties']['value'], definitions)}
                for k, v in value.items()]
    if isinstance(value, dict) and 'properties' in schema:
        result = {k: wire_value(v, schema['properties'][k], definitions) for k, v in value.items()}
        for k in schema.get('required', ()):
            if k not in result:
                field = schema['properties'][k]
                if Draft202012Validator({**field,'$defs':definitions}).is_valid(None):
                    result[k] = None
                else:
                    raise ValueError('Missing nonnullable field: ' + k)
        return result
    if isinstance(value, list) and 'items' in schema:
        return [wire_value(v, schema['items'], definitions) for v in value]
    return value


@lru_cache(maxsize=1)
def npc_contracts():
    """Six real factory outputs; synthetic in-memory context, no config/store/worker."""
    from app.model_profile_probes import synthetic_request
    from app.model_requests import CognitiveModelRequestFactory
    from app.cognitive_step_models import TaskCognitiveStep, TaskTerminalStep, RecallOutcome
    from app.consolidation_model_request import consolidation_request
    from app.memory_consolidation_context import MemoryConsolidationContext, ConsolidationEvidenceEvent, TrustedPersistentIdentity
    from app.relationship_reflection_candidate import RelationshipReflectionCandidate
    from app.relationship_reflection_model import RelationshipReflectionModel
    from app.providers import ModelResult
    from tests.test_action_feedback import make_request
    task = dict(status='OK', tasks=[], available_goal_kinds=['serve_drink', 'clean_assigned_counter'])
    def cognition(request, **kw):
        return CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=32768, **kw)
    evidence = ConsolidationEvidenceEvent(experience_seq=1, event_id='synthetic-event', event_type='dialogue_input',
        source_type='dialogue', actor_entity_id='probe_player', target_entity_ids=('probe_npc',),
        participant_handles={'probe_player':'participant_1', 'probe_npc':'participant_2'}, structured_payload={},
        natural_language_payload='Hello', occurred_at=1., observed_at=1., world_revision=None, confidence=1., provenance='HEARD')
    context = MemoryConsolidationContext('synthetic-episode', 'probe_npc', None, 1, 1, 'test_end', 'synthetic-session',
        (evidence,), (), {}, ('HEARD',), 'a'*64, 'synthetic')
    class Capture:
        name = 'mock'; model = 'synthetic'; context_length = 32768
        async def execute_preemptible_background(self, provider, request):
            self.request = request
            return ModelResult(text='{"outcome":"NO_CHANGE","interpretation":null,"state_update":null}')
    capture = Capture()
    candidate = RelationshipReflectionCandidate(TrustedPersistentIdentity('npc','Character','synthetic-npc'),
        TrustedPersistentIdentity('player','Player','synthetic-player'), 'synthetic-episode', 'a'*64, (evidence,))
    asyncio.run(RelationshipReflectionModel(capture, capture, context_length=32768).interpret(candidate))
    return (
        ('step0', cognition(synthetic_request(), task_context=task), TaskCognitiveStep, {'decision_kind':'NO_ACTION'}),
        ('step1', cognition(synthetic_request(), task_context=task, cognition_phase=1, recall_outcome=RecallOutcome(status='NO_MEMORY_FOUND')),
         TaskTerminalStep, {'decision_kind':'NO_ACTION'}),
        ('continuation', cognition(synthetic_request(True)), DialogueContinuationStep, {'decision_kind':'NO_ACTION'}),
        ('consolidation', consolidation_request(context, (evidence,)), MemoryConsolidationResponse,
         {'episodic_memories':[{'summary':'A greeting','subject_handles':['participant_1'],
            'evidence_event_ids':['synthetic-event'],'importance':.5,'confidence':.8}], 'semantic_facts':[]}),
        ('relationship', capture.request, RelationshipReflectionModelOutput,
         {'outcome':'NO_CHANGE','interpretation':None,'state_update':None}),
        ('task_failure', cognition(make_request()), CognitiveDecision,
         {'decision_kind':'DIRECT_RESPONSE','utterance':'这次没有完成。','dialogue_turn_policy':'WAIT_FOR_PLAYER'}),
    )


class AdapterTests(unittest.TestCase):
    def setUp(self):
        # A missed MockTransport must fail locally, never contact a real endpoint.
        for target in ('httpx.AsyncHTTPTransport.handle_async_request','httpx.HTTPTransport.handle_request'):
            guard=patch(target,side_effect=AssertionError('REAL_NETWORK_FORBIDDEN'))
            guard.start()
            self.addCleanup(guard.stop)

    def config(self, protocol, **kwargs):
        kwargs.setdefault("context_length", DEFAULT_COGNITIVE_CONTEXT_LENGTH)
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
                response = wire_value({"result":{"decision_kind":"NO_ACTION"}} if wrapped else
                                      {"decision_kind":"NO_ACTION"}, outbound)
                self.assertTrue(Draft202012Validator(outbound).is_valid(response))
                validator.model_validate_json(unwrap_text(json.dumps(response), wrapped, schema, profile))
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

    def test_pattern_key_maps_roundtrip_without_sending_unsupported_keywords(self):
        # Real CognitiveActionProposal.bindings uses this patternProperties shape.
        schema = {'type':'object', 'patternProperties':{'^[a-z][a-z0-9_]*$':{'type':'string','minLength':1,'maxLength':120}},
                  'additionalProperties':False,'minProperties':1,'maxProperties':2}
        for profile in ('portable_union','openai_strict','anthropic'):
            with self.subTest(profile=profile):
                projected, wrapped = project_schema(schema, profile)
                self.assertNotIn('patternProperties', json.dumps(projected))
                self.assertEqual(projected['type'], 'array')
                valid = '[{"key":"target","value":"cup_01"}]'
                self.assertEqual(json.loads(unwrap_text(valid, wrapped, schema, profile)), {'target':'cup_01'})
                for invalid in ('[{"key":"INVALID!","value":"cup_01"}]',
                                '[]', '[{"key":"a","value":"x"},{"key":"b","value":"x"},{"key":"c","value":"x"}]',
                                '[{"key":"target","value":4}]',
                                '[{"key":"target","value":"cup_01"},{"key":"target","value":"cup_02"}]'):
                    with self.assertRaises(ProviderError): unwrap_text(invalid, wrapped, schema, profile)

    def test_anthropic_objects_explicitly_forbid_extra_properties(self):
        schema = {'type':'object','properties':{'name':{'type':'string'}},'required':['name']}
        projected, _ = project_schema(schema, 'anthropic')
        self.assertIs(projected['additionalProperties'], False)

    def test_map_projection_refuses_mixed_or_ambiguous_shapes_without_field_loss(self):
        for schema in (
            {'type':'object','properties':{'fixed':{'type':'string'}},'additionalProperties':{'type':'string'}},
            {'type':'object','patternProperties':{'^a':{'type':'string'},'^b':{'type':'integer'}}},
            {'type':'object','patternProperties':{'^a':{'type':'string'}},'additionalProperties':True},
        ):
            original=deepcopy(schema)
            with self.assertRaises(ProviderError):project_schema(schema,'openai_strict')
            self.assertEqual(original,schema)

    def test_gemini_portable_subset_preserves_constraints_in_local_validation(self):
        schema = {'type':'object','properties':{'name':{'type':'string','minLength':2,'pattern':'^[a-z]+$'}},
                  'required':['name'],'additionalProperties':False}
        projected, wrapped = project_schema(schema, 'portable_union')
        self.assertNotIn('minLength', projected['properties']['name'])
        self.assertNotIn('pattern', projected['properties']['name'])
        with self.assertRaises(ProviderError): unwrap_text('{"name":"X"}', wrapped, schema, 'portable_union')

    def official_config(self, preset):
        return MainModelConfig(display_name='Offline official preset',provider_type=preset.protocol,
            base_url=preset.base_url,api_key='synthetic-offline-only',model='synthetic-model',context_length=32768,
            schema_profile=preset.schema_profile,token_parameter=preset.token_parameter,
            thinking_parameter=preset.thinking_parameter,send_temperature=preset.send_temperature,
            health_strategy=preset.health_strategy)

    @staticmethod
    def outbound_schema(preset, payload):
        if preset.preset_id == 'deepseek': return payload['text']['format']['schema']
        if preset.preset_id == 'gemini': return payload['generationConfig']['responseJsonSchema']
        if preset.preset_id == 'claude': return payload['output_config']['format']['schema']
        return payload['response_format']['json_schema']['schema']

    @staticmethod
    def mock_response(preset, text):
        if preset.preset_id == 'deepseek':
            return {'status':'completed','output':[{'type':'message','role':'assistant','content':[{'type':'output_text','text':text}]}]}
        if preset.preset_id == 'gemini': return {'candidates':[{'content':{'parts':[{'text':text}]},'finishReason':'STOP'}]}
        if preset.preset_id == 'claude': return {'content':[{'type':'text','text':text}],'stop_reason':'end_turn'}
        return {'choices':[{'message':{'content':text},'finish_reason':'stop'}]}

    def official_presets(self):
        return [p for p in MAIN_PRESETS if p.preset_id in {'deepseek','openai','gemini','claude','qwen','glm','kimi'}]

    def test_seven_official_presets_six_factory_contracts_http_roundtrip(self):
        for preset in self.official_presets():
            for name, model_request, validator, sample in npc_contracts():
                with self.subTest(provider=preset.preset_id,contract=name):
                    seen=[]
                    provider=build_main_provider(self.official_config(preset))
                    original=deepcopy(model_request.response_schema)
                    outbound=self.outbound_schema(preset,provider.budget_payload(model_request))
                    wrapped=outbound.get('properties',{}).get('result') is not None
                    valid=wire_value({'result':sample} if wrapped else sample,outbound)
                    Draft202012Validator.check_schema(outbound)
                    self.assertTrue(Draft202012Validator(outbound).is_valid(valid))
                    def handler(request):
                        seen.append(request)
                        payload=json.loads(request.content)
                        self.assertEqual(self.outbound_schema(preset,payload),outbound)
                        self.assertEqual(request.method,'POST')
                        suffix='/responses' if preset.preset_id=='deepseek' else '/models/synthetic-model:generateContent' if preset.preset_id=='gemini' else '/messages' if preset.preset_id=='claude' else '/chat/completions'
                        self.assertEqual(str(request.url),preset.base_url+suffix)
                        auth='x-goog-api-key' if preset.preset_id=='gemini' else 'x-api-key' if preset.preset_id=='claude' else 'authorization'
                        self.assertEqual(request.headers[auth], 'synthetic-offline-only' if auth!='authorization' else 'Bearer synthetic-offline-only')
                        self.assertEqual(request.headers['content-type'],'application/json')
                        if preset.preset_id=='claude':self.assertEqual(request.headers['anthropic-version'],'2023-06-01')
                        if preset.preset_id in ('openai','qwen'):self.assertTrue(payload['response_format']['json_schema']['strict'])
                        return httpx.Response(200,json=self.mock_response(preset,json.dumps(valid)))
                    provider=build_main_provider(self.official_config(preset),httpx.MockTransport(handler))
                    response=asyncio.run(provider.generate(model_request))
                    parsed=validator.model_validate_json(response.text)
                    if hasattr(parsed,'to_response'):parsed.to_response()
                    self.assertEqual(len(seen),1)
                    self.assertEqual(model_request.response_schema,original)

    def test_official_gemini_six_schemas_have_no_discriminator_oneof_or_refs(self):
        preset=next(p for p in MAIN_PRESETS if p.preset_id=='gemini')
        provider=build_main_provider(self.official_config(preset))
        for name, request, _, _ in npc_contracts():
            schema=self.outbound_schema(preset,provider.budget_payload(request))
            def walk(node):
                if isinstance(node,dict):
                    self.assertFalse(set(node)&{'discriminator','oneOf','$ref','$defs','const','patternProperties','propertyNames','minLength','maxLength','pattern'})
                    for value in node.values():walk(value)
                elif isinstance(node,list):
                    for value in node:walk(value)
            with self.subTest(contract=name):walk(schema)

    def test_openai_strict_six_schemas_closed_required_and_bounded(self):
        preset=next(p for p in MAIN_PRESETS if p.preset_id=='openai')
        provider=build_main_provider(self.official_config(preset))
        for name, request, _, _ in npc_contracts():
            schema=self.outbound_schema(preset,provider.budget_payload(request));total=0
            self.assertEqual(schema['type'],'object');self.assertNotIn('anyOf',schema)
            def walk(node,depth=0):
                nonlocal total
                if isinstance(node,dict):
                    self.assertFalse(set(node)&{'discriminator','oneOf','patternProperties'})
                    if 'properties' in node:
                        total+=len(node['properties']);self.assertLessEqual(depth,10)
                        self.assertEqual(set(node['required']),set(node['properties']))
                        self.assertIs(node['additionalProperties'],False)
                    for key,value in node.items():walk(value,depth+(key in ('properties','items')))
                elif isinstance(node,list):
                    for value in node:walk(value,depth)
            with self.subTest(contract=name):
                walk(schema);self.assertLessEqual(total,5000)

    def test_official_invalid_json_wrong_branch_and_wrong_type_rejected_without_retry(self):
        request=npc_contracts()[0][1]
        for preset in self.official_presets():
            provider=build_main_provider(self.official_config(preset))
            schema=self.outbound_schema(preset,provider.budget_payload(request))
            wrapped='result' in schema.get('properties',{})
            for text in ('not json',json.dumps({'result':{'decision_kind':'FORGED'}} if wrapped else {'decision_kind':'FORGED'}),
                         json.dumps({'result':{'decision_kind':42}} if wrapped else {'decision_kind':42})):
                calls=[]
                def handler(req):
                    calls.append(req)
                    return httpx.Response(200,json=self.mock_response(preset,text))
                provider=build_main_provider(self.official_config(preset),httpx.MockTransport(handler))
                with self.subTest(provider=preset.preset_id,text=text),self.assertRaises((ProviderError,ValueError)):
                    result=asyncio.run(provider.generate(request))
                    from app.cognitive_step_models import TaskCognitiveStep
                    TaskCognitiveStep.model_validate_json(result.text)
                self.assertEqual(len(calls),1)

    def test_official_http_schema_auth_and_rate_limit_errors_are_single_call(self):
        request=npc_contracts()[0][1]
        for preset in self.official_presets():
            for status,suffix in ((400,'SCHEMA_REJECTED'),(401,'AUTH_FAILED'),(403,'AUTH_FAILED'),(429,'RATE_LIMITED')):
                calls=[]
                def handler(req):
                    calls.append(req)
                    return httpx.Response(status,json={'error':{'message':'unsupported schema synthetic-private-echo',
                        'param':'text.format.schema.oneOf'}})
                provider=build_main_provider(self.official_config(preset),httpx.MockTransport(handler))
                expected=('DEEPSEEK_SCHEMA_UNSUPPORTED' if status==400 else 'DEEPSEEK_'+suffix) if preset.preset_id=='deepseek' else (
                    'GEMINI_' if preset.preset_id=='gemini' else 'ANTHROPIC_' if preset.preset_id=='claude' else 'OPENAI_COMPATIBLE_')+suffix
                with self.subTest(provider=preset.preset_id,status=status),self.assertRaisesRegex(ProviderError,'^'+expected+'$'):
                    asyncio.run(provider.generate(request))
                self.assertEqual(len(calls),1)

    def test_official_six_contracts_still_reject_invalid_business_fields(self):
        for preset in self.official_presets():
            for name,request,validator,_ in npc_contracts():
                invalid=({'episodic_memories':42,'semantic_facts':[]} if name=='consolidation' else
                    {'outcome':'NO_CHANGE','interpretation':42,'state_update':None} if name=='relationship' else
                    {'decision_kind':42})
                config=self.official_config(preset)
                schema=self.outbound_schema(preset,build_main_provider(config).budget_payload(request))
                wire={'result':invalid} if 'result' in schema.get('properties',{}) else invalid
                calls=[]
                def handler(req):
                    calls.append(req)
                    return httpx.Response(200,json=self.mock_response(preset,json.dumps(wire)))
                provider=build_main_provider(config,httpx.MockTransport(handler))
                with self.subTest(provider=preset.preset_id,contract=name),self.assertRaises((ProviderError,ValueError)):
                    result=asyncio.run(provider.generate(request))
                    validator.model_validate_json(result.text)
                self.assertEqual(len(calls),1)

    def test_official_malformed_http_response_and_envelope_never_retry(self):
        for preset in self.official_presets():
            for body in (b'not json',b'{}'):
                calls=[]
                def handler(req):
                    calls.append(req)
                    return httpx.Response(200,content=body)
                provider=build_main_provider(self.official_config(preset),httpx.MockTransport(handler))
                with self.subTest(provider=preset.preset_id,body=body),self.assertRaises(ProviderError):
                    asyncio.run(provider.generate(npc_contracts()[0][1]))
                self.assertEqual(len(calls),1)

    def test_official_generation_parameter_envelopes_and_native_budget_mapping(self):
        request=ModelRequest(messages=(ModelMessage(role='user',content='Synthetic JSON probe'),),
            inference_options=InferenceOptions(temperature=.2,think=False,max_output_tokens=128))
        for preset in self.official_presets():
            with self.subTest(provider=preset.preset_id):
                payload=build_main_provider(self.official_config(preset)).budget_payload(request)
                if preset.preset_id=='gemini':
                    self.assertEqual(payload['generationConfig']['maxOutputTokens'],128)
                    self.assertEqual(payload['generationConfig']['temperature'],.2)
                else:
                    token='max_output_tokens' if preset.preset_id=='deepseek' else preset.token_parameter
                    self.assertEqual(payload[token],128)
                    self.assertFalse(payload['stream'])
                    if preset.send_temperature:self.assertEqual(payload['temperature'],.2)
                    else:self.assertNotIn('temperature',payload)
                if preset.preset_id=='deepseek':self.assertEqual(payload['reasoning'],{'effort':'none'})
                if preset.preset_id=='qwen':self.assertIs(payload['enable_thinking'],False)
                if preset.preset_id in ('glm','kimi'):self.assertEqual(payload['thinking'],{'type':'disabled'})
        for preset in [p for p in self.official_presets() if p.preset_id in ('gemini','claude')]:
            config=self.official_config(preset).model_copy(update={'thinking_budget':2048})
            enabled=request.model_copy(update={'inference_options':InferenceOptions(think=True,max_output_tokens=4096)})
            payload=build_main_provider(config).budget_payload(enabled)
            if preset.preset_id=='gemini':self.assertEqual(payload['generationConfig']['thinkingConfig'],{'thinkingBudget':2048})
            else:self.assertEqual(payload['thinking'],{'type':'enabled','budget_tokens':2048})

    def test_qwen_new_preset_uses_existing_strict_transport_not_raw_pattern_properties(self):
        preset=next(p for p in MAIN_PRESETS if p.preset_id=='qwen')
        self.assertEqual(preset.schema_profile,'openai_strict')

    def test_anthropic_complexity_is_reported_as_known_limit_not_live_pass(self):
        # Claude docs: <=24 optional parameters and <=16 union parameters.
        # Keep all business branches; do not weaken contracts to conceal this blocker.
        preset=next(p for p in MAIN_PRESETS if p.preset_id=='claude')
        provider=build_main_provider(self.official_config(preset));blocked=[]
        for name,request,_,_ in npc_contracts():
            schema=self.outbound_schema(preset,provider.budget_payload(request));optional=unions=0
            def walk(node):
                nonlocal optional,unions
                if isinstance(node,dict):
                    if 'properties' in node:
                        self.assertIs(node['additionalProperties'],False)
                        optional+=len(set(node['properties'])-set(node.get('required',())))
                    unions+=int('anyOf' in node)
                    for value in node.values():walk(value)
                elif isinstance(node,list):
                    for value in node:walk(value)
            walk(schema)
            if optional>24 or unions>16:blocked.append(name)
        self.assertEqual(blocked,['step0','step1'])

    def test_remaining_consumer_schemas_project_without_claiming_live_compatibility(self):
        for validator in (MemoryConsolidationResponse,RelationshipReflectionModelResponse,
                          RelationshipReflectionModelOutput,CognitiveDecision):
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

