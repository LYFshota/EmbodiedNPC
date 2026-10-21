using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    /// <summary>Thin endpoint that records and exposes events delivered by AgentEventRouter.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    public sealed class AgentEventReceiver : MonoBehaviour
    {
        [SerializeField] private WorldEntity receiverEntity;
        [SerializeField] private EventInterestProfile interestProfile;
        [SerializeField] private CurrentBehaviorMode currentBehaviorMode;
        [SerializeField] private BehaviorModeInterestOverrideProfile modeInterestOverrides;
        [SerializeField, Range(1, 32)] private int recentEventCapacity = 8;
        [SerializeField] private bool logReceivedEvents;

        private readonly List<AgentEvent> recentEvents = new();
        private EmbodiedNPC.Routines.DailyBehaviorController dailyBehavior;

        public WorldEntity ReceiverEntity => receiverEntity;
        public string EntityId => receiverEntity == null ? string.Empty : receiverEntity.EntityId;
        public EventInterestProfile InterestProfile => interestProfile;
        public CurrentBehaviorMode CurrentBehaviorMode => currentBehaviorMode;
        public BehaviorModeInterestOverrideProfile ModeInterestOverrides => modeInterestOverrides;
        public AgentEvent LastEvent { get; private set; }
        public EventInterestDecision LastInterestDecision { get; private set; }
        public IReadOnlyList<AgentEvent> RecentEvents => recentEvents;
        public event Action<AgentEvent> EventReceived;
        public event Action<AgentEvent, EventInterestDecision> InterestEvaluated;

        private void Awake()
        {
            if (receiverEntity == null) receiverEntity = GetComponent<WorldEntity>();
            dailyBehavior = GetComponent<EmbodiedNPC.Routines.DailyBehaviorController>();
        }

        public void Configure(WorldEntity entity, int capacity = 8, bool enableDebugLog = false,
            EventInterestProfile profile = null, CurrentBehaviorMode behaviorMode = null,
            BehaviorModeInterestOverrideProfile behaviorModeOverrides = null)
        {
            receiverEntity = entity;
            interestProfile = profile;
            currentBehaviorMode = behaviorMode;
            modeInterestOverrides = behaviorModeOverrides;
            recentEventCapacity = Mathf.Clamp(capacity, 1, 32);
            logReceivedEvents = enableDebugLog;
            TrimHistory();
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (receiverEntity == null || receiverEntity.EntityKind != EntityKind.Agent)
            {
                error = "AgentEventReceiver requires an Agent WorldEntity.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(receiverEntity.EntityId))
            {
                error = "AgentEventReceiver requires a configured EntityId.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public bool Receive(AgentEvent agentEvent)
        {
            if (agentEvent == null || !isActiveAndEnabled || !TryValidateConfiguration(out _)) return false;

            LastEvent = agentEvent;
            LastInterestDecision = EventInterestEvaluator.Evaluate(agentEvent, EntityId, interestProfile,
                currentBehaviorMode == null ? string.Empty : currentBehaviorMode.CurrentModeId,
                modeInterestOverrides, dailyBehavior != null && dailyBehavior.isActiveAndEnabled ? dailyBehavior.CurrentMode.ToString().ToLowerInvariant() : "");
            recentEvents.Add(agentEvent);
            TrimHistory();
            if (logReceivedEvents)
                Debug.Log($"[AgentEventReceiver] receiver={EntityId}; type={agentEvent.EventType}; event={agentEvent.EventId}", this);
            if (EventReceived != null)
            {
                foreach (Delegate subscriber in EventReceived.GetInvocationList())
                {
                    try { ((Action<AgentEvent>)subscriber).Invoke(agentEvent); }
                    catch (Exception exception)
                    {
                        Debug.LogError($"[AgentEventReceiver] subscriber failed: {exception.GetType().Name}", this);
                    }
                }
            }
            if (InterestEvaluated != null)
            {
                foreach (Delegate subscriber in InterestEvaluated.GetInvocationList())
                {
                    try
                    {
                        ((Action<AgentEvent, EventInterestDecision>)subscriber)
                            .Invoke(agentEvent, LastInterestDecision);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError($"[AgentEventReceiver] interest subscriber failed: {exception.GetType().Name}",
                            this);
                    }
                }
            }
            return true;
        }

        /// <summary>Finds one still-bounded recent event by its stable correlation ID.</summary>
        public bool TryGetRecentEvent(string eventId, out AgentEvent agentEvent)
        {
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                for (int index = recentEvents.Count - 1; index >= 0; index--)
                {
                    AgentEvent candidate = recentEvents[index];
                    if (candidate != null && string.Equals(candidate.EventId, eventId, StringComparison.Ordinal))
                    {
                        agentEvent = candidate;
                        return true;
                    }
                }
            }

            agentEvent = null;
            return false;
        }

        private void TrimHistory()
        {
            while (recentEvents.Count > Mathf.Clamp(recentEventCapacity, 1, 32))
                recentEvents.RemoveAt(0);
        }

        private void OnValidate()
        {
            if (receiverEntity == null) receiverEntity = GetComponent<WorldEntity>();
            recentEventCapacity = Mathf.Clamp(recentEventCapacity, 1, 32);
        }
    }
}
