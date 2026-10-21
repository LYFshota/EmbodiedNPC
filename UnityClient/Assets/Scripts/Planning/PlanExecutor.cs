using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Planning.Recovery;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    public enum PlanExecutionStatus { Succeeded, Failed, Cancelled, TimedOut }

    public readonly struct PlanExecutionResult
    {
        public PlanExecutionResult(PlanExecutionStatus status, string failureCode, string planHash,
            int completedActions, int replanCount)
        {
            Status = status;
            FailureCode = failureCode ?? string.Empty;
            PlanHash = planHash ?? string.Empty;
            CompletedActions = completedActions;
            ReplanCount = replanCount;
        }

        public PlanExecutionStatus Status { get; }
        public string FailureCode { get; }
        public string PlanHash { get; }
        public int CompletedActions { get; }
        public int ReplanCount { get; }
    }

    public readonly struct PlanExecutionDebugInfo
    {
        public PlanExecutionDebugInfo(string planId, string planHash, int actionCount, int replanCount,
            int worldRevision, int perceptionRevision, string reason)
        {
            PlanId = planId ?? string.Empty;
            PlanHash = planHash ?? string.Empty;
            ActionCount = actionCount;
            ReplanCount = replanCount;
            WorldRevision = worldRevision;
            PerceptionRevision = perceptionRevision;
            Reason = reason ?? string.Empty;
        }

        public string PlanId { get; }
        public string PlanHash { get; }
        public int ActionCount { get; }
        public int ReplanCount { get; }
        public int WorldRevision { get; }
        public int PerceptionRevision { get; }
        public string Reason { get; }
    }

    /// <summary>Pure bounded-loop policy shared by runtime execution and EditMode tests.</summary>
    public sealed class PlanReplanGuard
    {
        private readonly int maxReplans;
        private readonly int maxSameHashReplans;
        private readonly Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

        public PlanReplanGuard(int maxReplans = 2, int maxSameHashReplans = 1)
        {
            this.maxReplans = Math.Max(0, maxReplans);
            this.maxSameHashReplans = Math.Max(0, maxSameHashReplans);
        }

        public int ReplanCount { get; private set; }

        public bool TryAccept(string planHash, bool isReplan, out string failureCode)
        {
            if (string.IsNullOrWhiteSpace(planHash))
            {
                failureCode = "PLAN_HASH_MISSING";
                return false;
            }
            if (isReplan && ReplanCount >= maxReplans)
            {
                failureCode = "GOAL_REPLAN_BUDGET_EXHAUSTED";
                return false;
            }

            occurrences.TryGetValue(planHash, out int previousOccurrences);
            int priorReplansForHash = Math.Max(0, previousOccurrences - 1);
            if (isReplan && priorReplansForHash >= maxSameHashReplans)
            {
                failureCode = "SAME_PLAN_HASH_REPLAN_EXHAUSTED";
                return false;
            }

            if (isReplan) ReplanCount++;
            occurrences[planHash] = previousOccurrences + 1;
            failureCode = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Owns one admitted Goal's plan, action sequencing, validation, reservation and bounded replanning.
    /// GoalPlanRunner remains the backend/presentation adapter; this type never interprets model text.
    /// </summary>
    public sealed class PlanExecutor
    {
        private const float ReplanBackoffSeconds = 0.5f;
        private const float GoalDurationSeconds = 30f;

        private readonly EntityRegistry entityRegistry;
        private readonly ResourceReservationService reservations;
        private readonly WorldStateService worldState;
        private readonly NavMeshAgent agent;
        private readonly SafeDropZone dropZone;
        private readonly Transform actorHoldPoint;
        private readonly Transform destinationHoldParent;
        private readonly IPlannerNavigationProbe navigationProbe;
        private readonly IPlannerPerceptionProbe perceptionProbe;
        private readonly PlannerSnapshotBuilder snapshotBuilder;
        private readonly RuntimeActionValidator actionValidator;
        private readonly UniformCostPlanner planner = new();
        private readonly PlanActionRunnerSet runnerSet;
        private readonly float leaseRenewIntervalSeconds;
        private readonly VisionSensor visionSensor;
        private Vector3? lastConfirmedApproachPosition;
        private readonly PerceivedStateStore perceivedState;
        private readonly TargetRediscovery targetRediscovery;
        private readonly NavigationApproachResolver approachResolver = new();
        private readonly ActionCatalog actionCatalog;
        private int rediscoveryAttempts;
        private bool rediscovering;

        private IResolvedActionGoal resolvedGoal;
        private ActionRegistration actionRegistration;
        private ActionExecutionPreparation preparation;
        private string goalId;
        private string traceId;
        private string proposalId;
        private PlanResult plan;
        private int actionIndex;
        private int completedActions;
        private int snapshotSequence;
        private IPlanActionRunner activeRunner;
        private ReservationLeaseSnapshot lease;
        private RetryBudget retryBudget;
        private PlanReplanGuard replanGuard;
        private bool waitingToReplan;
        private float replanAt;
        private string replanReason;
        private float nextLeaseRenewAt;

        public PlanExecutor(EntityRegistry entityRegistry, ResourceReservationService reservations,
            WorldStateService worldState, NavMeshAgent agent, PerceivedStateStore perceivedState,
            SafeDropZone dropZone, Transform actorHoldPoint, Transform destinationHoldParent,
            float leaseRenewIntervalSeconds = 5f, float interactionRange = 0.85f,
            ActionCatalog actionCatalog = null)
        {
            this.entityRegistry = entityRegistry;
            this.reservations = reservations;
            this.worldState = worldState;
            this.agent = agent;
            this.dropZone = dropZone;
            this.actorHoldPoint = actorHoldPoint;
            this.destinationHoldParent = destinationHoldParent;
            this.leaseRenewIntervalSeconds = Mathf.Max(0.5f, leaseRenewIntervalSeconds);
            this.actionCatalog = actionCatalog ?? BuiltInActionCatalog.Instance;
            navigationProbe = new NavMeshPlannerNavigationProbe(agent);
            perceptionProbe = new PerceivedStatePlannerProbe(perceivedState);
            visionSensor = perceivedState == null ? null : perceivedState.GetComponent<VisionSensor>();
            this.perceivedState = perceivedState;
            targetRediscovery = new TargetRediscovery(new NavMeshRediscoveryDriver(agent, visionSensor));
            snapshotBuilder = new PlannerSnapshotBuilder(entityRegistry, reservations, worldState, navigationProbe, dropZone);
            actionValidator = new RuntimeActionValidator(entityRegistry, reservations, navigationProbe,
                perceptionProbe, interactionRange);
            runnerSet = new PlanActionRunnerSet(new IPlanActionRunner[]
            {
                new WalkToActionRunner(), new PickUpActionRunner(), new PutDownActionRunner(), new GiveActionRunner(),
                new OpenActionRunner(), new PrepareDrinkActionRunner(), new CleanCounterActionRunner(),
            });
        }

        public event Action<string> Recovering;
        public event Action<PlanExecutionDebugInfo> PlanChanged;
        public event Action<PlanExecutionResult> Completed;
        public event Action<PlanActionProgress> ProgressChanged;

        public bool IsExecuting { get; private set; }
        public bool IsRediscovering => rediscovering;
        public string ActivePlanHash => plan?.PlanHash ?? string.Empty;
        public int CurrentActionIndex => actionIndex;
        public int ReplanCount => replanGuard?.ReplanCount ?? 0;

        public bool TryBegin(IResolvedActionGoal admittedGoal, string executionTraceId, out string failureCode,
            string executionProposalId = "")
        {
            failureCode = string.Empty;
            if (IsExecuting)
            {
                failureCode = "PLAN_ALREADY_EXECUTING";
                return false;
            }
            if (admittedGoal == null)
            {
                failureCode = "REFERENCE_MISSING";
                return false;
            }
            if (!actionCatalog.TryGet(admittedGoal.ActionId, out ActionRegistration registration))
            {
                failureCode = "ACTION_NOT_REGISTERED";
                return false;
            }
            if (registration.PlanFactory == null || registration.ExecutionPreparationPolicy == null)
            {
                failureCode = "ACTION_REGISTRATION_INCOMPLETE";
                return false;
            }
            if (entityRegistry == null || worldState == null || agent == null)
            {
                failureCode = "REFERENCE_MISSING";
                return false;
            }

            resolvedGoal = admittedGoal;
            actionRegistration = registration;
            traceId = executionTraceId ?? string.Empty;
            proposalId = executionProposalId ?? string.Empty;
            goalId = $"goal_{Guid.NewGuid():N}";
            actionIndex = 0;
            completedActions = 0;
            rediscoveryAttempts = 0;
            rediscovering = false;
            snapshotSequence = 0;
            replanGuard = new PlanReplanGuard(maxReplans: 2, maxSameHashReplans: 1);
            retryBudget = new RetryBudget(Time.unscaledTime, maxSameFailureRetries: 1,
                maxRecoveriesPerGoal: 2, maxGoalDurationSeconds: GoalDurationSeconds);

            if (!TryBuildPlan(false, "INITIAL_PLAN", out failureCode))
            {
                ResetSession();
                return false;
            }
            if ((preparation.LeaseRequirement.IsRequired && reservations == null) ||
                (preparation.Bindings.TargetHeldState != null &&
                 (actorHoldPoint == null || destinationHoldParent == null)))
            {
                failureCode = "REFERENCE_MISSING";
                ResetSession();
                return false;
            }
            if (plan.Status == PlanSearchStatus.Found && preparation.LeaseRequirement.IsRequired &&
                !reservations.TryAcquire(preparation.LeaseRequirement.Target.EntityId,
                    preparation.Bindings.Actor.EntityId, traceId, out lease))
            {
                failureCode = "TARGET_RESERVED";
                ResetSession();
                return false;
            }
            IsExecuting = true;
            if (preparation.LeaseRequirement.IsRequired)
                nextLeaseRenewAt = Time.time + leaseRenewIntervalSeconds;
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (!IsExecuting) return;
            if (resolvedGoal is ResolvedPrepareDrinkGoal drink &&
                !DrinkTypes.ValidateRuntime(entityRegistry, drink.Actor, drink.Target, drink.Player))
            { Finish(PlanExecutionStatus.Failed, "STATION_UNAVAILABLE"); return; }
            if (resolvedGoal is ResolvedCleanCounterGoal clean &&
                !CleanCounterRules.ValidateRuntime(entityRegistry, clean.Actor, clean.Target, clean.ConditionVersion))
            { Finish(PlanExecutionStatus.Failed, "COUNTER_UNAVAILABLE"); return; }
            if (retryBudget != null && retryBudget.IsExpired(Time.unscaledTime))
            {
                Finish(PlanExecutionStatus.TimedOut, "GOAL_DEADLINE_EXCEEDED");
                return;
            }
            if (plan.Status == PlanSearchStatus.AlreadySatisfied)
            {
                Finish(PlanExecutionStatus.Succeeded, string.Empty);
                return;
            }
            if (!RenewLeaseIfNeeded()) return;
            if (rediscovering)
            {
                targetRediscovery.Tick(Time.unscaledTimeAsDouble, Time.unscaledDeltaTime);
                if (targetRediscovery.Status == RediscoveryStatus.Searching) return;
                rediscovering = false;
                if (targetRediscovery.Status != RediscoveryStatus.Succeeded)
                {
                    if (targetRediscovery.FailureCode == "TARGET_NOT_REDISCOVERED")
                        perceivedState?.SpatialMemory?.Invalidate(ActiveTarget?.EntityId);
                    Finish(PlanExecutionStatus.Failed, targetRediscovery.FailureCode);
                }
                // Preserve the suspended phase: a pending replan must rebuild first;
                // an action-level search instead revalidates the current action next tick.
                return; // no world action executes in the search callback
            }
            if (waitingToReplan)
            {
                if (Time.unscaledTime >= replanAt) ExecuteReplan();
                return;
            }
            if (activeRunner != null)
            {
                PlanActionRunResult result = activeRunner.Tick(deltaTime);
                if (result.Status != PlanActionRunStatus.Running && result.Status != PlanActionRunStatus.Idle)
                    HandleActionResult(result);
                return;
            }
            StartCurrentAction();
        }

        public bool Cancel(string failureCode)
        {
            if (!IsExecuting) return false;
            activeRunner?.Cancel(string.IsNullOrWhiteSpace(failureCode) ? "CANCELLED_BY_REQUEST" : failureCode);
            Finish(PlanExecutionStatus.Cancelled,
                string.IsNullOrWhiteSpace(failureCode) ? "CANCELLED_BY_REQUEST" : failureCode);
            return true;
        }

        private void StartCurrentAction()
        {
            if (plan == null || actionIndex >= plan.Actions.Count)
            {
                Finish(PlanExecutionStatus.Succeeded, string.Empty);
                return;
            }

            ActionInstance action = plan.Actions[actionIndex];
            bool usesTargetPosition = action.Definition.ActionType == PlannerActionType.PickUp ||
                action.Definition.ActionType == PlannerActionType.WalkTo &&
                action.Bindings.TryGetValue("destination", out string walkTarget) && walkTarget == ActiveTarget?.EntityId;
            if (usesTargetPosition && !HasCurrentTargetEvidence())
            {
                if (TryRediscoverTarget()) return;
                Finish(PlanExecutionStatus.Failed, "TARGET_NOT_CURRENTLY_VISIBLE");
                return;
            }
            if (preparation == null)
            {
                Finish(PlanExecutionStatus.Failed, "EXECUTION_PREPARATION_MISSING");
                return;
            }
            if (preparation.Bindings.TargetApproachPosition.HasValue)
            {
                WorldEntity target = preparation.Bindings.Target;
                if (!entityRegistry.TryGetEntity(target.EntityId, out WorldEntity currentTarget) ||
                    currentTarget != target || !approachResolver.TryResolve(agent, currentTarget, out Vector3 position))
                {
                    RequestReplan(action, "TARGET_UNREACHABLE", worldState?.WorldRevision ?? 0);
                    return;
                }
                preparation = preparation.WithBindings(
                    preparation.Bindings.WithTargetApproachPosition(position));
            }
            if (preparation.RefreshBeforeActionValidation &&
                !TryPrepareExecution(out string preparationFailure))
            {
                Finish(PlanExecutionStatus.Failed, preparationFailure);
                return;
            }
            RuntimeActionValidationResult validation = actionValidator.Validate(
                new RuntimeActionValidationContext(plan, action, preparation, lease.ReservationId, dropZone));
            if (validation.Decision == RuntimeActionDecision.NeedsReplan)
            {
                RequestReplan(action, validation.FailureCode,
                    preparation.PlanningInput.Snapshot.WorldRevision);
                return;
            }
            if (validation.Decision == RuntimeActionDecision.Abort)
            {
                Finish(PlanExecutionStatus.Failed, validation.FailureCode);
                return;
            }
            if (!runnerSet.TryGet(action.Definition.ActionType, out activeRunner))
            {
                Finish(PlanExecutionStatus.Failed, "ACTION_RUNNER_MISSING");
                return;
            }

            Debug.Log($"[PlanExecutor] START plan={plan.PlanId}; index={actionIndex}; action={action}");
            PublishProgress(new PlanActionProgress(PlanProgressKind.Action, plan.PlanId, actionIndex,
                ReplanCount, action.Definition.ActionType, action.Bindings));
            PlanActionRunResult result = activeRunner.Start(new PlanActionExecutionContext(action,
                preparation.Bindings, lease.ReservationId, traceId, agent,
                entityRegistry, worldState, actorHoldPoint, destinationHoldParent, dropZone, proposalId, reservations));
            if (result.Status != PlanActionRunStatus.Running) HandleActionResult(result);
        }

        private void HandleActionResult(PlanActionRunResult result)
        {
            ActionInstance action = plan.Actions[actionIndex];
            activeRunner = null;
            if (result.Status == PlanActionRunStatus.Succeeded)
            {
                if (preparation?.CompletionFacingTarget != null && preparation.Bindings.Actor != null)
                {
                    Vector3 facing = preparation.CompletionFacingTarget.transform.position -
                                     preparation.Bindings.Actor.transform.position;
                    facing.y = 0f;
                    if (facing.sqrMagnitude > 0.001f)
                        preparation.Bindings.Actor.transform.rotation =
                            Quaternion.LookRotation(facing.normalized, Vector3.up);
                }
                completedActions++;
                actionIndex++;
                if (actionIndex >= plan.Actions.Count) Finish(PlanExecutionStatus.Succeeded, string.Empty);
                return;
            }
            if (result.Status == PlanActionRunStatus.Cancelled)
            {
                Finish(PlanExecutionStatus.Cancelled, result.SkillResult.FailureCode);
                return;
            }
            if (result.Status == PlanActionRunStatus.TimedOut)
            {
                RequestRecovery(action, result.SkillResult);
                return;
            }
            RequestRecovery(action, result.SkillResult);
        }

        private void RequestRecovery(ActionInstance action, SkillResult result)
        {
            FailureSignature signature = new(ActiveGoalType, action.Definition.SkillType,
                result.TargetEntityId ?? ActiveTarget?.EntityId ?? string.Empty, result.FailureCode, result.WorldRevisionAfter);
            RecoveryDecision decision = RecoveryPolicy.Decide(result, signature, retryBudget, Time.unscaledTime);
            Debug.Log($"[PlanExecutor] RECOVERY category={decision.Category}; action={decision.Action}; " +
                      $"reason={decision.ReasonCode}; plan_hash={plan.PlanHash}");
            if (decision.Action == RecoveryAction.RetryCurrentAction)
            {
                ScheduleReplan(result.FailureCode);
                return;
            }
            Finish(result.Status == SkillStatus.TimedOut ? PlanExecutionStatus.TimedOut : PlanExecutionStatus.Failed,
                decision.Action == RecoveryAction.RequestClarification
                    ? "CLARIFICATION_REQUIRED:" + result.FailureCode
                    : decision.ReasonCode);
        }

        private void RequestReplan(ActionInstance action, string failureCode, int worldRevision)
        {
            FailureSignature signature = new(ActiveGoalType, action.Definition.SkillType,
                ActiveTarget?.EntityId ?? string.Empty, failureCode, worldRevision);
            string rejectionCode = string.Empty;
            if (retryBudget == null || !retryBudget.TryConsume(signature, Time.unscaledTime, out rejectionCode))
            {
                Finish(PlanExecutionStatus.Failed,
                    string.IsNullOrWhiteSpace(rejectionCode) ? "RECOVERY_BUDGET_UNAVAILABLE" : rejectionCode);
                return;
            }
            ScheduleReplan(failureCode);
        }

        private void ScheduleReplan(string reason)
        {
            waitingToReplan = true;
            replanReason = string.IsNullOrWhiteSpace(reason) ? "ACTION_PRECONDITION_CHANGED" : reason;
            replanAt = Time.unscaledTime + ReplanBackoffSeconds;
            PublishProgress(new PlanActionProgress(PlanProgressKind.Replanning, plan?.PlanId, actionIndex, ReplanCount));
            Recovering?.Invoke(replanReason);
        }

        private void ExecuteReplan()
        {
            if (!EnsureLease(out string leaseFailure))
            {
                Finish(PlanExecutionStatus.Failed, leaseFailure);
                return;
            }
            if (!TryBuildPlan(true, replanReason, out string failureCode))
            {
                // Keep waitingToReplan and the original failure reason while searching.
                // The same Goal retains its lease, deadline and consumed recovery budget.
                // On success Tick returns here before any old plan action can restart.
                if (failureCode == "TARGET_NOT_CURRENTLY_VISIBLE" && TryRediscoverTarget()) return;
                Finish(PlanExecutionStatus.Failed, failureCode);
                return;
            }
            waitingToReplan = false;
            actionIndex = 0;
            replanReason = string.Empty;
        }

        private bool TryBuildPlan(bool isReplan, string reason, out string failureCode)
        {
            if (!HasCurrentTargetEvidence())
            {
                failureCode = "TARGET_NOT_CURRENTLY_VISIBLE";
                return false;
            }
            if (!TryPrepareExecution(out failureCode))
                return false;
            PlanResult candidate = actionRegistration.PlanFactory.CreatePlan(
                planner, goalId, preparation.PlanningInput);
            if (!candidate.IsSuccess)
            {
                failureCode = candidate.Status == PlanSearchStatus.NoPlan ? "NO_PLAN" : candidate.Status.ToString().ToUpperInvariant();
                return false;
            }
            if (!replanGuard.TryAccept(candidate.PlanHash, isReplan, out failureCode)) return false;

            plan = candidate;
            actionIndex = 0;
            PlanChanged?.Invoke(new PlanExecutionDebugInfo(plan.PlanId, plan.PlanHash, plan.Actions.Count,
                replanGuard.ReplanCount, plan.PlannedFromWorldRevision, plan.PlannedFromPerceptionRevision, reason));
            Debug.Log($"[PlanExecutor] PLAN id={plan.PlanId}; hash={plan.PlanHash}; actions=" +
                      $"{string.Join(" -> ", plan.Actions.Select(action => action.ToString()))}; " +
                      $"replans={replanGuard.ReplanCount}; reason={reason}");
            failureCode = string.Empty;
            return true;
        }

        private bool TryPrepareExecution(out string failureCode)
        {
            if (resolvedGoal == null || actionRegistration?.ExecutionPreparationPolicy == null)
            {
                failureCode = "EXECUTION_PREPARATION_MISSING";
                return false;
            }

            ActionExecutionPreparationContext context = new($"snapshot_{++snapshotSequence}", goalId,
                perceptionProbe?.PerceptionRevision ?? 0,
                visionSensor == null ? null : lastConfirmedApproachPosition,
                snapshotBuilder, entityRegistry, worldState, agent, approachResolver);
            ActionExecutionPreparationResult result =
                actionRegistration.ExecutionPreparationPolicy.Prepare(resolvedGoal, context);
            if (!result.IsSuccess)
            {
                failureCode = result.FailureCode;
                return false;
            }
            if (!string.Equals(result.Preparation.PlanningInput.ActionId, resolvedGoal.ActionId,
                    StringComparison.Ordinal))
            {
                failureCode = "EXECUTION_PREPARATION_ACTION_MISMATCH";
                return false;
            }

            preparation = result.Preparation;
            failureCode = string.Empty;
            return true;
        }

        private bool TryRediscoverTarget()
        {
            if (rediscoveryAttempts >= 1 || perceivedState?.SpatialMemory == null ||
                ActiveTarget == null ||
                !perceivedState.SpatialMemory.TryGet(ActiveTarget.EntityId, Time.unscaledTimeAsDouble, out var record)) return false;
            rediscoveryAttempts++;
            targetRediscovery.Begin(ActiveTarget.EntityId, record.LastKnownPosition, Time.unscaledTimeAsDouble);
            rediscovering = true;
            PublishProgress(new PlanActionProgress(PlanProgressKind.Rediscovering, plan?.PlanId, actionIndex, ReplanCount));
            Recovering?.Invoke("TARGET_REDISCOVERY_STARTED");
            return true;
        }

        private void PublishProgress(PlanActionProgress progress)
        {
            if (ProgressChanged == null) return;
            foreach (Delegate observer in ProgressChanged.GetInvocationList())
                try { ((Action<PlanActionProgress>)observer)(progress); }
                catch (Exception exception)
                { Debug.LogWarning($"[PlanExecutor] Progress observer failed: {exception.GetType().Name}"); }
        }

        private bool HasCurrentTargetEvidence()
        {
            // Only this admitted action may use its configured station. Movable item perception is unchanged.
            if (resolvedGoal is ResolvedPrepareDrinkGoal drink)
                return DrinkTypes.ValidateRuntime(entityRegistry, drink.Actor, drink.Target, drink.Player);
            if (resolvedGoal is ResolvedCleanCounterGoal clean)
                return CleanCounterRules.ValidateRuntime(entityRegistry, clean.Actor, clean.Target, clean.ConditionVersion);
            // Legacy test adapters can omit VisionSensor. Production plans must not refresh
            // hidden target coordinates from a new authoritative planning snapshot.
            if (visionSensor == null) return true;
            if (resolvedGoal?.HasProprioceptiveTargetEvidence == true)
            {
                lastConfirmedApproachPosition ??= resolvedGoal.Actor.transform.position;
                return true;
            }
            if (!visionSensor.isActiveAndEnabled || ActiveTarget == null) return false;
            visionSensor.ScanNow();
            bool visible = visionSensor.CurrentVisibleEntityIds.Contains(ActiveTarget.EntityId);
            if (visible && preparation?.Bindings.TargetAnchors?.PickupApproachPoint != null)
                lastConfirmedApproachPosition = preparation.Bindings.TargetAnchors.PickupApproachPoint.position;
            return visible;
        }

        private bool RenewLeaseIfNeeded()
        {
            if (preparation == null || !preparation.LeaseRequirement.IsRequired) return true;
            if (plan.Status != PlanSearchStatus.Found || Time.time < nextLeaseRenewAt) return true;
            nextLeaseRenewAt = Time.time + leaseRenewIntervalSeconds;
            if (reservations.TryRenew(lease.ReservationId, lease.OwnerEntityId, out lease)) return true;
            Finish(PlanExecutionStatus.Failed, "LEASE_EXPIRED");
            return false;
        }

        private bool EnsureLease(out string failureCode)
        {
            if (preparation == null || !preparation.LeaseRequirement.IsRequired)
            {
                failureCode = string.Empty;
                return true;
            }
            if (plan?.Status == PlanSearchStatus.AlreadySatisfied)
            {
                failureCode = string.Empty;
                return true;
            }
            if (reservations.IsLeaseValid(lease.ReservationId, preparation.Bindings.Actor.EntityId))
            {
                failureCode = string.Empty;
                return true;
            }
            if (reservations.TryAcquire(preparation.LeaseRequirement.Target.EntityId,
                    preparation.Bindings.Actor.EntityId, traceId, out lease))
            {
                nextLeaseRenewAt = Time.time + leaseRenewIntervalSeconds;
                failureCode = string.Empty;
                return true;
            }
            failureCode = "TARGET_RESERVED";
            return false;
        }

        private void Finish(PlanExecutionStatus status, string failureCode)
        {
            if (!IsExecuting) return;
            targetRediscovery.Cancel(failureCode);
            rediscovering = false;
            string finalHash = plan?.PlanHash ?? string.Empty;
            int finalReplans = replanGuard?.ReplanCount ?? 0;
            if (activeRunner?.IsRunning == true) activeRunner.Cancel(failureCode);
            ReleaseLease();
            IsExecuting = false;
            waitingToReplan = false;
            activeRunner = null;
            Completed?.Invoke(new PlanExecutionResult(status, failureCode, finalHash,
                completedActions, finalReplans));
            ResetSession();
        }

        private void ReleaseLease()
        {
            if (reservations != null && !string.IsNullOrWhiteSpace(lease.ReservationId))
                reservations.Release(lease.ReservationId, lease.OwnerEntityId);
            lease = default;
        }

        private void ResetSession()
        {
            lastConfirmedApproachPosition = null;
            IsExecuting = false;
            resolvedGoal = null;
            actionRegistration = null;
            preparation = null;
            goalId = string.Empty;
            traceId = string.Empty;
            plan = null;
            activeRunner = null;
            retryBudget = null;
            replanGuard = null;
            waitingToReplan = false;
            replanReason = string.Empty;
            lease = default;
        }

        private WorldEntity ActiveTarget => resolvedGoal?.Target;
        private string ActiveGoalType => resolvedGoal?.ActionId ?? string.Empty;
    }
}
