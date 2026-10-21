using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.World
{
    public readonly struct AreaMembershipChange
    {
        public AreaMembershipChange(string entityId, EntityKind entityKind, AgentRole agentRole,
            string previousAreaEntityId, string currentAreaEntityId)
        {
            EntityId = entityId ?? string.Empty;
            EntityKind = entityKind;
            AgentRole = agentRole;
            PreviousAreaEntityId = previousAreaEntityId ?? string.Empty;
            CurrentAreaEntityId = currentAreaEntityId ?? string.Empty;
        }

        public string EntityId { get; }
        public EntityKind EntityKind { get; }
        public AgentRole AgentRole { get; }
        public string PreviousAreaEntityId { get; }
        public string CurrentAreaEntityId { get; }
    }

    /// <summary>Owns the single primary semantic area for each active runtime entity.</summary>
    [DisallowMultipleComponent]
    public sealed class AreaMembershipService : MonoBehaviour
    {
        [SerializeField] private EntityRegistry registry;

        private readonly Dictionary<string, WorldEntity> primaryAreasByEntityId = new();
        private bool subscribed;

        public event Action<AreaMembershipChange> PrimaryAreaChanged;

        private void OnEnable() => Subscribe();

        private void OnDisable() => Unsubscribe();

        public void Configure(EntityRegistry entityRegistry)
        {
            Unsubscribe();
            registry = entityRegistry;
            if (isActiveAndEnabled) Subscribe();
        }

        public bool TryEnterArea(WorldEntity entity, WorldEntity areaEntity, out string error)
        {
            if (!TryResolveRegistered(entity, out WorldEntity registeredEntity))
            {
                error = "Entity must be active in the EntityRegistry.";
                return false;
            }
            if (!TryResolveRegistered(areaEntity, out WorldEntity registeredArea))
            {
                error = "Area must be active in the EntityRegistry.";
                return false;
            }
            if (registeredArea.EntityKind != EntityKind.Area)
            {
                error = $"WorldEntity '{registeredArea.name}' is not an Area.";
                return false;
            }
            if (registeredEntity == registeredArea)
            {
                error = "An Area cannot contain itself.";
                return false;
            }

            if (!primaryAreasByEntityId.TryGetValue(registeredEntity.EntityId, out WorldEntity previousArea) ||
                previousArea != registeredArea)
            {
                primaryAreasByEntityId[registeredEntity.EntityId] = registeredArea;
                PublishChange(registeredEntity, previousArea, registeredArea);
            }
            error = string.Empty;
            return true;
        }

        public bool LeaveArea(WorldEntity entity, WorldEntity areaEntity)
        {
            if (entity == null || areaEntity == null ||
                !primaryAreasByEntityId.TryGetValue(entity.EntityId, out WorldEntity currentArea) ||
                currentArea != areaEntity) return false;

            primaryAreasByEntityId.Remove(entity.EntityId);
            PublishChange(entity, currentArea, null);
            return true;
        }

        public bool ClearPrimaryArea(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId) ||
                !primaryAreasByEntityId.TryGetValue(entityId, out WorldEntity currentArea)) return false;
            WorldEntity entity = null;
            if (registry != null) registry.TryGetEntity(entityId, out entity);
            primaryAreasByEntityId.Remove(entityId);
            if (entity != null) PublishChange(entity, currentArea, null);
            return true;
        }

        public bool TryGetPrimaryArea(string entityId, out WorldEntity areaEntity)
        {
            if (string.IsNullOrWhiteSpace(entityId) ||
                !primaryAreasByEntityId.TryGetValue(entityId, out areaEntity))
            {
                areaEntity = null;
                return false;
            }

            if (!TryResolveRegisteredEntityId(entityId) ||
                !TryResolveRegistered(areaEntity, out WorldEntity registeredArea) ||
                registeredArea.EntityKind != EntityKind.Area)
            {
                primaryAreasByEntityId.Remove(entityId);
                areaEntity = null;
                return false;
            }

            areaEntity = registeredArea;
            return true;
        }

        private bool TryResolveRegistered(WorldEntity candidate, out WorldEntity registered)
        {
            registered = null;
            return candidate != null && registry != null && registry.IsReady &&
                   registry.TryGetEntity(candidate.EntityId, out registered) && registered == candidate;
        }

        private bool TryResolveRegisteredEntityId(string entityId)
        {
            return registry != null && registry.IsReady && registry.TryGetEntity(entityId, out _);
        }

        private void Subscribe()
        {
            if (subscribed || registry == null) return;
            registry.EntityUnregistered += HandleEntityUnregistered;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (registry != null) registry.EntityUnregistered -= HandleEntityUnregistered;
            subscribed = false;
        }

        private void HandleEntityUnregistered(WorldEntity entity)
        {
            if (entity == null) return;
            if (primaryAreasByEntityId.TryGetValue(entity.EntityId, out WorldEntity previousArea))
            {
                primaryAreasByEntityId.Remove(entity.EntityId);
                PublishChange(entity, previousArea, null);
            }

            List<string> membersToClear = null;
            foreach (KeyValuePair<string, WorldEntity> membership in primaryAreasByEntityId)
            {
                if (membership.Value != entity) continue;
                membersToClear ??= new List<string>();
                membersToClear.Add(membership.Key);
            }

            if (membersToClear == null) return;
            foreach (string memberEntityId in membersToClear)
            {
                primaryAreasByEntityId.Remove(memberEntityId);
                if (registry != null && registry.TryGetEntity(memberEntityId, out WorldEntity member))
                    PublishChange(member, entity, null);
            }
        }

        private void PublishChange(WorldEntity entity, WorldEntity previousArea, WorldEntity currentArea)
        {
            if (entity == null || PrimaryAreaChanged == null) return;
            AreaMembershipChange change = new(entity.EntityId, entity.EntityKind, entity.AgentRole,
                previousArea == null ? string.Empty : previousArea.EntityId,
                currentArea == null ? string.Empty : currentArea.EntityId);
            foreach (Delegate subscriber in PrimaryAreaChanged.GetInvocationList())
            {
                try { ((Action<AreaMembershipChange>)subscriber).Invoke(change); }
                catch (Exception exception)
                {
                    Debug.LogError($"[AreaMembership] change subscriber failed: {exception.GetType().Name}", this);
                }
            }
        }
    }
}
