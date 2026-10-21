using EmbodiedNPC.Planning.MiniGoap;

namespace EmbodiedNPC.Actions
{
    /// <summary>Immutable Mini-GOAP input prepared from an already admitted concrete goal.</summary>
    public interface IActionPlanningInput
    {
        string ActionId { get; }
        PlannerSnapshot Snapshot { get; }
    }

    /// <summary>Action-specific construction of the existing Mini-GOAP goal and action instances.</summary>
    public interface IActionPlanFactory
    {
        PlanResult CreatePlan(UniformCostPlanner planner, string goalId, IActionPlanningInput input);
    }
}
