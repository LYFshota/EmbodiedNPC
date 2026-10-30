import unittest
from pydantic import ValidationError
from app.contracts import CognitiveDecision
from app.cognitive_step_models import CognitiveStepResult

class ExpressionIconTests(unittest.TestCase):
    def test_old_reply_and_none_are_silent_and_wire_compatible(self):
        for fields in ({}, {'expression_icon': None}, {'expression_icon': 'none'}):
            d = CognitiveDecision(decision_kind='DIRECT_RESPONSE', utterance='Hello?', emotion='happy', **fields)
            self.assertNotIn('expression_icon', d.model_dump(mode='json'))
    def test_four_explicit_cues_survive_model_steps_and_wire(self):
        for cue in ['happy', 'confused', 'surprised', 'thinking']:
            d = CognitiveStepResult.model_validate({'decision_kind': 'DIRECT_RESPONSE', 'utterance': 'Hello.', 'expression_icon': cue})
            self.assertEqual(d.root.model_dump(mode='json')['expression_icon'], cue)
    def test_arbitrary_text_and_non_reply_cues_rejected(self):
        for cue in ['wave', '<size=999>!</size>', 'confused;prepare_drink']:
            with self.assertRaises(ValidationError):
                CognitiveDecision(decision_kind='DIRECT_RESPONSE', utterance='Hi', expression_icon=cue)
        with self.assertRaises(ValidationError):
            CognitiveDecision(decision_kind='NO_ACTION', expression_icon='happy')
