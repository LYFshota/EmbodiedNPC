using System;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Settings;
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

        private bool useSavedProfile = true;
        private void OnEnable()
        {
            PlayerNameSettings.Changed += RefreshSavedProfile;
            if (useSavedProfile && PlayerNameSettings.Current.Revision > 1 && GetComponent<PlayerIdentityBinding>()?.CurrentIdentity != null)
                RefreshSavedProfile();
        }
        private void OnDisable() => PlayerNameSettings.Changed -= RefreshSavedProfile;
        private void RefreshSavedProfile()
        {
            var saved = PlayerNameSettings.Current;
            if (displayName != saved.DisplayName && profileRevision < long.MaxValue)
                profileRevision = Math.Max(profileRevision + 1, saved.Revision);
            useSavedProfile = true;
            var binding = GetComponent<PlayerIdentityBinding>();
            if (binding != null && !binding.TryApplyIdentity(out string error))
                Debug.LogError("[LocalTestIdentityProvider] Profile refresh failed: " + error, this);
        }

        public bool TryGetCurrentIdentity(out PlayerIdentitySnapshot snapshot)
        {
            if (useSavedProfile)
            {
                var saved = PlayerNameSettings.Current;
                // Missing preference retains the scene's existing effective default.
                if (saved.Revision > 1)
                {
                    displayName = saved.DisplayName;
                    profileRevision = Math.Max(profileRevision, saved.Revision);
                    updatedAtUnixMilliseconds = saved.UpdatedAtMs;
                }
            }
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
            useSavedProfile = false;
            playerUid = uid;
            displayName = currentDisplayName;
            profileRevision = revision;
            updatedAtUnixMilliseconds = Math.Max(0, updatedAt.ToUnixTimeMilliseconds());
        }

        public bool TrySetDisplayName(string currentDisplayName, long revision, DateTimeOffset updatedAt)
        {
            if (!PlayerNameSettings.TryValidate(currentDisplayName, out currentDisplayName) || revision < 0) return false;
            useSavedProfile = false;
            displayName = currentDisplayName;
            profileRevision = revision;
            updatedAtUnixMilliseconds = Math.Max(0, updatedAt.ToUnixTimeMilliseconds());
            return true;
        }
    }
}
