using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Bounded offsets on this frame's Humanoid pose, after Visual body facing.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(120)]
    public sealed class HeadGazeController : MonoBehaviour
    {
        [SerializeField] private AttentionController attention;
        [SerializeField] private Animator animator;
        [SerializeField] private Transform visualBody;
        [SerializeField] private Transform actorRoot;
        [SerializeField] private GestureController gesturePlayback;
        [SerializeField, Range(0, 60)] private float fullYaw = 30f;
        [SerializeField, Range(1, 60)] private float maxYaw = 50f;
        [SerializeField, Range(0, 90)] private float combinedYawCap = 75f;
        [SerializeField, Range(0, 40)] private float downPitch = 20f;
        [SerializeField, Range(0, 40)] private float upPitch = 25f;
        [SerializeField, Range(0, 1)] private float neckShare = .35f;
        [SerializeField, Min(.01f)] private float blendIn = .25f;
        [SerializeField, Min(.01f)] private float blendOut = .4f;
        [SerializeField, Min(1f)] private float listeningDegreesPerSecond = 85f;
        [SerializeField, Min(1f)] private float talkingDegreesPerSecond = 65f;
        private ExpressiveController expressive;
        private Transform head, neck;
        private Quaternion headBase, neckBase, headApplied, neckApplied;
        private bool applied;
        private Vector2 angles;
        public float CurrentWeight { get; private set; }
        public Vector2 CurrentAngles => angles;
        public bool IsRigReady => head != null;
        public Transform Head => head;
        public Transform Neck => neck;
        public void SetGesturePlayback(GestureController playback) => gesturePlayback = playback;

        private void Awake()
        { expressive = actorRoot == null ? null : actorRoot.GetComponent<ExpressiveController>(); ResolveRig(); }
        public void Configure(AttentionController selection, Animator character, Transform body, Transform actor)
        {
            ResetPresentation(); attention = selection; animator = character; visualBody = body; actorRoot = actor;
            expressive = actorRoot == null ? null : actorRoot.GetComponent<ExpressiveController>();
            ResolveRig();
        }
        private void ResolveRig()
        {
            head = neck = null;
            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid) return;
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        }

        // Also usable by deterministic tests. Angle axes come from body space, never bone-local XYZ.
        public static Vector3 SolveAngles(Transform actor, Transform body, Vector3 origin, Vector3 point,
            float full = 30f, float maximum = 50f, float combined = 75f, float down = 20f, float up = 25f)
        {
            Vector3 direction = point - origin;
            if (actor == null || body == null || direction.sqrMagnitude < .0001f) return Vector3.zero;
            Vector3 local = body.InverseTransformDirection(direction.normalized);
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            float weight = Mathf.Abs(yaw) <= full ? 1f :
                Mathf.Clamp01((maximum - Mathf.Abs(yaw)) / Mathf.Max(.01f, maximum - full));
            float bodyYaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(actor.forward, actor.up),
                Vector3.ProjectOnPlane(body.forward, actor.up), actor.up);
            yaw = Mathf.Clamp(yaw, -maximum, maximum);
            yaw = Mathf.Clamp(bodyYaw + yaw, -combined, combined) - bodyYaw;
            return new Vector3(yaw, Mathf.Clamp(pitch, -down, up), weight);
        }

        public void Tick(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime < 0) return;
            RestorePose();
            if (!isActiveAndEnabled || head == null || visualBody == null || actorRoot == null ||
                animator == null || !animator.isActiveAndEnabled) { ResetPresentation(); return; }
            bool nod = gesturePlayback != null && gesturePlayback.IsNodActive;
            bool tracking = !nod && attention != null && attention.isActiveAndEnabled && attention.HasGazePoint && !attention.IsSuppressed;
            Vector3 desired = tracking ? SolveAngles(actorRoot, visualBody, head.position, attention.CurrentGazePoint,
                fullYaw, maxYaw, combinedYawCap, downPitch, upPitch) : Vector3.zero;
            float duration = nod ? .15f : desired.z > CurrentWeight ? blendIn : blendOut;
            CurrentWeight = Mathf.MoveTowards(CurrentWeight, desired.z, deltaTime / Mathf.Max(.01f, duration));
            // Retain the last legal direction while fading, rather than snapping to forward on loss.
            if (tracking && desired.z > 0)
                angles = Vector2.MoveTowards(angles, new Vector2(desired.x, desired.y),
                    (expressive != null && expressive.State == DialoguePresentationState.Talking ?
                        talkingDegreesPerSecond : listeningDegreesPerSecond) * deltaTime);
            if (CurrentWeight <= 0) { angles = Vector2.zero; if (!nod) return; }
            float bodyYaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(actorRoot.forward, actorRoot.up),
                Vector3.ProjectOnPlane(visualBody.forward, actorRoot.up), actorRoot.up);
            float yaw = Mathf.Clamp(bodyYaw + angles.x * CurrentWeight, -combinedYawCap, combinedYawCap) - bodyYaw;
            yaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
            float pitch = angles.y * CurrentWeight;
            // One bone writer: gaze yields before the project's bounded one-shot Nod offset.
            if (nod && gesturePlayback.NodElapsed >= .15f) { yaw = 0; pitch = -gesturePlayback.NodPitch; }
            headBase = head.localRotation;
            if (neck != null) neckBase = neck.localRotation;
            Quaternion offset = Quaternion.AngleAxis(yaw, visualBody.up) * Quaternion.AngleAxis(-pitch, visualBody.right);
            float share = neck == null ? 0 : neckShare;
            if (neck != null) neck.rotation = Quaternion.Slerp(Quaternion.identity, offset, share) * neck.rotation;
            head.rotation = Quaternion.Slerp(Quaternion.identity, offset, 1f - share) * head.rotation;
            headApplied = head.localRotation;
            if (neck != null) neckApplied = neck.localRotation;
            applied = true;
        }
        private void RestorePose()
        {
            if (!applied) return;
            // Animator may already have written a fresh pose. Never replace that with last frame's base.
            if (head != null && Quaternion.Angle(head.localRotation, headApplied) < .01f) head.localRotation = headBase;
            if (neck != null && Quaternion.Angle(neck.localRotation, neckApplied) < .01f) neck.localRotation = neckBase;
            applied = false;
        }
        public void ResetPresentation()
        { RestorePose(); CurrentWeight = 0; angles = Vector2.zero; }
        private void Update() => RestorePose(); // Remove our previous offset before Animator evaluation, including culling/pause.
        private void LateUpdate() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
