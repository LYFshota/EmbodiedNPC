using System;
using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Actions
{
    public static class CleanCounterRules
    {
        public static bool ValidateRuntime(EntityRegistry registry, WorldEntity actor, WorldEntity target, int condition) =>
            DrinkTypes.IsCurrent(registry, actor) && DrinkTypes.IsCurrent(registry, target) &&
            actor.TryGetComponent(out CounterCleaningCapability cap) && cap.IsAuthorized && cap.Station.Entity == target &&
            cap.Station.World.GetCounterState(target.EntityId) == CounterCleanliness.NeedsCleaning &&
            cap.Station.World.GetCounterConditionRevision(target.EntityId) == condition;
    }
    internal sealed class ResolvedCleanCounterGoal : IResolvedActionGoal
    {
        public ResolvedCleanCounterGoal(WorldEntity actor, WorldEntity target, int condition, int revision)
        { Actor = actor; Target = target; ConditionVersion = condition; ValidatedWorldRevision = revision; }
        public string ActionId => "clean_counter";
        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public int ConditionVersion { get; }
        public int ValidatedWorldRevision { get; }
        public bool HasProprioceptiveTargetEvidence => false;
    }
    public sealed class CleanCounterBinder : IActionBinder
    {
        public ActionBindingResult Bind(GoalProposal proposal, ActionDescriptor descriptor, ActionAdmissionContext context)
        {
            if (context.Actor == null || !context.Actor.TryGetComponent(out CounterCleaningCapability cap) || !cap.IsAuthorized ||
                proposal.target_entity_id != cap.Station.Entity.EntityId || !string.IsNullOrEmpty(proposal.destination_entity_id) ||
                !string.IsNullOrEmpty(proposal.drink_type) || cap.Station.World != context.WorldState ||
                !CleanCounterRules.ValidateRuntime(context.EntityRegistry, context.Actor, cap.Station.Entity,
                    context.WorldState.GetCounterConditionRevision(proposal.target_entity_id)))
                return ActionBindingResult.Rejected(GoalValidationFailureCode.TargetActionUnavailable, "清洁目标、能力或需求已失效。");
            return ActionBindingResult.Bound(context.Actor, cap.Station.Entity, null);
        }
    }
    public sealed class CleanCounterAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor, IActionBinder binder, ActionAdmissionContext context)
        {
            var binding = binder.Bind(proposal, descriptor, context);
            if (!binding.IsBound) return GoalValidationResult.Rejected(binding.FailureCode, binding.Detail);
            if (context.Reservations == null || context.Agent == null || !context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return GoalValidationResult.Rejected(GoalValidationFailureCode.ActorNotOnNavMesh, "清洁执行环境不可用。");
            return GoalValidationResult.Approved(new ResolvedCleanCounterGoal(binding.Actor, binding.Target,
                context.WorldState.GetCounterConditionRevision(binding.Target.EntityId), context.WorldState.WorldRevision));
        }
    }
    internal sealed class CleanCounterInput : IActionPlanningInput
    {
        public string ActionId => "clean_counter";
        public PlannerSnapshot Snapshot { get; set; }
        public ResolvedCleanCounterGoal Goal { get; set; }
    }
    public sealed class CleanCounterPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal, ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedCleanCounterGoal goal || context == null ||
                !CleanCounterRules.ValidateRuntime(context.EntityRegistry, goal.Actor, goal.Target, goal.ConditionVersion) ||
                !context.ApproachResolver.TryResolve(context.Agent, goal.Target, out var position))
                return ActionExecutionPreparationResult.Failed("COUNTER_UNAVAILABLE", "清洁点失效或不可达。");
            var facts = new Dictionary<string, string> { [PlanningFact.Location(goal.Actor.EntityId)] = PlanningFact.Current,
                [PlanningFact.Busy(goal.Actor.EntityId)] = PlanningFact.False, [PlanningFact.Reachable(goal.Target.EntityId)] = PlanningFact.True,
                ["counter_cleaned"] = PlanningFact.False };
            var input = new CleanCounterInput { Goal = goal, Snapshot = new PlannerSnapshot(context.SnapshotId, context.GoalId,
                context.WorldState.WorldRevision, context.PerceptionRevision, new PlanningState(facts)) };
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(input,
                new ValidatedActionBindings(goal.Actor, goal.Target, null, targetApproachPosition: position),
                ActionLeaseRequirement.Exclusive(goal.Target), refreshBeforeActionValidation: true));
        }
    }
    public sealed class CleanCounterPlanFactory : IActionPlanFactory
    {
        public PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input)
        {
            var clean = input as CleanCounterInput ?? throw new ArgumentException("CleanCounterInput required."); var g = clean.Goal;
            var goal = new PlanningGoal(goalId, "clean_counter", new Dictionary<string,string> { ["counter_cleaned"] = PlanningFact.True });
            var walk = new ActionInstance(new ActionDefinition("clean_counter.walk", PlannerActionType.WalkTo, 1, "WalkTo"),
                new Dictionary<string,string> { ["actor"] = g.Actor.EntityId, ["destination"] = g.Target.EntityId },
                new Dictionary<string,string> { [PlanningFact.Busy(g.Actor.EntityId)] = PlanningFact.False, [PlanningFact.Reachable(g.Target.EntityId)] = PlanningFact.True },
                new Dictionary<string,string> { [PlanningFact.Location(g.Actor.EntityId)] = g.Target.EntityId });
            var wipe = new ActionInstance(new ActionDefinition("clean_counter.wipe", PlannerActionType.CleanCounter, 1, "CleanCounter"),
                new Dictionary<string,string> { ["actor"] = g.Actor.EntityId, ["target"] = g.Target.EntityId,
                    ["condition"] = g.ConditionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new Dictionary<string,string> { [PlanningFact.Location(g.Actor.EntityId)] = g.Target.EntityId },
                new Dictionary<string,string> { ["counter_cleaned"] = PlanningFact.True });
            return planner.Plan(input.Snapshot, goal, new[] { walk, wipe });
        }
    }
    public sealed class CleanCounterPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result) => new(result.Status, result.FailureCode,
            result.Status switch { ActionExecutionStatus.Accepted => "好呀，我去把旁边那块柜台擦干净。",
                ActionExecutionStatus.Succeeded => "柜台已经擦干净了。", ActionExecutionStatus.Cancelled => "清洁已停止。",
                ActionExecutionStatus.TimedOut => "清洁超时了。", _ => "这次没能完成清洁。" });
    }
}
