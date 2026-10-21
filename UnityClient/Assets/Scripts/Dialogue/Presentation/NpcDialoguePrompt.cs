using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    /// <summary>A screen-space hint anchored beside the existing eligible dialogue candidate.</summary>
    internal sealed class NpcDialoguePrompt
    {
        private readonly RectTransform root, parent;
        private readonly TMP_Text nameText;
        private readonly Canvas canvas;
        private DialogueParticipant target;
        private bool hidden;
        public bool IsVisible => root.gameObject.activeInHierarchy;
        public string Label => "[E] " + nameText.text;

        public NpcDialoguePrompt(TMP_Text label, bool bake = false)
        {
            nameText = label;
            canvas = label.canvas;
            if (!bake)
            {
                root = (RectTransform)label.transform.parent;
                parent = (RectTransform)root.parent;
                root.gameObject.SetActive(false);
                return;
            }
            parent = (RectTransform)label.transform.parent;
            var go = new GameObject("NpcDialoguePrompt", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            root = (RectTransform)go.transform;
            root.anchorMin = root.anchorMax = new Vector2(.5f, .5f);
            root.pivot = new Vector2(0, .5f);
            root.sizeDelta = new Vector2(250, 70);
            SettingsPanelTheme.Surface(go.GetComponent<Image>(), new Color(.26f, .24f, .18f, .93f));
            go.GetComponent<Image>().raycastTarget = false;
            label.transform.SetParent(root, false);
            var textRect = label.rectTransform;
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(66, 8); textRect.offsetMax = new Vector2(-22, -8);
            label.color = new Color32(249, 244, 213, 255);
            label.fontStyle = FontStyles.Bold; label.fontSize = 26;
            label.enableAutoSizing = true; label.fontSizeMin = 18; label.fontSizeMax = 26;
            label.alignment = TextAlignmentOptions.Center;
            label.richText = false; label.raycastTarget = false;
            var badge = new GameObject("InteractKey", typeof(RectTransform), typeof(Image));
            badge.transform.SetParent(root, false);
            var badgeRect = (RectTransform)badge.transform;
            badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(0, .5f);
            badgeRect.pivot = new Vector2(0, .5f);
            badgeRect.sizeDelta = new Vector2(42, 38); badgeRect.anchoredPosition = new Vector2(15, 0);
            SettingsPanelTheme.Surface(badge.GetComponent<Image>(), new Color32(249, 244, 213, 255));
            badge.GetComponent<Image>().pixelsPerUnitMultiplier = 3;
            badge.GetComponent<Image>().raycastTarget = false;
            var key = new GameObject("KeyLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
            key.transform.SetParent(badgeRect, false);
            var keyRect = (RectTransform)key.transform;
            keyRect.anchorMin = Vector2.zero; keyRect.anchorMax = Vector2.one;
            keyRect.offsetMin = keyRect.offsetMax = Vector2.zero;
            var keyText = key.GetComponent<TMP_Text>();
            keyText.font = label.font; keyText.text = "E"; keyText.fontSize = 25;
            keyText.fontStyle = FontStyles.Bold; keyText.color = new Color(.28f, .25f, .19f);
            keyText.alignment = TextAlignmentOptions.Center; keyText.raycastTarget = false;
            root.gameObject.SetActive(false);
        }

        public void SetTarget(DialogueParticipant candidate)
        {
            target = candidate;
            if (candidate == null) { root.gameObject.SetActive(false); return; }
            var composer = candidate.GetComponent<CognitiveContextComposer>();
            var perceived = candidate.GetComponent<PerceivableEntity>();
            string original = composer != null && composer.CharacterDefinition != null ?
                CharacterProfileResolver.Resolve(composer.CharacterDefinition, CharacterOverrideStore.Shared).display_name :
                perceived != null ? perceived.DisplayName : candidate.name;
            nameText.text = SettingsUiLanguage.NpcDisplayName(candidate.Entity?.EntityId, original);
        }

        public void SetHidden(bool value)
        {
            hidden = value;
            if (hidden) root.gameObject.SetActive(false);
        }

        public void RefreshPosition(Camera camera)
        {
            if (hidden || target == null || camera == null) { root.gameObject.SetActive(false); return; }
            Vector3 screen = camera.WorldToScreenPoint(target.transform.position + Vector3.up * 1.15f);
            bool visible = screen.z > 0 && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
            root.gameObject.SetActive(visible);
            if (!visible) return;
            var uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, uiCamera, out var point);
            float offset = 82;
            float x = point.x + offset;
            if (x + root.rect.width > parent.rect.xMax - 16) x = point.x - offset - root.rect.width;
            root.anchoredPosition = new Vector2(Mathf.Clamp(x, parent.rect.xMin + 16, parent.rect.xMax-root.rect.width-16),
                Mathf.Clamp(point.y, parent.rect.yMin+51, parent.rect.yMax-51));
        }
    }
}
