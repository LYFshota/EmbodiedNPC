"""Daily life-state intents never certify dialogue as an executed world fact."""
import unittest
from app.cognitive_entry import CognitiveBackendEntry
from app.action_proposal_validator import ActionProposalValidationError
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from tests.test_daily_behavior_cognition import request, WireScriptedProvider
from tests.test_memory_d2_cognitive_steps import cognitive_request


class DailyModeDecouplingTests(unittest.IsolatedAsyncioTestCase):
    async def run_decision(self, current, decision):
        provider = WireScriptedProvider([decision])
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=32768,
            inference_options=InferenceOptions(max_output_tokens=512)).run(current)
        self.assertEqual(len(provider.requests), 1)
        return result, provider

    async def test_life_mode_is_soft_and_does_not_replace_persona_or_change_input(self):
        current = request(text="回来工作吧，你喜欢什么？")
        before = current.context.model_dump(mode="json")
        result, provider = await self.run_decision(current, dict(decision_kind="DIRECT_RESPONSE", target_daily_mode="work", utterance="我喜欢画招牌。"))
        self.assertIsNone(result.validated_goal)
        self.assertEqual(current.context.model_dump(mode="json"), before)
        projected = CognitiveModelRequestFactory.context_from(provider.requests[0])
        self.assertEqual(projected.character, current.context.character)
        self.assertEqual(projected.capabilities, current.context.capabilities)
        self.assertEqual(projected.daily_behavior.current_mode, "rest")
        self.assertIn("soft life-state tendency", provider.requests[0].messages[0].content)
        self.assertNotIn("a rejected proposal suppresses that reply", provider.requests[0].messages[0].content)

    async def test_original_language_risks_are_dialogue_not_validated_actions(self):
        for text in ("我这就给你做拿铁，稍等两分钟。", "杯子我已经拿来了，咖啡也做好了。", "我喜欢画街头招牌。那杯我马上给你端过去。", "刚刚已为你做好拿铁，我这就送到你的桌边。", "我已经回到吧台，拿铁也做好了。", "我喜欢画街头招牌。那杯拿铁马上给你送过去。"):
            for mode in (None, "work"):
                with self.subTest(text=text, mode=mode):
                    result, _ = await self.run_decision(request(), dict(decision_kind="DIRECT_RESPONSE", utterance=text, target_daily_mode=mode))
                    self.assertEqual(result.decision.utterance, text)
                    self.assertIsNone(result.validated_goal)
                    self.assertIsNone(result.decision.action_proposal)

    async def test_without_daily_behavior_remains_complete_ordinary_cognition(self):
        current = cognitive_request("你平时喜欢什么？")
        result, provider = await self.run_decision(current, dict(decision_kind="DIRECT_RESPONSE", utterance="我喜欢画画。"))
        self.assertEqual(result.decision.utterance, "我喜欢画画。")
        self.assertNotIn("soft life-state tendency", provider.requests[0].messages[0].content)

    async def test_rest_action_does_not_require_mode_change(self):
        result, _ = await self.run_decision(request(), dict(decision_kind="ACTION_PROPOSAL", utterance="我去拿杯子。", action_proposal=dict(action_id="bring_object", bindings=dict(target="cup_01", destination="player_01"))))
        self.assertEqual(result.validated_goal.type, "bring_object")
        self.assertIsNone(result.decision.target_daily_mode)

    async def test_combined_mode_cannot_legalize_an_invalid_action(self):
        with self.assertRaises(ActionProposalValidationError):
            await self.run_decision(request(), dict(decision_kind="ACTION_PROPOSAL", target_daily_mode="work", action_proposal=dict(action_id="bring_object", bindings=dict(target="unknown_cup", destination="player_01"))))
