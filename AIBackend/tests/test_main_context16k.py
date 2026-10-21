"""Narrow 16K product-default / legacy-capacity / final-request checks, no model IO."""
import json
import unittest
from datetime import datetime, timezone

from app.cognitive_entry import CognitiveBackendEntry
from app.context_budget_allocator import ContextBudgetExceeded, ContextBudgetPolicy
from app.context_token_estimator import ContextTokenEstimator
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_api_config import MainModelConfig, EmbeddingConfig, ModelApiConfig
from app.model_config_runtime import restart_required
from app.model_profile_api import SaveProfileCommand, profile_connection
from app.model_profile_config import ModelConfigV2Document
from app.model_requests import CognitiveModelRequestFactory
from app.providers import ModelResult
from tests.test_world_knowledge import official_merchant_request


class CaptureProvider:
    name = 'context16k-test'
    model = 'no-inference'
    def __init__(self, capacity):
        self.context_length = capacity
        self.requests = []
    async def generate(self, request):
        self.requests.append(request)
        return ModelResult(text='{"decision_kind":"NO_ACTION"}')


class MainContext16KTests(unittest.IsolatedAsyncioTestCase):
    async def test_full_current_persona_request_16k_admits_and_reserves_output(self):
        provider = CaptureProvider(16384)
        request = official_merchant_request('请简单介绍一下你自己。')
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor()).run(request)
        self.assertEqual(result.decision.decision_kind, 'NO_ACTION')
        self.assertEqual(len(provider.requests), 1)
        model = provider.requests[0]
        self.assertEqual(CognitiveModelRequestFactory.context_from(model).character, request.context.character)
        self.assertEqual(model.inference_options.max_output_tokens, 1024)
        policy = ContextBudgetPolicy.cognition(16384, 1024)
        measured = ContextTokenEstimator().estimate_text(json.dumps(model.model_dump(mode='json'), ensure_ascii=False, separators=(',', ':')))
        self.assertLessEqual(measured, policy.input_hard_cap)
        self.assertLess(policy.input_hard_cap, 16384)
        self.assertEqual(policy.input_hard_cap + policy.response_reserve + policy.estimation_reserve, 16384)
        print('16K final-wire estimate=%s input_cap=%s output=%s margin=%s' %
              (measured, policy.input_hard_cap, policy.response_reserve, policy.estimation_reserve))

    async def test_provider_declared_8k_cannot_be_overridden_by_requested_16k(self):
        provider = CaptureProvider(8192)
        with self.assertRaises(ContextBudgetExceeded) as error:
            await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=16384).run(
                official_merchant_request('请简单介绍一下你自己。'))
        self.assertEqual(provider.requests, [])
        self.assertTrue(error.exception.allocation.safe_failure)
        self.assertEqual(error.exception.allocation.input_hard_cap, 6400)
        self.assertEqual(str(error.exception), 'COGNITIVE_PROMPT_BUDGET_EXCEEDED')

    def test_new_mock_slot_16k_but_existing_nullable_config_is_not_migrated(self):
        empty = ModelConfigV2Document(revision=1, updated_at=datetime.now(timezone.utc))
        config = dict(display_name='Test', provider_type='mock', model='fixed-greeting-v1')
        command = SaveProfileCommand(expected_revision=0, kind='main', profile_id='test-main', preset_id='custom',
                                     key_action='retain', config=config)
        self.assertEqual(profile_connection(command, empty).context_length, 16384)
        legacy = MainModelConfig(**config)
        self.assertIsNone(legacy.context_length)  # Loading legacy JSON keeps its existing runtime fallback.
        active = empty.model_copy(update={'activation': ModelApiConfig(main_model=legacy, embedding=EmbeddingConfig())})
        changed = active.model_copy(update={'activation': ModelApiConfig(main_model=legacy.model_copy(
            update={'context_length': 16384}), embedding=EmbeddingConfig())})
        self.assertTrue(restart_required(active, changed))
        self.assertIsNone(active.main_model.context_length)


if __name__ == '__main__':
    unittest.main()
