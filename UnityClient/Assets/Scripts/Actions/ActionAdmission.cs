using System;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine.AI;

namespace EmbodiedNPC.Actions
{
    /// <summary>Dependencies already owned by GoalValidator; policies may validate but never execute with them.</summary>
    public sealed class ActionAdmissionContext
    {
        public ActionAdmissionContext(EntityRegistry entityRegistry, ResourceReservationService reservations,
            WorldStateService worldState, WorldEntity actor, NavMeshAgent agent)
        {
            EntityRegistry = entityRegistry;
            Reservations = reservations;
            WorldState = worldState;
            Actor = actor;
            Agent = agent;
        }

        public EntityRegistry EntityRegistry { get; }
        public ResourceReservationService Reservations { get; }
        public WorldStateService WorldState { get; }
        public WorldEntity Actor { get; }
        public NavMeshAgent Agent { get; }
    }

    /// <summary>Authoritative entities resolved only from IDs already present in the accepted GoalProposal.</summary>
    public readonly struct ActionBindingResult
    {
        private ActionBindingResult(bool isBound, WorldEntity actor, WorldEntity target, WorldEntity destination,
            GoalValidationFailureCode failureCode, string detail)
        {
            IsBound = isBound;
            Actor = actor;
            Target = target;
            Destination = destination;
            FailureCode = failureCode;
            Detail = detail ?? string.Empty;
        }

        public bool IsBound { get; }
        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public WorldEntity Destination { get; }
        public GoalValidationFailureCode FailureCode { get; }
        public string Detail { get; }

        public static ActionBindingResult Bound(WorldEntity actor, WorldEntity target, WorldEntity destination) =>
            new(true, actor, target, destination, GoalValidationFailureCode.None, string.Empty);

        public static ActionBindingResult Rejected(GoalValidationFailureCode failureCode, string detail) =>
            new(false, null, null, null, failureCode, detail);
    }

    public interface IActionBinder
    {
        ActionBindingResult Bind(GoalProposal proposal, ActionDescriptor descriptor, ActionAdmissionContext context);
    }

    public interface IActionAdmissionPolicy
    {
        GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor,
            IActionBinder binder, ActionAdmissionContext context);
    }

    /// <summary>
    /// Shared stable-ID binder for the current target/destination wire shape. It never enumerates registry entities.
    /// </summary>
    public sealed class EntityTargetBinder : IActionBinder
    {
        public ActionBindingResult Bind(GoalProposal proposal, ActionDescriptor descriptor,
            ActionAdmissionContext context)
        {
            if (proposal == null) throw new ArgumentNullException(nameof(proposal));
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (context == null) throw new ArgumentNullException(nameof(context));

            bool hasTarget = descriptor.BindingSchema.Any(binding =>
                binding.Name == "target" && binding.Kind == ActionBindingKind.EntityRef);
            bool hasDestination = descriptor.BindingSchema.Any(binding =>
                binding.Name == "destination" && binding.Kind == ActionBindingKind.EntityRef);
            bool requiresDestination = descriptor.BindingSchema.Any(binding =>
                binding.Name == "destination" && binding.Kind == ActionBindingKind.EntityRef && binding.Required);
            if (!hasTarget)
                throw new InvalidOperationException($"ACTION_TARGET_BINDING_MISSING:{descriptor.ActionId}");

            if (!string.IsNullOrEmpty(proposal.drink_type) || !ActionContractRules.IsEntityId(proposal.target_entity_id) ||
                requiresDestination && !ActionContractRules.IsEntityId(proposal.destination_entity_id) ||
                !hasDestination && !string.IsNullOrEmpty(proposal.destination_entity_id) ||
                hasDestination && !string.IsNullOrEmpty(proposal.destination_entity_id) &&
                !ActionContractRules.IsEntityId(proposal.destination_entity_id))
            {
                return Reject(GoalValidationFailureCode.InvalidEntityId, "Goal 中的实体 ID 格式无效。");
            }

            EntityRegistry registry = context.EntityRegistry;
            if (registry == null || !registry.IsReady)
                return Reject(GoalValidationFailureCode.EntityRegistryNotReady, "EntityRegistry 尚未准备完成。");
            if (context.Actor == null || string.IsNullOrWhiteSpace(context.Actor.EntityId) ||
                !registry.TryGetEntity(context.Actor.EntityId, out _))
                return Reject(GoalValidationFailureCode.ActorNotRegistered, "执行 NPC 未注册。");
            if (!registry.TryGetEntity(proposal.target_entity_id, out WorldEntity target))
                return Reject(GoalValidationFailureCode.TargetNotFound, "目标实体不存在。");

            WorldEntity destination = null;
            if (!string.IsNullOrEmpty(proposal.destination_entity_id) &&
                !registry.TryGetEntity(proposal.destination_entity_id, out destination))
            {
                return Reject(GoalValidationFailureCode.DestinationNotFound, "交付目标实体不存在。");
            }

            return ActionBindingResult.Bound(context.Actor, target, destination);
        }

        private static ActionBindingResult Reject(GoalValidationFailureCode code, string detail) =>
            ActionBindingResult.Rejected(code, detail);
    }
}
