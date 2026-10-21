using UnityEngine;

namespace EmbodiedNPC.Interaction
{
    /// <summary>
    /// Runtime ownership state for an item that can be carried by a world entity.
    /// </summary>
    public sealed class HeldItemState : MonoBehaviour
    {
        [SerializeField] private string holderEntityId = string.Empty;

        public string HolderEntityId => holderEntityId;
        public bool IsHeld => !string.IsNullOrEmpty(holderEntityId);

        internal bool TrySetHolder(string entityId)
        {
            if (IsHeld || string.IsNullOrWhiteSpace(entityId))
            {
                return false;
            }

            holderEntityId = entityId;
            return true;
        }

        internal bool TryTransferHolder(string expectedHolderEntityId, string nextHolderEntityId)
        {
            if (holderEntityId != expectedHolderEntityId || string.IsNullOrWhiteSpace(nextHolderEntityId))
            {
                return false;
            }

            holderEntityId = nextHolderEntityId;
            return true;
        }

        internal bool TryReleaseHolder(string expectedHolderEntityId)
        {
            if (string.IsNullOrWhiteSpace(expectedHolderEntityId) ||
                !string.Equals(holderEntityId, expectedHolderEntityId, System.StringComparison.Ordinal))
            {
                return false;
            }

            holderEntityId = string.Empty;
            return true;
        }

        internal void RestoreHolder(string entityId)
        {
            holderEntityId = entityId ?? string.Empty;
        }
    }
}
