using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    /// <summary>
    /// Read-only, scene-bound execution context produced by GoalValidator.
    /// Runners must consume this context and never resolve entity IDs themselves.
    /// </summary>
    public sealed class ResolvedBringObjectGoal : EmbodiedNPC.Actions.IResolvedActionGoal
    {
        public ResolvedBringObjectGoal(
            WorldEntity actor,
            WorldEntity target,
            WorldEntity destination,
            HeldItemState targetHeldState,
            InteractionAnchorProvider targetAnchors,
            InteractionAnchorProvider destinationAnchors,
            int validatedWorldRevision)
        {
            Actor = actor;
            Target = target;
            Destination = destination;
            TargetHeldState = targetHeldState;
            TargetAnchors = targetAnchors;
            DestinationAnchors = destinationAnchors;
            ValidatedWorldRevision = validatedWorldRevision;
        }

        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public WorldEntity Destination { get; }
        public HeldItemState TargetHeldState { get; }
        public InteractionAnchorProvider TargetAnchors { get; }
        public InteractionAnchorProvider DestinationAnchors { get; }
        public int ValidatedWorldRevision { get; }
        public string ActionId => "bring_object";
        public bool HasProprioceptiveTargetEvidence => TargetHeldState != null && TargetHeldState.IsHeld &&
            TargetHeldState.HolderEntityId == Actor?.EntityId;
    }
}
