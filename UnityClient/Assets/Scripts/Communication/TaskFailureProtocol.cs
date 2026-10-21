using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Communication
{
    /// <summary>
    /// Unity-authoritative semantic failure. The backend may explain it or select one exact alternative;
    /// it cannot add entities, actions, coordinates, or world facts.
    /// </summary>
    [Serializable]
    public sealed class TaskFailureReport
    {
        public string message_type = "task_failure_report";
        public string schema_version = "1.0";
        public string request_id;
        public string trace_id;
        public int world_revision;
        public string proposal_id;
        public int proposal_revision;
        public string failure_stage;
        public string failure_code;
        public string detail;
        public GoalProposal failed_goal;
        public string[] involved_entity_ids = Array.Empty<string>();
        public string reply_language = "zh-CN";
        public EmbodiedNPC.Cognition.CognitiveCharacterDto character;
        public bool retryable;
        public TaskAlternative[] allowed_alternatives = Array.Empty<TaskAlternative>();

        public string Serialize() => EmbodiedNPC.Cognition.CognitiveRequest.SerializeFields(this);

        public bool IsValid => (character == null || character.IsValid) &&
                               EmbodiedNPC.Cognition.ReplyUtteranceContract.IsLanguage(reply_language) && message_type == "task_failure_report" && schema_version == "1.0" &&
                               !string.IsNullOrWhiteSpace(request_id) && !string.IsNullOrWhiteSpace(trace_id) &&
                               world_revision >= 0 && IsIdentifier(proposal_id) &&
                               proposal_revision >= 0 && proposal_revision < 2 &&
                               IsFailureStage(failure_stage) && !string.IsNullOrWhiteSpace(failure_code) &&
                               failure_code.Length <= 80 && (detail?.Length ?? 0) <= 240 &&
                               failed_goal != null && failed_goal.type == "bring_object" &&
                               IsEntityId(failed_goal.target_entity_id) &&
                               IsEntityId(failed_goal.destination_entity_id) && involved_entity_ids != null &&
                               involved_entity_ids.Length <= 16 && involved_entity_ids.All(IsEntityId) &&
                               allowed_alternatives != null && allowed_alternatives.Length <= 8 &&
                               allowed_alternatives.All(alternative => alternative != null && alternative.IsValid) &&
                               allowed_alternatives.Select(value => value.alternative_id).Distinct().Count() ==
                               allowed_alternatives.Length;

        internal static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 120) return false;
            return value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');
        }

        internal static bool IsEntityId(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] < 'a' || value[0] > 'z') return false;
            return value.Skip(1).All(character => char.IsLower(character) || char.IsDigit(character) || character == '_');
        }

        private static bool IsFailureStage(string value) =>
            value == "proposal_validation" || value == "planning" || value == "execution";
    }

    [Serializable]
    public sealed class TaskAlternative
    {
        public string alternative_id;
        public string description;
        public GoalProposal goal;

        public bool IsValid => TaskFailureReport.IsIdentifier(alternative_id) &&
                               !string.IsNullOrWhiteSpace(description) && description.Length <= 120 &&
                               goal != null && goal.type == "bring_object" &&
                               TaskFailureReport.IsEntityId(goal.target_entity_id) &&
                               TaskFailureReport.IsEntityId(goal.destination_entity_id);
    }

    [Serializable]
    internal sealed class TaskFailureResponse
    {
        public string message_type;
        public string schema_version;
        public string request_id;
        public string trace_id;
        public int world_revision;
        public string proposal_id;
        public int proposal_revision;
        public string resolution;
        public string utterance;
        public string emotion;
        public string gesture;
        public string selected_alternative_id;
    }

    /// <summary>Validated response plus the exact Unity-authored Goal selected by ID.</summary>
    public sealed class ValidatedTaskFailureResponse
    {
        internal ValidatedTaskFailureResponse(TaskFailureResponse response, GoalProposal selectedGoal)
        {
            RequestId = response.request_id;
            TraceId = response.trace_id;
            WorldRevision = response.world_revision;
            ProposalId = response.proposal_id;
            ProposalRevision = response.proposal_revision;
            Resolution = response.resolution;
            Utterance = response.utterance;
            Emotion = response.emotion;
            Gesture = response.gesture;
            SelectedGoal = selectedGoal == null ? null : new GoalProposal
            {
                type = selectedGoal.type,
                target_entity_id = selectedGoal.target_entity_id,
                destination_entity_id = selectedGoal.destination_entity_id,
            };
        }

        public string RequestId { get; }
        public string TraceId { get; }
        public int WorldRevision { get; }
        public string ProposalId { get; }
        public int ProposalRevision { get; }
        public string Resolution { get; }
        public string Utterance { get; }
        public string Emotion { get; }
        public string Gesture { get; }
        public GoalProposal SelectedGoal { get; }
    }

    /// <summary>Single-use correlation and allowlist validation for semantic failure responses.</summary>
    public sealed class TaskFailureResponseInbox
    {
        private readonly Dictionary<string, Ticket> pending = new(StringComparer.Ordinal);
        private readonly Func<double> clock;

        public event Action<string> RequestInvalidated;

        public TaskFailureResponseInbox(Func<double> clock) => this.clock = clock;

        public int PendingCount { get { Expire(); return pending.Count; } }

        public bool Register(TaskFailureReport report, double timeoutSeconds = 30)
        {
            Expire();
            if (report == null || !report.IsValid || timeoutSeconds <= 0 || pending.Count >= 16 ||
                pending.ContainsKey(report.request_id)) return false;
            pending.Add(report.request_id, new Ticket(report, clock() + timeoutSeconds));
            return true;
        }

        public bool TryAccept(string json, int currentWorldRevision, out ValidatedTaskFailureResponse result,
            out string code)
        {
            result = null;
            if (!TryParse(json, out TaskFailureResponse response, out code)) return false;
            Expire();
            if (!pending.TryGetValue(response.request_id, out Ticket ticket))
                return Reject("REQUEST_NOT_PENDING", out code);
            if (!EmbodiedNPC.Cognition.ReplyUtteranceContract.Fits(response.utterance, ticket.Report.reply_language))
                return Reject("UTTERANCE_INVALID", out code);
            if (ticket.Report.trace_id != response.trace_id || ticket.Report.proposal_id != response.proposal_id)
                return Reject("CORRELATION_MISMATCH", out code);
            if (ticket.Report.world_revision != response.world_revision || currentWorldRevision != response.world_revision)
                return Reject("STALE_WORLD_REVISION", out code);
            if (response.proposal_revision != ticket.Report.proposal_revision + 1 || response.proposal_revision > 2)
                return Reject("PROPOSAL_REVISION_INVALID", out code);

            GoalProposal selectedGoal = null;
            if (response.resolution == "revise")
            {
                TaskAlternative selected = ticket.Report.allowed_alternatives.FirstOrDefault(
                    alternative => alternative.alternative_id == response.selected_alternative_id);
                if (selected == null) return Reject("ALTERNATIVE_NOT_ALLOWED", out code);
                selectedGoal = selected.goal;
            }
            else if (!string.IsNullOrEmpty(response.selected_alternative_id))
            {
                return Reject("UNEXPECTED_ALTERNATIVE", out code);
            }

            pending.Remove(response.request_id);
            result = new ValidatedTaskFailureResponse(response, selectedGoal);
            code = string.Empty;
            return true;
        }

        public void Cancel(string requestId)
        {
            if (requestId != null && pending.Remove(requestId)) RequestInvalidated?.Invoke(requestId);
        }

        public void Clear()
        {
            string[] requestIds = pending.Keys.ToArray();
            pending.Clear();
            foreach (string requestId in requestIds) RequestInvalidated?.Invoke(requestId);
        }

        public void Expire()
        {
            List<string> expired = pending.Where(pair => clock() >= pair.Value.Deadline)
                .Select(pair => pair.Key).ToList();
            foreach (string key in expired) Cancel(key);
        }

        private static bool TryParse(string json, out TaskFailureResponse response, out string code)
        {
            response = null;
            code = "INVALID_JSON";
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(json))
                {
                    MaxDepth = 4,
                    DateParseHandling = DateParseHandling.None,
                };
                JObject root = JObject.Load(reader,
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) return false;
                string[] required = { "message_type", "schema_version", "request_id", "trace_id", "world_revision",
                    "proposal_id", "proposal_revision", "resolution", "utterance", "emotion", "gesture",
                    "selected_alternative_id" };
                if (root.Properties().Any(property => !required.Contains(property.Name)) ||
                    required.Any(key => root[key] == null)) return Reject("SCHEMA_FIELDS", out code);
                response = root.ToObject<TaskFailureResponse>();
                if (response.message_type != "task_failure_response" || response.schema_version != "1.0")
                    return Reject("SCHEMA_VERSION", out code);
                if (string.IsNullOrWhiteSpace(response.request_id) || string.IsNullOrWhiteSpace(response.trace_id) ||
                    response.world_revision < 0 || !TaskFailureReport.IsIdentifier(response.proposal_id) ||
                    response.proposal_revision < 1 || response.proposal_revision > 2)
                    return Reject("CORRELATION_INVALID", out code);
                if (response.resolution != "explain" && response.resolution != "clarify" &&
                    response.resolution != "revise") return Reject("RESOLUTION_INVALID", out code);
                if (string.IsNullOrWhiteSpace(response.utterance) || !EmbodiedNPC.Cognition.ReplyUtteranceContract.Fits(response.utterance, "en-US"))
                    return Reject("UTTERANCE_INVALID", out code);
                if (response.emotion != "happy" && response.emotion != "neutral" && response.emotion != "fear")
                    return Reject("EMOTION_INVALID", out code);
                if (response.gesture != "none" && response.gesture != "wave" && response.gesture != "nod")
                    return Reject("GESTURE_INVALID", out code);
                return true;
            }
            catch (Exception exception) when (exception is JsonException || exception is OverflowException ||
                                              exception is FormatException || exception is ArgumentException)
            {
                return Reject("INVALID_JSON", out code);
            }
        }

        private static bool Reject(string value, out string code) { code = value; return false; }

        private readonly struct Ticket
        {
            public Ticket(TaskFailureReport report, double deadline) { Report = report; Deadline = deadline; }
            public TaskFailureReport Report { get; }
            public double Deadline { get; }
        }
    }
}
