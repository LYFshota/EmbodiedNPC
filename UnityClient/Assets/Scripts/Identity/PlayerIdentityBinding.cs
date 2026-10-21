using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Identity
{
    /// <summary>Binds a host player UID before its WorldEntity enters the active registry.</summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    public sealed class PlayerIdentityBinding : MonoBehaviour
    {
        public const string IdentityNamespace = "player";
        public const string IdentityType = "Player";

        [SerializeField] private WorldEntity playerEntity;
        [SerializeField] private MonoBehaviour providerComponent;
        [SerializeField] private EntityRegistry registry;

        public PlayerIdentitySnapshot CurrentIdentity { get; private set; }

        private void Awake()
        {
            if (!TryApplyIdentity(out string error))
                Debug.LogError($"[PlayerIdentityBinding] {error}", this);
        }

        public void Configure(WorldEntity entity, MonoBehaviour provider, EntityRegistry entityRegistry)
        {
            playerEntity = entity;
            providerComponent = provider;
            registry = entityRegistry;
        }

        /// <summary>
        /// Applies a UID before registration. A registered entity may refresh profile fields only while its UID is unchanged.
        /// </summary>
        public bool TryApplyIdentity(out string error)
        {
            if (playerEntity == null)
            {
                error = "Player WorldEntity is required.";
                return false;
            }
            if (!playerEntity.IsAgent(AgentRole.Player))
            {
                error = $"WorldEntity '{playerEntity.name}' must be Agent/Player.";
                return false;
            }
            if (registry == null)
            {
                error = "EntityRegistry is required to protect the persistent identity index lifecycle.";
                return false;
            }
            if (providerComponent is not IPlayerIdentityProvider provider)
            {
                error = "Provider component must implement IPlayerIdentityProvider.";
                return false;
            }
            if (!provider.TryGetCurrentIdentity(out PlayerIdentitySnapshot snapshot) || snapshot == null)
            {
                error = "Player identity provider did not return a valid identity.";
                return false;
            }

            PersistentIdentityRef identity = new(IdentityNamespace, IdentityType, snapshot.PlayerUid);
            if (!identity.TryGetKey(out PersistentIdentityKey requestedKey))
            {
                error = "Player identity provider returned an invalid PlayerUid.";
                return false;
            }

            bool alreadyBound = playerEntity.TryGetPersistentIdentityKey(out PersistentIdentityKey currentKey);
            if (registry.IsRegistered(playerEntity) && (!alreadyBound || currentKey != requestedKey))
            {
                error = "Registered player identity cannot change in place; unregister before rebinding PlayerUid.";
                return false;
            }

            if (!alreadyBound || currentKey != requestedKey)
                playerEntity.SetPersistentIdentityForRegistration(identity);
            CurrentIdentity = snapshot;
            error = string.Empty;
            return true;
        }

        private void OnValidate()
        {
            if (playerEntity == null) playerEntity = GetComponent<WorldEntity>();
        }
    }
}
