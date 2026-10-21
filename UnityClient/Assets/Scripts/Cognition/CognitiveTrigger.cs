using System;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Immutable lightweight reason to begin cognition; it never owns Unity objects or full context.</summary>
    public sealed class CognitiveTrigger
    {
        public CognitiveTrigger(string triggerId, string triggerType, string agentEntityId,
            string sourceEventId, double createdAt, string reason = "")
        {
            if (string.IsNullOrWhiteSpace(triggerId))
                throw new ArgumentException("TriggerId is required.", nameof(triggerId));
            if (string.IsNullOrWhiteSpace(triggerType))
                throw new ArgumentException("TriggerType is required.", nameof(triggerType));
            if (string.IsNullOrWhiteSpace(agentEntityId))
                throw new ArgumentException("AgentEntityId is required.", nameof(agentEntityId));

            TriggerId = triggerId;
            TriggerType = triggerType;
            AgentEntityId = agentEntityId;
            SourceEventId = sourceEventId ?? string.Empty;
            CreatedAt = Math.Max(0d, createdAt);
            Reason = reason ?? string.Empty;
        }

        public string TriggerId { get; }
        public string TriggerType { get; }
        public string AgentEntityId { get; }
        public string SourceEventId { get; }
        public double CreatedAt { get; }
        public string Reason { get; }
    }
}
