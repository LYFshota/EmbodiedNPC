using System.Collections.Generic;
using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class SpatialWorkingMemoryTests
    {
        [Test]
        public void LookingAway_RetainsPositionBeyondVisualLifecycle_AndReadsDoNotRefreshExpiry()
        {
            var memory = new SpatialWorkingMemory();
            memory.Observe("cup_01", new Vector3(2, 0, 3), 1d, 4, Fields("一号杯"));

            Assert.That(memory.TryGet("cup_01", 20d, out SpatialMemoryRecord record), Is.True);
            Assert.That(record.LastKnownPosition, Is.EqualTo(new Vector3(2, 0, 3)));
            Assert.That(record.LastSeenAt, Is.EqualTo(1d));
            Assert.That(record.LastWorldRevision, Is.EqualTo(4));
            Assert.That(record.ConfidenceAt(20d), Is.LessThan(1f));
            Assert.That(memory.TryGet("cup_01", 120d, out _), Is.True);
            Assert.That(memory.TryGet("cup_01", 121d, out _), Is.False);
            Assert.That(memory.Count, Is.Zero);
        }

        [Test]
        public void Invalidation_BlocksLocationUntilNewVisualObservation()
        {
            var memory = new SpatialWorkingMemory();
            memory.Observe("cup_01", Vector3.zero, 1d, 0, Fields("一号杯"));
            Assert.That(memory.Invalidate("cup_01"), Is.True);
            Assert.That(memory.TryGet("cup_01", 2d, out _), Is.False);
            memory.Observe("cup_01", Vector3.right, 3d, 1, Fields("一号杯"));
            Assert.That(memory.TryGet("cup_01", 4d, out SpatialMemoryRecord record), Is.True);
            Assert.That(record.LastKnownPosition, Is.EqualTo(Vector3.right));
            Assert.That(record.LastSeenAt, Is.EqualTo(3d));
        }

        [Test]
        public void SemanticSnapshot_IsCopiedAndWhitelisted_AndOlderEvidenceCannotOverwrite()
        {
            var memory = new SpatialWorkingMemory();
            Dictionary<string, string> fields = Fields("一号杯");
            fields["hidden_inventory"] = "secret";
            fields["last_seen_position"] = "99,99,99";
            memory.Observe("cup_01", Vector3.zero, 3d, 1, fields);
            fields["display_name"] = "未被看到的改名";
            memory.Observe("cup_01", Vector3.one, 2d, 0, fields);

            Assert.That(memory.TryGet("cup_01", 4d, out SpatialMemoryRecord record), Is.True);
            Assert.That(record.SemanticFields["display_name"], Is.EqualTo("一号杯"));
            Assert.That(record.SemanticFields.ContainsKey("hidden_inventory"), Is.False);
            Assert.That(record.SemanticFields.ContainsKey("last_seen_position"), Is.False);
            Assert.That(record.LastKnownPosition, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void CapacityEviction_IsDeterministic_AndObserversAreIsolated()
        {
            var firstNpc = new SpatialWorkingMemory(capacity: 2);
            var secondNpc = new SpatialWorkingMemory(capacity: 2);
            firstNpc.Observe("b", Vector3.zero, 1d, 0, Fields("B"));
            firstNpc.Observe("a", Vector3.zero, 1d, 0, Fields("A"));
            firstNpc.Observe("c", Vector3.zero, 2d, 0, Fields("C"));

            Assert.That(firstNpc.Count, Is.EqualTo(2));
            Assert.That(firstNpc.TryGet("a", 2d, out _), Is.False);
            Assert.That(firstNpc.TryGet("b", 2d, out _), Is.True);
            Assert.That(secondNpc.TryGet("b", 2d, out _), Is.False);
            Assert.That(firstNpc.GetRecords(200d), Is.Empty);
        }

        private static Dictionary<string, string> Fields(string name) => new()
        {
            ["entity_kind"] = "Item", ["display_name"] = name, ["aliases"] = name,
            ["semantic_tags"] = "cup", ["capabilities"] = "pick_up,give",
        };
    }
}
