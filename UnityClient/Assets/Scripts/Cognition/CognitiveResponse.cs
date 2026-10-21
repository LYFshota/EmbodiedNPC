using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Cognition
{
    public static class CognitiveDecisionKinds
    {
        public const string DirectResponse = "DIRECT_RESPONSE";
        public const string NoAction = "NO_ACTION";
        public const string ActionProposal = "ACTION_PROPOSAL";
        public const string AutonomousActionIntent = "AUTONOMOUS_ACTION_INTENT";
        public const string DailyModeIntent = "DAILY_MODE_INTENT";
    }

    public static class EpisodeDirectives
    {
        public const string Continue = "CONTINUE";
        public const string End = "END";

        public static bool IsValidOptional(string value) => value == null || value == Continue || value == End;
    }

    [Serializable]
    public sealed class CognitiveActionProposal
    {
        public string action_id;
        public Dictionary<string, string> bindings;

        public bool IsValid => IsEntityId(action_id) && bindings != null && bindings.Count > 0 &&
                               bindings.Count <= 16 && bindings.All(pair => IsEntityId(pair.Key)) &&
                               (action_id == "prepare_drink"
                                   ? bindings.Count == 1 && bindings.TryGetValue("drink_type", out string drink) &&
                                     EmbodiedNPC.Actions.DrinkTypes.IsValid(drink)
                                   : bindings.All(pair => IsEntityId(pair.Value)));

        internal static bool IsEntityId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value[0] < 'a' || value[0] > 'z')
                return false;
            foreach (char character in value)
                if (!(character >= 'a' && character <= 'z') && !(character >= '0' && character <= '9') &&
                    character != '_') return false;
            return true;
        }
    }

    [Serializable]
    public sealed class CognitiveDecision
    {
        public string decision_kind;
        public string episode_directive;
        public string dialogue_turn_policy;
        public string utterance;
        public string emotion;
        public string gesture;
        public string look_at;
        public CognitiveActionProposal action_proposal;
        public string target_daily_mode;

        public bool IsValid
        {
            get
            {
                if (decision_kind == CognitiveDecisionKinds.AutonomousActionIntent)
                    return target_daily_mode == null && utterance == null && emotion == null && gesture == null && look_at == null &&
                        episode_directive == null && dialogue_turn_policy == null && action_proposal?.IsValid == true &&
                        action_proposal.action_id == "clean_counter" && action_proposal.bindings.Count == 1 && action_proposal.bindings.ContainsKey("target");
                if (decision_kind == CognitiveDecisionKinds.DailyModeIntent)
                    return target_daily_mode == "work" && utterance == null && emotion == null && gesture == null &&
                        look_at == null && action_proposal == null && episode_directive == null && dialogue_turn_policy == null;
                if (target_daily_mode != null && ((decision_kind != CognitiveDecisionKinds.DirectResponse &&
                    decision_kind != CognitiveDecisionKinds.ActionProposal) ||
                    !CognitiveDailyBehaviorDto.IsMode(target_daily_mode))) return false;
                if (!EpisodeDirectives.IsValidOptional(episode_directive)) return false;
                if (dialogue_turn_policy != null && dialogue_turn_policy != "WAIT_FOR_PLAYER" &&
                    dialogue_turn_policy != "MAY_CONTINUE") return false;
                if (dialogue_turn_policy == "MAY_CONTINUE" &&
                    (decision_kind != CognitiveDecisionKinds.DirectResponse ||
                     episode_directive == EpisodeDirectives.End)) return false;
                if (decision_kind == CognitiveDecisionKinds.DirectResponse)
                    return !string.IsNullOrWhiteSpace(utterance) && ReplyUtteranceContract.Fits(utterance, "en-US") &&
                           IsAllowedEmotion(emotion) && IsAllowedGesture(gesture) &&
                           (look_at == null || CognitiveActionProposal.IsEntityId(look_at)) &&
                           action_proposal == null;
                if (decision_kind == CognitiveDecisionKinds.ActionProposal)
                    return episode_directive != EpisodeDirectives.End &&
                           action_proposal != null && action_proposal.IsValid &&
                           (utterance == null || (!string.IsNullOrWhiteSpace(utterance) && ReplyUtteranceContract.Fits(utterance, "en-US"))) &&
                           IsAllowedEmotion(emotion) && IsAllowedGesture(gesture) &&
                           (look_at == null || CognitiveActionProposal.IsEntityId(look_at));
                return decision_kind == CognitiveDecisionKinds.NoAction && utterance == null &&
                       emotion == null && gesture == null && look_at == null && action_proposal == null;
            }
        }

        private static bool IsAllowedEmotion(string value) =>
            value == null || value == "happy" || value == "neutral" || value == "fear";
        private static bool IsAllowedGesture(string value) =>
            value == null || value == "none" || value == "wave" || value == "nod";
    }

    public static class CognitiveActionProposalAdapter
    {
        public static bool TryMap(CognitiveActionProposal proposal, out GoalProposal goal)
        {
            goal = null;
            if (proposal != null && proposal.IsValid && proposal.action_id == "prepare_drink")
            {
                goal = new GoalProposal { type = "prepare_drink", drink_type = proposal.bindings["drink_type"] };
                return true;
            }
            if (proposal != null && proposal.IsValid && proposal.action_id == "clean_counter" &&
                proposal.bindings.Count == 1 && proposal.bindings.TryGetValue("target", out var counter))
            { goal = new GoalProposal { type = "clean_counter", target_entity_id = counter }; return true; }
            if (proposal == null || !proposal.IsValid || proposal.action_id != "bring_object" ||
                proposal.bindings.Count != 2 || !proposal.bindings.TryGetValue("target", out string target) ||
                !proposal.bindings.TryGetValue("destination", out string destination)) return false;
            goal = new GoalProposal
            {
                type = "bring_object",
                target_entity_id = target,
                destination_entity_id = destination,
            };
            return true;
        }
    }

    [Serializable]
    public sealed class CognitiveResponse
    {
        public string message_type;
        public string schema_version;
        public string request_id;
        public string trace_id;
        public string run_id;
        public string agent_entity_id;
        public CognitiveDecision decision;
        public GoalProposal validated_goal;
        public CognitiveTaskReceiptDto task_receipt;
        public string task_result;
        public TaskExecutionTicketDto task_execution;
        public CapacityStatusDto context_status;

        public bool IsValid => message_type == "cognitive_response" && (schema_version == "1.0" || schema_version == "1.1") &&
                               (schema_version == "1.0" ? task_result == null && task_receipt == null :
                                   task_result == (task_receipt == null ? "none" : task_receipt.committed ? "committed" : "failed")) &&
                               (task_receipt == null || schema_version == "1.1" && task_receipt.IsValid &&
                                (decision?.decision_kind == CognitiveDecisionKinds.DirectResponse || decision?.decision_kind==CognitiveDecisionKinds.NoAction) && decision.target_daily_mode == null) &&
                               !string.IsNullOrWhiteSpace(request_id) && !string.IsNullOrWhiteSpace(trace_id) &&
                               !string.IsNullOrWhiteSpace(run_id) && !string.IsNullOrWhiteSpace(agent_entity_id) &&
                               (context_status == null || context_status.IsValid && context_status.checks.All(c => c.state != "error")) &&
                               decision != null && decision.IsValid && IsValidActionHandoff();

        private bool IsValidActionHandoff()
        {
            if(task_execution!=null && (schema_version!="1.1" || !task_execution.IsValid || task_receipt!=null ||
                decision.decision_kind!=CognitiveDecisionKinds.ActionProposal || decision.target_daily_mode!=null ||
                task_execution.request_id!=request_id || task_execution.run_id!=run_id || task_execution.trace_id!=trace_id ||
                task_execution.agent_entity_id!=agent_entity_id || !task_execution.MatchesProposal(decision.action_proposal))) return false;
            if (decision.decision_kind != CognitiveDecisionKinds.ActionProposal && decision.decision_kind != CognitiveDecisionKinds.AutonomousActionIntent)
                return validated_goal == null;
            return CognitiveActionProposalAdapter.TryMap(decision.action_proposal, out GoalProposal mapped) &&
                   validated_goal != null && validated_goal.type == mapped.type &&
                   validated_goal.target_entity_id == mapped.target_entity_id &&
                   validated_goal.destination_entity_id == mapped.destination_entity_id &&
                   validated_goal.drink_type == mapped.drink_type;
        }
    }

    public sealed class CognitiveTransportFailure
    {
        public CognitiveTransportFailure(string requestId, string runId, string agentEntityId, string reason,
            string traceId = null, string diagnosticCategory = null, CapacityStatusDto contextStatus = null)
        {
            RequestId = requestId;
            RunId = runId;
            AgentEntityId = agentEntityId;
            Reason = reason;
            TraceId = traceId;
            DiagnosticCategory = diagnosticCategory;
            ContextStatus = contextStatus;
        }

        public string RequestId { get; }
        public string RunId { get; }
        public string AgentEntityId { get; }
        public string Reason { get; }
        public string TraceId { get; }
        public string DiagnosticCategory { get; }
        public CapacityStatusDto ContextStatus { get; }
    }

    public static class CognitiveResponseValidator
    {
        private sealed class ErrorEnvelope
        {
            public string message_type;
            public string request_id;
            public string trace_id;
            public string run_id;
            public string agent_entity_id;
            public string reason;
            public string diagnostic_category;
            public CapacityStatusDto context_status;
        }

        private static readonly JsonSerializerSettings Strict = new()
        {
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        public static bool TryParse(string json, out CognitiveResponse response, out string error)
        {
            response = null;
            error = string.Empty;
            try
            {
                // Json.NET can coerce scalar values into strings. Bindings are strict strings on the wire.
                var wire = JObject.Parse(json);
                if (!CapacityStatusDto.StrictShape(wire["context_status"]))
                { error="COGNITIVE_RESPONSE_MALFORMED"; return false; }
                if(wire["task_execution"] is JObject ticket && !TaskExecutionTicketDto.StrictShape(ticket))
                { error="COGNITIVE_RESPONSE_MALFORMED";return false; }
                if (wire["task_receipt"] is JObject receipt)
                {
                    if (receipt["committed"]?.Type != JTokenType.Boolean || receipt["recovered"]?.Type != JTokenType.Boolean ||
                        receipt["task_version"] != null && receipt["task_version"].Type != JTokenType.Null && receipt["task_version"].Type != JTokenType.Integer ||
                        receipt.Properties().Any(p => p.Value.Type != JTokenType.Null &&
                            p.Name != "committed" && p.Name != "recovered" && p.Name != "task_version" &&
                            p.Name != "owner" && p.Name != "player" && p.Value.Type != JTokenType.String) ||
                        new[] { "owner", "player" }.Any(key => receipt[key] is not JObject identity ||
                            identity.Properties().Any(p => p.Value.Type != JTokenType.String)))
                    { error = "COGNITIVE_RESPONSE_MALFORMED"; return false; }
                }
                var actionWire = (wire["decision"] as JObject)?["action_proposal"] as JObject;
                if (actionWire?["bindings"] is JObject bindings &&
                    bindings.Properties().Any(p => p.Value.Type != JTokenType.String))
                { error = "COGNITIVE_RESPONSE_MALFORMED"; return false; }
                response = JsonConvert.DeserializeObject<CognitiveResponse>(json, Strict);
                if (response != null && response.IsValid) return true;
                error = "COGNITIVE_RESPONSE_INVALID";
            }
            catch (JsonException) { error = "COGNITIVE_RESPONSE_MALFORMED"; }
            response = null;
            return false;
        }

        public static bool TryParseFailure(string json, out CognitiveTransportFailure failure)
        {
            failure = null;
            try
            {
                var wire = JObject.Parse(json);
                if (!CapacityStatusDto.StrictShape(wire["context_status"])) return false;
                ErrorEnvelope value = JsonConvert.DeserializeObject<ErrorEnvelope>(json);
                if (wire["context_status"] is JObject capacity)
                    value.context_status = capacity.ToObject<CapacityStatusDto>(JsonSerializer.Create(Strict));
                if (value?.context_status != null && !value.context_status.IsValid) return false;
                if (value?.message_type != "error" || string.IsNullOrWhiteSpace(value.request_id) ||
                    string.IsNullOrWhiteSpace(value.reason)) return false;
                bool actionRejection = string.Equals(value.reason, "INVALID_ACTION_PROPOSAL",
                    StringComparison.Ordinal);
                bool taskRejection=value.reason.StartsWith("TASK_",StringComparison.Ordinal);
                if (!actionRejection && !taskRejection && !value.reason.StartsWith("COGNITIVE_",
                        StringComparison.Ordinal)) return false;
                if ((actionRejection || taskRejection) && (string.IsNullOrWhiteSpace(value.trace_id) ||
                    string.IsNullOrWhiteSpace(value.run_id) ||
                    string.IsNullOrWhiteSpace(value.agent_entity_id))) return false;
                failure = new CognitiveTransportFailure(value.request_id, value.run_id,
                    value.agent_entity_id, value.reason, value.trace_id, value.diagnostic_category, value.context_status);
                return true;
            }
            catch (JsonException) { return false; }
        }

        public static bool TryDescribeMalformedCognitiveResponse(string json,
            out CognitiveTransportFailure failure)
        {
            failure = null;
            try
            {
                JObject value = JObject.Parse(json);
                if ((string)value["message_type"] != "cognitive_response") return false;
                failure = new CognitiveTransportFailure((string)value["request_id"],
                    (string)value["run_id"], (string)value["agent_entity_id"],
                    "COGNITIVE_RESPONSE_MALFORMED", (string)value["trace_id"]);
                return !string.IsNullOrWhiteSpace(failure.RequestId);
            }
            catch (JsonException) { return false; }
        }
    }
}
