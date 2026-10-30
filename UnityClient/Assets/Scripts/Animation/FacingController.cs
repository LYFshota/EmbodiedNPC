using UnityEngine;
using UnityEngine.AI;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;

namespace EmbodiedNPC.Animation
{
    /// <summary>Bounded visual gaze and optional standing conversation turns after locomotion yields.</summary>
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
        private bool requested, bodyTurnAllowed, turningBody;
        private NavMeshAgent agent;
        private GoalPlanRunner goals;
        private PerceptionContextProvider posture;
        [SerializeField, Min(1f)] private float bodyDegreesPerSecond = 90f;
        private const float BodyTurnStart = 70f, BodyTurnStop = 20f;
        public bool IsBodyTurning => turningBody;
        private bool captured;
        private Quaternion neutralLocalRotation;

        public Quaternion NeutralLocalRotation => neutralLocalRotation;
        public bool IsDialogueFacingActive { get; private set; }
        public Transform Visual => visual;
        public float MaxVisualYawDegrees => maxVisualYawDegrees;
        private bool CanDrive => captured && visual != null && actorRoot != null &&
            visual != actorRoot && visual.parent == actorRoot;

        private void Awake() { CaptureNeutral(); CacheActor(); }
        private void CacheActor()
        {
            agent = actorRoot == null ? null : actorRoot.GetComponent<NavMeshAgent>();
            goals = actorRoot == null ? null : actorRoot.GetComponent<GoalPlanRunner>();
            posture = actorRoot == null ? null : actorRoot.GetComponent<PerceptionContextProvider>();
        }
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
            CaptureNeutral(); CacheActor();
        }
        // The owning ExpressiveController resolves the authorized dialogue speaker.
        public void SetDialogueTarget(Transform speaker, bool active, bool allowBodyTurn = false)
        {
            if (target != speaker || !active || !allowBodyTurn) ReleaseBodyRotation();
            target = active ? speaker : null; requested = active && speaker != null;
            bodyTurnAllowed = requested && allowBodyTurn;
        }
        private bool CanTurnBody => bodyTurnAllowed && motionChannels != null &&
            !motionChannels.IsTaskMotionActive && !motionChannels.IsEmergencyMotionActive &&
            !motionChannels.IsCarryPresentationActive && (goals == null || !goals.HasActiveTask) &&
            (posture == null || !posture.HasAssignedFixedSeat) && agent != null &&
            agent.isActiveAndEnabled && agent.isOnNavMesh && agent.updateRotation && !agent.pathPending && !agent.hasPath &&
            agent.velocity.sqrMagnitude < .0025f;
        private void ReleaseBodyRotation() => turningBody = false;
        private void TurnBody(Vector3 direction, float deltaTime)
        {
            if (!CanTurnBody || direction.sqrMagnitude < .0001f) { ReleaseBodyRotation(); return; }
            float yaw = Vector3.SignedAngle(actorRoot.forward, direction, Vector3.up);
            if (Mathf.Abs(yaw) > BodyTurnStart) turningBody = true;
            if (Mathf.Abs(yaw) <= BodyTurnStop) turningBody = false;
            if (!turningBody) return;
            // No path, velocity or formal owner: take rotation only for this synchronous write.
            // Never retain updateRotation=false into a later executor/runner admission.
            agent.updateRotation = false;
            try
            {
                actorRoot.rotation = Quaternion.RotateTowards(actorRoot.rotation,
                    Quaternion.LookRotation(direction, Vector3.up), bodyDegreesPerSecond * deltaTime);
            }
            finally { agent.updateRotation = true; }
        }
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
                TurnBody(direction, deltaTime);
                if (direction.sqrMagnitude > 0.0001f)
                {
                    Vector3 local = actorRoot.InverseTransformDirection(direction);
                    Vector3 neutralForward = Vector3.ProjectOnPlane(neutralLocalRotation * Vector3.forward, Vector3.up);
                    float yaw = Vector3.SignedAngle(neutralForward, Vector3.ProjectOnPlane(local, Vector3.up), Vector3.up);
                    desired = Quaternion.AngleAxis(Mathf.Clamp(yaw, -maxVisualYawDegrees, maxVisualYawDegrees), Vector3.up) * neutralLocalRotation;
                }
            }
            if (!IsDialogueFacingActive) ReleaseBodyRotation();
            visual.localRotation = Quaternion.RotateTowards(visual.localRotation, desired, degreesPerSecond * deltaTime);
        }
        public void ResetPresentation()
        {
            ReleaseBodyRotation();
            target = null; requested = bodyTurnAllowed = false; IsDialogueFacingActive = false;
            if (CanDrive) visual.localRotation = neutralLocalRotation;
        }
        private void LateUpdate() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
