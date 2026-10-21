using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionPlanFactoryTests
    {
        [Test]
        public void BuiltInRegistrations_ComposeSpecificPlanFactories()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);

            Assert.That(bring.PlanFactory, Is.TypeOf<BringObjectPlanFactory>());
            Assert.That(move.PlanFactory, Is.TypeOf<MoveToEntityPlanFactory>());
        }

        [Test]
        public void BringObjectFactory_PreservesExistingMiniGoapPlan()
        {
            const string goalId = "goal_bring_factory";
            BringObjectBindings bindings = new("npc_01", "cup_01", "player_01");
            PlannerSnapshot snapshot = new("snapshot_bring_factory", goalId, 12, 7, State(
                (PlanningFact.Location("npc_01"), "start"),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Hand("npc_01"), PlanningFact.Free),
                (PlanningFact.Holder("cup_01"), PlanningFact.None),
                (PlanningFact.Reserved("cup_01"), PlanningFact.False),
                (PlanningFact.Reachable("cup_01"), PlanningFact.True),
                (PlanningFact.Reachable("player_01"), PlanningFact.True)));
            BringObjectPlanningInput input = new(snapshot, bindings);
            UniformCostPlanner planner = new();
            PlanResult expected = planner.Plan(snapshot, BringObjectActionCatalog.CreateGoal(goalId, bindings),
                BringObjectActionCatalog.CreateActions(bindings));

            Assert.That(BuiltInActionCatalog.Instance.TryGet(input.ActionId, out ActionRegistration registration),
                Is.True);
            PlanResult actual = registration.PlanFactory.CreatePlan(planner, goalId, input);

            Assert.That(actual.Status, Is.EqualTo(PlanSearchStatus.Found));
            Assert.That(actual.PlanHash, Is.EqualTo(expected.PlanHash));
            Assert.That(actual.Actions.Select(action => action.Definition.ActionType), Is.EqualTo(new[]
            {
                PlannerActionType.WalkTo, PlannerActionType.PickUp,
                PlannerActionType.WalkTo, PlannerActionType.Give,
            }));
        }

        [Test]
        public void MoveToEntityFactory_CreatesExistingSingleWalkPlan()
        {
            const string goalId = "goal_move_factory";
            PlannerSnapshot snapshot = new("snapshot_move_factory", goalId, 3, 5, State(
                (PlanningFact.Location("npc_01"), PlanningFact.Current),
                (PlanningFact.Busy("npc_01"), PlanningFact.False),
                (PlanningFact.Reachable("book_01"), PlanningFact.True)));
            MoveToEntityPlanningInput input = new(snapshot, "npc_01", "book_01");

            Assert.That(BuiltInActionCatalog.Instance.TryGet(input.ActionId, out ActionRegistration registration),
                Is.True);
            PlanResult plan = registration.PlanFactory.CreatePlan(new UniformCostPlanner(), goalId, input);

            Assert.That(plan.Status, Is.EqualTo(PlanSearchStatus.Found));
            Assert.That(plan.Goal.GoalType, Is.EqualTo("move_to_entity"));
            Assert.That(plan.Actions, Has.Count.EqualTo(1));
            Assert.That(plan.Actions[0].Definition.DefinitionId, Is.EqualTo("move_to_entity.walk"));
            Assert.That(plan.Actions[0].Definition.ActionType, Is.EqualTo(PlannerActionType.WalkTo));
            Assert.That(plan.Actions[0].Bindings["actor"], Is.EqualTo("npc_01"));
            Assert.That(plan.Actions[0].Bindings["destination"], Is.EqualTo("book_01"));
        }

        [Test]
        public void Factory_RejectsPlanningInputForDifferentAction()
        {
            PlannerSnapshot snapshot = new("snapshot_mismatch", "goal_mismatch", 0, 0,
                State((PlanningFact.Location("npc_01"), PlanningFact.Current)));
            MoveToEntityPlanningInput input = new(snapshot, "npc_01", "book_01");

            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration registration),
                Is.True);
            Assert.Throws<ArgumentException>(() =>
                registration.PlanFactory.CreatePlan(new UniformCostPlanner(), "goal_mismatch", input));
        }

        private static PlanningState State(params (string key, string value)[] facts) =>
            new(Pairs(facts));

        private static IEnumerable<KeyValuePair<string, string>> Pairs(
            params (string key, string value)[] facts) => facts.Select(fact =>
                new KeyValuePair<string, string>(fact.key, fact.value));
    }
}
