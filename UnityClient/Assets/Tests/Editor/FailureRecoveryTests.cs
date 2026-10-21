using EmbodiedNPC.Planning.Recovery;
using EmbodiedNPC.World;
using NUnit.Framework;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class FailureRecoveryTests
    {
        [TestCase("TARGET_RESERVED", SkillStatus.Failed, FailureCategory.Transient)]
        [TestCase("HAND_OCCUPIED", SkillStatus.Failed, FailureCategory.Recoverable)]
        [TestCase("TARGET_NOT_PERCEIVED", SkillStatus.Failed, FailureCategory.MissingInformation)]
        [TestCase("REFERENCE_MISSING", SkillStatus.Failed, FailureCategory.Terminal)]
        [TestCase("BACKEND_UNAVAILABLE", SkillStatus.Failed, FailureCategory.Infrastructure)]
        [TestCase("INTERRUPTED_BY_THREAT", SkillStatus.Cancelled, FailureCategory.Interrupted)]
        public void Classifier_MapsAllRecoveryCategories(string code, SkillStatus status,
            FailureCategory expected)
        {
            Assert.That(FailureClassifier.Classify(code, status), Is.EqualTo(expected));
        }

        [Test]
        public void RetryBudget_AllowsOnlyOneRetryForSameWorldFailure()
        {
            RetryBudget budget = new(0f);
            FailureSignature signature = new("bring_object", "PickUp", "cup_01", "PATH_INVALID", 4);

            Assert.That(budget.TryConsume(signature, 1f, out _), Is.True);
            Assert.That(budget.TryConsume(signature, 2f, out string rejection), Is.False);
            Assert.That(rejection, Is.EqualTo("SAME_FAILURE_RETRY_EXHAUSTED"));
        }

        [Test]
        public void RetryBudget_CapsTotalRecoveriesAcrossDifferentFailures()
        {
            RetryBudget budget = new(0f);

            Assert.That(budget.TryConsume(new FailureSignature("g", "PickUp", "a", "PATH_INVALID", 1), 1f, out _), Is.True);
            Assert.That(budget.TryConsume(new FailureSignature("g", "Give", "a", "PLAYER_PATH_INVALID", 1), 2f, out _), Is.True);
            Assert.That(budget.TryConsume(new FailureSignature("g", "Give", "a", "GIVE_TIMEOUT", 1), 3f, out string rejection), Is.False);
            Assert.That(rejection, Is.EqualTo("GOAL_RECOVERY_BUDGET_EXHAUSTED"));
        }

        [Test]
        public void RetryBudget_RejectsRecoveryAtDeadline()
        {
            RetryBudget budget = new(10f, maxGoalDurationSeconds: 30f);

            Assert.That(budget.TryConsume(new FailureSignature("g", "PickUp", "a", "PATH_INVALID", 1),
                40f, out string rejection), Is.False);
            Assert.That(rejection, Is.EqualTo("GOAL_DEADLINE_EXCEEDED"));
        }

        [Test]
        public void InterruptedFailure_NeverRetries()
        {
            SkillResult result = new("skill", "trace", "PickUp", "npc", "cup", null,
                SkillStatus.Cancelled, "CANCELLED_BY_REQUEST", 1, 0, 0);
            RetryBudget budget = new(0f);

            RecoveryDecision decision = RecoveryPolicy.Decide(result,
                new FailureSignature("bring_object", "PickUp", "cup", result.FailureCode, 0), budget, 1f);

            Assert.That(decision.Category, Is.EqualTo(FailureCategory.Interrupted));
            Assert.That(decision.Action, Is.EqualTo(RecoveryAction.AbortGoal));
            Assert.That(budget.RecoveryCount, Is.Zero);
        }

        [TestCase("TARGET_NOT_FOUND", FailureCategory.MissingInformation, RecoveryAction.RequestClarification)]
        [TestCase("REFERENCE_MISSING", FailureCategory.Terminal, RecoveryAction.AbortGoal)]
        [TestCase("BACKEND_UNAVAILABLE", FailureCategory.Infrastructure, RecoveryAction.AbortGoal)]
        public void NonRecoverableFailure_SelectsSafeTerminalAction(string code, FailureCategory category,
            RecoveryAction expectedAction)
        {
            SkillResult result = new("skill", "trace", "PickUp", "npc", "cup", null,
                SkillStatus.Failed, code, 1, 0, 0);
            RetryBudget budget = new(0f);

            RecoveryDecision decision = RecoveryPolicy.Decide(result,
                new FailureSignature("bring_object", "PickUp", "cup", code, 0), budget, 1f);

            Assert.That(decision.Category, Is.EqualTo(category));
            Assert.That(decision.Action, Is.EqualTo(expectedAction));
            Assert.That(budget.RecoveryCount, Is.Zero);
        }
    }
}
