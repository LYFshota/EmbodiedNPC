using System;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    /// <summary>Projects authoritative Player primary-area changes into AgentEvent.</summary>
    [DisallowMultipleComponent]
    public sealed class AreaMembershipAgentEventBridge : MonoBehaviour
    {
        [SerializeField] private AreaMembershipService membershipService;
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private WorldStateService worldState;
        private bool subscribed;

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();

        public void Configure(AreaMembershipService service, AgentEventRouter eventRouter,
            WorldStateService state = null)
        {
            Unsubscribe();
            membershipService = service;
            router = eventRouter;
            worldState = state;
            if (isActiveAndEnabled) Subscribe();
        }

        public int Publish(AreaMembershipChange change)
        {
            if (router == null || change.EntityKind != EntityKind.Agent || change.AgentRole != AgentRole.Player)
                return 0;

            int delivered = 0;
            int? revision = worldState == null ? null : worldState.WorldRevision;
            double occurredAt = Time.realtimeSinceStartupAsDouble;
            if (!string.IsNullOrWhiteSpace(change.PreviousAreaEntityId))
            {
                delivered += router.Route(new AgentEvent(NewEventId(), AgentEventTypes.PlayerLeftArea,
                    change.EntityId, string.Empty, change.PreviousAreaEntityId, null, occurredAt, revision));
            }
            if (!string.IsNullOrWhiteSpace(change.CurrentAreaEntityId))
            {
                delivered += router.Route(new AgentEvent(NewEventId(), AgentEventTypes.PlayerEnteredArea,
                    change.EntityId, string.Empty, change.CurrentAreaEntityId, null, occurredAt, revision));
            }
            return delivered;
        }

        private void HandlePrimaryAreaChanged(AreaMembershipChange change) => Publish(change);

        private void Subscribe()
        {
            if (subscribed || membershipService == null) return;
            membershipService.PrimaryAreaChanged += HandlePrimaryAreaChanged;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (membershipService != null)
                membershipService.PrimaryAreaChanged -= HandlePrimaryAreaChanged;
            subscribed = false;
        }

        private static string NewEventId() => $"agent_event_{Guid.NewGuid():N}";
    }
}
