using System;
using System.Collections.Generic;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Perception;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    public sealed class ChatHistoryMessage
    {
        public long Sequence;
        public long DisplayOrder;
        public double Timestamp;
        public string Speaker, Text;
        public bool IsPlayer;
        public string ObserverEntityId;
        public string ConversationId, EventId, TraceId;
        // UI-only provenance. Never submitted as dialogue_input or read as a cognition prompt.
        public Observation EmoteObservation;
        public string RequestId, RunId;
    }

    /// <summary>Presentation only: keeps the existing input, send and cancel listeners.</summary>
    internal sealed class ChatHudLayout
    {
        private readonly RectTransform bar, drawer;
        private readonly ScrollRect scroll;
        private readonly TMP_Text legacyText;
        private readonly Button historyButton;
        private readonly TMP_FontAsset font;
        private TMP_Text title, placeholder;
        private ChatHudIcon outline;
        private ChatHudIcon body;
        private RectTransform cancel;
        private readonly List<GameObject> rows = new();
        private ChatHistoryMessageView messageTemplate;
        private static readonly Color Cream = new Color32(249, 244, 213, 255);
        private static readonly Color Ink = new Color32(95, 79, 56, 255);
        private static readonly Color Gold = new Color32(240, 207, 112, 255);

        public ChatHudLayout(Transform root, GameObject panel, ScrollRect scrollView, TMP_Text text, Button button)
        {
            bar = (RectTransform)root;
            drawer = (RectTransform)panel.transform;
            scroll = scrollView;
            legacyText = text;
            historyButton = button;
            font = text.font;
        }
        private static RectTransform Rect(Transform t, Vector2 min, Vector2 max, Vector2 lo, Vector2 hi)
        {
            var r = (RectTransform)t;
            r.anchorMin = min; r.anchorMax = max; r.pivot = new Vector2(.5f, .5f);
            r.offsetMin = lo; r.offsetMax = hi;
            return r;
        }
        private static GameObject New(string name, Transform parent, params Type[] types)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            foreach (var type in types) go.AddComponent(type);
            return go;
        }
        private TMP_Text Text(string name, Transform parent, float size, Color color)
        {
            var t = New(name, parent, typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            t.font = font; t.fontSize = size; t.color = color;
            t.richText = false; t.raycastTarget = false;
            return t;
        }
        public void Build(Action toggle)
        {
            foreach (var effect in bar.GetComponentsInChildren<Outline>(true)) effect.enabled = false;
            // Drawer remains inside the existing dialogue UI hit-test root.
            drawer.SetParent(bar.parent, false);
            drawer.SetSiblingIndex(bar.GetSiblingIndex());
            Rect(drawer, new Vector2(.02f, .065f), new Vector2(.33f, .94f), Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(drawer.GetComponent<Image>(), new Color(.91f, .90f, .80f, .96f));
            title = drawer.Find("Title").GetComponent<TMP_Text>();
            Rect(title.transform, new Vector2(0, 1), Vector2.one, new Vector2(26, -66), new Vector2(-62, -22));
            title.fontSize = 28; title.fontStyle = FontStyles.Bold; title.color = Ink;
            var close = New("CloseHistory", drawer, typeof(Image), typeof(Button));
            Rect(close.transform, Vector2.one, Vector2.one, new Vector2(-57, -61), new Vector2(-19, -23));
            SettingsPanelTheme.Surface(close.GetComponent<Image>(), Gold);
            close.GetComponent<Button>().onClick.AddListener(() => toggle());
            var cross = Text("Label", close.transform, 27, Ink);
            cross.text = "×"; cross.alignment = TextAlignmentOptions.Center;
            Rect(cross.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var line = New("HeaderRule", drawer, typeof(Image));
            Rect(line.transform, new Vector2(0, 1), Vector2.one, new Vector2(26, -78), new Vector2(-26, -76));
            line.GetComponent<Image>().color = new Color(.55f, .50f, .35f, .18f);
            var status = drawer.Find("Status").GetComponent<TMP_Text>();
            status.color = Ink; status.fontSize = 17;
            Rect(status.transform, Vector2.zero, new Vector2(1, 0), new Vector2(26, 110), new Vector2(-26, 138));
            Rect(scroll.transform, Vector2.zero, Vector2.one, new Vector2(26, 145), new Vector2(-20, -94));
            scroll.GetComponent<Image>().color = Color.clear;
            scroll.viewport.GetComponent<Image>().color = Color.clear;
            Rect(scroll.viewport, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-14, 0));
            SettingsPanelTheme.Surface(scroll.verticalScrollbar.GetComponent<Image>(), new Color(.6f, .56f, .43f, .08f));
            SettingsPanelTheme.Surface(scroll.verticalScrollbar.handleRect.GetComponent<Image>(), new Color(.6f, .56f, .43f, .35f));
            legacyText.enabled = false;
            legacyText.richText = false;
            var bg = bar.Find("Background").GetComponent<Image>();
            bg.color = Color.clear;
            var ring = New("ChatOutline", bg.transform, typeof(ChatHudIcon));
            Rect(ring.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            outline = ring.GetComponent<ChatHudIcon>();
            outline.IsOutline = true; outline.color = Gold;
            var inner = New("ChatGlass", bg.transform, typeof(ChatHudIcon));
            Rect(inner.transform, Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, -4));
            body = inner.GetComponent<ChatHudIcon>();
            body.IsCapsuleBackground = true;
            body.color = new Color(.27f, .26f, .24f, .76f);
            body.raycastTarget = false;
            outline.raycastTarget = false;
            var input = bar.Find("TMP_InputField").GetComponent<TMP_InputField>();
            Rect(input.transform, Vector2.zero, Vector2.one, new Vector2(24, 10), new Vector2(-110, -10));
            input.GetComponent<Image>().color = Color.clear;
            input.textComponent.color = Cream; input.textComponent.fontSize = 24;
            input.customCaretColor = true; input.caretColor = Cream;
            input.selectionColor = new Color(1, .88f, .52f, .35f);
            placeholder = input.placeholder as TMP_Text;
            placeholder.color = new Color(Cream.r, Cream.g, Cream.b, .65f);
            placeholder.fontStyle = FontStyles.Normal; placeholder.fontSize = 24;
            var send = bar.Find("SendButton").GetComponent<Button>();
            Rect(send.transform, new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(-100, -30), new Vector2(-12, 30));
            SettingsPanelTheme.Surface(send.GetComponent<Image>(), Cream);
            send.GetComponentInChildren<TMP_Text>().text = string.Empty;
            var plane = New("SendIcon", send.transform, typeof(ChatHudIcon));
            Rect(plane.transform, Vector2.zero, Vector2.one, new Vector2(24, 13), new Vector2(-24, -13));
            plane.GetComponent<ChatHudIcon>().color = Ink;
            plane.GetComponent<ChatHudIcon>().raycastTarget = false;
            cancel = (RectTransform)bar.Find("CancelButton");
            SettingsPanelTheme.Surface(cancel.GetComponent<Image>(), new Color(.25f, .24f, .21f, .65f));
            var cancelText = cancel.GetComponentInChildren<TMP_Text>();
            cancelText.text = "×"; cancelText.fontSize = 28; cancelText.color = Cream;
            Rect(cancel, new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(12, -21), new Vector2(54, 21));
            Rect(historyButton.transform, new Vector2(0, .5f), new Vector2(0, .5f), new Vector2(-70, -28), new Vector2(-14, 28));
            SettingsPanelTheme.Surface(historyButton.GetComponent<Image>(), new Color(.25f, .24f, .21f, .65f));
            foreach (var image in historyButton.GetComponentsInChildren<Image>())
                if (image.gameObject != historyButton.gameObject) image.color = Cream;
            RefreshLanguage();
            SetExpanded(false);
        }
        public void RefreshLanguage()
        {
            title.text = SettingsUiLanguage.T("聊天记录", "Chat history");
            placeholder.text = SettingsUiLanguage.T("输入聊天信息", "Enter a message");
        }
        public void Bind(Action toggle, ChatHistoryMessageView template)
        {
            messageTemplate = template;
            title = drawer.Find("Title").GetComponent<TMP_Text>();
            placeholder = bar.Find("TMP_InputField").GetComponent<TMP_InputField>().placeholder as TMP_Text;
            outline = bar.Find("Background/ChatOutline").GetComponent<ChatHudIcon>();
            body = bar.Find("Background/ChatGlass").GetComponent<ChatHudIcon>();
            cancel = (RectTransform)bar.Find("CancelButton");
            drawer.Find("CloseHistory").GetComponent<Button>().onClick.AddListener(() => toggle());
            RefreshLanguage();
        }
        public void SetExpanded(bool expanded)
        {
            Rect(bar, new Vector2(expanded ? .033f : .30f, 0), new Vector2(expanded ? .317f : .78f, 0),
                new Vector2(0, expanded ? 85 : 28), new Vector2(0, expanded ? 177 : 112));
            historyButton.gameObject.SetActive(!expanded);
            cancel.gameObject.SetActive(!expanded);
            outline.color = expanded ? new Color(Gold.r, Gold.g, Gold.b, 0) : Gold;
        }
        public void Render(IReadOnlyList<ChatHistoryMessage> messages)
        {
            foreach (var row in rows) { row.SetActive(false); UnityEngine.Object.Destroy(row); }
            rows.Clear();
            float width = scroll.content.rect.width;
            float bubbleWidth = width * .89f;
            float y = 4;
            foreach (var message in messages)
            {
                string[] segments = message.IsPlayer ? new[] { message.Text } : DialoguePresentationText.HistorySegments(message.Text);
                for (int segment = 0; segment < segments.Length; segment++)
                {
                    bool first = segment == 0;
                    float heading = first ? 35 : 0;
                    float gap = segment == segments.Length - 1 ? 18 : 6;
                    if (messageTemplate != null)
                    {
                        var view = UnityEngine.Object.Instantiate(messageTemplate, scroll.content);
                        view.gameObject.SetActive(true);
                        rows.Add(view.gameObject);
                        float messageHeight = view.Present(message, bubbleWidth, segments[segment], first);
                        Rect(view.transform, new Vector2(0,1), Vector2.one,
                            new Vector2(0,-y-messageHeight-heading), new Vector2(0,-y));
                        y += messageHeight + heading + gap;
                        continue;
                    }
                    var row = New("ChatMessage", scroll.content);
                    rows.Add(row);
                    var speaker = Text("Speaker", row.transform, 21,
                        message.IsPlayer ? Ink : new Color(.47f, .60f, .29f));
                    speaker.gameObject.SetActive(first);
                    speaker.text = message.IsPlayer ? SettingsUiLanguage.T("我", "Me") :
                        SettingsUiLanguage.NpcDisplayName(message.ObserverEntityId, message.Speaker);
                    speaker.fontStyle = FontStyles.Bold;
                    speaker.alignment = message.IsPlayer ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;
                    Rect(speaker.transform, new Vector2(0, 1), Vector2.one, new Vector2(2, -29), new Vector2(-2, -1));
                    var bubble = New("Bubble", row.transform, typeof(Image));
                    var text = Text("Message", bubble.transform, 23, Ink);
                    text.text = segments[segment];
                    text.alignment = TextAlignmentOptions.TopLeft;
                    text.textWrappingMode = TextWrappingModes.Normal;
                    text.overflowMode = TextOverflowModes.Overflow;
                    float height = Mathf.Max(34, text.GetPreferredValues(segments[segment], bubbleWidth - 32, 0).y) + 24;
                    Rect(bubble.transform, new Vector2(message.IsPlayer ? 1 : 0, 1), new Vector2(message.IsPlayer ? 1 : 0, 1),
                        new Vector2(message.IsPlayer ? -bubbleWidth : 0, -height - heading), new Vector2(message.IsPlayer ? 0 : bubbleWidth, -heading));
                    SettingsPanelTheme.Surface(bubble.GetComponent<Image>(), message.IsPlayer ? new Color(1, .88f, .57f, .95f) : Cream);
                    Rect(text.transform, Vector2.zero, Vector2.one, new Vector2(16, 12), new Vector2(-16, -12));
                    Rect(row.transform, new Vector2(0, 1), Vector2.one, new Vector2(0, -y - height - heading), new Vector2(0, -y));
                    y += height + heading + gap;
                }
            }
            scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(y, scroll.viewport.rect.height));
        }
    }
}
