using System;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Interaction
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class DeterministicPickupRunner : MonoBehaviour
    {
        [SerializeField] private NavMeshAgent agent;
        [SerializeField, Min(0f)] private float arrivalTolerance = 0.1f;
        [SerializeField, Min(0.1f)] private float timeoutSeconds = 30f;
        [SerializeField] private WorldEntity carrier;
        [SerializeField] private Transform handHoldPoint;
        [SerializeField] private ResourceReservationService reservations;
        [SerializeField] private WorldStateService worldState;
        [SerializeField] private bool autoStartOnPlay;
        private ResolvedBringObjectGoal context;
        private string activeReservationId;
        private bool navigationStarted, pickupComplete, failureReported;
        private float elapsedSeconds;
        public event Action<SkillResult> Completed;

        private void Awake() { if (agent == null) agent = GetComponent<NavMeshAgent>(); if (carrier == null) carrier = GetComponent<WorldEntity>(); if (reservations == null) reservations = FindAnyObjectByType<ResourceReservationService>(); if (worldState == null) worldState = GetComponent<WorldStateService>(); }
        private void Start() { if (autoStartOnPlay) Debug.LogWarning("[DeterministicPickupRunner] autoStartOnPlay 需要通过 Planner 提供上下文，已忽略。", this); }

        private void Update()
        {
            if (!navigationStarted || pickupComplete || failureReported) return;
            elapsedSeconds += Time.deltaTime;
            if (elapsedSeconds >= timeoutSeconds) { Fail(SkillStatus.TimedOut, "PICKUP_TIMEOUT"); return; }
            if (agent.pathPending) return;
            if (!agent.isOnNavMesh) { Fail(SkillStatus.Failed, "AGENT_NOT_ON_NAVMESH"); return; }
            if (agent.pathStatus == NavMeshPathStatus.PathInvalid) { Fail(SkillStatus.Failed, "PATH_INVALID"); return; }
            if (agent.pathStatus == NavMeshPathStatus.PathPartial) { Fail(SkillStatus.Failed, "PATH_PARTIAL"); return; }
            if (Vector3.Distance(transform.position, context.TargetAnchors.PickupApproachPoint.position) <= agent.stoppingDistance + arrivalTolerance) CompletePickup();
        }

        public void BeginPickup(ResolvedBringObjectGoal resolvedContext, string reservationId)
        {
            if (navigationStarted) return;
            ResetForNextRun();
            context = resolvedContext;
            activeReservationId = reservationId;
            if (context == null || context.Actor == null || agent == null || carrier == null || reservations == null || string.IsNullOrWhiteSpace(reservationId) ||
                !reservations.IsLeaseValid(reservationId, context.Actor.EntityId) || context.Target == null || context.TargetHeldState == null ||
                context.TargetAnchors == null || !context.TargetAnchors.HasPickupAnchors || handHoldPoint == null)
            { Fail(SkillStatus.Failed, "REFERENCE_MISSING"); return; }
            if (context.TargetHeldState.IsHeld) { Fail(SkillStatus.Failed, "ITEM_ALREADY_HELD"); return; }
            if (!agent.isOnNavMesh) { Fail(SkillStatus.Failed, "AGENT_NOT_ON_NAVMESH"); return; }
            agent.isStopped = false;
            elapsedSeconds = 0f;
            navigationStarted = agent.SetDestination(context.TargetAnchors.PickupApproachPoint.position);
            if (!navigationStarted) { Fail(SkillStatus.Failed, "DESTINATION_REJECTED"); return; }
            Debug.Log($"[DeterministicPickupRunner] 开始：{carrier.EntityId} 前往 {context.Target.EntityId} 的 Pickup 交互点。", this);
        }

        public void Cancel()
        {
            Cancel("CANCELLED_BY_REQUEST");
        }

        public void Cancel(string failureCode)
        {
            if (!navigationStarted || pickupComplete || failureReported) return;
            string code = string.IsNullOrWhiteSpace(failureCode) ? "CANCELLED_BY_REQUEST" : failureCode;
            failureReported = true;
            StopAgent();
            Publish(RecordFailure(SkillStatus.Cancelled, code));
        }

        public void ResetForNextRun() { navigationStarted = false; pickupComplete = false; failureReported = false; elapsedSeconds = 0f; }

        private void CompletePickup()
        {
            SkillResult result = worldState == null
                ? default
                : worldState.TryPickUp($"{context.Target.EntityId}:pickup", GetTraceId(), context.Actor, context.Target,
                    context.TargetHeldState, handHoldPoint, context.TargetAnchors.GrabPoint);
            if (worldState == null) { FailWithoutRecording("WORLD_STATE_UNAVAILABLE"); return; }
            if (result.Status != SkillStatus.Succeeded)
            {
                failureReported = true;
                StopAgent();
                Publish(result);
                return;
            }
            pickupComplete = true; StopAgent();
            Debug.Log($"[DeterministicPickupRunner] PICKUP SUCCESS: {context.Target.EntityId} is held by {context.Actor.EntityId}.", this);
            Publish(result);
        }

        private void Fail(SkillStatus status, string code)
        {
            failureReported = true;
            StopAgent();
            Publish(RecordFailure(status, code));
        }

        private void FailWithoutRecording(string code)
        {
            failureReported = true;
            StopAgent();
            Publish(CreateFallbackResult(SkillStatus.Failed, code));
        }

        private SkillResult RecordFailure(SkillStatus status, string code)
        {
            if (worldState != null)
            {
                return worldState.RecordAttempt($"{context?.Target?.EntityId ?? "unknown"}:pickup", GetTraceId(),
                    "PickUp", context?.Actor, context?.Target, null, status, code);
            }

            Debug.LogError($"[DeterministicPickupRunner] {code}", this);
            return CreateFallbackResult(status, code);
        }

        private SkillResult CreateFallbackResult(SkillStatus status, string code) =>
            new($"{context?.Target?.EntityId ?? "unknown"}:pickup", GetTraceId(), "PickUp",
                context?.Actor?.EntityId, context?.Target?.EntityId, null, status, code, 0, 0, 0);
        private string GetTraceId() { return reservations != null && reservations.TryGetSnapshot(GetReservationId(), out ReservationLeaseSnapshot lease) ? lease.TraceId : string.Empty; }
        private string GetReservationId() { return activeReservationId; }
        private void StopAgent() { navigationStarted = false; if (agent == null) return; agent.isStopped = true; if (agent.hasPath) agent.ResetPath(); }
        private void Publish(SkillResult result) { Completed?.Invoke(result); }
    }
}
