using System;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Interaction
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class DeterministicGiveRunner : MonoBehaviour
    {
        [SerializeField] private NavMeshAgent agent;
        [SerializeField, Min(0f)] private float arrivalTolerance = 0.1f;
        [SerializeField, Min(0.1f)] private float timeoutSeconds = 30f;
        [SerializeField] private WorldEntity carrier;
        [SerializeField] private Transform playerHoldPoint;
        [SerializeField] private ResourceReservationService reservations;
        [SerializeField] private WorldStateService worldState;
        private string activeReservationId;
        private ResolvedBringObjectGoal context;
        private bool started, complete, failed;
        private float elapsedSeconds;
        public event Action<SkillResult> Completed;

        private void Awake() { if (agent == null) agent = GetComponent<NavMeshAgent>(); if (carrier == null) carrier = GetComponent<WorldEntity>(); if (reservations == null) reservations = FindAnyObjectByType<ResourceReservationService>(); if (worldState == null) worldState = GetComponent<WorldStateService>(); }

        private void Update()
        {
            if (!started || complete || failed) return;
            elapsedSeconds += Time.deltaTime;
            if (elapsedSeconds >= timeoutSeconds) Fail(SkillStatus.TimedOut, "GIVE_TIMEOUT");
        }

        private void LateUpdate()
        {
            if (!started || complete || failed || agent.pathPending) return;
            if (agent.pathStatus != NavMeshPathStatus.PathComplete) { Fail(SkillStatus.Failed, "PLAYER_PATH_INVALID"); return; }
            // Use the agent's path distance instead of a raw world-space distance.
            // ReceivePoint is a NavMesh interaction point and may have a different
            // vertical coordinate from the character root.
            if (agent.remainingDistance > agent.stoppingDistance + arrivalTolerance) return;
            SkillResult result = worldState == null
                ? default
                : worldState.TryTransferOwnership($"{context.Target.EntityId}:give", GetTraceId(), context.Actor, context.Target,
                    context.Destination, context.TargetHeldState, playerHoldPoint, context.DestinationAnchors.HoldPoint);
            if (worldState == null) { FailWithoutRecording("WORLD_STATE_UNAVAILABLE"); return; }
            if (result.Status != SkillStatus.Succeeded)
            {
                failed = true;
                StopAgent();
                Publish(result);
                return;
            }
            StopAgent(); complete = true;
            Debug.Log($"[DeterministicGiveRunner] GIVE SUCCESS: {context.Target.EntityId} is held by {context.Destination.EntityId}.", this);
            Publish(result);
        }

        public void BeginGive(ResolvedBringObjectGoal resolvedContext, string reservationId)
        {
            if (started) return;
            ResetForNextRun(); context = resolvedContext;
            activeReservationId = reservationId;
            if (context == null || context.Actor == null || agent == null || carrier == null || reservations == null || string.IsNullOrWhiteSpace(reservationId) ||
                !reservations.IsLeaseValid(reservationId, context.Actor.EntityId) || playerHoldPoint == null || context.TargetHeldState == null ||
                context.DestinationAnchors == null || !context.DestinationAnchors.HasGiveAnchors)
            { Fail(SkillStatus.Failed, "REFERENCE_MISSING"); return; }
            if (!context.TargetHeldState.IsHeld || context.TargetHeldState.HolderEntityId != context.Actor.EntityId)
            { Fail(SkillStatus.Failed, "ITEM_NOT_HELD_BY_NPC"); return; }
            if (!agent.isOnNavMesh) { Fail(SkillStatus.Failed, "AGENT_NOT_ON_NAVMESH"); return; }
            agent.isStopped = false; elapsedSeconds = 0f;
            started = agent.SetDestination(context.DestinationAnchors.ReceivePoint.position);
            if (!started) { Fail(SkillStatus.Failed, "PLAYER_DESTINATION_REJECTED"); return; }
            Debug.Log($"[DeterministicGiveRunner] 前往 {context.Destination.EntityId} 的 Receive 交互点。", this);
        }

        public void Cancel()
        {
            Cancel("CANCELLED_BY_REQUEST");
        }

        public void Cancel(string failureCode)
        {
            if (!started || complete || failed) return;
            string code = string.IsNullOrWhiteSpace(failureCode) ? "CANCELLED_BY_REQUEST" : failureCode;
            failed = true; StopAgent();
            Publish(RecordFailure(SkillStatus.Cancelled, code));
        }

        public void ResetForNextRun() { started = false; complete = false; failed = false; elapsedSeconds = 0f; }
        private void Fail(SkillStatus status, string code)
        {
            failed = true;
            StopAgent();
            Publish(RecordFailure(status, code));
        }

        private void FailWithoutRecording(string code)
        {
            failed = true;
            StopAgent();
            Publish(CreateFallbackResult(SkillStatus.Failed, code));
        }

        private SkillResult RecordFailure(SkillStatus status, string code)
        {
            if (worldState != null)
            {
                return worldState.RecordAttempt($"{context?.Target?.EntityId ?? "unknown"}:give", GetTraceId(),
                    "Give", context?.Actor, context?.Target, context?.Destination, status, code);
            }

            Debug.LogError($"[DeterministicGiveRunner] {code}", this);
            return CreateFallbackResult(status, code);
        }

        private SkillResult CreateFallbackResult(SkillStatus status, string code) =>
            new($"{context?.Target?.EntityId ?? "unknown"}:give", GetTraceId(), "Give",
                context?.Actor?.EntityId, context?.Target?.EntityId, context?.Destination?.EntityId,
                status, code, 0, 0, 0);
        private string GetTraceId() { return reservations != null && reservations.TryGetSnapshot(activeReservationId, out ReservationLeaseSnapshot lease) ? lease.TraceId : string.Empty; }
        private void StopAgent() { started = false; if (agent == null) return; agent.isStopped = true; if (agent.hasPath) agent.ResetPath(); }
        private void Publish(SkillResult result) { Completed?.Invoke(result); }
    }
}
