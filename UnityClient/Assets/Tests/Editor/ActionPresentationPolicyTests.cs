using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionPresentationPolicyTests
    {
        [Test]
        public void BuiltInActions_RegisterTheirOwnPresentationPolicies()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            Assert.That(bring.PresentationPolicy, Is.TypeOf<BringObjectPresentationPolicy>());
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);
            Assert.That(move.PresentationPolicy, Is.TypeOf<MoveToEntityPresentationPolicy>());
        }

        [TestCase("bring_object", ActionExecutionStatus.Accepted, "我会尝试把它拿给你。")]
        [TestCase("bring_object", ActionExecutionStatus.Succeeded, "给你。")]
        [TestCase("bring_object", ActionExecutionStatus.Failed, "这次没能把它拿给你。")]
        [TestCase("bring_object", ActionExecutionStatus.Cancelled, "这次没能把它拿给你。")]
        [TestCase("bring_object", ActionExecutionStatus.TimedOut, "这次没能把它拿给你。")]
        [TestCase("move_to_entity", ActionExecutionStatus.Accepted, "好的，我过去看看。")]
        [TestCase("move_to_entity", ActionExecutionStatus.Succeeded, "我已经到目标附近了。")]
        [TestCase("move_to_entity", ActionExecutionStatus.Failed, "我没能到达目标附近。")]
        [TestCase("move_to_entity", ActionExecutionStatus.Cancelled, "我没能到达目标附近。")]
        [TestCase("move_to_entity", ActionExecutionStatus.TimedOut, "我没能到达目标附近。")]
        public void ExistingLifecycleMessages_RemainUnchanged(string actionId,
            ActionExecutionStatus status, string expected)
        {
            BuiltInActionCatalog.Instance.TryGet(actionId, out ActionRegistration registration);
            ActionPresentationInfo presentation = registration.PresentationPolicy.Present(Result(actionId, status));
            Assert.That(presentation.Status, Is.EqualTo(status));
            Assert.That(presentation.Message, Is.EqualTo(expected));
        }

        [TestCase(ActionExecutionStatus.Recovering)]
        [TestCase(ActionExecutionStatus.Rejected)]
        [TestCase(ActionExecutionStatus.RequiresConfirmation)]
        [TestCase(ActionExecutionStatus.Clarification)]
        public void NonTerminalResponseMessage_IsPassedThrough(ActionExecutionStatus status)
        {
            BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration registration);
            ActionPresentationInfo presentation = registration.PresentationPolicy.Present(
                Result("bring_object", status, "保留现有上层文本。"));
            Assert.That(presentation.Code, Is.EqualTo("TEST_CODE"));
            Assert.That(presentation.Message, Is.EqualTo("保留现有上层文本。"));
        }

        [Test]
        public void ActionExecutionResult_PreservesIdentityBindingsAndWorldRevisions()
        {
            ActionExecutionResult result = Result("bring_object", ActionExecutionStatus.Failed);
            Assert.That(result.ProposalId, Is.EqualTo("proposal_01"));
            Assert.That(result.TraceId, Is.EqualTo("trace_01"));
            Assert.That(result.ValidatedBindings["target"], Is.EqualTo("cup_01"));
            Assert.That(result.ValidatedBindings["destination"], Is.EqualTo("player_01"));
            Assert.That(result.WorldRevisionBefore, Is.EqualTo(7));
            Assert.That(result.WorldRevisionAfter, Is.EqualTo(9));
        }

        [Test]
        public void PresentationPolicy_RejectsResultFromAnotherAction()
        {
            Assert.Throws<ArgumentException>(() => new BringObjectPresentationPolicy().Present(
                Result("move_to_entity", ActionExecutionStatus.Succeeded)));
        }

        private static ActionExecutionResult Result(string actionId, ActionExecutionStatus status,
            string sourceMessage = "") => new(actionId, "proposal_01", "trace_01", status, "TEST_CODE",
            new Dictionary<string, string>
            {
                ["target"] = "cup_01",
                ["destination"] = "player_01",
            }, 7, 9, sourceMessage);
    }
}
