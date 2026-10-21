using EmbodiedNPC.Planning;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class PlanExecutionPolicyTests
    {
        [Test]
        public void SamePlanHash_AllowsOneReplanThenStopsLoop()
        {
            PlanReplanGuard guard = new(maxReplans: 2, maxSameHashReplans: 1);

            Assert.That(guard.TryAccept("hash_a", false, out _), Is.True);
            Assert.That(guard.TryAccept("hash_a", true, out _), Is.True);
            Assert.That(guard.TryAccept("hash_a", true, out string failureCode), Is.False);
            Assert.That(failureCode, Is.EqualTo("SAME_PLAN_HASH_REPLAN_EXHAUSTED"));
            Assert.That(guard.ReplanCount, Is.EqualTo(1));
        }

        [Test]
        public void DifferentPlanHashes_StopAtGoalReplanBudget()
        {
            PlanReplanGuard guard = new(maxReplans: 2, maxSameHashReplans: 1);

            Assert.That(guard.TryAccept("hash_a", false, out _), Is.True);
            Assert.That(guard.TryAccept("hash_b", true, out _), Is.True);
            Assert.That(guard.TryAccept("hash_c", true, out _), Is.True);
            Assert.That(guard.TryAccept("hash_d", true, out string failureCode), Is.False);
            Assert.That(failureCode, Is.EqualTo("GOAL_REPLAN_BUDGET_EXHAUSTED"));
            Assert.That(guard.ReplanCount, Is.EqualTo(2));
        }
    }
}
