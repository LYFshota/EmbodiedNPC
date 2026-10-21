"""Offline fixture/parity checks for the capability A/B harness."""

import unittest

from app.model_requests import CognitiveModelRequestFactory
from tools.r4cc_cloud_model_smoke import SUBJECT_CATALOG, case_request
from tools.r4cc_model_ab_eval import (
    CASES, CLOUD_CALLS_MAX, OUT, SAMPLES, SCHEMA_SAMPLES,
    canonical_fixture, classify, digest, durable_request, memory_metadata, prior_turn,
)
from app.cognitive_step_models import MemoryRecallPlan


class ModelAbFixtureTests(unittest.TestCase):
    def test_call_budget_and_eight_current_tasks(self):
        self.assertEqual(8, len(CASES))
        self.assertEqual(3, SAMPLES)
        self.assertEqual(10, SCHEMA_SAMPLES)
        self.assertEqual(41, CLOUD_CALLS_MAX)

    def test_digest_covers_messages_schema_and_options(self):
        request = case_request("B", "我喜欢拿铁。")
        original = CognitiveModelRequestFactory.create_event(request, subject_catalog=SUBJECT_CATALOG, knowledge_context_limit=16384)
        changed = original.model_copy(update={"inference_options": original.inference_options.model_copy(
            update={"temperature": 0.2})})
        self.assertNotEqual(digest(original), digest(changed))
        self.assertEqual(digest(original), digest(original.model_copy(deep=True)))

    def test_canonical_fixture_yields_only_requested_fact(self):
        OUT.mkdir(exist_ok=True)
        memory = canonical_fixture()
        catalog = memory.prepare(durable_request("G", "我以前说过我最喜欢什么颜色？"))
        self.assertIsNotNone(catalog)
        outcome = memory.recall(catalog, MemoryRecallPlan(
            semantic_query="玩家以前说过最喜欢什么颜色？",
            subject_ref=catalog.requester_ref, memory_type="SEMANTIC_FACT"))
        self.assertEqual({"status": "OK", "evidence_count": 1,
                          "memory_types": ["SEMANTIC_FACT"],
                          "source_anchored": [True]}, memory_metadata(outcome))
        self.assertEqual(1, len(prior_turn("我喜欢拿铁。", "记住了。")))

    def test_schema_failure_is_not_counted_as_semantic_failure(self):
        row = {"schema_valid": False, "case_id": "C", "step": "step0",
               "decision_kind": None, "utterance": None, "action_proposal": None}
        self.assertEqual("NOT_EVALUABLE", classify(row))
        row.update(schema_valid=True, case_id="B", decision_kind="DIRECT_RESPONSE",
                   utterance="好的，请稍等。")
        self.assertEqual("DECISION_ERROR", classify(row))
        row.update(case_id="E", utterance="抱歉，我没办法飞到屋顶。")
        self.assertEqual("CORRECT", classify(row))

if __name__ == "__main__":
    unittest.main()
