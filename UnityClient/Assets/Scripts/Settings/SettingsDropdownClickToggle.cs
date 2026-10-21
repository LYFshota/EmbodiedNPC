using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Uses one hit target for open/close so a second click can dismiss a TMP menu.</summary>
    public sealed class SettingsDropdownClickToggle : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private TMP_Dropdown dropdown;
        private void Awake() { if (dropdown == null) dropdown = GetComponentInParent<TMP_Dropdown>(); }

        public static void Ensure(TMP_Dropdown target)
        {
            if (target == null || target.transform.Find("SettingsDropdownHitTarget") != null) return;
            var hit = new GameObject("SettingsDropdownHitTarget", typeof(RectTransform), typeof(Image),
                typeof(SettingsDropdownClickToggle));
            hit.transform.SetParent(target.transform, false);
            var rect = (RectTransform)hit.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            hit.GetComponent<Image>().color = Color.clear;
            hit.GetComponent<SettingsDropdownClickToggle>().dropdown = target;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (dropdown == null || !dropdown.IsActive() || !dropdown.IsInteractable()) return;
            if (dropdown.IsExpanded)
            {
                dropdown.Hide();
                return;
            }
            Canvas canvas = dropdown.GetComponentInParent<Canvas>();
            if (canvas != null)
                foreach (TMP_Dropdown other in canvas.GetComponentsInChildren<TMP_Dropdown>(true))
                    if (other != dropdown && other.IsExpanded) other.Hide();
            Canvas.ForceUpdateCanvases();
            SettingsPanelTheme.SizeDropdown(dropdown);
            dropdown.Show();
        }
    }

}
