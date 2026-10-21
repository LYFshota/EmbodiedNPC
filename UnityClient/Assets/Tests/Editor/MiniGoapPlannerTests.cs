using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Planning.MiniGoap;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class MiniGoapPlannerTests
    {
        [Test]
        public void PlanningState_CopiesInputAndHasStableOrderIndependentIdentity()
        {
            Dictionary<string, string> source = new() { ["b"] = "2", ["a"] = "1" };
            PlanningState state = new(source);
            source["a"] = "changed";
            PlanningState reordered = State(("a", "1"), ("b", "2"));

            Assert.That(state.TryGetValue("a", out string value), Is.True);
            Assert.That(value, Is.EqualTo("1"));
            Assert.That(state, Is.EqualTo(reordered));
            Assert.That(state.GetHashCode(), Is.EqualTo(reordered.GetHashCode()));
            Assert.That(state.CanonicalValue, Is.EqualTo("a=1;b=2;"));
        }

        [Test]
        public void ActionInstance_BindsConcreteEntitiesAndDoesNotMutateInputState()
        {
            BringObjectBindings binding = new("npc_01", "cup_01", "player_01");
            ActionInstance pickup = BringObjectActionCatalog.CreateActions(binding)
                .Single(action => action.Definition.ActionType == PlannerActionType.PickUp);
            PlanningState before = State(
                (PlanningFact.Location("npc_01"), "cup_01"),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None));

            PlanningState after = pickup.Apply(before);

            Assert.That(pickup.Bindings["target"], Is.EqualTo("cup_01"));
            Assert.That(before.TryGetValue(PlanningFact.Holder("cup_01"), out string oldHolder), Is.True);
            Assert.That(oldHolder, Is.EqualTo(PlanningFact.None));
            Assert.That(after.TryGetValue(PlanningFact.Holder("cup_01"), out string newHolder), Is.True);
            Assert.That(newHolder, Is.EqualTo("npc_01"));
        }

        [Test]
        public void EmptyHand_ProducesWalkPickupWalkGivePlan()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            AssertPlan(plan, PlannerActionType.WalkTo, PlannerActionType.PickUp,
                PlannerActionType.WalkTo, PlannerActionType.Give);
            Assert.That(plan.TotalCost, Is.EqualTo(5));
            Assert.That(plan.PlannedFromWorldRevision, Is.EqualTo(12));
            Assert.That(plan.PlannedFromPerceptionRevision, Is.EqualTo(7));
        }

        [Test]
        public void AlreadyHoldingTarget_ProducesOnlyWalkAndGive()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01", "cup_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), "cup_01"),
                (PlanningFact.Holder("cup_01"), "npc_01"),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            AssertPlan(plan, PlannerActionType.WalkTo, PlannerActionType.Give);
        }

        [Test]
        public void HandOccupied_ProducesPutDownBeforeBringSequence()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01", "book_01",
                dropZoneId: "safe_drop_zone_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), "book_01"),
                (PlanningFact.Holder("book_01"), "npc_01"),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Available("safe_drop_zone_01"), PlanningFact.True),
                (PlanningFact.Occupant("safe_drop_zone_01"), PlanningFact.None),
                (PlanningFact.Reachable("safe_drop_zone_01"), PlanningFact.True),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            AssertPlan(plan, PlannerActionType.WalkTo, PlannerActionType.PutDown, PlannerActionType.WalkTo,
                PlannerActionType.PickUp, PlannerActionType.WalkTo, PlannerActionType.Give);
            Assert.That(plan.TotalCost, Is.EqualTo(7));
            Assert.That(plan.Actions[0].Bindings["destination"], Is.EqualTo("safe_drop_zone_01"));
            Assert.That(plan.Actions[1].Bindings["destination"], Is.EqualTo("safe_drop_zone_01"));
        }

        [Test]
        public void HandOccupiedWithoutDropZone_ReturnsNoPlan()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01", "book_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), "book_01"),
                (PlanningFact.Holder("book_01"), "npc_01"),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.NoPlan));
        }

        [Test]
        public void ClosedDoor_ProducesOpenBeforeTargetTraversal()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01",
                blockingDoorId: "door_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("door_01"), PlanningFact.True),
                (PlanningFact.Open("door_01"), PlanningFact.False),
                (PlanningFact.Locked("door_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            AssertPlan(plan, PlannerActionType.WalkTo, PlannerActionType.Open,
                PlannerActionType.WalkTo, PlannerActionType.PickUp,
                PlannerActionType.WalkTo, PlannerActionType.Give);
            Assert.That(plan.Actions[0].Bindings["destination"], Is.EqualTo("door_01"));
        }

        [Test]
        public void AlreadySatisfied_ReturnsEmptySuccessfulPlan()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01"), State(
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), "player_01")));

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.AlreadySatisfied));
            Assert.That(plan.Actions, Is.Empty);
            Assert.That(plan.TotalCost, Is.Zero);
            Assert.That(plan.ExpandedNodes, Is.Zero);
        }

        [Test]
        public void MissingReachability_ReturnsNoPlan()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01"), State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.NoPlan));
            Assert.That(plan.IsSuccess, Is.False);
        }

        [Test]
        public void BusyActor_ReturnsNoPlanInsteadOfStartingAnotherNavigation()
        {
            PlanResult plan = Plan(new BringObjectBindings("npc_01", "cup_01", "player_01"), State(
                (PlanningFact.Location("npc_01"), PlanningFact.Current),
                (PlanningFact.Busy("npc_01"), PlanningFact.True),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.NoPlan));
        }

        [Test]
        public void SearchLimit_IsReportedInsteadOfRunningUnbounded()
        {
            BringObjectBindings binding = new("npc_01", "cup_01", "player_01");
            PlanningGoal goal = BringObjectActionCatalog.CreateGoal("goal_limit", binding);
            PlannerSnapshot snapshot = new("snapshot_limit", goal.GoalId, 0, 0, State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));

            PlanResult plan = new UniformCostPlanner(maxExpandedNodes: 1).Plan(snapshot, goal,
                BringObjectActionCatalog.CreateActions(binding));

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.SearchLimitExceeded));
            Assert.That(plan.ExpandedNodes, Is.EqualTo(1));
        }

        [Test]
        public void UniformCostSearch_SelectsCheaperMultiStepRoute()
        {
            PlanningGoal goal = new("goal_cost", "test", Pairs(("done", PlanningFact.True)));
            PlannerSnapshot snapshot = new("snapshot_cost", goal.GoalId, 0, 0,
                State(("mid", PlanningFact.False), ("done", PlanningFact.False)));
            ActionInstance expensive = Action("expensive", 5,
                Array.Empty<(string, string)>(), (("done", PlanningFact.True)));
            ActionInstance firstStep = Action("first", 1,
                Array.Empty<(string, string)>(), (("mid", PlanningFact.True)));
            ActionInstance secondStep = Action("second", 1,
                new[] { ("mid", PlanningFact.True) }, (("done", PlanningFact.True)));

            PlanResult plan = new UniformCostPlanner().Plan(snapshot, goal,
                new[] { expensive, firstStep, secondStep });

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.Found));
            Assert.That(plan.Actions.Select(action => action.Definition.DefinitionId),
                Is.EqualTo(new[] { "first", "second" }));
            Assert.That(plan.TotalCost, Is.EqualTo(2));
        }

        [Test]
        public void PlanLengthLimit_ReturnsNoPlanForLongerRoute()
        {
            PlanningGoal goal = new("goal_depth", "test", Pairs(("done", PlanningFact.True)));
            PlannerSnapshot snapshot = new("snapshot_depth", goal.GoalId, 0, 0,
                State(("mid", PlanningFact.False), ("done", PlanningFact.False)));
            ActionInstance firstStep = Action("first", 1,
                Array.Empty<(string, string)>(), (("mid", PlanningFact.True)));
            ActionInstance secondStep = Action("second", 1,
                new[] { ("mid", PlanningFact.True) }, (("done", PlanningFact.True)));

            PlanResult plan = new UniformCostPlanner(maxPlanLength: 1).Plan(snapshot, goal,
                new[] { firstStep, secondStep });

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.NoPlan));
        }

        [Test]
        public void PlanHash_IsStableAcrossEquivalentBindingOrdering()
        {
            PlanningGoal goal = new("goal_hash", "test", Pairs(("done", PlanningFact.True)));
            ActionDefinition definition = new("ordered", PlannerActionType.Open, 1, "Open");
            ActionInstance first = new(definition,
                Pairs(("b", "2"), ("a", "1")),
                Array.Empty<KeyValuePair<string, string>>(), Pairs(("done", PlanningFact.True)));
            ActionInstance second = new(definition,
                Pairs(("a", "1"), ("b", "2")),
                Array.Empty<KeyValuePair<string, string>>(), Pairs(("done", PlanningFact.True)));

            string firstHash = PlanHasher.Compute(goal, new[] { first });
            string secondHash = PlanHasher.Compute(goal, new[] { second });

            Assert.That(firstHash, Is.EqualTo(secondHash));
            Assert.That(firstHash, Has.Length.EqualTo(64));
        }

        [Test]
        public void NegativeActionCost_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ActionDefinition("invalid", PlannerActionType.Open, -1, "Open"));
        }

        private static PlanResult Plan(BringObjectBindings binding, PlanningState state)
        {
            PlanningGoal goal = BringObjectActionCatalog.CreateGoal("goal_0018", binding);
            PlannerSnapshot snapshot = new("facts_0081", goal.GoalId, 12, 7, state);
            return new UniformCostPlanner().Plan(snapshot, goal, BringObjectActionCatalog.CreateActions(binding));
        }

        private static PlanningState State(params (string key, string value)[] facts)
        {
            return new PlanningState(Pairs(facts));
        }

        private static ActionInstance Action(string id, int cost,
            (string key, string value)[] preconditions, params (string key, string value)[] effects) =>
            new(new ActionDefinition(id, PlannerActionType.Open, cost, id),
                Array.Empty<KeyValuePair<string, string>>(), Pairs(preconditions), Pairs(effects));

        private static IEnumerable<KeyValuePair<string, string>> Pairs(params (string key, string value)[] facts) =>
            facts.Select(fact => new KeyValuePair<string, string>(fact.key, fact.value));

        private static void AssertPlan(PlanResult plan, params PlannerActionType[] expected)
        {
            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.Found));
            Assert.That(plan.Actions.Select(action => action.Definition.ActionType), Is.EqualTo(expected));
            Assert.That(plan.PlanHash, Is.Not.Empty);
            Assert.That(plan.ExpandedNodes, Is.GreaterThan(0));
        }
    }
}
