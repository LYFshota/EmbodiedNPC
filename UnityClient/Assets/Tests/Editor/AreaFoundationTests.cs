using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class AreaFoundationTests
    {
        private readonly List<GameObject> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void EnterAndLeaveArea_UpdatesPrimaryMembership()
        {
            WorldEntity player = Entity("player_01", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity entrance = Entity("area_entrance", "cafe.area.entrance", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(player, entrance);
            AreaMembershipService membership = Membership(registry);

            Assert.That(membership.TryEnterArea(player, entrance, out string error), Is.True, error);
            Assert.That(membership.TryGetPrimaryArea(player.EntityId, out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(entrance));
            Assert.That(membership.LeaveArea(player, entrance), Is.True);
            Assert.That(membership.TryGetPrimaryArea(player.EntityId, out _), Is.False);
        }

        [Test]
        public void EnteringSecondArea_ReplacesPrimaryAreaAndLateExitDoesNotClearIt()
        {
            WorldEntity player = Entity("player_01", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity entrance = Entity("area_entrance", "cafe.area.entrance", EntityKind.Area);
            WorldEntity counter = Entity("area_counter", "cafe.area.counter", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(player, entrance, counter);
            AreaMembershipService membership = Membership(registry);

            Assert.That(membership.TryEnterArea(player, entrance, out _), Is.True);
            Assert.That(membership.TryEnterArea(player, counter, out _), Is.True);
            Assert.That(membership.LeaveArea(player, entrance), Is.False);
            Assert.That(membership.TryGetPrimaryArea(player.EntityId, out WorldEntity found), Is.True);
            Assert.That(found, Is.SameAs(counter));
        }

        [Test]
        public void UnregisterEntity_ClearsPrimaryMembership()
        {
            WorldEntity player = Entity("player_01", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity entrance = Entity("area_entrance", "cafe.area.entrance", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(player, entrance);
            AreaMembershipService membership = Membership(registry);
            Assert.That(membership.TryEnterArea(player, entrance, out _), Is.True);

            Assert.That(registry.Unregister(player), Is.True);

            Assert.That(membership.TryGetPrimaryArea(player.EntityId, out _), Is.False);
        }

        [Test]
        public void ReplacementWithNewEntityId_DoesNotInheritAreaMembership()
        {
            WorldEntity original = Entity("player_old", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity entrance = Entity("area_entrance", "cafe.area.entrance", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(original, entrance);
            AreaMembershipService membership = Membership(registry);
            Assert.That(membership.TryEnterArea(original, entrance, out _), Is.True);
            Assert.That(registry.Unregister(original), Is.True);
            WorldEntity replacement = Entity("player_new", "character.player", EntityKind.Agent, AgentRole.Player);
            registry.Register(replacement);

            Assert.That(membership.TryGetPrimaryArea(original.EntityId, out _), Is.False);
            Assert.That(membership.TryGetPrimaryArea(replacement.EntityId, out _), Is.False);
        }

        [Test]
        public void NonAreaWorldEntity_CannotBecomePrimaryArea()
        {
            WorldEntity player = Entity("player_01", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity counterObject = Entity("counter_01", "cafe.counter", EntityKind.Object);
            EntityRegistry registry = ReadyRegistry(player, counterObject);
            AreaMembershipService membership = Membership(registry);

            Assert.That(membership.TryEnterArea(player, counterObject, out string error), Is.False);
            StringAssert.Contains("not an Area", error);
            Assert.That(membership.TryGetPrimaryArea(player.EntityId, out _), Is.False);
        }

        private AreaMembershipService Membership(EntityRegistry registry)
        {
            AreaMembershipService membership = Component<AreaMembershipService>("AreaMembership");
            membership.Configure(registry);
            return membership;
        }

        private EntityRegistry ReadyRegistry(params WorldEntity[] entities)
        {
            EntityRegistry registry = Component<EntityRegistry>("Registry");
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity>(entities));
            registry.RebuildRegistry();
            Assert.That(registry.IsReady, Is.True);
            return registry;
        }

        private WorldEntity Entity(string id, string definitionId, EntityKind kind,
            AgentRole role = AgentRole.None)
        {
            WorldEntity entity = Component<WorldEntity>(id);
            entity.Configure(id, definitionId, kind, role);
            return entity;
        }

        private T Component<T>(string name) where T : Component
        {
            GameObject value = new(name);
            objects.Add(value);
            return value.AddComponent<T>();
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
