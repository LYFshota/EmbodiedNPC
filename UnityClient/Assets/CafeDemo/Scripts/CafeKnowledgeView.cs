using System;
using System.Collections.Generic;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Layout helpers scoped to the one Knowledge page, using existing TMP controls/theme.</summary>
    internal sealed class CafeKnowledgeView
    {
        private readonly TMP_FontAsset font;
        private readonly List<(TMP_Text text, string zh, string en)> labels = new();
        private readonly List<(TMP_Dropdown dropdown, string[] zh, string[] en)> choices = new();
        public CafeKnowledgeView(TMP_FontAsset font) { this.font = font; }
        public static GameObject Node(string name, Transform parent, params Type[] types)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            foreach (var type in types) go.AddComponent(type);
            return go;
        }
        public static void Rect(Transform value, Vector2 min, Vector2 max, Vector2 lo, Vector2 hi)
        { var r = (RectTransform)value; r.anchorMin = min; r.anchorMax = max; r.offsetMin = lo; r.offsetMax = hi; }
        public static void Height(GameObject go, float height)
        { var e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); e.minHeight = e.preferredHeight = height; }
        public TMP_Text Text(Transform parent, string name, string zh, string en, float size = 20)
        {
            var text = Node(name, parent, typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            text.font = font; text.fontSize = size; text.color = SettingsPanelTheme.Ink;
            text.raycastTarget = false; text.alignment = TextAlignmentOptions.MidlineLeft;
            labels.Add((text, zh, en)); text.text = SettingsUiLanguage.T(zh, en); return text;
        }
        public Button Button(Transform parent, string name, string zh, string en, Action click, bool primary = false)
        {
            var button = Node(name, parent, typeof(Image), typeof(Button)).GetComponent<Button>();
            button.targetGraphic = button.GetComponent<Image>();
            var text = Text(button.transform, "Caption", zh, en); text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 20;
            Rect(text.transform, Vector2.zero, Vector2.one, new Vector2(6, 2), new Vector2(-6, -2));
            SettingsPanelTheme.Button(button, primary); button.onClick.AddListener(() => click()); return button;
        }
        public TMP_InputField Input(Transform parent, string name, bool multiline = false)
        {
            var go = TMP_DefaultControls.CreateInputField(default); go.name = name; go.transform.SetParent(parent, false);
            var input = go.GetComponent<TMP_InputField>();
            foreach (var text in go.GetComponentsInChildren<TMP_Text>(true)) text.font = font;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.richText = false; input.resetOnDeActivation = false;
            if (input.placeholder is TMP_Text placeholder)
            {
                string zh = name == "KnowledgeSearch" ? "搜索名称或内容" : name == "RelatedKnowledgeSearch" ? "搜索关联知识名称" :
                    name == "AttributeKey" ? "属性名称" : name == "AttributeValue" ? "属性值" : "输入内容";
                string en = name == "KnowledgeSearch" ? "Search name or content" : name == "RelatedKnowledgeSearch" ? "Search related knowledge" :
                    name == "AttributeKey" ? "Attribute key" : name == "AttributeValue" ? "Attribute value" : "Enter text";
                labels.Add((placeholder, zh, en)); placeholder.text = SettingsUiLanguage.T(zh, en);
            }
            if (multiline)
            {
                var bar = DefaultControls.CreateScrollbar(default); bar.name = "TextScrollbar";
                bar.transform.SetParent(go.transform, false);
                Rect(bar.transform, new Vector2(1, 0), Vector2.one, new Vector2(-14, 4), new Vector2(-3, -4));
                var scrollbar = bar.GetComponent<Scrollbar>(); scrollbar.direction = Scrollbar.Direction.BottomToTop;
                input.verticalScrollbar = scrollbar;
                input.textViewport.offsetMax = new Vector2(-19, input.textViewport.offsetMax.y);
                SettingsPanelTheme.Surface(bar.GetComponent<Image>(), SettingsPanelTheme.Row);
                SettingsPanelTheme.Surface(scrollbar.handleRect.GetComponent<Image>(), SettingsPanelTheme.Muted);
            }
            SettingsPanelTheme.Apply(go); return input;
        }
        public TMP_Dropdown Dropdown(Transform parent, string name, string[] zh, string[] en)
        {
            var go = TMP_DefaultControls.CreateDropdown(default); go.name = name; go.transform.SetParent(parent, false);
            var dropdown = go.GetComponent<TMP_Dropdown>();
            foreach (var text in go.GetComponentsInChildren<TMP_Text>(true)) text.font = font;
            choices.Add((dropdown, zh, en)); SetChoices(dropdown, zh, en);
            SettingsPanelTheme.Apply(go); return dropdown;
        }
        private static void SetChoices(TMP_Dropdown dropdown, string[] zh, string[] en)
        {
            int selected = dropdown.value; dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(SettingsUiLanguage.IsEnglish ? en : zh));
            dropdown.SetValueWithoutNotify(Mathf.Min(selected, dropdown.options.Count - 1)); dropdown.RefreshShownValue();
        }
        public Toggle Toggle(Transform parent, string name, string caption, bool selected)
        {
            var row = Node(name + "Row", parent); Height(row, 38);
            var label = Text(row.transform, "ScopeLabel", caption, caption, 18);
            Rect(label.transform, Vector2.zero, Vector2.one, new Vector2(6, 0), new Vector2(-88, 0));
            var go = DefaultControls.CreateToggle(default); go.name = name; go.transform.SetParent(row.transform, false);
            Rect(go.transform, new Vector2(1,.5f), new Vector2(1,.5f), new Vector2(-76,-17), new Vector2(-8,17));
            var old = go.transform.Find("Label");
            if (old != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(old.gameObject); else UnityEngine.Object.DestroyImmediate(old.gameObject); }
            var toggle = go.GetComponent<Toggle>(); toggle.SetIsOnWithoutNotify(selected);
            SettingsUiLanguage.StyleToggle(toggle); return toggle;
        }
        public Transform Scroll(Transform parent, string name)
        {
            var go = DefaultControls.CreateScrollView(default); go.name = name; go.transform.SetParent(parent, false);
            var scroll = go.GetComponent<ScrollRect>(); scroll.horizontal = false; scroll.scrollSensitivity = 32;
            if (scroll.horizontalScrollbar != null) scroll.horizontalScrollbar.gameObject.SetActive(false);
            var content = scroll.content; content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one;
            content.pivot = new Vector2(.5f, 1); content.sizeDelta = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>(); layout.spacing = 8;
            layout.padding = new RectOffset(6, 6, 6, 10); layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            go.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            SettingsPanelTheme.Surface(scroll.verticalScrollbar.GetComponent<Image>(), SettingsPanelTheme.Row);
            SettingsPanelTheme.Surface(scroll.verticalScrollbar.handleRect.GetComponent<Image>(), SettingsPanelTheme.Muted);
            return content;
        }
        public void RefreshLanguage()
        {
            labels.RemoveAll(x => x.text == null);
            foreach (var label in labels) label.text.text = SettingsUiLanguage.T(label.zh, label.en);
            choices.RemoveAll(x => x.dropdown == null);
            foreach (var choice in choices) SetChoices(choice.dropdown, choice.zh, choice.en);
        }
    }
}
