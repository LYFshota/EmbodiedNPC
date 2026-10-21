using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed class SettingsToggleFeedback : MonoBehaviour
    {
        private Toggle toggle;
        private Graphic background;
        private void Awake()
        {
            toggle = GetComponent<Toggle>();
            background = toggle.targetGraphic;
        }
        private void OnEnable()
        {
            if (toggle == null) Awake();
            toggle.onValueChanged.AddListener(OnChanged);
            Refresh();
        }
        private void OnDisable() => toggle?.onValueChanged.RemoveListener(OnChanged);
        private void OnChanged(bool ignored) => Refresh();
        public void Refresh()
        {
            if (toggle == null) Awake();
            if (toggle.GetComponentInParent<SettingsPanelTheme>(true) != null)
            {
                Transform track = toggle.transform.Find("Background");
                if (track == null) return;
                var rect = (RectTransform)track;
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(0, 4); rect.offsetMax = new Vector2(0, -4);
                SettingsPanelTheme.Surface(track.GetComponent<Image>(), toggle.isOn ? SettingsPanelTheme.Accent : SettingsPanelTheme.Muted);
                // The thumb stays visible in both states; Toggle's checkbox graphic would fade it out.
                toggle.graphic = null;
                Transform check = track.Find("Checkmark");
                if (check != null) check.gameObject.SetActive(false);
                Transform thumb = track.Find("SwitchThumb");
                if (thumb == null)
                {
                    var go = new GameObject("SwitchThumb", typeof(RectTransform), typeof(Image));
                    go.transform.SetParent(track, false); thumb = go.transform;
                    go.GetComponent<Image>().raycastTarget = false;
                }
                var thumbRect = (RectTransform)thumb;
                thumbRect.anchorMin = thumbRect.anchorMax = new Vector2(toggle.isOn ? 1 : 0, .5f);
                thumbRect.pivot = new Vector2(toggle.isOn ? 1 : 0, .5f);
                thumbRect.sizeDelta = new Vector2(28, 28);
                thumbRect.anchoredPosition = new Vector2(toggle.isOn ? -4 : 4, 0);
                SettingsPanelTheme.Surface(thumb.GetComponent<Image>(), SettingsPanelTheme.Field);
                return;
            }
            if (background != null)
                background.color = toggle.isOn ? new Color(.12f, .68f, .32f, 1) :
                    new Color(.18f, .23f, .26f, 1);
        }
    }
}
