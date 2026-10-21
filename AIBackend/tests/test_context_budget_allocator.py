from __future__ import annotations

import unittest

from app.context_budget_allocator import (
    AtomicContextGroup,
    ContextBudgetAllocator,
    ContextBudgetExceeded,
    ContextBudgetPolicy,
)


class ContextBudgetAllocatorTests(unittest.TestCase):
    def test_pass_policies_reserve_model_context(self):
        pass1, pass2 = ContextBudgetPolicy.pass1(), ContextBudgetPolicy.pass2()
        self.assertEqual((6200, 1024, 968),
                         (pass1.input_hard_cap, pass1.response_reserve, pass1.estimation_reserve))
        self.assertEqual((5800, 1024, 1368, 1800),
                         (pass2.input_hard_cap, pass2.response_reserve,
                          pass2.estimation_reserve, pass2.evidence_ceiling))
        self.assertEqual(8192, pass1.input_hard_cap + pass1.response_reserve + pass1.estimation_reserve)
        self.assertEqual(8192, pass2.input_hard_cap + pass2.response_reserve + pass2.estimation_reserve)

    def test_atomic_group_is_included_or_dropped_whole(self):
        policy = ContextBudgetPolicy(
            name="test", max_context_tokens=100, input_hard_cap=50,
            response_reserve=25, estimation_reserve=25,
            recent_target_ceiling=20, summary_ceiling=0, evidence_ceiling=2,
        )
        allocator = ContextBudgetAllocator(policy)
        group = AtomicContextGroup("conflict:one", "evidence", "x" * 40, 0, 0)
        allocation = allocator.allocate(
            {"mandatory": "safe"}, (group,),
            lambda included: "safe" + ("x" * 40 if included else ""),
        )
        self.assertEqual((), allocation.included_components)
        self.assertEqual(("conflict:one",), allocation.dropped_components)

    def test_mandatory_overflow_is_safe_failure(self):
        policy = ContextBudgetPolicy(
            name="tiny", max_context_tokens=30, input_hard_cap=10,
            response_reserve=10, estimation_reserve=10,
            recent_target_ceiling=0, summary_ceiling=0, evidence_ceiling=0,
            recent_hard_minimum_turns=0,
        )
        allocator = ContextBudgetAllocator(policy)
        allocation = allocator.allocate({"mandatory": "中" * 20}, (), lambda _: "中" * 20)
        self.assertTrue(allocation.safe_failure)
        self.assertTrue(allocation.budget_exhausted)
        with self.assertRaises(ContextBudgetExceeded):
            allocator.require(allocation)

    def test_priority_is_deterministic_under_pressure(self):
        policy = ContextBudgetPolicy(
            name="priority", max_context_tokens=80, input_hard_cap=40,
            response_reserve=20, estimation_reserve=20,
            recent_target_ceiling=40, summary_ceiling=40, evidence_ceiling=40,
            recent_hard_minimum_turns=0,
        )
        allocator = ContextBudgetAllocator(policy)
        groups = (
            AtomicContextGroup("later", "recent", "x" * 100, 2, 0),
            AtomicContextGroup("first", "evidence", "x" * 100, 0, 0),
        )
        allocation = allocator.allocate(
            {"mandatory": "safe"}, groups,
            lambda included: "safe" + "".join("x" * 100 for _ in included),
        )
        self.assertEqual(("first",), allocation.included_components)
        self.assertEqual(("later",), allocation.dropped_components)

    def test_policy_rejects_reserves_above_context(self):
        with self.assertRaisesRegex(ValueError, "RESERVES_EXCEED"):
            ContextBudgetPolicy(name="bad", max_context_tokens=100,
                                input_hard_cap=80, response_reserve=20,
                                estimation_reserve=20)


if __name__ == "__main__":
    unittest.main()
