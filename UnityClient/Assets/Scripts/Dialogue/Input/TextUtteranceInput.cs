using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>
    /// M5-lite text-only input adapter. It owns view events and emits an utterance,
    /// but never contacts the backend or executes a Goal.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class TextUtteranceInput : MonoBehaviour, IUtteranceInput
    {
        [SerializeField] private CanvasGroup panelGroup;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private Button submitButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private TMP_Text feedbackText;
        [SerializeField, Min(1)] private int maxTextLength = 500;

        private DialogueContext activeContext;
        private bool presentationVisible;
        private bool submissionReady = true;
        private string textToRestoreAfterSubmission;

        public event Action<UserUtterance> Submitted;
        public event Action Cancelled;
        public bool HasActiveContext => activeContext != null && activeContext.IsValid;
        public bool IsVisible => presentationVisible && HasActiveContext &&
                                 (panelGroup == null || panelGroup.alpha > 0.5f);
        public bool IsInputFocused => inputField != null && inputField.isFocused;
        public TMP_InputField InputField => inputField;
        public bool IsSubmissionReady => submissionReady;
        public string CurrentText => inputField == null ? string.Empty : inputField.text;

        private void Awake()
        {
            if (panelGroup == null) panelGroup = GetComponent<CanvasGroup>();
            if (inputField != null) inputField.characterLimit = maxTextLength;
            RefreshView();
        }

        private void OnEnable()
        {
            if (inputField != null) inputField.onSubmit.AddListener(HandleFieldSubmit);
            if (submitButton != null) submitButton.onClick.AddListener(SubmitFromField);
            if (cancelButton != null) cancelButton.onClick.AddListener(CancelInput);
        }

        private void OnDisable()
        {
            if (inputField != null) inputField.onSubmit.RemoveListener(HandleFieldSubmit);
            if (submitButton != null) submitButton.onClick.RemoveListener(SubmitFromField);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(CancelInput);
        }

        public void BeginInput(DialogueContext context)
        {
            activeContext = context;
            SetFeedback(HasActiveContext ? string.Empty : "请选择可对话的 NPC");
            RefreshView();
        }

        public void ShowInput()
        {
            if (!HasActiveContext) return;
            presentationVisible = true;
            RefreshView();
            FocusInput();
        }

        public void HideInput()
        {
            presentationVisible = false;
            inputField?.DeactivateInputField();
            RefreshView();
        }

        public void EndInput()
        {
            presentationVisible = false;
            activeContext = null;
            if (inputField != null)
            {
                inputField.DeactivateInputField();
                inputField.text = string.Empty;
            }
            SetFeedback(string.Empty);
            RefreshView();
        }

        public void SetSubmissionReady(bool ready, string unavailableMessage = null)
        {
            submissionReady = ready;
            if (ready) SetFeedback(string.Empty);
            else if (!string.IsNullOrEmpty(unavailableMessage)) SetFeedback(unavailableMessage);
            RefreshView();
        }

        public void RestoreTextIfEmpty(string value)
        {
            if (inputField == null || !string.IsNullOrEmpty(inputField.text) || string.IsNullOrEmpty(value)) return;
            inputField.text = value;
        }

        public void PreserveTextAfterSubmission(string value, string message)
        {
            textToRestoreAfterSubmission = value ?? string.Empty;
            SetFeedback(message ?? string.Empty);
        }

        public bool SubmitText(string rawText)
        {
            if (!HasActiveContext)
            {
                SetFeedback("请选择可对话的 NPC");
                return false;
            }

            if (!submissionReady)
            {
                SetFeedback("后端未连接，输入内容已保留");
                return false;
            }

            string normalized = Normalize(rawText);
            if (string.IsNullOrEmpty(normalized))
            {
                SetFeedback(string.Empty);
                return false;
            }

            if (normalized.Length > maxTextLength)
            {
                SetFeedback($"输入不能超过 {maxTextLength} 个字符");
                return false;
            }

            Submitted?.Invoke(new UserUtterance
            {
                requestId = string.Empty,
                conversationId = activeContext.conversationId,
                speakerEntityId = activeContext.speakerEntityId,
                targetAgentId = activeContext.targetAgentId,
                inputMode = "text",
                text = normalized,
            });
            if (inputField != null) inputField.text = textToRestoreAfterSubmission ?? string.Empty;
            if (textToRestoreAfterSubmission == null) SetFeedback(string.Empty);
            textToRestoreAfterSubmission = null;
            FocusInput();
            return true;
        }

        public void CancelInput()
        {
            Cancelled?.Invoke();
        }

        private void HandleFieldSubmit(string value) => SubmitText(value);
        private void SubmitFromField() => SubmitText(inputField == null ? string.Empty : inputField.text);

        private static string Normalize(string value) => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Normalize(NormalizationForm.FormKC).Trim();

        private void RefreshView()
        {
            bool canInteract = HasActiveContext && presentationVisible;
            if (inputField != null) inputField.interactable = canInteract;
            if (submitButton != null) submitButton.interactable = canInteract && submissionReady;
            if (cancelButton != null) cancelButton.interactable = canInteract;
            if (panelGroup == null) return;
            panelGroup.alpha = canInteract ? 1f : 0f;
            panelGroup.interactable = canInteract;
            panelGroup.blocksRaycasts = canInteract;
        }

        private void FocusInput()
        {
            if (inputField == null || !inputField.interactable) return;
            inputField.Select();
            inputField.ActivateInputField();
        }

        private void SetFeedback(string message)
        {
            if (feedbackText != null) feedbackText.text = message;
        }
    }
}
