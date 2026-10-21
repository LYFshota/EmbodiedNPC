using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Interaction
{
    /// <summary>A scene-authored, authoritative destination for deterministic item placement.</summary>
    [RequireComponent(typeof(WorldEntity))]
    public sealed class SafeDropZone : MonoBehaviour
    {
        [SerializeField] private WorldEntity zone;
        [SerializeField] private Transform approachPoint;
        [SerializeField] private Transform placementPoint;
        [SerializeField] private bool canPlace = true;

        private string occupyingItemId = string.Empty;

        public WorldEntity Zone => zone;
        public Transform ApproachPoint => approachPoint;
        public Transform PlacementPoint => placementPoint;
        public bool IsConfigured => zone != null && zone.gameObject == gameObject &&
                                    zone.EntityKind == EntityKind.Environment &&
                                    approachPoint != null && placementPoint != null;
        public bool CanPlace => IsConfigured && canPlace && string.IsNullOrEmpty(occupyingItemId);
        public string OccupyingItemId => occupyingItemId;

        private void Awake()
        {
            if (zone == null)
            {
                zone = GetComponent<WorldEntity>();
            }
        }

        internal bool TryOccupy(string itemEntityId)
        {
            if (!CanPlace || string.IsNullOrWhiteSpace(itemEntityId))
            {
                return false;
            }

            occupyingItemId = itemEntityId;
            return true;
        }

        internal bool TryReleaseOccupancy(string expectedItemEntityId)
        {
            if (!string.Equals(occupyingItemId, expectedItemEntityId, System.StringComparison.Ordinal))
            {
                return false;
            }

            occupyingItemId = string.Empty;
            return true;
        }

        internal void RestoreOccupancy(string itemEntityId)
        {
            occupyingItemId = itemEntityId ?? string.Empty;
        }
    }
}
