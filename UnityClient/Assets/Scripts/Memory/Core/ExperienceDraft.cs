using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Memory
{
    /// <summary>Observer-local source evidence before Backend identity and sequence allocation.</summary>
    public sealed class ExperienceDraft
    {
        public const string CurrentSchemaVersion = "experience-draft/1.1";
        public const int MaxPersistentParticipants = 32;

        public ExperienceDraft(string observerId, string eventType, string sourceType, string sourceSessionId,
            string clockDomain, string sourceEventId, IReadOnlyCollection<string> sourceObservationIds,
            string actorEntityId, IReadOnlyCollection<string> targetEntityIds,
            IReadOnlyDictionary<string, object> structuredPayload, string naturalLanguagePayload,
            double occurredAt, double observedAt, int? worldRevision, string requestId, string traceId,
            string conversationId, float? confidence, string provenance,
            IReadOnlyCollection<PersistentParticipantIdentity> persistentParticipants = null)
        {
            ObserverId = observerId ?? string.Empty;
            EventType = eventType ?? string.Empty;
            SourceType = sourceType ?? string.Empty;
            SourceSessionId = sourceSessionId ?? string.Empty;
            ClockDomain = clockDomain ?? string.Empty;
            SourceEventId = sourceEventId;
            SourceObservationIds = Copy(sourceObservationIds);
            ActorEntityId = actorEntityId;
            TargetEntityIds = Copy(targetEntityIds);
            PersistentParticipants = Copy(persistentParticipants);
            StructuredPayload = new ReadOnlyDictionary<string, object>(
                new Dictionary<string, object>(structuredPayload ?? new Dictionary<string, object>()));
            NaturalLanguagePayload = naturalLanguagePayload;
            OccurredAt = occurredAt;
            ObservedAt = observedAt;
            WorldRevision = worldRevision;
            RequestId = requestId;
            TraceId = traceId;
            ConversationId = conversationId;
            Confidence = confidence;
            Provenance = provenance ?? string.Empty;
        }

        public string SchemaVersion => CurrentSchemaVersion;
        public string ObserverId { get; }
        public string EventType { get; }
        public string SourceType { get; }
        public string SourceSessionId { get; }
        public string ClockDomain { get; }
        public string SourceEventId { get; }
        public IReadOnlyList<string> SourceObservationIds { get; }
        public string ActorEntityId { get; }
        public IReadOnlyList<string> TargetEntityIds { get; }
        public IReadOnlyList<PersistentParticipantIdentity> PersistentParticipants { get; }
        public IReadOnlyDictionary<string, object> StructuredPayload { get; }
        public string NaturalLanguagePayload { get; }
        public double OccurredAt { get; }
        public double ObservedAt { get; }
        public int? WorldRevision { get; }
        public string RequestId { get; }
        public string TraceId { get; }
        public string ConversationId { get; }
        public float? Confidence { get; }
        public string Provenance { get; }

        public bool IsValid => !string.IsNullOrWhiteSpace(ObserverId) &&
                               !string.IsNullOrWhiteSpace(EventType) &&
                               !string.IsNullOrWhiteSpace(SourceType) &&
                               !string.IsNullOrWhiteSpace(SourceSessionId) &&
                               ClockDomain == "unity_realtime" &&
                               OccurredAt >= 0d && ObservedAt >= OccurredAt &&
                               (!Confidence.HasValue || Confidence.Value >= 0f && Confidence.Value <= 1f) &&
                               !string.IsNullOrWhiteSpace(Provenance) && HasValidPersistentParticipants();

        private bool HasValidPersistentParticipants()
        {
            if (PersistentParticipants.Count > MaxPersistentParticipants) return false;
            var participants = new HashSet<string>(StringComparer.Ordinal);
            var persistentEntities = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(ActorEntityId)) participants.Add(ActorEntityId);
            if (SourceType == "observation" && StructuredPayload.TryGetValue("known_fields", out object fields) &&
                fields is IReadOnlyDictionary<string, object> known && known.TryGetValue("event_type", out object type) &&
                ((string)type == "player.emote" || ((string)type == "heard_speech" && EventType == "observation" &&
                    Provenance == "HEARD" && ConversationId == null && StructuredPayload.TryGetValue("source_channel", out var channel) &&
                    (string)channel == "Audio" && known.TryGetValue("observer_entity_id", out var observer) &&
                    (string)observer == ObserverId && known.TryGetValue("social_role", out var role) && (string)role == "bystander")))
                participants.Add(ObserverId); // A witness is not a dialogue recipient.
            foreach (string target in TargetEntityIds)
                if (!string.IsNullOrWhiteSpace(target)) participants.Add(target);
            foreach (PersistentParticipantIdentity identity in PersistentParticipants)
            {
                if (identity == null || !identity.IsValid || !participants.Contains(identity.EntityId) ||
                    !persistentEntities.Add(identity.EntityId)) return false;
            }
            return true;
        }

        private static IReadOnlyList<T> Copy<T>(IReadOnlyCollection<T> values) =>
            new ReadOnlyCollection<T>(new List<T>(values ?? Array.Empty<T>()));
    }

    /// <summary>Long-lived identity evidence for one actor or target in an Experience.</summary>
    public sealed class PersistentParticipantIdentity
    {
        public PersistentParticipantIdentity(string entityId, string identityNamespace, string identityType,
            string persistentId)
        {
            EntityId = entityId ?? string.Empty;
            IdentityNamespace = identityNamespace ?? string.Empty;
            IdentityType = identityType ?? string.Empty;
            PersistentId = persistentId ?? string.Empty;
        }

        public string EntityId { get; }
        public string IdentityNamespace { get; }
        public string IdentityType { get; }
        public string PersistentId { get; }
        public bool IsValid => !string.IsNullOrWhiteSpace(EntityId) && EntityId.Length <= 120 &&
                               !string.IsNullOrWhiteSpace(IdentityNamespace) && IdentityNamespace.Length <= 80 &&
                               !string.IsNullOrWhiteSpace(IdentityType) && IdentityType.Length <= 80 &&
                               !string.IsNullOrWhiteSpace(PersistentId) && PersistentId.Length <= 160;
    }

    /// <summary>Projects authoritative active participant identities; missing identity remains absent.</summary>
    public static class PersistentParticipantIdentityResolver
    {
        public static IReadOnlyList<PersistentParticipantIdentity> ResolveParticipants(EntityRegistry registry,
            string actorEntityId, IReadOnlyCollection<string> targetEntityIds)
        {
            var result = new List<PersistentParticipantIdentity>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            TryAdd(actorEntityId, registry, seen, result);
            foreach (string targetEntityId in targetEntityIds ?? Array.Empty<string>())
                TryAdd(targetEntityId, registry, seen, result);
            return new ReadOnlyCollection<PersistentParticipantIdentity>(result);
        }

        public static IReadOnlyList<PersistentParticipantIdentity> ResolvePlayers(EntityRegistry registry,
            string actorEntityId, IReadOnlyCollection<string> targetEntityIds) =>
            ResolveParticipants(registry, actorEntityId, targetEntityIds);

        private static void TryAdd(string entityId, EntityRegistry registry, HashSet<string> seen,
            List<PersistentParticipantIdentity> result)
        {
            if (registry == null || string.IsNullOrWhiteSpace(entityId) || !seen.Add(entityId) ||
                !registry.TryGetEntity(entityId, out WorldEntity entity) ||
                !entity.TryGetPersistentIdentityKey(out PersistentIdentityKey key)) return;
            result.Add(new PersistentParticipantIdentity(entity.EntityId, key.Namespace, key.IdentityType,
                key.PersistentId));
        }
    }
}
