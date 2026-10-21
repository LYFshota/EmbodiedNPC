using System;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Validation;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using TMPro;
using UnityEngine;

namespace EmbodiedNPC.Dialogue
{
    /// <summary>M5-lite's sole submission entry: validates input, owns the active request, and gates replies.</summary>
    public sealed partial class DialogueController : MonoBehaviour
    {
        [SerializeField] private TextUtteranceInput textInput;
        [SerializeField] private DialogueEligibilityValidator eligibilityValidator;
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private bool setupReady = true;

        private DialogueParticipant selectedTarget;
        private DialogueContext activeContext;
        private bool hasEverConnected;
        private bool transportLossHandled;
        private bool contextResetPending;
        private bool presentationVisible;
        private DialogueExperienceAdapter dialogueExperienceAdapter;
        private float nextConnectionRecoveryAt;
        private const float ConnectionRecoveryInterval = 5f;

        public event Action<UserUtterance> ValidatedUtteranceReady;
        public event Action<UserUtterance> DialogueSubmissionAccepted;
        public event Action<DialogueParticipant> ActiveSessionChanged;
        public event Action<bool> PresentationVisibilityChanged;
        public DialogueParticipant SelectedTarget => selectedTarget;
        public bool HasActiveSession => activeContext != null && activeContext.IsValid;
        public bool IsPresentationVisible => presentationVisible;
        public bool IsChangingSession { get; private set; }
        public bool IsSetupReady => setupReady;
        public string CurrentConversationId => activeContext?.conversationId;
        public TMP_InputField DialogueInputField => textInput?.InputField;
        public string ActiveSpeakerEntityId => HasActiveSession ? activeContext.speakerEntityId : null;
        public string LastLifecycleReason { get; private set; } = "not_started";
        public DialogueEligibilityResult LastEligibility { get; private set; }

        private void Awake()
        {
            if (backendClient == null) backendClient = FindAnyObjectByType<BackendWebSocketClient>();
            hasEverConnected = backendClient != null && backendClient.IsConnected;
            if (backendClient != null)
                dialogueExperienceAdapter = new DialogueExperienceAdapter(
                    draft => backendClient.EnqueueExperience(draft), FindAnyObjectByType<EntityRegistry>());
        }

        private void OnEnable()
        {
            if (textInput != null) textInput.Submitted += HandleSubmitted;
            if (textInput != null) textInput.Cancelled += HandleInputCancelled;
            if (backendClient != null)
            {
                backendClient.ConnectionStatusChanged += HandleConnectionStatusChanged;
                backendClient.ExperienceAppendSettled += HandleExperienceSettled;
            }
            EmbodiedNPC.Settings.SettingsUiLanguage.Changed += RefreshRequestFeedback;
        }

        private void Start() => ClearSession("靠近 NPC 后按 E 开始对话");

        private void OnDisable()
        {
            bool hadActiveSession = HasActiveSession || selectedTarget != null;
            if (hadActiveSession) RecordLifecycle("controller_disabled");
            if (textInput != null) textInput.Submitted -= HandleSubmitted;
            if (textInput != null) textInput.Cancelled -= HandleInputCancelled;
            if (backendClient != null)
            {
                backendClient.ConnectionStatusChanged -= HandleConnectionStatusChanged;
                backendClient.ExperienceAppendSettled -= HandleExperienceSettled;
            }
            EmbodiedNPC.Settings.SettingsUiLanguage.Changed -= RefreshRequestFeedback;
            UnbindFeedbackDriver();
            selectedTarget = null;
            activeContext = null;
            SetPresentationVisible(false);
            textInput?.EndInput();
            if (hadActiveSession) ActiveSessionChanged?.Invoke(null);
        }

        public bool SelectTarget(DialogueParticipant target)
        {
            if (!setupReady) return false;
            bool continueConversation = selectedTarget == target && HasActiveSession;
            if (selectedTarget != target)
            {
                CancelRediscoveryForSessionChange();
                if (selectedTarget != null) RecordLifecycle("switch_npc");
            }
            LastEligibility = CheckEligibility(target);
            if (!LastEligibility.IsEligible)
            {
                ClearSession(ToDisplayMessage(LastEligibility.FailureCode));
                return false;
            }

            selectedTarget = target;
            BindFeedbackDriver(target.GetComponent<EmbodiedNPC.Cognition.CognitiveRunDriver>());
            if (!continueConversation) activeContext = new DialogueContext
            {
                conversationId = $"{FindPlayerId()}:{target.Entity.EntityId}:{Guid.NewGuid():N}",
                speakerEntityId = FindPlayerId(),
                targetAgentId = target.Entity.EntityId,
            };
            if (!continueConversation) RecordLifecycle("conversation_created");
            textInput?.BeginInput(activeContext);
            textInput?.SetSubmissionReady(backendClient != null && backendClient.IsConnected,
                "后端未连接，输入内容会保留");
            if (!continueConversation) ResetRequestFeedback();
            if (!RefreshRequestFeedbackIfAny()) SetStatus(string.Empty);
            if (!continueConversation) ActiveSessionChanged?.Invoke(selectedTarget);
            ShowDialoguePresentation();
            return true;
        }

        public void ShowDialoguePresentation()
        {
            if (!setupReady || !HasActiveSession || selectedTarget == null ||
                !CheckEligibility(selectedTarget).IsEligible) return;
            textInput?.ShowInput();
            SetPresentationVisible(true);
        }

        public void HideDialoguePresentation()
        {
            // Closing the input presentation does not cancel the accepted request.
            textInput?.HideInput();
            SetPresentationVisible(false);
        }

        private void SetPresentationVisible(bool visible)
        {
            if (presentationVisible == visible) return;
            presentationVisible = visible;
            PresentationVisibilityChanged?.Invoke(visible);
        }

        public DialogueEligibilityResult CheckEligibility(DialogueParticipant target) =>
            eligibilityValidator == null
                ? new DialogueEligibilityResult(DialogueEligibilityFailureCode.DialogueNotAllowed)
                : eligibilityValidator.Validate(target);

        public void ClearSession(string message = "靠近 NPC 后按 E 开始对话")
        {
            UnbindFeedbackDriver();
            ResetRequestFeedback();
            bool hadActiveSession = HasActiveSession || selectedTarget != null;
            CancelRediscoveryForSessionChange();
            if (hadActiveSession) RecordLifecycle("conversation_ended");
            selectedTarget = null;
            activeContext = null;
            HideDialoguePresentation();
            textInput?.EndInput();
            SetStatus(message);
            if (hadActiveSession) ActiveSessionChanged?.Invoke(null);
        }

        public void SetSetupReady(bool ready)
        {
            if (setupReady == ready) return;
            setupReady = ready;
            if (!ready) ClearSession("请先完成模型配置");
        }

        public void CancelForEmergency()
        {
            RotateConversation("emergency_interrupt");
            SetStatus("EMERGENCY_RESPONSE_ACTIVE");
        }

        private void HandleSubmitted(UserUtterance utterance)
        {
            if (!setupReady || utterance == null || activeContext == null ||
                utterance.conversationId != activeContext.conversationId ||
                utterance.speakerEntityId != activeContext.speakerEntityId ||
                utterance.targetAgentId != activeContext.targetAgentId)
            {
                SetStatus("STALE_UTTERANCE");
                return;
            }
            if (backendClient == null || !backendClient.IsConnected)
            {
                textInput?.SetSubmissionReady(false, "后端未连接，输入内容已保留");
                SetStatus("BACKEND_OFFLINE");
                return;
            }
            LastEligibility = eligibilityValidator == null
                ? new DialogueEligibilityResult(DialogueEligibilityFailureCode.DialogueNotAllowed)
                : eligibilityValidator.Validate(selectedTarget);
            if (!LastEligibility.IsEligible)
            {
                ClearSession(ToDisplayMessage(LastEligibility.FailureCode));
                return;
            }

            if (!utterance.IsValid)
            {
                SetStatus("UTTERANCE_INVALID");
                return;
            }

            ValidatedUtteranceReady?.Invoke(utterance);

            IDialogueSubmissionHandler submissionHandler =
                selectedTarget == null ? null : selectedTarget.GetComponent<IDialogueSubmissionHandler>();
            if (submissionHandler != null)
            {
                DialogueSubmissionResult result;
                var previousFeedback = (feedbackRequestId, feedbackTraceId, replyFeedback, inputConfirmed,
                    responseExpected, responseConfirmed, feedbackSettled);
                submittingForFeedback = true;
                feedbackRequestId = feedbackTraceId = replyFeedback = null;
                inputConfirmed = responseExpected = responseConfirmed = feedbackSettled = false;
                try { result = submissionHandler.Submit(utterance); }
                finally { submittingForFeedback = false; }
                if (result.Accepted)
                {
                    TrackAcceptedFeedback(result.AcceptedTurn);
                    DialogueSubmissionAccepted?.Invoke(utterance);
                    if (result.AcceptedTurn != null)
                        dialogueExperienceAdapter?.Handle(result.AcceptedTurn);
                    RefreshRequestFeedback();
                }
                else
                {
                    (feedbackRequestId, feedbackTraceId, replyFeedback, inputConfirmed,
                        responseExpected, responseConfirmed, feedbackSettled) = previousFeedback;
                    textInput?.PreserveTextAfterSubmission(utterance.text, ToSubmissionMessage(result.Status));
                    SetStatus(ToSubmissionMessage(result.Status));
                }
                return;
            }

            textInput?.PreserveTextAfterSubmission(utterance.text, "当前目标未接入认知对话");
            SetStatus("COGNITIVE_HANDLER_UNAVAILABLE");
        }

        private void Update()
        {
            if (HasActiveSession)
            {
                DialogueEligibilityResult eligibility = CheckEligibility(selectedTarget);
                if (eligibility.FailureCode == DialogueEligibilityFailureCode.TargetOutOfDialogueRange)
                {
                    if (presentationVisible) HideDialoguePresentation();
                }
                else if (!eligibility.IsEligible)
                {
                    ClearSession("DIALOGUE_TARGET_UNAVAILABLE");
                    return;
                }
            }
            RefreshTransportReadiness();
        }

        private void RefreshTransportReadiness()
        {
            if (!HasActiveSession || backendClient == null) return;
            bool connected = backendClient.IsConnected;
            if (textInput != null && textInput.IsSubmissionReady != connected)
                textInput.SetSubmissionReady(connected, "后端未连接，输入内容已保留");
            // Recover on demand after a bounded connection round has ended.
            // EnsureConnected retains the single existing task and its attempt limit.
            if (!setupReady || !presentationVisible || connected || Time.unscaledTime < nextConnectionRecoveryAt) return;
            nextConnectionRecoveryAt = Time.unscaledTime + ConnectionRecoveryInterval;
            backendClient.EnsureConnected();
        }

        private void HandleInputCancelled()
        {
            HideDialoguePresentation();
        }

        private void RotateConversation(string reason)
        {
            ClearWaitingBubble();
            if (activeContext == null) return;
            activeContext.conversationId = $"{activeContext.speakerEntityId}:{activeContext.targetAgentId}:{Guid.NewGuid():N}";
            RecordLifecycle(reason);
        }

        private void CancelRediscoveryForSessionChange()
        {
            // Cancellation may synchronously publish an Outcome before selectedTarget is cleared.
            IsChangingSession = true;
            try { CancelRediscovery(); }
            finally { IsChangingSession = false; }
        }

        private void CancelRediscovery()
        {
            GoalPlanRunner search = selectedTarget == null ? null : selectedTarget.GetComponent<GoalPlanRunner>();
            if (search != null && search.IsRediscovering) search.InterruptCurrentPlan("CANCELLED_BY_REQUEST");
        }

        private void HandleConnectionStatusChanged(string status)
        {
            if (status != "CONNECTED") ClearWaitingBubble();
            if (status == "CONNECTED")
            {
                bool isReconnect = hasEverConnected && transportLossHandled;
                hasEverConnected = true;
                transportLossHandled = false;
                textInput?.SetSubmissionReady(true);
                if (HasActiveSession)
                {
                    if (contextResetPending) ResetRequestFeedback();
                    if (!RefreshRequestFeedbackIfAny()) SetStatus(EmbodiedNPC.Settings.SettingsUiLanguage.T(
                        contextResetPending ? "对话通道已恢复；会话已重置。" : "对话通道已连接。",
                        contextResetPending ? "Dialogue connection restored; session reset." : "Dialogue connection established."));
                }
                if (isReconnect) RecordLifecycle("transport_reconnected");
                contextResetPending = false;
                return;
            }

            textInput?.SetSubmissionReady(false, "后端未连接，输入内容已保留");
            if (hasEverConnected && !transportLossHandled)
            {
                RotateConversation("transport_disconnected");
                transportLossHandled = true;
                contextResetPending = HasActiveSession;
            }
            if (HasActiveSession) SetStatus(EmbodiedNPC.Settings.SettingsUiLanguage.T(
                feedbackRequestId != null ? "对话通道中断；本次结果与保存尚未确认。" : "对话通道未连接，请检查连接。",
                feedbackRequestId != null ? "Dialogue connection lost; result and storage are unconfirmed." : "Dialogue is disconnected. Check the connection."));
        }

        private void RecordLifecycle(string reason)
        {
            LastLifecycleReason = reason;
            Debug.Log($"[Dialogue] LIFECYCLE reason={reason}; conversation={activeContext?.conversationId ?? "none"}; " +
                      $"speaker={activeContext?.speakerEntityId ?? "none"}; target={activeContext?.targetAgentId ?? "none"}; " +
                      "pending=0", this);
        }

        private string FindPlayerId()
        {
            foreach (WorldEntity entity in FindObjectsByType<WorldEntity>())
            {
                if (entity.IsAgent(AgentRole.Player)) return entity.EntityId;
            }
            return string.Empty;
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }

        private static string ToDisplayMessage(DialogueEligibilityFailureCode code) => code switch
        {
            DialogueEligibilityFailureCode.TargetNotFound => "TARGET_NOT_FOUND",
            DialogueEligibilityFailureCode.TargetCannotCommunicate => "TARGET_CANNOT_COMMUNICATE",
            DialogueEligibilityFailureCode.TargetOutOfDialogueRange => "TARGET_OUT_OF_DIALOGUE_RANGE",
            _ => "DIALOGUE_NOT_ALLOWED",
        };

        private static string ToSubmissionMessage(string status) => status switch
        {
            "COGNITIVE_RUN_BUSY" => "NPC 正在处理上一条消息，输入内容已保留",
            "COGNITIVE_EVENT_NOT_ROUTED" => "消息未能送达 NPC，输入内容已保留",
            "COGNITIVE_RUN_NOT_STARTED" => "NPC 暂时无法回应，输入内容已保留",
            _ => "消息提交失败，输入内容已保留",
        };
    }
}
