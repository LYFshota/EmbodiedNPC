using System.Collections;
using System;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class CafeUiFlowController
    {
        private GameObject capacityError, capacityWarning;
        private TMP_Text capacityErrorText, capacityWarningText;
        private Button capacityDismiss;
        private bool checkingCapacity;
        private float nextCapacityCheck;
        private int capacityRevision = -1;
        private bool capacityEnglish, capacityDismissed;
        private ContextCapacityState capacityState;
        private PortablePreferenceFile capacityPreferences;
        private ContextCapacityState CapacityState => capacityState ?? ContextCapacityFeedback.Current;
        private bool CapacityWarningDismissed => capacityPreferences?.ContextWarningDismissed ?? ContextCapacityFeedback.WarningDismissed;

        private GameObject CapacityCard(string name, Color color, out TMP_Text text)
        {
            var card = new GameObject(name, typeof(RectTransform), typeof(Image));
            card.transform.SetParent(transform, false);
            var rect = (RectTransform)card.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, 1);
            rect.anchoredPosition = new Vector2(0, -18); rect.sizeDelta = new Vector2(900, 270);
            var image = card.GetComponent<Image>(); image.color = color; image.raycastTarget = false;
            text = Instantiate(hint, card.transform); text.name = "Message";
            text.gameObject.SetActive(true); text.fontSize = 21; text.enableAutoSizing = false;
            text.color = new Color(.98f, .95f, .88f); text.raycastTarget = false;
            text.alignment = TextAlignmentOptions.TopLeft; text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(22, 55); text.rectTransform.offsetMax = new Vector2(-22, -16);
            card.SetActive(false); return card;
        }
        private void CreateCapacityFeedback()
        {
            capacityError = CapacityCard("ContextCapacityError", new Color(.36f,.14f,.11f,.97f), out capacityErrorText);
            capacityWarning = CapacityCard("ContextCapacityWarning", new Color(.30f,.23f,.14f,.97f), out capacityWarningText);
            capacityDismiss = Instantiate(helpButton, capacityWarning.transform);
            capacityDismiss.name = "DismissContextWarning"; capacityDismiss.onClick = new Button.ButtonClickedEvent();
            var rect = (RectTransform)capacityDismiss.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1,0); rect.pivot = new Vector2(1,0);
            rect.anchoredPosition = new Vector2(-18,10); rect.sizeDelta = new Vector2(300,38);
            var caption = capacityDismiss.GetComponentInChildren<TMP_Text>(true);
            caption.fontSize = 19; caption.enableAutoSizing = false;
            caption.textWrappingMode = TextWrappingModes.NoWrap;
            caption.rectTransform.anchorMin = Vector2.zero; caption.rectTransform.anchorMax = Vector2.one;
            caption.rectTransform.offsetMin = new Vector2(10,2); caption.rectTransform.offsetMax = new Vector2(-10,-2);
            capacityDismiss.gameObject.SetActive(true);
            capacityDismiss.onClick.AddListener(() => { if (capacityPreferences?.DismissContextWarning() ?? ContextCapacityFeedback.DismissWarning()) { capacityRevision = -1; RefreshCapacityFeedback(); } });
        }
        public void RefreshCapacityFeedback()
        {
            if (capacityError == null) return;
            var state = CapacityState;
            bool dismissed = CapacityWarningDismissed;
            if (capacityRevision != state.Revision || capacityEnglish != SettingsUiLanguage.IsEnglish || capacityDismissed != dismissed)
            {
                capacityRevision = state.Revision; capacityEnglish = SettingsUiLanguage.IsEnglish; capacityDismissed = dismissed;
                capacityErrorText.text = ContextCapacityFeedback.ErrorText;
                capacityWarningText.text = ContextCapacityFeedback.WarningText;
                FitCapacityCard(capacityError, capacityErrorText);
                FitCapacityCard(capacityWarning, capacityWarningText);
                capacityDismiss.GetComponentInChildren<TMP_Text>(true).text = SettingsUiLanguage.T("关闭，不再提醒", "Dismiss, don't remind again");
                bool error = state.Errors.Count > 0;
                capacityError.SetActive(error);
                capacityWarning.SetActive(!error && state.WarningPending && !dismissed);
            }
            if (Application.isPlaying && state.Errors.Count > 0 && !checkingCapacity && Time.unscaledTime >= nextCapacityCheck)
                StartCoroutine(RecheckCapacity());
        }
        private static void FitCapacityCard(GameObject card, TMP_Text text)
        {
            float preferred = text.GetPreferredValues(text.text, 856, 0).y;
            bool dismissible = card.GetComponentInChildren<Button>(true) != null;
            float footer = dismissible ? 55 : 16;
            text.rectTransform.offsetMin = new Vector2(22, footer);
            ((RectTransform)card.transform).sizeDelta = new Vector2(900, Mathf.Max(dismissible ? 190 : 110, preferred + footer + 20));
        }
        private IEnumerator RecheckCapacity()
        {
            checkingCapacity = true; nextCapacityCheck = Time.unscaledTime + 8;
            var client = FindAnyObjectByType<ModelConfigClient>();
            if (client != null)
            {
                var errors = CapacityState.Errors.ToArray();
                for (int i=0;i<errors.Length;i+=64)
                    yield return client.RecheckContextCapacity(errors.Skip(i).Take(64).ToArray(), (value,error) =>
                    { if (error == null) CapacityState.Rechecked(value); });
            }
            checkingCapacity = false;
        }
    }
}
