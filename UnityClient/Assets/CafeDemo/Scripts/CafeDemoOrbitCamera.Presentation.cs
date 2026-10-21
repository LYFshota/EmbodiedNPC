using EmbodiedNPC.Dialogue.Input;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CafeDemo
{
    // Both modes have one Transform writer and share the existing cursor/UI contract.
    public sealed partial class CafeDemoOrbitCamera
    {
        [Header("Presentation camera")]
        [SerializeField] private bool enablePresentationCamera;
        [SerializeField, Min(.1f)] private float presentationMoveSpeed = 4f;
        [SerializeField, Min(1f)] private float presentationFastMultiplier = 3f;
        [SerializeField, Range(.1f, 10f)] private float presentationFovStep = 3f;
        private CafeDemoPlayerController presentationPlayer;
        private PlayerDialogueInteractor presentationInteractor;
        private Camera presentationCamera;
        private float presentationYaw, presentationPitch;
        private float orbitFieldOfView;
        private int modeChangedFrame = -1;
        public bool IsPresentationCameraActive { get; private set; }
        public bool IsPresentationUiHidden { get; private set; }
        public void SetPresentationCameraAllowed(bool allowed)
        {
            enablePresentationCamera = allowed;
            if (!allowed && IsPresentationCameraActive) ExitPresentationCamera();
        }

        private void InitializePresentationCamera()
        {
            SetPresentationCameraAllowed(EmbodiedNPC.Settings.DisplaySettings.FreeCameraEnabled);
            presentationCamera = GetComponent<Camera>();
            if (!playerRoot) return;
            presentationPlayer = playerRoot.GetComponent<CafeDemoPlayerController>();
            presentationInteractor = playerRoot.GetComponent<PlayerDialogueInteractor>();
        }

        private bool PresentationInputBlocked()
        {
            if (inputSuspended || dialogueCursorMode || settingsCursorMode || Time.frameCount == uiReleasedFrame) return true;
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            // Also protect other editable UI that does not use the dialogue suspension contract.
            return selected != null && (selected.GetComponentInParent<TMP_InputField>() != null ||
                                        selected.GetComponentInParent<InputField>() != null);
        }

        private bool PresentationReferencesReady() => target && target.gameObject.activeInHierarchy &&
            playerRoot && playerRoot.gameObject.activeInHierarchy && presentationPlayer &&
            presentationPlayer.isActiveAndEnabled && presentationCamera && presentationCamera.isActiveAndEnabled;

        private void Update()
        {
            if (!PresentationReferencesReady()) { ExitPresentationCamera(); return; }
            if (enablePresentationCamera && !PresentationInputBlocked() &&
                Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            {
                if (IsPresentationCameraActive) ExitPresentationCamera();
                else EnterPresentationCamera();
            }
            if (IsPresentationCameraActive && !PresentationInputBlocked() &&
                Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame)
                SetPresentationUiHidden(!IsPresentationUiHidden);
        }

        private void SetPresentationUiHidden(bool hidden)
        {
            IsPresentationUiHidden = hidden;
            presentationInteractor?.SetPromptHidden(hidden);
        }

        private void EnterPresentationCamera()
        {
            IsPresentationCameraActive = true;
            modeChangedFrame = Time.frameCount;
            orbitFieldOfView = presentationCamera.fieldOfView;
            var angles = transform.eulerAngles;
            presentationYaw = angles.y;
            presentationPitch = Mathf.DeltaAngle(0f, angles.x);
            presentationPlayer.SetPresentationInputSuppressed(true);
            SetPresentationUiHidden(false);
            presentationInteractor?.SetUsePlayerProximitySelection(true);
            SetPlayerVisible(true);
        }

        private void ExitPresentationCamera()
        {
            if (!IsPresentationCameraActive) return;
            IsPresentationCameraActive = false;
            modeChangedFrame = Time.frameCount;
            if (presentationCamera) presentationCamera.fieldOfView = orbitFieldOfView;
            if (presentationPlayer) presentationPlayer.SetPresentationInputSuppressed(false);
            SetPresentationUiHidden(false);
            if (presentationInteractor)
            {
                presentationInteractor.SetUsePlayerProximitySelection(false);
            }
            // Orbit yaw/pitch/distance were never changed. LateUpdate follows the live target.
        }

        private void UpdatePresentationCamera(bool captureChanged)
        {
            if (PresentationInputBlocked() || !pointerCaptured || captureChanged ||
                modeChangedFrame == Time.frameCount) return;
            float scroll = zoom.ReadValue<float>();
            // Normalize native Windows wheel units to the same per-notch FOV change.
            if (InputSystem.settings.scrollDeltaBehavior != InputSettings.ScrollDeltaBehavior.UniformAcrossAllPlatforms)
                scroll /= 120f;
            if (scroll != 0f)
                presentationCamera.fieldOfView = Mathf.Clamp(
                    presentationCamera.fieldOfView - scroll * presentationFovStep, 15f, 100f);
            var delta = pointerDelta.ReadValue<Vector2>();
            presentationYaw = Mathf.Repeat(presentationYaw + delta.x * mouseSensitivity, 360f);
            presentationPitch = Mathf.Clamp(presentationPitch - delta.y * mouseSensitivity, -89f, 89f);
            Quaternion rotation = Quaternion.Euler(presentationPitch, presentationYaw, 0f);
            Vector2 moveInput = presentationPlayer.ReadMoveInput();
            var keyboard = Keyboard.current;
            float vertical = keyboard == null ? 0f : (keyboard.spaceKey.isPressed ? 1f : 0f) -
                (keyboard.leftCtrlKey.isPressed ? 1f : 0f);
            Vector3 movement = Vector3.ClampMagnitude(rotation * new Vector3(moveInput.x, 0f, moveInput.y) +
                Vector3.up * vertical, 1f);
            float speed = presentationMoveSpeed * (keyboard != null && keyboard.leftShiftKey.isPressed
                ? presentationFastMultiplier : 1f);
            transform.SetPositionAndRotation(transform.position + movement * speed * Time.deltaTime, rotation);
        }
    }
}
