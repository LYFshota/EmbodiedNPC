using System;
using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Actions
{
    internal sealed class ResolvedServeSnackGoal : IResolvedActionGoal
    {
        public ResolvedServeSnackGoal(WorldEntity actor, WorldEntity station, WorldEntity player, string snack, string quantity, int revision)
        { Actor=actor; Target=station; Player=player; Snack=snack; Quantity=quantity; ValidatedWorldRevision=revision; }
        public string ActionId => "serve_snack";
        public WorldEntity Actor { get; }
        public WorldEntity Target { get; }
        public WorldEntity Player { get; }
        public string Snack { get; }
        public string Quantity { get; }
        public int ValidatedWorldRevision { get; }
        public bool HasProprioceptiveTargetEvidence => false;
    }
    public sealed class ServeSnackBinder : IActionBinder
    {
        public ActionBindingResult Bind(GoalProposal proposal, ActionDescriptor descriptor, ActionAdmissionContext context)
        {
            if (!SnackCatalog.IsValid(proposal.snack_type) || !SnackCatalog.IsQuantity(proposal.quantity) ||
                !string.IsNullOrEmpty(proposal.drink_type) || !string.IsNullOrEmpty(proposal.target_entity_id) ||
                !string.IsNullOrEmpty(proposal.destination_entity_id) || context.Actor == null ||
                !context.Actor.TryGetComponent(out SnackServiceCapability cap) || !cap.IsAuthorized ||
                string.IsNullOrEmpty(proposal.RequestingPlayerId) || context.EntityRegistry == null ||
                !context.EntityRegistry.TryGetEntity(proposal.RequestingPlayerId, out var player) ||
                !SnackServiceCapability.ValidateRuntime(context.EntityRegistry, context.Actor, cap.Station, player, proposal.snack_type))
                return ActionBindingResult.Rejected(GoalValidationFailureCode.TargetActionUnavailable, "点心类型、数量、服务权限或请求玩家无效。");
            return ActionBindingResult.Bound(context.Actor, cap.Station, player);
        }
    }
    public sealed class ServeSnackAdmissionPolicy : IActionAdmissionPolicy
    {
        public GoalValidationResult Validate(GoalProposal proposal, ActionDescriptor descriptor, IActionBinder binder, ActionAdmissionContext context)
        {
            var binding=binder.Bind(proposal,descriptor,context);
            if (!binding.IsBound) return GoalValidationResult.Rejected(binding.FailureCode,binding.Detail);
            if (context.WorldState == null || context.Reservations == null || context.Agent == null || !context.Agent.isActiveAndEnabled || !context.Agent.isOnNavMesh)
                return GoalValidationResult.Rejected(GoalValidationFailureCode.ActorNotOnNavMesh,"点心服务执行环境不可用。");
            return GoalValidationResult.Approved(new ResolvedServeSnackGoal(binding.Actor,binding.Target,binding.Destination,
                proposal.snack_type,proposal.quantity,context.WorldState.WorldRevision));
        }
    }
    internal sealed class ServeSnackInput : IActionPlanningInput
    {
        public string ActionId => "serve_snack";
        public PlannerSnapshot Snapshot { get; set; }
        public ResolvedServeSnackGoal Goal { get; set; }
    }
    public sealed class ServeSnackPreparationPolicy : IActionExecutionPreparationPolicy
    {
        public ActionExecutionPreparationResult Prepare(IResolvedActionGoal resolvedGoal, ActionExecutionPreparationContext context)
        {
            if (resolvedGoal is not ResolvedServeSnackGoal goal || context == null ||
                !SnackServiceCapability.ValidateRuntime(context.EntityRegistry,goal.Actor,goal.Target,goal.Player,goal.Snack) ||
                !goal.Actor.GetComponent<SnackServiceCapability>().TryResolve(context.Agent,goal.Snack,out var position))
                return ActionExecutionPreparationResult.Failed("SNACK_SERVICE_UNAVAILABLE","点心取餐点失效或不可达。");
            var facts=new Dictionary<string,string> {
                [PlanningFact.Location(goal.Actor.EntityId)]=PlanningFact.Current, [PlanningFact.Busy(goal.Actor.EntityId)]=PlanningFact.False,
                [PlanningFact.Reachable(goal.Target.EntityId)]=PlanningFact.True, ["snack_served"]=PlanningFact.False };
            var input=new ServeSnackInput { Goal=goal, Snapshot=new PlannerSnapshot(context.SnapshotId,context.GoalId,
                context.WorldState.WorldRevision,context.PerceptionRevision,new PlanningState(facts)) };
            return ActionExecutionPreparationResult.Succeeded(new ActionExecutionPreparation(input,
                new ValidatedActionBindings(goal.Actor,goal.Target,goal.Player,targetApproachPosition:position),
                ActionLeaseRequirement.Exclusive(goal.Target),refreshBeforeActionValidation:true));
        }
    }
    public sealed class ServeSnackPlanFactory : IActionPlanFactory
    {
        public PlanResult CreatePlan(UniformCostPlanner planner,string goalId,IActionPlanningInput input)
        {
            var data=input as ServeSnackInput ?? throw new ArgumentException("ServeSnackInput required."); var g=data.Goal;
            var goal=new PlanningGoal(goalId,"serve_snack",new Dictionary<string,string> { ["snack_served"]=PlanningFact.True });
            var walk=new ActionInstance(new ActionDefinition("serve_snack.walk",PlannerActionType.WalkTo,1,"WalkTo"),
                new Dictionary<string,string> { ["actor"]=g.Actor.EntityId,["destination"]=g.Target.EntityId },
                new Dictionary<string,string> { [PlanningFact.Busy(g.Actor.EntityId)]=PlanningFact.False,[PlanningFact.Reachable(g.Target.EntityId)]=PlanningFact.True },
                new Dictionary<string,string> { [PlanningFact.Location(g.Actor.EntityId)]=g.Target.EntityId });
            var serve=new ActionInstance(new ActionDefinition("serve_snack.serve",PlannerActionType.ServeSnack,1,"ServeSnack"),
                new Dictionary<string,string> { ["actor"]=g.Actor.EntityId,["station"]=g.Target.EntityId,["player"]=g.Player.EntityId,
                    ["snack_type"]=g.Snack,["quantity"]=g.Quantity },
                new Dictionary<string,string> { [PlanningFact.Location(g.Actor.EntityId)]=g.Target.EntityId },
                new Dictionary<string,string> { ["snack_served"]=PlanningFact.True });
            return planner.Plan(input.Snapshot,goal,new[] {walk,serve});
        }
    }
    public sealed class ServeSnackPresentationPolicy : IActionPresentationPolicy
    {
        public ActionPresentationInfo Present(ActionExecutionResult result)
        {
            result.ValidatedBindings.TryGetValue("snack_type",out var snack); result.ValidatedBindings.TryGetValue("quantity",out var quantity);
            string name=SnackCatalog.Name(snack,SettingsUiLanguage.IsEnglish);
            string message=result.Status switch {
                ActionExecutionStatus.Accepted => SettingsUiLanguage.T($"好呀，我去取{name} × {quantity}，稍等一下。",$"I'll get {name} × {quantity}. Please wait."),
                ActionExecutionStatus.Succeeded => SettingsUiLanguage.T(
                    quantity == "1" ? $"你的{name}准备好了，请享用。" : $"{quantity}份{name}准备好了，请享用。",
                    quantity == "1" ? $"Your {name} is ready. Enjoy!" : $"Your order of {name} × {quantity} is ready. Enjoy!"),
                ActionExecutionStatus.Cancelled => SettingsUiLanguage.T("点心服务已取消。","Snack service cancelled."),
                _ => SettingsUiLanguage.T("这次没有完成点心服务。","Snack service was not completed.") };
            return new ActionPresentationInfo(result.Status,result.FailureCode,message);
        }
    }
}
