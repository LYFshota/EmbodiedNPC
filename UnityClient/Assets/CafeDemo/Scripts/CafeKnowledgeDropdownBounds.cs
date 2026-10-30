using TMPro;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Keep the TMP popup in the knowledge page, above its fixed action bar.</summary>
    [RequireComponent(typeof(TMP_Dropdown))]
    internal sealed class CafeKnowledgeDropdownBounds : MonoBehaviour
    {
        public const float MaxHeight = 240;
        private TMP_Dropdown dropdown;
        private RectTransform page;
        private RectTransform previousPopup;
        private float preferredHeight;
        private readonly Vector3[] corners = new Vector3[4];

        private void Awake() => dropdown = GetComponent<TMP_Dropdown>();

        private void LateUpdate()
        {
            if (dropdown == null || !dropdown.IsExpanded) return;
            if (page == null) page = GetComponentInParent<CafeWorldKnowledgePanel>()?.transform as RectTransform;
            var popup = transform.Find("Dropdown List") as RectTransform;
            if (page == null || popup == null || page.rect.height <= 0) return;
            if (popup != previousPopup)
            { previousPopup = popup; preferredHeight = popup.rect.height; }

            var safe = page.rect;
            safe.xMin += 8; safe.xMax -= 8; safe.yMax -= 8;
            var footer = page.Find("KnowledgeFooter") as RectTransform;
            safe.yMin = footer == null ? safe.yMin + 8 :
                RectTransformUtility.CalculateRelativeRectTransformBounds(page, footer).max.y + 8;
            if (safe.height <= 0 || safe.width <= 0) return;

            float scale = page.InverseTransformVector(popup.TransformVector(Vector3.up)).magnitude;
            if (scale <= 0) return;
            popup.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Min(preferredHeight, MaxHeight, safe.height / scale));
            var field = (RectTransform)transform;
            float horizontalScale = page.InverseTransformVector(popup.TransformVector(Vector3.right)).magnitude;
            if (horizontalScale > 0) popup.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                Mathf.Min(field.rect.width, safe.width / horizontalScale));
            // Scroll content deliberately extends past the mask; constrain the popup rect alone.
            popup.GetWorldCorners(corners);
            var min = (Vector2)page.InverseTransformPoint(corners[0]);
            var max = (Vector2)page.InverseTransformPoint(corners[2]);
            field.GetWorldCorners(corners);
            var fieldMin = (Vector2)page.InverseTransformPoint(corners[0]);
            var fieldMax = (Vector2)page.InverseTransformPoint(corners[2]);
            float height = max.y - min.y, width = max.x - min.x;
            float below = fieldMin.y - safe.yMin, above = safe.yMax - fieldMax.y;
            float bottom = below >= height || below >= above ? fieldMin.y - height : fieldMax.y;
            float dx = Mathf.Clamp(fieldMin.x, safe.xMin, safe.xMax - width) - min.x;
            float dy = Mathf.Clamp(bottom, safe.yMin, safe.yMax - height) - min.y;
            popup.position += page.TransformVector(new Vector3(dx, dy, 0));
        }
    }
}
