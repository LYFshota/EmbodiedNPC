using System;
using System.Collections.Generic;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;

namespace EmbodiedNPC.Actions
{
    public sealed class OpenDoorPlanningInput : IActionPlanningInput
    {
        public OpenDoorPlanningInput(PlannerSnapshot snapshot, string actorId, string targetId)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            ActorId = Require(actorId, nameof(actorId));
            TargetId = Require(targetId, nameof(targetId));
        }

        public string ActionId => "open_door";
        public PlannerSnapshot Snapshot { get; }
        public string ActorId { get; }
        public string TargetId { get; }

        private static string Require(string value, string name) =>
            !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Entity ID is required.", name);
    }

    public sealed class OpenDoorPlanFactory : IActionPlanFactory
    {
        private static readonly ActionDefinition WalkTo =
            new("open_door.walk", PlannerActionType.WalkTo, 1, "WalkTo");
        private static readonly ActionDefinition Open =
            new("open_door.open", PlannerActionType.Open, 1, "Open");

        public PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input)
        {
            if (planner == null) throw new ArgumentNullException(nameof(planner));
            if (input is not OpenDoorPlanningInput openInput)
                throw new ArgumentException("open_door requires OpenDoorPlanningInput.", nameof(input));

            PlanningGoal goal = new(goalId, openInput.ActionId,
                Facts((PlanningFact.Open(openInput.TargetId), PlanningFact.True)));
            ActionInstance walk = new(WalkTo,
                Facts(("actor", openInput.ActorId), ("destination", openInput.TargetId)),
                Facts((PlanningFact.Busy(openInput.ActorId), PlanningFact.False),
                    (PlanningFact.Reachable(openInput.TargetId), PlanningFact.True)),
                Facts((PlanningFact.Location(openInput.ActorId), openInput.TargetId)));
            ActionInstance open = new(Open,
                Facts(("actor", openInput.ActorId), ("door", openInput.TargetId)),
                Facts((PlanningFact.Location(openInput.ActorId), openInput.TargetId),
                    (PlanningFact.Open(openInput.TargetId), PlanningFact.False),
                    (PlanningFact.Locked(openInput.TargetId), PlanningFact.False)),
                Facts((PlanningFact.Open(openInput.TargetId), PlanningFact.True)));
            return planner.Plan(openInput.Snapshot, goal, new[] { walk, open });
        }

        private static IEnumerable<KeyValuePair<string, string>> Facts(
            params (string key, string value)[] facts)
        {
            foreach ((string key, string value) in facts)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    public sealed class BringObjectPlanFactory : IActionPlanFactory
    {
        public PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input)
        {
            if (planner == null) throw new ArgumentNullException(nameof(planner));
            if (input is not BringObjectPlanningInput bringInput)
                throw new ArgumentException("bring_object requires BringObjectPlanningInput.", nameof(input));

            PlanningGoal goal = BringObjectActionCatalog.CreateGoal(goalId, bringInput.Bindings);
            return planner.Plan(bringInput.Snapshot, goal,
                BringObjectActionCatalog.CreateActions(bringInput.Bindings));
        }
    }

    public sealed class MoveToEntityPlanningInput : IActionPlanningInput
    {
        public MoveToEntityPlanningInput(PlannerSnapshot snapshot, string actorId, string targetId)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            ActorId = Require(actorId, nameof(actorId));
            TargetId = Require(targetId, nameof(targetId));
        }

        public string ActionId => "move_to_entity";
        public PlannerSnapshot Snapshot { get; }
        public string ActorId { get; }
        public string TargetId { get; }

        private static string Require(string value, string name) =>
            !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Entity ID is required.", name);
    }

    public sealed class MoveToEntityPlanFactory : IActionPlanFactory
    {
        private static readonly ActionDefinition WalkTo =
            new("move_to_entity.walk", PlannerActionType.WalkTo, 1, "WalkTo");

        public PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input)
        {
            if (planner == null) throw new ArgumentNullException(nameof(planner));
            if (input is not MoveToEntityPlanningInput moveInput)
                throw new ArgumentException("move_to_entity requires MoveToEntityPlanningInput.", nameof(input));

            PlanningGoal goal = new(goalId, moveInput.ActionId,
                Facts((PlanningFact.Location(moveInput.ActorId), moveInput.TargetId)));
            ActionInstance walk = new(WalkTo,
                Facts(("actor", moveInput.ActorId), ("destination", moveInput.TargetId)),
                Facts((PlanningFact.Busy(moveInput.ActorId), PlanningFact.False),
                    (PlanningFact.Reachable(moveInput.TargetId), PlanningFact.True)),
                Facts((PlanningFact.Location(moveInput.ActorId), moveInput.TargetId)));
            return planner.Plan(moveInput.Snapshot, goal, new[] { walk });
        }

        private static IEnumerable<KeyValuePair<string, string>> Facts(
            params (string key, string value)[] facts)
        {
            foreach ((string key, string value) in facts)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }
}
