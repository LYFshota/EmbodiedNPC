using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    /// <summary>One authored spare counter surface; contains no mutable cleanliness authority.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(WorldEntity))]
    public sealed class CounterCleaningStation : MonoBehaviour
    {
        [SerializeField] private Transform approachPoint;
        [SerializeField] private Transform operationPoint;
        [SerializeField] private WorldStateService world;
        [SerializeField] private EntityRegistry registry;
        public WorldEntity Entity => GetComponent<WorldEntity>();
        public Transform ApproachPoint => approachPoint;
        public Transform OperationPoint => operationPoint;
        public WorldStateService World => world;
        public bool IsAvailable => isActiveAndEnabled && Entity != null && Entity.isActiveAndEnabled &&
            Entity.EntityKind == EntityKind.Environment && approachPoint != null && operationPoint != null &&
            approachPoint.gameObject.activeInHierarchy && operationPoint.gameObject.activeInHierarchy && world != null && world.isActiveAndEnabled;
        public void Configure(Transform approach, Transform operation, WorldStateService authority, EntityRegistry entities)
        { approachPoint = approach; operationPoint = operation; world = authority; registry = entities; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [ContextMenu("Debug/Mark NeedsCleaning (controlled spill)")]
        private void DebugSpill()
        {
            if (Application.isPlaying && IsAvailable)
                world.TryMarkCounterNeedsCleaning(registry, this, "developer_controlled_spill");
        }
#endif
    }
}
