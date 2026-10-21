using TMPro;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [RequireComponent(typeof(CanvasGroup), typeof(RectTransform))]
    public sealed class ActionFeedbackView : MonoBehaviour
    {
        [SerializeField] private TMP_Text ownerText;
        [SerializeField] private TMP_Text detailText;
        private CanvasGroup group;
        public RectTransform Rect => (RectTransform)transform;
        public string Detail => detailText == null ? "" : detailText.text;

        public void Configure(TMP_Text title, TMP_Text detail)
        { ownerText = title; detailText = detail; }
        public void SetText(string owner, string detail)
        {
            if (ownerText != null) ownerText.text = owner;
            if (detailText != null) detailText.text = detail;
            SetAlpha(1);
        }
        public void SetAlpha(float alpha)
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            group.alpha = alpha; group.blocksRaycasts = false; group.interactable = false;
        }
    }
}
