using System;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionFeedbackContractTests
    {
        private static CognitiveActionFeedback Feedback() => new() {
            original_request_id="original_request", original_run_id="original_run", outcome_id="outcome_original",
            proposal_id="proposal_original", trace_id="trace_original", proposal_revision=0, world_revision=4,
            failure_code="NO_PLAN", detail="The attempt failed; the physical cause is unknown.",
            failed_goal=new GoalProposal {type="bring_object",target_entity_id="cup_01",destination_entity_id="player_01"},
            allowed_alternatives=new[] {new CognitiveActionAlternative {alternative_id="bring_cup_02",description="换用已授权的另一只杯子",
                goal=new GoalProposal {type="bring_object",target_entity_id="cup_02",destination_entity_id="player_01"}}} };

        [Test] public void Feedback_KeepsOriginalLineageAndCommittedStepEvidence()
        {
            var value=Feedback(); value.completed_actions=2;
            Assert.That(value.IsValid,Is.True);
            Assert.That(value.original_request_id,Is.EqualTo("original_request"));
            value.allowed_alternatives[0].goal.destination_entity_id="other_player";
            Assert.That(value.IsValid,Is.False,"An alternative cannot change the original recipient.");
        }
        [TestCase(-1)] [TestCase(2)] public void Feedback_RejectsUnboundedRevision(int revision)
        {var value=Feedback();value.proposal_revision=revision;Assert.That(value.IsValid,Is.False);}
        [Test] public void Feedback_DuplicateCandidatesAndMissingOutcomeAreRejected()
        {
            var value=Feedback();value.allowed_alternatives=new[]{value.allowed_alternatives[0],value.allowed_alternatives[0]};
            Assert.That(value.IsValid,Is.False);value=Feedback();value.outcome_id=null;Assert.That(value.IsValid,Is.False);
        }
        [TestCase("CANCELLED_BY_REQUEST",PlanExecutionStatus.Cancelled)]
        [TestCase("INTERRUPTED_BY_THREAT",PlanExecutionStatus.Cancelled)]
        [TestCase("RUNNER_DISABLED",PlanExecutionStatus.Failed)]
        public void CancellationAndInvalidWorldNeverWakeRecovery(string code,PlanExecutionStatus status)
        {Assert.That(ActionFeedbackPolicy.ShouldRequestCognition(code,status),Is.False);}
        [Test] public void UnifiedDecision_SelectionCannotCarryAnotherWorldAction()
        {
            var decision=new CognitiveDecision {decision_kind="DIRECT_RESPONSE",utterance="可以换成另一只允许的杯子。",
                dialogue_turn_policy="WAIT_FOR_PLAYER",recovery_alternative_id="bring_cup_02"};
            Assert.That(decision.IsValid,Is.True);decision.action_proposal=new CognitiveActionProposal {
                action_id="prepare_drink",bindings=new System.Collections.Generic.Dictionary<string,string>{{"drink_type","latte"}}};
            Assert.That(decision.IsValid,Is.False);
        }
        [Test] public async System.Threading.Tasks.Task FeedbackRetirementDoesNotDiscardExistingTaskCommandOnDisconnect()
        {
            var root=new UnityEngine.GameObject("C3_TransportRetentionBoundary");
            try
            {
                var backend=root.AddComponent<BackendWebSocketClient>();
                var field=typeof(BackendWebSocketClient).GetField("pendingTaskCommands",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var pending=(System.Collections.Generic.Dictionary<string,CognitiveRequest>)field.GetValue(backend);
                var original=new CognitiveRequest {request_id="original_task_command"};pending.Add(original.request_id,original);
                await backend.DisconnectAsync();Assert.That(pending[original.request_id],Is.SameAs(original));
            }
            finally {UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void RetiredFailureEnvelopeIsNotAUnifiedResponse()
        {Assert.That(CognitiveResponseValidator.TryParse("{\"message_type\":\"task_failure_response\"}",out _,out _),Is.False);}
    }
}
