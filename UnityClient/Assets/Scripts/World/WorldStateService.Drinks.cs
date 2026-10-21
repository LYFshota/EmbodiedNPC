using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;

namespace EmbodiedNPC.World
{
    public readonly struct DrinkReceipt
    {
        internal DrinkReceipt(string id, string actor, string proposal, string trace, string player, string drink, int revision,
            PersistentIdentityKey actorIdentity = default, PersistentIdentityKey playerIdentity = default)
        { Id = id; Actor = actor; Proposal = proposal; Trace = trace; Player = player; DrinkType = drink; WorldRevision = revision;
            ActorIdentity = actorIdentity; PlayerIdentity = playerIdentity; }
        public string Id { get; }
        public string Actor { get; }
        public string Proposal { get; }
        public string Trace { get; }
        public string Player { get; }
        public string DrinkType { get; }
        public int WorldRevision { get; }
        // Identity evidence belongs to the validated world effect, not a later dialogue or entity lookup.
        public PersistentIdentityKey ActorIdentity { get; }
        public PersistentIdentityKey PlayerIdentity { get; }
    }

    public sealed partial class WorldStateService
    {
        // Scene-runtime authority only; revisions/replans never form a new order identity.
        private readonly Dictionary<(string actor, string proposal, string trace), DrinkReceipt> drinkReceipts = new();
        private readonly Dictionary<(string player, string drink), int> playerDrinks = new();
        public int GetDrinkCount(string player, string drink) =>
            playerDrinks.TryGetValue((player, drink), out int value) ? value : 0;
        public bool TryGetDrinkReceipt(string actor, string proposal, string trace, out DrinkReceipt receipt) =>
            drinkReceipts.TryGetValue((actor, proposal, trace), out receipt);

        internal SkillResult TryCommitDrink(string proposal, string trace, EntityRegistry registry,
            WorldEntity actor, WorldEntity station, WorldEntity player, string drink)
        {
            string skill = "prepare_drink:" + proposal;
            if (!isActiveAndEnabled || string.IsNullOrEmpty(proposal) || string.IsNullOrEmpty(trace) || !DrinkTypes.IsValid(drink) ||
                !DrinkTypes.ValidateRuntime(registry, actor, station, player))
                return RecordAttempt(skill, trace, "PrepareDrink", actor, station, player, SkillStatus.Failed, "DRINK_COMMIT_INVALID");
            if (TryGetDrinkReceipt(actor.EntityId, proposal, trace, out var existing))
                return RecordAttempt(skill, trace, "PrepareDrink", actor, station, player,
                    existing.Player == player.EntityId && existing.DrinkType == drink ? SkillStatus.Succeeded : SkillStatus.Failed,
                    existing.Player == player.EntityId && existing.DrinkType == drink ? "DRINK_ALREADY_COMMITTED" : "DRINK_ORDER_CONFLICT");
            var capability = station.GetComponent<DrinkPreparationStation>();
            if(!TaskEffectAllowed(actor.EntityId,proposal,trace,"prepare_drink",station.EntityId,player.EntityId,drink,0))
                return RecordAttempt(skill,trace,"PrepareDrink",actor,station,player,SkillStatus.Failed,"TASK_GUARD_EXPIRED");
            if (UnityEngine.Vector3.Distance(actor.transform.position, capability.ApproachPoint.position) > .85f)
                return RecordAttempt(skill, trace, "PrepareDrink", actor, station, player, SkillStatus.Failed, "ACTION_OUT_OF_RANGE");
            var countKey = (player.EntityId, drink);
            int count = checked(GetDrinkCount(player.EntityId, drink) + 1);
            actor.TryGetPersistentIdentityKey(out var actorIdentity);
            player.TryGetPersistentIdentityKey(out var playerIdentity);
            var receipt = new DrinkReceipt("drink_" + Guid.NewGuid().ToString("N"), actor.EntityId,
                proposal, trace, player.EntityId, drink, worldRevision + 1, actorIdentity, playerIdentity);
            playerDrinks[countKey] = count;
            drinkReceipts.Add((actor.EntityId, proposal, trace), receipt);
            // Receipt and counter are visible before observers see the matching committed SkillResult.
            return Commit(skill, trace, "PrepareDrink", actor, station, player, SkillStatus.Succeeded, "");
        }
    }
}
