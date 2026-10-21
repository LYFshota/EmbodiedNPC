using System;
using EmbodiedNPC.Events;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Converts only EventInterest Wake results into observable CognitiveTrigger values.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AgentEventReceiver))]
    public sealed class WakeCognitiveTriggerBridge : MonoBehaviour
    {
        [SerializeField] private AgentEventReceiver eventReceiver;

        private bool isSubscribed;

        public AgentEventReceiver EventReceiver => eventReceiver;
        public CognitiveTrigger LastTrigger { get; private set; }
        public event Action<CognitiveTrigger> CognitiveTriggerCreated;

        private void Awake()
        {
            if (eventReceiver == null) eventReceiver = GetComponent<AgentEventReceiver>();
        }

        private void OnEnable() => Subscribe();

        private void OnDisable() => Unsubscribe();

        public void Configure(AgentEventReceiver receiver)
        {
            Unsubscribe();
            eventReceiver = receiver;
            Subscribe();
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (eventReceiver == null || string.IsNullOrWhiteSpace(eventReceiver.EntityId))
            {
                error = "WakeCognitiveTriggerBridge requires a configured AgentEventReceiver.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void Subscribe()
        {
            if (!isActiveAndEnabled || isSubscribed || eventReceiver == null) return;
            eventReceiver.InterestEvaluated += HandleInterestEvaluated;
            isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!isSubscribed || eventReceiver == null) return;
            eventReceiver.InterestEvaluated -= HandleInterestEvaluated;
            isSubscribed = false;
        }

        private void HandleInterestEvaluated(AgentEvent agentEvent, EventInterestDecision decision)
        {
            bool task = agentEvent?.EventType == AgentEventTypes.TaskConsideration && (GetComponent<TaskConsiderationSource>()?.CanWake(agentEvent) ?? false);
            if (decision != EventInterestDecision.Wake && !task || agentEvent == null ||
                !TryValidateConfiguration(out _)) return;
            if(agentEvent.EventType==AgentEventTypes.TaskConsideration && !task) return;
            if (agentEvent.EventType == AgentEventTypes.CounterNeedsCleaning &&
                !(GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.CanWake(agentEvent) ?? false)) return;
            if (agentEvent.EventType == AgentEventTypes.PlayerDwelledInServiceZone &&
                !(GetComponent<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>()?.CanWake(agentEvent) ?? false)) return;
            if (string.Equals(agentEvent.EventType, AgentEventTypes.ActionFailed, StringComparison.Ordinal) &&
                GetComponent<CognitiveRunDriver>() is { } driver &&
                driver.OwnsActionOutcome(agentEvent)) return;

            LastTrigger = new CognitiveTrigger(Guid.NewGuid().ToString("N"), CognitiveTriggerTypes.Event,
                eventReceiver.EntityId, agentEvent.EventId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, agentEvent.EventType);
            if (CognitiveTriggerCreated == null) return;

            foreach (Delegate subscriber in CognitiveTriggerCreated.GetInvocationList())
            {
                try { ((Action<CognitiveTrigger>)subscriber).Invoke(LastTrigger); }
                catch (Exception exception)
                {
                    Debug.LogError($"[WakeCognitiveTriggerBridge] subscriber failed: {exception.GetType().Name}",
                        this);
                }
            }
        }

        private void OnValidate()
        {
            if (eventReceiver == null) eventReceiver = GetComponent<AgentEventReceiver>();
        }
    }
}
