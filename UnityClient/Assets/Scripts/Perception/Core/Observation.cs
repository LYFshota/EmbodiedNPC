using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbodiedNPC.Perception
{
    public enum ObservationChannel { Direct, Manual, Visual, Proximity, Contact, Audio }
    public enum ObservationSourceType { GameplayEvent, Direct, ContinuousScan }
    public enum PerceptionLifecycleState { Detected, Tracked, Lost, Remembered, Forgotten }
    public enum ObservationApplyCode { Applied, Unchanged, Duplicate, StaleWorldRevision, ObserverMismatch, Invalid }

    /// <summary>Evidence available to exactly one observer. Unknown fields must never be filled by aggregation.</summary>
    public sealed class Observation
    {
        public Observation(string observationId, string observerEntityId, ObservationSourceType sourceType,
            string sourceId, ObservationChannel channel, string subjectEntityId,
            IReadOnlyDictionary<string, string> knownFields, IReadOnlyCollection<string> unknownFields,
            float confidence, int worldRevision, double observedAt, double expiresAt,
            PerceptionLifecycleState lifecycleState = PerceptionLifecycleState.Tracked,
            string sourceEventId = null, double occurredAt = double.NaN,
            string reporterEntityId = null, string parentMemoryId = null,
            string reportConversationId = null, int reportHop = 0, string traceId = null)
        {
            if (string.IsNullOrWhiteSpace(observationId) || string.IsNullOrWhiteSpace(observerEntityId) ||
                string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Observation identifiers are required.");
            ObservationId = observationId; ObserverEntityId = observerEntityId; SourceType = sourceType;
            SourceId = sourceId; Channel = channel; SubjectEntityId = subjectEntityId ?? string.Empty;
            SourceEventId = string.IsNullOrWhiteSpace(sourceEventId) ? sourceId : sourceEventId;
            TraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId;
            KnownFields = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(knownFields ??
                new Dictionary<string, string>()));
            UnknownFields = Array.AsReadOnly(new List<string>(unknownFields ?? Array.Empty<string>()).ToArray());
            Confidence = confidence < 0f ? 0f : confidence > 1f ? 1f : confidence; WorldRevision = worldRevision;
            ObservedAt = observedAt; OccurredAt = double.IsNaN(occurredAt) ? observedAt : occurredAt;
            ExpiresAt = expiresAt; LifecycleState = lifecycleState;
            ReporterEntityId = reporterEntityId ?? string.Empty;
            ParentMemoryId = parentMemoryId ?? string.Empty;
            ReportConversationId = reportConversationId ?? string.Empty;
            ReportHop = Math.Max(0, reportHop);
        }

        public string ObservationId { get; }
        public string ObserverEntityId { get; }
        public ObservationSourceType SourceType { get; }
        public string SourceId { get; }
        public string SourceEventId { get; }
        public string TraceId { get; }
        public ObservationChannel Channel { get; }
        public string SubjectEntityId { get; }
        public IReadOnlyDictionary<string, string> KnownFields { get; }
        public IReadOnlyList<string> UnknownFields { get; }
        public float Confidence { get; }
        public int WorldRevision { get; }
        public int PerceptionRevision { get; internal set; }
        public double ObservedAt { get; }
        public double OccurredAt { get; }
        public double ExpiresAt { get; }
        public PerceptionLifecycleState LifecycleState { get; }
        public string ReporterEntityId { get; }
        public string ParentMemoryId { get; }
        public string ReportConversationId { get; }
        public int ReportHop { get; }
        public string DeduplicationKey => $"{SourceId}|{ObserverEntityId}|{Channel}";
    }

    public readonly struct ObservationApplyResult
    {
        public ObservationApplyResult(ObservationApplyCode code, Observation observation, int perceptionRevision)
        { Code = code; Observation = observation; PerceptionRevision = perceptionRevision; }
        public ObservationApplyCode Code { get; }
        public Observation Observation { get; }
        public int PerceptionRevision { get; }
        public bool IsApplied => Code == ObservationApplyCode.Applied;
    }
}
