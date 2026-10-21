using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Rotates only the authored Visual wrapper. Never writes the actor or navigation.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class FacingController : MonoBehaviour
    {
        [SerializeField] private Transform visual;
        [SerializeField] private Transform actorRoot;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField, Range(0f, 60f)] private float maxVisualYawDegrees = 55f;
        [SerializeField, Min(1f)] private float degreesPerSecond = 120f;
        private Transform target;
        private bool requested;
        private bool captured;
        private Quaternion neutralLocalRotation;

        public Quaternion NeutralLocalRotation => neutralLocalRotation;
        public bool IsDialogueFacingActive { get; private set; }
        public Transform Visual => visual;
        public float MaxVisualYawDegrees => maxVisualYawDegrees;
        private bool CanDrive => captured && visual != null && actorRoot != null &&
            visual != actorRoot && visual.parent == actorRoot;

        private void Awake() => CaptureNeutral();
        private void CaptureNeutral()
        {
            if (visual == null) visual = transform;
            if (!captured) { neutralLocalRotation = visual.localRotation; captured = true; }
        }
        public void Configure(Transform wrapper, Transform actor, MotionChannelCoordinator channels)
        {
            ResetPresentation();
            if (visual != wrapper) captured = false;
            visual = wrapper; actorRoot = actor; motionChannels = channels;
            CaptureNeutral();
        }
        // The owning ExpressiveController resolves the authorized dialogue speaker.
        public void SetDialogueTarget(Transform speaker, bool active)
        { target = active ? speaker : null; requested = active && speaker != null; }
        public void Tick(float deltaTime)
        {
            if (!isActiveAndEnabled || !CanDrive || !float.IsFinite(deltaTime) || deltaTime < 0f) return;
            MotionChannel body = motionChannels == null ? MotionChannel.None : motionChannels.ActiveBodyChannel;
            IsDialogueFacingActive = requested && target != null && target.gameObject.activeInHierarchy &&
                body != MotionChannel.Emergency && body != MotionChannel.Interaction && body != MotionChannel.Locomotion;
            Quaternion desired = neutralLocalRotation;
            if (IsDialogueFacingActive)
            {
                Vector3 direction = Vector3.ProjectOnPlane(target.position - actorRoot.position, Vector3.up);
                if (direction.sqrMagnitude > 0.0001f)
                {
                    Vector3 local = actorRoot.InverseTransformDirection(direction);
                    Vector3 neutralForward = Vector3.ProjectOnPlane(neutralLocalRotation * Vector3.forward, Vector3.up);
                    float yaw = Vector3.SignedAngle(neutralForward, Vector3.ProjectOnPlane(local, Vector3.up), Vector3.up);
                    desired = Quaternion.AngleAxis(Mathf.Clamp(yaw, -maxVisualYawDegrees, maxVisualYawDegrees), Vector3.up) * neutralLocalRotation;
                }
            }
            visual.localRotation = Quaternion.RotateTowards(visual.localRotation, desired, degreesPerSecond * deltaTime);
        }
        public void ResetPresentation()
        {
            target = null; requested = false; IsDialogueFacingActive = false;
            if (CanDrive) visual.localRotation = neutralLocalRotation;
        }
        private void LateUpdate() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
