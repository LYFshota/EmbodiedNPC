using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.World
{
    /// <summary>Authoritative EntityId lookup for the active world runtime.</summary>
    public sealed class EntityRegistry : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("当前场景中需要按稳定 ID 查询的实体。请手工拖入 NPC、玩家和杯子。")]
        private List<WorldEntity> entities = new();

        [SerializeField]
        [Tooltip("M4 参数化场景开启后，自动发现当前场景中的 WorldEntity；Inspector 列表仍可用于显式限制实体。")]
        private bool autoDiscoverSceneEntities = true;

        [SerializeField]
        [Tooltip("进入播放模式时自动输出注册结果，方便 M2 验收。")]
        private bool logRegistrationsOnStart = true;

        private readonly Dictionary<string, WorldEntity> entitiesById = new(StringComparer.Ordinal);
        private readonly List<WorldEntity> registeredEntities = new();
        private readonly Dictionary<PersistentIdentityKey, WorldEntity> entitiesByPersistentIdentity = new();
        private readonly HashSet<string> retiredEntityIds = new(StringComparer.Ordinal);
        private IReadOnlyList<WorldEntity> registeredEntitiesView;

        public bool IsReady { get; private set; }
        public event Action<WorldEntity> EntityUnregistered;

        /// <summary>
        /// 已注册实体的只读视图。验证器只能读取，不得由此修改世界状态。
        /// </summary>
        public IReadOnlyList<WorldEntity> RegisteredEntities =>
            registeredEntitiesView ??= registeredEntities.AsReadOnly();

        private void Awake()
        {
            RebuildRegistry();

            if (logRegistrationsOnStart && IsReady)
            {
                LogRegistrations();
            }
        }

        /// <summary>
        /// Registry 未通过完整 validation 时始终返回 false；调用方不能使用 invalid snapshot。
        /// </summary>
        public bool TryGetEntity(string entityId, out WorldEntity entity)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(entityId))
            {
                entity = null;
                return false;
            }

            if (entitiesById.TryGetValue(entityId, out entity) && entity != null) return true;
            entity = null;
            return false;
        }

        /// <summary>Resolves an active persistent identity only while the complete Registry snapshot is valid.</summary>
        public bool TryResolvePersistentIdentity(PersistentIdentityKey key, out WorldEntity entity)
        {
            if (!IsReady || !key.IsValid)
            {
                entity = null;
                return false;
            }

            if (entitiesByPersistentIdentity.TryGetValue(key, out entity) && entity != null) return true;
            entity = null;
            return false;
        }

        public void Register(WorldEntity entity)
        {
            if (!IsReady) throw new InvalidOperationException("EntityRegistry must be ready before runtime registration.");
            ValidateRegistrationOwnership(entity);
            ValidateAndAdd(entity, entitiesById, registeredEntities, entitiesByPersistentIdentity);
            entity.MarkRegistered(this);
        }

        public bool Unregister(WorldEntity entity)
        {
            if (entity == null || !entitiesById.TryGetValue(entity.EntityId, out WorldEntity registered) ||
                registered != entity) return false;
            return RemoveRegisteredEntity(entity);
        }

        public bool Unregister(string entityId)
        {
            if (string.IsNullOrWhiteSpace(entityId) || !entitiesById.TryGetValue(entityId, out WorldEntity entity))
                return false;
            return RemoveRegisteredEntity(entity);
        }

        private bool RemoveRegisteredEntity(WorldEntity entity)
        {
            string entityId = entity.EntityId;
            entitiesById.Remove(entityId);
            registeredEntities.Remove(entity);
            if (entity.TryGetPersistentIdentityKey(out PersistentIdentityKey key))
                entitiesByPersistentIdentity.Remove(key);
            retiredEntityIds.Add(entityId);
            entity.MarkUnregistered(this);
            EntityUnregistered?.Invoke(entity);
            return true;
        }

        [ContextMenu("重建并验证实体注册表")]
        public void RebuildRegistry()
        {
            if (entities == null)
            {
                entities = new List<WorldEntity>();
            }

            List<WorldEntity> candidates = new List<WorldEntity>(entities);
            if (autoDiscoverSceneEntities)
            {
                foreach (WorldEntity discovered in FindObjectsByType<WorldEntity>())
                {
                    if (discovered != null && !candidates.Contains(discovered))
                    {
                        candidates.Add(discovered);
                    }
                }
            }

            Dictionary<string, WorldEntity> rebuiltById = new(StringComparer.Ordinal);
            List<WorldEntity> rebuiltEntities = new();
            Dictionary<PersistentIdentityKey, WorldEntity> rebuiltByPersistentIdentity = new();
            bool isValid = true;
            foreach (WorldEntity entity in candidates)
            {
                try
                {
                    ValidateRegistrationOwnership(entity);
                    if (entitiesById.TryGetValue(entity.EntityId, out WorldEntity previous) &&
                        !ReferenceEquals(previous, entity))
                        throw new InvalidOperationException($"EntityId '{entity.EntityId}' belongs to another runtime instance.");
                    ValidateAndAdd(entity, rebuiltById, rebuiltEntities, rebuiltByPersistentIdentity);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    isValid = false;
                    Debug.LogError($"[EntityRegistry] {exception.Message}", entity != null ? entity : this);
                }
            }

            if (isValid)
                foreach (WorldEntity previous in registeredEntities)
                    if (previous != null && !rebuiltEntities.Contains(previous))
                        retiredEntityIds.Add(previous.EntityId);
            ClearRegistry();
            if (!isValid)
            {
                IsReady = false;
                return;
            }

            foreach (KeyValuePair<string, WorldEntity> pair in rebuiltById) entitiesById.Add(pair.Key, pair.Value);
            registeredEntities.AddRange(rebuiltEntities);
            foreach (KeyValuePair<PersistentIdentityKey, WorldEntity> pair in rebuiltByPersistentIdentity)
                entitiesByPersistentIdentity.Add(pair.Key, pair.Value);
            foreach (WorldEntity entity in registeredEntities) entity.MarkRegistered(this);
            IsReady = true;
            Debug.Log($"[EntityRegistry] 注册表已验证：{registeredEntities.Count} 个实体。", this);
        }

        [ContextMenu("打印实体注册表")]
        public void LogRegistrations()
        {
            if (!IsReady)
            {
                Debug.LogError("[EntityRegistry] 注册表未通过验证，不能作为世界查询入口。", this);
                return;
            }

            foreach (WorldEntity entity in registeredEntities)
            {
                Debug.Log($"[EntityRegistry] {entity.EntityId} → {entity.name} ({entity.EntityKind}/{entity.AgentRole})", entity);
            }
        }

        private static void ValidateAndAdd(WorldEntity entity,
            IDictionary<string, WorldEntity> targetById, ICollection<WorldEntity> targetEntities,
            IDictionary<PersistentIdentityKey, WorldEntity> targetByPersistentIdentity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (!entity.TryValidateConfiguration(out string error))
                throw new ArgumentException($"WorldEntity '{entity.name}' is invalid: {error}", nameof(entity));
            if (targetById.TryGetValue(entity.EntityId, out WorldEntity existing))
                throw new InvalidOperationException(
                    $"Duplicate EntityId '{entity.EntityId}' on '{entity.name}'; already registered by '{existing.name}'.");
            if (entity.TryGetPersistentIdentityKey(out PersistentIdentityKey persistentKey) &&
                targetByPersistentIdentity.TryGetValue(persistentKey, out WorldEntity persistentExisting))
                throw new InvalidOperationException(
                    $"Duplicate PersistentIdentity '{persistentKey}' on '{entity.name}'; " +
                    $"already registered by '{persistentExisting.name}'.");

            targetById.Add(entity.EntityId, entity);
            targetEntities.Add(entity);
            if (entity.TryGetPersistentIdentityKey(out persistentKey))
                targetByPersistentIdentity.Add(persistentKey, entity);
        }

        internal bool IsRegistered(WorldEntity entity) => entity != null &&
            IsReady && entitiesById.TryGetValue(entity.EntityId, out WorldEntity registered) &&
            ReferenceEquals(registered, entity);

        private void ValidateRegistrationOwnership(WorldEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            entity.EnsureRegistrationAvailable(this);
            if (retiredEntityIds.Contains(entity.EntityId))
                throw new InvalidOperationException($"EntityId '{entity.EntityId}' was retired in this runtime.");
        }

        private void ClearRegistry()
        {
            foreach (WorldEntity entity in registeredEntities)
                if (entity != null) entity.MarkUnregistered(this);
            entitiesById.Clear();
            registeredEntities.Clear();
            entitiesByPersistentIdentity.Clear();
        }
    }
}
