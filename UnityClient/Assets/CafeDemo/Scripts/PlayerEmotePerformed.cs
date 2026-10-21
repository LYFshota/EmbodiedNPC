using System;

namespace CafeDemo
{
    /// <summary>One completed visual start, independent of any observer's perception.</summary>
    public sealed class PlayerEmotePerformed
    {
        public PlayerEmotePerformed(string eventId, string actorEntityId, string emoteId, double occurredAt,
            string targetEntityId = null, string responderEntityId = null)
        {
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(actorEntityId) ||
                string.IsNullOrWhiteSpace(emoteId) || !double.IsFinite(occurredAt) || occurredAt < 0d)
                throw new ArgumentException("Emote identifiers and a finite non-negative timestamp are required.");
            EventId = eventId; ActorEntityId = actorEntityId; EmoteId = emoteId; OccurredAt = occurredAt;
            TargetEntityId = targetEntityId ?? string.Empty;
            ResponderEntityId = responderEntityId ?? string.Empty;
        }
        public string EventId { get; }
        public string ActorEntityId { get; }
        public string EmoteId { get; }
        public double OccurredAt { get; }
        // Source-side inference, never automatically an observer's confirmed visual fact.
        public string TargetEntityId { get; }
        public string ResponderEntityId { get; }
        public string TraceId => "cognitive-trace-" + EventId;
    }
}
