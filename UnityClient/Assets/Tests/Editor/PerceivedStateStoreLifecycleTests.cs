using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class PerceivedStateStoreLifecycleTests
    {
        [Test]
        public void DisableKeepsSpatialMemory_DestroyClearsIt()
        {
            var owner = new GameObject("MemP0Observer");
            WorldEntity entity = owner.AddComponent<WorldEntity>();
            SetField(entity, "entityId", "mem_p0_observer");
            SetField(entity, "kind", EntityKind.Agent);
            SetField(entity, "agentRole", AgentRole.Npc);
            PerceivedStateStore store = owner.AddComponent<PerceivedStateStore>();
            InvokeLifecycle(store, "Awake");
            SpatialWorkingMemory memory = store.SpatialMemory;
            double observedAt = Time.realtimeSinceStartupAsDouble;
            memory.Observe("cup_01", Vector3.one, observedAt, 0, Fields());

            store.enabled = false;
            InvokeLifecycle(store, "OnDisable");

            Assert.That(memory.ActiveCount(Time.realtimeSinceStartupAsDouble), Is.EqualTo(1));
            Assert.That(memory.Diagnostics.Reason, Is.EqualTo("observer_disabled_keep"));

            InvokeLifecycle(store, "OnDestroy");
            Object.DestroyImmediate(store);

            Assert.That(memory.ActiveCount(Time.realtimeSinceStartupAsDouble), Is.Zero);
            Assert.That(memory.Diagnostics.Reason, Is.EqualTo("observer_destroyed"));
            Object.DestroyImmediate(owner);
        }

        [Test]
        public void RecreatedStoreStartsWithFreshObserverLocalMemory()
        {
            GameObject firstOwner = CreateObserver("first_observer");
            PerceivedStateStore first = firstOwner.GetComponent<PerceivedStateStore>();
            double now = Time.realtimeSinceStartupAsDouble;
            first.SpatialMemory.Observe("cup_01", Vector3.zero, now, 0, Fields());
            Assert.That(first.SpatialMemory.ActiveCount(now), Is.EqualTo(1));
            Object.DestroyImmediate(firstOwner);

            GameObject recreatedOwner = CreateObserver("first_observer");
            PerceivedStateStore recreated = recreatedOwner.GetComponent<PerceivedStateStore>();
            Assert.That(recreated.SpatialMemory.ActiveCount(Time.realtimeSinceStartupAsDouble), Is.Zero);
            Object.DestroyImmediate(recreatedOwner);
        }

        private static GameObject CreateObserver(string entityId)
        {
            var owner = new GameObject(entityId);
            WorldEntity entity = owner.AddComponent<WorldEntity>();
            SetField(entity, "entityId", entityId);
            SetField(entity, "kind", EntityKind.Agent);
            SetField(entity, "agentRole", AgentRole.Npc);
            InvokeLifecycle(owner.AddComponent<PerceivedStateStore>(), "Awake");
            return owner;
        }

        private static void InvokeLifecycle(PerceivedStateStore store, string method) =>
            typeof(PerceivedStateStore).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(store, null);

        private static void SetField<T>(WorldEntity entity, string name, T value) =>
            typeof(WorldEntity).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(entity, value);

        private static Dictionary<string, string> Fields() => new()
        {
            ["entity_kind"] = "Item",
            ["display_name"] = "一号杯",
            ["semantic_tags"] = "cup",
            ["capabilities"] = "pick_up,give",
        };
    }
}
