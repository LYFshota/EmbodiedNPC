using System;
using System.Collections.Generic;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    /// <summary>Projects a new observer-local visual presence into a routable fact.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore))]
    public sealed class VisualObservationAgentEventBridge : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private AgentEventRouter router;
        [SerializeField, Min(0f)] private float visualReengagementSeconds = 20f;

        private sealed class VisualPresenceState
        {
            public bool CurrentlyVisible;
            public double LastVisibleAt;
        }

        private readonly Dictionary<string, VisualPresenceState> presenceBySubject =
            new(StringComparer.Ordinal);
        private bool subscribed;

        public int EventCount { get; private set; }
        public AgentEvent LastEvent { get; private set; }
        public float VisualReengagementSeconds => visualReengagementSeconds;

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (perceivedState == null) perceivedState = GetComponent<PerceivedStateStore>();
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            if (router == null) router = FindAnyObjectByType<AgentEventRouter>();
        }

        private void OnEnable() => Subscribe();

        private void OnDisable()
        {
            Unsubscribe();
            foreach (VisualPresenceState state in presenceBySubject.Values)
                state.CurrentlyVisible = false;
        }

        public void Configure(WorldEntity owner, PerceivedStateStore state, EntityRegistry registry,
            AgentEventRouter eventRouter, float reengagementSeconds = 20f)
        {
            Unsubscribe();
            observer = owner;
            perceivedState = state;
            entityRegistry = registry;
            router = eventRouter;
            visualReengagementSeconds = Mathf.Max(0f, reengagementSeconds);
            if (isActiveAndEnabled) Subscribe();
        }

        public bool TryProject(ObservationApplyResult result, out AgentEvent agentEvent)
        {
            agentEvent = null;
            Observation observation = result.Observation;
            if (!result.IsApplied || observation == null ||
                observation.Channel != ObservationChannel.Visual || observer == null ||
                !string.Equals(observation.ObserverEntityId, observer.EntityId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(observation.SubjectEntityId) ||
                string.Equals(observation.SubjectEntityId, observer.EntityId, StringComparison.Ordinal) ||
                entityRegistry == null ||
                !entityRegistry.TryGetEntity(observation.SubjectEntityId, out WorldEntity subject) ||
                !subject.IsAgent(AgentRole.Player)) return false;

            if (observation.LifecycleState != PerceptionLifecycleState.Tracked ||
                !observation.KnownFields.TryGetValue("visibility_state", out string visibility) ||
                visibility != "current_visible")
            {
                if (presenceBySubject.TryGetValue(subject.EntityId, out VisualPresenceState missing))
                    missing.CurrentlyVisible = false;
                return false;
            }

            double observedAt = observation.ObservedAt;
            if (double.IsNaN(observedAt) || double.IsInfinity(observedAt) || observedAt < 0d)
                return false;
            bool newPresence = !presenceBySubject.TryGetValue(subject.EntityId, out VisualPresenceState state);
            if (newPresence)
            {
                state = new VisualPresenceState();
                presenceBySubject.Add(subject.EntityId, state);
            }
            else if (observedAt < state.LastVisibleAt)
            {
                return false;
            }
            else if (state.CurrentlyVisible ||
                     observedAt - state.LastVisibleAt < visualReengagementSeconds)
            {
                state.CurrentlyVisible = true;
                state.LastVisibleAt = observedAt;
                return false;
            }

            state.CurrentlyVisible = true;
            state.LastVisibleAt = observedAt;
            agentEvent = new AgentEvent($"agent_event_{Guid.NewGuid():N}",
                AgentEventTypes.EntityBecameVisible, subject.EntityId, observer.EntityId,
                string.Empty, null, observedAt, observation.WorldRevision,
                string.IsNullOrWhiteSpace(observation.TraceId)
                    ? $"cognitive-trace-{Guid.NewGuid():N}" : observation.TraceId,
                observation.ObservationId);
            return true;
        }

        private void HandleObservationApplied(ObservationApplyResult result)
        {
            if (!TryProject(result, out AgentEvent agentEvent)) return;
            LastEvent = agentEvent;
            EventCount++;
            router?.Route(agentEvent);
        }

        private void Subscribe()
        {
            if (subscribed || !isActiveAndEnabled || perceivedState == null) return;
            perceivedState.ObservationApplied += HandleObservationApplied;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (perceivedState != null)
                perceivedState.ObservationApplied -= HandleObservationApplied;
            subscribed = false;
        }

        private void OnValidate() => visualReengagementSeconds = Mathf.Max(0f, visualReengagementSeconds);
    }
}
