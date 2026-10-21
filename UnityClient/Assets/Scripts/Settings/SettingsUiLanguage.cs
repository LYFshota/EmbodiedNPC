using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Presentation preference only; model configuration remains owned by the Backend.</summary>
    public static class SettingsUiLanguage
    {
        private static readonly Color Coffee = new Color(.25f, .16f, .12f, 1);
        private static readonly Color CoffeePage = new Color(.29f, .19f, .14f, 1);
        private static readonly Color Cream = new Color(.91f, .81f, .67f, 1);
        private static readonly Color Paper = new Color(.97f, .92f, .83f, 1);
        private static readonly Color Ink = new Color(.20f, .13f, .10f, 1);
        private const string PreferenceKey = "EmbodiedNPC.Settings.Language";
        public static event Action Changed;
        public static bool IsEnglish => PortableDataPaths.IsPortable
            ? PortablePreferences.Current.Language == 1 : PlayerPrefs.GetInt(PreferenceKey, 0) == 1;
        public static string T(string chinese, string english) => IsEnglish ? english : chinese;

        // Cafe UI aliases only. Never feed these labels back into profiles or identity data.
        public static string NpcDisplayName(string entityId, string original)
        {
            var names = entityId switch {
                "npc_cafe_01" or "cafe.merchant" => ("林薇薇", "Lin Weiwei"),
                "npc_cafe_regular_01" or "cafe.regular_customer" => ("沈知意", "Shen Zhiyi"),
                "npc_cafe_companion_01" or "cafe.companion_customer" => ("温予安", "Wen Yu'an"),
                "npc_cafe_quiet_01" or "cafe.quiet_customer" => ("江亦舟", "Jiang Yizhou"),
                _ => (null, null)
            };
            if (names.Item1 == null) return original;
            // User-authored names have no reliable translation; preserve their text.
            if (!string.IsNullOrEmpty(original) && original != names.Item1 && original != names.Item2) return original;
            return T(names.Item1, names.Item2);
        }

        public static string StatusEnglish(string chinese)
        {
            switch (chinese)
            {
                case "请先配置主模型与 Embedding，再保存配置。": return "Configure the main model and embedding, then save.";
                case "正在连接 AI Backend…": return "Connecting to AI Backend…";
                case "无法连接 AI Backend，请先启动 Backend 后重试。": return "Cannot reach AI Backend. Start it and retry.";
                case "无法连接 AI Backend，请重试。": return "Cannot reach AI Backend. Retry.";
                case "配置已重新加载。": return "Configuration reloaded.";
                case "当前没有已保存的模型配置。": return "No saved model configuration.";
                case "配置已保存，需要重启 AI Backend 后生效。": return "Saved. Restart AI Backend to apply.";
                case "配置已生效。": return "Configuration is active.";
                case "配置尚未可用，请检查模型连接并重新检查。": return "Configuration is unavailable. Check the connection and recheck.";
                case "配置已保存并已生效。": return "Saved and active.";
                case "配置已发生变化，请重新打开模型页后再保存。": return "Configuration changed. Reopen the model page before saving.";
                case "保存失败，请检查配置后重试。": return "Save failed. Check the configuration and retry.";
                case "Embedding 超时参数无效。": return "Embedding timeout is invalid.";
                case "请先填写有效的 API 地址和超时。": return "Enter a valid API URL and timeout first.";
                case "无法获取模型列表，请手动输入模型名称。": return "Cannot list models. Enter the model name manually.";
                case "模型列表已更新；也可以继续手动输入模型名称。": return "Model list updated. You can also enter a model name manually.";
                case "请先启用 Embedding。": return "Enable embedding first.";
                case "连接超时，请检查 API 地址。": return "Connection timed out. Check the API URL.";
                case "配置无效，请检查 API 地址、模型和参数。": return "Invalid configuration. Check the URL, model and parameters.";
                case "连接失败，请检查配置。": return "Connection failed. Check the configuration.";
                case "✓ Embedding 可用": return "✓ Embedding available";
                case "✓ 主模型连接成功": return "✓ Main model connection succeeded";
                case "Embedding 测试失败，请检查配置。": return "Embedding test failed. Check the configuration.";
                case "主模型连接失败，请检查配置。": return "Main model test failed. Check the configuration.";
                default: return chinese;
            }
        }

        public static void SetEnglish(bool english)
        {
            int value = english ? 1 : 0;
            if (IsEnglish == english) return;
            if (PortableDataPaths.IsPortable) PortablePreferences.Current.SetLanguage(english);
            else { PlayerPrefs.SetInt(PreferenceKey, value); PlayerPrefs.Save(); }
            Changed?.Invoke();
        }

        public static void StyleToggle(Toggle toggle)
        {
            if (toggle == null || toggle.targetGraphic == null) return;
            if (toggle.group != null && toggle.group.name == "EmbeddingProviderChoices") return;
            // TMP dropdown choices are Toggles too, but their selection highlight is not a checkbox.
            if (toggle.GetComponentInParent<TMP_Dropdown>(true) != null) return;
            toggle.transition = Selectable.Transition.None;
            var feedback = toggle.GetComponent<SettingsToggleFeedback>();
            if (feedback == null) feedback = toggle.gameObject.AddComponent<SettingsToggleFeedback>();
            feedback.Refresh();
        }

        public static void StyleCoffeeForm(GameObject panel)
        {
            if (panel == null) return;
            if (panel.GetComponentInParent<SettingsPanelTheme>(true) != null)
            {
                SettingsPanelTheme.Apply(panel);
                return;
            }
            if (panel.TryGetComponent(out Image surface)) surface.color = CoffeePage;
            foreach (TMP_InputField input in panel.GetComponentsInChildren<TMP_InputField>(true))
                if (input.targetGraphic is Image background) background.color = Paper;
            foreach (TMP_Dropdown dropdown in panel.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                if (dropdown.targetGraphic is Image background) background.color = Paper;
                StyleDropdownArrow(dropdown);
                SettingsDropdownClickToggle.Ensure(dropdown);
            }
            foreach (Button button in panel.GetComponentsInChildren<Button>(true))
                StyleCoffeeButton(button);
        }

        public static void StyleCoffeeWindow(GameObject panelRoot)
        {
            Transform window = panelRoot?.transform.Find("Window");
            if (window == null) return;
            if (window.GetComponent<SettingsPanelTheme>() != null)
            {
                SettingsPanelTheme.Apply(window.gameObject);
                return;
            }
            if (window.TryGetComponent(out Image background)) background.color = Coffee;
            foreach (string page in new[] { "OverviewPage", "NpcPage", "DebugPage", "TracePage" })
            {
                Transform child = window.Find(page);
                if (child != null && child.TryGetComponent(out Image image)) image.color = CoffeePage;
            }
            foreach (Button button in window.GetComponentsInChildren<Button>(true))
                StyleCoffeeButton(button);
            foreach (TMP_Dropdown dropdown in window.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                StyleDropdownArrow(dropdown);
                SettingsDropdownClickToggle.Ensure(dropdown);
            }
        }

        internal static void StyleDropdownArrow(TMP_Dropdown dropdown)
        {
            if (dropdown.GetComponentInParent<TMP_InputField>() != null) return;
            Transform arrow = dropdown.transform.Find("Arrow");
            if (arrow != null) arrow.gameObject.SetActive(false);
            Transform existing = dropdown.transform.Find("SettingsChevron");
            if (existing != null || dropdown.captionText == null) return;
            var chevronObject = new GameObject("SettingsChevron", typeof(RectTransform), typeof(TextMeshProUGUI));
            chevronObject.transform.SetParent(dropdown.transform, false);
            var rect = (RectTransform)chevronObject.transform;
            rect.anchorMin = new Vector2(1, 0);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-38, 0);
            rect.offsetMax = Vector2.zero;
            var text = chevronObject.GetComponent<TextMeshProUGUI>();
            text.font = dropdown.captionText.font;
            text.fontSize = dropdown.captionText.fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Ink;
            text.raycastTarget = false;
            text.text = "v";
        }

        private static void StyleCoffeeButton(Button button)
        {
            if (button.targetGraphic is Image background) background.color = Cream;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.color = Ink;
        }
    }

}
