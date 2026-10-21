using System;

namespace EmbodiedNPC.Dialogue
{
    /// <summary>Unity-side semantic input. Media details stay in the input adapter.</summary>
    [Serializable]
    public sealed class UserUtterance
    {
        public string requestId;
        public string conversationId;
        public string speakerEntityId;
        public string targetAgentId;
        public string inputMode;
        public string text;

        public bool IsValid => !string.IsNullOrWhiteSpace(conversationId) &&
                               !string.IsNullOrWhiteSpace(speakerEntityId) &&
                               !string.IsNullOrWhiteSpace(targetAgentId) && inputMode == "text" &&
                               !string.IsNullOrWhiteSpace(text) && text.Length <= 500;
    }
}
