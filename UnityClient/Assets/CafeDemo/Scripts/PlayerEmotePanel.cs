using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>A local modal view. It borrows the existing input/cursor contract only while it owns it.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-200)]
    public sealed class PlayerEmotePanel : MonoBehaviour
    {
        [SerializeField] private PlayerEmoteController emotes;
        [SerializeField] private CafeDemoPlayerController player;
        [SerializeField] private CafeDemoOrbitCamera orbit;
        [SerializeField] private PlayerDialogueInteractor interactor;
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private CafeDemoDeveloperPanel settings;
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private GameObject hint;
        [SerializeField] private GameObject window;
        [SerializeField] private Button hintButton, closeButton, waveButton, cheerButton, proudButton;
        private InputAction toggle, cancel;
        private bool ownsInput, playerWasSuspended, orbitWasSuspended, interactionWasBlocked;
        private bool startedAction;
        private int closedFrame = -1;
        private GameObject previousSelection;
        private TMP_FontAsset runtimeFont;
        internal CafeUiFlowController UiFlow { get; set; }

        public bool IsOpen => window != null && window.activeSelf;
        public bool IsBusy => IsOpen || (emotes != null && emotes.IsActive);
        public bool BlocksOtherUi => IsBusy || closedFrame == Time.frameCount;

        private void Awake()
        {
            window.SetActive(false);
            // Dynamic glyphs must not dirty the shared CJK font/atlas or another chat's font WIP.
            var texts = GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0 && texts[0].font != null && texts[0].font.sourceFontFile != null)
            {
                runtimeFont = TMP_FontAsset.CreateFontAsset(texts[0].font.sourceFontFile);
                foreach (var text in texts) text.font = runtimeFont;
            }
        }
        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshLanguage;
            RefreshLanguage();
            if (emotes == null || inputActions == null) return;
            emotes.MenuHandlesInput = true;
            emotes.ActivityChanged += OnActivityChanged;
            toggle = inputActions.FindAction("Player/Wave", true).Clone(); toggle.Enable();
            cancel = inputActions.FindAction("UI/Cancel", true).Clone(); cancel.Enable();
            hintButton.onClick.AddListener(Open);
            closeButton.onClick.AddListener(Close);
            waveButton.onClick.AddListener(Wave);
            cheerButton.onClick.AddListener(Cheer);
            proudButton.onClick.AddListener(Proud);
        }
        private void RefreshLanguage()
        {
            var caption = hint == null ? null : hint.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (caption == null) return;
            caption.text = SettingsUiLanguage.T("表情", "Emotes");
            caption.enableAutoSizing = true; caption.fontSizeMin = 20; caption.fontSizeMax = 25;
        }
        private bool CanOpen()
        {
            if (UiFlow != null && UiFlow.BlocksGameplayInput) return false;
            if (emotes == null || !emotes.isActiveAndEnabled || emotes.IsActive || player == null ||
                !player.isActiveAndEnabled || player.IsInputSuspended || player.IsPresentationInputSuppressed ||
                orbit == null || orbit.IsInputSuspended || orbit.IsPresentationUiHidden ||
                (settings != null && settings.IsOpen) || (dialogue != null && dialogue.IsPresentationVisible)) return false;
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            return selected == null || (selected.GetComponentInParent<TMP_InputField>() == null &&
                selected.GetComponentInParent<InputField>() == null);
        }
        private void Update()
        {
            if (toggle != null && toggle.WasPressedThisFrame())
            {
                if (IsOpen) Close(); else Open();
            }
            if (UiFlow == null && IsOpen && cancel != null && cancel.WasPressedThisFrame()) Close();
            if (hint != null) hint.SetActive(!IsOpen && (emotes == null || !emotes.IsActive) &&
                (settings == null || !settings.IsOpen) && (dialogue == null || !dialogue.IsPresentationVisible) &&
                (UiFlow == null || !UiFlow.BlocksGameplayInput) &&
                (orbit == null || !orbit.IsPresentationUiHidden));
        }
        public void Open()
        {
            if (IsOpen || !CanOpen()) return;
            AcquireInput();
            window.SetActive(true);
            hint.SetActive(false);
            orbit.SetDialogueCursorMode(true);
            previousSelection = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(waveButton.gameObject);
        }
        public void Close()
        {
            if (!IsOpen) return;
            window.SetActive(false);
            closedFrame = Time.frameCount;
            orbit?.SetDialogueCursorMode(false);
            RestoreSelection();
            if (!startedAction) ReleaseInput();
        }
        private void Wave() => Select("wave");
        private void Cheer() => Select("cheer");
        private void Proud() => Select("proud");
        public bool Select(string emoteId)
        {
            if (!IsOpen || emotes == null || emotes.IsActive) return false;
            Close(); // Release this modal's lock before requesting the existing action controller.
            bool accepted = emotes.TryPlay(emoteId);
            if (accepted) startedAction = true;
            return accepted;
        }
        private void OnActivityChanged(bool active)
        {
            if (active) AcquireInput();
            else { startedAction = false; ReleaseInput(); }
        }
        private void AcquireInput()
        {
            if (ownsInput) return;
            ownsInput = true;
            playerWasSuspended = player.IsInputSuspended;
            orbitWasSuspended = orbit.IsInputSuspended;
            interactionWasBlocked = interactor != null && interactor.InteractionBlocked;
            player.SetDialogueInputSuspended(true);
            orbit.SetDialogueInputSuspended(true);
            interactor?.SetInteractionBlocked(true);
        }
        private void ReleaseInput()
        {
            if (!ownsInput) return;
            ownsInput = false;
            bool otherUi = (dialogue != null && dialogue.IsPresentationVisible) || (settings != null && settings.IsOpen);
            if (player != null) player.SetDialogueInputSuspended(playerWasSuspended || otherUi);
            if (orbit != null) orbit.SetDialogueInputSuspended(orbitWasSuspended || otherUi);
            interactor?.SetInteractionBlocked(interactionWasBlocked || otherUi);
        }
        private void RestoreSelection()
        {
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(previousSelection);
            previousSelection = null;
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) { Close(); if (startedAction) emotes?.Cancel(); }
        }
        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage;
            Close();
            if (startedAction) emotes?.Cancel();
            ReleaseInput();
            if (emotes != null) { emotes.ActivityChanged -= OnActivityChanged; emotes.MenuHandlesInput = false; }
            toggle?.Disable(); toggle?.Dispose(); toggle = null;
            cancel?.Disable(); cancel?.Dispose(); cancel = null;
            hintButton?.onClick.RemoveListener(Open); closeButton?.onClick.RemoveListener(Close);
            waveButton?.onClick.RemoveListener(Wave); cheerButton?.onClick.RemoveListener(Cheer); proudButton?.onClick.RemoveListener(Proud);
        }
        private void OnDestroy()
        {
            if (runtimeFont == null) return;
            foreach (var texture in runtimeFont.atlasTextures) if (texture != null) Destroy(texture);
            if (runtimeFont.material != null) Destroy(runtimeFont.material);
            Destroy(runtimeFont);
        }
    }
}
