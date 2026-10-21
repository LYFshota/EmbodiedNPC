using UnityEngine;
using UnityEngine.InputSystem;
using EmbodiedNPC.Dialogue.Input;

namespace CafeDemo
{
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent, DefaultExecutionOrder(-100)]
    public sealed partial class CafeDemoOrbitCamera : MonoBehaviour, IDialogueInputSuspendTarget, IDialogueCursorTarget
    {
        [SerializeField] private Transform target;
        [SerializeField] private Transform playerRoot;
        [SerializeField] private float initialYaw;
        [SerializeField, Range(15,55)] private float initialPitch = 30;
        [SerializeField, Min(.2f)] private float distance = 6;
        [SerializeField, Min(.2f)] private float minimumZoom = 1.2f;
        [SerializeField, Min(.2f)] private float maximumZoom = 7;
        [SerializeField] private Vector2 pitchLimits = new Vector2(-10,55);
        [SerializeField, Min(0)] private float mouseSensitivity = .18f;
        [SerializeField, Min(0)] private float wheelSensitivity = .006f;
        [SerializeField, Min(.01f)] private float collisionRadius = .20f;
        [SerializeField, Min(0)] private float collisionPadding = .06f;
        [SerializeField, Min(.1f)] private float recoverySpeed = 5;
        [SerializeField, Min(0)] private float hidePlayerBelowDistance = 1.15f;
        [SerializeField] private LayerMask obstructionMask = ~0;
        [SerializeField] private InputAction rotateButton = new InputAction("OrbitHold", InputActionType.Button, "<Mouse>/rightButton");
        [SerializeField] private InputAction pointerDelta = new InputAction("OrbitDelta", InputActionType.Value, "<Mouse>/delta");
        [SerializeField] private InputAction zoom = new InputAction("OrbitZoom", InputActionType.Value, "<Mouse>/scroll/y");
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private readonly InputAction releasePointer = new InputAction("OrbitRelease", InputActionType.Button, "<Keyboard>/escape");
        private bool pointerCaptured;
        private bool inputSuspended;
        private bool dialogueCursorMode;
        private bool settingsCursorMode;
        private bool restorePointerCapture;
        private int uiReleasedFrame = -1;
        private float yaw, pitch, actualDistance;
        private Renderer[] playerRenderers;
        private bool[] originalVisibility;

        public Transform Target => target;
        public float Yaw => yaw;
        public float Pitch => pitch;
        public float DesiredDistance => distance;
        public float ActualDistance => actualDistance;
        public float CollisionRadius => collisionRadius;
        public bool PointerCaptured => pointerCaptured;
        public bool IsInputSuspended => inputSuspended;

        public void SetDialogueInputSuspended(bool suspended)
        {
            inputSuspended = suspended;
        }

        public void SetDialogueCursorMode(bool active)
        {
            if (dialogueCursorMode == active) return;
            dialogueCursorMode = active;
            restorePointerCapture = !active;
            if (active) CapturePointer(false);
        }

        public void SetSettingsCursorMode(bool active)
        {
            if (settingsCursorMode == active) return;
            settingsCursorMode = active;
            if (active)
            {
                CapturePointer(false);
            }
            else
            {
                uiReleasedFrame = Time.frameCount;
                restorePointerCapture = false;
                CapturePointer(!dialogueCursorMode);
            }
        }

        public void RestorePointerAfterUi(bool wasCaptured)
        {
            restorePointerCapture = false;
            uiReleasedFrame = Time.frameCount;
            CapturePointer(wasCaptured && !dialogueCursorMode && !settingsCursorMode && !inputSuspended);
        }

        private void Awake()
        {
            yaw = initialYaw; pitch = Mathf.Clamp(initialPitch, pitchLimits.x, pitchLimits.y); actualDistance = distance;
            playerRenderers = playerRoot ? playerRoot.GetComponentsInChildren<Renderer>() : new Renderer[0];
            originalVisibility = new bool[playerRenderers.Length];
            for(int i=0;i<playerRenderers.Length;i++) originalVisibility[i]=playerRenderers[i].enabled;
            InitializePresentationCamera();
        }
        private void OnEnable()
        {
            if (!target || !playerRoot) { Debug.LogError("CafeDemo orbit camera requires target and player references.", this); enabled = false; return; }
            rotateButton.Enable(); pointerDelta.Enable(); zoom.Enable(); releasePointer.Enable();
            CapturePointer(!dialogueCursorMode && !settingsCursorMode);
        }
        private void OnDisable() { ExitPresentationCamera(); rotateButton.Disable(); pointerDelta.Disable(); zoom.Disable(); releasePointer.Disable(); CapturePointer(false); SetPlayerVisible(true); }
        private void OnDestroy() { rotateButton.Dispose(); pointerDelta.Dispose(); zoom.Dispose(); releasePointer.Dispose(); }

        private void CapturePointer(bool capture)
        {
            pointerCaptured = capture;
            Cursor.lockState = capture ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !capture;
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) CapturePointer(false);
        }

        private void LateUpdate()
        {
            if (!target || !playerRoot || !target.gameObject.activeInHierarchy || !playerRoot.gameObject.activeInHierarchy)
            {
                ExitPresentationCamera();
                return;
            }
            bool captureChanged = false;
            if (dialogueCursorMode || settingsCursorMode)
            {
                if (pointerCaptured) CapturePointer(false);
            }
            else if (restorePointerCapture)
            {
                // The session may end from this same Escape event. Wait until Escape is no longer this frame.
                if (!releasePointer.WasPressedThisFrame())
                {
                    CapturePointer(true);
                    restorePointerCapture = false;
                    captureChanged = true;
                }
            }
            else if (!pointerCaptured && rotateButton.WasPressedThisFrame()) { CapturePointer(true); captureChanged = true; }
            if (IsPresentationCameraActive)
            {
                UpdatePresentationCamera(captureChanged);
                return;
            }
            if (!inputSuspended && pointerCaptured && !captureChanged && Time.frameCount != modeChangedFrame && Time.frameCount != uiReleasedFrame)
            {
                var delta = pointerDelta.ReadValue<Vector2>();
                yaw = Mathf.Repeat(yaw + delta.x * mouseSensitivity, 360);
                pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, pitchLimits.x, pitchLimits.y);
            }
            float scroll = !inputSuspended && pointerCaptured && !captureChanged && Time.frameCount != modeChangedFrame && Time.frameCount != uiReleasedFrame
                ? zoom.ReadValue<float>() : 0;
            // Input System 1.20 defaults to one unit per notch instead of Windows' native 120.
            if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.UniformAcrossAllPlatforms) scroll *= 120;
            if (scroll != 0)
            {
                // Zoom inward from the visible position, including when a wall already pulled us in.
                float start = scroll > 0 ? Mathf.Min(distance, actualDistance) : distance;
                distance = Mathf.Clamp(start - scroll * wheelSensitivity, minimumZoom, maximumZoom);
            }
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
            Vector3 direction = rotation * Vector3.back;
            float allowed = distance;
            int count = Physics.SphereCastNonAlloc(target.position, collisionRadius, direction, hits, distance, obstructionMask, QueryTriggerInteraction.Ignore);
            // A saturated buffer cannot guarantee that the nearest wall was returned.
            if (count == hits.Length)
            {
                foreach (var hit in Physics.SphereCastAll(target.position, collisionRadius, direction, distance, obstructionMask, QueryTriggerInteraction.Ignore))
                    allowed = LimitDistance(allowed, hit);
            }
            else for (int i = 0; i < count; i++) allowed = LimitDistance(allowed, hits[i]);
            // Pull in immediately; only recovery is smoothed. Never clamp to a minimum beyond a wall.
            actualDistance = allowed < actualDistance ? allowed : Mathf.Lerp(actualDistance, allowed, 1 - Mathf.Exp(-recoverySpeed * Time.deltaTime));
            transform.SetPositionAndRotation(target.position + direction * actualDistance, rotation);
            // In tight corners the capsule would otherwise fill the entire view. No walls are faded.
            SetPlayerVisible(actualDistance >= hidePlayerBelowDistance);
        }
        private void SetPlayerVisible(bool visible)
        {
            if(playerRenderers==null)return;
            for(int i=0;i<playerRenderers.Length;i++)if(playerRenderers[i])playerRenderers[i].enabled=visible&&originalVisibility[i];
        }
        private float LimitDistance(float allowed, RaycastHit hit)
        {
            if (!hit.collider || hit.transform == playerRoot || hit.transform.IsChildOf(playerRoot)) return allowed;
            return Mathf.Min(allowed, Mathf.Max(0, hit.distance - collisionPadding));
        }
    }
}
