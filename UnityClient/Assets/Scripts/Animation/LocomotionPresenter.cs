using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Animation
{
    /// <summary>Read-only NavMesh projection into motion arbitration and an optional Animator Speed.</summary>
    [DisallowMultipleComponent]
    public sealed class LocomotionPresenter : MonoBehaviour
    {
        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private Animator animator;
        [SerializeField] private string speedParameter = "Speed";
        [SerializeField, Min(0f)] private float speedDeadzone = 0.05f;
        [SerializeField, Min(0f)] private float dampingSeconds = 0.1f;
        private RuntimeAnimatorController inspectedController;
        private int speedHash;
        public float PlanarSpeed { get; private set; }
        public float NormalizedSpeed { get; private set; }
        public bool NavigationActive { get; private set; }
        public bool HasSpeedParameter { get; private set; }

        private void Awake() => InspectAnimator();
        private void OnEnable() => InspectAnimator();
        public void Configure(NavMeshAgent source, MotionChannelCoordinator channels, Animator playback = null)
        { ClearProjection(); agent = source; motionChannels = channels; animator = playback; InspectAnimator(); }
        internal static float ProjectSpeed(Vector3 velocity, float referenceSpeed, float deadzone)
        {
            float speed = Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude;
            return !float.IsFinite(speed) || !float.IsFinite(referenceSpeed) || referenceSpeed <= 0f || speed <= deadzone
                ? 0f : Mathf.Clamp01(speed / referenceSpeed);
        }
        internal static bool IsNavigationInProgress(bool stopped, bool pending, bool hasPath,
            float remainingDistance, float stoppingDistance, float planarSpeed, float deadzone) =>
            !stopped && (pending || planarSpeed > deadzone ||
                hasPath && (float.IsPositiveInfinity(remainingDistance) || remainingDistance > stoppingDistance + deadzone));
        public void Tick(float deltaTime)
        {
            if (!isActiveAndEnabled) return;
            bool ready = agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
            Vector3 velocity = ready ? agent.velocity : Vector3.zero;
            PlanarSpeed = Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude;
            NormalizedSpeed = ready ? ProjectSpeed(velocity, agent.speed, speedDeadzone) : 0f;
            NavigationActive = ready && IsNavigationInProgress(agent.isStopped, agent.pathPending,
                agent.hasPath, agent.remainingDistance, agent.stoppingDistance, PlanarSpeed, speedDeadzone);
            // Reconcile only transitions, including a coordinator cleanup; never replay gestures.
            if (motionChannels != null && motionChannels.IsNavigationActive != NavigationActive)
                motionChannels.SetNavigationActive(NavigationActive);
            WriteSpeed(deltaTime);
        }
        private void InspectAnimator()
        {
            inspectedController = animator == null ? null : animator.runtimeAnimatorController;
            speedHash = Animator.StringToHash(speedParameter ?? string.Empty);
            HasSpeedParameter = false;
            if (animator == null || inspectedController == null) return;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float && parameter.nameHash == speedHash)
                { HasSpeedParameter = true; break; }
        }
        private void WriteSpeed(float deltaTime)
        {
            if (animator == null || !animator.isActiveAndEnabled) return;
            if (inspectedController != animator.runtimeAnimatorController) InspectAnimator();
            if (HasSpeedParameter && float.IsFinite(deltaTime) && deltaTime >= 0f)
                animator.SetFloat(speedHash, NormalizedSpeed, dampingSeconds, deltaTime);
        }
        private void ClearProjection()
        {
            PlanarSpeed = NormalizedSpeed = 0f; NavigationActive = false;
            if (motionChannels != null && motionChannels.IsNavigationActive) motionChannels.SetNavigationActive(false);
            if (animator != null && animator.isActiveAndEnabled)
            {
                if (inspectedController != animator.runtimeAnimatorController) InspectAnimator();
                if (HasSpeedParameter) animator.SetFloat(speedHash, 0f);
            }
        }
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() => ClearProjection();
    }
}
