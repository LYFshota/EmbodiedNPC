using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class WorkingMemoryLifecycleTests
    {
        [Test]
        public void ActiveCount_PrunesExpiredRecordsWithoutRefreshingTheirAge()
        {
            var memory = new SpatialWorkingMemory(lifetimeSeconds: 10d);
            memory.Observe("cup_01", Vector3.one, 1d, 2, Fields());

            Assert.That(memory.ActiveCount(10.99d), Is.EqualTo(1));
            Assert.That(memory.ActiveCount(11d), Is.Zero);
            Assert.That(memory.Diagnostics.Reason, Is.EqualTo("ttl_expired"));
            Assert.That(memory.Diagnostics.AffectedCount, Is.EqualTo(1));
        }

        [Test]
        public void ExplicitClear_RemovesOnlyThisObserversBoundedStateAndRecordsReason()
        {
            var firstObserver = new SpatialWorkingMemory();
            var secondObserver = new SpatialWorkingMemory();
            firstObserver.Observe("cup_01", Vector3.zero, 1d, 0, Fields());
            secondObserver.Observe("cup_01", Vector3.right, 1d, 0, Fields());

            Assert.That(firstObserver.Clear("test_reset", 2d), Is.EqualTo(1));

            Assert.That(firstObserver.ActiveCount(2d), Is.Zero);
            Assert.That(firstObserver.Diagnostics.Reason, Is.EqualTo("test_reset"));
            Assert.That(secondObserver.ActiveCount(2d), Is.EqualTo(1));
        }

        [Test]
        public void DialogueContext_RequiresConversationSpeakerAndTargetIdentity()
        {
            var context = new DialogueContext
            {
                conversationId = "",
                speakerEntityId = "player_01",
                targetAgentId = "npc_01",
            };
            Assert.That(context.IsValid, Is.False);

            context.conversationId = "conversation_01";
            Assert.That(context.IsValid, Is.True);
        }
        private static Dictionary<string, string> Fields() => new()
        {
            ["entity_kind"] = "Item",
            ["display_name"] = "一号杯",
            ["semantic_tags"] = "cup",
            ["capabilities"] = "pick_up,give",
        };

    }
}
