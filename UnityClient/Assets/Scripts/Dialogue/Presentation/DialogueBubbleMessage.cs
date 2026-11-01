using System;

namespace EmbodiedNPC.Dialogue.Presentation
{
    public static class DialoguePresentationText
    {
        private const string Pause = "[[PAUSE]]";

        public static string PlainText(string text) => text == null ? string.Empty :
            text.Contains(Pause) ? string.Join("\n", Segments(text)) : text;

        // Persisted speech replaces presentation markers with natural paragraph boundaries.
        // Reuse the same empty-segment and three-segment rules without changing stored messages.
        public static string[] HistorySegments(string text) => Segments(text);

        public static string[] Segments(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return System.Array.Empty<string>();
            // Both live speech and saved history use explicit paragraph boundaries only.
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Pause);
            if (!text.Contains(Pause)) return new[] { text };
            var parts = new System.Collections.Generic.List<string>();
            foreach (string raw in text.Split(new[] { Pause }, System.StringSplitOptions.None))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                // Keep all content even if the model exceeds the three-bubble limit.
                if (parts.Count < 3) parts.Add(part);
                else parts[2] += "\n" + part;
            }
            return parts.ToArray();
        }
    }

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
