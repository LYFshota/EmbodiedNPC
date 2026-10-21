using System;
using System.Collections;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Dialogue;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    public sealed class CognitiveRequestFeedback
    {
        public CognitiveRequestFeedback(CognitiveRequest request, string status, string reason)
        {
            RequestId = request.request_id; TraceId = request.trace_id;
            AgentEntityId = request.run.agent_entity_id;
            EventType = request.context?.current_event?.event_type;
            if (request.context?.current_event?.payload != null)
                foreach (var pair in request.context.current_event.payload)
                    if (pair?.key == AgentEventPayloadKeys.ConversationId) ConversationId = pair.value;
            Status = status; Reason = reason;
        }
        public string RequestId { get; }
        public string TraceId { get; }
        public string AgentEntityId { get; }
        public string ConversationId { get; }
        public string EventType { get; }
        public string Status { get; }
        public string Reason { get; }
    }

    /// <summary>Memory-agnostic evidence emitted only after the owning run's utterance is presented.</summary>
    public sealed class PresentedCognitiveUtterance
    {
        public PresentedCognitiveUtterance(string requestId, string traceId, string runId,
            string ownerAgentEntityId, string playerEntityId, string utterance, int worldRevision,
            double presentedAt, string sourceEventId, string conversationId, string episodeDirective,
            string dialogueTurnPolicy = null, CognitiveEventDto continuationEvent = null, CognitiveEventDto sourceEvent = null)
        {
            RequestId = requestId ?? string.Empty;
            TraceId = traceId ?? string.Empty;
            RunId = runId ?? string.Empty;
            OwnerAgentEntityId = ownerAgentEntityId ?? string.Empty;
            PlayerEntityId = playerEntityId ?? string.Empty;
            Utterance = utterance ?? string.Empty;
            WorldRevision = Math.Max(0, worldRevision);
            PresentedAt = Math.Max(0d, presentedAt);
            SourceEventId = sourceEventId ?? string.Empty;
            ConversationId = conversationId ?? string.Empty;
            EpisodeDirective = episodeDirective;
            DialogueTurnPolicy = dialogueTurnPolicy;
            ContinuationEvent = continuationEvent;
            SourceEvent = sourceEvent;
        }

        public string RequestId { get; }
        public string TraceId { get; }
        public string RunId { get; }
        public string OwnerAgentEntityId { get; }
        public string PlayerEntityId { get; }
        public string Utterance { get; }
        public int WorldRevision { get; }
        public double PresentedAt { get; }
        public string SourceEventId { get; }
        public string ConversationId { get; }
        public string EpisodeDirective { get; }
        public string DialogueTurnPolicy { get; }
        public CognitiveEventDto ContinuationEvent { get; }
        public CognitiveEventDto SourceEvent { get; }
        public bool IsDialogueEvidence => !string.IsNullOrWhiteSpace(RequestId) &&
                                          !string.IsNullOrWhiteSpace(TraceId) &&
                                          !string.IsNullOrWhiteSpace(RunId) &&
                                          !string.IsNullOrWhiteSpace(OwnerAgentEntityId) &&
                                          !string.IsNullOrWhiteSpace(PlayerEntityId) &&
                                          !string.IsNullOrWhiteSpace(ConversationId) &&
                                          !string.Equals(OwnerAgentEntityId, PlayerEntityId,
                                              StringComparison.Ordinal) &&
                                          !string.IsNullOrWhiteSpace(Utterance);
    }

    internal sealed class CognitiveActionOutcomeOwnership
    {
        public CognitiveActionOutcomeOwnership(string runId, string proposalId, int proposalRevision,
            string traceId, string agentEntityId)
        {
            RunId = runId ?? string.Empty;
            ProposalId = proposalId ?? string.Empty;
            ProposalRevision = Math.Max(0, proposalRevision);
            TraceId = traceId ?? string.Empty;
            AgentEntityId = agentEntityId ?? string.Empty;
        }

        public string RunId { get; }
        public string ProposalId { get; }
        public int ProposalRevision { get; }
        public string TraceId { get; }
        public string AgentEntityId { get; }
    }

    public interface ICognitiveResponsePresenter
    {
        bool TryPresentCognitiveUtterance(string utterance);
    }

    /// <summary>Thin adapter from context requests and transport responses to the owning run terminal state.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    [RequireComponent(typeof(AgentOrchestrator))]
    [RequireComponent(typeof(CognitiveContextComposer))]
    public sealed class CognitiveRunDriver : MonoBehaviour
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private AgentOrchestrator orchestrator;
        [SerializeField] private CognitiveContextComposer composer;
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private MonoBehaviour presentationBehaviour;
        [SerializeField] private CognitiveActionHandoff actionHandoff;
        [SerializeField] private GoalPlanRunner goalPlanRunner;
        [SerializeField, Min(1f)] private float responseTimeoutSeconds = 30f;

        private CognitiveRequest pendingRequest;
        private CognitiveResponse bufferedContinuation;
        private CognitiveResponse bufferedServiceMode;
        private CognitiveResponse bufferedTaskConsideration;
        private bool subscribed;
        private bool outcomeSubscribed;
        private Coroutine timeoutCoroutine;
        private CognitiveActionOutcomeOwnership pendingActionOutcome;
        private CognitiveActionOutcomeOwnership lastSettledActionOutcome;
        private CognitiveRequest actionPresentationRequest;
        private CognitiveRequest settledActionPresentationRequest;
        private GoalOutcome? settledPresentationOutcome;
        private string consumedPresentationOutcomeId;
        private DialogueController dialogueController;
        private string actionConversationId, actionLifecycleReason;

        public CognitiveRequest PendingRequest => pendingRequest;
        /// <summary>The current Run's owned event, which Backend projects as CurrentTask. Never returns an old request.</summary>
        public string CurrentTaskEventType
        {
            get
            {
                CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
                return run != null && pendingRequest?.run != null &&
                       string.Equals(pendingRequest.run.run_id, run.RunId, StringComparison.Ordinal) &&
                       string.Equals(pendingRequest.run.agent_entity_id, owner?.EntityId, StringComparison.Ordinal)
                    ? pendingRequest.context?.current_event?.event_type : null;
            }
        }
        public ICognitiveResponsePresenter Presenter => presentationBehaviour as ICognitiveResponsePresenter;
        public bool? LastSendSucceeded { get; private set; }
        public GoalProposal LastValidatedGoalProposal { get; private set; }
        public string LastDailyModeResult { get; private set; } = "none";
        private string lastActionHandoffResult="none";
        public string LastActionHandoffResult {get=>lastActionHandoffResult;private set {
            lastActionHandoffResult=value;
            try {if(pendingRequest?.task_consideration!=null && value!="none")
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("admission",new Newtonsoft.Json.Linq.JObject {
                    ["task_state"]=value=="action_accepted"?"permitted":"rejected",["admission_result"]=value.Split(':')[0],
                    ["request_id"]=pendingRequest.request_id,["run_id"]=pendingRequest.run.run_id,
                    ["visit_id"]=pendingRequest.task_runtime?.visit_id,["agent_id"]=pendingRequest.run.agent_entity_id });}
            catch(Exception) { }
        }}
        public string LastActionOutcomePresentationResult { get; private set; } = "none";
        internal CognitiveActionOutcomeOwnership PendingActionOutcome => pendingActionOutcome;
        public bool HasPendingActionOutcome => pendingActionOutcome != null;
        public event Action<PresentedCognitiveUtterance> UtterancePresented;
        public event Action<CognitiveRequest, string> ContinuationSettled;
        public event Action<CognitiveRequestFeedback> RequestFeedback;

        private void Awake() => ResolveReferences();
        private void OnEnable()
        {
            ResolveReferences();
            Subscribe();
        }
        private void OnDisable()
        {
            CancelServiceZoneConsideration("driver_disabled");
            CancelContinuation(pendingRequest?.request_id);
            Unsubscribe();
        }
        private void LateUpdate() { FlushContinuationResponse(); FlushServiceModeResponse(); FlushTaskConsideration(); }

        public bool CancelTaskConsideration(string reason)
        {
            if(pendingRequest?.task_consideration==null) return false;
            var request=pendingRequest; pendingRequest=null; bufferedTaskConsideration=null; StopResponseTimeout();
            if(orchestrator?.CurrentRun?.RunId==request.run.run_id) orchestrator.CancelCurrentRun();
            if(backendClient!=null) _=backendClient.SendCognitiveCancelAsync(request);
            PublishRequestFeedback(request,"cancelled",reason); return true;
        }
        public bool FlushTaskConsideration()
        {
            var response=bufferedTaskConsideration; bufferedTaskConsideration=null;
            if(response==null) return false;
            EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("consideration",new Newtonsoft.Json.Linq.JObject {
                ["task_state"]="response_received",["request_id"]=response.request_id,["run_id"]=response.run_id,["trace_id"]=response.trace_id,
                ["agent_id"]=response.agent_entity_id,["decision_kind"]=response.decision.decision_kind });
            var source=GetComponent<TaskConsiderationSource>();
            if(!(source?.Matches(pendingRequest,orchestrator?.CurrentRun) ?? false))
            { CancelTaskConsideration("task_condition_expired"); return false; }
            if(response.decision.decision_kind==CognitiveDecisionKinds.ActionProposal)
            {
                var original=pendingRequest;
                FindAnyObjectByType<TaskRuntimeBridge>()?.TrackReservation(original,response);
                var execution=GetComponent<TaskExecutionBridge>()??gameObject.AddComponent<TaskExecutionBridge>();
                return execution.RequestStart(original,response,backendClient,actionHandoff?.GoalPlanRunner,permitted=>
                {
                    if(!permitted) { if(pendingRequest==original) FailOwnedRun("TASK_START_REJECTED");return; }
                    bool current=pendingRequest==original && source.Matches(original,orchestrator?.CurrentRun);
                    bool started=current && ConsumeOwnedResponse(response);
                    if(!started) execution.NotStarted();
                    source.RecordResult(started?"execution_handoff":"not_started");
                });
            }
            bool accepted=ConsumeOwnedResponse(response);
            source.RecordResult(accepted?"considered":"response_rejected"); return accepted;
        }

        public bool CancelServiceZoneConsideration(string reason) => CancelLowPrioritySystemConsideration(reason);
        public bool CancelLowPrioritySystemConsideration(string reason)
        {
            var request = pendingRequest; var run = orchestrator?.CurrentRun;
            if (request == null || run == null || !run.IsLowPrioritySystemConsideration || run.State != CognitiveRunState.Running ||
                pendingActionOutcome != null || request.run.run_id != run.RunId || request.run.agent_entity_id != owner?.EntityId ||
                (request.context?.current_event?.event_type != AgentEventTypes.PlayerDwelledInServiceZone &&
                 request.context?.current_event?.event_type != AgentEventTypes.CounterNeedsCleaning)) return false;
            pendingRequest = null; bufferedServiceMode = null; StopResponseTimeout();
            orchestrator.CancelCurrentRun();
            if (backendClient != null) _ = backendClient.SendCognitiveCancelAsync(request);
            if (run.IsCounterCleaningConsideration) owner.GetComponent<CounterCleaningCognitionSource>()?.RecordResult("cancelled:" + reason);
            else owner.GetComponent<ServiceZoneDwellCognitionSource>()?.RecordResult("cancelled:" + reason);
            PublishRequestFeedback(request, "cancelled", reason); return true;
        }
        public bool FlushServiceModeResponse()
        {
            var response = bufferedServiceMode; bufferedServiceMode = null;
            var request = pendingRequest; var run = orchestrator?.CurrentRun;
            if (response == null || request == null || run == null || !run.IsLowPrioritySystemConsideration ||
                request.run.run_id != run.RunId || response.run_id != run.RunId || response.request_id != request.request_id ||
                response.trace_id != request.trace_id) return false;
            if (run.IsCounterCleaningConsideration)
            {
                bool current = owner.GetComponent<CounterCleaningCognitionSource>()?.Matches(request, run) ?? false;
                var taskRuntime=FindAnyObjectByType<TaskRuntimeBridge>();
                if(current && response.task_receipt!=null) taskRuntime?.AcceptReceipt(request,response);
                if (current && response.decision.decision_kind == CognitiveDecisionKinds.AutonomousActionIntent)
                {
                    if(taskRuntime?.HasConditionTask(owner.EntityId,taskRuntime.CurrentCondition(owner))==true)
                    { FailOwnedRun("TASK_COUNTER_ALREADY_MANAGED");return false; }
                    bool accepted = ConsumeActionResponse(response, run);
                    owner.GetComponent<CounterCleaningCognitionSource>()?.RecordResult(accepted ? "action_accepted" : "action_rejected");
                    return accepted;
                }
                pendingRequest = null; StopResponseTimeout(); orchestrator.CompleteCurrentRun();
                owner.GetComponent<CounterCleaningCognitionSource>()?.RecordResult(current ? "no_action" : "condition_rejected");
                PublishRequestFeedback(request, "response_received", current ? null : "expired_system_condition"); return current;
            }
            bool valid = owner.GetComponent<ServiceZoneDwellCognitionSource>()?.Matches(request, run) ?? false;
            string reason = "expired_system_condition";
            if (valid && response.decision.decision_kind == CognitiveDecisionKinds.DailyModeIntent)
                valid = owner.GetComponent<DailyBehaviorController>()?.TryCommitSystemMode(request, run, response.decision.target_daily_mode, out reason) ?? false;
            else if (valid) reason = "system_kept_mode";
            LastDailyModeResult = reason;
            pendingRequest = null; StopResponseTimeout();
            orchestrator.CompleteCurrentRun();
            owner.GetComponent<ServiceZoneDwellCognitionSource>()?.RecordResult(valid ? reason : "rejected:" + reason);
            PublishRequestFeedback(request, "response_received", valid ? null : reason); return valid;
        }

        public bool CancelContinuation(string requestId)
        {
            CognitiveRequest request = pendingRequest;
            if (!IsContinuation(request) || request.request_id != requestId) return false;
            pendingRequest = null;
            bufferedContinuation = null;
            StopResponseTimeout();
            if (orchestrator?.CurrentRun?.RunId == request.run.run_id) orchestrator.CancelCurrentRun();
            if (backendClient != null) _ = backendClient.SendCognitiveCancelAsync(request);
            ContinuationSettled?.Invoke(request, "cancelled");
            PublishRequestFeedback(request, "cancelled", "cancelled");
            return true;
        }

        private static bool IsContinuation(CognitiveRequest request) =>
            request?.context?.current_event?.event_type == AgentEventTypes.DialogueContinue;

        public bool FlushContinuationResponse()
        {
            CognitiveResponse response = bufferedContinuation;
            if (response == null) return false;
            bufferedContinuation = null;
            if (!IsContinuation(pendingRequest) || composer?.ContinuationScheduler == null ||
                !composer.ContinuationScheduler.CanCommit(pendingRequest) || !composer.HasCurrentSceneFacts(pendingRequest))
            {
                CancelContinuation(pendingRequest?.request_id);
                return false;
            }
            return ConsumeOwnedResponse(response);
        }

        public void Configure(WorldEntity entity, AgentOrchestrator runOrchestrator,
            CognitiveContextComposer contextComposer, BackendWebSocketClient client, MonoBehaviour presenter)
        {
            Unsubscribe();
            owner = entity;
            orchestrator = runOrchestrator;
            composer = contextComposer;
            backendClient = client;
            presentationBehaviour = presenter;
            ResolveReferences();
            Subscribe();
        }

        public bool TryOwnRequest(CognitiveRequest request)
        {
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            if (request == null || !request.IsValid || run == null ||
                run.IsTaskConsideration != (request.task_consideration!=null) ||
                !string.Equals(request.run.run_id, run.RunId, StringComparison.Ordinal) ||
                !string.Equals(request.run.agent_entity_id, owner?.EntityId, StringComparison.Ordinal)) return false;
            pendingRequest = request;
            LastValidatedGoalProposal = null;
            LastDailyModeResult = "none";
            LastActionHandoffResult = "none";
            return true;
        }

        public bool TryConsumeResponse(CognitiveResponse response)
        {
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            if (response == null || !response.IsValid || pendingRequest == null || run == null || run.State != CognitiveRunState.Running ||
                !string.Equals(response.request_id, pendingRequest.request_id, StringComparison.Ordinal) ||
                !string.Equals(response.trace_id, pendingRequest.trace_id, StringComparison.Ordinal) ||
                !string.Equals(response.run_id, run.RunId, StringComparison.Ordinal) ||
                !string.Equals(response.agent_entity_id, owner?.EntityId, StringComparison.Ordinal)) return false;
            if (!TaskReceiptValidator.Matches(response, pendingRequest))
            { FailOwnedRun("COGNITIVE_TASK_RECEIPT_INVALID"); return false; }
            if (response.task_receipt != null && (!owner.TryGetPersistentIdentityKey(out var taskOwner) ||
                response.task_receipt.owner.identity_namespace != taskOwner.Namespace ||
                response.task_receipt.owner.identity_type != taskOwner.IdentityType ||
                response.task_receipt.owner.persistent_id != taskOwner.PersistentId))
            { FailOwnedRun("COGNITIVE_TASK_OWNER_CHANGED"); return false; }
            if (pendingRequest.task_runtime != null &&
                !(FindAnyObjectByType<TaskRuntimeBridge>()?.IsCurrent(pendingRequest.task_runtime) ?? false))
            { FailOwnedRun("COGNITIVE_TASK_CONTEXT_EXPIRED"); return false; }

            if(run.IsTaskConsideration)
            {
                if(!ReplyUtteranceContract.Fits(response.decision.utterance,pendingRequest.reply_language))
                { FailOwnedRun("UTTERANCE_HARD_LIMIT_EXCEEDED"); return false; }
                if(response.decision.decision_kind==CognitiveDecisionKinds.ActionProposal && response.task_execution?.Matches(pendingRequest,response)!=true)
                { FailOwnedRun("TASK_EXECUTION_TICKET_INVALID"); return false; }
                if(response.decision.target_daily_mode!=null || response.decision.dialogue_turn_policy=="MAY_CONTINUE" ||
                    !(GetComponent<TaskConsiderationSource>()?.Matches(pendingRequest,run) ?? false) || bufferedTaskConsideration!=null)
                { FailOwnedRun("TASK_CONSIDERATION_STALE"); return false; }
                bufferedTaskConsideration=response; return true;
            }

            if (run.IsCounterCleaningConsideration)
            {
                if (response.decision.decision_kind != CognitiveDecisionKinds.NoAction &&
                    response.decision.decision_kind != CognitiveDecisionKinds.AutonomousActionIntent)
                { FailOwnedRun("AUTONOMOUS_OUTPUT_FORBIDDEN"); return false; }
                if (response.decision.episode_directive != null || response.decision.dialogue_turn_policy != null || bufferedServiceMode != null) return false;
                bufferedServiceMode = response; return true;
            }
            if (run.IsServiceZoneConsideration)
            {
                var d = response.decision;
                if ((d.decision_kind != CognitiveDecisionKinds.NoAction && d.decision_kind != CognitiveDecisionKinds.DailyModeIntent) ||
                    d.utterance != null || d.emotion != null || d.gesture != null || d.look_at != null || d.action_proposal != null ||
                    d.episode_directive != null || d.dialogue_turn_policy != null || response.validated_goal != null)
                { FailOwnedRun("SYSTEM_MODE_OUTPUT_FORBIDDEN"); return false; }
                if (bufferedServiceMode != null) return false;
                bufferedServiceMode = response; return true;
            }
            if (response.decision.decision_kind == CognitiveDecisionKinds.DailyModeIntent || response.decision.decision_kind == CognitiveDecisionKinds.AutonomousActionIntent)
            { FailOwnedRun("SYSTEM_MODE_SOURCE_FORBIDDEN"); return false; }

            if (!ReplyUtteranceContract.Fits(response.decision.utterance, pendingRequest.reply_language))
            { FailOwnedRun("UTTERANCE_HARD_LIMIT_EXCEEDED"); return false; }
            if (IsContinuation(pendingRequest))
            {
                if (response.decision.decision_kind == CognitiveDecisionKinds.ActionProposal || response.decision.target_daily_mode != null)
                { FailOwnedRun(); return false; }
                if (bufferedContinuation != null) return false;
                // Every Update input path finishes before LateUpdate presentation. No input FIFO is needed.
                bufferedContinuation = response;
                return true;
            }
            return ConsumeOwnedResponse(response);
        }

        private bool ConsumeOwnedResponse(CognitiveResponse response)
        {
            CognitiveRun run = orchestrator?.CurrentRun;
            if (pendingRequest == null || run == null || response.request_id != pendingRequest.request_id ||
                response.trace_id != pendingRequest.trace_id || response.run_id != run.RunId) return false;
            if (pendingRequest.context_diagnostics_version == 1 && response.context_status != null)
                ContextCapacityFeedback.Current.Observe(response.context_status);
            if(response.task_receipt!=null) FindAnyObjectByType<TaskRuntimeBridge>()?.AcceptReceipt(pendingRequest,response);

            if (response.decision.decision_kind == CognitiveDecisionKinds.ActionProposal)
                return ConsumeActionResponse(response, run);

            if (response.decision.target_daily_mode != null)
            {
                var daily = owner.GetComponent<DailyBehaviorController>();
                string reason = "daily_mode_unavailable";
                bool committed = daily != null && daily.TryCommitCognitiveMode(pendingRequest, run,
                    response.decision.target_daily_mode, () => TryPresentOwnedUtterance(pendingRequest,
                        ModeStatus(daily, true, false, pendingRequest.reply_language)), out reason);
                LastDailyModeResult = reason;
                if (!committed)
                {
                    if (reason == "presentation_unavailable") { FailOwnedRun(); return false; }
                    var fallback = ModeStatus(daily, false, false, pendingRequest.reply_language);
                    if (!TryPresentOwnedUtterance(pendingRequest, fallback)) { FailOwnedRun(); return false; }
                }
            }
            else if (response.decision.decision_kind == CognitiveDecisionKinds.DirectResponse &&
                !TryPresentOwnedUtterance(pendingRequest, response.decision))
            {
                FailOwnedRun();
                return false;
            }
            CognitiveRequest completed = pendingRequest;
            pendingRequest = null;
            StopResponseTimeout();
            bool settled = orchestrator.CompleteCurrentRun();
            if (IsContinuation(completed)) ContinuationSettled?.Invoke(completed, "completed");
            if (settled) PublishRequestFeedback(completed, "response_received", null);
            return settled;
        }

        private static CognitiveDecision ModeStatus(DailyBehaviorController daily, bool changed, bool taskNotStarted, string language)
        {
            // A committed mode does not validate free-text commitments or prove arrival.
            // Keep the existing factual fallback, expressed as a brief conversational reply.
            string text = daily == null ? "刚才没能调整过来，先照旧吧。" : changed ?
                daily.CurrentMode == DailyBehaviorMode.Work ? "好，那我接着忙了。" :
                daily.CurrentMode == DailyBehaviorMode.Rest ? "好，那我先歇会儿。" : "嗯，我先放松一会儿。" :
                daily.CurrentMode == DailyBehaviorMode.Work ? "我还在忙，这会儿先照旧。" :
                daily.CurrentMode == DailyBehaviorMode.Rest ? "我还在休息，这会儿先照旧。" : "我还在放松，这会儿先照旧。";
            if (language == "en-US") text = daily == null ? "I couldn't change that just now; I'll carry on as before." : changed ?
                daily.CurrentMode == DailyBehaviorMode.Work ? "All right, I'll get back to work." :
                daily.CurrentMode == DailyBehaviorMode.Rest ? "All right, I'll take a break." : "I'll relax for a while." :
                daily.CurrentMode == DailyBehaviorMode.Work ? "I'm still working; I'll carry on as before." :
                daily.CurrentMode == DailyBehaviorMode.Rest ? "I'm still resting; I'll carry on as before." : "I'm still relaxing; I'll carry on as before.";
            return new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                dialogue_turn_policy = "WAIT_FOR_PLAYER",
                utterance = text + (taskNotStarted ? (language == "en-US" ? " Your requested task hasn't started yet." : "不过，你刚才托我的事还没开始做。") : string.Empty) };
        }

        private bool ConsumeActionResponse(CognitiveResponse response, CognitiveRun run)
        {
            CognitiveRequest actionRequest = pendingRequest;
            bool combined = response.decision.target_daily_mode != null;
            var daily = owner.GetComponent<DailyBehaviorController>();
            if (!WorldActionEligibility.MayProposeNewWorldAction(actionRequest, run, owner, response.decision.action_proposal))
            {
                LastActionHandoffResult = "action_not_eligible";
                FailOwnedRun();
                return false;
            }
            if (actionHandoff == null ||
                !actionHandoff.TryPrepareOwnership(actionRequest, response,
                    out CognitiveActionOutcomeOwnership ownership))
            {
                LastActionHandoffResult = "action_ownership_unavailable";
                if (combined) TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, false, true, actionRequest.reply_language));
                FailOwnedRun();
                return false;
            }
            if (combined)
            {
                string reason = "daily_mode_unavailable";
                if (daily == null || !daily.TryValidateCognitiveMode(actionRequest, run,
                        response.decision.target_daily_mode, out reason))
                {
                    LastDailyModeResult = reason;
                    LastActionHandoffResult = "mode_prevalidation_failed";
                    TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, false, true, actionRequest.reply_language));
                    FailOwnedRun(); return false;
                }
                if (!actionHandoff.TryPrevalidate(actionRequest, response, out reason))
                {
                    LastActionHandoffResult = "action_prevalidation_failed:" + reason;
                    TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, false, true, actionRequest.reply_language));
                    FailOwnedRun(); return false;
                }
                if (!daily.TryCommitCognitiveModeForAction(actionRequest, run,
                        response.decision.target_daily_mode, out reason))
                {
                    LastDailyModeResult = reason;
                    LastActionHandoffResult = "mode_commit_failed";
                    TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, false, true, actionRequest.reply_language));
                    FailOwnedRun(); return false;
                }
                LastDailyModeResult = reason;
            }
            goalPlanRunner = actionHandoff.GoalPlanRunner;
            SubscribeGoalOutcomes();
            // Establish the run's outcome ownership before Unity can accept/start the goal.
            if (!TryBeginActionWait(ownership))
            {
                LastActionHandoffResult = "action_wait_failed";
                if (combined) TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, true, true, actionRequest.reply_language));
                FailOwnedRun();
                return false;
            }
            if (!actionHandoff.TryHandoff(actionRequest, response))
            {
                LastActionHandoffResult = "action_handoff_failed";
                FailUnacceptedActionWait(ownership);
                if (combined) TryPresentOwnedUtterance(actionRequest, ModeStatus(daily, true, true, actionRequest.reply_language));
                return false;
            }
            LastValidatedGoalProposal = response.validated_goal;
            LastActionHandoffResult = "action_accepted";
            // A synchronous GoalOutcome subscriber may already have settled this accepted task.
            if (!ReferenceEquals(pendingActionOutcome, ownership) ||
                run.State != CognitiveRunState.WaitingActionResult)
            {
                PublishRequestFeedback(actionRequest, "response_received", null);
                return true;
            }
            if (response.decision.utterance != null &&
                !TryPresentAcceptedActionDecision(actionRequest, response.decision))
                Debug.LogWarning("[Cognition] Accepted action utterance was not presented; " +
                                 "the owned world action remains active.", this);
            PublishRequestFeedback(actionRequest, "response_received", null);
            return true;
        }

        internal bool TryPresentAcceptedAction(CognitiveRequest request, string utterance,
            string episodeDirective)
            => TryPresentAcceptedActionDecision(request, new CognitiveDecision
            {
                decision_kind = CognitiveDecisionKinds.ActionProposal,
                utterance = utterance, episode_directive = episodeDirective,
            });

        private bool TryPresentAcceptedActionDecision(CognitiveRequest request, CognitiveDecision decision)
        {
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            if (request == null || pendingActionOutcome == null || run == null ||
                run.State != CognitiveRunState.WaitingActionResult ||
                !string.Equals(request.run.run_id, pendingActionOutcome.RunId, StringComparison.Ordinal) ||
                !string.Equals(request.run.agent_entity_id, pendingActionOutcome.AgentEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(request.trace_id, pendingActionOutcome.TraceId, StringComparison.Ordinal) ||
                !string.Equals(GoalPlanRunner.NormalizeProposalId(request.request_id),
                    pendingActionOutcome.ProposalId, StringComparison.Ordinal)) return false;
            return TryPresentOwnedUtterance(request, decision);
        }

        private bool TryPresentOwnedUtterance(CognitiveRequest request, CognitiveDecision decision, bool publishCognitiveEvidence = true)
        {
            if (request == null || Presenter == null || decision == null) return false;
            string utterance = decision.utterance;
            string episodeDirective = decision.episode_directive;

            CognitiveEventDto sourceEvent = request.context?.current_event;
            string playerEntityId = request.context?.relevant_player?.player_entity_id;
            if (string.IsNullOrWhiteSpace(playerEntityId) && sourceEvent != null &&
                !string.Equals(sourceEvent.source_entity_id, owner?.EntityId, StringComparison.Ordinal))
                playerEntityId = sourceEvent.source_entity_id;
            string conversationId = GetEventPayload(sourceEvent, AgentEventPayloadKeys.ConversationId);
            bool didPresent;
            try
            {
                didPresent = Presenter is ICognitiveDecisionPresenter aware
                    ? aware.TryPresentCognitiveDecision(CopyDecisionForPresentation(decision),
                        new CognitivePresentationContext(request, playerEntityId, conversationId))
                    : Presenter.TryPresentCognitiveUtterance(utterance);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Cognition] presentation failed: {exception.GetType().Name}", this);
                return false;
            }
            if (!didPresent) return false;
            // Terminal Action presentation is already backed by GoalOutcome evidence. Do not
            // append a second model-response event with the original cognitive request ID.
            if (!publishCognitiveEvidence) return true;
            double presentedAt = Time.realtimeSinceStartupAsDouble;
            var presented = new PresentedCognitiveUtterance(request.request_id,
                request.trace_id, request.run.run_id, owner?.EntityId, playerEntityId,
                utterance, sourceEvent?.has_world_revision == true ? sourceEvent.world_revision : 0,
                presentedAt, sourceEvent?.event_id ?? request.trigger.source_event_id,
                conversationId, episodeDirective, decision.dialogue_turn_policy,
                sourceEvent?.event_type == AgentEventTypes.DialogueContinue ? sourceEvent : null, sourceEvent);
            PublishPresented(presented);
            return true;
        }

        // Presentation receives an isolated copy, so it cannot edit the model's action proposal or evidence.
        private static CognitiveDecision CopyDecisionForPresentation(CognitiveDecision decision) => new()
        {
            decision_kind = decision.decision_kind, episode_directive = decision.episode_directive,
            dialogue_turn_policy = decision.dialogue_turn_policy,
            utterance = decision.utterance, emotion = decision.emotion, gesture = decision.gesture,
            look_at = decision.look_at,
            action_proposal = decision.action_proposal == null ? null : new CognitiveActionProposal
            {
                action_id = decision.action_proposal.action_id,
                bindings = new System.Collections.Generic.Dictionary<string, string>(decision.action_proposal.bindings),
            },
        };

        private void PublishPresented(PresentedCognitiveUtterance presented)
        {
            Delegate[] subscribers = UtterancePresented?.GetInvocationList();
            if (subscribers == null) return;
            foreach (Delegate subscriber in subscribers)
            {
                try { ((Action<PresentedCognitiveUtterance>)subscriber).Invoke(presented); }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[Cognition] presented-utterance observer failed: {exception.GetType().Name}",
                        this);
                }
            }
        }

        private static string GetEventPayload(CognitiveEventDto sourceEvent, string key)
        {
            if (sourceEvent?.payload == null) return string.Empty;
            foreach (CognitiveKeyValueDto pair in sourceEvent.payload)
                if (pair != null && string.Equals(pair.key, key, StringComparison.Ordinal))
                    return pair.value ?? string.Empty;
            return string.Empty;
        }

        internal bool TryBeginActionWait(CognitiveActionOutcomeOwnership ownership)
        {
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            if (ownership == null || run == null || run.State != CognitiveRunState.Running ||
                string.IsNullOrWhiteSpace(ownership.ProposalId) ||
                !string.Equals(ownership.RunId, run.RunId, StringComparison.Ordinal) ||
                !string.Equals(ownership.AgentEntityId, owner?.EntityId, StringComparison.Ordinal) ||
                !orchestrator.WaitForCurrentRunActionResult()) return false;

            actionPresentationRequest = pendingRequest;
            actionConversationId = GetEventPayload(pendingRequest?.context?.current_event, AgentEventPayloadKeys.ConversationId);
            if (string.IsNullOrEmpty(actionConversationId) && dialogueController?.SelectedTarget?.Entity == owner)
                actionConversationId = dialogueController.CurrentConversationId;
            actionLifecycleReason = dialogueController?.LastLifecycleReason;
            settledActionPresentationRequest = null; settledPresentationOutcome = null;
            consumedPresentationOutcomeId = null; LastActionOutcomePresentationResult = "none";
            pendingRequest = null;
            StopResponseTimeout();
            pendingActionOutcome = ownership;
            lastSettledActionOutcome = null;
            return true;
        }

        private bool FailUnacceptedActionWait(CognitiveActionOutcomeOwnership ownership)
        {
            if (!ReferenceEquals(pendingActionOutcome, ownership)) return false;
            pendingActionOutcome = null;
            actionPresentationRequest = null;
            LastValidatedGoalProposal = null;
            return orchestrator != null && orchestrator.FailCurrentRun();
        }

        internal bool TryConsumeActionOutcome(GoalOutcome outcome)
        {
            CognitiveActionOutcomeOwnership ownership = pendingActionOutcome;
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            if (ownership == null || run == null || run.State != CognitiveRunState.WaitingActionResult ||
                !Matches(outcome, ownership)) return false;

            bool settled = outcome.Status switch
            {
                PlanExecutionStatus.Succeeded => orchestrator.CompleteCurrentRun(),
                PlanExecutionStatus.Cancelled => orchestrator.CancelCurrentRun(),
                PlanExecutionStatus.Failed => orchestrator.FailCurrentRun(),
                PlanExecutionStatus.TimedOut => orchestrator.FailCurrentRun(),
                _ => false,
            };
            if (!settled) return false;

            lastSettledActionOutcome = ownership;
            settledActionPresentationRequest = actionPresentationRequest;
            settledPresentationOutcome = outcome;
            actionPresentationRequest = null;
            pendingActionOutcome = null;
            return true;
        }

        private void HandleGoalPresentation(GoalPlanPresentation presentation)
        {
            if (!presentation.Outcome.HasValue || !settledPresentationOutcome.HasValue ||
                !presentation.Outcome.Value.Equals(settledPresentationOutcome.Value) ||
                !Matches(presentation.Outcome.Value, lastSettledActionOutcome) || settledActionPresentationRequest == null)
                return;
            if (settledActionPresentationRequest.context?.current_event?.event_type == AgentEventTypes.CounterNeedsCleaning)
            { LastActionOutcomePresentationResult = "autonomous_silent"; return; }
            var outcome = presentation.Outcome.Value;
            bool succeeded = outcome.Status == PlanExecutionStatus.Succeeded;
            if (presentation.Kind != (succeeded ? GoalPlanPresentationKind.Succeeded : GoalPlanPresentationKind.Failed) ||
                consumedPresentationOutcomeId == outcome.OutcomeId) return;
            // Consume once even when the original UI is closed; a late replay must not reopen it.
            consumedPresentationOutcomeId = outcome.OutcomeId;
            if (pendingRequest != null || orchestrator.HasActiveRun || goalPlanRunner.HasActiveTask ||
                !ActionPresentationScopeMatches())
            { LastActionOutcomePresentationResult = "lifecycle_unavailable"; return; }
            var decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = presentation.Message, dialogue_turn_policy = "WAIT_FOR_PLAYER" };
            LastActionOutcomePresentationResult = TryPresentOwnedUtterance(settledActionPresentationRequest, decision, false)
                ? "displayed" : "presentation_unavailable";
        }

        private bool ActionPresentationScopeMatches()
        {
            if (dialogueController == null) return string.IsNullOrEmpty(actionConversationId);
            if (!dialogueController.isActiveAndEnabled || dialogueController.IsChangingSession) return false;
            if (!string.IsNullOrEmpty(actionConversationId))
                return dialogueController.HasActiveSession && dialogueController.IsPresentationVisible &&
                    dialogueController.CurrentConversationId == actionConversationId &&
                    dialogueController.SelectedTarget?.Entity == owner &&
                    dialogueController.ActiveSpeakerEntityId == settledActionPresentationRequest.context?.relevant_player?.player_entity_id;
            // Preserve unbound local requests, but never overwrite a newly opened/switched session.
            return !dialogueController.HasActiveSession && !dialogueController.IsPresentationVisible &&
                dialogueController.SelectedTarget == null && dialogueController.LastLifecycleReason == actionLifecycleReason;
        }

        internal bool OwnsActionOutcome(AgentEvent agentEvent)
        {
            if (agentEvent == null ||
                !agentEvent.TryGetPayload(AgentEventPayloadKeys.ProposalId, out string proposalId) ||
                !agentEvent.TryGetPayload(AgentEventPayloadKeys.ProposalRevision, out string revisionText) ||
                !int.TryParse(revisionText, out int proposalRevision) ||
                !agentEvent.TryGetPayload(AgentEventPayloadKeys.TraceId, out string traceId)) return false;

            return Matches(agentEvent.SourceEntityId, proposalId, proposalRevision, traceId,
                       pendingActionOutcome) ||
                   Matches(agentEvent.SourceEntityId, proposalId, proposalRevision, traceId,
                       lastSettledActionOutcome);
        }

        public bool TryConsumeFailure(CognitiveTransportFailure failure)
        {
            if (failure == null || pendingRequest == null ||
                !string.Equals(failure.RequestId, pendingRequest.request_id, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(failure.TraceId) &&
                 !string.Equals(failure.TraceId, pendingRequest.trace_id, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(failure.RunId) &&
                 !string.Equals(failure.RunId, pendingRequest.run.run_id, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(failure.AgentEntityId) &&
                 !string.Equals(failure.AgentEntityId, owner?.EntityId, StringComparison.Ordinal))) return false;
            if (pendingRequest.context_diagnostics_version == 1 && failure.ContextStatus != null &&
                orchestrator?.CurrentRun is { State: CognitiveRunState.Running } capacityRun &&
                capacityRun.RunId == pendingRequest.run.run_id &&
                new[] { "CONTEXT_BUDGET_EXCEEDED", "CONTEXT_LENGTH_EXCEEDED", "OUTPUT_RESERVE_EXCEEDS_CONTEXT" }.Contains(failure.DiagnosticCategory) &&
                failure.ContextStatus.checks.All(c => c.state == "error" || c.state == "ok") &&
                !string.IsNullOrWhiteSpace(failure.TraceId) && !string.IsNullOrWhiteSpace(failure.RunId) &&
                !string.IsNullOrWhiteSpace(failure.AgentEntityId))
                ContextCapacityFeedback.Current.Observe(failure.ContextStatus);
            return FailOwnedRun(failure.Reason, failure.DiagnosticCategory);
        }

        private async void HandleRequestCreated(CognitiveRequest request)
        {
            if (!TryOwnRequest(request)) return;
            bool sent = backendClient != null && await backendClient.SendCognitiveRequestAsync(request);
            if ((IsContinuation(request) || request.task_consideration!=null || (request.context?.current_event?.event_type == AgentEventTypes.PlayerDwelledInServiceZone || request.context?.current_event?.event_type == AgentEventTypes.CounterNeedsCleaning)) && pendingRequest != request)
            {
                // The request send can complete after local invalidation. Retire it again on the wire.
                if (backendClient != null) await backendClient.SendCognitiveCancelAsync(request);
                return;
            }
            LastSendSucceeded = sent;
            if(request.task_consideration!=null)
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("consideration",new Newtonsoft.Json.Linq.JObject {
                    ["task_state"]=sent?"sent":"send_failed",["request_id"]=request.request_id,["run_id"]=request.run.run_id,
                    ["visit_id"]=request.task_runtime?.visit_id,["agent_id"]=request.run.agent_entity_id });
            if (!sent && this != null && pendingRequest == request) FailOwnedRun("SEND_FAILED");
            else if (this != null && pendingRequest == request)
                timeoutCoroutine = StartCoroutine(FailAfterTimeout(request));
        }

        private void HandleResponse(CognitiveResponse response) => TryConsumeResponse(response);
        private void HandleFailure(CognitiveTransportFailure failure) => TryConsumeFailure(failure);
        private void HandleGoalOutcome(GoalOutcome outcome) => TryConsumeActionOutcome(outcome);

        private bool FailOwnedRun(string reason = "COGNITIVE_REQUEST_FAILED", string diagnosticCategory = null)
        {
            if (pendingRequest == null) return false;
            CognitiveRequest failed = pendingRequest;
            if(failed.task_consideration!=null)
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("error",new Newtonsoft.Json.Linq.JObject { ["task_state"]="failed",
                    ["request_id"]=failed.request_id,["run_id"]=failed.run.run_id,["agent_id"]=failed.run.agent_entity_id,
                    ["visit_id"]=failed.task_runtime?.visit_id,["error_code"]=reason });
            pendingRequest = null;
            bufferedContinuation = null;
            bufferedServiceMode = null;
            bufferedTaskConsideration = null;
            StopResponseTimeout();
            bool settled = orchestrator?.CurrentRun?.RunId == failed.run.run_id && orchestrator.FailCurrentRun();
            if(failed.task_consideration!=null)
            {
                if(backendClient!=null) _=backendClient.SendCognitiveCancelAsync(failed);
                owner.GetComponent<TaskConsiderationSource>()?.RecordResult("not_executed:"+reason);
            }
            if (failed.context?.current_event?.event_type == AgentEventTypes.PlayerDwelledInServiceZone)
            {
                if (backendClient != null) _ = backendClient.SendCognitiveCancelAsync(failed);
                owner.GetComponent<ServiceZoneDwellCognitionSource>()?.RecordResult("failed:" + reason);
            }
            if (failed.context?.current_event?.event_type == AgentEventTypes.CounterNeedsCleaning)
            {
                if (backendClient != null) _ = backendClient.SendCognitiveCancelAsync(failed);
                owner.GetComponent<CounterCleaningCognitionSource>()?.RecordResult("failed:" + reason);
            }
            if (IsContinuation(failed))
            {
                if (backendClient != null) _ = backendClient.SendCognitiveCancelAsync(failed);
                ContinuationSettled?.Invoke(failed, "failed");
            }
            if (settled) PublishRequestFeedback(failed, "failed", diagnosticCategory ?? reason);
            return settled;
        }

        private IEnumerator FailAfterTimeout(CognitiveRequest request)
        {
            yield return new WaitForSecondsRealtime(responseTimeoutSeconds);
            if (pendingRequest != request) yield break;
            timeoutCoroutine = null;
            FailOwnedRun("CLIENT_TIMEOUT");
        }

        private void PublishRequestFeedback(CognitiveRequest request, string status, string reason)
        {
            var value = new CognitiveRequestFeedback(request, status, reason);
            foreach (Delegate subscriber in RequestFeedback?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action<CognitiveRequestFeedback>)subscriber)(value); }
                catch (Exception error) { Debug.LogWarning("[Cognition] feedback observer failed: " + error.GetType().Name, this); }
        }

        private void StopResponseTimeout()
        {
            if (timeoutCoroutine == null) return;
            StopCoroutine(timeoutCoroutine);
            timeoutCoroutine = null;
        }

        private void ResolveReferences()
        {
            if (owner == null) owner = GetComponent<WorldEntity>();
            if (orchestrator == null) orchestrator = GetComponent<AgentOrchestrator>();
            if (composer == null) composer = GetComponent<CognitiveContextComposer>();
            if (dialogueController == null) dialogueController = FindAnyObjectByType<DialogueController>();
            if (actionHandoff == null) actionHandoff = GetComponent<CognitiveActionHandoff>();
            if (goalPlanRunner == null)
                goalPlanRunner = actionHandoff == null ? GetComponent<GoalPlanRunner>() : actionHandoff.GoalPlanRunner;
        }

        private void Subscribe()
        {
            if (!isActiveAndEnabled) return;
            if (!subscribed && composer != null && backendClient != null)
            {
                composer.CognitiveRequestCreated += HandleRequestCreated;
                backendClient.CognitiveResponseReceived += HandleResponse;
                backendClient.CognitiveRequestFailed += HandleFailure;
                subscribed = true;
            }
            SubscribeGoalOutcomes();
        }

        private void SubscribeGoalOutcomes()
        {
            if (!isActiveAndEnabled || outcomeSubscribed || goalPlanRunner == null) return;
            goalPlanRunner.GoalOutcomePublished += HandleGoalOutcome;
            goalPlanRunner.PresentationReady += HandleGoalPresentation;
            outcomeSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (subscribed)
            {
                if (composer != null) composer.CognitiveRequestCreated -= HandleRequestCreated;
                if (backendClient != null)
                {
                    backendClient.CognitiveResponseReceived -= HandleResponse;
                    backendClient.CognitiveRequestFailed -= HandleFailure;
                }
                subscribed = false;
            }
            if (outcomeSubscribed && goalPlanRunner != null)
            {
                goalPlanRunner.GoalOutcomePublished -= HandleGoalOutcome;
                goalPlanRunner.PresentationReady -= HandleGoalPresentation;
            }
            outcomeSubscribed = false;
        }

        private static bool Matches(GoalOutcome outcome, CognitiveActionOutcomeOwnership ownership) =>
            ownership != null &&
            string.Equals(outcome.ProposalId, ownership.ProposalId, StringComparison.Ordinal) &&
            outcome.ProposalRevision == ownership.ProposalRevision &&
            string.Equals(outcome.TraceId, ownership.TraceId, StringComparison.Ordinal) &&
            string.Equals(outcome.ActorEntityId, ownership.AgentEntityId, StringComparison.Ordinal);

        private static bool Matches(string actorEntityId, string proposalId, int proposalRevision,
            string traceId, CognitiveActionOutcomeOwnership ownership) => ownership != null &&
            string.Equals(proposalId, ownership.ProposalId, StringComparison.Ordinal) &&
            proposalRevision == ownership.ProposalRevision &&
            string.Equals(traceId, ownership.TraceId, StringComparison.Ordinal) &&
            string.Equals(actorEntityId, ownership.AgentEntityId, StringComparison.Ordinal);

        private void OnValidate() => ResolveReferences();
    }
}
