using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests
{
    public sealed class CognitiveTestPresenter : MonoBehaviour, ICognitiveResponsePresenter
    {
        public int Count { get; private set; }
        public string LastUtterance { get; private set; }
        public bool ShouldAccept { get; set; } = true;
        public bool TryPresentCognitiveUtterance(string utterance)
        {
            if (!ShouldAccept || string.IsNullOrWhiteSpace(utterance)) return false;
            Count++;
            LastUtterance = utterance;
            return true;
        }
    }

    public sealed class CognitiveDecisionLoopTests
    {
        private readonly List<Object> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void BackendActionRejection_SettlesOwnedRunImmediatelyAndAllowsNextTurn()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            BackendWebSocketClient backend = driver.gameObject.AddComponent<BackendWebSocketClient>();
            driver.Configure(driver.GetComponent<WorldEntity>(), orchestrator,
                driver.GetComponent<CognitiveContextComposer>(), backend, presenter);
            CognitiveRun rejectedRun = orchestrator.CurrentRun;
            string rejection = "{\"message_type\":\"error\",\"request_id\":\"" + request.request_id +
                "\",\"trace_id\":\"" + request.trace_id + "\",\"run_id\":\"" + rejectedRun.RunId +
                "\",\"agent_entity_id\":\"" + rejectedRun.AgentEntityId +
                "\",\"reason\":\"INVALID_ACTION_PROPOSAL\"}";

            Assert.That(backend.ProcessIncomingJson(rejection.Replace(request.trace_id, "other_trace")), Is.True);
            Assert.That(backend.ProcessIncomingJson(rejection.Replace(request.request_id, "other_request")),
                Is.True);
            Assert.That(backend.ProcessIncomingJson(rejection.Replace(rejectedRun.RunId, "other_run")),
                Is.True);
            Assert.That(backend.ProcessIncomingJson(rejection.Replace(rejectedRun.AgentEntityId,
                "other_agent")), Is.True);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(rejectedRun));
            Assert.That(driver.PendingRequest, Is.SameAs(request));

            Assert.That(backend.ProcessIncomingJson(rejection), Is.True);
            Assert.That(backend.LastCognitiveIngressStatus, Is.EqualTo("INVALID_ACTION_PROPOSAL"));
            Assert.That(rejectedRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(driver.PendingRequest, Is.Null);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presenter.Count, Is.Zero);

            CognitiveTrigger next = new("trigger_02", CognitiveTriggerTypes.Event,
                rejectedRun.AgentEntityId, "event_02", 2d, AgentEventTypes.PlayerEnteredArea);
            Assert.That(orchestrator.TryStartRun(next, out CognitiveRun nextRun), Is.True);
            Assert.That(backend.ProcessIncomingJson(rejection), Is.True);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(nextRun));
            Assert.That(nextRun.State, Is.EqualTo(CognitiveRunState.Running));
        }

        [TestCase("TIMEOUT")]
        [TestCase("AUTH_FAILED")]
        [TestCase(null)]
        public void OwnedFailure_EmitsCorrelatedFeedbackOnceWithoutChangingRunSettlement(string category)
        {
            Setup(out var orchestrator, out var driver, out _, out var request);
            var feedback = new List<CognitiveRequestFeedback>();
            driver.RequestFeedback += feedback.Add;
            var failure = new CognitiveTransportFailure(request.request_id, request.run.run_id,
                request.run.agent_entity_id, "COGNITIVE_REQUEST_FAILED", request.trace_id, category);
            Assert.IsTrue(driver.TryConsumeFailure(failure));
            Assert.IsFalse(driver.TryConsumeFailure(failure));
            Assert.AreEqual(CognitiveRunState.Failed, orchestrator.LastRun.State);
            Assert.IsNull(driver.PendingRequest);
            Assert.AreEqual(1, feedback.Count);
            Assert.AreEqual(request.request_id, feedback[0].RequestId);
            Assert.AreEqual("conversation_01", feedback[0].ConversationId);
            Assert.AreEqual(category ?? "COGNITIVE_REQUEST_FAILED", feedback[0].Reason);
        }
        [Test] public void CapacityFailureRejectsStaleOwnershipBeforeUpdatingPersistentFeedback()
        {
            Setup(out _,out var driver,out _,out var request);
            var condition=ContextCapacityFeedbackTests.Check(identity:'f');
            condition.condition_id=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
            var status=ContextCapacityFeedbackTests.Status(condition);
            int before=ContextCapacityFeedback.Current.Errors.Count;
            Assert.IsFalse(driver.TryConsumeFailure(new CognitiveTransportFailure("old_request",request.run.run_id,
                request.run.agent_entity_id,"COGNITIVE_REQUEST_FAILED",request.trace_id,"CONTEXT_BUDGET_EXCEEDED",status)));
            Assert.AreEqual(before,ContextCapacityFeedback.Current.Errors.Count);
            Assert.IsTrue(driver.TryConsumeFailure(new CognitiveTransportFailure(request.request_id,request.run.run_id,
                request.run.agent_entity_id,"COGNITIVE_REQUEST_FAILED",request.trace_id,"CONTEXT_BUDGET_EXCEEDED",status)));
            Assert.AreEqual(before+1,ContextCapacityFeedback.Current.Errors.Count);
            condition.state="ok";condition.provider_verified=true;
            ContextCapacityFeedback.Current.Observe(status);
            Assert.AreEqual(before,ContextCapacityFeedback.Current.Errors.Count);
        }

        [Test]
        public void ClientWaitTimeout_ReportsUnknownServerResultWithoutResubmission()
        {
            Setup(out var orchestrator, out var driver, out _, out var request);
            CognitiveRequestFeedback feedback = null;
            driver.RequestFeedback += value => feedback = value;
            var wait = (IEnumerator)typeof(CognitiveRunDriver).GetMethod("FailAfterTimeout",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, new object[] { request });
            Assert.IsTrue(wait.MoveNext()); Assert.IsFalse(wait.MoveNext());
            Assert.AreEqual("CLIENT_TIMEOUT", feedback.Reason);
            Assert.AreEqual(CognitiveRunState.Failed, orchestrator.LastRun.State);
            Assert.IsNull(driver.PendingRequest);
        }

        [TestCase("AUTH_FAILED")]
        [TestCase("CONTEXT_BUDGET_EXCEEDED")]
        [TestCase("CONTEXT_LENGTH_EXCEEDED")]
        [TestCase("OUTPUT_RESERVE_EXCEEDS_CONTEXT")]
        public void BackendErrorEnvelope_DiagnosticReachesOwnedFeedbackWithoutChangingIngressReason(string diagnostic)
        {
            Setup(out var orchestrator, out var driver, out var presenter, out var request);
            var backend = driver.gameObject.AddComponent<BackendWebSocketClient>();
            driver.Configure(driver.GetComponent<WorldEntity>(), orchestrator,
                driver.GetComponent<CognitiveContextComposer>(), backend, presenter);
            CognitiveRequestFeedback feedback = null;
            driver.RequestFeedback += value => feedback = value;
            string json = "{\"message_type\":\"error\",\"request_id\":\"" + request.request_id +
                "\",\"trace_id\":\"" + request.trace_id + "\",\"run_id\":\"" + request.run.run_id +
                "\",\"agent_entity_id\":\"" + request.run.agent_entity_id +
                "\",\"reason\":\"COGNITIVE_REQUEST_FAILED\",\"diagnostic_category\":\"" + diagnostic + "\"}";
            Assert.IsTrue(backend.ProcessIncomingJson(json));
            Assert.AreEqual("COGNITIVE_REQUEST_FAILED", backend.LastCognitiveIngressStatus);
            Assert.AreEqual(diagnostic, feedback.Reason);
            var text = EmbodiedNPC.Settings.P0FeedbackText.Dialogue(feedback.Reason);
            if (diagnostic != "AUTH_FAILED")
            {
                StringAssert.Contains("上下文", text.Chinese);
                StringAssert.Contains("context", text.English);
            }
            Assert.AreEqual(request.trace_id, feedback.TraceId);
            Assert.AreEqual(CognitiveRunState.Failed, orchestrator.LastRun.State);
            Assert.AreEqual(0, presenter.Count);
        }

        [Test]
        public void DelayedActionRejection_CannotSettleOrPresentTheNextRun()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest first);
            BackendWebSocketClient backend = driver.gameObject.AddComponent<BackendWebSocketClient>();
            driver.Configure(driver.GetComponent<WorldEntity>(), orchestrator,
                driver.GetComponent<CognitiveContextComposer>(), backend, presenter);
            CognitiveRun firstRun = orchestrator.CurrentRun;
            string delayed = "{\"message_type\":\"error\",\"request_id\":\"" + first.request_id +
                "\",\"trace_id\":\"" + first.trace_id + "\",\"run_id\":\"" + firstRun.RunId +
                "\",\"agent_entity_id\":\"" + firstRun.AgentEntityId +
                "\",\"reason\":\"INVALID_ACTION_PROPOSAL\"}";

            Assert.That(orchestrator.CancelCurrentRun(), Is.True);
            CognitiveTrigger next = new("trigger_02", CognitiveTriggerTypes.Event,
                firstRun.AgentEntityId, "event_02", 2d, AgentEventTypes.PlayerEnteredArea);
            Assert.That(orchestrator.TryStartRun(next, out CognitiveRun nextRun), Is.True);
            CognitiveRequest nextRequest = Request(nextRun, next);
            nextRequest.request_id = "request_02";
            nextRequest.trace_id = "trace_02";
            nextRequest.context.current_event.event_id = "event_02";
            Assert.That(driver.TryOwnRequest(nextRequest), Is.True);

            Assert.That(backend.ProcessIncomingJson(delayed), Is.True);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(nextRun));
            Assert.That(driver.PendingRequest, Is.SameAs(nextRequest));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presenter.Count, Is.Zero);

            Assert.That(driver.TryConsumeResponse(Response(nextRequest, "DIRECT_RESPONSE")), Is.True);
            Assert.That(nextRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(presenter.Count, Is.EqualTo(1));
            Assert.That(backend.ProcessIncomingJson(delayed), Is.True);
            Assert.That(nextRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(presenter.Count, Is.EqualTo(1));
        }

        [Test]
        public void DirectResponseEnd_PresentsPropagatesDirectiveAndCompletesOwningRun()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver,
                out List<PresentedCognitiveUtterance> presentations);

            Assert.That(driver.TryConsumeResponse(Response(request, "DIRECT_RESPONSE", EpisodeDirectives.End)),
                Is.True);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(presenter.Count, Is.EqualTo(1));
            Assert.That(presenter.LastUtterance, Is.EqualTo("欢迎光临。"));
            Assert.That(presentations, Has.Count.EqualTo(1));
            Assert.That(presentations[0].EpisodeDirective, Is.EqualTo(EpisodeDirectives.End));
            Assert.That(experiences, Has.Count.EqualTo(1));
            ExperienceDraft response = experiences[0];
            Assert.That(response.EventType, Is.EqualTo("dialogue_response"));
            Assert.That(response.ObserverId, Is.EqualTo("npc_cafe_01"));
            Assert.That(response.ActorEntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(response.TargetEntityIds, Is.EqualTo(new[] { "player_01" }));
            Assert.That(response.NaturalLanguagePayload, Is.EqualTo("欢迎光临。"));
            Assert.That(response.RequestId, Is.EqualTo("request_01"));
            Assert.That(response.TraceId, Is.EqualTo("trace_01"));
            Assert.That(response.ConversationId, Is.EqualTo("conversation_01"));
            Assert.That(response.SourceEventId, Is.EqualTo("event_01"));
            Assert.That(response.StructuredPayload["episode_directive"], Is.EqualTo(EpisodeDirectives.End));
        }

        [Test]
        public void PresentationFailure_FailsOwningRunWithoutResponseExperience()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            presenter.ShouldAccept = false;
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);

            Assert.That(driver.TryConsumeResponse(Response(request, "DIRECT_RESPONSE")), Is.False);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void WrongRequestRunOrAgent_IsRejectedWithoutPresentationOrCompletion()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);

            CognitiveResponse wrongRequest = Response(request, "DIRECT_RESPONSE");
            wrongRequest.request_id = "wrong_request";
            Assert.That(driver.TryConsumeResponse(wrongRequest), Is.False);
            CognitiveResponse wrongRun = Response(request, "DIRECT_RESPONSE");
            wrongRun.run_id = "wrong_run";
            Assert.That(driver.TryConsumeResponse(wrongRun), Is.False);
            CognitiveResponse wrongAgent = Response(request, "DIRECT_RESPONSE");
            wrongAgent.agent_entity_id = "npc_other";
            Assert.That(driver.TryConsumeResponse(wrongAgent), Is.False);

            Assert.That(orchestrator.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(request));
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void WrongTraceDirectResponse_IsRejectedWithoutPresentation()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);
            CognitiveResponse response = Response(request, "DIRECT_RESPONSE");
            response.trace_id = "unowned_trace";

            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(orchestrator.HasActiveRun, Is.True);
            Assert.That(orchestrator.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(request));
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void WrongTraceActionProposal_IsRejectedBeforePresentationOrGoalHandoff()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);
            CognitiveResponse response = ActionResponse(request);
            response.trace_id = "unowned_trace";

            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(orchestrator.HasActiveRun, Is.True);
            Assert.That(orchestrator.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(request));
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void RegularCustomerDriver_RejectsMerchantOwnedResponse()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request,
                "npc_cafe_regular_01", "cafe.regular_customer");
            CognitiveResponse merchantResponse = Response(request, "DIRECT_RESPONSE");
            merchantResponse.agent_entity_id = "npc_cafe_01";

            Assert.That(driver.TryConsumeResponse(merchantResponse), Is.False);
            Assert.That(orchestrator.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.PendingRequest, Is.SameAs(request));
            Assert.That(presenter.Count, Is.Zero);
        }

        [Test]
        public void NoAction_CompletesWithoutPresentation()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);

            Assert.That(driver.TryConsumeResponse(Response(request, "NO_ACTION", EpisodeDirectives.End)), Is.True);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void CognitiveResponseJson_AcceptsCanonicalDirectiveAndRejectsTransition()
        {
            const string json = "{\"message_type\":\"cognitive_response\",\"schema_version\":\"1.0\"," +
                "\"request_id\":\"request_01\",\"trace_id\":\"trace_01\",\"run_id\":\"run_01\"," +
                "\"agent_entity_id\":\"npc_cafe_01\",\"decision\":{" +
                "\"decision_kind\":\"DIRECT_RESPONSE\",\"episode_directive\":\"END\"," +
                "\"utterance\":\"明天见。\",\"emotion\":\"neutral\",\"gesture\":\"none\"," +
                "\"look_at\":\"player_01\",\"action_proposal\":null},\"validated_goal\":null}";

            Assert.That(CognitiveResponseValidator.TryParse(json, out CognitiveResponse response, out _), Is.True);
            Assert.That(response.decision.episode_directive, Is.EqualTo(EpisodeDirectives.End));
            Assert.That(CognitiveResponseValidator.TryParse(
                json.Replace("\"END\"", "\"TRANSITION\""), out _, out _), Is.False);
        }

        [Test]
        public void ActionProposalEnd_IsRejectedBeforePresentationOrHandoff()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            CognitiveResponse response = ActionResponse(request, EpisodeDirectives.End);

            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(response.IsValid, Is.False);
            Assert.That(orchestrator.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presenter.Count, Is.Zero);
        }

        [Test]
        public void SuccessfulActionHandoff_WaitsWithSameActiveRunAndOwnership()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out _, out _);
            CognitiveRun run = orchestrator.CurrentRun;
            CognitiveActionOutcomeOwnership ownership = Ownership(run);

            Assert.That(driver.TryBeginActionWait(ownership), Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(orchestrator.CurrentRun, Is.SameAs(run));
            Assert.That(orchestrator.HasActiveRun, Is.True);
            Assert.That(driver.PendingActionOutcome, Is.SameAs(ownership));
        }

        [Test]
        public void CorrelatedSucceededOutcome_CompletesSameRunAndClearsOwnership()
        {
            SetupWaiting(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);

            Assert.That(driver.TryConsumeActionOutcome(Outcome(ownership, PlanExecutionStatus.Succeeded)), Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(orchestrator.LastRun, Is.SameAs(run));
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(driver.PendingActionOutcome, Is.Null);
        }

        [Test]
        public void CorrelatedFailedOutcome_FailsSameRunWithoutCreatingSecondRun()
        {
            SetupWaiting(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);
            WakeCognitiveTriggerBridge bridge = orchestrator.TriggerSource;
            AgentEventReceiver receiver = bridge.EventReceiver;

            Assert.That(driver.TryConsumeActionOutcome(Outcome(ownership, PlanExecutionStatus.Failed)), Is.True);
            Assert.That(receiver.Receive(ActionFailedEvent(ownership)), Is.True);

            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.LastRun, Is.SameAs(run));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(bridge.LastTrigger, Is.Null);
            Assert.That(receiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(receiver.LastEvent.EventType, Is.EqualTo(AgentEventTypes.ActionFailed));
        }

        [Test]
        public void CorrelatedCancelledOutcome_CancelsSameRun()
        {
            SetupWaiting(out _, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);

            Assert.That(driver.TryConsumeActionOutcome(Outcome(ownership, PlanExecutionStatus.Cancelled)), Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Cancelled));
        }

        [Test]
        public void CorrelatedTimedOutOutcome_FailsSameRun()
        {
            SetupWaiting(out _, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);

            Assert.That(driver.TryConsumeActionOutcome(Outcome(ownership, PlanExecutionStatus.TimedOut)), Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Failed));
        }

        [Test]
        public void WrongProposalId_IsIgnoredWhileRunKeepsWaiting()
        {
            SetupWaiting(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);

            GoalOutcome wrong = new("outcome_wrong", "other_proposal", ownership.ProposalRevision,
                ownership.TraceId, "bring_object", ownership.AgentEntityId, "cup_01", "player_01",
                PlanExecutionStatus.Succeeded, string.Empty, 99, 2d);
            Assert.That(driver.TryConsumeActionOutcome(wrong), Is.False);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(orchestrator.HasActiveRun, Is.True);
            Assert.That(driver.PendingActionOutcome, Is.SameAs(ownership));
        }

        [Test]
        public void WrongProposalRevision_IsIgnoredWhileRunKeepsWaiting()
        {
            SetupWaiting(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership);

            GoalOutcome wrong = new("outcome_wrong", ownership.ProposalId, ownership.ProposalRevision + 1,
                ownership.TraceId, "bring_object", ownership.AgentEntityId, "cup_01", "player_01",
                PlanExecutionStatus.Succeeded, string.Empty, 0, 2d);
            Assert.That(driver.TryConsumeActionOutcome(wrong), Is.False);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(orchestrator.HasActiveRun, Is.True);
        }

        [Test]
        public void ActionHandoffFailure_FailsWithoutEnteringWaitState()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            request.context.current_event.target_entity_id = request.run.agent_entity_id;
            request.context.relevant_player = new CognitivePlayerDto
                { player_entity_id = "player_01", display_name = "玩家" };
            CognitiveRun run = orchestrator.CurrentRun;
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver,
                out List<PresentedCognitiveUtterance> presentations);

            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(driver.PendingActionOutcome, Is.Null);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(presentations, Is.Empty);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void PassiveActionProposal_IsRejectedBeforePresentationOrHandoff()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            GoalPlanRunner runner = AttachRejectingHandoff(driver, orchestrator, presenter);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver,
                out List<PresentedCognitiveUtterance> presentations);

            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(presentations, Is.Empty);
            Assert.That(experiences, Is.Empty);
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(runner.ActiveProposalId, Is.Empty);
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));
        }

        [Test]
        public void AdmissionReject_DoesNotPresentActionCommitment()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            request.context.current_event.target_entity_id = request.run.agent_entity_id;
            request.context.relevant_player = new CognitivePlayerDto
                { player_entity_id = "player_01", display_name = "玩家" };
            GoalPlanRunner runner = AttachRejectingHandoff(driver, orchestrator, presenter);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver,
                out List<PresentedCognitiveUtterance> presentations);
            LogAssert.Expect(LogType.Warning, new Regex("\\[GoalPlan\\] REJECTED code=GoalMissing"));

            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(runner.ActiveProposalId, Is.Not.Empty); // The runner was reached.
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(presentations, Is.Empty);
            Assert.That(experiences, Is.Empty);
        }

        [Test]
        public void FailedCognitiveRun_TaskFailureTicketCanOnlyExplainItsOwnedProposal()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            request.context.current_event.target_entity_id = request.run.agent_entity_id;
            request.context.relevant_player = new CognitivePlayerDto
                { player_entity_id = "player_01", display_name = "玩家" };
            GoalPlanRunner runner = AttachRejectingHandoff(driver, orchestrator, presenter);
            BackendWebSocketClient backend = driver.gameObject.AddComponent<BackendWebSocketClient>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GoalPlanRunner).GetMethod("OnDisable", flags).Invoke(runner, null);
            typeof(GoalPlanRunner).GetField("backendClient", flags).SetValue(runner, backend);
            typeof(GoalPlanRunner).GetMethod("OnEnable", flags).Invoke(runner, null);
            CognitiveRun originalRun = orchestrator.CurrentRun;
            int outcomes = 0;
            runner.GoalOutcomePublished += _ => outcomes++;
            LogAssert.Expect(LogType.Warning, new Regex("\\[GoalPlan\\] REJECTED code=GoalMissing"));

            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(driver.PendingActionOutcome, Is.Null);
            Assert.That(runner.ActiveProposalId,
                Is.EqualTo(GoalPlanRunner.NormalizeProposalId(request.request_id)));
            Assert.That(runner.ActiveProposalRevision, Is.Zero);
            Assert.That(runner.ActiveTraceId, Is.EqualTo(request.trace_id));
            Assert.That(runner.HasActiveTask, Is.False);

            // Simulate the exact ticket that SendSemanticFailureAsync registers only when connected.
            // Initial exact admission has no authorized replacement, so revision cannot start an action.
            TaskFailureReport report = new()
            {
                request_id = "failure-request-owned", trace_id = "failure-trace-owned",
                world_revision = 0, proposal_id = runner.ActiveProposalId,
                proposal_revision = runner.ActiveProposalRevision,
                failure_stage = "proposal_validation", failure_code = "GoalMissing",
                failed_goal = ActionResponse(request).validated_goal,
                involved_entity_ids = new[] { "cup_01", "player_01" },
                retryable = false, allowed_alternatives = System.Array.Empty<TaskAlternative>(),
            };
            Assert.That(report.IsValid, Is.True);
            var inbox = (TaskFailureResponseInbox)typeof(BackendWebSocketClient)
                .GetProperty("FailureInbox", flags).GetValue(backend);
            Assert.That(inbox.Register(report), Is.True);
            typeof(GoalPlanRunner).GetField("pendingFailureRequestId", flags)
                .SetValue(runner, report.request_id);
            Assert.That(backend.PendingRequestCount, Is.EqualTo(1));
            Assert.That(runner.HasActiveTask, Is.True); // Owned continuation ticket, no executing plan.

            string Reply(string requestId, string traceId, string proposalId, int revision,
                string resolution, string selected) =>
                "{\"message_type\":\"task_failure_response\",\"schema_version\":\"1.0\"," +
                $"\"request_id\":\"{requestId}\",\"trace_id\":\"{traceId}\"," +
                $"\"world_revision\":0,\"proposal_id\":\"{proposalId}\"," +
                $"\"proposal_revision\":{revision},\"resolution\":\"{resolution}\"," +
                "\"utterance\":\"目前无法执行。\",\"emotion\":\"neutral\",\"gesture\":\"none\"," +
                $"\"selected_alternative_id\":\"{selected}\"}}";

            Assert.That(backend.ProcessIncomingJson(Reply("other", report.trace_id,
                report.proposal_id, 1, "explain", "")), Is.False);
            Assert.That(backend.ProcessIncomingJson(Reply(report.request_id, "other-trace",
                report.proposal_id, 1, "explain", "")), Is.False);
            Assert.That(backend.ProcessIncomingJson(Reply(report.request_id, report.trace_id,
                "other-proposal", 1, "explain", "")), Is.False);
            Assert.That(backend.ProcessIncomingJson(Reply(report.request_id, report.trace_id,
                report.proposal_id, 2, "explain", "")), Is.False);
            Assert.That(backend.ProcessIncomingJson(Reply(report.request_id, report.trace_id,
                report.proposal_id, 1, "revise", "invented-target")), Is.False);
            Assert.That(backend.PendingRequestCount, Is.EqualTo(1));
            Assert.That(runner.ActiveProposalRevision, Is.Zero);

            GoalPlanPresentation presentation = default;
            runner.PresentationReady += value => presentation = value;
            string valid = Reply(report.request_id, report.trace_id, report.proposal_id,
                1, "explain", "");
            Assert.That(backend.ProcessIncomingJson(valid), Is.True);
            Assert.That(presentation.Kind, Is.EqualTo(GoalPlanPresentationKind.Clarification));
            Assert.That(presentation.Code, Is.EqualTo("TASK_EXPLAIN"));
            Assert.That(backend.ProcessIncomingJson(valid), Is.False); // Single-use ticket.
            Assert.That(backend.PendingRequestCount, Is.Zero);
            Assert.That(runner.ActiveProposalId, Is.Empty);
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(runner.IsExecuting, Is.False);
            Assert.That(outcomes, Is.Zero);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(driver.PendingActionOutcome, Is.Null);
            Assert.That(presenter.Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator FailedCognitiveRun_ConnectedTaskFailureReportKeepsExactLineage()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TaskCompletionSource<bool> releaseSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> reportReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task serverTask = Task.Run(() =>
            {
                try
                {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                List<byte> headerBytes = new();
                while (headerBytes.Count < 16384)
                {
                    int next = stream.ReadByte();
                    if (next < 0) throw new EndOfStreamException();
                    headerBytes.Add((byte)next);
                    int n = headerBytes.Count;
                    if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 &&
                        headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                }
                string headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                string key = null;
                foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                if (key == null) throw new InvalidDataException("Missing WebSocket key");
                byte[] hash;
                using (SHA1 sha = SHA1.Create())
                    hash = sha.ComputeHash(Encoding.ASCII.GetBytes(
                        key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                string handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
                    "Connection: Upgrade\r\nSec-WebSocket-Accept: " + Convert.ToBase64String(hash) + "\r\n\r\n";
                byte[] handshakeBytes = Encoding.ASCII.GetBytes(handshake);
                stream.Write(handshakeBytes, 0, handshakeBytes.Length);

                int first = stream.ReadByte();
                int second = stream.ReadByte();
                if (first < 0 || second < 0 || (first & 15) != 1 || (second & 128) == 0)
                    throw new InvalidDataException("Expected masked text frame");
                long length = second & 127;
                if (length == 126) length = (stream.ReadByte() << 8) | stream.ReadByte();
                else if (length == 127)
                {
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | (byte)stream.ReadByte();
                }
                if (length > 8192) throw new InvalidDataException("Oversized report");
                byte[] mask = ReadExactly(stream, 4);
                byte[] payload = ReadExactly(stream, (int)length);
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                reportReceived.TrySetResult(Encoding.UTF8.GetString(payload));
                releaseSocket.Task.Wait();
                }
                catch (Exception exception) { reportReceived.TrySetException(exception); throw; }
            });

            byte[] ReadExactly(Stream stream, int length)
            {
                byte[] value = new byte[length];
                int offset = 0;
                while (offset < length)
                {
                    int read = stream.Read(value, offset, length - offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                return value;
            }

            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            request.context.current_event.target_entity_id = request.run.agent_entity_id;
            request.context.relevant_player = new CognitivePlayerDto
                { player_entity_id = "player_01", display_name = "玩家" };
            GoalPlanRunner runner = AttachRejectingHandoff(driver, orchestrator, presenter);
            BackendWebSocketClient backend = driver.gameObject.AddComponent<BackendWebSocketClient>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(BackendWebSocketClient).GetField("serverUrl", flags)
                .SetValue(backend, $"ws://127.0.0.1:{port}/ws/");
            typeof(BackendWebSocketClient).GetField("connectOnStart", flags).SetValue(backend, false);
            typeof(GoalPlanRunner).GetMethod("OnDisable", flags).Invoke(runner, null);
            typeof(GoalPlanRunner).GetField("backendClient", flags).SetValue(runner, backend);
            typeof(GoalPlanRunner).GetMethod("OnEnable", flags).Invoke(runner, null);
            backend.ConnectFromContextMenu();
            float deadline = Time.realtimeSinceStartup + 8f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(backend.IsConnected, Is.True,
                $"status={backend.ConnectionStatus} attempts={backend.ConnectionAttempts} server={serverTask.Status} error={serverTask.Exception}");

            CognitiveRun originalRun = orchestrator.CurrentRun;
            LogAssert.Expect(LogType.Warning, new Regex("\\[GoalPlan\\] REJECTED code=GoalMissing"));
            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            while (!reportReceived.Task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(reportReceived.Task.IsCompletedSuccessfully, Is.True,
                $"server={serverTask.Status} error={serverTask.Exception}");
            TaskFailureReport report = JsonUtility.FromJson<TaskFailureReport>(reportReceived.Task.Result);
            Assert.That(report.IsValid, Is.True);
            Assert.That(report.proposal_id, Is.EqualTo(runner.ActiveProposalId));
            Assert.That(report.proposal_revision, Is.Zero);
            Assert.That(runner.ActiveTraceId, Is.EqualTo(request.trace_id));
            Assert.That(report.allowed_alternatives, Is.Empty);
            Assert.That(backend.PendingRequestCount, Is.EqualTo(1));
            Assert.That(runner.HasActiveTask, Is.True);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(driver.PendingActionOutcome, Is.Null);

            string response = "{\"message_type\":\"task_failure_response\",\"schema_version\":\"1.0\"," +
                $"\"request_id\":\"{report.request_id}\",\"trace_id\":\"{report.trace_id}\"," +
                $"\"world_revision\":{report.world_revision},\"proposal_id\":\"{report.proposal_id}\"," +
                "\"proposal_revision\":1,\"resolution\":\"explain\"," +
                "\"utterance\":\"目前无法执行。\",\"emotion\":\"neutral\",\"gesture\":\"none\"," +
                "\"selected_alternative_id\":\"\"}";
            GoalPlanPresentation presentation = default;
            runner.PresentationReady += value => presentation = value;
            Assert.That(backend.ProcessIncomingJson(response), Is.True);
            Assert.That(presentation.Kind, Is.EqualTo(GoalPlanPresentationKind.Clarification));
            Assert.That(backend.ProcessIncomingJson(response), Is.False);
            Assert.That(backend.PendingRequestCount, Is.Zero);
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(runner.IsExecuting, Is.False);
            Assert.That(originalRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(driver.PendingActionOutcome, Is.Null);
            Assert.That(presenter.Count, Is.Zero);
            releaseSocket.TrySetResult(true);
            listener.Stop();
            _ = backend.DisconnectAsync();
        }

        [Test]
        public void PresentationFailureAfterOwnership_DoesNotDiscardStartedActionWait()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            CognitiveRun run = orchestrator.CurrentRun;
            CognitiveActionOutcomeOwnership ownership = new(run.RunId,
                GoalPlanRunner.NormalizeProposalId(request.request_id), 0, request.trace_id,
                run.AgentEntityId);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);
            Assert.That(driver.TryBeginActionWait(ownership), Is.True);
            presenter.ShouldAccept = false;

            Assert.That(driver.TryPresentAcceptedAction(request, "好的，我去拿杯子。", null), Is.False);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(driver.PendingActionOutcome, Is.SameAs(ownership));
            Assert.That(orchestrator.CurrentRun, Is.SameAs(run));
            Assert.That(experiences, Is.Empty);
            Assert.That(driver.TryConsumeActionOutcome(Outcome(ownership, PlanExecutionStatus.Succeeded)),
                Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(driver.PendingActionOutcome, Is.Null);
        }

        [Test]
        public void AcceptedActionUtterance_WithConversation_PreservesDialogueExperience()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            var run = orchestrator.CurrentRun;
            var ownership = new CognitiveActionOutcomeOwnership(run.RunId,
                GoalPlanRunner.NormalizeProposalId(request.request_id), 0, request.trace_id, run.AgentEntityId);
            var experiences = CapturePresentedExperiences(driver, out var presentations);
            Assert.That(driver.TryBeginActionWait(ownership), Is.True);
            Assert.That(driver.TryPresentAcceptedAction(request, "好的，我去拿杯子。", null), Is.True);
            Assert.That(presenter.Count, Is.EqualTo(1));
            Assert.That(presentations, Has.Count.EqualTo(1));
            Assert.That(presentations[0].IsDialogueEvidence, Is.True);
            Assert.That(experiences, Has.Count.EqualTo(1));
            Assert.That(experiences[0].EventType, Is.EqualTo("dialogue_response"));
            Assert.That(experiences[0].ConversationId, Is.EqualTo("conversation_01"));
            Assert.That(experiences[0].NaturalLanguagePayload, Is.EqualTo("好的，我去拿杯子。"));
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
        }

        [Test]
        public void MissingOrInconsistentEventOrigin_CannotAuthorizeAction()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            request.context.current_event = null;
            Assert.That(request.IsValid, Is.True); // Cognition may still return a direct response.
            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));

            CognitiveRequest mismatched = Request(orchestrator.LastRun,
                new CognitiveTrigger("trigger_01", CognitiveTriggerTypes.Event,
                    "npc_cafe_01", "event_01", 1d));
            mismatched.run.trigger_type = "goal";
            Assert.That(mismatched.IsValid, Is.False);
            mismatched.run.trigger_type = CognitiveTriggerTypes.Event;
            mismatched.trigger.source_event_id = "other_event";
            Assert.That(mismatched.IsValid, Is.False);
        }

        [Test]
        public void WorldActionEligibility_WhitespaceWireValuesMatchBackendPolicy()
        {
            Setup(out AgentOrchestrator orchestrator, out _, out _, out CognitiveRequest request);
            request.context.current_event.event_type = AgentEventTypes.PlayerUtterance;
            request.context.current_event.target_entity_id = request.run.agent_entity_id;
            request.context.relevant_player = new CognitivePlayerDto
                { player_entity_id = "player_01", display_name = "玩家" };
            CognitiveRun run = orchestrator.CurrentRun;
            WorldEntity owner = orchestrator.GetComponent<WorldEntity>();
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.True);

            request.context.current_event.trace_id = "  ";
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.True);
            request.context.current_event.trace_id = "other_trace";
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.False);
            request.context.current_event.trace_id = null;

            request.context.current_event.source_entity_id = "  ";
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.False);
            request.context.current_event.source_entity_id = "player_01";
            request.trigger.source_event_id = "  ";
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.False);
            request.context.current_event.event_id = "  ";
            Assert.That(WorldActionEligibility.MayProposeNewWorldAction(request, run, owner), Is.False);
        }

        [Test]
        public void CancelledOrWrongAgentActionResponse_CannotPresentOrStartAction()
        {
            Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
                out CognitiveTestPresenter presenter, out CognitiveRequest request);
            List<ExperienceDraft> experiences = CapturePresentedExperiences(driver, out _);
            CognitiveResponse otherAgent = ActionResponse(request);
            otherAgent.agent_entity_id = "npc_cafe_regular_01";
            Assert.That(driver.TryConsumeResponse(otherAgent), Is.False);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(driver.HasPendingActionOutcome, Is.False);

            Assert.That(orchestrator.CancelCurrentRun(), Is.True);
            Assert.That(driver.TryConsumeResponse(ActionResponse(request)), Is.False);
            Assert.That(presenter.Count, Is.Zero);
            Assert.That(experiences, Is.Empty);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
        }

        [Test]
        public void OtherNpcRequest_CannotReplaceThisNpcsPendingActionOrigin()
        {
            Setup(out _, out _, out _, out CognitiveRequest merchantRequest);
            Setup(out AgentOrchestrator customerRun, out CognitiveRunDriver customerDriver,
                out CognitiveTestPresenter customerPresenter, out CognitiveRequest customerRequest,
                "npc_cafe_regular_01", "cafe.regular_customer");

            Assert.That(customerDriver.TryOwnRequest(merchantRequest), Is.False);
            Assert.That(customerDriver.PendingRequest, Is.SameAs(customerRequest));
            Assert.That(customerRun.CurrentRun.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(customerPresenter.Count, Is.Zero);
        }

        [Test]
        public void UnownedSelfActionFailure_StillCreatesWakeTrigger()
        {
            Setup(out AgentOrchestrator orchestrator, out _, out _, out _);
            Assert.That(orchestrator.FailCurrentRun(), Is.True);
            CognitiveActionOutcomeOwnership unowned = new("other_run", "other_proposal", 3,
                "other_trace", "npc_cafe_01");

            Assert.That(orchestrator.TriggerSource.EventReceiver.Receive(ActionFailedEvent(unowned)), Is.True);
            Assert.That(orchestrator.TriggerSource.LastTrigger, Is.Not.Null);
            Assert.That(orchestrator.HasActiveRun, Is.True);
        }

        private void Setup(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
            out CognitiveTestPresenter presenter, out CognitiveRequest request,
            string entityId = "npc_cafe_01", string characterId = "cafe.merchant")
        {
            GameObject root = new("Merchant");
            objects.Add(root);
            WorldEntity entity = root.AddComponent<WorldEntity>();
            entity.Configure(entityId, "character.npc.test", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = root.AddComponent<AgentEventReceiver>();
            EventInterestProfile profile = ScriptableObject.CreateInstance<EventInterestProfile>();
            objects.Add(profile);
            profile.Configure(EventInterestDecision.Ignore);
            receiver.Configure(entity, profile: profile);
            WakeCognitiveTriggerBridge bridge = root.AddComponent<WakeCognitiveTriggerBridge>();
            bridge.Configure(receiver);
            orchestrator = root.AddComponent<AgentOrchestrator>();
            orchestrator.Configure(entity, bridge);
            CognitiveContextComposer composer = root.AddComponent<CognitiveContextComposer>();
            presenter = root.AddComponent<CognitiveTestPresenter>();
            driver = root.AddComponent<CognitiveRunDriver>();
            driver.Configure(entity, orchestrator, composer, null, presenter);

            CognitiveTrigger trigger = new("trigger_01", CognitiveTriggerTypes.Event, entity.EntityId,
                "event_01", 1d, AgentEventTypes.PlayerEnteredArea);
            Assert.That(orchestrator.TryStartRun(trigger, out CognitiveRun run), Is.True);
            request = Request(run, trigger, characterId);
            Assert.That(driver.TryOwnRequest(request), Is.True);
        }

        private static GoalPlanRunner AttachRejectingHandoff(CognitiveRunDriver driver,
            AgentOrchestrator orchestrator, CognitiveTestPresenter presenter)
        {
            GameObject root = driver.gameObject;
            GoalPlanRunner runner = root.AddComponent<GoalPlanRunner>();
            root.AddComponent<CognitiveActionHandoff>();
            driver.Configure(root.GetComponent<WorldEntity>(), orchestrator,
                root.GetComponent<CognitiveContextComposer>(), null, presenter);
            return runner;
        }

        private static CognitiveRequest Request(CognitiveRun run, CognitiveTrigger trigger,
            string characterId = "cafe.merchant")
        {
            CognitiveTriggerDto triggerDto = CognitiveTriggerDto.From(trigger);
            return new CognitiveRequest
            {
                request_id = "request_01",
                trace_id = "trace_01",
                run = CognitiveRunDto.From(run),
                trigger = triggerDto,
                context = new CognitiveContextSnapshot
                {
                    run_id = run.RunId,
                    agent_entity_id = run.AgentEntityId,
                    trigger = triggerDto,
                    current_event = new CognitiveEventDto
                    {
                        event_id = "event_01", event_type = AgentEventTypes.PlayerEnteredArea,
                        source_entity_id = "player_01", area_entity_id = "area_cafe_counter",
                        world_revision = 7, has_world_revision = true,
                        payload = new[]
                        {
                            new CognitiveKeyValueDto
                                { key = AgentEventPayloadKeys.ConversationId, value = "conversation_01" },
                        },
                    },
                    character = new CognitiveCharacterDto
                    {
                        character_id = characterId,
                        display_name = characterId == "cafe.merchant" ? "店员" : "顾客",
                        role_summary = "咖啡馆店员", speaking_style = "友好简洁",
                        backstory = "在社区咖啡馆工作。", personality = "温和。", boundaries = "不编造事实。",
                    },
                    perception = PerceptionSnapshotDto.Empty(run.AgentEntityId),
                },
            };
        }

        private static CognitiveResponse Response(CognitiveRequest request, string kind,
            string episodeDirective = null) => new()
        {
            message_type = "cognitive_response",
            schema_version = "1.0",
            request_id = request.request_id,
            trace_id = request.trace_id,
            run_id = request.run.run_id,
            agent_entity_id = request.run.agent_entity_id,
            decision = kind == "NO_ACTION"
                ? new CognitiveDecision { decision_kind = kind, episode_directive = episodeDirective }
                : new CognitiveDecision
                {
                    decision_kind = kind, utterance = "欢迎光临。", emotion = "neutral",
                    gesture = "none", look_at = "player_01", episode_directive = episodeDirective,
                },
        };

        private static List<ExperienceDraft> CapturePresentedExperiences(CognitiveRunDriver driver,
            out List<PresentedCognitiveUtterance> presentations)
        {
            var values = new List<ExperienceDraft>();
            presentations = new List<PresentedCognitiveUtterance>();
            List<PresentedCognitiveUtterance> captured = presentations;
            var adapter = new DialogueExperienceAdapter(values.Add);
            driver.UtterancePresented += value =>
            {
                captured.Add(value);
                adapter.Handle(value);
            };
            return values;
        }

        private void SetupWaiting(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver,
            out CognitiveRun run, out CognitiveActionOutcomeOwnership ownership)
        {
            Setup(out orchestrator, out driver, out _, out _);
            run = orchestrator.CurrentRun;
            ownership = Ownership(run);
            Assert.That(driver.TryBeginActionWait(ownership), Is.True);
        }

        private static CognitiveActionOutcomeOwnership Ownership(CognitiveRun run) =>
            new(run.RunId, "request_01", 0, "trace_01", run.AgentEntityId);

        private static GoalOutcome Outcome(CognitiveActionOutcomeOwnership ownership, PlanExecutionStatus status) =>
            new($"outcome_{status}", ownership.ProposalId, ownership.ProposalRevision, ownership.TraceId,
                "bring_object", ownership.AgentEntityId, "cup_01", "player_01", status,
                status == PlanExecutionStatus.Succeeded ? string.Empty : "TEST_FAILURE", 2, 2d);

        private static AgentEvent ActionFailedEvent(CognitiveActionOutcomeOwnership ownership) =>
            new("event_failed", AgentEventTypes.ActionFailed, ownership.AgentEntityId,
                ownership.AgentEntityId, string.Empty, new Dictionary<string, string>
                {
                    [AgentEventPayloadKeys.ProposalId] = ownership.ProposalId,
                    [AgentEventPayloadKeys.ProposalRevision] = ownership.ProposalRevision.ToString(),
                    [AgentEventPayloadKeys.TraceId] = ownership.TraceId,
                }, 2d);

        private static CognitiveResponse ActionResponse(CognitiveRequest request,
            string episodeDirective = null) => new()
        {
            message_type = "cognitive_response",
            schema_version = "1.0",
            request_id = request.request_id,
            trace_id = request.trace_id,
            run_id = request.run.run_id,
            agent_entity_id = request.run.agent_entity_id,
            decision = new CognitiveDecision
            {
                decision_kind = CognitiveDecisionKinds.ActionProposal,
                episode_directive = episodeDirective,
                utterance = "好的。",
                emotion = "neutral",
                gesture = "none",
                look_at = "player_01",
                action_proposal = new CognitiveActionProposal
                {
                    action_id = "bring_object",
                    bindings = new Dictionary<string, string>
                    {
                        ["target"] = "cup_01",
                        ["destination"] = "player_01",
                    },
                },
            },
            validated_goal = new GoalProposal
            {
                type = "bring_object",
                target_entity_id = "cup_01",
                destination_entity_id = "player_01",
            },
        };
    }
}
