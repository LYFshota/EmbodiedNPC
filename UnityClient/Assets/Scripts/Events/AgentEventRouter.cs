using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    [Serializable]
    public sealed class AgentEventAreaSubscription
    {
        [SerializeField] private string eventType;
        [SerializeField] private WorldEntity areaEntity;
        [SerializeField] private AgentEventReceiver receiver;

        public AgentEventAreaSubscription(string subscribedEventType, WorldEntity subscribedArea,
            AgentEventReceiver subscribedReceiver)
        {
            eventType = subscribedEventType;
            areaEntity = subscribedArea;
            receiver = subscribedReceiver;
        }

        public string EventType => eventType ?? string.Empty;
        public string AreaEntityId => areaEntity == null ? string.Empty : areaEntity.EntityId;
        public AgentEventReceiver Receiver => receiver;
    }

    /// <summary>Routes only direct targets and explicitly configured Area subscriptions.</summary>
    [DisallowMultipleComponent]
    public sealed class AgentEventRouter : MonoBehaviour
    {
        [SerializeField] private AgentEventReceiver[] receivers = Array.Empty<AgentEventReceiver>();
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private AgentEventAreaSubscription[] areaSubscriptions =
            Array.Empty<AgentEventAreaSubscription>();

        private readonly Dictionary<string, AgentEventReceiver> receiversByEntityId =
            new(StringComparer.Ordinal);
        private readonly Dictionary<AreaRouteKey, List<AgentEventReceiver>> areaRoutes = new();

        private void Awake()
        {
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            RebuildRoutes();
        }

        public void Configure(AgentEventReceiver[] configuredReceivers,
            AgentEventAreaSubscription[] configuredAreaSubscriptions, EntityRegistry registry = null)
        {
            if (registry != null) entityRegistry = registry;
            receivers = configuredReceivers ?? Array.Empty<AgentEventReceiver>();
            areaSubscriptions = configuredAreaSubscriptions ?? Array.Empty<AgentEventAreaSubscription>();
            RebuildRoutes();
        }

        public bool TryValidateConfiguration(out string error)
        {
            HashSet<string> receiverIds = new(StringComparer.Ordinal);
            foreach (AgentEventReceiver receiver in receivers)
            {
                if (receiver == null)
                {
                    error = "Configured AgentEventReceiver is missing.";
                    return false;
                }
                if (!receiver.TryValidateConfiguration(out error)) return false;
                if (!receiverIds.Add(receiver.EntityId))
                {
                    error = $"Duplicate AgentEventReceiver EntityId '{receiver.EntityId}'.";
                    return false;
                }
            }

            foreach (AgentEventAreaSubscription subscription in areaSubscriptions)
            {
                if (subscription == null || string.IsNullOrWhiteSpace(subscription.EventType) ||
                    string.IsNullOrWhiteSpace(subscription.AreaEntityId) || subscription.Receiver == null)
                {
                    error = "Area subscriptions require event type, Area WorldEntity, and receiver.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public int Route(AgentEvent agentEvent)
        {
            if (agentEvent == null || entityRegistry == null || !entityRegistry.IsReady) return 0;

            HashSet<AgentEventReceiver> destinations = new();
            if (!string.IsNullOrWhiteSpace(agentEvent.TargetEntityId) &&
                TryResolveDirectReceiver(agentEvent.TargetEntityId, out AgentEventReceiver directReceiver))
                destinations.Add(directReceiver);

            if (!string.IsNullOrWhiteSpace(agentEvent.AreaEntityId) &&
                areaRoutes.TryGetValue(new AreaRouteKey(agentEvent.EventType, agentEvent.AreaEntityId),
                    out List<AgentEventReceiver> subscribedReceivers))
            {
                foreach (AgentEventReceiver receiver in subscribedReceivers)
                    if (receiver != null) destinations.Add(receiver);
            }

            int delivered = 0;
            foreach (AgentEventReceiver receiver in destinations)
                if (IsRoutableReceiver(receiver) && receiver.Receive(agentEvent)) delivered++;
            return delivered;
        }

        private bool IsRoutableReceiver(AgentEventReceiver receiver) =>
            receiver != null && receiver.isActiveAndEnabled && receiver.gameObject.activeInHierarchy &&
            receiver.ReceiverEntity != null && entityRegistry.IsRegistered(receiver.ReceiverEntity);

        /// <summary>Deliver an observer-local projection without turning the observer into the social target.</summary>
        public bool RouteObserved(AgentEvent agentEvent, string observerEntityId) =>
            agentEvent != null && entityRegistry != null && entityRegistry.IsReady &&
            !string.IsNullOrWhiteSpace(observerEntityId) &&
            TryResolveDirectReceiver(observerEntityId, out AgentEventReceiver receiver) &&
            IsRoutableReceiver(receiver) && receiver.Receive(agentEvent);

        private bool TryResolveDirectReceiver(string entityId, out AgentEventReceiver receiver)
        {
            if (receiversByEntityId.TryGetValue(entityId, out receiver) && receiver != null) return true;

            // Scene references can awaken before their WorldEntity identity is observable to this component.
            // Fall back only across the explicitly configured endpoints, then cache the exact match.
            foreach (AgentEventReceiver configuredReceiver in receivers)
            {
                if (configuredReceiver == null ||
                    !string.Equals(configuredReceiver.EntityId, entityId, StringComparison.Ordinal)) continue;
                receiversByEntityId[entityId] = configuredReceiver;
                receiver = configuredReceiver;
                return true;
            }

            receiver = null;
            return false;
        }

        public void RebuildRoutes()
        {
            receiversByEntityId.Clear();
            areaRoutes.Clear();

            foreach (AgentEventReceiver receiver in receivers)
            {
                if (receiver == null || string.IsNullOrWhiteSpace(receiver.EntityId) ||
                    receiversByEntityId.ContainsKey(receiver.EntityId)) continue;
                receiversByEntityId.Add(receiver.EntityId, receiver);
            }

            foreach (AgentEventAreaSubscription subscription in areaSubscriptions)
            {
                if (subscription == null || string.IsNullOrWhiteSpace(subscription.EventType) ||
                    string.IsNullOrWhiteSpace(subscription.AreaEntityId) || subscription.Receiver == null) continue;
                AreaRouteKey key = new(subscription.EventType, subscription.AreaEntityId);
                if (!areaRoutes.TryGetValue(key, out List<AgentEventReceiver> routeReceivers))
                {
                    routeReceivers = new List<AgentEventReceiver>();
                    areaRoutes.Add(key, routeReceivers);
                }
                if (!routeReceivers.Contains(subscription.Receiver)) routeReceivers.Add(subscription.Receiver);
            }
        }

        private readonly struct AreaRouteKey : IEquatable<AreaRouteKey>
        {
            public AreaRouteKey(string eventType, string areaEntityId)
            {
                EventType = eventType ?? string.Empty;
                AreaEntityId = areaEntityId ?? string.Empty;
            }

            private string EventType { get; }
            private string AreaEntityId { get; }

            public bool Equals(AreaRouteKey other) =>
                string.Equals(EventType, other.EventType, StringComparison.Ordinal) &&
                string.Equals(AreaEntityId, other.AreaEntityId, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is AreaRouteKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(EventType), StringComparer.Ordinal.GetHashCode(AreaEntityId));
        }
    }
}
