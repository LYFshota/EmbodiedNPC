using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Actions
{
    public static class DrinkTypes
    {
        // Stable protocol order and both display languages have one authoring source.
        private static readonly (string Id, string Chinese, string English)[] definitions =
        {
            ("latte", "拿铁", "Latte"),
            ("cappuccino", "卡布奇诺", "Cappuccino"),
            ("americano", "美式", "Americano"),
            ("espresso", "浓缩咖啡", "Espresso"),
            ("flat_white", "馥芮白", "Flat White"),
            ("mocha", "摩卡", "Mocha"),
            ("caramel_macchiato", "焦糖玛奇朵", "Caramel Macchiato"),
            ("hot_chocolate", "热巧克力", "Hot Chocolate"),
            ("matcha_latte", "抹茶拿铁", "Matcha Latte"),
            ("black_tea", "红茶", "Black Tea"),
        };
        public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(definitions.Select(value => value.Id).ToArray());
        private static readonly Dictionary<string, (string Id, string Chinese, string English)> byId =
            definitions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        public static bool IsValid(string value) => value != null && byId.ContainsKey(value);
        public static string Name(string value, bool english = false) => value != null && byId.TryGetValue(value, out var definition)
            ? (english ? definition.English : definition.Chinese) : (english ? "drink" : "饮品");
        public static bool IsCurrent(EntityRegistry registry, WorldEntity entity) => registry != null &&
            entity != null && entity.isActiveAndEnabled && entity.gameObject.activeInHierarchy &&
            registry.TryGetEntity(entity.EntityId, out var current) && current == entity;
        public static bool ValidateRuntime(EntityRegistry registry, WorldEntity actor, WorldEntity station, WorldEntity player)
            => IsCurrent(registry, actor) && IsCurrent(registry, station) && IsCurrent(registry, player) &&
               actor.EntityId == "npc_cafe_01" && player.IsAgent(AgentRole.Player) &&
               actor.TryGetComponent(out DrinkPreparationCapability capability) && capability.IsAuthorized &&
               capability.Station.Entity == station;
    }

    internal sealed class ResolvedPrepareDrinkGoal : IResolvedActionGoal
    {
        public ResolvedPrepareDrinkGoal(WorldEntity actor, WorldEntity target, WorldEntity player, string drink, int revision)
        { Actor = actor; Target = target; Player = player; Drink = drink; ValidatedWorldRevision = revision; }
        public string ActionId => "prepare_drink";
        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public WorldEntity Player { get; }
        public string Drink { get; }
        public int ValidatedWorldRevision { get; }
        public bool HasProprioceptiveTargetEvidence => false;
    }

    public sealed class PrepareDrinkBinder : IActionBinder
    {
        public ActionBindingResult Bind(GoalProposal proposal, ActionDescriptor descriptor, ActionAdmissionContext context)
        {
            if (!DrinkTypes.IsValid(proposal.drink_type) || !string.IsNullOrEmpty(proposal.target_entity_id) ||
                !string.IsNullOrEmpty(proposal.destination_entity_id) || context.Actor == null ||
                !context.Actor.TryGetComponent(out DrinkPreparationCapability capability) || !capability.IsAuthorized ||
                string.IsNullOrEmpty(proposal.RequestingPlayerId) || context.EntityRegistry == null ||
                !context.EntityRegistry.TryGetEntity(proposal.RequestingPlayerId, out var player) ||
                !DrinkTypes.ValidateRuntime(context.EntityRegistry, context.Actor, capability.Station.Entity, player))
                return ActionBindingResult.Rejected(GoalValidationFailureCode.TargetActionUnavailable, "制作参数、能力、制作点或请求玩家无效。");
            return ActionBindingResult.Bound(context.Actor, capability.Station.Entity, player);
        }
    }

    public sealed class PrepareDrinkAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor, IActionBinder binder,
            ActionAdmissionContext context)
        {
            var binding = binder.Bind(proposal, descriptor, context);
            if (!binding.IsBound) return GoalValidationResult.Rejected(binding.FailureCode, binding.Detail);
            if (context.WorldState == null || context.Reservations == null || context.Agent == null ||
                !context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return GoalValidationResult.Rejected(GoalValidationFailureCode.ActorNotOnNavMesh, "制作执行环境不可用。");
            return GoalValidationResult.Approved(new ResolvedPrepareDrinkGoal(binding.Actor, binding.Target,
                binding.Destination, proposal.drink_type, context.WorldState.WorldRevision));
        }
    }

    internal sealed class PrepareDrinkInput : IActionPlanningInput
    {
        public string ActionId => "prepare_drink";
        public PlannerSnapshot Snapshot { get; set; }
        public ResolvedPrepareDrinkGoal Goal { get; set; }
    }

    public sealed class PrepareDrinkPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal, ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedPrepareDrinkGoal goal || context == null ||
                !DrinkTypes.ValidateRuntime(context.EntityRegistry, goal.Actor, goal.Target, goal.Player) ||
                !context.ApproachResolver.TryResolve(context.Agent, goal.Target, out var position))
                return ActionExecutionPreparationResult.Failed("STATION_UNAVAILABLE", "制作点失效或路径不可达。");
            var facts = new Dictionary<string, string>
            {
                [PlanningFact.Location(goal.Actor.EntityId)] = PlanningFact.Current,
                [PlanningFact.Busy(goal.Actor.EntityId)] = PlanningFact.False,
                [PlanningFact.Reachable(goal.Target.EntityId)] = PlanningFact.True,
                ["drink_prepared"] = PlanningFact.False,
            };
            var input = new PrepareDrinkInput { Goal = goal, Snapshot = new PlannerSnapshot(context.SnapshotId,
                context.GoalId, context.WorldState.WorldRevision, context.PerceptionRevision, new PlanningState(facts)) };
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(input,
                new ValidatedActionBindings(goal.Actor, goal.Target, goal.Player, targetApproachPosition: position),
                ActionLeaseRequirement.Exclusive(goal.Target), refreshBeforeActionValidation: true));
        }
    }

    public sealed class PrepareDrinkPlanFactory : IActionPlanFactory
    {
        public PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input)
        {
            var drink = input as PrepareDrinkInput ?? throw new ArgumentException("PrepareDrinkInput required.");
            var goal = drink.Goal;
            var planGoal = new PlanningGoal(goalId, "prepare_drink", new Dictionary<string, string> { ["drink_prepared"] = PlanningFact.True });
            var walk = new ActionInstance(new ActionDefinition("prepare_drink.walk", PlannerActionType.WalkTo, 1, "WalkTo"),
                new Dictionary<string, string> { ["actor"] = goal.Actor.EntityId, ["destination"] = goal.Target.EntityId },
                new Dictionary<string, string> { [PlanningFact.Busy(goal.Actor.EntityId)] = PlanningFact.False,
                    [PlanningFact.Reachable(goal.Target.EntityId)] = PlanningFact.True },
                new Dictionary<string, string> { [PlanningFact.Location(goal.Actor.EntityId)] = goal.Target.EntityId });
            var prepare = new ActionInstance(new ActionDefinition("prepare_drink.prepare", PlannerActionType.PrepareDrink, 1, "PrepareDrink"),
                new Dictionary<string, string> { ["actor"] = goal.Actor.EntityId, ["station"] = goal.Target.EntityId,
                    ["drink_type"] = goal.Drink, ["player"] = goal.Player.EntityId },
                new Dictionary<string, string> { [PlanningFact.Location(goal.Actor.EntityId)] = goal.Target.EntityId },
                new Dictionary<string, string> { ["drink_prepared"] = PlanningFact.True });
            return planner.Plan(input.Snapshot, planGoal, new[] { walk, prepare });
        }
    }

    public sealed class PrepareDrinkPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result)
        {
            result.ValidatedBindings.TryGetValue("drink_type", out string drink);
            string name = DrinkTypes.Name(drink, SettingsUiLanguage.IsEnglish);
            string message = result.Status switch
            {
                ActionExecutionStatus.Accepted => SettingsUiLanguage.T($"好呀，我去准备{name}，稍等一下。", $"Sure, I will prepare {name}. Please wait."),
                ActionExecutionStatus.Succeeded => SettingsUiLanguage.T($"{name}做好了，请享用。", $"Your {name} is ready. Enjoy!"),
                ActionExecutionStatus.Cancelled => SettingsUiLanguage.T("我已经停止制作饮品。", "I have stopped preparing the drink."),
                ActionExecutionStatus.TimedOut => SettingsUiLanguage.T("制作超时了，这次没有完成。", "Drink preparation timed out and was not completed."),
                ActionExecutionStatus.Failed or ActionExecutionStatus.Rejected => SettingsUiLanguage.T("这次没能完成饮品制作。", "I could not finish preparing the drink."),
                _ => result.SourceMessage,
            };
            return new ActionPresentationInfo(result.Status, result.FailureCode, message);
        }
    }
}
