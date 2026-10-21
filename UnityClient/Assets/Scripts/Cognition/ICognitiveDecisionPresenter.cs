using System;
using EmbodiedNPC.Communication;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Unity-local correlation only, not another backend protocol.</summary>
    public readonly struct CognitivePresentationContext
    {
        private readonly string[] attentionCandidates;
        private readonly string relevantPlayerId;
        public CognitivePresentationContext(CognitiveRequest request, string playerId, string conversationId)
        {
            RequestId = request.request_id; TraceId = request.trace_id; RunId = request.run.run_id;
            OwnerAgentEntityId = request.run.agent_entity_id; PlayerEntityId = playerId;
            ConversationId = conversationId;
            relevantPlayerId = request.context?.relevant_player?.player_entity_id;
            PerceivedEntityDto[] entities = request.context?.perception?.entities;
            attentionCandidates = new string[entities?.Length ?? 0];
            for (int i = 0; i < attentionCandidates.Length; i++) attentionCandidates[i] = entities[i]?.entity_id;
        }
        public string RequestId { get; }
        public string TraceId { get; }
        public string RunId { get; }
        public string OwnerAgentEntityId { get; }
        public string PlayerEntityId { get; }
        public string ConversationId { get; }
        public bool AllowsAttentionTarget(string id) => !string.IsNullOrWhiteSpace(id) &&
            (string.Equals(relevantPlayerId, id, StringComparison.Ordinal) ||
             attentionCandidates != null && Array.IndexOf(attentionCandidates, id) >= 0);
    }

    /// <summary>Called by the owning Driver only, after action admission where applicable.</summary>
    public interface ICognitiveDecisionPresenter : ICognitiveResponsePresenter
    {
        bool TryPresentCognitiveDecision(CognitiveDecision decision, CognitivePresentationContext context);
    }
}
