using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    public sealed class ChatHistoryMessageView : MonoBehaviour
    {
        [SerializeField] private TMP_Text speaker;
        [SerializeField] private TMP_Text message;
        [SerializeField] private Image bubble;
        public void Configure()
        {
            speaker = transform.Find("Speaker").GetComponent<TMP_Text>();
            bubble = transform.Find("Bubble").GetComponent<Image>();
            message = bubble.transform.Find("Message").GetComponent<TMP_Text>();
        }
        public float Present(ChatHistoryMessage data, float width, string segmentText = null, bool showSpeaker = true)
        {
            speaker.gameObject.SetActive(showSpeaker);
            speaker.text = data.IsPlayer ? EmbodiedNPC.Settings.SettingsUiLanguage.T("我", "Me") :
                EmbodiedNPC.Settings.SettingsUiLanguage.NpcDisplayName(data.ObserverEntityId, data.Speaker);
            speaker.color = data.IsPlayer ? new Color32(95,79,56,255) : new Color(.47f,.60f,.29f);
            speaker.alignment = data.IsPlayer ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft;
            string text = segmentText ?? data.Text;
            message.text = text;
            message.richText = false;
            message.textWrappingMode = TextWrappingModes.Normal;
            message.overflowMode = TextOverflowModes.Overflow;
            float height = Mathf.Max(34, message.GetPreferredValues(text, width-32, 0).y)+24;
            float heading = showSpeaker ? 35 : 0;
            var rect = (RectTransform)bubble.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(data.IsPlayer ? 1 : 0,1);
            rect.offsetMin = new Vector2(data.IsPlayer ? -width : 0,-height-heading);
            rect.offsetMax = new Vector2(data.IsPlayer ? 0 : width,-heading);
            bubble.color = data.IsPlayer ? new Color(1,.88f,.57f,.95f) : new Color32(249,244,213,255);
            return height;
        }
    }
}
