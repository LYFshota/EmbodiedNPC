using System;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Cafe-local Cancel owner. Borrows existing player/UI locks; never pauses the world.</summary>
    public sealed partial class CafeUiFlowController : MonoBehaviour
    {
        public const string MenuResource = "CafeUI/CafeGameMenu";
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private GameObject menuSurface, helpSurface;
        [SerializeField] private Button resumeButton, settingsButton, helpButton, helpBack;
        [SerializeField] private TMP_Text heading, hint, helpText;
        private CafeDemoDeveloperPanel settings;
        private CafeDemoPlayerController player;
        private CafeDemoOrbitCamera orbit;
        private PlayerDialogueInteractor interactor;
        private DialogueController dialogue;
        private DialogueInputModeController inputMode;
        private DialogueHistoryPanel history;
        private PlayerEmotePanel emotes;
        private InputSystemUIInputModule uiModule;
        private InputActionReference previousUiCancel;
        private InputAction cancel;
        private Keyboard keyboard;
        private string composition = "";
        private bool bound, ownsInput, playerWasSuspended, orbitWasSuspended, interactionWasBlocked, pointerWasBlocked;
        private bool pointerWasCaptured, previousExternalCancel, previousExternalUiLock, focused = true;
        private Func<bool> previousInputGate;
        private GameObject previousSelection;
        private int cancelFrame = -1, changedFrame = -1;
        private TMP_FontAsset runtimeFont;
        private Button exitButton, exitConfirm, exitCancel;
        private GameObject exitSurface;
        private TMP_Text exitWarning;
        private TaskRuntimeBridge taskRuntime;
        private bool quitting;
        public bool IsMenuOpen => ownsInput;
        public bool IsHelpOpen => helpSurface != null && helpSurface.activeSelf;
        public bool IsExitConfirmationOpen => exitSurface != null && exitSurface.activeSelf;
        public bool BlocksGameplayInput => ownsInput || (settings != null && settings.IsOpen) ||
            cancelFrame == Time.frameCount || changedFrame == Time.frameCount || previousInputGate?.Invoke() == true;

        public static void Attach(CafeDemoDeveloperPanel settings, CafeDemoPlayerController player,
            CafeDemoOrbitCamera orbit, DialogueController dialogue)
        {
            if (settings.UiFlow != null) return;
            var prefab = Resources.Load<GameObject>(MenuResource);
            if (prefab == null) { Debug.LogError("Cafe game menu prefab is missing."); return; }
            var flow = Instantiate(prefab, settings.transform.parent).GetComponent<CafeUiFlowController>();
            // This overlay is nested in Cafe's existing screen canvas. Do not retain the
            // standalone authoring Canvas's driven scale/size from an unopened Game View.
            var rect = (RectTransform)flow.transform;
            rect.localScale = Vector3.one; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(.5f,.5f); rect.offsetMin = rect.offsetMax = Vector2.zero;
            flow.Configure(settings, player, orbit, dialogue);
        }
        public void Configure(CafeDemoDeveloperPanel host, CafeDemoPlayerController movement,
            CafeDemoOrbitCamera camera, DialogueController controller)
        {
            settings = host; player = movement; orbit = camera; dialogue = controller;
            interactor = player.GetComponent<PlayerDialogueInteractor>();
            inputMode = FindAnyObjectByType<DialogueInputModeController>();
            history = FindAnyObjectByType<DialogueHistoryPanel>(); emotes = FindAnyObjectByType<PlayerEmotePanel>();
            uiModule = FindAnyObjectByType<InputSystemUIInputModule>();
            if (isActiveAndEnabled) Bind();
        }
        private void Awake()
        {
            menuSurface.SetActive(false); helpSurface.SetActive(false);
            var texts = GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 0 && texts[0].font?.sourceFontFile != null)
            {
                runtimeFont = TMP_FontAsset.CreateFontAsset(texts[0].font.sourceFontFile);
                runtimeFont.isMultiAtlasTexturesEnabled = true;
                foreach (var text in texts) text.font = runtimeFont;
            }
            CreateExitControls();
            CreateCommissionControls();
            CreateCapacityFeedback();
        }
        private void CreateExitControls()
        {
            // Borrow the menu's own button/card styles, without a second menu prefab.
            exitButton = Instantiate(helpButton, helpButton.transform.parent);
            exitButton.name = "ExitGame"; exitButton.onClick = new Button.ButtonClickedEvent();
            ((RectTransform)exitButton.transform).anchoredPosition = new Vector2(0, -507);
            ((RectTransform)helpButton.transform.parent).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 628);
            exitSurface = Instantiate(helpSurface, helpSurface.transform.parent); exitSurface.name = "ExitConfirmation";
            exitWarning = exitSurface.GetComponentsInChildren<TMP_Text>(true).Single(t => t.name == helpText.name);
            exitCancel = exitSurface.GetComponentInChildren<Button>(true);
            exitConfirm = Instantiate(exitCancel, exitCancel.transform.parent); exitConfirm.name = "ConfirmExitGame";
            exitCancel.onClick = new Button.ButtonClickedEvent(); exitConfirm.onClick = new Button.ButtonClickedEvent();
            ((RectTransform)exitWarning.transform.parent).sizeDelta = new Vector2(700, 440);
            var textRect = (RectTransform)exitWarning.transform;
            textRect.anchoredPosition = new Vector2(0, -174); textRect.sizeDelta = new Vector2(-88, 260);
            exitWarning.fontSize = 24;
            foreach (var pair in new[] { (exitCancel, -146f), (exitConfirm, 146f) })
            {
                var rect = (RectTransform)pair.Item1.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0); rect.pivot = new Vector2(.5f,.5f);
                rect.anchoredPosition = new Vector2(pair.Item2, 58); rect.sizeDelta = new Vector2(270, 64);
            }
            exitSurface.SetActive(false);
        }
        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshLanguage; RefreshLanguage();
            if (settings != null) Bind();
        }
        private void Bind()
        {
            if (bound) return;
            bound = true; settings.UiFlow = this;
            settings.Opened += SettingsOpened; settings.Closed += SettingsClosed;
            if (emotes != null) emotes.UiFlow = this;
            if (interactor != null)
            {
                previousExternalCancel = interactor.ExternalCancelRouting;
                previousInputGate = interactor.UiInputBlocked;
                interactor.ExternalCancelRouting = true; interactor.UiInputBlocked = () => BlocksGameplayInput;
            }
            if (inputMode != null) previousExternalUiLock = inputMode.ExternalUiInputBlocked;
            // Route UI/Cancel here as well as keyboard ESC. TMP/UI's own EventSystem Cancel must not
            // hide a dropdown first and then let the same press close its Settings host.
            if (uiModule != null) { previousUiCancel = uiModule.cancel; uiModule.cancel = null; }
            cancel = inputActions.FindAction("UI/Cancel", true).Clone();
            cancel.performed += OnCancel; cancel.Enable();
            resumeButton.onClick.AddListener(Resume); settingsButton.onClick.AddListener(OpenSettings);
            helpButton.onClick.AddListener(OpenHelp); helpBack.onClick.AddListener(CloseHelp);
            exitButton.onClick.AddListener(OpenExitConfirmation); exitCancel.onClick.AddListener(CancelExit);
            exitConfirm.onClick.AddListener(ConfirmExit);
            BindKeyboard();
        }
        private void Update() { BindKeyboard(); if(ownsInput) RefreshTaskFeedback(); RefreshCapacityFeedback(); }

        public static string TaskFeedbackText(string code)
            => TaskFeedbackText(code, SettingsUiLanguage.IsEnglish);
        private static string TaskFeedbackText(string code, bool english)
        {
            string T(string zh, string en) => english ? en : zh;
            switch(code)
            {
                case "saved": return ""; // Fresh committed saves use the existing short ActionFeedback Toast.
                case "save_failed": return T("任务未能保存。","Task could not be saved.");
                case "cancelled": return ""; // Only the trusted cancellation acknowledgement emits a Toast.
                case "cancel_pending": case "cancellation_requested": return T("取消请求处理中，尚未确认停止。","Cancellation pending; stopping is not yet confirmed.");
                case "too_late": return T("动作已经完成，取消过晚。","The action completed before cancellation.");
                case "completed": return ""; // Actual world-result feedback remains owned by ActionFeedbackPresenter.
                case "result_pending": return T("执行结果待同步。","Execution result awaiting synchronization.");
                case "failed": return T("任务执行失败，未自动重做。","Task execution failed; it has not been repeated automatically.");
                case "blocked": case "needs_verification": return T("执行状态需要核实，任务不会自动重做。","Execution needs verification; it will not be repeated automatically.");
                default:return "";
            }
        }
        private void BindKeyboard()
        {
            if (keyboard == Keyboard.current) return;
            if (keyboard != null) keyboard.onIMECompositionChange -= OnComposition;
            keyboard = Keyboard.current; composition = "";
            if (keyboard != null) keyboard.onIMECompositionChange += OnComposition;
        }
        private void OnComposition(IMECompositionString value) => composition = value.ToString();
        private void OnCancel(InputAction.CallbackContext context) => RequestCancel();
        public void RequestCancel()
        {
            if (!bound || !isActiveAndEnabled || cancelFrame == Time.frameCount) return;
            cancelFrame = Time.frameCount;
            // Keep the native IME's current draft authoritative when composition is observable.
            if (!string.IsNullOrEmpty(composition)) return;
            // Settings is a sibling of this prefab. Check its active dropdown before its host.
            foreach (var dropdown in FindObjectsByType<TMP_Dropdown>())
                if (dropdown.gameObject.scene == gameObject.scene && dropdown.IsExpanded) { dropdown.Hide(); return; }
            if (settings.IsOpen)
            {
                if (!settings.CancelTopConfirmation()) settings.Close();
                return;
            }
            if (history != null && history.IsOpen) { history.Close(); RestoreDialogueFocus(); return; }
            if (emotes != null && emotes.IsOpen) { emotes.Close(); return; }
            if (emotes != null && emotes.BlocksOtherUi) return;
            if (IsExitConfirmationOpen) { CancelExit(); return; }
            if (CloseCommissionLayer()) return;
            if (IsHelpOpen) { CloseHelp(); return; }
            if (ownsInput) { Resume(); return; }
            if (settings.IsStartupAdmissionHeld || dialogue == null || !dialogue.IsSetupReady)
            { settings.OpenStartupSettings(); return; }
            if (dialogue.IsPresentationVisible) { interactor?.EndDialogue(); return; }
            OpenMenu();
        }
        public void OpenMenu()
        {
            if (!bound || ownsInput || settings.IsOpen || settings.IsStartupAdmissionHeld || dialogue == null ||
                !dialogue.IsSetupReady || dialogue.IsPresentationVisible || (emotes != null && emotes.BlocksOtherUi)) return;
            playerWasSuspended = player.IsInputSuspended; orbitWasSuspended = orbit.IsInputSuspended;
            interactionWasBlocked = interactor != null && interactor.InteractionBlocked;
            pointerWasBlocked = inputMode != null && inputMode.PointerExitBlocked;
            pointerWasCaptured = orbit.PointerCaptured;
            previousSelection = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            ownsInput = true; changedFrame = Time.frameCount;
            ApplyMenuLock(); menuSurface.SetActive(true); helpSurface.SetActive(false);
            taskRuntime=FindAnyObjectByType<TaskRuntimeBridge>();
            if(taskRuntime!=null) foreach(string agent in taskRuntime.StatusAgentIds) taskRuntime.RefreshStatus(agent);
            RefreshTaskFeedback();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(resumeButton.gameObject);
        }
        private void ApplyMenuLock()
        {
            player.SetDialogueInputSuspended(true); orbit.SetDialogueInputSuspended(true);
            orbit.SetSettingsCursorMode(true); interactor?.SetInteractionBlocked(true);
            if (inputMode != null) { inputMode.PointerExitBlocked = true; inputMode.ExternalUiInputBlocked = true; }
        }
        public void Resume()
        {
            if (!ownsInput || settings.IsOpen) return;
            ownsInput = false; changedFrame = Time.frameCount;
            menuSurface.SetActive(false); helpSurface.SetActive(false); exitSurface.SetActive(false);
            commissionSurface.SetActive(false); commissionConfirming=false;
            bool otherUi = dialogue != null && dialogue.IsPresentationVisible || settings.IsStartupAdmissionHeld ||
                emotes != null && emotes.IsBusy;
            if (inputMode != null)
            {
                inputMode.PointerExitBlocked = pointerWasBlocked;
                inputMode.ExternalUiInputBlocked = previousExternalUiLock;
            }
            player.SetDialogueInputSuspended(playerWasSuspended || otherUi);
            orbit.SetDialogueInputSuspended(orbitWasSuspended || otherUi);
            interactor?.SetInteractionBlocked(interactionWasBlocked || otherUi);
            orbit.SetSettingsCursorMode(otherUi);
            orbit.RestorePointerAfterUi(pointerWasCaptured && focused);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(
                previousSelection != null && previousSelection.activeInHierarchy ? previousSelection : null);
            previousSelection = null;
        }
        public void OpenSettings() { if (ownsInput) settings.Open(); }
        private void SettingsOpened()
        {
            if (inputMode != null) inputMode.ExternalUiInputBlocked = true;
            if (ownsInput) { menuSurface.SetActive(false); helpSurface.SetActive(false); commissionSurface.SetActive(false); commissionConfirming=false; }
        }
        private void SettingsClosed()
        {
            changedFrame = Time.frameCount;
            if (inputMode != null) inputMode.ExternalUiInputBlocked = ownsInput || previousExternalUiLock;
            if (!ownsInput) return;
            ApplyMenuLock(); menuSurface.SetActive(true);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(resumeButton.gameObject);
        }
        public void OpenHelp()
        {
            if (!ownsInput || settings.IsOpen) return;
            helpText.text = SettingsOperationHelp.Text;
            menuSurface.SetActive(false); helpSurface.SetActive(true); commissionSurface.SetActive(false); commissionConfirming=false;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(helpBack.gameObject);
        }
        public void CloseHelp()
        {
            helpSurface.SetActive(false); if (ownsInput) menuSurface.SetActive(true);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(resumeButton.gameObject);
        }
        public void OpenExitConfirmation()
        {
            if (!ownsInput || settings.IsOpen || quitting) return;
            RefreshExitWarning(); menuSurface.SetActive(false); exitSurface.SetActive(true);
            commissionSurface.SetActive(false); commissionConfirming=false;
            EventSystem.current?.SetSelectedGameObject(exitCancel.gameObject);
        }
        private void RefreshExitWarning()
        {
            var backend = FindAnyObjectByType<BackendWebSocketClient>();
            int pending = backend == null ? -1 : backend.PendingExperienceCount;
            bool busy = FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None)
                .Any(d => d.PendingRequest != null || d.HasPendingActionOutcome) ||
                FindObjectsByType<GoalPlanRunner>(FindObjectsSortMode.None).Any(g => g.HasActiveTask);
            exitWarning.text = SettingsUiLanguage.T("退出游戏？", "Exit game?") + "\n\n" +
                (pending > 0 ? SettingsUiLanguage.T($"尚有 {pending} 条记录未确认。\n", $"{pending} records are awaiting acknowledgement.\n") : "") +
                (busy ? SettingsUiLanguage.T("NPC 仍有任务在处理。\n", "NPC work is still in progress.\n") : "") +
                SettingsUiLanguage.T("退出不代表全部工作已保存；未确认的内容可能丢失。", "Exiting does not confirm all work is saved; unacknowledged content may be lost.");
        }
        public void CancelExit()
        {
            if (quitting) return;
            exitSurface.SetActive(false); if (ownsInput) menuSurface.SetActive(true);
            EventSystem.current?.SetSelectedGameObject(exitButton.gameObject);
        }
        private void ConfirmExit()
        {
            if (!IsExitConfirmationOpen || quitting) return;
            quitting = true; exitConfirm.interactable = false;
            Application.Quit(); // Normal exit; Portable owns Backend shutdown. Never use restart code 73.
        }
        private void RestoreDialogueFocus()
        {
            var input = dialogue == null ? null : dialogue.DialogueInputField;
            if (dialogue != null && dialogue.IsPresentationVisible && input != null && input.isActiveAndEnabled && input.interactable)
            { input.Select(); input.ActivateInputField(); }
        }
        private void RefreshLanguage()
        { ApplyMenuLanguage(SettingsUiLanguage.IsEnglish); }
        private void ApplyMenuLanguage(bool english)
        {
            if (heading == null) return;
            heading.text = english ? "Game menu" : "游戏菜单";
            hint.text = english ? "The cafe keeps running while this menu is open." : "菜单打开时，咖啡馆仍会继续运行。";
            resumeButton.GetComponentInChildren<TMP_Text>(true).text = english ? "Resume" : "继续游戏";
            settingsButton.GetComponentInChildren<TMP_Text>(true).text = english ? "Settings" : "设置";
            helpButton.GetComponentInChildren<TMP_Text>(true).text = english ? "Controls" : "操作说明";
            helpBack.GetComponentInChildren<TMP_Text>(true).text = SettingsUiLanguage.T("返回游戏菜单", "Back to game menu");
            helpText.text = SettingsOperationHelp.Text;
            if (exitButton != null)
            {
                exitButton.GetComponentInChildren<TMP_Text>(true).text = english ? "Exit Game" : "退出游戏";
                exitCancel.GetComponentInChildren<TMP_Text>(true).text = SettingsUiLanguage.T("取消", "Cancel");
                exitConfirm.GetComponentInChildren<TMP_Text>(true).text = SettingsUiLanguage.T("退出游戏", "Exit Game");
                if (IsExitConfirmationOpen) RefreshExitWarning();
            }
            RefreshCommissionLanguage(english);
        }
        private void OnApplicationFocus(bool hasFocus)
        {
            focused = hasFocus;
            if (ownsInput) ApplyMenuLock();
        }
        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage;
            if (keyboard != null) keyboard.onIMECompositionChange -= OnComposition;
            keyboard = null; composition = "";
            if (!bound) return;
            if (ownsInput && settings != null && !settings.IsOpen) Resume();
            settings.Opened -= SettingsOpened; settings.Closed -= SettingsClosed; settings.UiFlow = null;
            if (emotes != null) emotes.UiFlow = null;
            if (interactor != null) { interactor.ExternalCancelRouting = previousExternalCancel; interactor.UiInputBlocked = previousInputGate; }
            if (inputMode != null) inputMode.ExternalUiInputBlocked = previousExternalUiLock;
            if (uiModule != null && uiModule.cancel == null) uiModule.cancel = previousUiCancel;
            cancel.performed -= OnCancel; cancel.Disable(); cancel.Dispose(); cancel = null;
            resumeButton.onClick.RemoveListener(Resume); settingsButton.onClick.RemoveListener(OpenSettings);
            helpButton.onClick.RemoveListener(OpenHelp); helpBack.onClick.RemoveListener(CloseHelp);
            exitButton.onClick.RemoveListener(OpenExitConfirmation); exitCancel.onClick.RemoveListener(CancelExit);
            exitConfirm.onClick.RemoveListener(ConfirmExit);
            bound = false;
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
