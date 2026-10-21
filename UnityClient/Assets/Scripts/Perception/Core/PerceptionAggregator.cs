using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Perception
{
    /// <summary>Deterministic, observer-local aggregation and de-duplication boundary for M6-Lite.1.</summary>
    public sealed class PerceptionAggregator
    {
        private readonly string observerEntityId;
        private readonly Dictionary<string, PerceivedEntityRecord> entities = new();
        private readonly Dictionary<string, Observation> observationsByKey = new();
        private int perceptionRevision;
        private int latestWorldRevision = -1;

        public PerceptionAggregator(string observerEntityId)
        {
            if (string.IsNullOrWhiteSpace(observerEntityId)) throw new ArgumentException("observerEntityId is required.");
            this.observerEntityId = observerEntityId;
        }

        public string ObserverEntityId => observerEntityId;
        public int PerceptionRevision => perceptionRevision;
        public int LatestWorldRevision => latestWorldRevision;
        public int DuplicateCount { get; private set; }
        public int StaleCount { get; private set; }
        public IReadOnlyDictionary<string, PerceivedEntityRecord> Entities => entities;
        public IReadOnlyCollection<Observation> Observations => observationsByKey.Values;

        public ObservationApplyResult Apply(Observation observation)
        {
            if (observation == null) return new ObservationApplyResult(ObservationApplyCode.Invalid, null, perceptionRevision);
            if (!string.Equals(observerEntityId, observation.ObserverEntityId, StringComparison.Ordinal))
                return new ObservationApplyResult(ObservationApplyCode.ObserverMismatch, observation, perceptionRevision);
            if (observationsByKey.ContainsKey(observation.DeduplicationKey))
            {
                DuplicateCount++;
                return new ObservationApplyResult(ObservationApplyCode.Duplicate, observation, perceptionRevision);
            }
            if (observation.WorldRevision < latestWorldRevision)
            {
                StaleCount++;
                return new ObservationApplyResult(ObservationApplyCode.StaleWorldRevision, observation, perceptionRevision);
            }

            observation.PerceptionRevision = ++perceptionRevision;
            latestWorldRevision = Math.Max(latestWorldRevision, observation.WorldRevision);
            observationsByKey.Add(observation.DeduplicationKey, observation);
            if (!string.IsNullOrWhiteSpace(observation.SubjectEntityId))
            {
                if (!entities.TryGetValue(observation.SubjectEntityId, out PerceivedEntityRecord record))
                {
                    record = new PerceivedEntityRecord(observation.SubjectEntityId);
                    entities.Add(record.EntityId, record);
                }
                record.Apply(observation);
            }
            return new ObservationApplyResult(ObservationApplyCode.Applied, observation, perceptionRevision);
        }

        /// <summary>
        /// Refreshes one continuous source (for example a VisionSensor target) without weakening
        /// the duplicate-event protection used by immutable GameplayEvents.
        /// </summary>
        public ObservationApplyResult ApplyOrRefresh(Observation observation)
        {
            if (observation == null) return new ObservationApplyResult(ObservationApplyCode.Invalid, null, perceptionRevision);
            if (!string.Equals(observerEntityId, observation.ObserverEntityId, StringComparison.Ordinal))
                return new ObservationApplyResult(ObservationApplyCode.ObserverMismatch, observation, perceptionRevision);
            if (!observationsByKey.TryGetValue(observation.DeduplicationKey, out Observation previous)) return Apply(observation);
            if (observation.WorldRevision < latestWorldRevision)
            {
                StaleCount++;
                return new ObservationApplyResult(ObservationApplyCode.StaleWorldRevision, observation, perceptionRevision);
            }

            if (HasSameMeaning(previous, observation))
            {
                observation.PerceptionRevision = previous.PerceptionRevision;
                latestWorldRevision = Math.Max(latestWorldRevision, observation.WorldRevision);
                observationsByKey[observation.DeduplicationKey] = observation;
                if (!string.IsNullOrWhiteSpace(observation.SubjectEntityId) &&
                    entities.TryGetValue(observation.SubjectEntityId, out PerceivedEntityRecord unchangedRecord))
                    unchangedRecord.RefreshFreshness(observation);
                return new ObservationApplyResult(ObservationApplyCode.Unchanged, observation, perceptionRevision);
            }

            observation.PerceptionRevision = ++perceptionRevision;
            latestWorldRevision = Math.Max(latestWorldRevision, observation.WorldRevision);
            observationsByKey[observation.DeduplicationKey] = observation;
            if (!string.IsNullOrWhiteSpace(observation.SubjectEntityId))
            {
                if (!entities.TryGetValue(observation.SubjectEntityId, out PerceivedEntityRecord record))
                {
                    record = new PerceivedEntityRecord(observation.SubjectEntityId);
                    entities.Add(record.EntityId, record);
                }
                record.Apply(observation);
            }
            return new ObservationApplyResult(ObservationApplyCode.Applied, observation, perceptionRevision);
        }

        private static bool HasSameMeaning(Observation previous, Observation next)
        {
            if (previous.SourceType != next.SourceType || previous.Channel != next.Channel ||
                !string.Equals(previous.SubjectEntityId, next.SubjectEntityId, StringComparison.Ordinal) ||
                previous.LifecycleState != next.LifecycleState || Math.Abs(previous.Confidence - next.Confidence) > 0.0001f)
                return false;
            if (previous.KnownFields.Count != next.KnownFields.Count || previous.UnknownFields.Count != next.UnknownFields.Count)
                return false;
            foreach (KeyValuePair<string, string> pair in previous.KnownFields)
                if (!next.KnownFields.TryGetValue(pair.Key, out string value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
                    return false;
            foreach (string field in previous.UnknownFields)
                if (!ContainsUnknownField(next.UnknownFields, field)) return false;
            return true;
        }

        private static bool ContainsUnknownField(IReadOnlyList<string> fields, string expected)
        {
            foreach (string field in fields)
                if (string.Equals(field, expected, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
