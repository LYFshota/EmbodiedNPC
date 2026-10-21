using System;

namespace EmbodiedNPC.Dialogue
{
    /// <summary>Input-time dialogue target selected by a later DialogueController.</summary>
    [Serializable]
    public sealed class DialogueContext
    {
        public string conversationId;
        public string speakerEntityId;
        public string targetAgentId;

        public bool IsValid => !string.IsNullOrWhiteSpace(conversationId)
                               && !string.IsNullOrWhiteSpace(speakerEntityId)
                               && !string.IsNullOrWhiteSpace(targetAgentId);
    }
}
