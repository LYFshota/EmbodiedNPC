from __future__ import annotations

import unittest

from app.context_token_estimator import ContextTokenEstimator


class ContextTokenEstimatorTests(unittest.TestCase):
    def test_estimate_is_deterministic_and_cjk_conservative(self):
        estimator = ContextTokenEstimator()
        text = "玩家想要一号杯"
        self.assertEqual(estimator.estimate_text(text), estimator.estimate_text(text))
        self.assertGreaterEqual(estimator.estimate_text(text), len(text))

    def test_ascii_and_json_punctuation_receive_nonzero_conservative_cost(self):
        estimator = ContextTokenEstimator()
        self.assertGreaterEqual(estimator.estimate_text("abcdefgh"), 2)
        self.assertGreaterEqual(estimator.estimate_text('{"id":"cup_01"}'), 5)

    def test_component_breakdown_matches_total(self):
        estimate = ContextTokenEstimator().estimate_components({"system": "hello", "input": "你好"})
        self.assertEqual(sum(estimate.component_breakdown.values()), estimate.estimated_tokens)
        self.assertEqual(7, estimate.estimated_chars)

    def test_empty_text_costs_zero_and_invalid_factor_fails(self):
        self.assertEqual(0, ContextTokenEstimator().estimate_text(""))
        with self.assertRaisesRegex(ValueError, "SAFETY_FACTOR_INVALID"):
            ContextTokenEstimator(0.99)

    def test_safety_factor_never_reduces_estimate(self):
        text = "mixed JSON {\"entity_id\":\"杯子\"}"
        self.assertGreaterEqual(
            ContextTokenEstimator(1.5).estimate_text(text),
            ContextTokenEstimator(1.0).estimate_text(text),
        )


if __name__ == "__main__":
    unittest.main()
