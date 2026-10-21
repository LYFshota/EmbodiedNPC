using System;
using System.Collections.Generic;
using System.Globalization;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Memory
{
    /// <summary>Maps only already-applied observer-local evidence; never queries global world state.</summary>
    public sealed class ObservationExperienceAdapter
    {
        private readonly Action<ExperienceDraft> sink;
        public const double DefaultSpatialChangeMeters = 0.25d;
        private readonly double spatialChangeMeters;
        private readonly EntityRegistry entityRegistry;
        private readonly HashSet<string> emoteSources = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Observer, string Source), Observation> admittedScans = new();

        public ObservationExperienceAdapter(Action<ExperienceDraft> sink,
            double spatialChangeMeters = DefaultSpatialChangeMeters, EntityRegistry entityRegistry = null)
        {
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            if (double.IsNaN(spatialChangeMeters) || double.IsInfinity(spatialChangeMeters) || spatialChangeMeters < 0d)
                throw new ArgumentOutOfRangeException(nameof(spatialChangeMeters));
            this.spatialChangeMeters = spatialChangeMeters;
            this.entityRegistry = entityRegistry;
        }

        public void Handle(ObservationApplyResult result)
        {
            if (!result.IsApplied || result.Observation == null) return;
            try
            {
                Observation observation = result.Observation;
                bool emote = Read(observation, "event_type") == "player.emote" ||
                    Read(observation, "event_type") == "heard_speech";
                if (emote && emoteSources.Contains(observation.DeduplicationKey)) return;
                bool continuous = observation.SourceType == ObservationSourceType.ContinuousScan;
                var key = (observation.ObserverEntityId, observation.DeduplicationKey);
                if (continuous && admittedScans.TryGetValue(key, out Observation previous) &&
                    SameHistoricalSample(previous, observation)) return;
                ExperienceDraft draft = Create(observation, result.PerceptionRevision, entityRegistry);
                if (draft == null) return;
                sink(draft);
                if (emote) emoteSources.Add(observation.DeduplicationKey);
                if (continuous) admittedScans[key] = observation;
            }
            catch (Exception) { /* Experience persistence cannot break perception or Legacy M7. */ }
        }

        // This is historical sampling only. Perception/SpatialMemory still receive every refresh.
        // Compare with the last admitted sample so gradual movement eventually becomes evidence.
        private bool SameHistoricalSample(Observation previous, Observation next)
        {
            if (previous.Channel != next.Channel || previous.SubjectEntityId != next.SubjectEntityId ||
                previous.LifecycleState != next.LifecycleState || previous.Confidence != next.Confidence ||
                previous.ReporterEntityId != next.ReporterEntityId || previous.ParentMemoryId != next.ParentMemoryId ||
                previous.ReportConversationId != next.ReportConversationId || previous.ReportHop != next.ReportHop ||
                previous.KnownFields.Count != next.KnownFields.Count || previous.UnknownFields.Count != next.UnknownFields.Count)
                return false;
            foreach (KeyValuePair<string, string> field in previous.KnownFields)
            {
                if (!next.KnownFields.TryGetValue(field.Key, out string value)) return false;
                if (string.Equals(field.Value, value, StringComparison.Ordinal)) continue;
                if (next.Channel != ObservationChannel.Visual || field.Key != "last_seen_position" ||
                    !WithinSpatialThreshold(field.Value, value)) return false;
            }
            foreach (string field in previous.UnknownFields)
            {
                bool found = false;
                foreach (string other in next.UnknownFields)
                    if (string.Equals(field, other, StringComparison.Ordinal)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        private bool WithinSpatialThreshold(string previous, string next)
        {
            string[] before = previous?.Split(',');
            string[] after = next?.Split(',');
            if (before == null || after == null || before.Length != 3 || after.Length != 3) return false;
            double distanceSquared = 0d;
            for (int i = 0; i < 3; i++)
            {
                if (!double.TryParse(before[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ||
                    !double.TryParse(after[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) ||
                    double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b)) return false;
                distanceSquared += (a - b) * (a - b);
            }
            return distanceSquared < spatialChangeMeters * spatialChangeMeters;
        }

        public static ExperienceDraft Create(Observation observation, int perceptionRevision = 0, EntityRegistry registry = null)
        {
            if (observation == null || string.IsNullOrWhiteSpace(observation.ObserverEntityId)) return null;
            string epistemic = Read(observation, "epistemic_status");
            string sourceEventType = Read(observation, "event_type");
            bool emote = sourceEventType == "player.emote";
            bool hearing = sourceEventType == "heard_speech" && observation.Channel == ObservationChannel.Audio &&
                Read(observation, "social_role") == "bystander" && Read(observation, "observer_entity_id") == observation.ObserverEntityId;
            if (sourceEventType == "heard_speech" && !hearing) return null;
            bool reported = epistemic.Equals("reported", StringComparison.OrdinalIgnoreCase);
            if (!reported && (sourceEventType.StartsWith("Goal", StringComparison.OrdinalIgnoreCase) ||
                              sourceEventType.StartsWith("Skill", StringComparison.OrdinalIgnoreCase)))
                return null; // Typed Goal/Skill adapters own these canonical sources.

            string actor = NullIfBlank(Read(observation, "actor_entity_id"));
            string target = NullIfBlank(Read(observation, "target_entity_id")) ??
                            NullIfBlank(observation.SubjectEntityId);
            var targets = string.IsNullOrWhiteSpace(target) ? Array.Empty<string>() : new[] { target };
            var known = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in observation.KnownFields)
                known[pair.Key] = pair.Value;
            var payload = new Dictionary<string, object>
            {
                ["perception_revision"] = perceptionRevision > 0 ? perceptionRevision : observation.PerceptionRevision,
                ["lifecycle_state"] = observation.LifecycleState.ToString(),
                ["observation_source_type"] = observation.SourceType.ToString(),
                ["source_channel"] = observation.Channel.ToString(),
                ["raw_source_id"] = observation.SourceId,
                ["known_fields"] = known,
                ["unknown_fields"] = new List<string>(observation.UnknownFields),
            };
            if (reported)
            {
                payload["reporter_entity_id"] = observation.ReporterEntityId;
                payload["parent_memory_id"] = observation.ParentMemoryId;
                payload["report_conversation_id"] = observation.ReportConversationId;
                payload["report_hop"] = observation.ReportHop;
            }
            double occurred = observation.OccurredAt, observed = observation.ObservedAt;
            if (emote)
            {
                // The visual event carries UTC; the ledger's Unity clock must match dialogue/presentation clocks.
                payload["event_utc"] = observation.OccurredAt;
                payload["source_event_id"] = ExperienceIds.ProtocolId(observation.SourceEventId, "observation_source");
                payload["trace_id"] = observation.TraceId;
                occurred = observed = UnityEngine.Time.realtimeSinceStartupAsDouble;
            }
            var identities = emote || hearing ? PersistentParticipantIdentityResolver.ResolveParticipants(registry, actor,
                new List<string>(targets) { observation.ObserverEntityId }) : null;
            return new ExperienceDraft(
                observation.ObserverEntityId, reported ? "reported_observation" : "observation", "observation",
                ClientSessionIdentity.Current, "unity_realtime",
                ExperienceIds.ProtocolId(observation.SourceEventId, "observation_source"),
                new[] { ExperienceIds.ProtocolId(observation.ObservationId, "observation") }, actor, targets,
                payload, null, occurred, observed, observation.WorldRevision,
                null, emote || hearing ? ExperienceIds.ProtocolId(observation.TraceId, "trace") : null,
                reported ? observation.ReportConversationId : null, observation.Confidence,
                Provenance(observation, epistemic), identities);
        }

        private static string Provenance(Observation observation, string epistemic)
        {
            if (epistemic.Equals("reported", StringComparison.OrdinalIgnoreCase)) return "REPORTED";
            if (epistemic.Equals("inferred", StringComparison.OrdinalIgnoreCase)) return "INFERRED";
            if (observation.Channel == ObservationChannel.Audio) return "HEARD";
            if (observation.Channel == ObservationChannel.Visual) return "WITNESSED";
            return "SYSTEM_CONFIRMED";
        }

        private static string Read(Observation observation, string key) =>
            observation.KnownFields.TryGetValue(key, out string value) ? value ?? string.Empty : string.Empty;
        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
