using System;
using System.Collections;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Title-only visibility adapter. All configuration operations remain in the original form.</summary>
    public sealed class TitleSettingsHost : MonoBehaviour
    {
        [SerializeField] private GameObject surface;
        [SerializeField] private FirstRunModelSetup form;
        [SerializeField] private Button closeButton, saveButton;
        private PlayerSettingsNavigation navigation;
        public PlayerSettingsNavigation Navigation => navigation;
        public event Action Activated;
        public event Action Closed;
        public bool IsOpen => surface != null && surface.activeSelf;
        public bool IsBusy => IsOpen && form != null && form.IsSavingSettings;
        public bool AIRequiresRestart => form != null && form.SavedAIRequiresRestart;

        private void OnEnable()
        {
            closeButton.onClick.AddListener(TryCloseFromButton);
            form.ConfigurationActivated += OnActivated;
            form.RestartToApplyRequested += RestartToApply;
        }
        private void OnDisable()
        {
            closeButton.onClick.RemoveListener(TryCloseFromButton);
            form.ConfigurationActivated -= OnActivated;
            form.RestartToApplyRequested -= RestartToApply;
        }
        public void Open(ModelConfigView config, string error)
        {
            if (IsOpen) return;
            surface.SetActive(true);
            if (config?.profiles != null) form.ApplyStartupResult(config, error);
            else form.ReloadForDeveloper();
            EnsureNavigation();
            navigation.SetDeveloper(false);
            if (config == null || !config.configured || error != null) navigation.ShowAi(false);
        }
        private void EnsureNavigation()
        {
            if (navigation != null) return;
            var window = surface.transform.Find("Window");
            var model = window.Find("ModelPage").gameObject;
            var pages = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in window)
                if (child.name.EndsWith("Page")) pages.Add(child.gameObject);
            navigation = new PlayerSettingsNavigation(window, form, FindAnyObjectByType<CafeBackgroundMusic>(), null,
                pages.ToArray(), next => { if (!form.BlocksSettingsNavigation) next(); },
                () => { foreach (var page in pages) page.SetActive(page == model); },
                Array.Empty<(string, string, Action)>());
        }
        private void OnDestroy() => navigation?.Dispose();
        public bool TryClose()
        {
            if (!IsOpen) return false;
            if (form.ConsumeSettingsEscape() || (navigation != null && navigation.ConsumeBack())) return false;
            foreach (var dropdown in surface.GetComponentsInChildren<TMP_Dropdown>())
                if (dropdown.IsExpanded) { dropdown.Hide(); return false; }
            // Keep the form's explicit consent/unsaved-draft actions authoritative.
            if (IsBusy || surface.transform.Find("ModelProfileConfirmation") != null) return false;
            foreach (var t in surface.GetComponentsInChildren<Transform>())
                if (t.name == "ModelProfileConfirmation") return false;
            surface.SetActive(false);
            Closed?.Invoke();
            return true;
        }
        private void TryCloseFromButton() => TryClose();
        private void OnActivated() => StartCoroutine(NotifyAfterSave());
        private void RestartToApply() => PortableAIConfigRestart.Request();
        private IEnumerator NotifyAfterSave()
        {
            // ConfigurationActivated can precede ContextPolicy saving in the same form operation.
            yield return null;
            while (IsBusy) yield return null;
            Activated?.Invoke();
        }
    }
}
