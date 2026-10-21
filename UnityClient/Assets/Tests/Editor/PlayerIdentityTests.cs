using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests
{
    public sealed class StubPlayerIdentityProvider : MonoBehaviour, IPlayerIdentityProvider
    {
        private PlayerIdentitySnapshot snapshot;

        public void Configure(string uid, string displayName, long revision = 1)
        {
            Assert.That(PlayerIdentitySnapshot.TryCreate(uid, displayName, revision, DateTimeOffset.UnixEpoch,
                out snapshot, out string error), Is.True, error);
        }

        public void Invalidate() => snapshot = null;

        public bool TryGetCurrentIdentity(out PlayerIdentitySnapshot current)
        {
            current = snapshot;
            return current != null;
        }
    }

    public sealed class PlayerIdentityTests
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
        public void ValidSnapshot_IsAcceptedWithAllContractFields()
        {
            DateTimeOffset updatedAt = DateTimeOffset.FromUnixTimeSeconds(1234);
            Assert.That(PlayerIdentitySnapshot.TryCreate("U10001", "Alice", 7, updatedAt,
                out PlayerIdentitySnapshot snapshot, out string error), Is.True, error);
            Assert.That(snapshot.PlayerUid, Is.EqualTo("U10001"));
            Assert.That(snapshot.DisplayName, Is.EqualTo("Alice"));
            Assert.That(snapshot.ProfileRevision, Is.EqualTo(7));
            Assert.That(snapshot.UpdatedAt, Is.EqualTo(updatedAt));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void EmptyPlayerUid_IsRejected(string uid)
        {
            Assert.That(PlayerIdentitySnapshot.TryCreate(uid, "Alice", 1, DateTimeOffset.UnixEpoch,
                out PlayerIdentitySnapshot snapshot, out string error), Is.False);
            Assert.That(snapshot, Is.Null);
            StringAssert.Contains("PlayerUid", error);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void EmptyDisplayName_IsRejected(string displayName)
        {
            Assert.That(PlayerIdentitySnapshot.TryCreate("U10001", displayName, 1, DateTimeOffset.UnixEpoch,
                out PlayerIdentitySnapshot snapshot, out string error), Is.False);
            Assert.That(snapshot, Is.Null);
            StringAssert.Contains("DisplayName", error);
        }

        [Test]
        public void NegativeProfileRevision_IsRejected()
        {
            Assert.That(PlayerIdentitySnapshot.TryCreate("U10001", "Alice", -1, DateTimeOffset.UnixEpoch,
                out PlayerIdentitySnapshot snapshot, out string error), Is.False);
            Assert.That(snapshot, Is.Null);
            StringAssert.Contains("ProfileRevision", error);
        }

        [Test]
        public void DisplayNameChange_KeepsSamePlayerUidAndPersistentKey()
        {
            PlayerIdentitySnapshot first = Snapshot("U10001", "Alice", 1);
            PlayerIdentitySnapshot renamed = Snapshot("U10001", "Alicia", 2);

            Assert.That(first.PlayerUid, Is.EqualTo(renamed.PlayerUid));
            Assert.That(PlayerKey(first), Is.EqualTo(PlayerKey(renamed)));
        }

        [Test]
        public void SameDisplayName_DifferentPlayerUidsRemainDistinct()
        {
            PlayerIdentitySnapshot first = Snapshot("U10001", "Player", 1);
            PlayerIdentitySnapshot second = Snapshot("U10002", "Player", 1);

            Assert.That(first.DisplayName, Is.EqualTo(second.DisplayName));
            Assert.That(PlayerKey(first), Is.Not.EqualTo(PlayerKey(second)));
        }

        [Test]
        public void Binding_CreatesExpectedPersistentIdentityKey()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            EntityRegistry registry = Registry();
            PlayerIdentityBinding binding = Binding(player, Provider("U10001", "Alice"), registry);

            Assert.That(binding.TryApplyIdentity(out string error), Is.True, error);
            Assert.That(player.TryGetPersistentIdentityKey(out PersistentIdentityKey key), Is.True);
            Assert.That(key.ToString(), Is.EqualTo("player:Player:U10001"));
        }

        [TestCase(EntityKind.Agent, AgentRole.Npc)]
        [TestCase(EntityKind.Object, AgentRole.None)]
        public void Binding_RejectsNonPlayerEntity(EntityKind kind, AgentRole role)
        {
            WorldEntity entity = Entity("not_player", kind, role);
            PlayerIdentityBinding binding = Binding(entity, Provider("U10001", "Alice"), Registry());

            Assert.That(binding.TryApplyIdentity(out string error), Is.False);
            StringAssert.Contains("Agent/Player", error);
            Assert.That(entity.HasPersistentIdentity, Is.False);
        }

        [Test]
        public void Binding_RejectsInvalidProviderIdentity()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            StubPlayerIdentityProvider provider = Component<StubPlayerIdentityProvider>("Provider");
            provider.Invalidate();
            PlayerIdentityBinding binding = Binding(player, provider, Registry());

            Assert.That(binding.TryApplyIdentity(out string error), Is.False);
            StringAssert.Contains("did not return", error);
        }

        [Test]
        public void Registry_ResolvesBoundPlayerByPersistentIdentity()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            EntityRegistry registry = Registry();
            Assert.That(Binding(player, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, player);

            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(player));
        }

        [Test]
        public void DuplicateActivePlayerUid_IsRejected()
        {
            EntityRegistry registry = Registry();
            WorldEntity first = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            WorldEntity second = Entity("player_02", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(first, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Assert.That(Binding(second, Provider("U10001", "Alicia"), registry).TryApplyIdentity(out _), Is.True);
            SetField(registry, "entities", new List<WorldEntity> { first, second });
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("Duplicate PersistentIdentity 'player:Player:U10001'"));

            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.False);
        }

        [Test]
        public void Unregister_RemovesPersistentIdentityLookup()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            EntityRegistry registry = Registry();
            Assert.That(Binding(player, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, player);

            Assert.That(registry.Unregister(player), Is.True);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.False);
            Assert.That(registry.IsReady, Is.True);
        }

        [Test]
        public void Rebuild_DoesNotResurrectRetiredPlayerEntityId()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            EntityRegistry registry = Registry();
            Assert.That(Binding(player, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);

            Rebuild(registry, player);
            Assert.That(registry.Unregister(player), Is.True);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.False);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("EntityId 'player_01' was retired"));
            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.False);
        }

        [Test]
        public void InvalidRegistry_DoesNotResolvePersistentIdentity()
        {
            EntityRegistry registry = Registry();
            WorldEntity first = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            WorldEntity second = Entity("player_02", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(first, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Assert.That(Binding(second, Provider("U10001", "Alice 2"), registry).TryApplyIdentity(out _), Is.True);
            SetField(registry, "entities", new List<WorldEntity> { first, second });
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("Duplicate PersistentIdentity 'player:Player:U10001'"));

            registry.RebuildRegistry();

            Assert.That(registry.IsReady, Is.False);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.False);
        }

        [Test]
        public void ReadyRegistry_DoesNotResolveInvalidPersistentIdentityKey()
        {
            EntityRegistry registry = Registry();
            Rebuild(registry);

            Assert.That(registry.TryResolvePersistentIdentity(default, out WorldEntity found), Is.False);
            Assert.That(found, Is.Null);
        }

        [Test]
        public void RegisteredBinding_AllowsDisplayNameRefreshButRejectsUidChange()
        {
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            EntityRegistry registry = Registry();
            StubPlayerIdentityProvider provider = Provider("U10001", "Alice");
            PlayerIdentityBinding binding = Binding(player, provider, registry);
            Assert.That(binding.TryApplyIdentity(out _), Is.True);
            Rebuild(registry, player);

            provider.Configure("U10001", "Alicia", 2);
            Assert.That(binding.TryApplyIdentity(out string renameError), Is.True, renameError);
            Assert.That(binding.CurrentIdentity.DisplayName, Is.EqualTo("Alicia"));
            Assert.That(player.EntityId, Is.EqualTo("player_01"));
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.True);

            provider.Configure("U20002", "Alicia", 3);
            Assert.That(binding.TryApplyIdentity(out string rebindError), Is.False);
            StringAssert.Contains("unregister", rebindError.ToLowerInvariant());
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U10001"), out _), Is.True);
            Assert.That(registry.TryResolvePersistentIdentity(PlayerKey("U20002"), out _), Is.False);
        }

        [Test]
        public void InstanceReplacement_PreservesUidContinuityAndResolvesOnlyNewEntity()
        {
            EntityRegistry registry = Registry();
            WorldEntity first = Entity("player_instance_A", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(first, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, first);
            PersistentIdentityKey firstKey = PlayerKey("U10001");
            Assert.That(registry.Unregister(first), Is.True);

            WorldEntity second = Entity("player_instance_B", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(second, Provider("U10001", "Alice Renamed"), registry).TryApplyIdentity(out _), Is.True);
            registry.Register(second);

            Assert.That(first.EntityId, Is.Not.EqualTo(second.EntityId));
            Assert.That(second.TryGetPersistentIdentityKey(out PersistentIdentityKey secondKey), Is.True);
            Assert.That(secondKey, Is.EqualTo(firstKey));
            Assert.That(registry.TryResolvePersistentIdentity(firstKey, out WorldEntity resolved), Is.True);
            Assert.That(resolved, Is.SameAs(second));
        }

        [Test]
        public void DialogueAndGoalOutcome_ProjectTheSameAuthoritativePlayerIdentity()
        {
            EntityRegistry registry = Registry();
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            WorldEntity merchant = Entity("npc_cafe_01", EntityKind.Agent, AgentRole.Npc);
            merchant.Configure(merchant.EntityId, merchant.DefinitionId, EntityKind.Agent, AgentRole.Npc,
                new PersistentIdentityRef("npc", "Character", "cafe.merchant.001"));
            Assert.That(Binding(player, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, player, merchant);

            ExperienceDraft input = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(false,
                "request_input", "trace_input", "conversation_01", player.EntityId, "npc_cafe_01",
                "coffee", 1, 1d, 1d), registry);
            ExperienceDraft response = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(true,
                "request_response", "trace_response", "conversation_01", player.EntityId, "npc_cafe_01",
                "coming", 1, 2d, 2d), registry);
            ExperienceDraft goal = GoalOutcomeExperienceAdapter.Create(new GoalOutcome(
                "outcome_01", "proposal_01", 1, "trace_goal", "bring_object", "npc_cafe_01",
                "cup_01", player.EntityId, PlanExecutionStatus.Succeeded, string.Empty, 2, 3d), registry);

            AssertParticipant(input, "player_01", "U10001");
            AssertParticipant(response, "player_01", "U10001");
            AssertParticipant(goal, "player_01", "U10001");
            AssertParticipant(input, "npc_cafe_01", "cafe.merchant.001", "npc", "Character");
            AssertParticipant(response, "npc_cafe_01", "cafe.merchant.001", "npc", "Character");
            AssertParticipant(goal, "npc_cafe_01", "cafe.merchant.001", "npc", "Character");
            Assert.That(input.PersistentParticipants, Has.Count.EqualTo(2));
            Assert.That(response.PersistentParticipants, Has.Count.EqualTo(2));
            Assert.That(goal.PersistentParticipants, Has.Count.EqualTo(2));
            Assert.That(input.ActorEntityId, Is.EqualTo("player_01"));
            Assert.That(response.TargetEntityIds, Is.EqualTo(new[] { "player_01" }));
            Assert.That(goal.TargetEntityIds, Does.Contain("player_01"));
        }

        [Test]
        public void ExperienceProjection_PreservesUidAcrossDifferentRuntimeEntityIds()
        {
            EntityRegistry registry = Registry();
            WorldEntity first = Entity("player_instance_a", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(first, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, first);
            ExperienceDraft firstDraft = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(false,
                "request_a", "trace_a", "conversation_a", first.EntityId, "npc_cafe_01", "hello",
                1, 1d, 1d), registry);

            Assert.That(registry.Unregister(first), Is.True);
            WorldEntity second = Entity("player_instance_b", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(second, Provider("U10001", "Alice Renamed"), registry)
                .TryApplyIdentity(out _), Is.True);
            registry.Register(second);
            ExperienceDraft secondDraft = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(false,
                "request_b", "trace_b", "conversation_b", second.EntityId, "npc_cafe_01", "hello again",
                2, 2d, 2d), registry);

            Assert.That(firstDraft.ActorEntityId, Is.EqualTo("player_instance_a"));
            Assert.That(secondDraft.ActorEntityId, Is.EqualTo("player_instance_b"));
            Assert.That(firstDraft.PersistentParticipants.Single().PersistentId,
                Is.EqualTo(secondDraft.PersistentParticipants.Single().PersistentId));
        }

        [Test]
        public void SkillExperience_UsesBoundActorAndActualTargetsOnly()
        {
            EntityRegistry registry = Registry();
            WorldEntity player = Entity("player_01", EntityKind.Agent, AgentRole.Player);
            WorldEntity merchant = Entity("npc_cafe_01", EntityKind.Agent, AgentRole.Npc);
            merchant.Configure(merchant.EntityId, merchant.DefinitionId, EntityKind.Agent, AgentRole.Npc,
                new PersistentIdentityRef("npc", "Character", "cafe.merchant.001"));
            Assert.That(Binding(player, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, player, merchant);

            var pickup = new SkillResult("pick", "trace", "PickUp", merchant.EntityId, "cup_01", "",
                SkillStatus.Succeeded, "", 1L, 0, 1);
            var give = new SkillResult("give", "trace", "Give", merchant.EntityId, "cup_01", player.EntityId,
                SkillStatus.Succeeded, "", 2L, 1, 2);
            ExperienceDraft picked = SkillResultExperienceAdapter.Create(pickup, 1d, registry);
            ExperienceDraft given = SkillResultExperienceAdapter.Create(give, 2d, registry);
            AssertParticipant(picked, merchant.EntityId, "cafe.merchant.001", "npc", "Character");
            Assert.That(picked.PersistentParticipants, Has.Count.EqualTo(1));
            AssertParticipant(given, player.EntityId, "U10001");
            AssertParticipant(given, merchant.EntityId, "cafe.merchant.001", "npc", "Character");
            Assert.That(given.PersistentParticipants, Has.Count.EqualTo(2));
            Assert.That(SkillResultExperienceAdapter.Create(pickup, 1d).PersistentParticipants, Is.Empty);
        }

        [Test]
        public void ExperienceProjection_DoesNotMergeSameDisplayNameWithDifferentUids()
        {
            EntityRegistry registry = Registry();
            WorldEntity first = Entity("player_a", EntityKind.Agent, AgentRole.Player);
            WorldEntity second = Entity("player_b", EntityKind.Agent, AgentRole.Player);
            Assert.That(Binding(first, Provider("U10001", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Assert.That(Binding(second, Provider("U20002", "Alice"), registry).TryApplyIdentity(out _), Is.True);
            Rebuild(registry, first, second);

            ExperienceDraft firstDraft = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(false,
                "request_a", "trace_a", "conversation_a", first.EntityId, "npc_cafe_01", "hello",
                1, 1d, 1d), registry);
            ExperienceDraft secondDraft = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(false,
                "request_b", "trace_b", "conversation_b", second.EntityId, "npc_cafe_01", "hello",
                1, 1d, 1d), registry);

            Assert.That(firstDraft.PersistentParticipants.Single().PersistentId, Is.EqualTo("U10001"));
            Assert.That(secondDraft.PersistentParticipants.Single().PersistentId, Is.EqualTo("U20002"));
        }

        private static void AssertParticipant(ExperienceDraft draft, string entityId, string persistentId)
        {
            AssertParticipant(draft, entityId, persistentId,
                PlayerIdentityBinding.IdentityNamespace, PlayerIdentityBinding.IdentityType);
        }

        private static void AssertParticipant(ExperienceDraft draft, string entityId, string persistentId,
            string identityNamespace, string identityType)
        {
            PersistentParticipantIdentity identity = draft.PersistentParticipants.Single(
                value => value.EntityId == entityId);
            Assert.That(identity.EntityId, Is.EqualTo(entityId));
            Assert.That(identity.IdentityNamespace, Is.EqualTo(identityNamespace));
            Assert.That(identity.IdentityType, Is.EqualTo(identityType));
            Assert.That(identity.PersistentId, Is.EqualTo(persistentId));
            Assert.That(draft.IsValid, Is.True);
        }

        private PlayerIdentitySnapshot Snapshot(string uid, string displayName, long revision)
        {
            Assert.That(PlayerIdentitySnapshot.TryCreate(uid, displayName, revision, DateTimeOffset.UnixEpoch,
                out PlayerIdentitySnapshot snapshot, out string error), Is.True, error);
            return snapshot;
        }

        private static PersistentIdentityKey PlayerKey(PlayerIdentitySnapshot snapshot) => PlayerKey(snapshot.PlayerUid);

        private static PersistentIdentityKey PlayerKey(string uid)
        {
            Assert.That(new PersistentIdentityRef(PlayerIdentityBinding.IdentityNamespace,
                PlayerIdentityBinding.IdentityType, uid).TryGetKey(out PersistentIdentityKey key), Is.True);
            return key;
        }

        private StubPlayerIdentityProvider Provider(string uid, string displayName)
        {
            StubPlayerIdentityProvider provider = Component<StubPlayerIdentityProvider>("Provider");
            provider.Configure(uid, displayName);
            return provider;
        }

        private PlayerIdentityBinding Binding(WorldEntity entity, MonoBehaviour provider, EntityRegistry registry)
        {
            PlayerIdentityBinding binding = entity.gameObject.AddComponent<PlayerIdentityBinding>();
            binding.Configure(entity, provider, registry);
            return binding;
        }

        private WorldEntity Entity(string id, EntityKind kind, AgentRole role)
        {
            WorldEntity entity = Component<WorldEntity>(id);
            entity.Configure(id, $"test.{id}", kind, role);
            return entity;
        }

        private EntityRegistry Registry()
        {
            EntityRegistry registry = Component<EntityRegistry>("Registry");
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity>());
            return registry;
        }

        private void Rebuild(EntityRegistry registry, params WorldEntity[] entities)
        {
            SetField(registry, "entities", new List<WorldEntity>(entities));
            registry.RebuildRegistry();
            Assert.That(registry.IsReady, Is.True);
        }

        private T Component<T>(string name) where T : Component
        {
            GameObject value = new(name);
            objects.Add(value);
            return value.AddComponent<T>();
        }

        private static void SetField<T>(object target, string name, T value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
