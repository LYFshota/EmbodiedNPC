"""One unified cognition call over trusted failure evidence; all providers are scripted."""
import copy
import json
import unittest
from pydantic import ValidationError
from app.contracts import CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import ProviderError
from tests.test_execution_context import wire
from tests.test_daily_behavior_cognition import WireScriptedProvider


def make_request(*, alternatives=False, completed=0, language='zh-CN'):
    raw = wire('FAILED')
    state = raw['context']['runtime_state']
    raw['schema_version'] = '1.0'
    raw.pop('task_runtime', None)
    raw['trace_id'] = state['trace_id']
    event = raw['context']['current_event']
    event.update(event_type='action.failed', event_id=state['outcome_id'], trace_id=state['trace_id'],
                 source_entity_id=raw['run']['agent_entity_id'], target_entity_id=raw['run']['agent_entity_id'])
    raw['trigger']['source_event_id'] = state['outcome_id']
    raw['context']['trigger']['source_event_id'] = state['outcome_id']
    raw['recall_identity']['requester_entity_id'] = None
    raw['reply_language'] = language
    goal = dict(type='bring_object', target_entity_id='cup_01', destination_entity_id='player_01') if alternatives else dict(type='prepare_drink',drink_type='latte')
    raw['action_feedback'] = dict(original_request_id='request_original', original_run_id='run_original',
        outcome_id=state['outcome_id'], proposal_id=state['proposal_id'], proposal_revision=0, trace_id=state['trace_id'],
        world_revision=state['world_revision'], completed_actions=completed, failure_code=state['failure_code'],
        detail='The attempt failed; committed effects remain committed. The physical cause is unknown.',
        failed_goal=goal, allowed_alternatives=[dict(alternative_id='bring_cup_02',description='换用另一只允许的杯子',
            goal=dict(type='bring_object',target_entity_id='cup_02',destination_entity_id='player_01'))] if alternatives else [])
    return CognitiveRequest.model_validate(raw)


def reply(**changes):
    return dict(decision_kind='DIRECT_RESPONSE', utterance='这次没做成。你想换一杯还是先聊聊？',
                dialogue_turn_policy='WAIT_FOR_PLAYER', **changes)


class ActionFeedbackTests(unittest.IsolatedAsyncioTestCase):
    async def execute(self, request=None, decision=None):
        provider = WireScriptedProvider([decision or reply()])
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=32768).run(request or make_request())
        self.assertEqual(len(provider.requests), 1)
        self.assertIsNone(result.validated_goal)
        self.assertIsNone(result.task_receipt)
        self.assertIsNone(result.task_execution)
        return result, provider.requests[0]

    async def test_explain_or_clarify_uses_cognition_without_task_or_memory_calls(self):
        result, model = await self.execute()
        self.assertEqual(result.decision.utterance, reply()['utterance'])
        self.assertEqual(model.response_schema['title'], 'CognitiveDecision')
        self.assertTrue({"utterance","dialogue_turn_policy"}.issubset(model.response_schema["required"]))
        self.assertEqual(model.response_schema["properties"]["utterance"]["type"],"string")
        self.assertIn('does not establish a physical cause', model.messages[0].content)
        self.assertIn('intent, not proof', model.messages[0].content)
        self.assertNotIn('MEMORY_RECALL', json.dumps(model.response_schema))

    async def test_exact_unity_alternative_is_selection_only(self):
        result, model = await self.execute(make_request(alternatives=True), reply(recovery_alternative_id='bring_cup_02'))
        self.assertEqual(result.decision.recovery_alternative_id, 'bring_cup_02')
        self.assertEqual(model.response_schema['properties']['recovery_alternative_id']['enum'], [None,'bring_cup_02'])

    async def test_partial_steps_remain_evidence_not_world_effect_replay(self):
        req=make_request(completed=2)
        original=req.action_feedback.model_dump()
        _, model=await self.execute(req)
        self.assertIn('"completed_actions":2', model.messages[1].content)
        self.assertEqual(req.action_feedback.model_dump(), original)

    async def test_unoffered_selection_new_action_and_continuation_are_rejected_once(self):
        for output in (reply(recovery_alternative_id='forged'),reply(target_daily_mode='work'),
                       dict(decision_kind='ACTION_PROPOSAL',action_proposal=dict(action_id='prepare_drink',bindings={'drink_type':'latte'})),
                       dict(decision_kind='MEMORY_RECALL',recall_plan={'semantic_query':'失败'}),
                       dict(reply(),dialogue_turn_policy='MAY_CONTINUE')):
            with self.subTest(output=output):
                p=WireScriptedProvider([output])
                with self.assertRaises((ValueError,ValidationError)):
                    await CognitiveBackendEntry(p,PrioritizedModelExecutor(),context_length=32768).run(make_request())
                self.assertEqual(len(p.requests),1)

    def test_forged_outcome_trace_proposal_and_cancelled_state_rejected_before_provider(self):
        req=make_request(); raw=wire('FAILED')
        # Retain private identity data omitted from ordinary model serialization.
        raw=json.loads(req.model_dump_json()); raw['action_feedback']=req.action_feedback.model_dump(); raw['recall_identity']=req.recall_identity.model_dump()
        for key,value in (('outcome_id','other_outcome'),('trace_id','other_trace'),('proposal_id','other_proposal'),('proposal_revision',2)):
            data=copy.deepcopy(raw);data['action_feedback'][key]=value
            with self.subTest(key=key),self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)
        data=copy.deepcopy(raw);data['context']['runtime_state']['status']='CANCELLED'
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)

    async def test_provider_failure_is_not_retried_in_unified_entry(self):
        class Failing(WireScriptedProvider):
            async def generate(self, request):
                self.requests.append(request)
                raise ProviderError('offline failure')
        p=Failing([])
        with self.assertRaises(ProviderError): await CognitiveBackendEntry(p,PrioritizedModelExecutor(),context_length=32768).run(make_request())
        self.assertEqual(len(p.requests),1)

    async def test_locale_length_and_natural_clarification_preserved(self):
        from app.reply_language import hard_limit
        for language in ('zh-CN','en-US'):
            req=make_request(language=language)
            for size in (hard_limit(language),hard_limit(language)+1):
                p=WireScriptedProvider([dict(reply(),utterance='x'*size)])
                if size<=hard_limit(language):
                    result=await CognitiveBackendEntry(p,PrioritizedModelExecutor(),context_length=32768).run(req)
                    self.assertEqual(len(result.decision.utterance),size)
                else:
                    with self.assertRaises(ValidationError): await CognitiveBackendEntry(p,PrioritizedModelExecutor(),context_length=32768).run(req)
                self.assertEqual(len(p.requests),1)

    def test_original_scope_and_budget_cannot_be_widened(self):
        req=make_request(alternatives=True); raw=json.loads(req.model_dump_json())
        raw['action_feedback']=req.action_feedback.model_dump();raw['recall_identity']=req.recall_identity.model_dump()
        raw['action_feedback']['allowed_alternatives'][0]['goal']['destination_entity_id']='other_player'
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(raw)

    async def test_configured_mock_uses_unified_contract(self):
        from app.model_requests import create_mock_provider
        result=await CognitiveBackendEntry(create_mock_provider(),PrioritizedModelExecutor(),context_length=32768).run(make_request())
        self.assertEqual(result.decision.decision_kind,'DIRECT_RESPONSE')
        self.assertIsNone(result.validated_goal)

    async def test_ordinary_chat_has_no_recovery_selection_capability(self):
        req=CognitiveRequest.model_validate(wire())
        model=CognitiveModelRequestFactory.create_event(req,knowledge_context_limit=32768)
        self.assertNotIn('recovery_alternative_id',json.dumps(model.response_schema))
        p=WireScriptedProvider([reply(recovery_alternative_id='bring_cup_02')])
        with self.assertRaisesRegex(ValueError,'COGNITIVE_RECOVERY_SOURCE_REQUIRED'):
            await CognitiveBackendEntry(p,PrioritizedModelExecutor(),context_length=32768).run(req)
        self.assertEqual(len(p.requests),1)
