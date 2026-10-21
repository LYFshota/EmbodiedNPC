using System.Collections.Generic;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    /// <summary>Adapts validated backend Goals to PlanExecutor and owns presentation only.</summary>
    public sealed class GoalPlanRunner : MonoBehaviour
    {
        public event System.Action<GoalPlanPresentation> PresentationReady;
        public event System.Action<GoalOutcome> GoalOutcomePublished;
        public event System.Action<TaskFeedbackSnapshot> TaskStarted;
        public event System.Action<TaskFeedbackSnapshot> TaskPhaseChanged;
        public TaskFeedbackSnapshot CurrentTaskFeedback { get; private set; }
        internal WorldStateService WorldState => worldState;
        internal bool TryGetCounterCleaningReceipt(TaskFeedbackKey key, out CounterCleaningReceipt receipt)
        {
            receipt = default;
            return worldState != null && worldState.TryGetCounterCleaningReceipt(key.Actor, key.Proposal, key.Trace, out receipt);
        }
        internal bool TryGetDrinkReceipt(TaskFeedbackKey key, out DrinkReceipt receipt)
        {
            receipt = default;
            return worldState != null && worldState.TryGetDrinkReceipt(key.Actor, key.Proposal, key.Trace, out receipt);
        }

        [Header("Goal input and authority")]
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private GoalValidator goalValidator;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private ResourceReservationService reservations;
        [SerializeField] private WorldStateService worldState;
        [SerializeField] private WorldEntity actor;

        [Header("Action execution")]
        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField] private SafeDropZone safeDropZone;
        [SerializeField] private Transform actorHoldPoint;
        [SerializeField] private Transform destinationHoldParent;
        [SerializeField, Min(0.5f)] private float leaseRenewIntervalSeconds = 5f;
        [SerializeField, Min(0.05f)] private float interactionRange = 0.85f;

        [Header("Presentation and interruption")]
        [SerializeField] private InteractionSafetyController safetyController;
        [SerializeField] private MotionChannelCoordinator motionChannels;

        private PlanExecutor executor;
        private GoalProposal activeGoal;
        private string activeProposalId;
        private string pendingFailureRequestId;
        private int activeProposalRevision;
        private string activeTraceId;
        private int activeSubmittedWorldRevision;
        private ExecutionAuthorization activeAuthorization;
        private NavMeshRediscoveryDriver rediscoveryDriver;
        private TargetRediscovery rediscovery;
        private PerceivedStatePlannerProbe perceptionProbe;
        private bool awaitingRediscovery;
        private string feedbackPlayerId;
        private string activeActorEntityId;
        private long feedbackSequence;
        private bool shuttingDown;

        public bool IsExecuting => awaitingRediscovery || executor != null && executor.IsExecuting;
        public bool HasActiveTask => IsExecuting || activeGoal != null ||
                                     !string.IsNullOrEmpty(pendingFailureRequestId) || awaitingRediscovery;
        public bool IsRediscovering => awaitingRediscovery || executor != null && executor.IsRediscovering;
        public string RediscoveryFailureCode => rediscovery?.FailureCode ?? string.Empty;
        public string ActivePlanHash => executor?.ActivePlanHash ?? string.Empty;
        public int ReplanCount => executor?.ReplanCount ?? 0;
        public string ActiveProposalId => activeProposalId ?? string.Empty;
        public int ActiveProposalRevision => activeProposalRevision;
        public string ActiveTraceId => activeTraceId ?? string.Empty;
        public int ActiveSubmittedWorldRevision => activeSubmittedWorldRevision;

        private void Awake()
        {
            if (actor == null) actor = GetComponent<WorldEntity>();
            if (goalValidator == null) goalValidator = GetComponent<GoalValidator>();
            if (worldState == null) worldState = GetComponent<WorldStateService>();
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (perceivedState == null) perceivedState = GetComponent<PerceivedStateStore>();
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            if (reservations == null) reservations = FindAnyObjectByType<ResourceReservationService>();
            if (safeDropZone == null) safeDropZone = FindAnyObjectByType<SafeDropZone>();
            if (actorHoldPoint == null) actorHoldPoint = GameObject.Find("HandHoldPoint")?.transform;
            if (destinationHoldParent == null) destinationHoldParent = GameObject.Find("PlayerHoldPoint")?.transform;
            if (safetyController == null) safetyController = GetComponent<InteractionSafetyController>();
            if (motionChannels == null) motionChannels = GetComponent<MotionChannelCoordinator>();
            rediscoveryDriver = new NavMeshRediscoveryDriver(agent, GetComponent<VisionSensor>());
            rediscovery = new TargetRediscovery(rediscoveryDriver);
            perceptionProbe = new PerceivedStatePlannerProbe(perceivedState);

            executor = new PlanExecutor(entityRegistry, reservations, worldState, agent, perceivedState,
                safeDropZone, actorHoldPoint, destinationHoldParent, leaseRenewIntervalSeconds, interactionRange);
        }

        private void OnEnable()
        {
            shuttingDown = false;
            if (backendClient != null)
            {
                backendClient.ValidatedTaskFailureResponseReceived += HandleTaskFailureResponse;
                backendClient.TaskFailureRequestInvalidated += HandleTaskFailureRequestInvalidated;
            }
            if (executor == null) return;
            executor.Recovering += HandleRecovering;
            executor.Completed += HandleCompleted;
            executor.ProgressChanged += HandleProgress;
        }

        private void OnDisable()
        {
            shuttingDown = true;
            // Settle while the executor completion callback and task identity still exist.
            InterruptCurrentPlan("RUNNER_DISABLED");
            rediscovery?.Cancel("RUNNER_DISABLED");
            awaitingRediscovery = false;
            if (backendClient != null)
            {
                backendClient.ValidatedTaskFailureResponseReceived -= HandleTaskFailureResponse;
                backendClient.TaskFailureRequestInvalidated -= HandleTaskFailureRequestInvalidated;
            }
            if (executor != null)
            {
                executor.Recovering -= HandleRecovering;
                executor.Completed -= HandleCompleted;
                executor.ProgressChanged -= HandleProgress;
            }
            motionChannels?.EndTaskMotion();
            InvalidateProposal();
        }

        private void Update()
        {
            if (awaitingRediscovery)
            {
                rediscovery.Tick(Time.unscaledTimeAsDouble, Time.unscaledDeltaTime);
                if (rediscovery.Status == RediscoveryStatus.Searching) return;
                awaitingRediscovery = false;
                if (rediscovery.Status == RediscoveryStatus.Succeeded)
                    BeginValidatedProposal(activeGoal, activeTraceId, activeProposalId, activeProposalRevision);
                else
                {
                    if (rediscovery.FailureCode == "TARGET_NOT_REDISCOVERED")
                        perceivedState?.SpatialMemory?.Invalidate(activeGoal?.target_entity_id);
                    HandleCompleted(new PlanExecutionResult(PlanExecutionStatus.Failed,
                        rediscovery.FailureCode, string.Empty, 0, 0));
                }
                return;
            }
            executor?.Tick(Time.deltaTime);
        }

        [ContextMenu("Cancel Current Goal Plan")]
        public void CancelCurrentPlan() => InterruptCurrentPlan("CANCELLED_BY_REQUEST");

        public bool InterruptCurrentPlan(string failureCode)
        {
            bool hadProposal = !string.IsNullOrEmpty(activeProposalId);
            bool cancelled;
            if (awaitingRediscovery)
            {
                awaitingRediscovery = false;
                rediscovery.Cancel(failureCode);
                HandleCompleted(new PlanExecutionResult(PlanExecutionStatus.Cancelled, failureCode, string.Empty, 0, 0));
                cancelled = true;
            }
            else cancelled = executor != null && executor.Cancel(failureCode);
            if (!cancelled && CurrentTaskFeedback != null && activeGoal != null)
            {
                HandleCompleted(new PlanExecutionResult(PlanExecutionStatus.Cancelled, failureCode, "", 0, 0));
                cancelled = true;
            }
            // Execution may already be idle while its semantic reply is pending.
            // Preserve outcome metadata until the synchronous completion callback finishes.
            InvalidateProposal();
            return cancelled || hadProposal;
        }

        private void InvalidateProposal()
        {
            backendClient?.CancelPendingFailureRequest(pendingFailureRequestId);
            pendingFailureRequestId = null;
            activeProposalId = string.Empty;
            activeProposalRevision = 0;
            activeTraceId = string.Empty;
            activeSubmittedWorldRevision = 0;
            activeGoal = null;
            activeAuthorization = null;
            activeReplyLanguage = "zh-CN";
            feedbackPlayerId = string.Empty;
            CurrentTaskFeedback = null;
        }

        /// <summary>
        /// Runtime-v2 entry for an already schema-validated high-level proposal. Unity admission,
        /// planning and execution remain authoritative.
        /// </summary>
        private string activeReplyLanguage = "zh-CN";

        public bool TrySubmitGoal(GoalProposal proposal, string traceId, string proposalId,
            int proposalRevision, int worldRevision, string initiatingPlayerEntityId = null, string replyLanguage = "zh-CN")
        {
            if (proposal != null && proposal.type == "prepare_drink")
                proposal.RequestingPlayerId = initiatingPlayerEntityId;
            string rejection = SubmitRejection(proposal, worldRevision);
            if (rejection != null)
            {
                Reject(rejection, proposal);
                return false;
            }

            InvalidateProposal();
            activeReplyLanguage = EmbodiedNPC.Cognition.ReplyUtteranceContract.IsLanguage(replyLanguage) ? replyLanguage : "zh-CN";
            activeProposalId = NormalizeProposalId(proposalId);
            activeProposalRevision = System.Math.Max(0, proposalRevision);
            activeTraceId = traceId ?? string.Empty;
            activeSubmittedWorldRevision = worldRevision;
            feedbackPlayerId = initiatingPlayerEntityId ?? string.Empty;
            activeActorEntityId = actor == null ? string.Empty : actor.EntityId;
            return TryStartProposal(proposal, activeTraceId, activeProposalId, activeProposalRevision);
        }

        private string SubmitRejection(GoalProposal proposal, int worldRevision)
        {
            if (proposal == null) return "GOAL_MISSING";
            if (motionChannels != null && motionChannels.IsEmergencyMotionActive) return "THREAT_RESPONSE_ACTIVE";
            if (worldState != null && worldRevision != worldState.WorldRevision) return "STALE_WORLD_REVISION";
            return HasActiveTask ? "PLAN_ALREADY_EXECUTING" : null;
        }

        // Reuse the current admission policy without reserving, planning or starting navigation.
        internal bool TryPrevalidateGoal(GoalProposal proposal, int worldRevision, out string reason)
        {
            reason = !isActiveAndEnabled ? "GOAL_RUNNER_UNAVAILABLE" : SubmitRejection(proposal, worldRevision);
            if (reason != null) return false;
            var validation = goalValidator == null
                ? GoalValidationResult.Rejected(GoalValidationFailureCode.GoalMissing, "GoalValidator 未绑定。")
                : goalValidator.ValidateAdmission(proposal);
            reason = validation.IsApproved ? "action_valid" : validation.FailureCode.ToString();
            return validation.IsApproved;
        }

        private bool TryStartProposal(GoalProposal proposal, string traceId, string proposalId, int proposalRevision)
        {
            // Admission checks types, ownership/capabilities and anchor presence, not positions.
            // Even preparatory search motion needs a valid high-level goal.
            GoalValidationResult admission = goalValidator == null
                ? GoalValidationResult.Rejected(GoalValidationFailureCode.GoalMissing, "GoalValidator 未绑定。")
                : goalValidator.ValidateAdmission(proposal);
            if (!admission.IsApproved)
            {
                return BeginValidatedProposal(proposal, traceId, proposalId, proposalRevision);
            }
            EnsureExactAuthorization(proposal, proposalRevision, admission.ResolvedGoal?.Target?.EntityId);
            // Never use the admitted target's live anchor position until sensory confirmation.
            // Own hand state is proprioception, not knowledge of a hidden external position.
            if (admission.ResolvedGoal?.HasProprioceptiveTargetEvidence == true ||
                admission.ResolvedGoal is ResolvedPrepareDrinkGoal || admission.ResolvedGoal is ResolvedCleanCounterGoal)
                return BeginValidatedProposal(proposal, traceId, proposalId, proposalRevision);
            if (perceptionProbe.IsKnownAndNotForgotten(proposal.target_entity_id))
                return BeginValidatedProposal(proposal, traceId, proposalId, proposalRevision);
            rediscoveryDriver.Scan();
            if (rediscoveryDriver.CanSee(proposal.target_entity_id))
                return BeginValidatedProposal(proposal, traceId, proposalId, proposalRevision);
            if (perceivedState?.SpatialMemory == null ||
                !perceivedState.SpatialMemory.TryGet(proposal.target_entity_id, Time.unscaledTimeAsDouble, out var record))
            {
                Reject("TARGET_LOCATION_UNKNOWN", proposal);
                return false;
            }
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh || agent.hasPath || agent.pathPending)
            {
                Reject("REDISCOVERY_ACTOR_UNAVAILABLE", proposal);
                return false;
            }
            activeGoal = proposal;
            activeTraceId = traceId ?? string.Empty;
            activeProposalId = proposalId;
            activeProposalRevision = proposalRevision;
            rediscovery.Begin(proposal.target_entity_id, record.LastKnownPosition, Time.unscaledTimeAsDouble);
            awaitingRediscovery = true;
            motionChannels?.BeginTaskMotion();
            BeginTaskFeedback(new PlanActionProgress(PlanProgressKind.Rediscovering, "", 0, 0));
            Publish(CreateActionPresentation(ActionExecutionStatus.Recovering,
                "TARGET_REDISCOVERY_STARTED", proposal, "我记得它之前的位置，先去确认一下。"));
            return true;
        }

        private bool BeginValidatedProposal(GoalProposal proposal, string traceId, string proposalId, int proposalRevision)
        {
            GoalValidationResult validation = goalValidator == null
                ? GoalValidationResult.Rejected(GoalValidationFailureCode.GoalMissing, "GoalValidator 未绑定。")
                : goalValidator.ValidateAdmission(proposal);
            if (!validation.IsApproved)
            {
                if (CurrentTaskFeedback != null)
                {
                    HandleCompleted(new PlanExecutionResult(PlanExecutionStatus.Failed,
                        validation.FailureCode.ToString(), "", 0, 0));
                    return false;
                }
                motionChannels?.EndTaskMotion();
                activeGoal = null;
                string code = validation.FailureCode.ToString();
                Reject(code, proposal);
                _ = SendSemanticFailureAsync(proposal, proposalId, proposalRevision, "proposal_validation",
                    code, validation.Detail, null);
                return false;
            }

            activeGoal = proposal;
            activeProposalId = proposalId;
            activeProposalRevision = proposalRevision;
            activeTraceId = traceId ?? string.Empty;
            string failureCode = string.Empty;
            bool began = executor != null && executor.TryBegin(validation.ResolvedGoal, traceId, out failureCode, proposalId);
            if (!began)
            {
                string code = string.IsNullOrWhiteSpace(failureCode) ? "PLAN_EXECUTOR_UNAVAILABLE" : failureCode;
                if (CurrentTaskFeedback != null)
                {
                    HandleCompleted(new PlanExecutionResult(PlanExecutionStatus.Failed, code, "", 0, 0));
                    return false;
                }
                Reject(code, proposal);
                activeGoal = null;
                motionChannels?.EndTaskMotion();
                _ = SendSemanticFailureAsync(proposal, proposalId, proposalRevision, "planning", code, code, null);
                return false;
            }

            motionChannels?.BeginTaskMotion();
            if (CurrentTaskFeedback == null)
                BeginTaskFeedback(new PlanActionProgress(PlanProgressKind.Accepted, "", 0, 0));
            else HandleProgress(new PlanActionProgress(PlanProgressKind.Accepted, "", 0, ReplanCount));
            Publish(CreateActionPresentation(ActionExecutionStatus.Accepted, string.Empty, proposal));
            return true;
        }

        private void HandleTaskFailureResponse(ValidatedTaskFailureResponse response)
        {
            if (response == null || response.RequestId != pendingFailureRequestId ||
                string.IsNullOrEmpty(activeProposalId) || response.ProposalId != activeProposalId ||
                response.ProposalRevision != activeProposalRevision + 1) return;

            pendingFailureRequestId = null; // The inbox already consumed this exact ticket.
            activeProposalRevision = response.ProposalRevision;
            if (response.Resolution == "revise" && response.SelectedGoal != null)
            {
                if (IsExecuting || motionChannels != null && motionChannels.IsEmergencyMotionActive)
                {
                    Reject("REVISION_EXECUTION_NOT_ALLOWED", response.SelectedGoal);
                    return;
                }
                TryStartProposal(response.SelectedGoal, response.TraceId, response.ProposalId,
                    response.ProposalRevision);
                return;
            }

            Publish(CreateActionPresentation(ActionExecutionStatus.Clarification,
                "TASK_" + response.Resolution.ToUpperInvariant(), activeGoal, response.Utterance));
            InvalidateProposal();
        }

        private void HandleTaskFailureRequestInvalidated(string requestId)
        {
            if (requestId == pendingFailureRequestId) pendingFailureRequestId = null;
        }

        private async System.Threading.Tasks.Task SendSemanticFailureAsync(GoalProposal failedGoal,
            string proposalId, int proposalRevision, string stage, string failureCode, string detail,
            PlanExecutionStatus? status)
        {
            // Persistent task attempts settle through their trusted receipt path;
            // a failure must not start the ordinary semantic recovery model loop.
            if(GetComponent<EmbodiedNPC.Cognition.TaskExecutionBridge>()?.Owns(proposalId,activeTraceId)??false) return;
            if (backendClient == null || !backendClient.IsConnected || failedGoal == null ||
                proposalId != activeProposalId || proposalRevision != activeProposalRevision ||
                proposalRevision >= 2 || !TaskFailurePolicy.ShouldReport(stage, failureCode, status)) return;
            if (!BuiltInActionCatalog.Instance.TryGet(failedGoal.type, out ActionRegistration registration) ||
                !registration.ReportsTaskFailures) return;

            // Failed search is not permission to enumerate unseen replacement objects.
            TaskAlternative[] alternatives = System.Array.Empty<TaskAlternative>();
            if (!TaskFailurePolicy.IsSpatialEvidenceFailure(failureCode) && activeAuthorization != null &&
                registration.CandidateProvider != null && registration.RecoveryPolicy != null)
            {
                ActionCandidateContext candidateContext = new(activeAuthorization,
                    BuildObserverLocalCandidateIds(Time.unscaledTimeAsDouble), entityRegistry, actor,
                    StableRecoverySeed(proposalId, proposalRevision));
                alternatives = TaskFailurePolicy.BuildAlternatives(registration, candidateContext, failedGoal);
            }
            TaskFailureReport report = new()
            {
                request_id = $"task-failure-{System.Guid.NewGuid():N}",
                trace_id = $"failure-trace-{System.Guid.NewGuid():N}",
                world_revision = worldState == null ? 0 : worldState.WorldRevision,
                proposal_id = proposalId,
                proposal_revision = proposalRevision,
                reply_language = activeReplyLanguage,
                character = actor?.GetComponent<EmbodiedNPC.Cognition.CognitiveContextComposer>()?.CaptureCharacterProfile(),
                failure_stage = stage,
                failure_code = failureCode,
                detail = detail ?? string.Empty,
                failed_goal = new GoalProposal
                {
                    type = failedGoal.type,
                    target_entity_id = failedGoal.target_entity_id,
                    destination_entity_id = failedGoal.destination_entity_id,
                },
                involved_entity_ids = failedGoal.target_entity_id == failedGoal.destination_entity_id
                    ? new[] { failedGoal.target_entity_id }
                    : new[] { failedGoal.target_entity_id, failedGoal.destination_entity_id },
                retryable = alternatives.Length > 0,
                allowed_alternatives = alternatives,
            };
            backendClient.CancelPendingFailureRequest(pendingFailureRequestId);
            pendingFailureRequestId = report.request_id;
            bool sent = await backendClient.SendTaskFailureReportAsync(report);
            if (!sent && pendingFailureRequestId == report.request_id) pendingFailureRequestId = null;
            Debug.Log($"[GoalPlan] TASK_FAILURE_REPORT code={failureCode}; stage={stage}; " +
                      $"proposal={proposalId}; revision={proposalRevision}; alternatives={alternatives.Length}; sent={sent}",
                this);
        }

        private void EnsureExactAuthorization(GoalProposal proposal, int proposalRevision, string resolvedTargetId = null)
        {
            if (activeAuthorization != null || proposal == null ||
                !BuiltInActionCatalog.Instance.TryGet(proposal.type, out ActionRegistration registration)) return;
            activeAuthorization = new ExecutionAuthorization(proposal.type, ActionSelectionMode.Exact,
                new[] { resolvedTargetId ?? proposal.target_entity_id }, registration.Descriptor.ConfirmationPolicy,
                proposalRevision, activeSubmittedWorldRevision);
        }

        private IReadOnlyCollection<string> BuildObserverLocalCandidateIds(double now)
        {
            HashSet<string> ids = new(System.StringComparer.Ordinal);
            if (perceivedState?.Aggregator != null)
            {
                foreach (Observation observation in perceivedState.Aggregator.Observations)
                {
                    if (observation == null || string.IsNullOrWhiteSpace(observation.SubjectEntityId) ||
                        observation.LifecycleState == PerceptionLifecycleState.Forgotten ||
                        observation.ExpiresAt < now) continue;
                    ids.Add(observation.SubjectEntityId);
                }
            }
            if (perceivedState?.SpatialMemory != null)
            {
                foreach (SpatialMemoryRecord record in perceivedState.SpatialMemory.GetRecords(now))
                    ids.Add(record.EntityId);
            }
            return ids;
        }

        private static int StableRecoverySeed(string proposalId, int proposalRevision)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char value in proposalId ?? string.Empty)
                {
                    hash ^= value;
                    hash *= 16777619u;
                }
                return (int)(hash ^ (uint)proposalRevision);
            }
        }

        internal static string NormalizeProposalId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return "proposal_unknown";
            System.Text.StringBuilder builder = new(requestId.StartsWith("proposal_", System.StringComparison.Ordinal)
                ? "" : "proposal_");
            foreach (char character in requestId)
                builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '-'
                    ? character : '_');
            return builder.Length <= 120 ? builder.ToString() : builder.ToString(0, 120);
        }

        private void BeginTaskFeedback(PlanActionProgress progress)
        {
            if (CurrentTaskFeedback != null || activeGoal == null) return;
            activeActorEntityId = actor == null ? activeActorEntityId : actor.EntityId;
            CurrentTaskFeedback = CreateFeedback(progress);
            PublishFeedback(TaskStarted, CurrentTaskFeedback);
        }

        private TaskFeedbackSnapshot CreateFeedback(PlanActionProgress progress) => new(
            new TaskFeedbackKey(activeActorEntityId, activeProposalId, activeProposalRevision, activeTraceId),
            activeGoal.type, activeGoal.target_entity_id, activeGoal.destination_entity_id,
            feedbackPlayerId, worldState == null ? activeSubmittedWorldRevision : worldState.WorldRevision,
            ++feedbackSequence, progress, CurrentTaskFeedback?.TargetDefinition ?? ResolveTargetDefinition(), activeGoal.drink_type);

        private string ResolveTargetDefinition() => !string.IsNullOrEmpty(activeGoal.target_entity_id) && entityRegistry != null &&
            entityRegistry.TryGetEntity(activeGoal.target_entity_id, out WorldEntity target) ? target.DefinitionId : "";

        private void HandleProgress(PlanActionProgress progress)
        {
            if (CurrentTaskFeedback == null || activeGoal == null) return;
            CurrentTaskFeedback = CreateFeedback(progress);
            PublishFeedback(TaskPhaseChanged, CurrentTaskFeedback);
        }

        private void PublishFeedback(System.Action<TaskFeedbackSnapshot> observers, TaskFeedbackSnapshot snapshot)
        {
            if (observers == null) return;
            foreach (System.Delegate observer in observers.GetInvocationList())
                try { ((System.Action<TaskFeedbackSnapshot>)observer)(snapshot); }
                catch (System.Exception exception)
                { Debug.LogWarning($"[GoalPlan] Feedback observer failed: {exception.GetType().Name}", this); }
        }

        private void HandleRecovering(string failureCode) =>
            Publish(CreateActionPresentation(ActionExecutionStatus.Recovering, failureCode, activeGoal));

        private void HandleCompleted(PlanExecutionResult result)
        {
            if (activeGoal == null) return; // Already settled or never accepted.
            GoalProposal completedGoal = activeGoal;
            string proposalId = activeProposalId;
            int proposalRevision = activeProposalRevision;
            string traceId = activeTraceId;
            GoalOutcome? outcome = BuildGoalOutcome(completedGoal, proposalId, proposalRevision, traceId, result);
            GoalPlanPresentation presentation = CreateActionPresentation(
                ToActionExecutionStatus(result.Status), result.FailureCode, completedGoal);
            presentation = new GoalPlanPresentation(presentation.Kind, presentation.Code, presentation.Message, outcome);
            Debug.Log($"[GoalPlan] {result.Status} type={completedGoal?.type}; target={completedGoal?.target_entity_id}; " +
                      $"destination={completedGoal?.destination_entity_id}; plan_hash={result.PlanHash}; " +
                      $"completed_actions={result.CompletedActions}; replans={result.ReplanCount}; " +
                      $"failure_code={result.FailureCode}", this);
            if (result.Status != PlanExecutionStatus.Succeeded && !shuttingDown)
                safetyController?.RecoverToSafeState();
            motionChannels?.EndTaskMotion();
            activeGoal = null;
            CurrentTaskFeedback = null;

            if (result.Status == PlanExecutionStatus.Succeeded || result.Status == PlanExecutionStatus.Cancelled)
            {
                InvalidateProposal();
            }
            PublishGoalOutcome(outcome);
            Publish(presentation);

            if (result.Status != PlanExecutionStatus.Succeeded && result.Status != PlanExecutionStatus.Cancelled &&
                !(GetComponent<EmbodiedNPC.Cognition.TaskExecutionBridge>()?.Owns(proposalId,traceId)??false))
                _ = SendSemanticFailureAsync(completedGoal, proposalId, proposalRevision, "execution",
                    result.FailureCode, result.FailureCode, result.Status);
        }

        private GoalOutcome? BuildGoalOutcome(GoalProposal completedGoal, string proposalId, int proposalRevision,
            string traceId, PlanExecutionResult result)
        {
            string actorId = actor == null ? activeActorEntityId : actor.EntityId;
            if (completedGoal == null || string.IsNullOrEmpty(actorId)) return null;
            string outcomeId = $"goal_outcome_{proposalId}_r{proposalRevision}_{result.Status}";
            return new GoalOutcome(outcomeId, proposalId, proposalRevision, traceId,
                completedGoal.type, actorId, completedGoal.target_entity_id,
                completedGoal.destination_entity_id, result.Status, result.FailureCode,
                worldState == null ? 0 : worldState.WorldRevision,
                Time.realtimeSinceStartupAsDouble);
        }

        private void PublishGoalOutcome(GoalOutcome? outcome)
        {
            if (!outcome.HasValue || GoalOutcomePublished == null) return;
            foreach (System.Delegate subscriber in GoalOutcomePublished.GetInvocationList())
            {
                try { ((System.Action<GoalOutcome>)subscriber).Invoke(outcome.Value); }
                catch (System.Exception exception)
                {
                    Debug.LogError($"[GoalPlan] GoalOutcome subscriber failed: {exception.GetType().Name}", this);
                }
            }
        }

        private GoalPlanPresentation CreateActionPresentation(ActionExecutionStatus status, string code,
            GoalProposal goal, string sourceMessage = "")
        {
            string actionId = goal?.type ?? activeAuthorization?.ActionId ?? string.Empty;
            if (!BuiltInActionCatalog.Instance.TryGet(actionId, out ActionRegistration registration) ||
                registration.PresentationPolicy == null)
                return new GoalPlanPresentation(ToGoalPlanPresentationKind(status), code, sourceMessage);

            Dictionary<string, string> bindings = new(System.StringComparer.Ordinal);
            // A rejected proposal has not necessarily crossed binding/admission; never label its raw IDs validated.
            if (status != ActionExecutionStatus.Rejected)
            {
                if (!string.IsNullOrEmpty(goal?.drink_type)) bindings["drink_type"] = goal.drink_type;
                if (!string.IsNullOrWhiteSpace(goal?.target_entity_id)) bindings["target"] = goal.target_entity_id;
                if (!string.IsNullOrWhiteSpace(goal?.destination_entity_id))
                    bindings["destination"] = goal.destination_entity_id;
            }
            int currentRevision = worldState == null ? 0 : worldState.WorldRevision;
            ActionExecutionResult result = new(actionId, activeProposalId, activeTraceId, status, code, bindings,
                activeAuthorization?.WorldRevision ?? currentRevision, currentRevision, sourceMessage);
            ActionPresentationInfo presentation = registration.PresentationPolicy.Present(result);
            return new GoalPlanPresentation(ToGoalPlanPresentationKind(presentation.Status),
                presentation.Code, presentation.Message);
        }

        private static ActionExecutionStatus ToActionExecutionStatus(PlanExecutionStatus status) => status switch
        {
            PlanExecutionStatus.Succeeded => ActionExecutionStatus.Succeeded,
            PlanExecutionStatus.Failed => ActionExecutionStatus.Failed,
            PlanExecutionStatus.Cancelled => ActionExecutionStatus.Cancelled,
            PlanExecutionStatus.TimedOut => ActionExecutionStatus.TimedOut,
            _ => ActionExecutionStatus.Failed,
        };

        private static GoalPlanPresentationKind ToGoalPlanPresentationKind(ActionExecutionStatus status) =>
            status switch
            {
                ActionExecutionStatus.Accepted => GoalPlanPresentationKind.Accepted,
                ActionExecutionStatus.Recovering => GoalPlanPresentationKind.Recovering,
                ActionExecutionStatus.Rejected => GoalPlanPresentationKind.Rejected,
                ActionExecutionStatus.Succeeded => GoalPlanPresentationKind.Succeeded,
                ActionExecutionStatus.Clarification or ActionExecutionStatus.RequiresConfirmation =>
                    GoalPlanPresentationKind.Clarification,
                ActionExecutionStatus.Failed or ActionExecutionStatus.Cancelled or ActionExecutionStatus.TimedOut =>
                    GoalPlanPresentationKind.Failed,
                _ => GoalPlanPresentationKind.Failed,
            };

        private void Reject(string code, GoalProposal goal = null)
        {
            Debug.LogWarning($"[GoalPlan] REJECTED code={code}", this);
            Publish(CreateActionPresentation(ActionExecutionStatus.Rejected, code, goal));
        }

        private void Publish(GoalPlanPresentationKind kind, string code, string message = "")
        {
            Publish(new GoalPlanPresentation(kind, code, message));
        }

        private void Publish(GoalPlanPresentation presentation) => PresentationReady?.Invoke(presentation);
    }

    public readonly struct GoalPlanPresentation
    {
        public GoalPlanPresentation(GoalPlanPresentationKind kind, string code, string message = "", GoalOutcome? outcome = null)
        {
            Kind = kind;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Outcome = outcome;
        }

        public GoalPlanPresentationKind Kind { get; }
        public string Code { get; }
        public string Message { get; }
        // Local terminal provenance; not a new outcome or Backend wire field.
        public GoalOutcome? Outcome { get; }

    }

    public enum GoalPlanPresentationKind { Accepted, Recovering, Rejected, Succeeded, Failed, Clarification }
}
