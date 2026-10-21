using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class SpatialContextProjectionTests
    {
        [Test]
        public void RememberedTarget_SurvivesShortVisualForgetting_WithoutReadingHiddenChangesOrSendingPositions()
        {
            var state = new PerceptionAggregator("npc_01");
            var memory = new SpatialWorkingMemory();
            Dictionary<string, string> fields = Fields("一号杯", "current_visible");
            memory.Observe("cup_01", new Vector3(123, 45, 67), 1d, 0, fields);
            state.ApplyOrRefresh(Visual("cup_01", 1d, fields));
            state.ApplyOrRefresh(Visual("cup_01", 10d, Fields("一号杯", "forgotten"), PerceptionLifecycleState.Forgotten));
            fields["display_name"] = "隐藏的新名称";

            PerceptionSnapshotDto dto = new ContextSnapshotBuilder().Build(state, "把一号杯给我", 20d, 5, memory).ToPerceptionDto();

            Assert.That(dto.entities, Has.Length.EqualTo(1));
            Assert.That(dto.entities[0].visibility, Is.EqualTo("remembered"));
            Assert.That(dto.entities[0].display_name, Is.EqualTo("一号杯"));
            string json = JsonUtility.ToJson(dto);
            Assert.That(json, Does.Not.Contain("position"));
            Assert.That(json, Does.Not.Contain("123"));
            Assert.That(json, Does.Not.Contain("隐藏的新名称"));
        }

        [Test]
        public void CurrentVisibleSemantics_WinOverOlderOrIncompleteEventCopiesAndCachedNames()
        {
            var state = new PerceptionAggregator("npc_01");
            var memory = new SpatialWorkingMemory();
            memory.Observe("cup_01", Vector3.zero, 1d, 0, Fields("旧名称", "current_visible"));
            state.ApplyOrRefresh(Visual("cup_01", 4d, Fields("当前一号杯", "current_visible")));
            state.Apply(new Observation("event", "npc_01", ObservationSourceType.GameplayEvent, "event", ObservationChannel.Visual,
                "cup_01", new Dictionary<string, string> { ["event_type"] = "ItemChanged" }, new string[0], 1f, 0, 5d, 100d));

            PerceivedEntityDto entity = new ContextSnapshotBuilder().Build(state, "杯子", 6d, 1, memory).ToPerceptionDto().entities.Single();

            Assert.That(entity.visibility, Is.EqualTo("current_visible"));
            Assert.That(entity.display_name, Is.EqualTo("当前一号杯"));
            Assert.That(entity.kind, Is.EqualTo("item"));
        }

        [Test]
        public void ExpiredInvalidatedAndForgottenTargets_AreNotOfferedAsCandidates()
        {
            var state = new PerceptionAggregator("npc_01");
            var memory = new SpatialWorkingMemory(10d);
            foreach (string id in new[] { "expired", "invalidated", "never_memorized" })
                state.ApplyOrRefresh(Visual(id, 1d, Fields(id, "forgotten"), PerceptionLifecycleState.Forgotten));
            memory.Observe("expired", Vector3.zero, 1d, 0, Fields("过期杯", "current_visible"));
            memory.Observe("invalidated", Vector3.zero, 5d, 0, Fields("失效杯", "current_visible"));
            memory.Invalidate("invalidated");

            Assert.That(new ContextSnapshotBuilder().Build(state, "杯子", 12d, 5, memory).ToPerceptionDto().entities, Is.Empty);
        }

        [Test]
        public void Projection_IsBoundedAndPrioritizesExplicitRememberedTarget()
        {
            var state = new PerceptionAggregator("npc_01");
            var memory = new SpatialWorkingMemory();
            memory.Observe("special", Vector3.zero, 1d, 0, Fields("一号杯", "current_visible"));
            for (int index = 0; index < 20; index++)
            {
                string id = "visible_" + index;
                state.ApplyOrRefresh(Visual(id, 2d, Fields(id, "current_visible")));
            }

            PerceivedEntityDto[] entities = new ContextSnapshotBuilder().Build(state, "把一号杯给我", 3d, 1, memory).ToPerceptionDto().entities;

            Assert.That(entities, Has.Length.EqualTo(16));
            Assert.That(entities[0].entity_id, Is.EqualTo("special"));
            Assert.That(entities[0].visibility, Is.EqualTo("remembered"));
            Assert.That(entities.Select(entity => entity.entity_id).Distinct().Count(), Is.EqualTo(16));
        }

        private static Dictionary<string, string> Fields(string name, string visibility) => new()
        {
            ["entity_kind"] = "Item", ["display_name"] = name, ["aliases"] = name,
            ["semantic_tags"] = "cup", ["capabilities"] = "pick_up,give", ["visibility_state"] = visibility,
        };

        private static Observation Visual(string id, double now, IReadOnlyDictionary<string, string> fields,
            PerceptionLifecycleState lifecycle = PerceptionLifecycleState.Tracked) =>
            new("obs_" + id, "npc_01", ObservationSourceType.ContinuousScan, "visual:" + id, ObservationChannel.Visual,
                id, fields, new string[0], 1f, 0, now, double.PositiveInfinity, lifecycle);
    }
}
