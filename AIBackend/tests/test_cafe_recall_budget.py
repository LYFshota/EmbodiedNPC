"""Frozen full Cafe request at the final provider boundary, not a semantic model stub."""
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import unittest
from unittest.mock import patch

from app.cognitive_step_models import RecallOutcome
from app.context_budget_allocator import ContextBudgetExceeded, ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.contracts import CognitiveRequest
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.model_requests import CognitiveModelRequestFactory, trusted_local_time
from app.providers import InferenceOptions, OllamaProvider
from app.recent_dialogue_context import RecentDialogueTimeline
from app.relationship_context import RelationshipContext
from app.world_knowledge import KnowledgeContext
from app.relationship_context import RelationshipContext
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'))


class CafeRecallBudgetTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads((Path(__file__).parent / 'fixtures/cafe_16k_recall_budget.json').read_text(encoding='utf-8'))
        self.request = CognitiveRequest.model_validate(self.fixture['request'])
        self.kwargs = copy.deepcopy(self.fixture['kwargs'])
        self.kwargs['cognition_phase'] = 1
        for key, cls in (('recall_outcome', RecallOutcome), ('relationship_context', RelationshipContext),
                         ('knowledge_context', KnowledgeContext), ('inference_options', InferenceOptions)):
            if self.kwargs.get(key) is not None:
                self.kwargs[key] = cls.model_validate(self.kwargs[key])
        self.kwargs['recent_dialogue'] = RecentDialogueTimeline(self.kwargs['recent_dialogue'],
            **self.fixture['recent_metadata'])
        # Keep wall-clock wording stable; capacity/provenance/source data stay real.
        timer = patch('app.model_requests.trusted_local_time', return_value=trusted_local_time(
            datetime(2026, 10, 22, 20, 0, tzinfo=timezone(timedelta(hours=8)))))
        timer.start()
        self.addCleanup(timer.stop)

    def build(self, *, builder=None, **changes):
        with self.assertLogs('embodiednpc.backend.context_budget', level='INFO') as logs:
            model = CognitiveModelRequestFactory.create_event(self.request,
                **{**self.kwargs, 'budget_payload_builder': builder, **changes})
        allocation = json.loads(next(r.message.split('=', 1)[1] for r in logs.records
                                     if r.message.startswith('context_budget=')))
        wire = builder(model) if builder else model.model_dump(mode='json')
        self.assertEqual(ContextTokenEstimator().estimate_text(encode(wire)), allocation['estimated_total'])
        self.assertLessEqual(allocation['estimated_total'], allocation['input_hard_cap'])
        self.assertLessEqual(allocation['estimated_total'] + allocation['reserved_output_tokens'] +
                             allocation['reserved_estimation_tokens'], changes.get('knowledge_context_limit', 16384))
        self.assertEqual(self.request.context, CognitiveModelRequestFactory.context_from(model))
        return model, allocation

    def outcome(self, model):
        text = model.messages[-1].content.split('HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ', 1)[1]
        return RecallOutcome.model_validate(json.JSONDecoder().raw_decode(text)[0])

    def test_full_cafe_16k_delivers_the_entire_recalled_item_on_each_wire(self):
        providers = (None, OllamaProvider('http://unused.invalid', 'qwen3.5:4b', 1, context_length=16384),
            DeepSeekOfficialResponsesProvider(base_url='https://unused.invalid', api_key='offline', model='deepseek-flash'))
        original = encode(self.fixture)
        for provider in providers:
            with self.subTest(provider=type(provider).__name__):
                model, allocation = self.build(builder=provider.budget_payload if provider else None)
                self.assertEqual(self.kwargs['recall_outcome'], self.outcome(model))
                self.assertEqual(1, len(self.outcome(model).evidence.items))
                self.assertIn('memory:0', allocation['included_components'])
                self.assertNotIn('MEMORY_RECALL', encode(model.response_schema))
                self.assertEqual((1024, 1536), (allocation['reserved_output_tokens'], allocation['reserved_estimation_tokens']))
        self.assertEqual(original, encode(self.fixture))

    def test_full_cafe_8k_fails_safely_without_removing_the_contract(self):
        with self.assertRaises(ContextBudgetExceeded) as error:
            self.build(knowledge_context_limit=8192)
        allocation = error.exception.allocation
        self.assertTrue(allocation.safe_failure)
        self.assertGreater(allocation.estimated_total, allocation.input_hard_cap)
        self.assertEqual(ContextBudgetPolicy.cognition(8192, 1024).input_hard_cap, allocation.input_hard_cap)

    def test_recommended_32k_full_cafe_step0_step1_official_payload(self):
        provider = DeepSeekOfficialResponsesProvider(base_url='https://unused.invalid',
            api_key='offline', model='deepseek-flash')
        relationship = RelationshipContext(interpretations=('历史主观解释，不是事实或行动许可。' * 20,),
            sources=({'reflection_id': 'synthetic-32k', 'generated_at': 1.0},),
            truncated=False, policy_version='synthetic-32k')
        knowledge = KnowledgeContext(status='OK', items=tuple(dict(knowledge_id=f'k{i}',
            content=f'参考{i}：' + '仅供一般饮品讨论，不代表实时菜单或库存。' * 20,
            version=1) for i in range(3)))
        original = encode(self.fixture)
        for step in (0, 1):
            with self.subTest(step=step):
                model, allocation = self.build(cognition_phase=step,builder=provider.budget_payload,
                    knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH,
                    relationship_context=relationship,
                    knowledge_context=knowledge,
                    recall_outcome=self.kwargs['recall_outcome'] if step else None)
                self.assertEqual(self.request.context.character,
                                 CognitiveModelRequestFactory.context_from(model).character)
                self.assertIn(relationship.interpretations[0], model.messages[-1].content)
                for item in knowledge.items:
                    self.assertIn(item['content'], model.messages[-1].content)
                self.assertEqual([], allocation['dropped_components'])
                self.assertEqual(1024, allocation['reserved_output_tokens'])
                self.assertEqual(ContextBudgetPolicy.cognition(DEFAULT_COGNITIVE_CONTEXT_LENGTH, 1024)
                                 .estimation_reserve, allocation['reserved_estimation_tokens'])
                if step:
                    self.assertEqual(self.kwargs['recall_outcome'], self.outcome(model))
                    self.assertNotIn('MEMORY_RECALL', encode(model.response_schema))
                else:
                    self.assertIn('MemoryRecallStep', model.response_schema['$defs'])
                print('CAFE_32K_WIRE', step, allocation['estimated_total'],
                      allocation['input_hard_cap'], allocation['included_components'],
                      allocation['dropped_components'])
        self.assertEqual(original, encode(self.fixture))

    def test_evidence_that_cannot_fit_is_dropped_whole_with_explicit_status(self):
        outcome = self.kwargs['recall_outcome']
        item = outcome.evidence.items[0].model_copy(update={'summary': '完整条件事实。' * 200})
        oversized = outcome.model_copy(update={'evidence': outcome.evidence.model_copy(update={'items': (item,)})})
        model, allocation = self.build(recall_outcome=oversized)
        delivered = self.outcome(model)
        self.assertEqual('EVIDENCE_BUDGET_EXHAUSTED', delivered.status)
        self.assertEqual((), delivered.evidence.items)
        self.assertTrue(delivered.evidence.truncated)
        self.assertEqual('global_input_cap', allocation['drop_reasons']['memory:0'])
        self.assertIn('budget exhaustion means evidence could not fit', model.messages[0].content)
        self.assertEqual(item, oversized.evidence.items[0])

    def test_all_recalled_items_precede_optional_background(self):
        # A smaller established context isolates ordering from the full Cafe size.
        from tests.test_memory_d2_cognitive_steps import cognitive_request
        self.request = cognitive_request()
        self.kwargs.update(task_context=None, recent_dialogue=None, recent_interactions=None)
        item = self.kwargs['recall_outcome'].evidence.items[0]
        items = tuple(item.model_copy(update={'memory_id': f'memory_{i}',
            'summary': f'第{i}条：过去喜欢拿铁；后来不喜欢；最近恢复，但仅限周末。'}) for i in range(5))
        outcome = self.kwargs['recall_outcome'].model_copy(update={'evidence':
            self.kwargs['recall_outcome'].evidence.model_copy(update={'items': items})})
        knowledge = KnowledgeContext(status='OK', items=tuple(dict(knowledge_id=f'k{i}',
            content='可选背景。' * 100, version=1) for i in range(3)))
        model, allocation = self.build(recall_outcome=outcome, knowledge_context=knowledge)
        self.assertEqual(items, self.outcome(model).evidence.items)
        selected = allocation['included_components']
        self.assertEqual([f'memory:{i}' for i in range(5)], list(selected[:5]))

    def test_qualified_fact_and_conflict_lineage_survive_without_semantic_truncation(self):
        from tests.test_memory_d2_cognitive_steps import cognitive_request
        from app.canonical_recall_models import CanonicalEvidenceItem
        # Leave space for the complete conflict-bearing item; retain the Cafe's
        # event, world, action/task contracts and the original subject catalog.
        self.request = self.request.model_copy(update={'context': self.request.context.model_copy(
            update={'character': cognitive_request().context.character})})
        payload = self.kwargs['recall_outcome'].evidence.items[0].model_dump(mode='json')
        payload.update(summary='起初喜欢拿铁；后来不喜欢；最近恢复，但仅限周末，其他时候喝无糖茶。',
            source_excerpt='最近恢复，但仅限周末，其他时候喝无糖茶。', excerpt_event_id='qualified-event',
            occurred_start=1.0, occurred_end=3.0, occurred_time_basis='OBSERVED_UTC',
            valid_from=3.0, valid_to=10.0, lifecycle_status='CONFLICTED',
            provenance=['PLAYER_UTTERANCE'], provenance_label='PLAYER_UTTERANCE',
            maintenance_relations=[dict(relation='CONFLICTS', related_memory_id='conflicting-memory',
                related_summary='另一来源称仍然不喜欢拿铁，先后关系未确认。',
                related_lifecycle_status='CONFLICTED', related_provenance_label='NPC_SELF_STATEMENT')])
        item = CanonicalEvidenceItem.model_validate(payload)
        outcome = self.kwargs['recall_outcome'].model_copy(update={'evidence':
            self.kwargs['recall_outcome'].evidence.model_copy(update={'items': (item,)})})
        model, _ = self.build(recall_outcome=outcome)
        self.assertEqual(item, self.outcome(model).evidence.items[0])
        policy = model.messages[0].content
        self.assertIn('NPC_SELF_STATEMENT proves only what the NPC said', policy)
        self.assertIn('CONFLICTED remains unresolved even outside top-k', policy)


if __name__ == '__main__':
    unittest.main()
