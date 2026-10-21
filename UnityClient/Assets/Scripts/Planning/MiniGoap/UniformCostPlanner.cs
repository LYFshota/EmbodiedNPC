using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Planning.MiniGoap
{
    /// <summary>Bounded deterministic Uniform-Cost Search for the small M7 action set.</summary>
    public sealed class UniformCostPlanner
    {
        public UniformCostPlanner(int maxExpandedNodes = 256, int maxPlanLength = 16)
        {
            if (maxExpandedNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxExpandedNodes));
            if (maxPlanLength < 1) throw new ArgumentOutOfRangeException(nameof(maxPlanLength));
            MaxExpandedNodes = maxExpandedNodes;
            MaxPlanLength = maxPlanLength;
        }

        public int MaxExpandedNodes { get; }
        public int MaxPlanLength { get; }

        public PlanResult Plan(PlannerSnapshot snapshot, PlanningGoal goal, IReadOnlyList<ActionInstance> actions)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (goal == null) throw new ArgumentNullException(nameof(goal));
            if (!string.Equals(snapshot.GoalId, goal.GoalId, StringComparison.Ordinal))
                throw new ArgumentException("Snapshot and goal IDs must match.", nameof(goal));

            if (goal.IsSatisfiedBy(snapshot.State))
                return new PlanResult(PlanSearchStatus.AlreadySatisfied, snapshot, goal, Array.Empty<ActionInstance>(), 0, 0);

            IReadOnlyList<ActionInstance> candidates = actions ?? Array.Empty<ActionInstance>();
            List<SearchNode> open = new() { new SearchNode(snapshot.State, null, null, 0, 0, 0) };
            Dictionary<PlanningState, int> bestCosts = new() { [snapshot.State] = 0 };
            long nextSequence = 1;
            int expandedNodes = 0;

            while (open.Count > 0)
            {
                SearchNode current = RemoveLowestCost(open);
                if (bestCosts.TryGetValue(current.State, out int knownCost) && current.Cost > knownCost) continue;
                expandedNodes++;
                if (expandedNodes > MaxExpandedNodes)
                    return new PlanResult(PlanSearchStatus.SearchLimitExceeded, snapshot, goal,
                        Array.Empty<ActionInstance>(), 0, MaxExpandedNodes);

                if (goal.IsSatisfiedBy(current.State))
                    return new PlanResult(PlanSearchStatus.Found, snapshot, goal, BuildActions(current),
                        current.Cost, expandedNodes);

                if (current.Depth >= MaxPlanLength) continue;
                foreach (ActionInstance action in candidates)
                {
                    if (action == null || !action.CanApply(current.State)) continue;
                    PlanningState nextState = action.Apply(current.State);
                    if (nextState.Equals(current.State)) continue;
                    int nextCost = checked(current.Cost + action.ComputedCost);
                    if (bestCosts.TryGetValue(nextState, out int previousCost) && previousCost <= nextCost) continue;
                    bestCosts[nextState] = nextCost;
                    open.Add(new SearchNode(nextState, current, action, nextCost, current.Depth + 1, nextSequence++));
                }
            }

            return new PlanResult(PlanSearchStatus.NoPlan, snapshot, goal, Array.Empty<ActionInstance>(), 0, expandedNodes);
        }

        private static SearchNode RemoveLowestCost(List<SearchNode> open)
        {
            int bestIndex = 0;
            for (int i = 1; i < open.Count; i++)
            {
                if (open[i].Cost < open[bestIndex].Cost ||
                    open[i].Cost == open[bestIndex].Cost && open[i].Sequence < open[bestIndex].Sequence)
                {
                    bestIndex = i;
                }
            }

            SearchNode result = open[bestIndex];
            open.RemoveAt(bestIndex);
            return result;
        }

        private static IReadOnlyList<ActionInstance> BuildActions(SearchNode node)
        {
            List<ActionInstance> reversed = new();
            for (SearchNode current = node; current?.Action != null; current = current.Parent)
                reversed.Add(current.Action);
            reversed.Reverse();
            return reversed;
        }

        private sealed class SearchNode
        {
            public SearchNode(PlanningState state, SearchNode parent, ActionInstance action,
                int cost, int depth, long sequence)
            {
                State = state;
                Parent = parent;
                Action = action;
                Cost = cost;
                Depth = depth;
                Sequence = sequence;
            }

            public PlanningState State { get; }
            public SearchNode Parent { get; }
            public ActionInstance Action { get; }
            public int Cost { get; }
            public int Depth { get; }
            public long Sequence { get; }
        }
    }
}
