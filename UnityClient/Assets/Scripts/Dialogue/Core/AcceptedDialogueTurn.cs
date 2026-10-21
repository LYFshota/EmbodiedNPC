namespace EmbodiedNPC.Dialogue
{
    /// <summary>Internal evidence emitted only after a dialogue input or response crosses its acceptance boundary.</summary>
    public sealed class AcceptedDialogueTurn
    {
        public AcceptedDialogueTurn(bool isResponse, string requestId, string traceId, string conversationId,
            string speakerEntityId, string targetAgentId, string text, int worldRevision,
            double occurredAt, double observedAt, string sourceEventId = null, string episodeDirective = null)
        {
            IsResponse = isResponse; RequestId = requestId; TraceId = traceId;
            ConversationId = conversationId; SpeakerEntityId = speakerEntityId;
            TargetAgentId = targetAgentId; Text = text; WorldRevision = worldRevision;
            OccurredAt = occurredAt; ObservedAt = observedAt; SourceEventId = sourceEventId;
            EpisodeDirective = episodeDirective;
        }
        public bool IsResponse { get; }
        public string RequestId { get; }
        public string TraceId { get; }
        public string ConversationId { get; }
        public string SpeakerEntityId { get; }
        public string TargetAgentId { get; }
        public string Text { get; }
        public int WorldRevision { get; }
        public double OccurredAt { get; }
        public double ObservedAt { get; }
        public string SourceEventId { get; }
        public string EpisodeDirective { get; }
    }
}
