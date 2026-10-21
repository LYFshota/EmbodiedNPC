using System;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Actions
{
    /// <summary>Unity-internal admitted goal boundary. It is never serialized onto the wire.</summary>
    public interface IResolvedActionGoal
    {
        string ActionId { get; }
        WorldEntity Actor { get; }
        WorldEntity Target { get; }
        int ValidatedWorldRevision { get; }
        bool HasProprioceptiveTargetEvidence { get; }
    }

    /// <summary>Typed entity/component bindings consumed by the existing atomic runners and validator.</summary>
    public sealed class ValidatedActionBindings
    {
        public ValidatedActionBindings(WorldEntity actor, WorldEntity target, WorldEntity destination = null,
            HeldItemState targetHeldState = null, InteractionAnchorProvider targetAnchors = null,
            InteractionAnchorProvider destinationAnchors = null, Vector3? targetApproachPosition = null,
            string heldItemId = "", string blockingDoorId = "", string dropZoneId = "",
            bool requiresTargetPerception = false, Vector3? destinationApproachPosition = null)
        {
            Actor = actor ?? throw new ArgumentNullException(nameof(actor));
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Destination = destination;
            TargetHeldState = targetHeldState;
            TargetAnchors = targetAnchors;
            DestinationAnchors = destinationAnchors;
            TargetApproachPosition = targetApproachPosition;
            DestinationApproachPosition = destinationApproachPosition;
            HeldItemId = heldItemId ?? string.Empty;
            BlockingDoorId = blockingDoorId ?? string.Empty;
            DropZoneId = dropZoneId ?? string.Empty;
            RequiresTargetPerception = requiresTargetPerception;
        }

        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public WorldEntity Destination { get; }
        public HeldItemState TargetHeldState { get; }
        public InteractionAnchorProvider TargetAnchors { get; }
        public InteractionAnchorProvider DestinationAnchors { get; }
        public Vector3? TargetApproachPosition { get; }
        public Vector3? DestinationApproachPosition { get; }
        public string HeldItemId { get; }
        public string BlockingDoorId { get; }
        public string DropZoneId { get; }
        public bool RequiresTargetPerception { get; }

        public ValidatedActionBindings WithTargetApproachPosition(Vector3 position) => new(
            Actor, Target, Destination, TargetHeldState, TargetAnchors, DestinationAnchors, position,
            HeldItemId, BlockingDoorId, DropZoneId, RequiresTargetPerception, DestinationApproachPosition);

        public ValidatedActionBindings WithDestinationApproachPosition(Vector3 position) => new(
            Actor, Target, Destination, TargetHeldState, TargetAnchors, DestinationAnchors,
            TargetApproachPosition, HeldItemId, BlockingDoorId, DropZoneId, RequiresTargetPerception, position);
    }

    public readonly struct ActionLeaseRequirement
    {
        private ActionLeaseRequirement(WorldEntity target) => Target = target;
        public WorldEntity Target { get; }
        public bool IsRequired => Target != null;
        public static ActionLeaseRequirement None => default;
        public static ActionLeaseRequirement Exclusive(WorldEntity target) =>
            new(target ?? throw new ArgumentNullException(nameof(target)));
    }

    public sealed class ActionExecutionPreparation
    {
        public ActionExecutionPreparation(IActionPlanningInput planningInput, ValidatedActionBindings bindings,
            ActionLeaseRequirement leaseRequirement, bool refreshBeforeActionValidation,
            WorldEntity completionFacingTarget = null)
        {
            PlanningInput = planningInput ?? throw new ArgumentNullException(nameof(planningInput));
            Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            LeaseRequirement = leaseRequirement;
            RefreshBeforeActionValidation = refreshBeforeActionValidation;
            CompletionFacingTarget = completionFacingTarget;
        }

        public IActionPlanningInput PlanningInput { get; }
        public ValidatedActionBindings Bindings { get; }
        public ActionLeaseRequirement LeaseRequirement { get; }
        public bool RefreshBeforeActionValidation { get; }
        public WorldEntity CompletionFacingTarget { get; }

        public ActionExecutionPreparation WithBindings(ValidatedActionBindings bindings) => new(
            PlanningInput, bindings, LeaseRequirement, RefreshBeforeActionValidation, CompletionFacingTarget);
    }

    public readonly struct ActionExecutionPreparationResult
    {
        private ActionExecutionPreparationResult(ActionExecutionPreparation preparation,
            string failureCode, string detail)
        {
            Preparation = preparation;
            FailureCode = failureCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public bool IsSuccess => Preparation != null;
        public ActionExecutionPreparation Preparation { get; }
        public string FailureCode { get; }
        public string Detail { get; }
        public static ActionExecutionPreparationResult Succeeded(ActionExecutionPreparation preparation) =>
            new(preparation ?? throw new ArgumentNullException(nameof(preparation)), string.Empty, string.Empty);
        public static ActionExecutionPreparationResult Failed(string failureCode, string detail) =>
            new(null, string.IsNullOrWhiteSpace(failureCode) ? "EXECUTION_PREPARATION_FAILED" : failureCode, detail);
    }

    /// <summary>Read-only services needed to project an admitted goal into the existing planner input.</summary>
    public sealed class ActionExecutionPreparationContext
    {
        public ActionExecutionPreparationContext(string snapshotId, string goalId, int perceptionRevision,
            Vector3? confirmedTargetApproachPosition, PlannerSnapshotBuilder snapshotBuilder,
            EntityRegistry entityRegistry, WorldStateService worldState, NavMeshAgent agent,
            NavigationApproachResolver approachResolver)
        {
            SnapshotId = snapshotId ?? string.Empty;
            GoalId = goalId ?? string.Empty;
            PerceptionRevision = perceptionRevision;
            ConfirmedTargetApproachPosition = confirmedTargetApproachPosition;
            SnapshotBuilder = snapshotBuilder;
            EntityRegistry = entityRegistry;
            WorldState = worldState;
            Agent = agent;
            ApproachResolver = approachResolver;
        }

        public string SnapshotId { get; }
        public string GoalId { get; }
        public int PerceptionRevision { get; }
        public Vector3? ConfirmedTargetApproachPosition { get; }
        public PlannerSnapshotBuilder SnapshotBuilder { get; }
        public EntityRegistry EntityRegistry { get; }
        public WorldStateService WorldState { get; }
        public NavMeshAgent Agent { get; }
        public NavigationApproachResolver ApproachResolver { get; }
    }

    public interface IActionExecutionPreparationPolicy
    {
        ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal,
            ActionExecutionPreparationContext context);
    }
}
