using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbodiedNPC.Events
{
    /// <summary>Immutable runtime event data. Entity references are stable IDs, never Unity objects.</summary>
    public sealed class AgentEvent
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyPayload =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        public AgentEvent(string eventId, string eventType, string sourceEntityId, string targetEntityId,
            string areaEntityId, IReadOnlyDictionary<string, string> payload, double occurredAt,
            int? worldRevision = null, string traceId = null, string sourceObservationId = null)
        {
            if (string.IsNullOrWhiteSpace(eventId))
                throw new ArgumentException("EventId is required.", nameof(eventId));
            if (string.IsNullOrWhiteSpace(eventType))
                throw new ArgumentException("EventType is required.", nameof(eventType));

            EventId = eventId;
            EventType = eventType;
            SourceEntityId = sourceEntityId ?? string.Empty;
            TargetEntityId = targetEntityId ?? string.Empty;
            AreaEntityId = areaEntityId ?? string.Empty;
            Payload = payload == null
                ? EmptyPayload
                : new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(payload, StringComparer.Ordinal));
            OccurredAt = Math.Max(0d, occurredAt);
            WorldRevision = worldRevision.HasValue ? Math.Max(0, worldRevision.Value) : null;
            TraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId;
            SourceObservationId = string.IsNullOrWhiteSpace(sourceObservationId) ? null : sourceObservationId;
        }

        public string EventId { get; }
        public string EventType { get; }
        public string SourceEntityId { get; }
        public string TargetEntityId { get; }
        public string AreaEntityId { get; }
        public IReadOnlyDictionary<string, string> Payload { get; }
        public double OccurredAt { get; }
        public int? WorldRevision { get; }
        public string TraceId { get; }
        public string SourceObservationId { get; }

        public bool TryGetPayload(string key, out string value) => Payload.TryGetValue(key, out value);
    }
}
