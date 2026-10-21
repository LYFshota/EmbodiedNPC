using System;

namespace EmbodiedNPC.Identity
{
    /// <summary>Authoritative current profile for one host-owned player identity.</summary>
    public sealed class PlayerIdentitySnapshot
    {
        private PlayerIdentitySnapshot(string playerUid, string displayName, long profileRevision,
            DateTimeOffset updatedAt)
        {
            PlayerUid = playerUid;
            DisplayName = displayName;
            ProfileRevision = profileRevision;
            UpdatedAt = updatedAt;
        }

        public string PlayerUid { get; }
        public string DisplayName { get; }
        public long ProfileRevision { get; }
        public DateTimeOffset UpdatedAt { get; }

        public static bool TryCreate(string playerUid, string displayName, long profileRevision,
            DateTimeOffset updatedAt, out PlayerIdentitySnapshot snapshot, out string error)
        {
            if (string.IsNullOrWhiteSpace(playerUid))
            {
                snapshot = null;
                error = "PlayerUid must not be empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                snapshot = null;
                error = "DisplayName must not be empty.";
                return false;
            }
            if (profileRevision < 0)
            {
                snapshot = null;
                error = "ProfileRevision must not be negative.";
                return false;
            }

            snapshot = new PlayerIdentitySnapshot(playerUid, displayName, profileRevision, updatedAt);
            error = string.Empty;
            return true;
        }
    }
}
