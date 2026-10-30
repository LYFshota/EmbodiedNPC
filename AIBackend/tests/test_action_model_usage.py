"""Registration ownership and exact shared contract matching; no model or storage."""
import unittest
from dataclasses import FrozenInstanceError
from app.action_catalog import BUILT_IN_ACTION_CATALOG, BRING_OBJECT_REGISTRATION
from app.action_contracts import ActionRegistration, binding_contract_signature
from app.contracts import CognitiveActionCapability, CognitiveDecision, CognitiveRequest


def capability(registration):
    d = registration.descriptor
    return CognitiveActionCapability(action_id=d.action_id, description=d.description,
        bindings=[dict(name=b.name, kind=b.kind, required=b.required, enum_values=b.enum_values)
                  for b in d.binding_schema])


class ActionModelUsageRegistrationTests(unittest.TestCase):
    def test_optional_guidance_preserves_existing_registration_and_wire_contract(self):
        registration = ActionRegistration(BRING_OBJECT_REGISTRATION.descriptor)
        self.assertEqual('', registration.model_usage)
        with self.assertRaises(FrozenInstanceError):
            registration.model_usage = 'changed'
        self.assertNotIn('model_usage', registration.descriptor.model_dump())
        for model in (CognitiveRequest, CognitiveDecision):
            self.assertNotIn('model_usage', str(model.model_json_schema()))

    def test_three_skills_own_semantics_not_a_second_parameter_contract(self):
        for action_id in ('prepare_drink', 'bring_object', 'clean_counter'):
            with self.subTest(action_id=action_id):
                registration = BUILT_IN_ACTION_CATALOG.try_get(action_id)
                self.assertTrue(registration.model_usage)
                runtime = capability(registration)
                self.assertEqual(binding_contract_signature(registration.descriptor.binding_schema),
                                 binding_contract_signature(runtime.bindings))
                # Enum definitions live only in the descriptor/runtime contract.
                for binding in registration.descriptor.binding_schema:
                    for enum in binding.enum_values:
                        self.assertNotIn(enum, registration.model_usage)
        self.assertEqual('', BUILT_IN_ACTION_CATALOG.try_get('open_door').model_usage)

    def test_shared_signature_retains_exact_validator_semantics(self):
        registration = BRING_OBJECT_REGISTRATION
        runtime = capability(registration)
        expected = binding_contract_signature(registration.descriptor.binding_schema)
        self.assertEqual(expected, binding_contract_signature(reversed(runtime.bindings)))
        for change in ({'kind':'enum', 'enum_values':('other',)}, {'required':False}, {'name':'other'}):
            values = [b.model_dump() for b in runtime.bindings]
            values[0].update(change)
            altered = CognitiveActionCapability(action_id=runtime.action_id, description=runtime.description,
                                                bindings=values)
            self.assertNotEqual(expected, binding_contract_signature(altered.bindings))
        drink = capability(BUILT_IN_ACTION_CATALOG.try_get('prepare_drink'))
        values = [b.model_dump() for b in drink.bindings]
        values[0]['enum_values'] = tuple(reversed(values[0]['enum_values']))
        altered = CognitiveActionCapability(action_id=drink.action_id, description=drink.description, bindings=values)
        self.assertNotEqual(binding_contract_signature(drink.bindings), binding_contract_signature(altered.bindings))

# Check final Provider input, not only the projected instruction helper.
import itertools
import json
from unittest.mock import patch
from pydantic import ValidationError
from app.action_catalog import ActionCatalog
from app.action_proposal_validator import ActionProposalValidator, ActionProposalValidationError
from app.contracts import CognitiveCapabilityManifest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.model_requests import CognitiveModelRequestFactory
from app.cognitive_step_models import RecallOutcome
from tests.test_daily_behavior_cognition import request as daily_request
from tests.test_prepare_drink import context as drink_context, decision as drink_decision

SKILLS = ('prepare_drink', 'bring_object', 'clean_counter')


def with_actions(request, ids):
    manifest = CognitiveCapabilityManifest(available_actions=[
        capability(BUILT_IN_ACTION_CATALOG.try_get(action_id)) for action_id in ids])
    return request.model_copy(update={'context': request.context.model_copy(update={'capabilities':manifest})})


def body(request, **kwargs):
    adapter = DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid', api_key='offline-only', model='offline')
    model = CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=32768,
        budget_payload_builder=adapter.budget_payload, **kwargs)
    return adapter.budget_payload(model)


def context_json(payload):
    return json.loads(next(line.split(': ',1)[1] for line in payload['input'][1]['content'].splitlines()
        if line.startswith('Cognitive context JSON: ')))


class ActionModelUsageProjectionTests(unittest.TestCase):
    def setUp(self):
        for target in ('socket.socket.connect', 'sqlite3.connect'):
            guard = patch(target, side_effect=AssertionError('Provider projection must be offline/storage-free'))
            guard.start()
            self.addCleanup(guard.stop)

    def assert_usage(self, payload, ids):
        system = payload['input'][0]['content']
        for action_id in SKILLS:
            registration = BUILT_IN_ACTION_CATALOG.try_get(action_id)
            self.assertEqual(action_id in ids, registration.model_usage in system)
            self.assertEqual(int(action_id in ids), system.count('Skill '+action_id+':'))

    def test_all_skill_combinations_both_locales_and_recall_steps_reach_provider(self):
        for mask, language, step1 in itertools.product(range(8), ('zh-CN','en-US'), (False,True)):
            ids = [action for n,action in enumerate(SKILLS) if mask & (1<<n)]
            with self.subTest(ids=ids,language=language,step1=step1):
                req = with_actions(daily_request(),ids).model_copy(update={'reply_language':language})
                original = req.context.model_dump_json()
                payload = body(req, cognition_phase=int(step1), recall_outcome=RecallOutcome(status='NO_MEMORY_FOUND') if step1 else None)
                self.assert_usage(payload,ids)
                projected = context_json(payload)
                self.assertEqual(req.context.capabilities.model_dump(mode='json'),projected['capabilities'])
                self.assertEqual('working',projected['behavior_mode']['mode_id'])
                self.assertEqual('rest',projected['daily_behavior']['current_mode'])
                self.assertEqual(original,req.context.model_dump_json())

    def test_missing_empty_and_incomplete_manifest_do_not_grant_catalog(self):
        req=with_actions(daily_request(),[])
        empty=body(req);self.assert_usage(empty,[])
        missing=req.model_copy(update={'context':req.context.model_copy(update={'capabilities':None})})
        unknown=body(missing);self.assert_usage(unknown,[])
        self.assertEqual([],context_json(empty)['capabilities']['available_actions'])
        self.assertIsNone(context_json(unknown)['capabilities'])
        with self.assertRaises(ValidationError):
            CognitiveCapabilityManifest.model_validate({})
        for current in (req,missing):
            with self.assertRaises(ActionProposalValidationError):
                ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(drink_decision()),current.context)

    def test_mismatched_contract_skips_notes_and_validator_still_rejects(self):
        original=drink_context()
        for change in ('kind','name','required','enum','enum_order','extra','missing'):
            value=original.model_dump(mode='json');bindings=value['capabilities']['available_actions'][0]['bindings']
            if change=='kind': bindings[0].update(kind='entity_ref',enum_values=[])
            if change=='name': bindings[0]['name']='other'
            if change=='required': bindings[0]['required']=False
            if change=='enum': bindings[0]['enum_values']=['tea']
            if change=='enum_order': bindings[0]['enum_values'] = tuple(reversed(bindings[0]['enum_values']))
            if change=='extra': bindings.append(dict(name='extra',kind='entity_ref',required=False))
            if change=='missing': bindings.clear()
            from app.contracts import CognitiveContextSnapshot
            context=CognitiveContextSnapshot.model_validate(value)
            req=daily_request().model_copy(update={'context':context})
            with self.subTest(change=change):
                with self.assertLogs('app.model_requests',level='WARNING') as logs:
                    payload=body(req)
                self.assertIn('ACTION_MODEL_USAGE_CONTRACT_MISMATCH:prepare_drink',' '.join(logs.output))
                self.assert_usage(payload,[])
                self.assertEqual(context.capabilities.model_dump(mode='json'),context_json(payload)['capabilities'])
                with self.assertRaises(ActionProposalValidationError):
                    ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(drink_decision()),context)

    def test_unknown_action_preserves_manifest_and_is_not_authorized_by_notes(self):
        req=with_actions(daily_request(),[])
        manifest=CognitiveCapabilityManifest(available_actions=[dict(action_id='guide_player',description='Unknown runtime action',bindings=[])])
        req=req.model_copy(update={'context':req.context.model_copy(update={'capabilities':manifest})})
        with self.assertLogs('app.model_requests',level='WARNING') as logs:
            payload=body(req)
        self.assertIn('ACTION_MODEL_USAGE_UNAVAILABLE:guide_player',' '.join(logs.output))
        self.assert_usage(payload,[])
        self.assertEqual('guide_player',context_json(payload)['capabilities']['available_actions'][0]['action_id'])
        decision=CognitiveDecision.model_validate(dict(decision_kind='ACTION_PROPOSAL',
            action_proposal=dict(action_id='guide_player',bindings={'target':'player_01'})))
        with self.assertRaises(ActionProposalValidationError):
            ActionProposalValidator().validate_and_adapt(decision,req.context)

    def test_optional_empty_guidance_does_not_remove_existing_action(self):
        registration=ActionRegistration(BRING_OBJECT_REGISTRATION.descriptor)
        catalog=ActionCatalog([registration])
        req=with_actions(daily_request(),['bring_object'])
        with patch('app.model_requests.BUILT_IN_ACTION_CATALOG',catalog):
            payload=body(req)
        self.assert_usage(payload,[])
        from tests.test_action_proposal_validation import _decision
        self.assertEqual('bring_object',ActionProposalValidator(catalog).validate_and_adapt(_decision(),req.context).type)

    def test_disabled_registration_is_not_advertised_or_authorized(self):
        descriptor=BRING_OBJECT_REGISTRATION.descriptor.model_copy(update={'available':False})
        catalog=ActionCatalog([ActionRegistration(descriptor,model_usage='Must not appear')])
        req=with_actions(daily_request(),['bring_object'])
        with patch('app.model_requests.BUILT_IN_ACTION_CATALOG',catalog),self.assertLogs('app.model_requests',level='WARNING'):
            payload=body(req)
        self.assertNotIn('Must not appear',payload['input'][0]['content'])
        from tests.test_action_proposal_validation import _decision
        with self.assertRaises(ActionProposalValidationError):
            ActionProposalValidator(catalog).validate_and_adapt(_decision(),req.context)

    def test_task_protocol_and_history_preserved_without_any_immediate_action(self):
        req=with_actions(daily_request(),[])
        tasks=dict(status='OK',available_goal_kinds=[],tasks=[dict(task_ref='t_saved',version=2,status='waiting',
            goal=dict(kind='serve_drink',drink_type='latte'),trigger_kind='next_visit')],has_more=False)
        history=[dict(role='player',text='下次来的时候做一杯咖啡。'),dict(role='npc',text='旧的拒绝原话必须保留。')]
        for step1 in (False,True):
            with self.subTest(step1=step1):
                payload=body(req,cognition_phase=int(step1),task_context=tasks,task_read_outcome=tasks if step1 else None,recent_dialogue=history)
                self.assert_usage(payload,[])
                system=payload['input'][0]['content'];user=payload['input'][1]['content']
                for text in ('FUTURE TASK CONTRACT','inherits the still-relevant future timing','TaskRead' if not step1 else 'Step 1'):
                    self.assertIn(text,system if text!='TaskRead' else str(payload['text']))
                self.assertIn('旧的拒绝原话必须保留。',user)
                self.assertIn('t_saved',user);self.assertIn('latte',user)
                schema=payload['text']['format']['schema']
                self.assertEqual(not step1,'TaskReadStep' in schema['$defs'])

    def test_no_daily_behavior_keeps_existing_legacy_mode(self):
        req=with_actions(daily_request(),['bring_object'])
        req=req.model_copy(update={'context':req.context.model_copy(update={'daily_behavior':None})})
        payload=body(req)
        self.assertEqual('working',context_json(payload)['behavior_mode']['mode_id'])
        self.assertIsNone(context_json(payload)['daily_behavior'])


class ActionModelUsageRunTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        guard = patch('socket.socket.connect', side_effect=AssertionError('Scripted runs must not use the network'))
        guard.start()
        self.addCleanup(guard.stop)

    async def test_cancel_old_goal_with_no_current_actions_still_one_inference(self):
        from tests.test_immediate_goal_control import request,reply,control
        from tests.test_daily_behavior_cognition import WireScriptedProvider
        from app.cognitive_entry import CognitiveBackendEntry
        from app.local_model_scheduler import PrioritizedModelExecutor
        req=with_actions(request(),[]);provider=WireScriptedProvider([reply(action_control=control())])
        result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=32768).run(req)
        self.assertEqual(1,len(provider.requests));self.assertIsNone(result.validated_goal)
        self.assertEqual('cancel_active_goal',result.decision.action_control.operation)
        system=provider.requests[0].messages[0].content
        self.assertIn('cancel_active_goal',system)
        self.assertNotIn(BRING_OBJECT_REGISTRATION.model_usage,system)

    async def test_saved_task_read_and_cancel_survive_loss_of_immediate_actions(self):
        import tempfile
        from pathlib import Path
        from app.cognitive_entry import CognitiveBackendEntry
        from app.cognitive_tasks import CognitiveTasks
        from app.sqlite_database import SQLiteDatabase
        from app.task_service import TaskService
        from app.task_repository import TaskRepository
        from app.local_model_scheduler import PrioritizedModelExecutor
        from tests.test_cognitive_tasks import request,Scripted,ACCEPT,task_data
        with tempfile.TemporaryDirectory(prefix='model-usage-tasks-') as temp:
            database=SQLiteDatabase(Path(temp)/'private.sqlite3')
            tasks=CognitiveTasks(TaskService(TaskRepository(database.connect)),expected_player_uid='synthetic-task-player')
            async def run(req,scripts):
                provider=Scripted(scripts)
                result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),tasks=tasks,context_length=32768).run(req)
                self.assertEqual(len(scripts),len(provider.requests))
                if not req.context.capabilities.available_actions:
                    self.assertEqual([],task_data(provider.requests[0])['available_goal_kinds'])
                for model in provider.requests:
                    self.assertIn('FUTURE TASK CONTRACT',model.messages[0].content)
                return result
            await run(request(),[ACCEPT])
            def answer(model):
                data=task_data(model);self.assertEqual('latte',data['tasks'][0]['goal']['drink_type'])
                return dict(decision_kind='DIRECT_RESPONSE',utterance='还有下次来时的拿铁约定。',future_commitment=False)
            await run(with_actions(request(number='read'),[]),[
                dict(decision_kind='TASK_READ',task_query=dict(op='query_tasks',statuses=['waiting'])),answer])
            def cancel(model):
                task=task_data(model)['tasks'][0]
                return dict(decision_kind='DIRECT_RESPONSE',utterance='这个约定取消了。',future_commitment=False,
                    task_operation=dict(op='update_task',task_ref=task['task_ref'],expected_version=task['version'],change='cancel',reason='玩家取消原约定'))
            result=await run(with_actions(request(number='cancel'),[]),[cancel])
            self.assertEqual('cancelled',result.task_receipt.task_status)
