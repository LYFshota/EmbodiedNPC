using System;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>Adapts player proximity and the existing Interact action into the authoritative dialogue entry.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerDialogueInteractor : MonoBehaviour
    {
        [Header("Input and session")]
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private DialogueController dialogueController;

        [Header("Candidate presentation")]
        [SerializeField] private Camera sceneCamera;
        [SerializeField] private bool preferScreenCenter;
        [SerializeField] private TMP_Text promptText;
        [SerializeField] private DialogueParticipant[] candidates = Array.Empty<DialogueParticipant>();

        private InputAction interactAction;
        private InputAction cancelAction;
        private bool enabledInteractAction;
        private bool enabledCancelAction;
        private DialogueParticipant currentCandidate;
        private NpcDialoguePrompt promptView;
        private bool usePlayerProximitySelection;
        private DialogueParticipant proximityCandidate;
        public bool InteractionBlocked { get; private set; }
        // A scene UI owner may route Cancel and fence same-frame gameplay input.
        public bool ExternalCancelRouting { get; set; }
        public Func<bool> UiInputBlocked { get; set; }
        public bool IsPromptHidden { get; private set; }

        /// <summary>Hides only the visual hint; candidate selection and E eligibility stay active.</summary>
        public void SetPromptHidden(bool hidden)
        {
            IsPromptHidden = hidden;
            promptView?.SetHidden(hidden);
            RefreshCandidate();
        }

        /// <summary>Presentation cameras may change selection, never eligibility or session semantics.</summary>
        public void SetUsePlayerProximitySelection(bool active)
        {
            proximityCandidate = active ? currentCandidate : null;
            usePlayerProximitySelection = active;
            RefreshCandidate();
        }

        public void SetInteractionBlocked(bool blocked)
        {
            InteractionBlocked = blocked;
            RefreshCandidate();
        }

        public DialogueParticipant CurrentCandidate => currentCandidate;
        public bool IsPromptVisible => promptView?.IsVisible ?? (promptText != null && promptText.gameObject.activeInHierarchy);
        public string PromptLabel => promptView?.Label ?? (promptText == null ? string.Empty : promptText.text);

#if UNITY_EDITOR
        public GameObject BakePromptForEditor()
        {
            new NpcDialoguePrompt(promptText, true);
            return promptText.transform.parent.gameObject;
        }
#endif

        private void Awake()
        {
            if (sceneCamera == null) sceneCamera = Camera.main;
            if (promptText != null) promptView = new NpcDialoguePrompt(promptText);
            SetCurrentCandidate(null);
        }

        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshPromptLanguage;
            // Script reload restores scene references but not this plain C# presenter.
            if (promptView == null && promptText != null)
            {
                promptView = new NpcDialoguePrompt(promptText);
                promptView.SetHidden(IsPromptHidden);
                promptView.SetTarget(currentCandidate);
            }
            if (dialogueController != null)
            {
                dialogueController.ActiveSessionChanged += HandleActiveSessionChanged;
            }

            if (inputActions != null)
            {
                interactAction = inputActions.FindAction("Player/Interact", false);
                if (interactAction != null)
                {
                    // The shared asset currently has a Hold interaction. started preserves an immediate E press.
                    interactAction.started += HandleInteractStarted;
                    if (!interactAction.enabled)
                    {
                        interactAction.Enable();
                        enabledInteractAction = true;
                    }
                }

                cancelAction = inputActions.FindAction("UI/Cancel", false);
                if (cancelAction != null)
                {
                    cancelAction.performed += HandleCancelPerformed;
                    if (!cancelAction.enabled)
                    {
                        cancelAction.Enable();
                        enabledCancelAction = true;
                    }
                }
            }
        }

        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshPromptLanguage;
            if (interactAction != null)
            {
                interactAction.started -= HandleInteractStarted;
                if (enabledInteractAction) interactAction.Disable();
                interactAction = null;
                enabledInteractAction = false;
            }

            if (cancelAction != null)
            {
                cancelAction.performed -= HandleCancelPerformed;
                if (enabledCancelAction) cancelAction.Disable();
                cancelAction = null;
                enabledCancelAction = false;
            }

            if (dialogueController != null)
            {
                dialogueController.ActiveSessionChanged -= HandleActiveSessionChanged;
                if (dialogueController.HasActiveSession)
                    dialogueController.ClearSession("PLAYER_INTERACTION_DISABLED");
            }

            SetCurrentCandidate(null);
        }

        private void Update()
        {
            RefreshCandidate();
        }

        private void LateUpdate() => promptView?.RefreshPosition(sceneCamera);

        public DialogueParticipant RefreshCandidate()
        {
            DialogueParticipant next = null;
            if (!InteractionBlocked && UiInputBlocked?.Invoke() != true && dialogueController != null && dialogueController.IsSetupReady &&
                !dialogueController.IsPresentationVisible)
            {
                DialogueParticipant selected = dialogueController.SelectedTarget;
                // A retained conversation must not override screen-center focus while its UI is hidden.
                DialogueParticipant retained = proximityCandidate != null ? proximityCandidate : selected;
                next = usePlayerProximitySelection && retained != null && retained.isActiveAndEnabled &&
                       dialogueController.CheckEligibility(retained).IsEligible ? retained :
                       !preferScreenCenter && dialogueController.HasActiveSession && selected != null &&
                       dialogueController.CheckEligibility(selected).IsEligible && IsInCameraView(selected)
                    ? selected
                    : FindBestCandidate();
            }
            SetCurrentCandidate(next);
            return currentCandidate;
        }

        public bool TryBeginDialogue()
        {
            DialogueParticipant candidate = RefreshCandidate();
            return candidate != null && dialogueController != null && dialogueController.SelectTarget(candidate);
        }

        public void EndDialogue()
        {
            if (dialogueController != null && dialogueController.IsPresentationVisible)
                dialogueController.HideDialoguePresentation("cancel_input");
        }

        private DialogueParticipant FindBestCandidate()
        {
            DialogueParticipant best = null;
            float bestDistanceSquared = float.PositiveInfinity;
            float bestViewDistanceSquared = float.PositiveInfinity;
            string bestId = string.Empty;

            if (candidates == null) return null;
            for (int index = 0; index < candidates.Length; index++)
            {
                DialogueParticipant candidate = candidates[index];
                if (candidate == null || !candidate.isActiveAndEnabled ||
                    !dialogueController.CheckEligibility(candidate).IsEligible || !IsInCameraView(candidate))
                    continue;

                float distanceSquared = (candidate.transform.position - transform.position).sqrMagnitude;
                Vector3 viewport = sceneCamera == null ? new Vector3(.5f, .5f, 1f) :
                    sceneCamera.WorldToViewportPoint(candidate.transform.position + Vector3.up);
                float viewDistanceSquared = preferScreenCenter && !usePlayerProximitySelection
                    ? ((Vector2)viewport - new Vector2(.5f, .5f)).sqrMagnitude : 0f;
                string candidateId = candidate.Entity == null ? string.Empty : candidate.Entity.EntityId;
                bool isMoreCentral = viewDistanceSquared < bestViewDistanceSquared - 0.0001f;
                bool sameViewDistance = Mathf.Abs(viewDistanceSquared - bestViewDistanceSquared) <= 0.0001f;
                bool isCloser = distanceSquared < bestDistanceSquared - 0.0001f;
                bool isStableTie = Mathf.Abs(distanceSquared - bestDistanceSquared) <= 0.0001f &&
                                   string.CompareOrdinal(candidateId, bestId) < 0;
                if (!isMoreCentral && !(sameViewDistance && (isCloser || isStableTie))) continue;

                best = candidate;
                bestDistanceSquared = distanceSquared;
                bestViewDistanceSquared = viewDistanceSquared;
                bestId = candidateId;
            }

            return best;
        }

        private bool IsInCameraView(DialogueParticipant candidate)
        {
            if (usePlayerProximitySelection) return true;
            if (sceneCamera == null) return true;
            Vector3 viewport = sceneCamera.WorldToViewportPoint(candidate.transform.position + Vector3.up);
            return viewport.z > 0f && viewport.x >= 0f && viewport.x <= 1f &&
                   viewport.y >= 0f && viewport.y <= 1f;
        }

        private void SetCurrentCandidate(DialogueParticipant candidate)
        {
            if (promptView != null)
            {
                if (currentCandidate != candidate) promptView.SetTarget(candidate);
                currentCandidate = candidate;
                promptView.RefreshPosition(sceneCamera);
                return;
            }
            if (currentCandidate == candidate)
            {
                if (promptText != null && promptText.gameObject.activeSelf != (candidate != null && !IsPromptHidden))
                    promptText.gameObject.SetActive(candidate != null && !IsPromptHidden);
                return;
            }
            currentCandidate = candidate;
            if (promptText == null) return;

            bool visible = candidate != null && !IsPromptHidden;
            if (candidate != null)
            {
                RefreshPromptLanguage();
            }
            promptText.gameObject.SetActive(visible);
        }

        private void RefreshPromptLanguage()
        {
            if (promptView != null) { promptView.SetTarget(currentCandidate); return; }
            if (currentCandidate == null || promptText == null) return;
            var perceived = currentCandidate.GetComponent<PerceivableEntity>();
            string name = SettingsUiLanguage.NpcDisplayName(currentCandidate.Entity?.EntityId, perceived?.DisplayName ?? "NPC");
            promptText.text = SettingsUiLanguage.T($"[E] 与{name}对话", $"[E] Talk to {name}");
        }

        private void HandleInteractStarted(InputAction.CallbackContext context)
        {
            TryBeginDialogue();
        }

        private void HandleCancelPerformed(InputAction.CallbackContext context)
        {
            if (!ExternalCancelRouting && !InteractionBlocked) EndDialogue();
        }

        private void HandleActiveSessionChanged(DialogueParticipant participant)
        {
            SetCurrentCandidate(null);
        }
    }
}
