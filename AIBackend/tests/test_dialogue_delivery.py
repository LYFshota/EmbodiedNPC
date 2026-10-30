import unittest
from typing import get_args
from pydantic import TypeAdapter, ValidationError
from app.contracts import Emotion, CognitiveDecision
from app.cognitive_step_models import CognitiveStepResult


class DialogueDeliveryTests(unittest.TestCase):
    def test_delivery_tones_are_bounded_in_terminal_and_step_schema(self):
        expected = ['happy', 'neutral', 'fear', 'curious', 'thoughtful', 'concerned', 'surprised']
        self.assertEqual(list(get_args(Emotion)), expected)
        for tone in expected:
            decision = CognitiveDecision(decision_kind='DIRECT_RESPONSE', utterance='Hello.', emotion=tone)
            self.assertEqual(decision.emotion, tone)
        schema = str(CognitiveStepResult.model_json_schema())
        for tone in expected:
            self.assertIn(tone, schema)
        with self.assertRaises(ValidationError):
            TypeAdapter(Emotion).validate_python('arbitrary_animation')

    def test_no_action_still_cannot_carry_delivery_or_action(self):
        with self.assertRaises(ValidationError):
            CognitiveDecision(decision_kind='NO_ACTION', emotion='curious')
