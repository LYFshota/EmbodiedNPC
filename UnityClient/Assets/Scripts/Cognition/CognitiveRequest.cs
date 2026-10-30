using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace EmbodiedNPC.Cognition
{
    [Serializable]
    public sealed class CognitiveActionAlternative
    {
        public string alternative_id, description;
        public EmbodiedNPC.Communication.GoalProposal goal;
        public bool IsValid => !string.IsNullOrWhiteSpace(alternative_id) && alternative_id.Length <= 120 &&
            !string.IsNullOrWhiteSpace(description) && description.Length <= 120 && goal != null &&
            goal.type == "bring_object" && CognitiveActionProposal.IsEntityId(goal.target_entity_id) &&
            CognitiveActionProposal.IsEntityId(goal.destination_entity_id);
    }

    [Serializable]
    public sealed class CognitiveActionFeedback
    {
        public string original_request_id, original_run_id, outcome_id, proposal_id, trace_id;
        public int proposal_revision, world_revision, completed_actions;
        public string failure_code, detail;
        public EmbodiedNPC.Communication.GoalProposal failed_goal;
        public CognitiveActionAlternative[] allowed_alternatives = Array.Empty<CognitiveActionAlternative>();
        public bool IsValid => !string.IsNullOrWhiteSpace(original_request_id) &&
            !string.IsNullOrWhiteSpace(original_run_id) && !string.IsNullOrWhiteSpace(outcome_id) &&
            !string.IsNullOrWhiteSpace(proposal_id) && !string.IsNullOrWhiteSpace(trace_id) &&
            proposal_revision >= 0 && proposal_revision < 2 && world_revision >= 0 && completed_actions >= 0 &&
            !string.IsNullOrWhiteSpace(failure_code) && failure_code.Length <= 80 && (detail?.Length ?? 0) <= 240 &&
            failed_goal != null && allowed_alternatives != null && allowed_alternatives.Length <= 4 &&
            allowed_alternatives.All(a => a?.IsValid == true && a.goal.type == failed_goal.type && a.goal.target_entity_id != failed_goal.target_entity_id &&
                a.goal.destination_entity_id == failed_goal.destination_entity_id) &&
            allowed_alternatives.Select(a => a.alternative_id).Distinct(StringComparer.Ordinal).Count() == allowed_alternatives.Length;
    }

    [Serializable]
    public sealed class CognitiveRunDto
    {
        public string run_id;
        public string agent_entity_id;
        public string trigger_id;
        public string trigger_type;
        public double started_at;

        public bool IsValid => !string.IsNullOrWhiteSpace(run_id) &&
                               !string.IsNullOrWhiteSpace(agent_entity_id) &&
                               !string.IsNullOrWhiteSpace(trigger_id) &&
                               !string.IsNullOrWhiteSpace(trigger_type);

        public static CognitiveRunDto From(CognitiveRun run) => run == null ? null : new()
        {
            run_id = run.RunId,
            agent_entity_id = run.AgentEntityId,
            trigger_id = run.TriggerId,
            trigger_type = run.TriggerType,
            started_at = run.StartedAt,
        };
    }

    [Serializable]
    public sealed class CognitiveRecallSubjectIdentityDto
    {
        public string entity_id;
        public string kind;
        public string identity_namespace;
        public string identity_type;
        public string persistent_id;

        public bool IsValid
        {
            get
            {
                bool hasIdentity = !string.IsNullOrWhiteSpace(identity_namespace) &&
                                   !string.IsNullOrWhiteSpace(identity_type) &&
                                   !string.IsNullOrWhiteSpace(persistent_id);
                bool noIdentity = string.IsNullOrEmpty(identity_namespace) &&
                                  string.IsNullOrEmpty(identity_type) && string.IsNullOrEmpty(persistent_id);
                return !string.IsNullOrWhiteSpace(entity_id) && entity_id.Length <= 120 &&
                       (kind == "player" || kind == "npc") && (hasIdentity || noIdentity) &&
                       (noIdentity || identity_namespace.Length <= 80 && identity_type.Length <= 80 &&
                        persistent_id.Length <= 160);
            }
        }

        public static CognitiveRecallSubjectIdentityDto From(WorldEntity entity)
        {
            if (entity == null || entity.EntityKind != EntityKind.Agent ||
                entity.AgentRole != AgentRole.Player && entity.AgentRole != AgentRole.Npc) return null;
            var value = new CognitiveRecallSubjectIdentityDto
            {
                entity_id = entity.EntityId,
                kind = entity.AgentRole == AgentRole.Player ? "player" : "npc",
            };
            if (entity.TryGetPersistentIdentityKey(out PersistentIdentityKey key))
            {
                value.identity_namespace = key.Namespace;
                value.identity_type = key.IdentityType;
                value.persistent_id = key.PersistentId;
            }
            return value;
        }
    }

    [Serializable]
    public sealed class CognitiveRecallIdentityDto
    {
        public CognitiveRecallSubjectIdentityDto observer;
        public string requester_entity_id;
        public CognitiveRecallSubjectIdentityDto[] current_subjects = Array.Empty<CognitiveRecallSubjectIdentityDto>();

        public bool IsValid => observer != null && observer.IsValid && observer.kind == "npc" &&
                               current_subjects != null && current_subjects.Length <= 16 &&
                               current_subjects.All(value => value != null && value.IsValid) &&
                               current_subjects.Select(value => value.entity_id)
                                   .Distinct(StringComparer.Ordinal).Count() == current_subjects.Length;
    }

    /// <summary>Event-capable cognition request; world events do not require text input.</summary>
    [Serializable]
    public sealed class CognitiveRequest
    {
        // Preserve optional nulls while keeping computed validation properties off the wire.
        private sealed class WireFieldsResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType) =>
                objectType.GetFields(BindingFlags.Instance | BindingFlags.Public).Cast<MemberInfo>().ToList();
        }

        private static readonly JsonSerializerSettings WireSettings = new()
        {
            ContractResolver = new WireFieldsResolver(),
            NullValueHandling = NullValueHandling.Include,
        };

        public string Serialize() => JsonConvert.SerializeObject(this, WireSettings);
        internal static string SerializeFields(object value) => JsonConvert.SerializeObject(value,WireSettings);

        public string message_type = "cognitive_request";
        public string schema_version = "1.0";
        public string request_id;
        public string trace_id;
        public CognitiveRunDto run;
        public CognitiveTriggerDto trigger;
        public CognitiveContextSnapshot context;
        // Private Unity-to-Backend identity input. The model prompt serializes context only.
        public CognitiveRecallIdentityDto recall_identity;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public CognitiveActionFeedback action_feedback;
        public string reply_language = "zh-CN";
        public int context_diagnostics_version = 1;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TaskRuntimeContextDto task_runtime;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public TaskConsiderationContextDto task_consideration;

        public bool IsValid => (action_feedback == null || action_feedback.IsValid &&
            schema_version == "1.0" && context?.current_event?.event_type == "action.failed" &&
            context.current_event.event_id == action_feedback.outcome_id && trace_id == action_feedback.trace_id &&
            context.runtime_state?.status == "FAILED" && context.runtime_state.outcome_id == action_feedback.outcome_id &&
            context.runtime_state.proposal_id == action_feedback.proposal_id &&
            context.runtime_state.proposal_revision == action_feedback.proposal_revision &&
            context.runtime_state.trace_id == action_feedback.trace_id && context.runtime_state.world_revision == action_feedback.world_revision &&
            context.current_event.source_entity_id == run?.agent_entity_id && context.current_event.target_entity_id == run?.agent_entity_id) && ReplyUtteranceContract.IsLanguage(reply_language) && message_type == "cognitive_request" &&
                               ((context?.current_event?.event_type == "npc.task_consideration") == (task_consideration!=null)) &&
                               (schema_version == "1.0" && task_runtime == null || schema_version == "1.1" && task_runtime?.IsValid == true) &&
                               !string.IsNullOrWhiteSpace(request_id) && !string.IsNullOrWhiteSpace(trace_id) &&
                               run != null && run.IsValid && trigger != null && trigger.IsValid &&
                               context != null && context.IsValid &&
                               string.Equals(run.run_id, context.run_id, StringComparison.Ordinal) &&
                               string.Equals(run.agent_entity_id, context.agent_entity_id, StringComparison.Ordinal) &&
                               string.Equals(run.trigger_id, trigger.trigger_id, StringComparison.Ordinal) &&
                               string.Equals(run.trigger_type, trigger.trigger_type, StringComparison.Ordinal) &&
                               string.Equals(trigger.trigger_id, context.trigger.trigger_id, StringComparison.Ordinal) &&
                               string.Equals(trigger.trigger_type, context.trigger.trigger_type,
                                   StringComparison.Ordinal) &&
                               string.Equals(trigger.agent_entity_id, run.agent_entity_id,
                                   StringComparison.Ordinal) &&
                               string.Equals(trigger.source_event_id, context.trigger.source_event_id,
                                   StringComparison.Ordinal) &&
                               (recall_identity == null || recall_identity.IsValid &&
                                string.Equals(recall_identity.observer.entity_id, run.agent_entity_id,
                                    StringComparison.Ordinal) &&
                                (string.IsNullOrWhiteSpace(recall_identity.requester_entity_id) ||
                                 context.current_event != null &&
                                 string.Equals(recall_identity.requester_entity_id,
                                     context.current_event.source_entity_id, StringComparison.Ordinal))) &&
                               (context.current_event == null ||
                                string.Equals(trigger.source_event_id, context.current_event.event_id,
                                    StringComparison.Ordinal)) &&
                               (context.current_event == null ||
                                string.IsNullOrWhiteSpace(context.current_event.trace_id) ||
                                string.Equals(trace_id, context.current_event.trace_id,
                                    StringComparison.Ordinal));
    }
}
