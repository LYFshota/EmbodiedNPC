using System.Collections;
using System.Collections.Generic;
using System;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Cafe settings and startup admission owner, including production task bridge/source wiring. Removing developer UI must preserve that wiring; saved model settings apply through Backend restart.</summary>
    public sealed partial class CafeDemoDeveloperPanel : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private ModelConfigClient client;
        [SerializeField] private CafeDemoPlayerController player;
        [SerializeField] private CafeDemoOrbitCamera orbit;
        [SerializeField] private FirstRunModelSetup modelForm;
        [SerializeField] private Button entryButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button overviewButton;
        [SerializeField] private Button modelButton;
        [SerializeField] private Button npcButton;
        [SerializeField] private Button debugButton;
        [SerializeField] private Button traceButton;
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private GameObject overviewPage;
        [SerializeField] private CafeBackgroundMusic backgroundMusic;
        [SerializeField] private GameObject modelPage;
        [SerializeField] private GameObject npcPage;
        [SerializeField] private GameObject debugPage;
        [SerializeField] private GameObject tracePage;
        [SerializeField] private CafeNpcCharacterSettings npcSettings;
        [SerializeField] private CafeNpcDebugPanel debugPanel;
        [SerializeField] private CafeRuntimeTracePanel tracePanel;
        [SerializeField] private TMP_Text backendText;
        [SerializeField] private TMP_Text mainText;
        [SerializeField] private TMP_Text embeddingText;
        [SerializeField] private TMP_Text healthText;
        [SerializeField] private TMP_Text restartText;

        private bool playerWasSuspended;
        private bool orbitWasSuspended;
        private bool loadingStatus;
        private Coroutine statusRequest;
        private bool escapeWasPressed;
        private int dialogueClosedFrame = -1;
        [SerializeField] private TMP_Dropdown languageDropdown;
        private RuntimeStatusView lastStatus;
        private bool statusFailed;
        private string statusError;
        private long statusHttpCode;
        private DateTime? statusCheckedAt;
        private bool startupConfigResolved;
        private ModelConfigView startupConfigSnapshot;
        private const float StartupConfigRetryInterval = .5f;
        private const float StartupConfigReadinessTimeout = 6f;
        private Coroutine startupRequest;
        private IEnumerator startupHttpRequest;
        private BackendWebSocketClient startupBackend;
        private bool startupStarted;
        private bool startupTransientFailure;
        private bool startupRequestInProgress;
        private bool startupRetryWake;
        private int startupGeneration;
        private float startupConfigStartedAt;
        // Narrow deterministic seams for the Cafe startup coroutine, never serialized settings.
        private Func<float> startupClock;
        private Func<Action<ModelConfigView, string>, int, IEnumerator> startupGetConfig;
        private float StartupNow => startupClock == null ? Time.realtimeSinceStartup : startupClock();
        private CafeSettingsLayout settingsLayout;
        private GameObject knowledgePage;
        private Button knowledgeButton;
        private CafeWorldKnowledgePanel knowledgeSettings;
        private bool interactionWasBlocked;
        private EmbodiedNPC.Dialogue.Input.DialogueInputModeController dialogueInputMode;
        private bool pointerExitWasBlocked;
        private TMP_InputField dialogueFocusToRestore, lastDialogueFocus;
        private string focusConversation, lastFocusConversation;
        private DialogueParticipant focusTarget;
        private int lastDialogueFocusFrame = -2;
        private bool startupAdmissionHeld, startupConfigAccepted;
        private bool startupPlayerSuspended, startupOrbitSuspended, startupInteractionBlocked;
        private GameObject startupOverlay;
        // Same narrow test boundary as startupGetConfig; production always checks the scene transport.
        private Func<bool> startupTransportReady;

        public bool IsOpen => panelRoot != null && panelRoot.activeSelf;
        // UiFlow retains menu ownership through help, commissions and exit confirmation.
        public bool BlocksWorldDebugOverlays => IsOpen || IsStartupAdmissionHeld || (UiFlow != null && UiFlow.IsMenuOpen);
        public CafeBackgroundMusic BackgroundMusic => backgroundMusic;
        public FirstRunModelSetup ModelForm => modelForm;
        public TMP_Dropdown LanguageDropdown => languageDropdown;
        public PlayerSettingsNavigation PlayerNavigation => settingsLayout?.PlayerNavigation;
        internal CafeUiFlowController UiFlow { get; set; }
        public bool IsStartupAdmissionHeld => startupAdmissionHeld || !startupConfigResolved;
        public event Action Opened;
        public event Action Closed;
        private void OnDestroy() { PlayerNavigation?.Dispose(); startupProgress?.Dispose(); }

        public void RequestSettingsNavigation(Action next)
        {
            if (!IsOpen || (modelForm != null && modelForm.BlocksSettingsNavigation)) return;
            if (LeaveKnowledge(() => RequestSettingsNavigation(next))) return;
            next();
        }
        public bool IsEntryVisible => entryButton != null && entryButton.gameObject.activeSelf;

        private void Awake()
        {
            dialogue?.SetSetupReady(false);
            if (Application.isPlaying && gameObject.scene.name == "CafeDemoV2" && player != null && orbit != null)
                CafeUiFlowController.Attach(this, player, orbit, dialogue);
        }

        private void OnEnable()
        {
            EnsureKnowledgePage();
            EnsureLanguageDropdown();
            StyleEntryButton();
            SettingsUiLanguage.Changed += RefreshLanguage;
            entryButton.onClick.AddListener(OpenFromHud);
            closeButton.onClick.AddListener(CloseWindow);
            overviewButton.onClick.AddListener(ShowOverview);
            modelButton.onClick.AddListener(ShowModel);
            npcButton.onClick.AddListener(ShowNpc);
            knowledgeButton?.onClick.AddListener(ShowKnowledge);
            if (debugButton != null) debugButton.onClick.AddListener(ShowDebug);
            if (traceButton != null) traceButton.onClick.AddListener(ShowTrace);
            if (dialogue != null) dialogue.PresentationVisibilityChanged += OnDialogueVisibilityChanged;
            if (modelForm != null) modelForm.ConfigurationActivated += OnConfigurationActivated;
            if (modelForm != null) modelForm.RuntimeStatusChecked += StartupRuntimeChecked;
            if (client != null) client.StartupCheckProgress += StartupHttpChanged;
            startupBackend = client == null ? null : client.BackendClient;
            if (startupBackend != null) startupBackend.ConnectionStatusChanged += OnStartupConnectionStatusChanged;
            panelRoot.SetActive(false);
            RefreshLanguage();
            UpdateEntry();
            if (startupStarted && !startupConfigResolved) BeginStartupConfig();
        }

        private void Start()
        {
            startupStarted = true;
            modelForm?.PrepareUnconfiguredDefaults();
            BeginStartupConfig();
        }

        private void BeginStartupConfig()
        {
            if (startupRequest != null || startupConfigResolved || !isActiveAndEnabled) return;
            dialogue?.SetSetupReady(false);
            HoldStartupAdmission();
            modelForm?.ShowStartupConnecting();
            startupTransientFailure = startupRetryWake = false;
            startupRequest = StartCoroutine(ResolveStartupConfig());
        }

        private void OnStartupConnectionStatusChanged(string status)
        {
            ShowStartupTransport(status);
            if (startupAdmissionHeld && startupConfigAccepted)
            {
                if (status == "CONNECTED") TryCompleteStartupAdmission();
                else if (status == "OFFLINE") { Open(); ShowModelPage(); }
            }
            ShowStartupFailureInForm();
            if (status == "CONNECTED" && isActiveAndEnabled && !startupConfigResolved &&
                startupTransientFailure && !startupRequestInProgress) startupRetryWake = true;
            if (!loadingStatus && (lastStatus != null || statusFailed)) RefreshBackendLine();
        }

        private IEnumerator ResolveStartupConfig()
        {
            int generation = startupGeneration;
            startupConfigStartedAt = StartupNow;
            float deadline = startupConfigStartedAt + StartupConfigReadinessTimeout;
            string error = "BACKEND_UNAVAILABLE";
            while (StartupNow < deadline && generation == startupGeneration && !startupConfigResolved)
            {
                ModelConfigView config = null;
                error = "BACKEND_UNAVAILABLE";
                startupRetryWake = false;
                startupRequestInProgress = true;
                Action<ModelConfigView, string> done = (value, code) =>
                {
                    if (generation != startupGeneration || !isActiveAndEnabled || startupConfigResolved) return;
                    config = value;
                    error = code;
                };
                int timeout = Mathf.Max(1, Mathf.CeilToInt(deadline - StartupNow));
                startupHttpRequest = startupGetConfig != null ? startupGetConfig(done, timeout) :
                    client == null ? null : client.GetStartupConfig(done, timeout);
                // Drive the one request here so the outer deadline can abort a slow HTTP attempt.
                // Disposing Send's iterator disposes its UnityWebRequest on disable or timeout.
                while (startupHttpRequest != null && generation == startupGeneration && StartupNow < deadline &&
                       startupHttpRequest.MoveNext())
                {
                    var asyncOperation = startupHttpRequest.Current as AsyncOperation;
                    if (asyncOperation == null) yield return startupHttpRequest.Current;
                    else while (!asyncOperation.isDone && generation == startupGeneration && StartupNow < deadline)
                        yield return null;
                }
                DisposeStartupHttpRequest();
                startupRequestInProgress = false;
                if (generation != startupGeneration || !isActiveAndEnabled || startupConfigResolved) yield break;
                if (StartupNow >= deadline) { error = "TIMEOUT"; break; }
                startupTransientFailure = error == "BACKEND_UNAVAILABLE" || error == "TIMEOUT";
                if (!startupTransientFailure)
                {
                    if (error == null && config != null) startupBackend?.EnsureConnected();
                    startupRequest = null;
                    ApplyStartupConfig(config, error);
                    yield break;
                }
                float nextAttempt = Mathf.Min(deadline, StartupNow + StartupConfigRetryInterval);
                while (StartupNow < nextAttempt && !startupRetryWake && generation == startupGeneration)
                    yield return null;
            }
            if (generation != startupGeneration || !isActiveAndEnabled || startupConfigResolved) yield break;
            startupRequest = null;
            ApplyStartupConfig(null, error);
        }

        private void DisposeStartupHttpRequest()
        {
            (startupHttpRequest as IDisposable)?.Dispose();
            startupHttpRequest = null;
        }

        private void CancelStartupConfig()
        {
            startupGeneration++;
            if (startupRequest != null) StopCoroutine(startupRequest);
            startupRequest = null;
            DisposeStartupHttpRequest();
            startupRequestInProgress = startupRetryWake = startupTransientFailure = false;
        }

        public void ApplyStartupConfig(ModelConfigView config, string error = null)
        {
            if(error==null && config!=null)startupConfigSnapshot=config;
            CancelStartupConfig();
            startupConfigResolved = true;
            bool configured = error == null && config != null && config.configured;
            startupConfigAccepted = configured;
            ShowStartupConfig(config, error, configured);
            dialogue?.SetSetupReady(false);
            if (configured) TryCompleteStartupAdmission();
            else { Open(); ShowModelPage(); }
            if (!configured) modelForm?.PrepareUnconfiguredDefaults();
            if (IsOpen || !configured) modelForm?.ApplyStartupResult(config, error);
            ShowStartupFailureInForm();
            UpdateEntry();
        }

        private void OnConfigurationActivated()
        {
            CancelStartupConfig();
            startupConfigResolved = true;
            startupConfigAccepted = true;
            startupProgress?.ClearFailure();
            startupProgress?.Step(1,StartupStepState.Completed);
            startupProgress?.Step(2,StartupStepState.Completed);
            startupProgress?.Step(3,StartupStepState.Running);
            startupBackend?.EnsureConnected();
            TryCompleteStartupAdmission();
            UpdateEntry();
        }

        private void HoldStartupAdmission()
        {
            if (!startupAdmissionHeld)
            {
                startupPlayerSuspended = player.IsInputSuspended;
                startupOrbitSuspended = orbit.IsInputSuspended;
                var interaction = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
                startupInteractionBlocked = interaction != null && interaction.InteractionBlocked;
                startupAdmissionHeld = true;
            }
            player.SetDialogueInputSuspended(true);
            orbit.SetDialogueInputSuspended(true);
            orbit.SetSettingsCursorMode(true);
            player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>()?.SetInteractionBlocked(true);
            if (startupOverlay == null) CreateStartupOverlay();
            startupOverlay.SetActive(!IsOpen);
        }

        private void TryCompleteStartupAdmission()
        {
            bool connected = startupTransportReady != null ? startupTransportReady() : startupBackend != null && startupBackend.IsConnected;
            if (!startupConfigAccepted || !connected) return;
            var taskRegistry = FindAnyObjectByType<EmbodiedNPC.World.EntityRegistry>();
            var taskPlayer = player == null ? null : player.GetComponent<EmbodiedNPC.World.WorldEntity>();
            var taskBridge = GetComponent<EmbodiedNPC.Cognition.TaskRuntimeBridge>() ??
                gameObject.AddComponent<EmbodiedNPC.Cognition.TaskRuntimeBridge>();
            if(taskBridge.Admit(taskRegistry, taskPlayer))
            {
                var events=FindAnyObjectByType<EmbodiedNPC.Events.AgentEventRouter>();
                foreach(var agent in taskRegistry.RegisteredEntities)
                    if(agent.IsAgent(EmbodiedNPC.World.AgentRole.Npc))
                    {
                        var source=agent.GetComponent<EmbodiedNPC.Cognition.TaskConsiderationSource>() ?? agent.gameObject.AddComponent<EmbodiedNPC.Cognition.TaskConsiderationSource>();
                        source.Configure(taskBridge,taskRegistry,taskPlayer,events,startupBackend,dialogue);
                    }
            }
            startupProgress?.ClearFailure();
            startupProgress?.Step(3,StartupStepState.Completed);
            startupProgress?.Step(4,StartupStepState.Completed);
            dialogue?.SetSetupReady(true);
            if (!startupAdmissionHeld) return;
            startupAdmissionHeld = false;
            if (startupOverlay != null) startupOverlay.SetActive(false);
            if (IsOpen) return; // Settings continues to own its original input contract until Close.
            bool visible = dialogue != null && dialogue.IsPresentationVisible;
            player.SetDialogueInputSuspended(startupPlayerSuspended || visible);
            orbit.SetDialogueInputSuspended(startupOrbitSuspended || visible);
            orbit.SetSettingsCursorMode(false);
            player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>()?.SetInteractionBlocked(startupInteractionBlocked);
            ShowStartupControlRestored();
        }

        private void CreateStartupOverlay()
        {
            startupOverlay = new GameObject("CafeStartupAdmission", typeof(RectTransform), typeof(Image));
            startupOverlay.transform.SetParent(transform.parent, false);
            var rect = (RectTransform)startupOverlay.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            startupOverlay.GetComponent<Image>().color = new Color(.12f, .08f, .06f, .94f);
            CreateStartupProgress();
        }

        private void OnDisable()
        {
            if (client != null) client.StartupCheckProgress -= StartupHttpChanged;
            if (startupBackend != null) startupBackend.ConnectionStatusChanged -= OnStartupConnectionStatusChanged;
            startupBackend = null;
            CancelStartupConfig();
            SettingsUiLanguage.Changed -= RefreshLanguage;
            if (languageDropdown != null) languageDropdown.onValueChanged.RemoveListener(OnLanguageChanged);
            entryButton.onClick.RemoveListener(OpenFromHud);
            closeButton.onClick.RemoveListener(CloseWindow);
            overviewButton.onClick.RemoveListener(ShowOverview);
            modelButton.onClick.RemoveListener(ShowModel);
            npcButton.onClick.RemoveListener(ShowNpc);
            knowledgeButton?.onClick.RemoveListener(ShowKnowledge);
            if (debugButton != null) debugButton.onClick.RemoveListener(ShowDebug);
            if (traceButton != null) traceButton.onClick.RemoveListener(ShowTrace);
            if (dialogue != null) dialogue.PresentationVisibilityChanged -= OnDialogueVisibilityChanged;
            if (modelForm != null) modelForm.ConfigurationActivated -= OnConfigurationActivated;
            if (modelForm != null) modelForm.RuntimeStatusChecked -= StartupRuntimeChecked;
            Close();
        }

        private void OnDialogueVisibilityChanged(bool visible)
        {
            if (!visible)
            {
                dialogueClosedFrame = Time.frameCount;
                dialogueFocusToRestore = lastDialogueFocus = null;
            }
        }

        private void LateUpdate()
        {
            if (IsOpen || dialogue == null) return;
            var input = dialogue.DialogueInputField;
            // Pointer-down selects the entry before pointer-up opens Settings.
            // Retain only that continuous transition, including a held click.
            bool entryHoldsSelection = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == entryButton.gameObject;
            if (dialogue.IsPresentationVisible && input != null && !input.isFocused && entryHoldsSelection &&
                lastDialogueFocus == input && lastFocusConversation == dialogue.CurrentConversationId && lastDialogueFocusFrame >= Time.frameCount - 1)
            {
                lastDialogueFocusFrame = Time.frameCount;
                return;
            }
            lastDialogueFocus = dialogue.IsPresentationVisible && input != null && input.isFocused ? input : null;
            lastFocusConversation = dialogue.CurrentConversationId;
            lastDialogueFocusFrame = Time.frameCount;
        }

        private void CaptureDialogueFocus()
        {
            dialogueFocusToRestore = null;
            var input = dialogue.DialogueInputField;
            if (!dialogue.IsPresentationVisible || input == null) return;
            // A pointer-down on the entry button deselects TMP before its onClick opens Settings.
            bool entryTookSelection = EventSystem.current != null && EventSystem.current.currentSelectedGameObject == entryButton.gameObject &&
                lastDialogueFocus == input && lastDialogueFocusFrame >= Time.frameCount - 1 && lastFocusConversation == dialogue.CurrentConversationId;
            if (!input.isFocused && !entryTookSelection) return;
            dialogueFocusToRestore = input; focusConversation = dialogue.CurrentConversationId; focusTarget = dialogue.SelectedTarget;
        }

        private void RestoreDialogueFocus()
        {
            var input = dialogueFocusToRestore;
            dialogueFocusToRestore = lastDialogueFocus = null;
            if (input == null || !isActiveAndEnabled || dialogue == null || !dialogue.IsPresentationVisible ||
                dialogue.CurrentConversationId != focusConversation || dialogue.SelectedTarget != focusTarget ||
                dialogue.DialogueInputField != input || !input.isActiveAndEnabled || !input.interactable || EventSystem.current == null) return;
            var selected = EventSystem.current.currentSelectedGameObject;
            if (selected != null && selected != entryButton.gameObject && !selected.transform.IsChildOf(panelRoot.transform)) return;
            input.Select(); input.ActivateInputField();
        }

        private void Update()
        {
            UpdateEntry();
            if (UiFlow != null) return;
            bool escapePressed = Keyboard.current != null && Keyboard.current.escapeKey.isPressed;
            if (escapePressed && !escapeWasPressed) OnSettingsKey();
            escapeWasPressed = escapePressed;
        }

        private void OnSettingsKey()
        {
            if (UiFlow != null) { UiFlow.RequestCancel(); return; }
            var emotePanel = FindAnyObjectByType<PlayerEmotePanel>();
            if (emotePanel != null && emotePanel.BlocksOtherUi) return;
            if (IsOpen) Close();
            else if (startupAdmissionHeld) { Open(); ShowModelPage(); }
            else if (startupConfigResolved && dialogue != null &&
                     !dialogue.IsPresentationVisible && dialogueClosedFrame != Time.frameCount) Open();
        }

        private void UpdateEntry()
        {
            bool available = startupConfigResolved && dialogue != null && !dialogue.IsPresentationVisible &&
                (orbit == null || !orbit.IsPresentationUiHidden);
            if (entryButton.gameObject.activeSelf != available)
                entryButton.gameObject.SetActive(available);
        }

        public void Open()
        {
            var emotePanel = FindAnyObjectByType<PlayerEmotePanel>();
            if (emotePanel != null && emotePanel.BlocksOtherUi) return;
            if (IsOpen || dialogue == null) return;
            if (startupOverlay != null) startupOverlay.SetActive(false);
            CaptureDialogueFocus();
            // Dialogue suspension is restored from live visibility, not a stale modal snapshot.
            playerWasSuspended = player.IsInputSuspended && !dialogue.IsPresentationVisible && !startupAdmissionHeld;
            orbitWasSuspended = orbit.IsInputSuspended && !dialogue.IsPresentationVisible && !startupAdmissionHeld;
            dialogueInputMode = FindAnyObjectByType<EmbodiedNPC.Dialogue.Input.DialogueInputModeController>();
            if (dialogueInputMode != null)
            {
                pointerExitWasBlocked = dialogueInputMode.PointerExitBlocked;
                dialogueInputMode.PointerExitBlocked = true;
            }
            var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
            if (interactor != null)
            {
                interactionWasBlocked = interactor.InteractionBlocked && !startupAdmissionHeld;
                interactor.SetInteractionBlocked(true);
            }
            // A nested modal canvas stays above later-created prompts and dialogue bubbles.
            var parentCanvas = panelRoot.transform.parent.GetComponentInParent<Canvas>();
            var modalCanvas = panelRoot.GetComponent<Canvas>();
            if (modalCanvas == null) modalCanvas = panelRoot.AddComponent<Canvas>();
            if (panelRoot.GetComponent<GraphicRaycaster>() == null) panelRoot.AddComponent<GraphicRaycaster>();
            panelRoot.SetActive(true);
            SyncModalCanvasWithParent(modalCanvas, parentCanvas);
            if (settingsLayout == null)
            {
                settingsLayout = new CafeSettingsLayout(panelRoot.transform.Find("Window"), this);
                settingsLayout.Bind(new[] { overviewButton, modelButton, npcButton, knowledgeButton, debugButton, traceButton },
                    new[] { overviewPage, modelPage, npcPage, knowledgePage, debugPage, tracePage });
                RefreshLanguage();
            }
            player.SetDialogueInputSuspended(true);
            orbit.SetDialogueInputSuspended(true);
            orbit.SetSettingsCursorMode(true);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            bool resumeDeveloper = PlayerNavigation != null && PlayerNavigation.IsDeveloper;
            if (!resumeDeveloper) npcSettings.ResetSelectionPreference();
            npcSettings.RefreshCandidates();
            if (PlayerNavigation != null)
            {
                if (!resumeDeveloper) PlayerNavigation.SetDeveloper(false);
            }
            else ShowOverview();
            Opened?.Invoke();
        }

        public void OpenStartupSettings() { Open(); if (IsOpen) ShowModelPage(); }
        public bool CancelTopConfirmation() =>
            knowledgeSettings != null && knowledgePage.activeInHierarchy && knowledgeSettings.CancelConfirmation() ||
            modelForm != null && modelForm.CancelSettingsConfirmation();

        public static void SyncModalCanvasWithParent(Canvas modalCanvas, Canvas parentCanvas)
        {
            bool cameraSpace = parentCanvas != null && parentCanvas.renderMode == RenderMode.ScreenSpaceCamera;
            modalCanvas.renderMode = cameraSpace ? RenderMode.ScreenSpaceCamera : RenderMode.ScreenSpaceOverlay;
            modalCanvas.worldCamera = cameraSpace ? parentCanvas.worldCamera : null;
            modalCanvas.planeDistance = parentCanvas == null ? 100f : Mathf.Max(0.01f, parentCanvas.planeDistance);
            modalCanvas.overrideSorting = true;
            modalCanvas.sortingOrder = parentCanvas == null ? 1 : parentCanvas.sortingOrder + 1;
        }

        // Perception options are previewed in the world after dismissal. ESC must not first
        // leave developer mode and thereby suppress that preview. Other pages keep back navigation.
        public void Close() => CloseCore(debugPage == null || !debugPage.activeInHierarchy);
        public void CloseWindow() => CloseCore(false);
        private void CloseCore(bool navigateBack)
        {
            if (!IsOpen) return;
            if (modelForm != null && modelForm.ConsumeSettingsEscape()) return;
            if (navigateBack && PlayerNavigation != null && PlayerNavigation.ConsumeBack()) return;
            if (LeaveKnowledge(() => CloseCore(navigateBack))) return;
            if (statusRequest != null) StopCoroutine(statusRequest);
            statusRequest = null;
            loadingStatus = false;
            panelRoot.SetActive(false);
            if (dialogueInputMode != null) dialogueInputMode.PointerExitBlocked = pointerExitWasBlocked;
            player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>()?.SetInteractionBlocked(interactionWasBlocked || startupAdmissionHeld);
            bool dialogueVisible = dialogue != null && dialogue.IsPresentationVisible;
            player.SetDialogueInputSuspended(playerWasSuspended || dialogueVisible || startupAdmissionHeld);
            orbit.SetDialogueInputSuspended(orbitWasSuspended || dialogueVisible || startupAdmissionHeld);
            orbit.SetSettingsCursorMode(startupAdmissionHeld);
            if (startupOverlay != null) startupOverlay.SetActive(startupAdmissionHeld);
            ShowStartupControlRestored();
            RestoreDialogueFocus();
            Closed?.Invoke();
        }

        public void ShowOverview()
        {
            if (!IsOpen) return;
            if (LeaveKnowledge(ShowOverview)) return;
            overviewPage.SetActive(true);
            settingsLayout?.Select(overviewPage);
            modelPage.SetActive(false);
            npcPage.SetActive(false);
            if (debugPage != null) debugPage.SetActive(false);
            if (tracePage != null) tracePage.SetActive(false);
            if (!loadingStatus) statusRequest = StartCoroutine(RefreshStatus());
        }

        public void ShowModel()
        {
            if (!IsOpen || LeaveKnowledge(ShowModel)) return;
            ShowModelPage();
            if (IsOpen && startupConfigResolved) modelForm.ReloadForDeveloper();
        }

        // Maintenance navigation reuses the current form draft, without reloading saved profiles.
        public void ShowModelPresentation()
        {
            ShowModelPage();
            // Startup already read this snapshot while Settings was closed. Bind it once,
            // rather than fetching profiles on navigation or replacing an existing draft.
            if(IsOpen && !modelForm.HasProfileSettings && startupConfigSnapshot?.profiles!=null)
                modelForm.ApplyStartupResult(startupConfigSnapshot,null);
        }

        private void ShowModelPage()
        {
            if (!IsOpen) return;
            if (LeaveKnowledge(ShowModelPage)) return;
            overviewPage.SetActive(false);
            modelPage.SetActive(true);
            settingsLayout?.Select(modelPage);
            npcPage.SetActive(false);
            if (debugPage != null) debugPage.SetActive(false);
            if (tracePage != null) tracePage.SetActive(false);
        }

        public void ShowNpc()
        {
            if (!IsOpen) return;
            if (LeaveKnowledge(ShowNpc)) return;
            overviewPage.SetActive(false);
            modelPage.SetActive(false);
            npcPage.SetActive(true);
            settingsLayout?.Select(npcPage);
            if (debugPage != null) debugPage.SetActive(false);
            if (tracePage != null) tracePage.SetActive(false);
            npcSettings.RefreshCandidates();
        }

        public void ShowDebug()
        {
            if (!IsOpen || debugPage == null || debugPanel == null) return;
            if (LeaveKnowledge(ShowDebug)) return;
            overviewPage.SetActive(false);
            modelPage.SetActive(false);
            npcPage.SetActive(false);
            debugPage.SetActive(true);
            settingsLayout?.Select(debugPage);
            if (tracePage != null) tracePage.SetActive(false);
            npcSettings.RefreshCandidates();
            debugPanel.RefreshNow();
        }

        private void EnsureLanguageDropdown()
        {
            if (overviewPage == null) return;
            if (languageDropdown != null)
            {
                languageDropdown.onValueChanged.RemoveListener(OnLanguageChanged);
                languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
                return;
            }
            var row = new GameObject("LanguageSelector", typeof(RectTransform));
            row.transform.SetParent(overviewPage.transform, false);
            var rect = (RectTransform)row.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(-180, -42);
            rect.sizeDelta = new Vector2(260, 43);
            var labelObject = new GameObject("LanguageLabel", typeof(RectTransform));
            labelObject.transform.SetParent(row.transform, false);
            var label = labelObject.AddComponent<TextMeshProUGUI>();
            label.font = backendText.font;
            label.fontSize = 18;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.text = "界面语言 / UI Language";
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = new Vector2(0, 0);
            labelRect.anchorMax = new Vector2(.48f, 1);
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var dropdownObject = TMP_DefaultControls.CreateDropdown(default);
            dropdownObject.name = "LanguageDropdown";
            dropdownObject.transform.SetParent(row.transform, false);
            var dropdownRect = (RectTransform)dropdownObject.transform;
            dropdownRect.anchorMin = new Vector2(.49f, 0);
            dropdownRect.anchorMax = Vector2.one;
            dropdownRect.offsetMin = dropdownRect.offsetMax = Vector2.zero;
            foreach (TMP_Text text in dropdownObject.GetComponentsInChildren<TMP_Text>(true)) text.font = backendText.font;
            languageDropdown = dropdownObject.GetComponent<TMP_Dropdown>();
            languageDropdown.ClearOptions();
            languageDropdown.AddOptions(new List<string> { "中文 / Chinese", "英文 / English" });
            languageDropdown.SetValueWithoutNotify(SettingsUiLanguage.IsEnglish ? 1 : 0);
            languageDropdown.RefreshShownValue();
            languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
        }

        private void OnLanguageChanged(int index) => SettingsUiLanguage.SetEnglish(index == 1);

        private void OpenFromHud()
        {
            if (UiFlow != null && !IsStartupAdmissionHeld) UiFlow.OpenMenu();
            else Open();
        }
        private void StyleEntryButton()
        {
            if (entryButton == null || entryButton.transform.Find("EscapeKeycap") != null) return;
            var rect = (RectTransform)entryButton.transform;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 190);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 62);
            rect.anchoredPosition = new Vector2(-115, rect.anchoredPosition.y);
            SettingsPanelTheme.Surface(entryButton.targetGraphic as Image, new Color32(38,48,39,210));
            var label = entryButton.GetComponentInChildren<TMP_Text>(true);
            label.color = new Color32(248,240,213,255); label.fontSize = 24;
            var caption = (RectTransform)label.transform;
            caption.anchorMin = Vector2.zero; caption.anchorMax = Vector2.one;
            caption.offsetMin = new Vector2(66,0); caption.offsetMax = new Vector2(-8,0);
            var cap = new GameObject("EscapeKeycap", typeof(RectTransform), typeof(Image));
            cap.transform.SetParent(entryButton.transform, false);
            var capRect = (RectTransform)cap.transform; capRect.anchorMin = capRect.anchorMax = new Vector2(0,.5f);
            capRect.anchoredPosition = new Vector2(37,0); capRect.sizeDelta = new Vector2(52,34);
            SettingsPanelTheme.Surface(cap.GetComponent<Image>(), SettingsPanelTheme.Field); cap.GetComponent<Image>().raycastTarget=false;
            var key = Instantiate(label, cap.transform); key.text = "ESC"; key.fontSize=18; key.color=SettingsPanelTheme.Ink;
            key.raycastTarget=false; key.alignment=TextAlignmentOptions.Center;
            var keyRect=(RectTransform)key.transform;keyRect.anchorMin=Vector2.zero;keyRect.anchorMax=Vector2.one;
            keyRect.offsetMin=keyRect.offsetMax=Vector2.zero;
        }

        private void RefreshLanguage()
        {
            SettingsUiLanguage.StyleCoffeeWindow(panelRoot);
            if (languageDropdown != null)
            {
                languageDropdown.SetValueWithoutNotify(SettingsUiLanguage.IsEnglish ? 1 : 0);
                languageDropdown.RefreshShownValue();
            }
            SetLabel(entryButton, "设置", "Settings");
            SetNamedLabel(panelRoot?.transform.Find("Window")?.gameObject, "Header",
                "EmbodiedNPC · 开发者设置", "EmbodiedNPC · Developer Panel");
            SetLabel(closeButton, "关闭", "Close");
            SetLabel(overviewButton, "总览", "Overview");
            SetLabel(modelButton, "模型 / API", "Model / API");
            SetLabel(npcButton, "NPC 人设", "NPC Character");
            SetLabel(debugButton, "调试", "Debug");
            SetLabel(traceButton, "运行追踪", "Runtime Trace");
            SetNamedLabel(overviewPage, "OverviewTitle", "总览", "Overview");
            SetNamedLabel(npcPage, "NpcTitle", "NPC 人设", "NPC Character");
            SetNamedLabel(npcPage, "NpcPickerLabel", "当前 NPC", "Selected NPC");
            SetNamedLabel(npcPage, "NpcIdLabel", "NPC ID（只读）", "NPC ID (read only)");
            SetNamedLabel(npcPage, "NameLabel", "原始姓名（可编辑）", "Raw display name (editable)");
            SetNamedLabel(npcPage, "RoleLabel", "角色身份", "Role");
            SetNamedLabel(npcPage, "BackstoryLabel", "背景经历", "Backstory");
            SetNamedLabel(npcPage, "PersonalityLabel", "性格特征", "Personality");
            SetNamedLabel(npcPage, "StyleLabel", "说话风格", "Speaking Style");
            SetNamedLabel(npcPage, "BoundariesLabel", "行为边界", "Boundaries");
            SetNamedButton(npcPage, "RestoreDefault", "恢复默认", "Restore defaults");
            SetNamedButton(npcPage, "SaveNpc", "保存", "Save");
            SetNamedLabel(debugPage, "DebugTitle", "NPC 运行状态 · 感知调试", "NPC Runtime / Perception Debug");
            SetNamedLabel(debugPage, "DebugPickerLabel", "当前 NPC", "Selected NPC");
            SetNamedLabel(debugPage, "VisionToggleLabel", "显示视野范围", "Show vision range");
            SetNamedLabel(debugPage, "ProximityToggleLabel", "显示近距范围", "Show proximity range");
            SetNamedLabel(debugPage, "EntityIdsToggleLabel", "显示实体 ID", "Show entity IDs");
            SetNamedLabel(debugPage, "PerceivedToggleLabel", "高亮已感知实体", "Highlight perceived entities");
            SetNamedLabel(tracePage, "TraceTitle", "运行追踪", "Runtime Trace");
            SetNamedLabel(tracePage, "AutoScrollLabel", "自动滚动", "Auto scroll");
            SetNamedLabel(tracePage, "FilterLabel", "分类", "Category");
            SetNamedButton(tracePage, "PauseView", "暂停显示", "Pause view");
            SetNamedButton(tracePage, "ClearView", "清空视图", "Clear view");
            SetNamedButton(tracePage, "OpenLogFolder", "打开日志目录", "Open log folder");
            if (panelRoot != null)
                foreach (Toggle toggle in panelRoot.GetComponentsInChildren<Toggle>(true))
                    SettingsUiLanguage.StyleToggle(toggle);
            if (loadingStatus)
                backendText.text = SettingsUiLanguage.T("后端：连接中…", "Backend: Connecting…");
            else if (statusFailed) ShowDisconnectedStatus();
            else if (lastStatus != null) RenderRuntimeStatus(lastStatus);
            settingsLayout?.RefreshLanguage();
            npcSettings?.RefreshBackgroundFeedback();
            SetNavigationLabel(overviewButton, "总览", "Overview");
            SetNavigationLabel(modelButton, "模型", "Model");
            SetNavigationLabel(npcButton, "NPC", "NPC");
            SetNavigationLabel(knowledgeButton, "知识", "Knowledge");
            SetNavigationLabel(debugButton, "调试", "Debug");
            SetNavigationLabel(traceButton, "运行追踪", "Runtime Trace");
            tracePanel?.RefreshView();
        }

        private static void SetLabel(Button button, string chinese, string english)
        {
            TMP_Text label = button?.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = SettingsUiLanguage.T(chinese, english);
        }

        private static void SetNamedLabel(GameObject root, string name, string chinese, string english)
        {
            TMP_Text label = root == null ? null : CafeSettingsLayout.FindRuntime(root.transform, name)?.GetComponent<TMP_Text>();
            if (label != null) label.text = SettingsUiLanguage.T(chinese, english);
        }

        private static void SetNamedButton(GameObject root, string name, string chinese, string english)
        {
            SetLabel(root == null ? null : CafeSettingsLayout.FindRuntime(root.transform, name)?.GetComponent<Button>(), chinese, english);
        }

        public void ShowTrace()
        {
            if (!IsOpen || tracePage == null || tracePanel == null) return;
            if (LeaveKnowledge(ShowTrace)) return;
            overviewPage.SetActive(false);
            modelPage.SetActive(false);
            npcPage.SetActive(false);
            if (debugPage != null) debugPage.SetActive(false);
            tracePage.SetActive(true);
            settingsLayout?.Select(tracePage);
            tracePanel.RefreshView();
        }

        public void EnsureKnowledgePage()
        {
            if (knowledgePage != null || panelRoot == null) return;
            var window = panelRoot.transform.Find("Window");
            if (window == null || npcButton == null || backendText == null) return;
            knowledgePage = CafeKnowledgeView.Node("KnowledgePage", window);
            CafeKnowledgeView.Rect(knowledgePage.transform, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -115));
            knowledgePage.SetActive(false);
            knowledgeSettings = knowledgePage.AddComponent<CafeWorldKnowledgePanel>();
            knowledgeSettings.Initialize(backendText.font, new KnowledgeManagementClient(
                () => client?.BackendClient?.HttpBaseUrl ?? "http://127.0.0.1:8000"));
            knowledgeButton = CafeKnowledgeView.Node("KnowledgeNav", npcButton.transform.parent, typeof(Image), typeof(Button)).GetComponent<Button>();
            knowledgeButton.targetGraphic = knowledgeButton.GetComponent<Image>();
            knowledgeButton.transform.SetSiblingIndex(npcButton.transform.GetSiblingIndex() + 1);
            var element = knowledgeButton.gameObject.AddComponent<LayoutElement>(); element.flexibleWidth = 1; element.minWidth = 0;
            var label = CafeKnowledgeView.Node("Caption", knowledgeButton.transform, typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            label.font = backendText.font; label.fontSize = 18; label.enableAutoSizing = true; label.fontSizeMin = 12; label.fontSizeMax = 18;
            label.alignment = TextAlignmentOptions.Center; label.raycastTarget = false;
            CafeKnowledgeView.Rect(label.transform, Vector2.zero, Vector2.one, new Vector2(3, 4), new Vector2(-3, -36));
            var icon = CafeKnowledgeView.Node("CategoryIcon", knowledgeButton.transform, typeof(SettingsNavigationIcon)).GetComponent<SettingsNavigationIcon>();
            icon.Kind = 4; icon.raycastTarget = false;
            CafeKnowledgeView.Rect(icon.transform, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(-15, -34), new Vector2(15, -4));
            SettingsPanelTheme.Button(knowledgeButton);
        }

        public void ShowKnowledge()
        {
            if (!IsOpen || knowledgePage == null) return;
            overviewPage.SetActive(false); modelPage.SetActive(false); npcPage.SetActive(false);
            if (debugPage != null) debugPage.SetActive(false);
            if (tracePage != null) tracePage.SetActive(false);
            knowledgePage.SetActive(true); settingsLayout?.Select(knowledgePage);
        }

        private bool LeaveKnowledge(Action next)
        {
            if (!isActiveAndEnabled || knowledgePage == null || !knowledgePage.activeSelf) return false;
            knowledgeSettings.RequestLeave(() => { knowledgePage.SetActive(false); next(); });
            return true;
        }

        private static void SetNavigationLabel(Button button, string chinese, string english)
        {
            SetLabel(button, chinese, english);
            var localized = button?.GetComponentInChildren<SettingsLocalizedLabel>(true);
            if (localized != null) { localized.Chinese = chinese; localized.English = english; }
        }

        private IEnumerator RefreshStatus()
        {
            loadingStatus = true;
            backendText.text = SettingsUiLanguage.T("后端：连接中…", "Backend: Connecting…");
            settingsLayout?.FormatOverview();
            RuntimeStatusView status = null;
            string error = null;
            long httpCode = 0;
            yield return client.GetRuntimeStatus((value, code) => { status = value; error = code; }, code => httpCode = code);
            if (error != null || status == null)
            {
                ApplyRuntimeFailure(error ?? "INVALID_RESPONSE", httpCode);
            }
            else ApplyRuntimeStatus(status);
            loadingStatus = false;
            statusRequest = null;
        }

        public void ApplyRuntimeStatus(RuntimeStatusView status)
        {
            if (status == null) return;
            lastStatus = status;
            statusFailed = false;
            statusError = null;
            statusHttpCode = 200;
            statusCheckedAt = DateTime.Now;
            RenderRuntimeStatus(status);
        }

        public void ApplyRuntimeFailure(string error, long httpCode)
        {
            statusFailed = true; statusError = error; statusHttpCode = httpCode;
            ShowDisconnectedStatus();
        }

        private void RefreshBackendLine()
        {
            if (backendText == null) return;
            string http = statusFailed ? statusHttpCode > 0 ? SettingsUiLanguage.T("已响应；状态读取失败", "Responded; status read failed") :
                SettingsUiLanguage.T("连接检查未成功", "Connection check unsuccessful") : SettingsUiLanguage.T("已响应", "Responded");
            bool wsConnected = startupBackend != null && startupBackend.ConnectionStatus == "CONNECTED";
            backendText.text = "HTTP: " + http + SettingsUiLanguage.T("\nWebSocket：", "\nWebSocket: ") +
                (wsConnected ? SettingsUiLanguage.T("已连接", "Connected") : SettingsUiLanguage.T("未连接", "Disconnected"));
        }

        private void RenderRuntimeStatus(RuntimeStatusView status)
        {
            string unknown = SettingsUiLanguage.T("未知", "unknown");
            RefreshBackendLine();
            mainText.text = SettingsUiLanguage.T("主模型：", "Main model: ") +
                            (status.active_main_model?.provider ?? unknown) +
                            " / " + (status.active_main_model?.model ?? unknown);
            ActiveEmbeddingStatus embedding = status.active_embedding;
            embeddingText.text = embedding == null ? SettingsUiLanguage.T("Embedding：未知", "Embedding: unknown") :
                !embedding.enabled ? SettingsUiLanguage.T("Embedding：已关闭", "Embedding: Disabled") :
                SettingsUiLanguage.T("Embedding：已启用 · ", "Embedding: Enabled · ") +
                (embedding.provider ?? unknown) + " / " + (embedding.model ?? unknown);
            healthText.text = SettingsUiLanguage.T("服务状态：", "Provider health: ") +
                              (status.active_provider == null ? unknown : status.active_provider.available ?
                                  SettingsUiLanguage.T("可用", "Available") : SettingsUiLanguage.T("不可用", "Unavailable"));
            restartText.text = SettingsUiLanguage.T("需要重启：", "Restart required: ") +
                (status.restart_required ? SettingsUiLanguage.T("是", "Yes") : SettingsUiLanguage.T("否", "No")) +
                (statusCheckedAt.HasValue ? SettingsUiLanguage.T("\n检查：", "\nChecked: ") + statusCheckedAt.Value.ToString("HH:mm:ss") : "");
            settingsLayout?.FormatOverview();
        }

        private void ShowDisconnectedStatus()
        {
            RefreshBackendLine();
            mainText.text = SettingsUiLanguage.T("主模型：状态未知", "Main model: status unknown");
            embeddingText.text = SettingsUiLanguage.T("Embedding：状态未知", "Embedding: status unknown");
            var message = P0FeedbackText.BackendError(statusError, statusHttpCode);
            healthText.text = SettingsUiLanguage.T(message.Chinese, message.English);
            restartText.text = SettingsUiLanguage.T("需要重启：未知", "Restart required: unknown") +
                (statusCheckedAt.HasValue ? SettingsUiLanguage.T("\n上次成功检查：", "\nLast successful check: ") + statusCheckedAt.Value.ToString("HH:mm:ss") : "");
            settingsLayout?.FormatOverview();
        }
    }
}
