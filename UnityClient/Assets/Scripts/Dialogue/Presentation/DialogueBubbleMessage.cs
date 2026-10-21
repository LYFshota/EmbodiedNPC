using System;

namespace EmbodiedNPC.Dialogue.Presentation
{
    public enum DialogueSpeakerKind
    {
        Player,
        Npc,
    }

    /// <summary>Pure presentation data; it intentionally carries no runtime or world-object references.</summary>
    public readonly struct DialogueBubbleMessage
    {
        public DialogueBubbleMessage(string messageId, string speakerEntityId, string text,
            double createdAt, long sequence, DialogueSpeakerKind speakerKind)
        {
            MessageId = messageId ?? string.Empty;
            SpeakerEntityId = speakerEntityId ?? string.Empty;
            Text = text ?? string.Empty;
            CreatedAt = createdAt;
            Sequence = sequence;
            SpeakerKind = speakerKind;
        }

        public string MessageId { get; }
        public string SpeakerEntityId { get; }
        public string Text { get; }
        public double CreatedAt { get; }
        public long Sequence { get; }
        public DialogueSpeakerKind SpeakerKind { get; }
        public bool IsValid => !string.IsNullOrWhiteSpace(MessageId) &&
                               !string.IsNullOrWhiteSpace(SpeakerEntityId) &&
                               !string.IsNullOrWhiteSpace(Text);
    }
}
