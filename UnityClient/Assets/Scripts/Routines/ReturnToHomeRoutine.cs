using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Routines
{
    /// <summary>One local return path followed by gradual restoration of the authored root orientation.</summary>
    [DisallowMultipleComponent]
    public sealed class ReturnToHomeRoutine : MonoBehaviour, INpcDefaultRoutine
    {
        [SerializeField] private Transform actorRoot;
        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private Transform homeAnchor;
        [SerializeField, Min(.1f)] private float sampleRadius = 1f;
        [SerializeField, Min(.01f)] private float arrivalTolerance = .08f;
        [SerializeField, Min(.1f)] private float retryCooldown = 1f;
        [SerializeField, Min(1f)] private float homeTurnSpeed = 180f;
        private Vector3 homePosition, ownedDestination;
        private Quaternion homeRotation;
        private NavMeshPath path;
        private bool ownsPath, warned;
        private double retryAt;
        private DailyBehaviorController dailyBehavior;
        public bool IsRunning { get; private set; }
        public Vector3 HomePosition => homePosition;
        public Quaternion HomeRotation => homeRotation;
        public bool OwnsPath => ownsPath;
        public bool IsWaitingForNavigation { get; private set; }
        public bool IsNavigationCoolingDown => Time.unscaledTimeAsDouble < retryAt;
        public int NavigationFailureCount { get; private set; }
        public int NavigationStartCount { get; private set; }
        public Vector3 DailyDestination => TrySelectDailyDestination(out var position, out _) ? position :
            actorRoot == null ? homePosition : actorRoot.position;
        private Quaternion DailyRotation => TrySelectDailyDestination(out _, out var rotation) ? rotation :
            actorRoot == null ? homeRotation : actorRoot.rotation;
        private bool TrySelectDailyDestination(out Vector3 position, out Quaternion rotation)
        {
            position = homePosition; rotation = homeRotation;
            if (dailyBehavior == null) return true;
            if (!dailyBehavior.isActiveAndEnabled || dailyBehavior.IsActivitySuspended || dailyBehavior.Profile == null ||
                !dailyBehavior.CurrentActivity.HasValue ||
                !dailyBehavior.Profile.AllowsActivity(dailyBehavior.CurrentMode, dailyBehavior.CurrentActivity.Value)) return false;
            if (dailyBehavior.CurrentActivity == DailyBehaviorActivity.HomeIdle) return true;
            return dailyBehavior.CurrentActivity == DailyBehaviorActivity.WindowIdle &&
                dailyBehavior.TryGetRestDestination(out position, out rotation);
        }
        public bool IsAtDailyDestination => actorRoot != null && AgentReady && TrySelectDailyDestination(out _, out _) &&
            Vector3.ProjectOnPlane(actorRoot.position - DailyDestination, Vector3.up).magnitude <=
            agent.stoppingDistance + arrivalTolerance && Quaternion.Angle(actorRoot.rotation, DailyRotation) <= 1f;

        private void Awake()
        {
            path = new NavMeshPath();
            dailyBehavior = GetComponent<DailyBehaviorController>();
            if (actorRoot == null || agent == null)
            {
                Debug.LogWarning("[ReturnHome] Actor root and NavMeshAgent are required; execution disabled.", this);
                enabled = false;
                return;
            }
            homePosition = homeAnchor == null ? actorRoot.position : homeAnchor.position;
            homeRotation = homeAnchor == null ? actorRoot.rotation : homeAnchor.rotation;
        }
        private bool AgentReady => agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;
        private bool StillOwnsDestination => AgentReady &&
            (agent.destination - ownedDestination).sqrMagnitude <= .0001f;
        public bool CanReachDailyDestination(Transform destination)
        {
            Vector3 point = destination == null ? homePosition : destination.position;
            return AgentReady && NavMesh.SamplePosition(point, out NavMeshHit hit, sampleRadius, agent.areaMask) &&
                Vector3.Distance(point, hit.position) <= .2f && agent.CalculatePath(hit.position, path) &&
                path.status == NavMeshPathStatus.PathComplete;
        }
        public void ResetNavigationRetry() { retryAt = 0; warned = false; IsWaitingForNavigation = false; }
        public void SuspendRoutine()
        {
            // A new formal destination may already have been issued earlier in this frame.
            if (ownsPath && StillOwnsDestination) agent.ResetPath();
            ownsPath = false;
            IsRunning = false;
        }
        public void TickRoutine(float deltaTime)
        {
            if (!isActiveAndEnabled || actorRoot == null) return;
            if (!TrySelectDailyDestination(out _, out _)) { SuspendRoutine(); IsWaitingForNavigation = false; return; }
            if (Time.unscaledTimeAsDouble < retryAt) return;
            if (!AgentReady) { FailReturn(); return; }
            if (ownsPath)
            {
                if (!StillOwnsDestination) { ownsPath = false; IsRunning = false; return; }
                if (agent.pathPending) return;
                if (agent.pathStatus != NavMeshPathStatus.PathComplete) { FailReturn(); return; }
                if (agent.remainingDistance > agent.stoppingDistance + arrivalTolerance ||
                    Vector3.ProjectOnPlane(agent.velocity, Vector3.up).magnitude > .05f) return;
                SuspendRoutine();
            }
            Vector3 targetPosition = DailyDestination;
            float distance = Vector3.ProjectOnPlane(actorRoot.position - targetPosition, Vector3.up).magnitude;
            if (distance > agent.stoppingDistance + arrivalTolerance)
            {
                if (!NavMesh.SamplePosition(targetPosition, out NavMeshHit hit, sampleRadius, agent.areaMask) ||
                    Vector3.Distance(targetPosition, hit.position) > .2f ||
                    !agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete ||
                    !agent.SetDestination(hit.position)) { FailReturn(); return; }
                ownedDestination = hit.position;
                NavigationStartCount++;
                ownsPath = IsRunning = true;
                agent.isStopped = false;
                warned = false;
                IsWaitingForNavigation = false;
                return;
            }
            actorRoot.rotation = Quaternion.RotateTowards(actorRoot.rotation, DailyRotation, homeTurnSpeed * Mathf.Max(0f, deltaTime));
            IsRunning = Quaternion.Angle(actorRoot.rotation, DailyRotation) > 1f;
            warned = false;
            IsWaitingForNavigation = false;
        }
        private void FailReturn()
        {
            SuspendRoutine();
            retryAt = Time.unscaledTimeAsDouble + retryCooldown;
            NavigationFailureCount++;
            IsWaitingForNavigation = true;
            dailyBehavior?.NotifyActivityNavigationFailure();
            if (warned) return;
            warned = true;
            Debug.LogWarning("[ReturnHome] Daily activity navigation unavailable; safe Idle until retry.", this);
        }
        private void OnDisable() => SuspendRoutine();
    }
}
