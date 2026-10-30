using System.Collections.Generic;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Cognition;
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
    public sealed partial class GoalPlanRunner : MonoBehaviour
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
        private int completedActionCount;

        public bool IsExecuting => awaitingRediscovery || executor != null && executor.IsExecuting;
        public bool HasActiveTask => IsExecuting || activeGoal != null;
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
            activeProposalId = string.Empty;
            activeProposalRevision = 0;
            activeTraceId = string.Empty;
            activeSubmittedWorldRevision = 0;
            activeGoal = null;
            activeAuthorization = null;
            feedbackPlayerId = string.Empty;
            CurrentTaskFeedback = null;
        }

        /// <summary>
        /// Runtime-v2 entry for an already schema-validated high-level proposal. Unity admission,
        /// planning and execution remain authoritative.
        /// </summary>

        public bool TrySubmitGoal(GoalProposal proposal, string traceId, string proposalId,
            int proposalRevision, int worldRevision, string initiatingPlayerEntityId = null)
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
                return false;
            }

            motionChannels?.BeginTaskMotion();
            if (CurrentTaskFeedback == null)
                BeginTaskFeedback(new PlanActionProgress(PlanProgressKind.Accepted, "", 0, 0));
            else HandleProgress(new PlanActionProgress(PlanProgressKind.Accepted, "", 0, ReplanCount));
            Publish(CreateActionPresentation(ActionExecutionStatus.Accepted, string.Empty, proposal));
            return true;
        }

        internal bool IsCurrentFeedback(CognitiveActionFeedback feedback, string player)
        {
            var state = CaptureExecutionState(player);
            return feedback?.IsValid == true && !HasActiveTask && state.status == "FAILED" &&
                state.outcome_id == feedback.outcome_id && state.proposal_id == activeProposalId &&
                state.proposal_id == feedback.proposal_id && state.proposal_revision == feedback.proposal_revision &&
                activeProposalRevision == feedback.proposal_revision && state.trace_id == feedback.trace_id &&
                state.world_revision == feedback.world_revision;
        }

        internal CognitiveActionFeedback BuildActionFeedback(GoalOutcome outcome, CognitiveRequest parent)
        {
            if (parent == null || outcome.Status is not (PlanExecutionStatus.Failed or PlanExecutionStatus.TimedOut) ||
                outcome.ProposalRevision >= 2 || executionOutcome?.Equals(outcome) != true ||
                (GetComponent<EmbodiedNPC.Cognition.TaskExecutionBridge>()?.Owns(outcome.ProposalId, outcome.TraceId) ?? false) ||
                !ActionFeedbackPolicy.ShouldRequestCognition(outcome.FailureCode, outcome.Status)) return null;
            var goal = new GoalProposal { type = outcome.GoalType,
                target_entity_id = string.IsNullOrEmpty(outcome.TargetEntityId) ? null : outcome.TargetEntityId,
                destination_entity_id = string.IsNullOrEmpty(outcome.DestinationEntityId) ? null : outcome.DestinationEntityId,
                drink_type = string.IsNullOrEmpty(executionFeedback?.DrinkType) ? null : executionFeedback.DrinkType };
            var feedback = new CognitiveActionFeedback { original_request_id = parent.action_feedback?.original_request_id ?? parent.request_id,
                original_run_id = parent.action_feedback?.original_run_id ?? parent.run.run_id,
                outcome_id = outcome.OutcomeId, proposal_id = outcome.ProposalId, proposal_revision = outcome.ProposalRevision,
                trace_id = outcome.TraceId, world_revision = worldState.WorldRevision, completed_actions = completedActionCount,
                failure_code = outcome.FailureCode, failed_goal = goal,
                detail = "The attempt ended with " + outcome.Status + ". Completed executor steps: " + completedActionCount +
                    ". Already committed world effects remain committed; the code does not establish a physical cause." };
            feedback.allowed_alternatives = FeedbackAlternatives(feedback);
            return IsCurrentFeedback(feedback, parent.context.relevant_player?.player_entity_id) ? feedback : null;
        }

        private CognitiveActionAlternative[] FeedbackAlternatives(CognitiveActionFeedback feedback)
        {
            // Replan only a different originally-authorized target; never replay the completed steps or widen Exact scope.
            if (ActionFeedbackPolicy.IsSpatialEvidenceFailure(feedback.failure_code) ||
                activeAuthorization == null || !BuiltInActionCatalog.Instance.TryGet(feedback.failed_goal.type, out var registration))
                return System.Array.Empty<CognitiveActionAlternative>();
            return ActionFeedbackPolicy.BuildAlternatives(registration, new ActionCandidateContext(activeAuthorization,
                BuildObserverLocalCandidateIds(Time.unscaledTimeAsDouble), entityRegistry, actor,
                StableRecoverySeed(feedback.proposal_id, feedback.proposal_revision)), feedback.failed_goal);
        }

        internal bool TryRecover(CognitiveActionFeedback feedback, string alternativeId, string player, out GoalProposal selected)
        {
            selected = null;
            if (!IsCurrentFeedback(feedback, player) || feedback.proposal_revision >= 2 ||
                motionChannels?.IsEmergencyMotionActive == true) return false;
            foreach (var candidate in FeedbackAlternatives(feedback))
                if (candidate.alternative_id == alternativeId && System.Array.Exists(feedback.allowed_alternatives,
                    a => a.alternative_id == alternativeId && a.goal.type == candidate.goal.type &&
                         a.goal.target_entity_id == candidate.goal.target_entity_id &&
                         a.goal.destination_entity_id == candidate.goal.destination_entity_id))
                {
                    if (!TryPrevalidateGoal(candidate.goal, feedback.world_revision, out _)) return false;
                    selected = candidate.goal;
                    activeProposalRevision++;
                    return TryStartProposal(selected, feedback.trace_id, feedback.proposal_id, activeProposalRevision);
                }
            return false;
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
            BindExecutionContext();
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
            executionFeedback = CurrentTaskFeedback;
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
            executionOutcome = outcome;
            completedActionCount = result.CompletedActions;
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
