using System;
using System.Collections.Generic;
using System.Text;
using EmbodiedNPC.Communication;

namespace EmbodiedNPC.Perception
{
    /// <summary>Bounded, observer-local context that is safe to send across the Unity/backend boundary.</summary>
    public sealed class ContextSnapshot
    {
        private readonly IReadOnlyList<PerceivedEntityDto> projectedEntities;

        public ContextSnapshot(string contextId, string observerEntityId, int perceptionRevision, PlannerFacts plannerFacts,
            IReadOnlyList<Observation> observations, int droppedObservations, double createdAt,
            IReadOnlyList<PerceivedEntityDto> projectedEntities = null, SceneFactsDto sceneFacts = null)
        {
            ContextId = contextId; ObserverEntityId = observerEntityId; PerceptionRevision = perceptionRevision;
            PlannerFacts = plannerFacts; Observations = observations; DroppedObservations = droppedObservations; CreatedAt = createdAt;
            this.projectedEntities = projectedEntities;
            SceneFacts = sceneFacts;
        }

        public string ContextId { get; }
        public string ObserverEntityId { get; }
        public int PerceptionRevision { get; }
        public PlannerFacts PlannerFacts { get; }
        public IReadOnlyList<Observation> Observations { get; }
        public int DroppedObservations { get; }
        public double CreatedAt { get; }
        public SceneFactsDto SceneFacts { get; }

        public string ToPromptText(int characterLimit = 1800)
        {
            StringBuilder text = new();
            text.Append("Observer-local perception only; unknown fields must not be inferred.\n");
            text.Append("Observer: ").Append(ObserverEntityId).Append("; revision: ").Append(PerceptionRevision).Append('\n');
            text.Append("Planner facts: ");
            foreach (KeyValuePair<string, string> fact in PlannerFacts.Values) text.Append(fact.Key).Append('=').Append(fact.Value).Append("; ");
            text.AppendLine();
            foreach (Observation observation in Observations)
            {
                text.Append("Observation ").Append(observation.ObservationId).Append(" [").Append(observation.Channel).Append("] ");
                if (!string.IsNullOrWhiteSpace(observation.SubjectEntityId)) text.Append("subject=").Append(observation.SubjectEntityId).Append("; ");
                foreach (KeyValuePair<string, string> field in observation.KnownFields) text.Append(field.Key).Append('=').Append(field.Value).Append("; ");
                if (observation.UnknownFields.Count > 0) text.Append("unknown=").Append(string.Join(",", observation.UnknownFields)).Append("; ");
                text.Append("confidence=").Append(observation.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
                if (text.Length >= characterLimit) break;
            }
            if (DroppedObservations > 0) text.Append("Truncated observations: ").Append(DroppedObservations).AppendLine();
            return text.Length <= characterLimit ? text.ToString() : text.ToString(0, characterLimit);
        }

        public PerceptionSnapshotDto ToPerceptionDto()
        {
            if (projectedEntities != null)
                return new PerceptionSnapshotDto
                {
                    observer_entity_id = ObserverEntityId,
                    perception_revision = PerceptionRevision,
                    entities = new List<PerceivedEntityDto>(projectedEntities).ToArray(),
                };
            Dictionary<string, PerceivedEntityDto> entities = new(StringComparer.Ordinal);
            foreach (Observation observation in Observations)
            {
                if (string.IsNullOrWhiteSpace(observation.SubjectEntityId)) continue;
                IReadOnlyDictionary<string, string> fields = observation.KnownFields;
                string visibility = Get(fields, "visibility_state", observation.LifecycleState.ToString()).ToLowerInvariant();
                if (visibility == "forgotten" || observation.ExpiresAt < CreatedAt) continue;
                entities[observation.SubjectEntityId] = CreateEntityDto(observation.SubjectEntityId, fields, visibility);
            }

            return new PerceptionSnapshotDto
            {
                observer_entity_id = ObserverEntityId,
                perception_revision = PerceptionRevision,
                entities = new List<PerceivedEntityDto>(entities.Values).ToArray(),
            };
        }

        // The wire whitelist intentionally excludes positions, raw observation payloads and live scene references.
        internal static PerceivedEntityDto CreateEntityDto(string entityId, IReadOnlyDictionary<string, string> fields,
            string visibility) => new()
        {
            entity_id = entityId,
            kind = ToWireKind(fields),
            display_name = Get(fields, "display_name", entityId),
            aliases = Split(Get(fields, "aliases", string.Empty)),
            semantic_tags = Split(Get(fields, "semantic_tags", string.Empty)),
            visibility = visibility,
            capabilities = Split(Get(fields, "capabilities", string.Empty)),
        };

        private static string Get(IReadOnlyDictionary<string, string> fields, string key, string fallback) =>
            fields != null && fields.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;

        private static string ToWireKind(IReadOnlyDictionary<string, string> fields)
        {
            string normalized = Get(fields, "entity_kind", "unknown").ToLowerInvariant();
            if (normalized == "agent")
            {
                string role = Get(fields, "agent_role", "unknown").ToLowerInvariant();
                return role == "player" || role == "npc" ? role : "unknown";
            }
            // The transport contract predates Runtime-v2's Object name and still calls carryable objects "item".
            return normalized == "object" ? "item" : normalized;
        }

        private static string[] Split(string value) => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
