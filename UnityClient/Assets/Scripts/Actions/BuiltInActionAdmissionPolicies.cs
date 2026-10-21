using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Actions
{
    public sealed class OpenDoorAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor,
            IActionBinder binder, ActionAdmissionContext context)
        {
            if (context.EntityRegistry == null || !context.EntityRegistry.IsReady)
                return Reject(GoalValidationFailureCode.EntityRegistryNotReady, "EntityRegistry 尚未准备完成。");
            if (context.WorldState == null)
                return Reject(GoalValidationFailureCode.WorldStateServiceNotReady, "WorldStateService 尚未绑定。");

            ActionBindingResult binding = binder.Bind(proposal, descriptor, context);
            if (!binding.IsBound) return Reject(binding.FailureCode, binding.Detail);
            if (!binding.Target.isActiveAndEnabled || !binding.Target.gameObject.activeInHierarchy ||
                binding.Target.EntityKind != EntityKind.Environment ||
                !binding.Target.TryGetComponent(out DoorState door))
                return Reject(GoalValidationFailureCode.TargetNotDoor, "目标不是可打开的门。");
            if (!door.isActiveAndEnabled || !door.IsConfigured ||
                door.Door != binding.Target || door.ApproachPoint == null)
                return Reject(GoalValidationFailureCode.TargetInteractionPointMissing,
                    "门缺少有效 DoorState 或 ApproachPoint。");
            if (door.IsOpen)
                return Reject(GoalValidationFailureCode.TargetAlreadySatisfied, "门已经打开。");
            if (door.IsLocked)
                return Reject(GoalValidationFailureCode.TargetLocked, "门当前已上锁。");
            if (context.Agent == null || !context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return Reject(GoalValidationFailureCode.ActorNotOnNavMesh, "NPC 当前不在有效 NavMesh 上。");

            return GoalValidationResult.Approved(new ResolvedOpenDoorGoal(
                binding.Actor, binding.Target, context.WorldState.WorldRevision));
        }

        private static GoalValidationResult Reject(GoalValidationFailureCode code, string detail) =>
            GoalValidationResult.Rejected(code, detail);
    }

    public sealed class MoveToEntityAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor,
            IActionBinder binder, ActionAdmissionContext context)
        {
            if (context.EntityRegistry == null || !context.EntityRegistry.IsReady)
                return Reject(GoalValidationFailureCode.EntityRegistryNotReady, "EntityRegistry 尚未准备完成。");
            if (context.WorldState == null)
                return Reject(GoalValidationFailureCode.WorldStateServiceNotReady, "WorldStateService 尚未绑定。");

            ActionBindingResult binding = binder.Bind(proposal, descriptor, context);
            if (!binding.IsBound) return Reject(binding.FailureCode, binding.Detail);
            if (context.Agent == null || !context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return Reject(GoalValidationFailureCode.ActorNotOnNavMesh, "NPC 当前不在有效 NavMesh 上。");

            return GoalValidationResult.Approved(new ResolvedMoveToEntityGoal(
                binding.Actor, binding.Target, default, context.WorldState.WorldRevision));
        }

        private static GoalValidationResult Reject(GoalValidationFailureCode code, string detail) =>
            GoalValidationResult.Rejected(code, detail);
    }

    public sealed class BringObjectAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor,
            IActionBinder binder, ActionAdmissionContext context)
        {
            if (context.EntityRegistry == null || !context.EntityRegistry.IsReady)
                return Reject(GoalValidationFailureCode.EntityRegistryNotReady, "EntityRegistry 尚未准备完成。");
            if (context.Reservations == null)
                return Reject(GoalValidationFailureCode.ReservationServiceNotReady,
                    "ResourceReservationService 尚未绑定。");
            if (context.WorldState == null)
                return Reject(GoalValidationFailureCode.WorldStateServiceNotReady, "WorldStateService 尚未绑定。");

            ActionBindingResult binding = binder.Bind(proposal, descriptor, context);
            if (!binding.IsBound) return Reject(binding.FailureCode, binding.Detail);
            if (binding.Target.EntityKind != EntityKind.Object)
                return Reject(GoalValidationFailureCode.TargetNotItem, "目标不是可交付物品。");
            if (binding.Destination == null || !binding.Destination.IsAgent(AgentRole.Player))
                return Reject(GoalValidationFailureCode.DestinationNotPlayer, "交付目标不是玩家实体。");
            if (!binding.Target.TryGetComponent(out HeldItemState targetHeldState))
                return Reject(GoalValidationFailureCode.TargetNotPickupable, "目标缺少 HeldItemState，不能拾取。");
            if (!binding.Target.TryGetComponent(out InteractableEntity targetInteractable) ||
                !targetInteractable.SupportsPickUp)
                return Reject(GoalValidationFailureCode.TargetActionUnavailable, "目标未声明 PickUp 能力。");
            if (!binding.Destination.TryGetComponent(out InteractableEntity destinationInteractable) ||
                !destinationInteractable.SupportsGive)
                return Reject(GoalValidationFailureCode.DestinationActionUnavailable, "交付目标未声明 Give 能力。");
            if (!binding.Target.TryGetComponent(out InteractionAnchorProvider targetAnchors) ||
                !targetAnchors.HasPickupAnchors)
                return Reject(GoalValidationFailureCode.TargetInteractionPointMissing, "目标缺少 Pickup 交互点。");
            if (!binding.Destination.TryGetComponent(out InteractionAnchorProvider destinationAnchors) ||
                !destinationAnchors.HasGiveAnchors)
                return Reject(GoalValidationFailureCode.DestinationInteractionPointMissing,
                    "交付目标缺少 Receive 交互点。");

            return GoalValidationResult.Approved(new ResolvedBringObjectGoal(
                binding.Actor, binding.Target, binding.Destination, targetHeldState,
                targetAnchors, destinationAnchors, context.WorldState.WorldRevision));
        }

        private static GoalValidationResult Reject(GoalValidationFailureCode code, string detail) =>
            GoalValidationResult.Rejected(code, detail);
    }
}
