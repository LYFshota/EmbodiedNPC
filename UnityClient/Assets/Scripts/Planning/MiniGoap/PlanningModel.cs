using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbodiedNPC.Planning.MiniGoap
{
    public enum PlannerActionType { WalkTo, PickUp, Give, PutDown, Open, PrepareDrink, CleanCounter }
    public enum PlanSearchStatus { Found, AlreadySatisfied, NoPlan, SearchLimitExceeded }

    public sealed class ActionDefinition
    {
        public ActionDefinition(string definitionId, PlannerActionType actionType, int baseCost, string skillType)
        {
            if (string.IsNullOrWhiteSpace(definitionId)) throw new ArgumentException("Definition ID is required.", nameof(definitionId));
            if (baseCost < 0) throw new ArgumentOutOfRangeException(nameof(baseCost), "Action cost cannot be negative.");
            if (string.IsNullOrWhiteSpace(skillType)) throw new ArgumentException("Skill type is required.", nameof(skillType));
            DefinitionId = definitionId;
            ActionType = actionType;
            BaseCost = baseCost;
            SkillType = skillType;
        }

        public string DefinitionId { get; }
        public PlannerActionType ActionType { get; }
        public int BaseCost { get; }
        public string SkillType { get; }
    }

    public sealed class ActionInstance
    {
        private readonly IReadOnlyDictionary<string, string> bindings;
        private readonly IReadOnlyDictionary<string, string> preconditions;
        private readonly IReadOnlyDictionary<string, string> effects;

        public ActionInstance(ActionDefinition definition,
            IEnumerable<KeyValuePair<string, string>> bindings,
            IEnumerable<KeyValuePair<string, string>> preconditions,
            IEnumerable<KeyValuePair<string, string>> effects,
            int? computedCost = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            ComputedCost = computedCost ?? definition.BaseCost;
            if (ComputedCost < 0) throw new ArgumentOutOfRangeException(nameof(computedCost), "Action cost cannot be negative.");
            this.bindings = Copy(bindings, nameof(bindings));
            this.preconditions = Copy(preconditions, nameof(preconditions));
            this.effects = Copy(effects, nameof(effects));
            CanonicalSignature = BuildCanonicalSignature();
        }

        public ActionDefinition Definition { get; }
        public int ComputedCost { get; }
        public IReadOnlyDictionary<string, string> Bindings => bindings;
        public IReadOnlyDictionary<string, string> Preconditions => preconditions;
        public IReadOnlyDictionary<string, string> Effects => effects;
        public string CanonicalSignature { get; }

        public bool CanApply(PlanningState state) => state != null && state.Matches(preconditions);
        public PlanningState Apply(PlanningState state) =>
            CanApply(state) ? state.Apply(effects) : throw new InvalidOperationException("Action preconditions are not satisfied.");

        public override string ToString()
        {
            string arguments = string.Join(",", bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
            return $"{Definition.ActionType}({arguments})";
        }

        private string BuildCanonicalSignature()
        {
            string orderedBindings = string.Join(";", bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
            return $"{Definition.DefinitionId}|{Definition.ActionType}|{orderedBindings}|{ComputedCost}";
        }

        private static IReadOnlyDictionary<string, string> Copy(IEnumerable<KeyValuePair<string, string>> source, string parameterName)
        {
            Dictionary<string, string> copy = new(StringComparer.Ordinal);
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("Keys cannot be empty.", parameterName);
                    if (!copy.TryAdd(pair.Key, pair.Value ?? string.Empty))
                        throw new ArgumentException($"Duplicate key: {pair.Key}", parameterName);
                }
            }
            return new ReadOnlyDictionary<string, string>(copy);
        }
    }

    public sealed class PlanningGoal
    {
        public PlanningGoal(string goalId, string goalType, IEnumerable<KeyValuePair<string, string>> desiredFacts)
        {
            if (string.IsNullOrWhiteSpace(goalId)) throw new ArgumentException("Goal ID is required.", nameof(goalId));
            if (string.IsNullOrWhiteSpace(goalType)) throw new ArgumentException("Goal type is required.", nameof(goalType));
            GoalId = goalId;
            GoalType = goalType;
            DesiredState = new PlanningState(desiredFacts);
        }

        public string GoalId { get; }
        public string GoalType { get; }
        public PlanningState DesiredState { get; }
        public bool IsSatisfiedBy(PlanningState state) => state != null && state.Matches(DesiredState.Values);
    }

    public sealed class PlannerSnapshot
    {
        public PlannerSnapshot(string snapshotId, string goalId, int worldRevision, int perceptionRevision, PlanningState state)
        {
            if (string.IsNullOrWhiteSpace(snapshotId)) throw new ArgumentException("Snapshot ID is required.", nameof(snapshotId));
            if (string.IsNullOrWhiteSpace(goalId)) throw new ArgumentException("Goal ID is required.", nameof(goalId));
            if (worldRevision < 0) throw new ArgumentOutOfRangeException(nameof(worldRevision));
            if (perceptionRevision < 0) throw new ArgumentOutOfRangeException(nameof(perceptionRevision));
            SnapshotId = snapshotId;
            GoalId = goalId;
            WorldRevision = worldRevision;
            PerceptionRevision = perceptionRevision;
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        public string SnapshotId { get; }
        public string GoalId { get; }
        public int WorldRevision { get; }
        public int PerceptionRevision { get; }
        public PlanningState State { get; }
    }

    public sealed class PlanResult
    {
        public PlanResult(PlanSearchStatus status, PlannerSnapshot snapshot, PlanningGoal goal,
            IReadOnlyList<ActionInstance> actions, int totalCost, int expandedNodes)
        {
            Status = status;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Goal = goal ?? throw new ArgumentNullException(nameof(goal));
            Actions = new ReadOnlyCollection<ActionInstance>((actions ?? Array.Empty<ActionInstance>()).ToList());
            TotalCost = totalCost;
            ExpandedNodes = expandedNodes;
            PlanHash = PlanHasher.Compute(goal, Actions);
            PlanId = $"plan_{PlanHash[..12]}";
        }

        public PlanSearchStatus Status { get; }
        public bool IsSuccess => Status is PlanSearchStatus.Found or PlanSearchStatus.AlreadySatisfied;
        public PlannerSnapshot Snapshot { get; }
        public PlanningGoal Goal { get; }
        public IReadOnlyList<ActionInstance> Actions { get; }
        public int TotalCost { get; }
        public int ExpandedNodes { get; }
        public string PlanHash { get; }
        public string PlanId { get; }
        public int PlannedFromWorldRevision => Snapshot.WorldRevision;
        public int PlannedFromPerceptionRevision => Snapshot.PerceptionRevision;
    }

    public static class PlanHasher
    {
        public static string Compute(PlanningGoal goal, IReadOnlyList<ActionInstance> actions)
        {
            if (goal == null) throw new ArgumentNullException(nameof(goal));
            StringBuilder source = new();
            source.Append(goal.GoalType).Append('|').Append(goal.GoalId).Append('|')
                .Append(goal.DesiredState.CanonicalValue);
            if (actions != null)
            {
                foreach (ActionInstance action in actions) source.Append("->").Append(action.CanonicalSignature);
            }

            using SHA256 sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()));
            StringBuilder result = new(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }
    }
}
