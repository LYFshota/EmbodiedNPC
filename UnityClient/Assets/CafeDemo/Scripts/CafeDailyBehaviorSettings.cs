using System.Collections.Generic;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Scene-local control; no model form save, transport, or persistent config dependency.</summary>
    [DisallowMultipleComponent]
    public sealed class CafeDailyBehaviorSettings : MonoBehaviour
    {
        [SerializeField] private FirstRunModelSetup modelForm;
        [SerializeField] private TMP_Dropdown dropdownTemplate;
        [SerializeField] private DailyBehaviorController dailyBehavior;
        private DailyBehaviorDemoTrigger source;
        private TMP_Text label, status;
        private TMP_Dropdown dropdown;
        private bool rejected;
        private float refreshAfter;
        private Transform debugRoot;
        public void BindDebugRoot(Transform root) { debugRoot = root; EnsureControls(); }
        public TMP_Dropdown ModeDropdown => dropdown;
        public string StatusText => status == null ? string.Empty : status.text;

        private void Awake() => source = dailyBehavior == null ? null : dailyBehavior.GetComponent<DailyBehaviorDemoTrigger>();
        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshLanguage;
            if (dropdown != null) dropdown.onValueChanged.AddListener(RequestSelection);
            RefreshLanguage();
        }
        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage;
            if (dropdown != null) dropdown.onValueChanged.RemoveListener(RequestSelection);
        }
        private void Update()
        {
            if (Time.unscaledTime < refreshAfter) return;
            refreshAfter = Time.unscaledTime + .2f;
            EnsureControls(); RefreshState();
        }
        private void EnsureControls()
        {
            if (dropdown != null || debugRoot == null ||
                dropdownTemplate == null || dailyBehavior == null) return;
            var parent = debugRoot;
            var row = new GameObject("DailyBehaviorModeRow", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
            row.transform.SetParent(parent, false);
            row.GetComponent<LayoutElement>().minHeight = row.GetComponent<LayoutElement>().preferredHeight = 66;
            SettingsPanelTheme.Surface(row.GetComponent<Image>(), SettingsPanelTheme.Row);
            row.GetComponent<Image>().raycastTarget = false;
            label = MakeText("Label", row.transform, 18);
            Place(label.rectTransform, Vector2.zero, new Vector2(.38f, 1), new Vector2(20, 6), new Vector2(-10, -6));
            dropdown = Instantiate(dropdownTemplate, row.transform);
            dropdown.name = "DailyBehaviorMode";
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dropdown.gameObject.SetActive(true);
            if (dropdown.TryGetComponent<LayoutElement>(out var layout)) layout.ignoreLayout = true;
            Place(dropdown.GetComponent<RectTransform>(), new Vector2(.4f, 0), Vector2.one, new Vector2(0, 12), new Vector2(-20, -12));
            status = MakeText("DailyBehaviorModeStatus", parent, 16);
            var height = status.gameObject.AddComponent<LayoutElement>();
            height.minHeight = 100; height.preferredHeight = -1;
            status.enableAutoSizing = false; status.fontSize = 20; status.textWrappingMode = TextWrappingModes.Normal;
            SettingsPanelTheme.Apply(row);
            dropdown.onValueChanged.AddListener(RequestSelection);
            RefreshLanguage();
        }
        private TMP_Text MakeText(string name, Transform parent, float size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TMP_Text>();
            text.font = dropdownTemplate.captionText.font; text.fontSize = size;
            text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = size;
            text.color = SettingsPanelTheme.Ink; text.raycastTarget = false;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            return text;
        }
        private static void Place(RectTransform rect, Vector2 min, Vector2 max, Vector2 bottom, Vector2 top)
        { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = bottom; rect.offsetMax = top; }
        private void RequestSelection(int value)
        {
            rejected = source == null || !source.RequestMode(value == 1 ? DailyBehaviorMode.Rest : DailyBehaviorMode.Work);
            RefreshState(); // Always project committed mode; a requested Rest is not yet an actual Rest.
        }
        private void RefreshLanguage()
        {
            if (dropdown == null) return;
            label.text = SettingsUiLanguage.T("林薇薇生活模式", "Lin Weiwei life mode");
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string> { SettingsUiLanguage.T("工作", "Work"), SettingsUiLanguage.T("休息", "Rest") });
            RefreshState();
        }
        private static string ModeCaption(DailyBehaviorMode mode) => mode == DailyBehaviorMode.Work ? SettingsUiLanguage.T("工作", "Work") : mode == DailyBehaviorMode.Rest ? SettingsUiLanguage.T("休息", "Rest") : SettingsUiLanguage.T("休闲", "Leisure");
        private void RefreshState()
        {
            if (dropdown == null) return;
            bool available = Application.isPlaying && dailyBehavior != null && dailyBehavior.isActiveAndEnabled &&
                source != null && source.isActiveAndEnabled;
            dropdown.interactable = available;
            bool pendingRequest = dailyBehavior != null && dailyBehavior.TryGetPendingMode(out _);
            DailyBehaviorMode selected = pendingRequest && dailyBehavior.TryGetPendingMode(out var requested) ?
                requested : dailyBehavior == null ? DailyBehaviorMode.Work : dailyBehavior.CurrentMode;
            string waiting = SettingsUiLanguage.T("（等待切换）", " (waiting)");
            dropdown.options[0].text = SettingsUiLanguage.T("工作", "Work") + (pendingRequest && selected == DailyBehaviorMode.Work ? waiting : "");
            dropdown.options[1].text = SettingsUiLanguage.T("休息", "Rest") + (pendingRequest && selected == DailyBehaviorMode.Rest ? waiting : "");
            dropdown.SetValueWithoutNotify(selected == DailyBehaviorMode.Rest ? 1 : 0);
            dropdown.RefreshShownValue();
            string text = SettingsUiLanguage.T("仅调试林薇薇本次运行的行为。选择即请求，无需保存，不改变默认人设。\n当前实际模式：", "Debugs Lin Weiwei during this run only. Selection sends a request without saving or changing the default persona.\nActual current mode: ") +
                (dailyBehavior == null ? SettingsUiLanguage.T("不可用", "Unavailable") : ModeCaption(dailyBehavior.CurrentMode));
            if (!available) text += SettingsUiLanguage.T(" · 不可用", " · unavailable");
            else if (dailyBehavior.TryGetPendingMode(out var pending))
                text += SettingsUiLanguage.T(" · 等待切换：", " · waiting for: ") + ModeCaption(pending);
            else if (rejected) text += SettingsUiLanguage.T(" · 请求未接受", " · request rejected");
            if (dailyBehavior?.LastTransitionReason == "deferred_busy_or_cooldown") text += SettingsUiLanguage.T("\n正在忙碌或等待切换间隔结束。", "\nBusy or waiting for the switching interval to end.");
            if (!string.IsNullOrEmpty(dailyBehavior?.LastTransitionReason)) text += "\n" + SettingsUiLanguage.T("最近切换：", "Latest transition: ") + SettingsUiLanguage.DebugValue(dailyBehavior.LastTransitionReason);
            status.text = text;
        }
    }
}
