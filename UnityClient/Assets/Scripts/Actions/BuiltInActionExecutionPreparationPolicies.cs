using System;
using System.Collections.Generic;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Actions
{
    public sealed class OpenDoorExecutionPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal,
            ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedOpenDoorGoal goal)
                return ActionExecutionPreparationResult.Failed("RESOLVED_GOAL_TYPE_MISMATCH",
                    "open_door requires ResolvedOpenDoorGoal.");
            if (context?.EntityRegistry == null || context.WorldState == null || context.Agent == null ||
                context.ApproachResolver == null ||
                goal.Actor == null || goal.Target == null || !goal.Actor.isActiveAndEnabled ||
                !goal.Target.isActiveAndEnabled || !goal.Target.gameObject.activeInHierarchy ||
                !context.EntityRegistry.TryGetEntity(goal.Target.EntityId, out WorldEntity currentTarget) ||
                currentTarget != goal.Target ||
                !currentTarget.TryGetComponent(out EmbodiedNPC.Interaction.DoorState door) ||
                !door.isActiveAndEnabled || !door.IsConfigured || door.Door != currentTarget)
                return ActionExecutionPreparationResult.Failed("ACTION_BINDING_STALE",
                    "Door binding or authoritative state is unavailable.");
            if (door.IsOpen)
                return ActionExecutionPreparationResult.Failed("DOOR_ALREADY_OPEN", "Door is already open.");
            if (door.IsLocked)
                return ActionExecutionPreparationResult.Failed("DOOR_LOCKED", "Door is locked.");
            if (!context.ApproachResolver.TryResolve(context.Agent, currentTarget, out UnityEngine.Vector3 position))
                return ActionExecutionPreparationResult.Failed("TARGET_UNREACHABLE",
                    "Unable to resolve a current authoritative door approach position.");

            string actorId = goal.Actor.EntityId;
            string targetId = goal.Target.EntityId;
            PlannerSnapshot snapshot = new(context.SnapshotId, context.GoalId,
                context.WorldState.WorldRevision, context.PerceptionRevision, new PlanningState(Facts(
                    (PlanningFact.Location(actorId), PlanningFact.Current),
                    (PlanningFact.Busy(actorId), PlanningFact.False),
                    (PlanningFact.Reachable(targetId), PlanningFact.True),
                    (PlanningFact.Open(targetId), PlanningFact.False),
                    (PlanningFact.Locked(targetId), PlanningFact.False))));
            OpenDoorPlanningInput input = new(snapshot, actorId, targetId);
            ValidatedActionBindings bindings = new(goal.Actor, goal.Target,
                targetApproachPosition: position, requiresTargetPerception: true);
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(
                input, bindings, ActionLeaseRequirement.None, refreshBeforeActionValidation: true,
                completionFacingTarget: goal.Target));
        }

        private static IEnumerable<KeyValuePair<string, string>> Facts(
            params (string key, string value)[] facts)
        {
            foreach ((string key, string value) in facts)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    public sealed class BringObjectExecutionPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal,
            ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedBringObjectGoal goal)
                return ActionExecutionPreparationResult.Failed("RESOLVED_GOAL_TYPE_MISMATCH",
                    "bring_object requires ResolvedBringObjectGoal.");
            if (context?.SnapshotBuilder == null)
                return ActionExecutionPreparationResult.Failed("REFERENCE_MISSING",
                    "PlannerSnapshotBuilder is unavailable.");

            if (context.Agent == null || context.ApproachResolver == null ||
                !context.ApproachResolver.TryResolveDeliveryApproach(
                    context.Agent, goal.Destination, out UnityEngine.Vector3 destinationApproach))
                return ActionExecutionPreparationResult.Failed("DESTINATION_UNREACHABLE",
                    "Unable to resolve a current collision-safe delivery approach position.");

            PlannerSnapshotBuildResult build = context.SnapshotBuilder.Build(context.SnapshotId,
                context.GoalId, context.PerceptionRevision, goal, context.ConfirmedTargetApproachPosition,
                destinationApproach);
            if (!build.IsSuccess)
                return ActionExecutionPreparationResult.Failed(
                    $"SNAPSHOT_{build.FailureCode.ToString().ToUpperInvariant()}", build.Detail);

            BringObjectBindings planningBindings = build.Input.Bindings;
            ValidatedActionBindings bindings = new(goal.Actor, goal.Target, goal.Destination,
                goal.TargetHeldState, goal.TargetAnchors, goal.DestinationAnchors,
                heldItemId: planningBindings.HeldItemId,
                blockingDoorId: planningBindings.BlockingDoorId,
                dropZoneId: planningBindings.DropZoneId,
                requiresTargetPerception: true,
                destinationApproachPosition: destinationApproach);
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(
                build.Input, bindings, ActionLeaseRequirement.Exclusive(goal.Target),
                refreshBeforeActionValidation: true));
        }
    }

    public sealed class MoveToEntityExecutionPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal,
            ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedMoveToEntityGoal goal)
                return ActionExecutionPreparationResult.Failed("RESOLVED_GOAL_TYPE_MISMATCH",
                    "move_to_entity requires ResolvedMoveToEntityGoal.");
            if (context?.EntityRegistry == null || context.WorldState == null || context.Agent == null ||
                context.ApproachResolver == null ||
                !context.EntityRegistry.TryGetEntity(goal.Target.EntityId, out WorldEntity currentTarget) ||
                currentTarget != goal.Target ||
                !context.ApproachResolver.TryResolve(context.Agent, currentTarget, out UnityEngine.Vector3 position))
                return ActionExecutionPreparationResult.Failed("TARGET_UNREACHABLE",
                    "Unable to resolve a current authoritative approach position.");

            string actorId = goal.Actor.EntityId;
            string targetId = goal.Target.EntityId;
            PlannerSnapshot snapshot = new(context.SnapshotId, context.GoalId,
                context.WorldState.WorldRevision, context.PerceptionRevision, new PlanningState(Facts(
                    (PlanningFact.Location(actorId), PlanningFact.Current),
                    (PlanningFact.Busy(actorId), PlanningFact.False),
                    (PlanningFact.Reachable(targetId), PlanningFact.True))));
            MoveToEntityPlanningInput input = new(snapshot, actorId, targetId);
            ValidatedActionBindings bindings = new(goal.Actor, goal.Target,
                targetApproachPosition: position, requiresTargetPerception: false);
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(
                input, bindings, ActionLeaseRequirement.None, refreshBeforeActionValidation: false,
                completionFacingTarget: goal.Target));
        }

        private static IEnumerable<KeyValuePair<string, string>> Facts(
            params (string key, string value)[] facts)
        {
            foreach ((string key, string value) in facts)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }
}
