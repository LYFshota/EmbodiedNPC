using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Planning.MiniGoap
{
    public readonly struct BringObjectBindings
    {
        public BringObjectBindings(string actorId, string targetId, string destinationId,
            string heldItemId = null, string blockingDoorId = null, string dropZoneId = null)
        {
            ActorId = Require(actorId, nameof(actorId));
            TargetId = Require(targetId, nameof(targetId));
            DestinationId = Require(destinationId, nameof(destinationId));
            HeldItemId = heldItemId ?? string.Empty;
            BlockingDoorId = blockingDoorId ?? string.Empty;
            DropZoneId = dropZoneId ?? string.Empty;
        }

        public string ActorId { get; }
        public string TargetId { get; }
        public string DestinationId { get; }
        public string HeldItemId { get; }
        public string BlockingDoorId { get; }
        public string DropZoneId { get; }

        private static string Require(string value, string name) =>
            !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Entity ID is required.", name);
    }

    public static class PlanningFact
    {
        public const string None = "none";
        public const string Free = "free";
        public const string Current = "current";
        public const string True = "true";
        public const string False = "false";
        public static string Location(string actorId) => $"location:{actorId}";
        public static string Busy(string actorId) => $"busy:{actorId}";
        public static string Holder(string itemId) => $"holder:{itemId}";
        public static string Hand(string actorId) => $"hand:{actorId}";
        public static string Reachable(string entityId) => $"reachable:{entityId}";
        public static string Reserved(string entityId) => $"reserved:{entityId}";
        public static string Open(string entityId) => $"open:{entityId}";
        public static string Locked(string entityId) => $"locked:{entityId}";
        public static string Available(string entityId) => $"available:{entityId}";
        public static string Occupant(string entityId) => $"occupant:{entityId}";
    }

    /// <summary>Creates only action instances bound to the current bring-object goal entities.</summary>
    public static class BringObjectActionCatalog
    {
        private static readonly ActionDefinition WalkTo = new("walk_to", PlannerActionType.WalkTo, 1, "WalkTo");
        private static readonly ActionDefinition PickUp = new("pick_up", PlannerActionType.PickUp, 2, "PickUp");
        private static readonly ActionDefinition Give = new("give", PlannerActionType.Give, 1, "Give");
        private static readonly ActionDefinition PutDown = new("put_down", PlannerActionType.PutDown, 1, "PutDown");
        private static readonly ActionDefinition Open = new("open", PlannerActionType.Open, 1, "Open");

        public static PlanningGoal CreateGoal(string goalId, BringObjectBindings binding) =>
            new(goalId, "bring_object", Facts((PlanningFact.Holder(binding.TargetId), binding.DestinationId)));

        public static IReadOnlyList<ActionInstance> CreateActions(BringObjectBindings binding)
        {
            List<ActionInstance> result = new();

            // Prefer releasing an unrelated held item before travelling to the requested target.
            if (!string.IsNullOrEmpty(binding.HeldItemId) &&
                !string.Equals(binding.HeldItemId, binding.TargetId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(binding.DropZoneId))
            {
                result.Add(CreateWalk(binding.ActorId, binding.DropZoneId));
                result.Add(new ActionInstance(PutDown,
                    Facts(("item", binding.HeldItemId), ("destination", binding.DropZoneId)),
                    Facts((PlanningFact.Location(binding.ActorId), binding.DropZoneId),
                        (PlanningFact.Hand(binding.ActorId), binding.HeldItemId),
                        (PlanningFact.Holder(binding.HeldItemId), binding.ActorId),
                        (PlanningFact.Available(binding.DropZoneId), PlanningFact.True)),
                    Facts((PlanningFact.Hand(binding.ActorId), PlanningFact.Free),
                        (PlanningFact.Holder(binding.HeldItemId), PlanningFact.None),
                        (PlanningFact.Available(binding.DropZoneId), PlanningFact.False),
                        (PlanningFact.Occupant(binding.DropZoneId), binding.HeldItemId))));
            }

            if (!string.IsNullOrEmpty(binding.BlockingDoorId))
            {
                result.Add(CreateWalk(binding.ActorId, binding.BlockingDoorId));
                result.Add(new ActionInstance(Open,
                    Facts(("door", binding.BlockingDoorId), ("unblocks", binding.TargetId)),
                    Facts((PlanningFact.Location(binding.ActorId), binding.BlockingDoorId),
                        (PlanningFact.Open(binding.BlockingDoorId), PlanningFact.False),
                        (PlanningFact.Locked(binding.BlockingDoorId), PlanningFact.False)),
                    Facts((PlanningFact.Open(binding.BlockingDoorId), PlanningFact.True),
                        (PlanningFact.Reachable(binding.TargetId), PlanningFact.True))));
            }

            result.Add(CreateWalk(binding.ActorId, binding.TargetId));
            result.Add(new ActionInstance(PickUp,
                Facts(("target", binding.TargetId)),
                Facts((PlanningFact.Location(binding.ActorId), binding.TargetId),
                    (PlanningFact.Reachable(binding.TargetId), PlanningFact.True),
                    (PlanningFact.Reserved(binding.TargetId), PlanningFact.False),
                    (PlanningFact.Hand(binding.ActorId), PlanningFact.Free),
                    (PlanningFact.Holder(binding.TargetId), PlanningFact.None)),
                Facts((PlanningFact.Hand(binding.ActorId), binding.TargetId),
                    (PlanningFact.Holder(binding.TargetId), binding.ActorId))));
            result.Add(CreateWalk(binding.ActorId, binding.DestinationId));
            result.Add(new ActionInstance(Give,
                Facts(("target", binding.TargetId), ("destination", binding.DestinationId)),
                Facts((PlanningFact.Location(binding.ActorId), binding.DestinationId),
                    (PlanningFact.Reachable(binding.DestinationId), PlanningFact.True),
                    (PlanningFact.Hand(binding.ActorId), binding.TargetId),
                    (PlanningFact.Holder(binding.TargetId), binding.ActorId)),
                Facts((PlanningFact.Hand(binding.ActorId), PlanningFact.Free),
                    (PlanningFact.Holder(binding.TargetId), binding.DestinationId))));
            return result;
        }

        private static ActionInstance CreateWalk(string actorId, string destinationId) =>
            new(WalkTo,
                Facts(("actor", actorId), ("destination", destinationId)),
                Facts((PlanningFact.Busy(actorId), PlanningFact.False),
                    (PlanningFact.Reachable(destinationId), PlanningFact.True)),
                Facts((PlanningFact.Location(actorId), destinationId)));

        private static IEnumerable<KeyValuePair<string, string>> Facts(params (string key, string value)[] facts)
        {
            foreach ((string key, string value) in facts)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }
}
