using System;
using System.Collections.Generic;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Perception
{
    /// <summary>
    /// M6.5 observer-local fast path. Threat safety remains deterministic and immediate.
    /// </summary>
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore), typeof(NavMeshAgent))]
    public sealed class ThreatFastPathController : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField] private GoalPlanRunner goalPlanRunner;
        [SerializeField] private InteractionSafetyController safetyController;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private DialogueController dialogueController;
        [SerializeField, Range(0f, 1f)] private float minimumConfidence = 0.25f;
        [SerializeField, Min(0.1f)] private float retreatDistance = 1.5f;
        [SerializeField, Min(0.1f)] private float retreatTimeoutSeconds = 2.5f;
        [SerializeField, Min(0f)] private float guardDurationSeconds = 1f;
        [SerializeField, Min(0f)] private float retriggerCooldownSeconds = 0.5f;

        private readonly List<ThreatResponseRecord> history = new();
        private ThreatPolicy policy;
        private ThreatResponseRecord activeRecord;
        private double responseDeadline;
        private double guardUntil;
        private double nextAllowedTriggerAt;
        private bool retreatStarted;

        public event Action<ThreatResponseRecord> ThreatResponseStarted;
        public event Action<ThreatResponseRecord> ThreatResponseCompleted;
        public IReadOnlyList<ThreatResponseRecord> History => history;
        public ThreatResponseRecord LastRecord => history.Count == 0 ? null : history[history.Count - 1];
        public bool IsResponding => activeRecord != null && !activeRecord.IsCompleted;

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (perceivedState == null) perceivedState = GetComponent<PerceivedStateStore>();
            if (goalPlanRunner == null) goalPlanRunner = GetComponent<GoalPlanRunner>();
            if (safetyController == null) safetyController = GetComponent<InteractionSafetyController>();
            if (motionChannels == null) motionChannels = GetComponent<MotionChannelCoordinator>();
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (backendClient == null) backendClient = FindAnyObjectByType<BackendWebSocketClient>();
            if (dialogueController == null) dialogueController = FindAnyObjectByType<DialogueController>();
            policy = new ThreatPolicy(minimumConfidence);
        }

        private void OnEnable()
        {
            if (perceivedState != null) perceivedState.ObservationApplied += HandleObservationApplied;
        }

        private void OnDisable()
        {
            if (perceivedState != null) perceivedState.ObservationApplied -= HandleObservationApplied;
            if (activeRecord != null && !activeRecord.IsCompleted)
                CompleteResponse("CONTROLLER_DISABLED");
            else
            {
                StopAgent();
                motionChannels?.EndEmergencyMotion();
            }
        }

        private void Update()
        {
            if (!IsResponding) return;
            double now = Time.unscaledTimeAsDouble;
            bool retreatFinished = !retreatStarted || agent == null || !agent.pathPending &&
                (!agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.05f);
            if (now >= guardUntil && (retreatFinished || now >= responseDeadline))
                CompleteResponse(now >= responseDeadline && !retreatFinished ? "RETREAT_TIMEOUT_GUARDING" : "SAFE_GUARD_REACHED");
        }

        public bool TryHandleObservation(Observation observation)
        {
            double now = Time.unscaledTimeAsDouble;
            if (now < nextAllowedTriggerAt) return false;
            ThreatDecision decision = policy.Evaluate(observation, now);
            if (!decision.ShouldInterrupt) return false;

            if (IsResponding) CompleteResponse("REPLACED_BY_HIGH_PRIORITY_THREAT");
            nextAllowedTriggerAt = now + retriggerCooldownSeconds;

            bool backendAvailable = backendClient != null && backendClient.IsConnected;
            bool planInterrupted = goalPlanRunner != null &&
                                   goalPlanRunner.InterruptCurrentPlan("INTERRUPTED_BY_THREAT");
            if (!planInterrupted) safetyController?.RecoverToSafeState();
            dialogueController?.CancelForEmergency();
            backendClient?.ClearPendingRequests();
            motionChannels?.BeginEmergencyMotion();

            Vector3 sourceDirection = ApproximateDirectionToWorld(decision.ApproximateDirection, transform);
            if (sourceDirection.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(sourceDirection, Vector3.up);

            retreatStarted = decision.Response == ThreatResponseKind.RetreatAndGuard && TryStartRetreat(-sourceDirection);
            activeRecord = new ThreatResponseRecord(observation, decision, now,
                Math.Max(0d, (now - observation.ObservedAt) * 1000d), planInterrupted, backendAvailable, retreatStarted);
            history.Add(activeRecord);
            responseDeadline = now + retreatTimeoutSeconds;
            guardUntil = now + guardDurationSeconds;

            Debug.Log($"[ThreatFastPath] START observation_id={observation.ObservationId}; reason={decision.ReasonCode}; " +
                      $"plan_interrupted={planInterrupted}; backend_available={backendAvailable}; " +
                      $"retreat_started={retreatStarted}; latency_ms={activeRecord.FastPathLatencyMilliseconds:0.00}", this);
            ThreatResponseStarted?.Invoke(activeRecord);
            return true;
        }

        public ThreatDecision ReplayDecision(ThreatResponseRecord record) => record == null
            ? ThreatDecision.Ignored(null, "REPLAY_RECORD_MISSING")
            : (policy ??= new ThreatPolicy(minimumConfidence)).Evaluate(record.Observation, record.EvaluatedAt);

        public static Vector3 ApproximateDirectionToWorld(string directionBand, Transform observerTransform)
        {
            if (observerTransform == null) return Vector3.zero;
            return directionBand switch
            {
                "front" => observerTransform.forward,
                "rear" => -observerTransform.forward,
                "right" => observerTransform.right,
                "left" => -observerTransform.right,
                "here" => observerTransform.forward,
                _ => Vector3.zero,
            };
        }

        private void HandleObservationApplied(ObservationApplyResult result)
        {
            if (result.IsApplied) TryHandleObservation(result.Observation);
        }

        private bool TryStartRetreat(Vector3 retreatDirection)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh || retreatDirection.sqrMagnitude < 0.0001f)
                return false;
            Vector3 desired = transform.position + retreatDirection.normalized * retreatDistance;
            if (!NavMesh.SamplePosition(desired, out NavMeshHit hit, retreatDistance, agent.areaMask)) return false;
            agent.isStopped = false;
            return agent.SetDestination(hit.position);
        }

        private void CompleteResponse(string resultCode)
        {
            if (activeRecord == null || activeRecord.IsCompleted) return;
            StopAgent();
            motionChannels?.EndEmergencyMotion();
            activeRecord.Complete(Time.unscaledTimeAsDouble, resultCode);
            Debug.Log($"[ThreatFastPath] COMPLETE observation_id={activeRecord.Observation.ObservationId}; " +
                      $"result={activeRecord.ResultCode}; duration_ms={activeRecord.DurationMilliseconds:0.00}", this);
            ThreatResponseCompleted?.Invoke(activeRecord);
        }

        private void StopAgent()
        {
            retreatStarted = false;
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
            agent.isStopped = true;
            if (agent.hasPath) agent.ResetPath();
        }
    }

    public sealed class ThreatResponseRecord
    {
        public ThreatResponseRecord(Observation observation, ThreatDecision decision, double evaluatedAt,
            double fastPathLatencyMilliseconds, bool planInterrupted, bool backendAvailable, bool retreatStarted)
        {
            Observation = observation;
            Decision = decision;
            EvaluatedAt = evaluatedAt;
            FastPathLatencyMilliseconds = fastPathLatencyMilliseconds;
            PlanInterrupted = planInterrupted;
            BackendAvailable = backendAvailable;
            RetreatStarted = retreatStarted;
        }

        public Observation Observation { get; }
        public ThreatDecision Decision { get; }
        public double EvaluatedAt { get; }
        public double FastPathLatencyMilliseconds { get; }
        public bool PlanInterrupted { get; }
        public bool BackendAvailable { get; }
        public bool RetreatStarted { get; }
        public bool IsCompleted { get; private set; }
        public double CompletedAt { get; private set; }
        public string ResultCode { get; private set; } = string.Empty;
        public double DurationMilliseconds => IsCompleted ? Math.Max(0d, (CompletedAt - EvaluatedAt) * 1000d) : 0d;

        internal void Complete(double completedAt, string resultCode)
        {
            IsCompleted = true;
            CompletedAt = completedAt;
            ResultCode = resultCode ?? string.Empty;
        }

    }
}
