using EmbodiedNPC.World;

namespace EmbodiedNPC.Planning
{
    /// <summary>Unity-authoritative closed, unlocked door admitted for one open_door goal.</summary>
    public sealed class ResolvedOpenDoorGoal : EmbodiedNPC.Actions.IResolvedActionGoal
    {
        public ResolvedOpenDoorGoal(WorldEntity actor, WorldEntity target, int validatedWorldRevision)
        {
            Actor = actor;
            Target = target;
            ValidatedWorldRevision = validatedWorldRevision;
        }

        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public int ValidatedWorldRevision { get; }
        public string ActionId => "open_door";
        public bool HasProprioceptiveTargetEvidence => false;
    }
}
