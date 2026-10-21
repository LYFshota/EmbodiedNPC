"""Offline parity and scoring checks for the four-model evaluation fixture."""

import tempfile
import unittest
from pathlib import Path

from tools import r4cc_model_ab_eval as common
from tools.r4cc_eval_scoring import score
from tools.r4cc_four_model_fixtures import PHASE1_CASES, PHASE2_CASES, build_requests


class FourModelEvaluationTests(unittest.TestCase):
    def test_fixed_requests_rebuild_with_identical_digests(self):
        old_out = common.OUT
        try:
            with tempfile.TemporaryDirectory() as directory:
                common.OUT = Path(directory)
                memory = common.canonical_fixture()
                first = build_requests(memory)
                second = build_requests(memory)
                self.assertEqual(set(PHASE1_CASES + PHASE2_CASES), set(first))
                self.assertEqual({case: common.digest(value[1]) for case, value in first.items()},
                                 {case: common.digest(value[1]) for case, value in second.items()})
                self.assertEqual("OK", first["G1"][2].status)
                self.assertEqual("OK", first["H1"][2].status)
        finally:
            common.OUT = old_out

    def test_g0_optional_memory_type_and_h1_separate_taxonomy(self):
        g0 = {"schema_valid": True, "case_id": "G0", "decision_kind": "MEMORY_RECALL",
              "utterance": None, "recall_plan": {"semantic_query": "最喜欢什么颜色",
                                                "memory_type": None,
                                                "has_subject_ref": True}}
        self.assertEqual("CORRECT", score(g0)["semantic_classification"])
        h1 = {"schema_valid": True, "case_id": "H1", "decision_kind": "DIRECT_RESPONSE",
              "utterance": "我不知道你最喜欢什么茶。"}
        self.assertEqual("CORRECT", score(h1)["semantic_classification"])
        h1["utterance"] += "只知道你喜欢拿铁咖啡。"
        self.assertEqual("PASS", score(h1)["answer_entailment"])
        self.assertEqual("OVER_INCLUSIVE", score(h1)["evidence_discipline"])


if __name__ == "__main__":
    unittest.main()
