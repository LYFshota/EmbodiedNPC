using UnityEngine;

namespace EmbodiedNPC.World
{
    /// <summary>Trigger bridge that updates primary Area membership for registered WorldEntities.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    [RequireComponent(typeof(Collider))]
    public sealed class AreaVolume : MonoBehaviour
    {
        [SerializeField] private WorldEntity areaEntity;
        [SerializeField] private Collider triggerCollider;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private AreaMembershipService membershipService;

        private void Awake()
        {
            if (!TryValidateConfiguration(out string error))
                Debug.LogError($"[AreaVolume] {error}", this);
        }

        public void Configure(WorldEntity semanticArea, Collider volumeCollider, EntityRegistry entityRegistry,
            AreaMembershipService areaMembershipService)
        {
            areaEntity = semanticArea;
            triggerCollider = volumeCollider;
            registry = entityRegistry;
            membershipService = areaMembershipService;
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (areaEntity == null || areaEntity.EntityKind != EntityKind.Area)
            {
                error = "AreaVolume requires a WorldEntity with EntityKind.Area.";
                return false;
            }
            if (triggerCollider == null || !triggerCollider.isTrigger)
            {
                error = "AreaVolume requires a trigger Collider.";
                return false;
            }
            if (registry == null || membershipService == null)
            {
                error = "AreaVolume requires EntityRegistry and AreaMembershipService references.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void OnTriggerEnter(Collider other)
        {
            WorldEntity enteringEntity = other != null ? other.GetComponentInParent<WorldEntity>() : null;
            if (enteringEntity == null || enteringEntity == areaEntity) return;
            membershipService.TryEnterArea(enteringEntity, areaEntity, out _);
        }

        private void OnTriggerExit(Collider other)
        {
            WorldEntity leavingEntity = other != null ? other.GetComponentInParent<WorldEntity>() : null;
            if (leavingEntity == null || leavingEntity == areaEntity) return;
            membershipService.LeaveArea(leavingEntity, areaEntity);
        }

        private void OnValidate()
        {
            if (areaEntity == null) areaEntity = GetComponent<WorldEntity>();
            if (triggerCollider == null) triggerCollider = GetComponent<Collider>();
        }
    }
}
