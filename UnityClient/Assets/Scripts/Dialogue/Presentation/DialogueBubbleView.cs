using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [Serializable]
    public sealed class DialogueBubbleVisualStyle
    {
        public Color backgroundColor = new Color(0.08f, 0.10f, 0.13f, 0.92f);
        public Color textColor = Color.white;
        [Min(10f)] public float fontSize = 25f;
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class DialogueBubbleView : MonoBehaviour
    {
        [SerializeField] private Image background;
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private Vector2 padding = new Vector2(28f, 18f);
        [SerializeField, Min(80f)] private float minimumWidth = 120f;
        [SerializeField, Min(100f)] private float maximumWidth = 520f;

        public DialogueBubbleMessage Message { get; private set; }
        public bool CanPresentText => messageText != null;
        public TMP_FontAsset Font => messageText == null ? null : messageText.font;
        public RectTransform RectTransform => (RectTransform)transform;
        public Rect ScreenRect { get; internal set; }

        public void Configure(Image image, TMP_Text text)
        {
            background = image;
            messageText = text;
        }

        public void SetMessage(DialogueBubbleMessage message, DialogueBubbleVisualStyle style)
        {
            Message = message;
            if (messageText == null) return;
            messageText.text = message.Text;
            messageText.textWrappingMode = TextWrappingModes.Normal;
            messageText.enableAutoSizing = false;
            messageText.characterSpacing = 0;
            messageText.wordSpacing = 0;
            messageText.alignment = TextAlignmentOptions.TopLeft;
            messageText.fontSize = style.fontSize;
            messageText.color = style.textColor;
            if (background != null) background.color = style.backgroundColor;

            // Measure once without wrapping, then clamp. Shrinking to a wrapped line's
            // fractional width can reflow the last word again at the final width.
            float textWidth = messageText.GetPreferredValues(message.Text, float.PositiveInfinity, 0f).x;
            float width = Mathf.Clamp(Mathf.Ceil(textWidth) + padding.x + 2, minimumWidth, maximumWidth);
            float height = Mathf.Ceil(messageText.GetPreferredValues(message.Text, width - padding.x, 0f).y) + padding.y;
            RectTransform.sizeDelta = new Vector2(width, Mathf.Max(48f, height));
        }
    }
}
