using System;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Planning.Recovery;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class PlanCompletionRegressionTests
    {
        [Test]
        public void TerminalPresentation_IsPublishedAfterProposalCleanup()
        {
            GameObject root = new("GoalPlanRunner_TerminalOrdering");
            root.SetActive(false);
            try
            {
                GoalPlanRunner runner = root.AddComponent<GoalPlanRunner>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(GoalPlanRunner).GetField("activeGoal", flags).SetValue(runner,
                    new GoalProposal { type = "move_to_entity", target_entity_id = "book_01" });
                typeof(GoalPlanRunner).GetField("activeProposalId", flags).SetValue(runner, "proposal_old");
                bool observedCleanState = false;
                runner.PresentationReady += _ => observedCleanState = string.IsNullOrEmpty(
                    (string)typeof(GoalPlanRunner).GetField("activeProposalId", flags).GetValue(runner));

                typeof(GoalPlanRunner).GetMethod("HandleCompleted", flags).Invoke(runner,
                    new object[] { new PlanExecutionResult(PlanExecutionStatus.Succeeded, "", "hash", 1, 0) });

                Assert.That(observedCleanState, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase(PlanExecutionStatus.Cancelled, "CANCELLED_BY_REQUEST", "我没能到达目标附近。")]
        [TestCase(PlanExecutionStatus.TimedOut, "GOAL_DEADLINE_EXCEEDED", "我没能到达目标附近。")]
        public void NonSuccessTerminalPresentation_PreservesLegacyFailedSemantics(
            PlanExecutionStatus status, string failureCode, string expectedMessage)
        {
            GameObject root = new("GoalPlanRunner_TerminalPresentation");
            root.SetActive(false);
            try
            {
                GoalPlanRunner runner = root.AddComponent<GoalPlanRunner>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(GoalPlanRunner).GetField("activeGoal", flags).SetValue(runner,
                    new GoalProposal { type = "move_to_entity", target_entity_id = "book_01" });
                GoalPlanPresentation observed = default;
                runner.PresentationReady += presentation => observed = presentation;

                typeof(GoalPlanRunner).GetMethod("HandleCompleted", flags).Invoke(runner,
                    new object[] { new PlanExecutionResult(status, failureCode, "hash", 0, 0) });

                Assert.That(observed.Kind, Is.EqualTo(GoalPlanPresentationKind.Failed));
                Assert.That(observed.Code, Is.EqualTo(failureCode));
                Assert.That(observed.Message, Is.EqualTo(expectedMessage));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SuccessAtDeadline_FinishesImmediatelyOnlyForFinalAction(bool finalAction)
        {
            // Feed the executor a committed Action result at the deadline boundary.
            // This isolates frame ordering; physical Give is covered by the scene suite.
            var binding = new BringObjectBindings("npc_01", "cup_01", "player_01");
            var action = BringObjectActionCatalog.CreateActions(binding)
                .Single(value => value.Definition.ActionType == PlannerActionType.Give);
            var goal = BringObjectActionCatalog.CreateGoal("goal_test", binding);
            var snapshot = new PlannerSnapshot("snapshot_test", "goal_test", 0, 0, new PlanningState(null));
            var plan = new PlanResult(PlanSearchStatus.Found, snapshot, goal,
                finalAction ? new[] { action } : new[] { action, action }, 1, 1);
            var executor = new PlanExecutor(null, null, null, null, null, null, null, null);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PlanExecutor).GetField("plan", flags).SetValue(executor, plan);
            typeof(PlanExecutor).GetField("retryBudget", flags).SetValue(executor,
                new RetryBudget(Time.unscaledTime - 60f));
            typeof(PlanExecutor).GetProperty("IsExecuting").SetValue(executor, true);
            int completions = 0;
            PlanExecutionResult terminal = default;
            executor.Completed += result => { completions++; terminal = result; };
            var skill = new SkillResult("give", "trace", "Give", "npc_01", "cup_01", "player_01",
                SkillStatus.Succeeded, "", 1, 0, 1);
            typeof(PlanExecutor).GetMethod("HandleActionResult", flags).Invoke(executor,
                new object[] { new PlanActionRunResult(PlanActionRunStatus.Succeeded, skill) });
            Assert.That(completions, Is.EqualTo(finalAction ? 1 : 0));
            executor.Tick(0f);
            executor.Tick(0f);
            Assert.That(completions, Is.EqualTo(1));
            Assert.That(terminal.Status, Is.EqualTo(finalAction ? PlanExecutionStatus.Succeeded : PlanExecutionStatus.TimedOut));
            Assert.That(terminal.CompletedActions, Is.EqualTo(1));
            Assert.That(terminal.PlanHash, Is.EqualTo(plan.PlanHash));
            Assert.That(executor.IsExecuting, Is.False);
        }
    }
}
