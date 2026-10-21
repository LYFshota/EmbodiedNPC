using System;

namespace EmbodiedNPC.Planning
{
    /// <summary>One terminal, authoritative result for an entire Goal rather than an intermediate Action.</summary>
    public readonly struct GoalOutcome
    {
        public GoalOutcome(string outcomeId, string proposalId, int proposalRevision, string traceId,
            string goalType, string actorEntityId, string targetEntityId, string destinationEntityId,
            PlanExecutionStatus status, string failureCode, int worldRevision, double occurredAt)
        {
            if (string.IsNullOrWhiteSpace(outcomeId)) throw new ArgumentException("outcomeId is required.", nameof(outcomeId));
            OutcomeId = outcomeId;
            ProposalId = proposalId ?? string.Empty;
            ProposalRevision = Math.Max(0, proposalRevision);
            TraceId = traceId ?? string.Empty;
            GoalType = goalType ?? string.Empty;
            ActorEntityId = actorEntityId ?? string.Empty;
            TargetEntityId = targetEntityId ?? string.Empty;
            DestinationEntityId = destinationEntityId ?? string.Empty;
            Status = status;
            FailureCode = failureCode ?? string.Empty;
            WorldRevision = Math.Max(0, worldRevision);
            OccurredAt = occurredAt;
        }

        public string OutcomeId { get; }
        public string ProposalId { get; }
        public int ProposalRevision { get; }
        public string TraceId { get; }
        public string GoalType { get; }
        public string ActorEntityId { get; }
        public string TargetEntityId { get; }
        public string DestinationEntityId { get; }
        public PlanExecutionStatus Status { get; }
        public string FailureCode { get; }
        public int WorldRevision { get; }
        public double OccurredAt { get; }
    }
}
