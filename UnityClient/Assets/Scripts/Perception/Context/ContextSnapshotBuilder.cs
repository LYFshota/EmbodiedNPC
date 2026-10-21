using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Applies a bounded deterministic attention policy before information is offered to an LLM.</summary>
    public sealed class ContextSnapshotBuilder
    {
        private readonly PlannerFactBuilder plannerFactBuilder;
        private readonly AttentionPolicy attentionPolicy;
        private long nextContextSequence;

        public ContextSnapshotBuilder(PlannerFactBuilder plannerFactBuilder = null, AttentionPolicy attentionPolicy = null)
        {
            this.plannerFactBuilder = plannerFactBuilder ?? new PlannerFactBuilder();
            this.attentionPolicy = attentionPolicy ?? new AttentionPolicy();
        }

        public ContextSnapshot Build(PerceptionAggregator state, string playerUtterance, double now, int maxObservations = 5,
            SpatialWorkingMemory spatialMemory = null, SelfSceneStateDto selfState = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            maxObservations = Math.Max(1, maxObservations);
            List<Observation> candidates = state.Observations
                .Where(observation => observation.ExpiresAt >= now)
                .OrderByDescending(observation => attentionPolicy.Score(observation, playerUtterance, now))
                .ThenByDescending(observation => observation.ObservedAt)
                .ToList();
            List<Observation> selected = candidates.Take(maxObservations).ToList();
            string contextId = $"ctx_{state.ObserverEntityId}_{++nextContextSequence:D6}";
            IReadOnlyList<PerceivedEntityDto> entities =
                ProjectEntities(state.Observations, selected, spatialMemory, playerUtterance, now);
            return new ContextSnapshot(contextId, state.ObserverEntityId, state.PerceptionRevision,
                plannerFactBuilder.Build(state, now), selected, candidates.Count - selected.Count, now,
                entities, ProjectSceneFacts(state.Observations, entities, selfState, now));
        }

        private static SceneFactsDto ProjectSceneFacts(IReadOnlyCollection<Observation> observations,
            IReadOnlyList<PerceivedEntityDto> entities, SelfSceneStateDto selfState, double now)
        {
            var visible = new HashSet<string>(entities.Where(x => x.visibility == "current_visible")
                .Select(x => x.entity_id), StringComparer.Ordinal);
            var visibleNpcs = new HashSet<string>(entities.Where(x => x.visibility == "current_visible" && x.kind == "npc")
                .Select(x => x.entity_id), StringComparer.Ordinal);
            var seats = new List<ObservedSeatDto>();
            foreach (var group in observations.Where(x => x.Channel == ObservationChannel.Visual &&
                         x.SourceType == ObservationSourceType.ContinuousScan).GroupBy(x => x.SubjectEntityId)
                         .OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                Observation value = group.OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.PerceptionRevision).First();
                if (!visible.Contains(value.SubjectEntityId) || value.ExpiresAt < now ||
                    !value.KnownFields.TryGetValue("visibility_state", out string visibility) || visibility != "current_visible" ||
                    !value.KnownFields.TryGetValue("seat_occupancy", out string occupancy)) continue;
                value.KnownFields.TryGetValue("seat_occupant_id", out string occupant);
                value.KnownFields.TryGetValue("seat_opposite_id", out string opposite);
                seats.Add(new ObservedSeatDto {
                    seat_entity_id = value.SubjectEntityId,
                    occupancy = occupancy == "occupied" || occupancy == "empty" ? occupancy : "unknown",
                    occupant_entity_id = occupancy == "occupied" && occupant != null && visibleNpcs.Contains(occupant) ? occupant : null,
                    relation_to_self = selfState?.posture != "seated" ? "unknown" :
                        selfState.seat_entity_id == value.SubjectEntityId ? "self" :
                        selfState.seat_entity_id == opposite ? "opposite" : "unknown"
                });
                if (seats.Count == 4) break;
            }
            return selfState == null && seats.Count == 0 ? null : new SceneFactsDto {
                self = selfState, observed_seats = seats.ToArray()
            };
        }

        private static IReadOnlyList<PerceivedEntityDto> ProjectEntities(IReadOnlyCollection<Observation> observations,
            IReadOnlyList<Observation> selected, SpatialWorkingMemory spatialMemory, string utterance, double now)
        {
            var latestVisual = new Dictionary<string, Observation>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, EntityCandidate>(StringComparer.Ordinal);
            var selectedIds = new HashSet<string>(selected.Select(value => value.SubjectEntityId), StringComparer.Ordinal);
            foreach (Observation observation in observations)
            {
                if (observation.Channel != ObservationChannel.Visual || string.IsNullOrWhiteSpace(observation.SubjectEntityId)) continue;
                if (!latestVisual.TryGetValue(observation.SubjectEntityId, out Observation prior) ||
                    (observation.SourceType == ObservationSourceType.ContinuousScan && prior.SourceType != ObservationSourceType.ContinuousScan) ||
                    (observation.SourceType == prior.SourceType && (observation.ObservedAt > prior.ObservedAt ||
                     (observation.ObservedAt == prior.ObservedAt && observation.PerceptionRevision > prior.PerceptionRevision))))
                    latestVisual[observation.SubjectEntityId] = observation;
            }

            // A later lost/forgotten visual record blocks old event copies from claiming current visibility.
            // The selected event budget must not hide a live target or overwrite its visible semantic name.
            foreach (Observation visual in latestVisual.Values)
            {
                if (visual.ExpiresAt < now || visual.LifecycleState == PerceptionLifecycleState.Forgotten ||
                    !visual.KnownFields.TryGetValue("visibility_state", out string visibility) || visibility != "current_visible" ||
                    (spatialMemory == null && !selectedIds.Contains(visual.SubjectEntityId))) continue;
                candidates[visual.SubjectEntityId] = new EntityCandidate(
                    ContextSnapshot.CreateEntityDto(visual.SubjectEntityId, visual.KnownFields, "current_visible"), visual.ObservedAt);
            }

            // Selected non-visual evidence remains available, but cannot revive a visually lost entity.
            foreach (Observation observation in selected)
            {
                string entityId = observation.SubjectEntityId;
                if (string.IsNullOrWhiteSpace(entityId) || latestVisual.ContainsKey(entityId) || candidates.ContainsKey(entityId) ||
                    observation.ExpiresAt < now || observation.LifecycleState == PerceptionLifecycleState.Forgotten) continue;
                string visibility = observation.KnownFields.TryGetValue("visibility_state", out string value)
                    ? value : observation.LifecycleState.ToString().ToLowerInvariant();
                if (visibility == "forgotten" || visibility == "lost" || visibility == "remembered") continue;
                candidates[entityId] = new EntityCandidate(
                    ContextSnapshot.CreateEntityDto(entityId, observation.KnownFields, visibility), observation.ObservedAt);
            }

            if (spatialMemory != null)
                foreach (SpatialMemoryRecord record in spatialMemory.GetRecords(now))
                {
                    if (candidates.TryGetValue(record.EntityId, out EntityCandidate known) && known.Entity.visibility == "current_visible") continue;
                    candidates[record.EntityId] = new EntityCandidate(
                        ContextSnapshot.CreateEntityDto(record.EntityId, record.SemanticFields, "remembered"), record.LastSeenAt);
                }

            EntityCandidate[] ranked = candidates.Values.OrderByDescending(value => Relevance(value.Entity, utterance))
                .ThenByDescending(value => value.Entity.visibility == "current_visible")
                .ThenByDescending(value => value.ObservedAt)
                .ThenBy(value => value.Entity.entity_id, StringComparer.Ordinal)
                .ToArray();
            Debug.Log("[ContextSnapshot] StageA candidates=" + string.Join(";", ranked.Select(value =>
                $"{value.Entity.entity_id}|tags={string.Join(",", value.Entity.semantic_tags)}|relevance={Relevance(value.Entity, utterance)}")));
            return ranked.Take(16).Select(value => value.Entity).ToArray();
        }

        private static int Relevance(PerceivedEntityDto entity, string utterance)
        {
            if (string.IsNullOrWhiteSpace(utterance)) return 0;
            if (Contains(utterance, entity.entity_id) || Contains(utterance, entity.display_name)) return 100;
            foreach (string alias in entity.aliases)
                if (Contains(utterance, alias)) return 80;
            foreach (string tag in entity.semantic_tags)
                if (SemanticRequestMatcher.Matches(utterance, tag)) return 40;
            return 0;
        }

        private static bool Contains(string text, string fragment) => !string.IsNullOrWhiteSpace(fragment) &&
            text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        private sealed class EntityCandidate
        {
            public EntityCandidate(PerceivedEntityDto entity, double observedAt) { Entity = entity; ObservedAt = observedAt; }
            public PerceivedEntityDto Entity { get; }
            public double ObservedAt { get; }
        }
    }
}
