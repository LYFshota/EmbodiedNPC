using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using EmbodiedNPC.Planning.MiniGoap;

namespace EmbodiedNPC.Planning
{
    // Local presentation correlation only. Neither world revision nor plan index identifies a task.
    public readonly struct TaskFeedbackKey : IEquatable<TaskFeedbackKey>
    {
        public TaskFeedbackKey(string actor, string proposal, int revision, string trace)
        { Actor = actor ?? ""; Proposal = proposal ?? ""; Revision = revision; Trace = trace ?? ""; }
        public string Actor { get; }
        public string Proposal { get; }
        public int Revision { get; }
        public string Trace { get; }
        public bool Equals(TaskFeedbackKey other) => Actor == other.Actor && Proposal == other.Proposal &&
            Revision == other.Revision && Trace == other.Trace;
        public override bool Equals(object obj) => obj is TaskFeedbackKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Actor, Proposal, Revision, Trace);
        public static TaskFeedbackKey From(GoalOutcome value) =>
            new(value.ActorEntityId, value.ProposalId, value.ProposalRevision, value.TraceId);
    }

    public enum PlanProgressKind { Accepted, Action, Rediscovering, Replanning }

    public readonly struct PlanActionProgress
    {
        public PlanActionProgress(PlanProgressKind kind, string planId, int index, int replans,
            PlannerActionType? action = null, IReadOnlyDictionary<string, string> bindings = null)
        {
            Kind = kind; PlanId = planId ?? ""; Index = index; Replans = replans; Action = action;
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (bindings != null) foreach (var pair in bindings) copy.Add(pair.Key, pair.Value);
            Bindings = new ReadOnlyDictionary<string, string>(copy);
        }
        public PlanProgressKind Kind { get; }
        public string PlanId { get; }
        public int Index { get; }
        public int Replans { get; }
        public PlannerActionType? Action { get; }
        public IReadOnlyDictionary<string, string> Bindings { get; }
    }

    /// <summary>Immutable projection of the Runner's actual active task; never an execution command.</summary>
    public sealed class TaskFeedbackSnapshot
    {
        public TaskFeedbackSnapshot(TaskFeedbackKey key, string actionId, string target, string destination,
            string player, int worldRevision, long sequence, PlanActionProgress progress, string targetDefinition = "", string drinkType = "")
        {
            Key = key; ActionId = actionId ?? ""; Target = target ?? ""; Destination = destination ?? "";
            Player = player ?? ""; WorldRevision = worldRevision; Sequence = sequence; Progress = progress;
            TargetDefinition = targetDefinition ?? "";
            DrinkType = drinkType ?? "";
        }
        public TaskFeedbackKey Key { get; }
        public string ActionId { get; }
        public string Target { get; }
        public string Destination { get; }
        public string Player { get; }
        public int WorldRevision { get; }
        public long Sequence { get; }
        public PlanActionProgress Progress { get; }
        public string TargetDefinition { get; }
        public string DrinkType { get; }
    }
}
