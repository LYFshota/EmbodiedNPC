using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests
{
    public sealed class EntityFoundationTests
    {
        private readonly List<GameObject> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in objects)
                if (value != null) UnityEngine.Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void SameDefinitionId_WithDifferentEntityIds_IsAllowed()
        {
            EntityRegistry registry = ReadyRegistry();
            WorldEntity first = Entity("cup_01", "cafe.cup.ceramic", EntityKind.Object);
            WorldEntity second = Entity("cup_02", "cafe.cup.ceramic", EntityKind.Object);

            registry.Register(first);
            registry.Register(second);

            Assert.That(registry.RegisteredEntities, Has.Count.EqualTo(2));
            Assert.That(first.DefinitionId, Is.EqualTo(second.DefinitionId));
        }

        [Test]
        public void DuplicateEntityId_IsRejectedWithoutInvalidatingHealthyRegistry()
        {
            WorldEntity original = Entity("cup_01", "cup.a", EntityKind.Object);
            EntityRegistry registry = ReadyRegistry(original);
            WorldEntity candidate = Entity("cup_01", "cup.b", EntityKind.Object);

            Assert.Throws<InvalidOperationException>(() =>
                registry.Register(candidate));
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(original));
            Assert.DoesNotThrow(() => candidate.Configure("cup_02", "cup.b", EntityKind.Object));
            registry.Register(candidate);
            Assert.That(registry.TryGetEntity("cup_02", out found), Is.True);
            Assert.That(found, Is.SameAs(candidate));
        }

        [Test]
        public void RegisteredEntity_InternalIdentitySetterCannotBypassFreeze()
        {
            WorldEntity entity = Entity("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc);
            EntityRegistry registry = ReadyRegistry(entity);
            MethodInfo setter = typeof(WorldEntity).GetMethod("SetPersistentIdentityForRegistration",
                BindingFlags.Instance | BindingFlags.NonPublic);

            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                setter.Invoke(entity, new object[] { new PersistentIdentityRef("npc", "Agent", "changed") }));

            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(entity.HasPersistentIdentity, Is.False);
            Assert.That(registry.TryGetEntity("npc_01", out _), Is.True);
        }

        [Test]
        public void RegisteredLegacyIdentity_CannotMigrateInPlace()
        {
            WorldEntity entity = Component<WorldEntity>("LegacyNpc");
            SetField(entity, "entityId", "npc_legacy");
            FieldInfo legacyField = typeof(WorldEntity).GetField("legacyEntityKind",
                BindingFlags.Instance | BindingFlags.NonPublic);
            legacyField.SetValue(entity, Enum.ToObject(legacyField.FieldType, 0));
            EntityRegistry registry = ReadyRegistry(entity);

            Assert.Throws<InvalidOperationException>(() => entity.MigrateLegacyIdentity());

            Assert.That(entity.HasLegacyKind, Is.True);
            Assert.That(registry.TryGetEntity("npc_legacy", out _), Is.True);
        }

        [Test]
        public void FailedRebuildReleasesMarkersAndSuccessfulRebuildRestoresThem()
        {
            WorldEntity original = Entity("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc);
            EntityRegistry registry = ReadyRegistry(original);
            WorldEntity invalid = Entity("broken", string.Empty, EntityKind.Object);
            SetField(registry, "entities", new List<WorldEntity> { original, invalid });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("WorldEntity 'broken' is invalid"));

            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            WorldEntity candidate = Entity("new_01", "character.npc", EntityKind.Agent, AgentRole.Npc);
            Assert.Throws<InvalidOperationException>(() => registry.Register(candidate));
            Assert.DoesNotThrow(() => candidate.Configure("new_02", "character.npc", EntityKind.Agent, AgentRole.Npc));
            Assert.DoesNotThrow(() => original.Configure("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc));
            SetField(registry, "entities", new List<WorldEntity> { original });
            registry.RebuildRegistry();
            Assert.That(registry.IsReady, Is.True);
            registry.RebuildRegistry();
            Assert.That(registry.TryGetEntity("npc_01", out _), Is.True);
            Assert.Throws<InvalidOperationException>(() => original.Configure("changed", "character.npc",
                EntityKind.Agent, AgentRole.Npc));
        }

        [Test]
        public void RegisteredEntity_ConfigureCannotDriftIdentityIndexes()
        {
            var oldIdentity = new PersistentIdentityRef("npc", "Agent", "original");
            WorldEntity entity = Entity("npc_original", "character.npc", EntityKind.Agent,
                AgentRole.Npc, oldIdentity);
            EntityRegistry registry = ReadyRegistry(entity);
            Assert.That(oldIdentity.TryGetKey(out PersistentIdentityKey oldKey), Is.True);

            Assert.Throws<InvalidOperationException>(() => entity.Configure("npc_changed", "character.changed",
                EntityKind.Agent, AgentRole.Npc, new PersistentIdentityRef("npc", "Agent", "changed")));

            Assert.That(entity.EntityId, Is.EqualTo("npc_original"));
            Assert.That(entity.DefinitionId, Is.EqualTo("character.npc"));
            Assert.That(registry.TryGetEntity("npc_original", out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(entity));
            Assert.That(registry.TryGetEntity("npc_changed", out _), Is.False);
            Assert.That(registry.TryResolvePersistentIdentity(oldKey, out found), Is.True);
            Assert.That(found, Is.SameAs(entity));
        }

        [Test]
        public void DestroyedRegisteredEntity_LookupsFailClosed()
        {
            var identity = new PersistentIdentityRef("npc", "Agent", "destroyed");
            WorldEntity entity = Entity("npc_destroyed", "character.npc", EntityKind.Agent,
                AgentRole.Npc, identity);
            EntityRegistry registry = ReadyRegistry(entity);
            Assert.That(identity.TryGetKey(out PersistentIdentityKey key), Is.True);
            UnityEngine.Object.DestroyImmediate(entity.gameObject);

            Assert.That(registry.TryGetEntity("npc_destroyed", out WorldEntity byId), Is.False);
            Assert.That(byId, Is.Null);
            Assert.That(registry.TryResolvePersistentIdentity(key, out WorldEntity byIdentity), Is.False);
            Assert.That(byIdentity, Is.Null);
        }

        [Test]
        public void RetiredEntityId_CannotBeReusedWithinOneRegistry()
        {
            WorldEntity original = Entity("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc);
            EntityRegistry registry = ReadyRegistry(original);
            Assert.That(registry.Unregister(original), Is.True);
            WorldEntity replacement = Entity("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc);

            Assert.Throws<InvalidOperationException>(() => registry.Register(replacement));
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryGetEntity("npc_01", out _), Is.False);
        }

        [Test]
        public void UnregisterImmediatelyRevokesVisionButKeepsSpatialHistory()
        {
            WorldEntity observer = Entity("npc_observer", "character.npc", EntityKind.Agent, AgentRole.Npc);
            WorldEntity target = Entity("target_01", "cafe.target", EntityKind.Object);
            target.transform.position = new Vector3(0f, 0f, 3f);
            PerceivableEntity perceivable = target.gameObject.AddComponent<PerceivableEntity>();
            SetField(perceivable, "entity", target);
            EntityRegistry registry = ReadyRegistry(observer);
            PerceivedStateStore store = observer.gameObject.AddComponent<PerceivedStateStore>();
            SetField(store, "observer", observer);
            typeof(PerceivedStateStore).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(store, null);
            VisionSensor vision = observer.gameObject.AddComponent<VisionSensor>();
            SetField(vision, "observer", observer);
            SetField(vision, "stateStore", store);
            SetField(vision, "entityRegistry", registry);
            typeof(VisionSensor).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(vision, null);

            vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds, Does.Not.Contain(target.EntityId));
            registry.Register(target);
            vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds, Does.Contain(target.EntityId));
            Assert.That(registry.Unregister(target), Is.True);

            Assert.That(vision.CurrentVisibleEntityIds, Does.Not.Contain(target.EntityId));
            Assert.That(store.Aggregator.Entities[target.EntityId].LifecycleState,
                Is.EqualTo(PerceptionLifecycleState.Lost));
            Assert.That(store.SpatialMemory.TryGet(target.EntityId, Time.unscaledTimeAsDouble, out _), Is.True);
            var projected = new ContextSnapshotBuilder().Build(store.Aggregator, string.Empty,
                Time.unscaledTimeAsDouble, spatialMemory: store.SpatialMemory).ToPerceptionDto().entities;
            Assert.That(projected.Single(value => value.entity_id == target.EntityId).visibility,
                Is.EqualTo("remembered"));
        }

        [Test]
        public void HistoricalSpatialRecord_CannotAuthorizeUnregisteredActionTarget()
        {
            WorldEntity actor = Entity("npc_01", "character.npc", EntityKind.Agent, AgentRole.Npc);
            WorldEntity target = Entity("target_01", "cafe.target", EntityKind.Object);
            EntityRegistry registry = ReadyRegistry(actor, target);
            SpatialWorkingMemory memory = new();
            memory.Observe(target.EntityId, new Vector3(0f, 0f, 3f), 1d, 0, null);
            Assert.That(registry.Unregister(target), Is.True);
            Assert.That(memory.TryGet(target.EntityId, 2d, out _), Is.True);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);

            ActionBindingResult result = move.Binder.Bind(new GoalProposal
            {
                type = "move_to_entity", target_entity_id = target.EntityId,
            }, move.Descriptor, new ActionAdmissionContext(registry, null, null, actor, null));

            Assert.That(result.IsBound, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetNotFound));
        }

        [TestCase(AgentRole.Player)]
        [TestCase(AgentRole.Npc)]
        public void Agent_WithSupportedRole_IsValid(AgentRole role)
        {
            WorldEntity entity = Entity("agent_01", "character.default", EntityKind.Agent, role);
            Assert.That(entity.TryValidateConfiguration(out string error), Is.True, error);
        }

        [Test]
        public void Object_WithPlayerRole_IsInvalid()
        {
            WorldEntity entity = Entity("cup_01", "cafe.cup", EntityKind.Object, AgentRole.Player);
            Assert.That(entity.TryValidateConfiguration(out string error), Is.False);
            StringAssert.Contains("cannot use agent role", error);

            EntityRegistry registry = ReadyRegistry();
            Assert.Throws<ArgumentException>(() => registry.Register(entity));
            Assert.That(registry.IsReady, Is.True);
        }

        [Test]
        public void PersistentIdentity_IsOptionalAndCompleteValueIsAccepted()
        {
            WorldEntity transient = Entity("cup_01", "cafe.cup", EntityKind.Object);
            WorldEntity persistent = Entity("player_01", "character.player.default", EntityKind.Agent,
                AgentRole.Player, new PersistentIdentityRef("player", "Player", "U10001"));

            Assert.That(transient.HasPersistentIdentity, Is.False);
            Assert.That(persistent.HasPersistentIdentity, Is.True);
            Assert.That(persistent.PersistentIdentity.PersistentId, Is.EqualTo("U10001"));
        }

        [Test]
        public void EmptyPersistentIdentity_IsEquivalentToNotConfiguredAndCannotProduceKey()
        {
            PersistentIdentityRef empty = new(null, string.Empty, "  ");
            WorldEntity entity = Entity("cup_01", "cafe.cup", EntityKind.Object,
                identity: empty);

            Assert.That(empty.IsEmpty, Is.True);
            Assert.That(empty.TryValidate(out string error), Is.True, error);
            Assert.That(empty.TryGetKey(out _), Is.False);
            Assert.That(entity.HasPersistentIdentity, Is.False);
        }

        [Test]
        public void PartialPersistentIdentity_IsInvalid()
        {
            PersistentIdentityRef partial = new("player", "Player", string.Empty);
            Assert.That(partial.IsEmpty, Is.False);
            Assert.That(partial.TryValidate(out string error), Is.False);
            Assert.That(partial.TryGetKey(out _), Is.False);
            StringAssert.Contains("fully specified", error);
        }

        [Test]
        public void IdenticalPersistentIdentityValues_AreEqualAndHaveStableKeyHash()
        {
            PersistentIdentityRef first = new("player", "Player", "U10001");
            PersistentIdentityRef second = new("player", "Player", "U10001");

            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
            Assert.That(first.TryGetKey(out PersistentIdentityKey firstKey), Is.True);
            Assert.That(second.TryGetKey(out PersistentIdentityKey secondKey), Is.True);
            Assert.That(firstKey, Is.EqualTo(secondKey));
            Assert.That(firstKey.GetHashCode(), Is.EqualTo(secondKey.GetHashCode()));
            Assert.That(firstKey.ToString(), Is.EqualTo("player:Player:U10001"));
        }

        [TestCase("other", "Player", "U10001")]
        [TestCase("player", "Npc", "U10001")]
        [TestCase("player", "Player", "u10001")]
        public void DifferentPersistentIdentityValues_AreNotEqual(string identityNamespace, string identityType,
            string persistentId)
        {
            PersistentIdentityRef baseline = new("player", "Player", "U10001");
            PersistentIdentityRef different = new(identityNamespace, identityType, persistentId);

            Assert.That(different, Is.Not.EqualTo(baseline));
            Assert.That(different.TryGetKey(out PersistentIdentityKey differentKey), Is.True);
            Assert.That(baseline.TryGetKey(out PersistentIdentityKey baselineKey), Is.True);
            Assert.That(differentKey, Is.Not.EqualTo(baselineKey));
        }

        [Test]
        public void DuplicatePersistentIdentity_IsRejectedWithoutInvalidatingHealthyRegistry()
        {
            PersistentIdentityRef identity = new("player", "Player", "U10001");
            WorldEntity original = Entity("player_01", "character.player", EntityKind.Agent,
                AgentRole.Player, identity);
            EntityRegistry registry = ReadyRegistry(original);

            Assert.Throws<InvalidOperationException>(() => registry.Register(
                Entity("player_02", "character.player", EntityKind.Agent, AgentRole.Player,
                    new PersistentIdentityRef("player", "Player", "U10001"))));

            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.RegisteredEntities, Has.Count.EqualTo(1));
        }

        [Test]
        public void RegisterAndUnregister_UpdateReadOnlyLookup()
        {
            EntityRegistry registry = ReadyRegistry();
            WorldEntity entity = Entity("cup_01", "cafe.cup", EntityKind.Object);

            registry.Register(entity);
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryGetEntity("cup_01", out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(entity));
            Assert.That(registry.Unregister(entity), Is.True);
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryGetEntity("cup_01", out _), Is.False);
            Assert.That(registry.RegisteredEntities, Is.Empty);
        }

        [Test]
        public void UnregisterMissingEntity_DoesNotCorruptHealthyRegistry()
        {
            WorldEntity registered = Entity("cup_01", "cafe.cup", EntityKind.Object);
            EntityRegistry registry = ReadyRegistry(registered);

            Assert.That(registry.Unregister("missing"), Is.False);
            Assert.That(registry.Unregister(Entity("cup_02", "cafe.cup", EntityKind.Object)), Is.False);
            Assert.That(registry.IsReady, Is.True);
            Assert.That(registry.TryGetEntity("cup_01", out _), Is.True);
        }

        [Test]
        public void RebuildRegistry_UsesSameConfigurationValidation()
        {
            EntityRegistry registry = Component<EntityRegistry>("Registry");
            WorldEntity invalid = Entity("cup_01", "cafe.cup", EntityKind.Object, AgentRole.Player);
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity> { invalid });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("WorldEntity 'cup_01' is invalid"));

            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            Assert.That(registry.TryGetEntity("cup_01", out _), Is.False);
        }

        [Test]
        public void FailedRebuild_ClearsPartialCandidateStateAndBlocksLookup()
        {
            EntityRegistry registry = ReadyRegistry(Entity("previous", "previous.definition", EntityKind.Object));
            WorldEntity valid = Entity("cup_01", "cafe.cup", EntityKind.Object);
            WorldEntity invalid = Entity("broken", "cafe.broken", EntityKind.Object, AgentRole.Player);
            SetField(registry, "entities", new List<WorldEntity> { valid, invalid });
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("WorldEntity 'broken' is invalid"));

            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            Assert.That(registry.RegisteredEntities, Is.Empty);
            Assert.That(registry.TryGetEntity("previous", out _), Is.False);
            Assert.That(registry.TryGetEntity("cup_01", out _), Is.False);
        }

        [Test]
        public void ExplicitDefinitionId_IsReportedWithoutFallback()
        {
            WorldEntity entity = Entity("cup_01", "cafe.cup.ceramic", EntityKind.Object);
            Assert.That(entity.HasExplicitDefinitionId, Is.True);
            Assert.That(entity.UsesLegacyDefinitionFallback, Is.False);
            Assert.That(entity.DefinitionId, Is.EqualTo("cafe.cup.ceramic"));
        }

        [Test]
        public void NewEntityWithoutDefinition_DoesNotReceiveLegacyFallback()
        {
            WorldEntity entity = Entity("cup_01", string.Empty, EntityKind.Object);
            Assert.That(entity.HasExplicitDefinitionId, Is.False);
            Assert.That(entity.UsesLegacyDefinitionFallback, Is.False);
            Assert.That(entity.DefinitionId, Is.Empty);
            Assert.That(entity.TryValidateConfiguration(out _), Is.False);
        }

        [TestCase(0, EntityKind.Agent, AgentRole.Npc)]
        [TestCase(1, EntityKind.Agent, AgentRole.Player)]
        [TestCase(2, EntityKind.Object, AgentRole.None)]
        [TestCase(3, EntityKind.Environment, AgentRole.None)]
        public void LegacySerializedEnumValue_IsMappedExplicitly(int legacyValue, EntityKind expectedKind,
            AgentRole expectedRole)
        {
            WorldEntity entity = Component<WorldEntity>("Legacy");
            SetField(entity, "entityId", "legacy_01");
            FieldInfo legacyField = typeof(WorldEntity).GetField("legacyEntityKind",
                BindingFlags.Instance | BindingFlags.NonPublic);
            legacyField.SetValue(entity, Enum.ToObject(legacyField.FieldType, legacyValue));

            Assert.That(entity.EntityKind, Is.EqualTo(expectedKind));
            Assert.That(entity.AgentRole, Is.EqualTo(expectedRole));
            Assert.That(entity.DefinitionId, Is.EqualTo("legacy_01"));
            Assert.That(entity.HasExplicitDefinitionId, Is.False);
            Assert.That(entity.UsesLegacyDefinitionFallback, Is.True);
            Assert.That(entity.MigrateLegacyIdentity(), Is.True);
            Assert.That(entity.HasLegacyKind, Is.False);
            Assert.That(entity.EntityKind, Is.EqualTo(expectedKind));
        }

        [Test]
        public void PerceivableEntity_ProjectsNewKindAndRoleWithoutPersistentIdentity()
        {
            GameObject owner = NewObject("NPC");
            WorldEntity entity = owner.AddComponent<WorldEntity>();
            entity.Configure("npc_01", "character.npc.default", EntityKind.Agent, AgentRole.Npc,
                new PersistentIdentityRef("npc", "Agent", "merchant_01"));
            PerceivableEntity perceivable = owner.AddComponent<PerceivableEntity>();
            SetField(perceivable, "entity", entity);
            Dictionary<string, string> fields = new();

            perceivable.WriteVisibleFields(fields);

            Assert.That(fields["entity_kind"], Is.EqualTo("Agent"));
            Assert.That(fields["agent_role"], Is.EqualTo("Npc"));
            Assert.That(fields.ContainsKey("persistent_id"), Is.False);
        }

        [Test]
        public void PerceivableEntity_DerivesPickUpFromInteractableWithoutAuthoredCapability()
        {
            PerceivableEntity perceivable = PickupPerceivable(true);

            Assert.That(VisibleCapabilities(perceivable), Is.EqualTo(new[] { "pick_up" }));
        }

        [Test]
        public void PerceivableEntity_DropsStaleAuthoredPickUpWhenInteractionDoesNotSupportIt()
        {
            PerceivableEntity perceivable = PickupPerceivable(false, "pick_up");

            Assert.That(VisibleCapabilities(perceivable), Is.Empty);
        }

        [Test]
        public void PerceivableEntity_PreservesOtherAuthoredCapabilities()
        {
            PerceivableEntity perceivable = PickupPerceivable(true, "custom_test");

            Assert.That(VisibleCapabilities(perceivable), Is.EqualTo(new[] { "custom_test", "pick_up" }));
        }

        [Test]
        public void PerceivableEntity_DoesNotDuplicateDerivedPickUp()
        {
            PerceivableEntity perceivable = PickupPerceivable(true, "pick_up");

            Assert.That(VisibleCapabilities(perceivable), Is.EqualTo(new[] { "pick_up" }));
        }

        private PerceivableEntity PickupPerceivable(bool supportsPickUp, params string[] authoredCapabilities)
        {
            WorldEntity entity = Entity("cup_01", "cafe.cup", EntityKind.Object);
            PerceivableEntity perceivable = entity.gameObject.AddComponent<PerceivableEntity>();
            InteractableEntity interactable = entity.gameObject.AddComponent<InteractableEntity>();
            SetField(perceivable, "entity", entity);
            SetField(perceivable, "publicCapabilities", authoredCapabilities);
            SetField(interactable, "supportsPickUp", supportsPickUp);
            return perceivable;
        }

        private static string[] VisibleCapabilities(PerceivableEntity perceivable)
        {
            Dictionary<string, string> fields = new();
            perceivable.WriteVisibleFields(fields);
            return fields.TryGetValue("capabilities", out string value)
                ? value.Split(',')
                : Array.Empty<string>();
        }

        private WorldEntity Entity(string id, string definition, EntityKind kind,
            AgentRole role = AgentRole.None, PersistentIdentityRef identity = null)
        {
            WorldEntity entity = Component<WorldEntity>(id);
            entity.Configure(id, definition, kind, role, identity);
            return entity;
        }

        private T Component<T>(string name) where T : Component => NewObject(name).AddComponent<T>();

        private EntityRegistry ReadyRegistry(params WorldEntity[] initialEntities)
        {
            EntityRegistry registry = Component<EntityRegistry>("Registry");
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity>(initialEntities));
            registry.RebuildRegistry();
            Assert.That(registry.IsReady, Is.True);
            return registry;
        }

        private GameObject NewObject(string name)
        {
            GameObject value = new(name);
            objects.Add(value);
            return value;
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
