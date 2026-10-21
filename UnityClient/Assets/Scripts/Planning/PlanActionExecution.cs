using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Planning
{
    public enum PlanActionRunStatus { Idle, Running, Succeeded, Failed, Cancelled, TimedOut }

    public readonly struct PlanActionRunResult
    {
        public PlanActionRunResult(PlanActionRunStatus status, SkillResult skillResult)
        {
            Status = status;
            SkillResult = skillResult;
        }

        public PlanActionRunStatus Status { get; }
        public SkillResult SkillResult { get; }
        public bool IsTerminal => Status != PlanActionRunStatus.Idle && Status != PlanActionRunStatus.Running;
        public static PlanActionRunResult Running() => new(PlanActionRunStatus.Running, default);
    }

    public sealed class PlanActionExecutionContext
    {
        public PlanActionExecutionContext(ActionInstance action, ValidatedActionBindings bindings,
            string reservationId, string traceId, NavMeshAgent agent, EntityRegistry entityRegistry,
            WorldStateService worldState, Transform actorHoldPoint, Transform destinationHoldParent,
            SafeDropZone dropZone, string proposalId = "", ResourceReservationService reservations = null)
        {
            Action = action ?? throw new ArgumentNullException(nameof(action));
            Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            ReservationId = reservationId ?? string.Empty;
            TraceId = traceId ?? string.Empty;
            Agent = agent;
            EntityRegistry = entityRegistry;
            WorldState = worldState;
            ActorHoldPoint = actorHoldPoint;
            DestinationHoldParent = destinationHoldParent;
            DropZone = dropZone;
            ProposalId = proposalId;
            Reservations = reservations;
        }

        public ActionInstance Action { get; }
        public ValidatedActionBindings Bindings { get; }
        public WorldEntity Actor => Bindings.Actor;
        public WorldEntity Target => Bindings.Target;
        public string ReservationId { get; }
        public string TraceId { get; }
        public NavMeshAgent Agent { get; }
        public EntityRegistry EntityRegistry { get; }
        public WorldStateService WorldState { get; }
        public Transform ActorHoldPoint { get; }
        public Transform DestinationHoldParent { get; }
        public SafeDropZone DropZone { get; }
        public string ProposalId { get; }
        public ResourceReservationService Reservations { get; }

        public string SkillId
        {
            get
            {
                string subjectId = Target.EntityId;
                if (Action.Definition.ActionType == PlannerActionType.PutDown &&
                    Action.Bindings.TryGetValue("item", out string itemId) && !string.IsNullOrWhiteSpace(itemId))
                    subjectId = itemId;
                else if (Action.Definition.ActionType == PlannerActionType.WalkTo &&
                         Action.Bindings.TryGetValue("destination", out string destinationId) &&
                         !string.IsNullOrWhiteSpace(destinationId))
                    subjectId = destinationId;
                else if (Action.Definition.ActionType == PlannerActionType.Open &&
                         Action.Bindings.TryGetValue("door", out string doorId) &&
                         !string.IsNullOrWhiteSpace(doorId))
                    subjectId = doorId;
                return $"{Action.Definition.ActionType.ToString().ToLowerInvariant()}:{subjectId}";
            }
        }

        public bool TryResolveWalkDestination(out WorldEntity destinationEntity, out Vector3 destination)
        {
            destinationEntity = null;
            destination = default;
            if (!Action.Bindings.TryGetValue("destination", out string destinationId)) return false;
            if (destinationId == Bindings.Target.EntityId && Bindings.TargetApproachPosition.HasValue)
            {
                destinationEntity = Bindings.Target;
                destination = Bindings.TargetApproachPosition.Value;
                return true;
            }
            if (destinationId == Bindings.Target.EntityId)
            {
                destinationEntity = Bindings.Target;
                if (Bindings.TargetAnchors?.PickupApproachPoint == null) return false;
                destination = Bindings.TargetAnchors.PickupApproachPoint.position;
            }
            else if (Bindings.Destination != null && destinationId == Bindings.Destination.EntityId)
            {
                destinationEntity = Bindings.Destination;
                if (Bindings.DestinationApproachPosition.HasValue)
                {
                    destination = Bindings.DestinationApproachPosition.Value;
                    return true;
                }
                if (Bindings.DestinationAnchors?.ReceivePoint == null) return false;
                destination = Bindings.DestinationAnchors.ReceivePoint.position;
            }
            else if (DropZone?.Zone != null && destinationId == DropZone.Zone.EntityId)
            {
                destinationEntity = DropZone.Zone;
                if (DropZone.ApproachPoint == null) return false;
                destination = DropZone.ApproachPoint.position;
            }
            else if (EntityRegistry != null && EntityRegistry.TryGetEntity(destinationId, out WorldEntity entity) &&
                     entity.TryGetComponent(out DoorState door) && door.IsConfigured)
            {
                destinationEntity = entity;
                destination = door.ApproachPoint.position;
            }
            return destinationEntity != null;
        }
    }

    public interface IPlanActionRunner
    {
        PlannerActionType ActionType { get; }
        bool IsRunning { get; }
        PlanActionRunResult Start(PlanActionExecutionContext context);
        PlanActionRunResult Tick(float deltaTime);
        PlanActionRunResult Cancel(string failureCode);
    }

    public sealed class WalkToActionRunner : IPlanActionRunner
    {
        private readonly float arrivalTolerance;
        private readonly float timeoutSeconds;
        private PlanActionExecutionContext context;
        private WorldEntity destinationEntity;
        private float elapsed;
        private float originalStoppingDistance;
        private bool adjustedStoppingDistance;

        public WalkToActionRunner(float arrivalTolerance = 0.1f, float timeoutSeconds = 30f)
        {
            this.arrivalTolerance = Mathf.Max(0f, arrivalTolerance);
            this.timeoutSeconds = Mathf.Max(0.1f, timeoutSeconds);
        }

        public PlannerActionType ActionType => PlannerActionType.WalkTo;
        public bool IsRunning { get; private set; }

        public PlanActionRunResult Start(PlanActionExecutionContext executionContext)
        {
            context = executionContext;
            elapsed = 0f;
            if (context?.Agent == null || !context.TryResolveWalkDestination(out destinationEntity, out Vector3 destination))
                return Fail(SkillStatus.Failed, "REFERENCE_MISSING");
            if (!context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return Fail(SkillStatus.Failed, "AGENT_NOT_ON_NAVMESH");

            if ((destinationEntity == context.Bindings.Destination &&
                context.Bindings.DestinationApproachPosition.HasValue) || destinationEntity.TryGetComponent<EmbodiedNPC.Actions.CounterCleaningStation>(out _))
            {
                originalStoppingDistance = context.Agent.stoppingDistance;
                context.Agent.stoppingDistance = Mathf.Min(originalStoppingDistance, 0.05f);
                adjustedStoppingDistance = true;
            }
            context.Agent.isStopped = false;
            IsRunning = context.Agent.SetDestination(destination);
            return IsRunning ? PlanActionRunResult.Running() : Fail(SkillStatus.Failed, "DESTINATION_REJECTED");
        }

        public PlanActionRunResult Tick(float deltaTime)
        {
            if (!IsRunning) return new PlanActionRunResult(PlanActionRunStatus.Idle, default);
            elapsed += Mathf.Max(0f, deltaTime);
            if (elapsed >= timeoutSeconds) return Fail(SkillStatus.TimedOut, "WALK_TIMEOUT");
            NavMeshAgent agent = context.Agent;
            if (!agent.isActiveAndEnabled || !agent.isOnNavMesh) return Fail(SkillStatus.Failed, "AGENT_NOT_ON_NAVMESH");
            if (agent.pathPending) return PlanActionRunResult.Running();
            if (agent.pathStatus == NavMeshPathStatus.PathInvalid) return Fail(SkillStatus.Failed, "PATH_INVALID");
            if (agent.pathStatus == NavMeshPathStatus.PathPartial) return Fail(SkillStatus.Failed, "PATH_PARTIAL");
            if (agent.remainingDistance > agent.stoppingDistance + arrivalTolerance) return PlanActionRunResult.Running();

            StopAgent();
            SkillResult result = Synthetic(SkillStatus.Succeeded, string.Empty);
            return new PlanActionRunResult(PlanActionRunStatus.Succeeded, result);
        }

        public PlanActionRunResult Cancel(string failureCode)
        {
            if (!IsRunning) return new PlanActionRunResult(PlanActionRunStatus.Idle, default);
            return Fail(SkillStatus.Cancelled,
                string.IsNullOrWhiteSpace(failureCode) ? "CANCELLED_BY_REQUEST" : failureCode);
        }

        private PlanActionRunResult Fail(SkillStatus status, string code)
        {
            StopAgent();
            SkillResult result = context?.WorldState != null
                ? context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "WalkTo",
                    context.Actor, destinationEntity, null, status, code)
                : Synthetic(status, code);
            return new PlanActionRunResult(ToRunStatus(status), result);
        }

        private SkillResult Synthetic(SkillStatus status, string code) => new(
            context?.SkillId ?? "walk_to:unknown", context?.TraceId ?? string.Empty, "WalkTo",
            context?.Actor?.EntityId, destinationEntity?.EntityId, null, status, code, 0,
            context?.WorldState?.WorldRevision ?? 0, context?.WorldState?.WorldRevision ?? 0);

        private void StopAgent()
        {
            IsRunning = false;
            if (context?.Agent == null) return;
            if (adjustedStoppingDistance)
            {
                context.Agent.stoppingDistance = originalStoppingDistance;
                adjustedStoppingDistance = false;
            }
            if (!context.Agent.isActiveAndEnabled) return;
            context.Agent.isStopped = true;
            // SetDestination may still be pending while hasPath is false. Clear that
            // request too, otherwise it can materialize after cancellation and keep
            // the next planning snapshot busy even though the actor is stopped.
            if (context.Agent.isOnNavMesh) context.Agent.ResetPath();
        }

        private static PlanActionRunStatus ToRunStatus(SkillStatus status) => status switch
        {
            SkillStatus.Succeeded => PlanActionRunStatus.Succeeded,
            SkillStatus.Cancelled => PlanActionRunStatus.Cancelled,
            SkillStatus.TimedOut => PlanActionRunStatus.TimedOut,
            _ => PlanActionRunStatus.Failed,
        };
    }

    public abstract class ImmediatePlanActionRunner : IPlanActionRunner
    {
        public abstract PlannerActionType ActionType { get; }
        public bool IsRunning => false;

        public PlanActionRunResult Start(PlanActionExecutionContext context)
        {
            SkillResult result = Execute(context);
            PlanActionRunStatus status = result.Status switch
            {
                SkillStatus.Succeeded => PlanActionRunStatus.Succeeded,
                SkillStatus.Cancelled => PlanActionRunStatus.Cancelled,
                SkillStatus.TimedOut => PlanActionRunStatus.TimedOut,
                _ => PlanActionRunStatus.Failed,
            };
            return new PlanActionRunResult(status, result);
        }

        public PlanActionRunResult Tick(float deltaTime) => new(PlanActionRunStatus.Idle, default);
        public PlanActionRunResult Cancel(string failureCode) => new(PlanActionRunStatus.Idle, default);
        protected abstract SkillResult Execute(PlanActionExecutionContext context);
    }

    public sealed class PickUpActionRunner : ImmediatePlanActionRunner
    {
        public override PlannerActionType ActionType => PlannerActionType.PickUp;
        protected override SkillResult Execute(PlanActionExecutionContext context) => context.WorldState.TryPickUp(
            context.SkillId, context.TraceId, context.Bindings.Actor, context.Bindings.Target,
            context.Bindings.TargetHeldState, context.ActorHoldPoint, context.Bindings.TargetAnchors.GrabPoint);
    }

    public sealed class PutDownActionRunner : ImmediatePlanActionRunner
    {
        public override PlannerActionType ActionType => PlannerActionType.PutDown;

        protected override SkillResult Execute(PlanActionExecutionContext context)
        {
            WorldEntity item = null;
            if (!context.Action.Bindings.TryGetValue("item", out string itemId) ||
                context.EntityRegistry == null || !context.EntityRegistry.TryGetEntity(itemId, out item) ||
                !item.TryGetComponent(out HeldItemState held))
                return context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "PutDown",
                    context.Bindings.Actor, item, context.DropZone?.Zone, SkillStatus.Failed, "ACTION_BINDING_STALE");
            return context.WorldState.TryPutDown(context.SkillId, context.TraceId, context.Bindings.Actor,
                item, context.DropZone, held);
        }
    }

    public sealed class GiveActionRunner : ImmediatePlanActionRunner
    {
        public override PlannerActionType ActionType => PlannerActionType.Give;
        protected override SkillResult Execute(PlanActionExecutionContext context) =>
            context.WorldState.TryTransferOwnership(context.SkillId, context.TraceId, context.Bindings.Actor,
                context.Bindings.Target, context.Bindings.Destination, context.Bindings.TargetHeldState,
                context.DestinationHoldParent, context.Bindings.DestinationAnchors.HoldPoint);
    }

    public sealed class OpenActionRunner : ImmediatePlanActionRunner
    {
        public override PlannerActionType ActionType => PlannerActionType.Open;

        protected override SkillResult Execute(PlanActionExecutionContext context)
        {
            WorldEntity doorEntity = null;
            if (!context.Action.Bindings.TryGetValue("door", out string doorId) ||
                context.EntityRegistry == null || !context.EntityRegistry.TryGetEntity(doorId, out doorEntity) ||
                !doorEntity.TryGetComponent(out DoorState door))
                return context.WorldState.RecordAttempt(context.SkillId, context.TraceId, "Open",
                    context.Bindings.Actor, doorEntity, null, SkillStatus.Failed, "ACTION_BINDING_STALE");
            return context.WorldState.TryOpen(context.SkillId, context.TraceId, context.Bindings.Actor, doorEntity, door);
        }
    }

    public sealed class PlanActionRunnerSet
    {
        private readonly Dictionary<PlannerActionType, IPlanActionRunner> runners = new();

        public PlanActionRunnerSet(IEnumerable<IPlanActionRunner> actionRunners)
        {
            foreach (IPlanActionRunner runner in actionRunners ?? Array.Empty<IPlanActionRunner>())
            {
                if (runner == null || runners.ContainsKey(runner.ActionType))
                    throw new ArgumentException("Each supported action type requires exactly one runner.", nameof(actionRunners));
                runners.Add(runner.ActionType, runner);
            }
        }

        public bool TryGet(PlannerActionType actionType, out IPlanActionRunner runner) => runners.TryGetValue(actionType, out runner);
    }
}
