using UnityEngine;
using UnityEngine.InputSystem;
using EmbodiedNPC.Dialogue.Input;

namespace CafeDemo
{
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public sealed class CafeDemoPlayerController : MonoBehaviour, IDialogueInputSuspendTarget
    {
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private string moveActionName = "Player/Move";
        [SerializeField] private Transform view;
        [SerializeField, Min(.1f)] private float moveSpeed = 2.6f;
        [SerializeField, Min(0)] private float turnSpeed = 540f;
        // Zero preserves the original demo response. V2 opts into bounded velocity changes.
        [SerializeField, Min(0)] private float acceleration;
        [SerializeField, Min(0)] private float deceleration;
        [SerializeField] private float gravity = -20f;
        private CharacterController controller;
        private InputAction move;
        private float verticalSpeed;
        private Vector3 planarVelocity;
        private bool inputSuspended;
        private bool presentationInputSuppressed;
        private bool waitForMoveRelease;

        public Transform View => view;
        public CharacterController Controller => controller;
        public bool IsInputSuspended => inputSuspended;
        public bool IsPresentationInputSuppressed => presentationInputSuppressed;
        internal Vector2 ReadMoveInput() => move == null ? Vector2.zero : Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1);

        internal void SetPresentationInputSuppressed(bool suppressed)
        {
            if (presentationInputSuppressed == suppressed) return;
            presentationInputSuppressed = suppressed;
            planarVelocity = Vector3.zero;
            verticalSpeed = 0f;
            if (!suppressed) waitForMoveRelease = true;
        }

        public void SetDialogueInputSuspended(bool suspended)
        {
            if (inputSuspended && !suspended) waitForMoveRelease = true;
            inputSuspended = suspended;
            if (suspended) planarVelocity = Vector3.zero;
        }

        private void Awake() { controller = GetComponent<CharacterController>(); }
        private void Start()
        {
            var appearance = GetComponent<PlayerVrmAppearance>() ?? gameObject.AddComponent<PlayerVrmAppearance>();
            appearance.Initialize(GetComponent<PlayerLocomotionPresenter>()?.Animator);
        }
        private void OnEnable()
        {
            if (!inputActions || !view)
            {
                Debug.LogError("CafeDemo player requires an input asset and camera reference.", this);
                enabled = false;
                return;
            }
            // Clone only Move: enabling/disabling this demo never changes the shared input asset.
            move = inputActions.FindAction(moveActionName, true).Clone();
            move.Enable();
            verticalSpeed = 0;
            planarVelocity = Vector3.zero;
        }
        private void OnDisable() { move?.Disable(); move?.Dispose(); move = null; verticalSpeed = 0; planarVelocity = Vector3.zero; }
        private void Update()
        {
            // Freeze local locomotion only. Never move/warp the authoritative player for a camera.
            if (presentationInputSuppressed) return;
            Vector2 rawInput = ReadMoveInput();
            if (waitForMoveRelease && rawInput.sqrMagnitude < .0001f) waitForMoveRelease = false;
            Vector2 input = inputSuspended || waitForMoveRelease ? Vector2.zero : rawInput;
            Vector3 forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 direction = Vector3.ClampMagnitude(forward * input.y + right * input.x, 1);
            Vector3 desiredVelocity = direction * moveSpeed;
            bool braking = desiredVelocity.sqrMagnitude < planarVelocity.sqrMagnitude ||
                Vector3.Dot(planarVelocity, desiredVelocity) < 0f;
            float rate = braking ? deceleration : acceleration;
            planarVelocity = inputSuspended ? Vector3.zero : rate > 0f
                ? Vector3.MoveTowards(planarVelocity, desiredVelocity, rate * Time.deltaTime) : desiredVelocity;
            if (controller.isGrounded && verticalSpeed < 0) verticalSpeed = -2f;
            verticalSpeed = Mathf.Max(verticalSpeed + gravity * Time.deltaTime, -30f);
            var flags = controller.Move((planarVelocity + Vector3.up * verticalSpeed) * Time.deltaTime);
            // Contact correction can exceed the requested displacement, especially at high FPS.
            // Read collision-limited movement without turning depenetration into extra momentum.
            planarVelocity = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(controller.velocity, Vector3.up), planarVelocity.magnitude);
            if ((flags & CollisionFlags.Above) != 0 && verticalSpeed > 0) verticalSpeed = 0;
            Vector3 facing = acceleration > 0f || deceleration > 0f ? planarVelocity : direction;
            if (facing.sqrMagnitude > .001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(facing), turnSpeed * Time.deltaTime);
        }
    }
}
