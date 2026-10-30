using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Shared presentation tokens for the cream settings surface.</summary>
    public sealed class SettingsPanelTheme : MonoBehaviour
    {
        public static readonly Color Paper = new Color32(242, 238, 219, 255);
        public static readonly Color Row = new Color32(229, 224, 204, 255);
        public static readonly Color Field = new Color32(250, 247, 234, 255);
        public static readonly Color Ink = new Color32(98, 83, 60, 255);
        public static readonly Color Muted = new Color32(153, 142, 120, 255);
        public static readonly Color Accent = new Color32(244, 213, 101, 255);
        public static readonly Color Navigation = new Color32(66, 61, 52, 255);
        public const float RowHeight = 68;
        public const float Spacing = 12;
        private static Sprite rounded;

        public static void Surface(Image image, Color color)
        {
            if (image == null) return;
            if (rounded == null) rounded = Resources.Load<Sprite>("CafeUI/RoundedSurface");
            if (rounded == null)
            {
                const int size = 64;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.name = "Settings rounded surface";
                texture.hideFlags = HideFlags.HideAndDontSave;
                texture.wrapMode = TextureWrapMode.Clamp;
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float distance = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(32, 32));
                        pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(32 - distance));
                    }
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                rounded = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f),
                    100, 0, SpriteMeshType.FullRect, new Vector4(31, 31, 31, 31));
                rounded.hideFlags = HideFlags.HideAndDontSave;
            }
            image.sprite = rounded;
            image.type = Image.Type.Sliced;
            image.color = color;
        }

        public static void Button(Button button, bool primary = false)
        {
            Surface(button.targetGraphic as Image, primary ? Accent :
                button.transform.parent.name.EndsWith("Row") ? Field : Row);
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1, .98f, .90f);
            colors.pressedColor = new Color(.88f, .84f, .71f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1, 1, 1, .4f);
            button.colors = colors;
            foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
            {
                text.color = Ink;
                if (button.transform.parent.name != "SettingsNavigation") text.fontSize = 20;
            }
        }

        public static void StyleScrollbar(Scrollbar scrollbar)
        {
            if (scrollbar == null || scrollbar.handleRect == null) return;
            Surface(scrollbar.GetComponent<Image>(), Row);
            var thumb = scrollbar.handleRect.GetComponent<Image>();
            Surface(thumb, Muted);
            scrollbar.targetGraphic = thumb;
            // The thumb must remain distinguishable when only the wheel is used.
            scrollbar.transition = Selectable.Transition.None;
            if (thumb != null) thumb.canvasRenderer.SetColor(Color.white);
        }

        public static void SizeDropdown(TMP_Dropdown dropdown)
        {
            if (dropdown == null || dropdown.template == null) return;
            var input = dropdown.GetComponentInParent<TMP_InputField>();
            var field = (RectTransform)(input != null ? input.transform : dropdown.transform);
            if (field.rect.width > 0)
                dropdown.template.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, field.rect.width);
            float scale = dropdown.GetComponentInParent<Canvas>()?.scaleFactor ?? 1;
            float available = Screen.height > 0 ? Screen.height / Mathf.Max(.1f,scale) - 100 : 600;
            var click = dropdown.GetComponentInChildren<SettingsDropdownClickToggle>(true);
            if (click != null && click.MaximumHeight > 0) available = Mathf.Min(available, click.MaximumHeight);
            dropdown.template.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Clamp(dropdown.options.Count * 44 + 12, 100, Mathf.Max(100,available)));
            var scroll = dropdown.template.GetComponent<ScrollRect>();
            if (scroll != null)
            {
                scroll.vertical = true; scroll.scrollSensitivity = 35;
                if (scroll.verticalScrollbar != null) scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }
        }

        public static void Apply(GameObject root)
        {
            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                // Keep glyph sampling inside the atlas padding to avoid neighboring glyph specks.
                text.extraPadding = false;
                text.color = text.gameObject.name == "Placeholder" ? Muted : Ink;
                text.raycastTarget = false;
            }
            foreach (var button in root.GetComponentsInChildren<Button>(true))
                Button(button, button.name == "SaveConfigButton" || button.name == "SaveNpc");
            foreach (var input in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                Surface(input.targetGraphic as Image, Field);
                input.textComponent.fontSize = 21;
                input.textComponent.enableAutoSizing = false;
                input.caretColor = Ink;
                input.customCaretColor = true;
                input.selectionColor = new Color(Accent.r, Accent.g, Accent.b, .5f);
                if (input.placeholder is TMP_Text placeholder)
                { placeholder.fontSize = 19; placeholder.fontStyle = FontStyles.Normal; }
            }
            foreach (var dropdown in root.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                SettingsUiLanguage.StyleDropdownArrow(dropdown);
                SettingsDropdownClickToggle.Ensure(dropdown);
                Surface(dropdown.targetGraphic as Image, Field);
                if (dropdown.captionText != null) dropdown.captionText.fontSize = 21;
                if (dropdown.itemText != null) dropdown.itemText.fontSize = 21;
                if (dropdown.template != null)
                {
                    SizeDropdown(dropdown);
                    foreach (var image in dropdown.template.GetComponentsInChildren<Image>(true))
                        if (image.name == "Template" || image.name == "Item Background") Surface(image, Field);
                    Transform item = dropdown.template.Find("Viewport/Content/Item");
                    if (item != null)
                    {
                        ((RectTransform)item).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 44);
                        var option = item.GetComponent<Toggle>();
                        if (option != null)
                        {
                            var colors = option.colors;
                            colors.highlightedColor = Accent; colors.selectedColor = Row;
                            option.colors = colors;
                        }
                    }
                }
            }
            foreach (var toggle in root.GetComponentsInChildren<Toggle>(true))
                SettingsUiLanguage.StyleToggle(toggle);
            foreach (var scrollbar in root.GetComponentsInChildren<Scrollbar>(true))
                StyleScrollbar(scrollbar);
        }
    }
}
