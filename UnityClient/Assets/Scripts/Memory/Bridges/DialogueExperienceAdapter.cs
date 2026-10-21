using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.World;
using EmbodiedNPC.Events;

namespace EmbodiedNPC.Memory
{
    public sealed class DialogueExperienceAdapter
    {
        private readonly Action<ExperienceDraft> sink;
        private readonly EntityRegistry entityRegistry;
        private readonly HashSet<string> emoteReplies = new(StringComparer.Ordinal);
        public DialogueExperienceAdapter(Action<ExperienceDraft> sink, EntityRegistry entityRegistry = null)
        {
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.entityRegistry = entityRegistry;
        }

        public void Handle(AcceptedDialogueTurn turn)
        {
            if (turn == null) return;
            try { sink(Create(turn, entityRegistry)); }
            catch (Exception) { /* Experience persistence cannot change dialogue acceptance. */ }
        }

        public void Handle(PresentedCognitiveUtterance presented)
        {
            if (presented?.SourceEvent?.event_type == AgentEventTypes.PlayerEmote)
            {
                string key = presented.OwnerAgentEntityId + ":" + presented.SourceEventId;
                if (emoteReplies.Contains(key)) return;
                ExperienceDraft draft = CreateEmoteReply(presented, entityRegistry);
                if (draft == null) return;
                try { sink(draft); emoteReplies.Add(key); } catch (Exception) { }
                return;
            }
            if (presented == null || !presented.IsDialogueEvidence) return;
            var turn = new AcceptedDialogueTurn(true, presented.RequestId, presented.TraceId,
                presented.ConversationId, presented.PlayerEntityId, presented.OwnerAgentEntityId,
                presented.Utterance, presented.WorldRevision, presented.PresentedAt,
                presented.PresentedAt, presented.SourceEventId, presented.EpisodeDirective);
            try { sink(Create(turn, entityRegistry, presented.ContinuationEvent)); }
            catch (Exception) { /* Persistence cannot change already completed presentation. */ }
        }

        public static ExperienceDraft CreateEmoteReply(PresentedCognitiveUtterance shown, EntityRegistry registry = null)
        {
            var source = shown?.SourceEvent;
            if (source == null || source.event_type != AgentEventTypes.PlayerEmote || source.event_id != shown.SourceEventId ||
                string.IsNullOrWhiteSpace(source.source_observation_id) || string.IsNullOrWhiteSpace(shown.TraceId) ||
                string.IsNullOrWhiteSpace(shown.RunId) || string.IsNullOrWhiteSpace(shown.RequestId) ||
                string.IsNullOrWhiteSpace(shown.PlayerEntityId) || string.IsNullOrWhiteSpace(shown.Utterance)) return null;
            var fields = new Dictionary<string, string>();
            foreach (var pair in source.payload) fields[pair.key] = pair.value;
            if (!fields.TryGetValue(AgentEventPayloadKeys.SocialObserver, out string observer) || observer != shown.OwnerAgentEntityId ||
                !fields.TryGetValue(AgentEventPayloadKeys.SocialResponseEligible, out string eligible) || eligible != "true" ||
                !fields.TryGetValue(AgentEventPayloadKeys.EmoteId, out string emote) ||
                (emote != "wave" && emote != "cheer" && emote != "flex") || source.source_entity_id != shown.PlayerEntityId) return null;
            var targets = new[] { shown.PlayerEntityId };
            return new ExperienceDraft(shown.OwnerAgentEntityId, "social_response", "social_interaction",
                ClientSessionIdentity.Current, "unity_realtime", ExperienceIds.ProtocolId(shown.SourceEventId, "observation_source"),
                new[] { ExperienceIds.ProtocolId(source.source_observation_id, "observation") }, shown.OwnerAgentEntityId, targets,
                new Dictionary<string, object> { ["parent_source_event_id"] = ExperienceIds.ProtocolId(shown.SourceEventId, "observation_source"),
                    ["emote_id"] = emote, ["run_id"] = shown.RunId, ["trace_id"] = shown.TraceId, ["actually_presented"] = true,
                    ["model_content_is_not_world_truth"] = true }, shown.Utterance,
                shown.PresentedAt, shown.PresentedAt, shown.WorldRevision, ExperienceIds.ProtocolId(shown.RequestId, "request"),
                ExperienceIds.ProtocolId(shown.TraceId, "trace"), null, null, "EXECUTED_BY_SELF",
                PersistentParticipantIdentityResolver.ResolveParticipants(registry, shown.OwnerAgentEntityId, targets));
        }

        public static ExperienceDraft Create(AcceptedDialogueTurn turn, EntityRegistry entityRegistry = null,
            CognitiveEventDto continuationEvent = null)
        {
            string observer = turn.TargetAgentId;
            string actor = turn.IsResponse ? turn.TargetAgentId : turn.SpeakerEntityId;
            string target = turn.IsResponse ? turn.SpeakerEntityId : turn.TargetAgentId;
            var payload = new Dictionary<string, object>
            {
                ["direction"] = turn.IsResponse ? "npc_to_player" : "player_to_npc",
                ["input_mode"] = "text",
                ["speaker_entity_id"] = actor,
                ["model_content_is_not_world_truth"] = turn.IsResponse,
            };
            if (turn.IsResponse && !string.IsNullOrWhiteSpace(turn.EpisodeDirective))
                payload["episode_directive"] = turn.EpisodeDirective;
            if (continuationEvent != null)
            {
                payload["initiative"] = "dialogue_continuation";
                payload["continuation_index"] = 1;
                foreach (var pair in continuationEvent.payload)
                {
                    if (pair.key == "parent_request_id" || pair.key == "parent_event_id" ||
                        pair.key == "last_response_event_id") payload[pair.key] = pair.value;
                    if (pair.key == "last_response_seq") payload[pair.key] = int.Parse(pair.value);
                }
            }
            string sourceEventId = string.IsNullOrWhiteSpace(turn.SourceEventId)
                ? ExperienceIds.ProtocolId(turn.RequestId, "dialogue")
                : ExperienceIds.ProtocolId(turn.SourceEventId, "dialogue");
            return new ExperienceDraft(observer,
                turn.IsResponse ? "dialogue_response" : "dialogue_input", "dialogue",
                ClientSessionIdentity.Current, "unity_realtime",
                sourceEventId, Array.Empty<string>(), actor,
                new[] { target }, payload, turn.Text, turn.OccurredAt, turn.ObservedAt,
                turn.WorldRevision, ExperienceIds.ProtocolId(turn.RequestId, "request"),
                ExperienceIds.ProtocolId(turn.TraceId, "trace"), turn.ConversationId, null,
                turn.IsResponse ? "EXECUTED_BY_SELF" : "SYSTEM_CONFIRMED",
                PersistentParticipantIdentityResolver.ResolveParticipants(entityRegistry, actor, new[] { target }));
        }
    }
}
