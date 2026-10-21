"""Offline checks for conservative evaluation-only semantic labels."""

import unittest

from tools.r4cc_eval_scoring import score


class EvaluationScoringTests(unittest.TestCase):
    def test_schema_failure_is_not_a_semantic_failure(self):
        self.assertEqual("NOT_EVALUABLE", score({"case_id": "H1", "schema_valid": False})[
            "semantic_classification"])

    def test_current_flight_refusal_with_cafe_role_is_not_old_coffee_topic(self):
        row = {"case_id": "E", "schema_valid": True, "decision_kind": "DIRECT_RESPONSE",
               "utterance": "抱歉，我只是咖啡馆店员，没法飞到屋顶。"}
        self.assertEqual("CORRECT", score(row)["semantic_classification"])
        row["utterance"] = "我没法飞。对了，你以前说过喜欢乌龙茶。"
        self.assertEqual("REVIEW_REQUIRED", score(row)["semantic_classification"])

    def test_h1_answer_and_evidence_discipline_are_separate(self):
        row = {"case_id": "H1", "schema_valid": True, "decision_kind": "DIRECT_RESPONSE",
               "utterance": "我不知道你最喜欢什么茶。"}
        self.assertEqual({"semantic_classification": "CORRECT", "answer_entailment": "PASS",
                          "evidence_discipline": "CLEAN"}, score(row))
        row["utterance"] += "不过我记得你喜欢拿铁咖啡。"
        self.assertEqual({"semantic_classification": "CORRECT", "answer_entailment": "PASS",
                          "evidence_discipline": "OVER_INCLUSIVE"}, score(row))
        row["utterance"] = "你最喜欢红茶。"
        self.assertEqual({"semantic_classification": "ANSWER_ENTAILMENT_ERROR",
                          "answer_entailment": "FAIL", "evidence_discipline": "UNSUPPORTED"}, score(row))

    def test_ambiguous_h1_requires_review(self):
        row = {"case_id": "H1", "schema_valid": True, "decision_kind": "DIRECT_RESPONSE",
               "utterance": "我记得你喜欢拿铁。"}
        self.assertEqual("REVIEW_REQUIRED", score(row)["semantic_classification"])


if __name__ == "__main__":
    unittest.main()
