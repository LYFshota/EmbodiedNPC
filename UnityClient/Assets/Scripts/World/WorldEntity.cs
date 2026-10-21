using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace EmbodiedNPC.World
{
    /// <summary>Identity and coarse semantic classification for one active world-runtime instance.</summary>
    public sealed class WorldEntity : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Unique ID within the current active runtime. Legacy readable IDs remain supported.")]
        private string entityId;

        [SerializeField]
        [Tooltip("Reusable content/template identity. Multiple runtime entities may share this value.")]
        private string definitionId;

        [SerializeField]
        [Tooltip("Coarse runtime kind. Player and NPC are Agent roles, not root kinds.")]
        private EntityKind kind = EntityKind.Object;

        [SerializeField]
        [Tooltip("Used only when Kind is Agent; non-Agent entities must use None.")]
        private AgentRole agentRole = AgentRole.None;

        [SerializeField]
        [Tooltip("Optional long-lived identity. It is not projected to perception/LLM context.")]
        private PersistentIdentityRef persistentIdentity;

        // v1 serialized Npc=0, Player=1, Item=2, Environment=3 into "entityKind".
        // Capture it separately so those integers can never be reinterpreted as the v2 enum.
        [SerializeField, HideInInspector, FormerlySerializedAs("entityKind")]
        private LegacyEntityKind legacyEntityKind = LegacyEntityKind.Unspecified;

        private EntityRegistry registrationOwner;

        public string EntityId => entityId;
        public bool HasExplicitDefinitionId => !string.IsNullOrWhiteSpace(definitionId);
        public bool UsesLegacyDefinitionFallback => !HasExplicitDefinitionId && HasLegacyKind;
        // Only deserialized v1 entities may temporarily use their old EntityId as a compatibility definition.
        public string DefinitionId => HasExplicitDefinitionId ? definitionId :
            UsesLegacyDefinitionFallback ? entityId : string.Empty;
        public EntityKind EntityKind => HasLegacyKind ? MapLegacyKind(legacyEntityKind) : kind;
        public AgentRole AgentRole => HasLegacyKind ? MapLegacyRole(legacyEntityKind) : agentRole;
        public PersistentIdentityRef PersistentIdentity => persistentIdentity;
        public bool HasPersistentIdentity => persistentIdentity != null && !persistentIdentity.IsEmpty;
        public bool HasLegacyKind => legacyEntityKind != LegacyEntityKind.Unspecified;

        public bool IsAgent(AgentRole role) => EntityKind == EntityKind.Agent && AgentRole == role;
        public bool TryGetPersistentIdentityKey(out PersistentIdentityKey key) =>
            PersistentIdentityKey.TryCreate(persistentIdentity, out key);

        public bool TryValidateConfiguration(out string error)
        {
            if (string.IsNullOrWhiteSpace(EntityId))
            {
                error = "EntityId must not be empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(DefinitionId))
            {
                error = "DefinitionId must not be empty.";
                return false;
            }
            if (EntityKind != EntityKind.Agent && AgentRole != AgentRole.None)
            {
                error = $"{EntityKind} cannot use agent role {AgentRole}.";
                return false;
            }
            if (persistentIdentity != null && !persistentIdentity.TryValidate(out error)) return false;
            error = string.Empty;
            return true;
        }

        /// <summary>Converts a deserialized v1 enum value into explicit v2 fields.</summary>
        public bool MigrateLegacyIdentity()
        {
            if (!HasLegacyKind) return false;
            EnsureNotRegistered();
            kind = MapLegacyKind(legacyEntityKind);
            agentRole = MapLegacyRole(legacyEntityKind);
            if (string.IsNullOrWhiteSpace(definitionId)) definitionId = entityId;
            legacyEntityKind = LegacyEntityKind.Unspecified;
            return true;
        }

        public void Configure(string runtimeEntityId, string contentDefinitionId, EntityKind entityKind,
            AgentRole role = AgentRole.None, PersistentIdentityRef identity = null)
        {
            EnsureNotRegistered();
            entityId = runtimeEntityId;
            definitionId = contentDefinitionId;
            kind = entityKind;
            agentRole = role;
            persistentIdentity = identity;
            legacyEntityKind = LegacyEntityKind.Unspecified;
        }

        /// <summary>Used by identity bindings before registration; registered entities must be unregistered first.</summary>
        internal void SetPersistentIdentityForRegistration(PersistentIdentityRef identity)
        {
            EnsureNotRegistered();
            persistentIdentity = identity;
        }

        internal void EnsureRegistrationAvailable(EntityRegistry owner)
        {
            if (registrationOwner != null && !ReferenceEquals(registrationOwner, owner))
                throw new InvalidOperationException($"WorldEntity '{name}' is already registered elsewhere.");
        }

        internal void MarkRegistered(EntityRegistry owner) => registrationOwner = owner;

        internal void MarkUnregistered(EntityRegistry owner)
        {
            if (ReferenceEquals(registrationOwner, owner)) registrationOwner = null;
        }

        private void EnsureNotRegistered()
        {
            if (registrationOwner != null)
                throw new InvalidOperationException($"WorldEntity '{name}' must be unregistered before changing identity.");
        }

        private static EntityKind MapLegacyKind(LegacyEntityKind legacy) => legacy switch
        {
            LegacyEntityKind.Npc => EntityKind.Agent,
            LegacyEntityKind.Player => EntityKind.Agent,
            LegacyEntityKind.Item => EntityKind.Object,
            LegacyEntityKind.Environment => EntityKind.Environment,
            _ => EntityKind.Object,
        };

        private static AgentRole MapLegacyRole(LegacyEntityKind legacy) => legacy switch
        {
            LegacyEntityKind.Npc => AgentRole.Npc,
            LegacyEntityKind.Player => AgentRole.Player,
            _ => AgentRole.None,
        };

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(entityId) && string.IsNullOrWhiteSpace(definitionId) && !HasLegacyKind)
                return;
            if (!TryValidateConfiguration(out string error))
                Debug.LogWarning($"[WorldEntity] Invalid configuration on '{name}': {error}", this);
        }
    }

    public enum EntityKind
    {
        Agent,
        Object,
        Environment,
        Area,
    }

    public enum AgentRole
    {
        None,
        Player,
        Npc,
        Companion,
        Enemy,
    }

    [Serializable]
    public sealed class PersistentIdentityRef : IEquatable<PersistentIdentityRef>
    {
        [SerializeField] private string identityNamespace;
        [SerializeField] private string identityType;
        [SerializeField] private string persistentId;

        public PersistentIdentityRef(string identityNamespace, string identityType, string persistentId)
        {
            this.identityNamespace = identityNamespace;
            this.identityType = identityType;
            this.persistentId = persistentId;
        }

        public string Namespace => identityNamespace;
        public string IdentityType => identityType;
        public string PersistentId => persistentId;
        public bool IsEmpty => string.IsNullOrWhiteSpace(identityNamespace) &&
                               string.IsNullOrWhiteSpace(identityType) &&
                               string.IsNullOrWhiteSpace(persistentId);

        public bool TryValidate(out string error)
        {
            if (IsEmpty)
            {
                error = string.Empty;
                return true;
            }
            if (string.IsNullOrWhiteSpace(identityNamespace) || string.IsNullOrWhiteSpace(identityType) ||
                string.IsNullOrWhiteSpace(persistentId))
            {
                error = "PersistentIdentityRef must be either empty or fully specified.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public bool Equals(PersistentIdentityRef other) => other != null &&
            string.Equals(identityNamespace ?? string.Empty, other.identityNamespace ?? string.Empty,
                StringComparison.Ordinal) &&
            string.Equals(identityType ?? string.Empty, other.identityType ?? string.Empty,
                StringComparison.Ordinal) &&
            string.Equals(persistentId ?? string.Empty, other.persistentId ?? string.Empty,
                StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is PersistentIdentityRef other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(identityNamespace ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(identityType ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(persistentId ?? string.Empty);
                return hash;
            }
        }

        public override string ToString() => TryGetKey(out PersistentIdentityKey key) ? key.ToString() :
            IsEmpty ? "<none>" : "<invalid>";

        public bool TryGetKey(out PersistentIdentityKey key) => PersistentIdentityKey.TryCreate(this, out key);
    }

    /// <summary>Ordinal, provider-canonicalized identity value used for runtime uniqueness checks.</summary>
    public readonly struct PersistentIdentityKey : IEquatable<PersistentIdentityKey>
    {
        private PersistentIdentityKey(string identityNamespace, string identityType, string persistentId)
        {
            Namespace = identityNamespace;
            IdentityType = identityType;
            PersistentId = persistentId;
        }

        public string Namespace { get; }
        public string IdentityType { get; }
        public string PersistentId { get; }
        public bool IsValid => !string.IsNullOrWhiteSpace(Namespace) &&
                               !string.IsNullOrWhiteSpace(IdentityType) &&
                               !string.IsNullOrWhiteSpace(PersistentId);

        public static bool TryCreate(PersistentIdentityRef identity, out PersistentIdentityKey key)
        {
            if (identity == null || !identity.TryValidate(out _) || identity.IsEmpty)
            {
                key = default;
                return false;
            }

            key = new PersistentIdentityKey(identity.Namespace, identity.IdentityType, identity.PersistentId);
            return true;
        }

        public bool Equals(PersistentIdentityKey other) =>
            string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) &&
            string.Equals(IdentityType, other.IdentityType, StringComparison.Ordinal) &&
            string.Equals(PersistentId, other.PersistentId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is PersistentIdentityKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(Namespace ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(IdentityType ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(PersistentId ?? string.Empty);
                return hash;
            }
        }

        public override string ToString() => IsValid ? $"{Namespace}:{IdentityType}:{PersistentId}" : "<invalid>";

        public static bool operator ==(PersistentIdentityKey left, PersistentIdentityKey right) => left.Equals(right);
        public static bool operator !=(PersistentIdentityKey left, PersistentIdentityKey right) => !left.Equals(right);
    }

    // Values intentionally mirror the v1 serialized integer layout. Do not reorder.
    internal enum LegacyEntityKind
    {
        Unspecified = -1,
        Npc = 0,
        Player = 1,
        Item = 2,
        Environment,
    }
}
