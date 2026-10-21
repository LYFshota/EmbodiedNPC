using System;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Owns the single foreground CognitiveRun slot for one Agent.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    [RequireComponent(typeof(WakeCognitiveTriggerBridge))]
    public sealed class AgentOrchestrator : MonoBehaviour
    {
        [SerializeField] private WorldEntity ownerEntity;
        [SerializeField] private WakeCognitiveTriggerBridge triggerSource;

        private bool isSubscribed;
        private CognitiveRun activeRun;

        public WorldEntity OwnerEntity => ownerEntity;
        public WakeCognitiveTriggerBridge TriggerSource => triggerSource;
        public CognitiveRun CurrentRun => activeRun;
        public CognitiveRun LastRun { get; private set; }
        public bool HasActiveRun => activeRun != null && !activeRun.IsTerminal;
        public event Action<CognitiveRun> RunStarted;

        private void Awake()
        {
            if (ownerEntity == null) ownerEntity = GetComponent<WorldEntity>();
            if (triggerSource == null) triggerSource = GetComponent<WakeCognitiveTriggerBridge>();
        }

        private void OnEnable() => Subscribe();

        private void OnDisable() => Unsubscribe();

        public void Configure(WorldEntity owner, WakeCognitiveTriggerBridge source)
        {
            Unsubscribe();
            ownerEntity = owner;
            triggerSource = source;
            Subscribe();
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (ownerEntity == null || ownerEntity.EntityKind != EntityKind.Agent ||
                string.IsNullOrWhiteSpace(ownerEntity.EntityId))
            {
                error = "AgentOrchestrator requires a configured Agent WorldEntity.";
                return false;
            }
            if (triggerSource == null || triggerSource.gameObject != gameObject)
            {
                error = "AgentOrchestrator requires a WakeCognitiveTriggerBridge on the same Agent root.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public bool TryStartRun(CognitiveTrigger trigger, out CognitiveRun run)
        {
            run = null;
            if (trigger == null || !TryValidateConfiguration(out _) || HasActiveRun ||
                !string.Equals(trigger.AgentEntityId, ownerEntity.EntityId, StringComparison.Ordinal))
                return false;

            bool service = false;
            if (triggerSource.EventReceiver.TryGetRecentEvent(trigger.SourceEventId, out var sourceEvent) &&
                sourceEvent.EventType == EmbodiedNPC.Events.AgentEventTypes.PlayerDwelledInServiceZone)
            {
                if (!ReferenceEquals(triggerSource.LastTrigger, trigger) ||
                    !(GetComponent<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>()?.CanWake(sourceEvent) ?? false)) return false;
                service = true;
            }
            bool counter = sourceEvent?.EventType == EmbodiedNPC.Events.AgentEventTypes.CounterNeedsCleaning;
            if (counter && (!ReferenceEquals(triggerSource.LastTrigger, trigger) ||
                !(GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.CanWake(sourceEvent) ?? false))) return false;
            bool task = sourceEvent?.EventType == EmbodiedNPC.Events.AgentEventTypes.TaskConsideration;
            if(task && (!ReferenceEquals(triggerSource.LastTrigger,trigger) || !(GetComponent<TaskConsiderationSource>()?.CanWake(sourceEvent) ?? false))) return false;
            run = new CognitiveRun(Guid.NewGuid().ToString("N"), ownerEntity.EntityId,
                trigger.TriggerId, trigger.TriggerType, UtcNowSeconds(), service, counter, task);
            activeRun = run;
            LastRun = run;
            InvokeRunStarted(run);
            return true;
        }

        public bool CompleteCurrentRun() => FinishCurrentRun(CognitiveRunState.Completed);

        public bool CancelCurrentRun() => FinishCurrentRun(CognitiveRunState.Cancelled);

        public bool FailCurrentRun() => FinishCurrentRun(CognitiveRunState.Failed);

        public bool WaitForCurrentRunActionResult() =>
            HasActiveRun && activeRun.TryWaitForActionResult();

        private bool FinishCurrentRun(CognitiveRunState terminalState)
        {
            if (!HasActiveRun || !activeRun.TryFinish(terminalState, UtcNowSeconds())) return false;

            LastRun = activeRun;
            activeRun = null;
            return true;
        }

        private void Subscribe()
        {
            if (!isActiveAndEnabled || isSubscribed || triggerSource == null) return;
            triggerSource.CognitiveTriggerCreated += HandleTriggerCreated;
            isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!isSubscribed || triggerSource == null) return;
            triggerSource.CognitiveTriggerCreated -= HandleTriggerCreated;
            isSubscribed = false;
        }

        private void HandleTriggerCreated(CognitiveTrigger trigger) => TryStartRun(trigger, out _);

        private void InvokeRunStarted(CognitiveRun run)
        {
            if (RunStarted == null) return;

            foreach (Delegate subscriber in RunStarted.GetInvocationList())
            {
                try { ((Action<CognitiveRun>)subscriber).Invoke(run); }
                catch (Exception exception)
                {
                    Debug.LogError($"[AgentOrchestrator] subscriber failed: {exception.GetType().Name}", this);
                }
            }
        }

        private static double UtcNowSeconds() =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

        private void OnValidate()
        {
            if (ownerEntity == null) ownerEntity = GetComponent<WorldEntity>();
            if (triggerSource == null) triggerSource = GetComponent<WakeCognitiveTriggerBridge>();
        }
    }
}
