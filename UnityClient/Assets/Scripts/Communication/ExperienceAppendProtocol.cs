using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbodiedNPC.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Communication
{
    public sealed class PersistentParticipantIdentityDto
    {
        public string entity_id;
        public string identity_namespace;
        public string identity_type;
        public string persistent_id;

        public static PersistentParticipantIdentityDto FromDomain(PersistentParticipantIdentity value) => new()
        {
            entity_id = value.EntityId,
            identity_namespace = value.IdentityNamespace,
            identity_type = value.IdentityType,
            persistent_id = value.PersistentId,
        };
    }

    public sealed class ExperienceDraftDto
    {
        public string schema_version;
        public string observer_id;
        public string event_type;
        public string source_type;
        public string source_session_id;
        public string clock_domain;
        public string source_event_id;
        public string[] source_observation_ids = Array.Empty<string>();
        public string actor_entity_id;
        public string[] target_entity_ids = Array.Empty<string>();
        public PersistentParticipantIdentityDto[] persistent_participants =
            Array.Empty<PersistentParticipantIdentityDto>();
        public Dictionary<string, object> structured_payload = new();
        public string natural_language_payload;
        public double occurred_at;
        public double observed_at;
        public int? world_revision;
        public string request_id;
        public string trace_id;
        public string conversation_id;
        public float? confidence;
        public string provenance;

        public static ExperienceDraftDto FromDomain(ExperienceDraft value) => new()
        {
            schema_version = value.SchemaVersion,
            observer_id = value.ObserverId,
            event_type = value.EventType,
            source_type = value.SourceType,
            source_session_id = value.SourceSessionId,
            clock_domain = value.ClockDomain,
            source_event_id = value.SourceEventId,
            source_observation_ids = value.SourceObservationIds.ToArray(),
            actor_entity_id = value.ActorEntityId,
            target_entity_ids = value.TargetEntityIds.ToArray(),
            persistent_participants = value.PersistentParticipants
                .Select(PersistentParticipantIdentityDto.FromDomain).ToArray(),
            structured_payload = new Dictionary<string, object>(value.StructuredPayload),
            natural_language_payload = value.NaturalLanguagePayload,
            occurred_at = value.OccurredAt,
            observed_at = value.ObservedAt,
            world_revision = value.WorldRevision,
            request_id = value.RequestId,
            trace_id = value.TraceId,
            conversation_id = value.ConversationId,
            confidence = value.Confidence,
            provenance = value.Provenance,
        };
    }

    public sealed class ExperienceAppendRequest
    {
        public string message_type = "experience_append_request";
        public string schema_version = "1.0";
        public string request_id;
        public string trace_id;
        public ExperienceDraftDto draft;

        public static ExperienceAppendRequest FromDraft(ExperienceDraft value) => new()
        {
            request_id = "experience-request-" + Guid.NewGuid().ToString("N"),
            trace_id = string.IsNullOrWhiteSpace(value?.TraceId)
                ? "experience-trace-" + Guid.NewGuid().ToString("N")
                : ExperienceIds.ProtocolId(value.TraceId, "trace"),
            draft = value == null ? null : ExperienceDraftDto.FromDomain(value),
        };

        [JsonIgnore]
        public bool IsValid => message_type == "experience_append_request" && schema_version == "1.0" &&
                               MemoryProtocolValidation.IsProtocolId(request_id) &&
                               MemoryProtocolValidation.IsProtocolId(trace_id) && draft != null &&
                               MemoryProtocolValidation.IsEntityId(draft.observer_id) &&
                               draft.schema_version == ExperienceDraft.CurrentSchemaVersion;

        public string Serialize() => JsonConvert.SerializeObject(this, Formatting.None);
    }

    internal sealed class ExperienceAppendResult
    {
        public string message_type;
        public string schema_version;
        public string request_id;
        public string trace_id;
        public string event_id;
        public string observer_id;
        public int experience_seq;
        public string status;
        public string reason;
    }

    public sealed class ValidatedExperienceAppendResult
    {
        internal ValidatedExperienceAppendResult(ExperienceAppendResult value)
        {
            RequestId = value.request_id; TraceId = value.trace_id; EventId = value.event_id;
            ObserverId = value.observer_id; ExperienceSequence = value.experience_seq;
            Status = value.status; Reason = value.reason;
        }
        public string RequestId { get; }
        public string TraceId { get; }
        public string EventId { get; }
        public string ObserverId { get; }
        public int ExperienceSequence { get; }
        public string Status { get; }
        public string Reason { get; }
        public bool IsPersisted => Status == "inserted" || Status == "already_exists";
    }

    public static class ExperienceAppendResultValidator
    {
        private static readonly string[] Required =
        {
            "message_type", "schema_version", "request_id", "trace_id", "event_id",
            "observer_id", "experience_seq", "status", "reason",
        };

        public static bool TryParse(string json, out ValidatedExperienceAppendResult validated, out string reason)
        {
            validated = null;
            reason = "INVALID_JSON";
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(json))
                { MaxDepth = 3, DateParseHandling = DateParseHandling.None };
                JObject root = JObject.Load(reader,
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read() || root.Properties().Any(property => !Required.Contains(property.Name)) ||
                    Required.Any(key => root[key] == null)) return Reject("SCHEMA_FIELDS", out reason);
                if (!IsString(root, "message_type") || !IsString(root, "schema_version") ||
                    !IsString(root, "request_id") || !IsString(root, "trace_id") ||
                    !IsString(root, "event_id") || !IsString(root, "observer_id") ||
                    root["experience_seq"].Type != JTokenType.Integer || !IsString(root, "status") ||
                    !IsString(root, "reason")) return Reject("SCHEMA_TYPES", out reason);
                ExperienceAppendResult result = root.ToObject<ExperienceAppendResult>();
                if (result.message_type != "experience_append_result" || result.schema_version != "1.0")
                    return Reject("SCHEMA_VERSION", out reason);
                if (!MemoryProtocolValidation.IsProtocolId(result.request_id) ||
                    !MemoryProtocolValidation.IsProtocolId(result.trace_id) ||
                    !MemoryProtocolValidation.IsOptionalProtocolId(result.event_id) ||
                    !MemoryProtocolValidation.IsOptionalEntityId(result.observer_id) || result.experience_seq < 0)
                    return Reject("CORRELATION_INVALID", out reason);
                if (result.status != "inserted" && result.status != "already_exists" &&
                    result.status != "id_conflict" && result.status != "rejected" &&
                    result.status != "storage_failed") return Reject("STATUS_INVALID", out reason);
                if ((result.reason?.Length ?? 0) > 120 ||
                    ((result.status == "inserted" || result.status == "already_exists") &&
                     (string.IsNullOrWhiteSpace(result.event_id) || result.experience_seq < 1)))
                    return Reject("RESULT_INVALID", out reason);
                validated = new ValidatedExperienceAppendResult(result);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is JsonException || exception is OverflowException ||
                                              exception is FormatException || exception is ArgumentException)
            { return Reject("INVALID_JSON", out reason); }
        }

        private static bool IsString(JObject root, string key) => root[key]?.Type == JTokenType.String;

        private static bool Reject(string value, out string reason) { reason = value; return false; }
    }
}
