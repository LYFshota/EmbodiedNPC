using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Planning
{
    /// <summary>Unity-authoritative target and reachable approach point for one move_to_entity goal.</summary>
    public sealed class ResolvedMoveToEntityGoal : EmbodiedNPC.Actions.IResolvedActionGoal
    {
        public ResolvedMoveToEntityGoal(WorldEntity actor, WorldEntity target, Vector3 approachPosition,
            int validatedWorldRevision)
        {
            Actor = actor;
            Target = target;
            ApproachPosition = approachPosition;
            ValidatedWorldRevision = validatedWorldRevision;
        }

        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public Vector3 ApproachPosition { get; }
        public int ValidatedWorldRevision { get; }
        public string ActionId => "move_to_entity";
        public bool HasProprioceptiveTargetEvidence => false;

        public ResolvedMoveToEntityGoal WithApproachPosition(Vector3 position) =>
            new(Actor, Target, position, ValidatedWorldRevision);
    }
}
