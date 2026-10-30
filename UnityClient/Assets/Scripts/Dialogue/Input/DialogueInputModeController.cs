using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Input
{
    /// <summary>Maps dialogue-session state to gameplay input consumers without coupling the text UI to them.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueInputModeController : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private MonoBehaviour[] suspendTargets = Array.Empty<MonoBehaviour>();
        [SerializeField] private Transform dialogueUiRoot;
        private readonly List<RaycastResult> pointerHits = new();
        private int enteredFrame;

        public bool IsDialogueInputModeActive { get; private set; }
        public bool PointerExitBlocked { get; set; }
        public bool ExternalUiInputBlocked { get; set; }

        private void OnEnable()
        {
            if (dialogueController == null) dialogueController = FindAnyObjectByType<DialogueController>();
            if (dialogueController != null) dialogueController.PresentationVisibilityChanged += HandleVisibilityChanged;
            Apply(dialogueController != null && dialogueController.IsPresentationVisible);
        }

        private void OnDisable()
        {
            if (dialogueController != null) dialogueController.PresentationVisibilityChanged -= HandleVisibilityChanged;
            Apply(false);
        }

        public void Configure(DialogueController controller, params MonoBehaviour[] targets)
        {
            if (isActiveAndEnabled && dialogueController != null)
                dialogueController.PresentationVisibilityChanged -= HandleVisibilityChanged;
            dialogueController = controller;
            suspendTargets = targets ?? Array.Empty<MonoBehaviour>();
            if (isActiveAndEnabled && dialogueController != null)
                dialogueController.PresentationVisibilityChanged += HandleVisibilityChanged;
            Apply(dialogueController != null && dialogueController.IsPresentationVisible);
        }

        private void HandleVisibilityChanged(bool visible) => Apply(visible);

        private void Update()
        {
            if (!IsDialogueInputModeActive || Time.frameCount == enteredFrame) return;
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                TryExitAtPointer(mouse.position.ReadValue());
        }

        public bool IsOverDialogueUi(Vector2 screenPosition)
        {
            if (dialogueUiRoot == null || EventSystem.current == null) return false;
            pointerHits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current)
                { position = screenPosition }, pointerHits);
            foreach (RaycastResult hit in pointerHits)
                if (hit.gameObject != null && hit.gameObject.transform.IsChildOf(dialogueUiRoot))
                    return true;
            return false;
        }

        public bool TryExitAtPointer(Vector2 screenPosition)
        {
            // Missing UI wiring must not make every input click end the session.
            if (PointerExitBlocked || !IsDialogueInputModeActive || dialogueUiRoot == null || EventSystem.current == null ||
                IsOverDialogueUi(screenPosition)) return false;
            // Other UI (including a settings entry or modal) owns its pointer click.
            foreach (RaycastResult hit in pointerHits)
                if (hit.module is GraphicRaycaster) return false;
            dialogueController.HideDialoguePresentation("outside_click");
            return true;
        }

        private void Apply(bool suspended)
        {
            if (suspended && !IsDialogueInputModeActive) enteredFrame = Time.frameCount;
            IsDialogueInputModeActive = suspended;
            foreach (MonoBehaviour behaviour in suspendTargets)
            {
                if (behaviour is IDialogueInputSuspendTarget target)
                    target.SetDialogueInputSuspended(suspended || ExternalUiInputBlocked);
            }
            foreach (MonoBehaviour behaviour in suspendTargets)
                if (behaviour is IDialogueCursorTarget cursor) cursor.SetDialogueCursorMode(suspended);
        }
    }
}
