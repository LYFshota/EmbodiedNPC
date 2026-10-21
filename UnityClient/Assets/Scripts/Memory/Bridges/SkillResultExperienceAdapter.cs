using System;
using System.Collections.Generic;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Memory
{
    public sealed class SkillResultExperienceAdapter
    {
        private readonly string observerId;
        private readonly Action<ExperienceDraft> sink;
        private readonly EntityRegistry entityRegistry;
        public SkillResultExperienceAdapter(string observerId, Action<ExperienceDraft> sink,
            EntityRegistry entityRegistry = null)
        {
            this.observerId = observerId ?? string.Empty;
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.entityRegistry = entityRegistry;
        }

        public void Handle(SkillResult result)
        {
            if (result.ActorEntityId != observerId) return;
            try { sink(Create(result, UnityEngine.Time.realtimeSinceStartupAsDouble, entityRegistry)); }
            catch (Exception) { /* Ledger failure must not affect the authoritative World transaction. */ }
        }

        public static ExperienceDraft Create(SkillResult result, double observedAt, EntityRegistry entityRegistry = null)
        {
            string eventType = result.Status switch
            {
                SkillStatus.Succeeded => "skill_succeeded",
                SkillStatus.Failed => "skill_failed",
                SkillStatus.Cancelled => "skill_cancelled",
                SkillStatus.TimedOut => "skill_timed_out",
                _ => throw new ArgumentOutOfRangeException(nameof(result)),
            };
            var targets = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.TargetEntityId)) targets.Add(result.TargetEntityId);
            if (!string.IsNullOrWhiteSpace(result.DestinationEntityId) && !targets.Contains(result.DestinationEntityId))
                targets.Add(result.DestinationEntityId);
            var payload = new Dictionary<string, object>
            {
                ["skill_id"] = result.SkillId,
                ["skill_type"] = result.SkillType,
                ["status"] = result.Status.ToString(),
                ["failure_code"] = result.FailureCode,
                ["destination_entity_id"] = result.DestinationEntityId,
                ["event_sequence"] = result.EventSequence,
                ["world_revision_before"] = result.WorldRevisionBefore,
            };
            string sourceId = ExperienceIds.ProtocolId(
                $"skill_{result.EventSequence}_{result.SkillId}", "skill_result");
            return new ExperienceDraft(result.ActorEntityId, eventType, "skill_result",
                ClientSessionIdentity.Current, "unity_realtime", sourceId, Array.Empty<string>(),
                result.ActorEntityId, targets, payload, null, observedAt, observedAt,
                result.WorldRevisionAfter, null, NullIfBlank(result.TraceId), null, null, "EXECUTED_BY_SELF",
                PersistentParticipantIdentityResolver.ResolveParticipants(entityRegistry, result.ActorEntityId, targets));
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
