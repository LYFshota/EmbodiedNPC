using System;
using EmbodiedNPC.Identity;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Inspector-configured local identity source for CafeDemo only.</summary>
    [DisallowMultipleComponent]
    public sealed class LocalTestIdentityProvider : MonoBehaviour, IPlayerIdentityProvider
    {
        [SerializeField] private string playerUid = "cafe-local-player-001";
        [SerializeField] private string displayName = "Player";
        [SerializeField] private long profileRevision = 1;
        [SerializeField] private long updatedAtUnixMilliseconds;

        public bool TryGetCurrentIdentity(out PlayerIdentitySnapshot snapshot)
        {
            DateTimeOffset updatedAt;
            try
            {
                updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, updatedAtUnixMilliseconds));
            }
            catch (ArgumentOutOfRangeException)
            {
                snapshot = null;
                return false;
            }

            return PlayerIdentitySnapshot.TryCreate(playerUid, displayName, profileRevision,
                updatedAt, out snapshot, out _);
        }

        public void Configure(string uid, string currentDisplayName, long revision, DateTimeOffset updatedAt)
        {
            playerUid = uid;
            displayName = currentDisplayName;
            profileRevision = revision;
            updatedAtUnixMilliseconds = Math.Max(0, updatedAt.ToUnixTimeMilliseconds());
        }

        public bool TrySetDisplayName(string currentDisplayName, long revision, DateTimeOffset updatedAt)
        {
            if (string.IsNullOrWhiteSpace(currentDisplayName) || revision < 0) return false;
            displayName = currentDisplayName;
            profileRevision = revision;
            updatedAtUnixMilliseconds = Math.Max(0, updatedAt.ToUnixTimeMilliseconds());
            return true;
        }
    }
}
