using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Perception;
using NUnit.Framework;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class ContextSnapshotBuilderTests
    {
        [Test]
        public void Snapshot_PrioritizesThreatAndRequestRelevantEvidence_AndDropsExpired()
        {
            PerceptionAggregator state = new("npc_01");
            state.Apply(ObservationFor("visual_book", ObservationChannel.Visual, "book_01", 0.9f, 10d,
                new Dictionary<string, string> { ["visibility_state"] = "current_visible", ["semantic_tags"] = "book" }));
            state.Apply(ObservationFor("weapon", ObservationChannel.Audio, string.Empty, 0.2f, 10d,
                new Dictionary<string, string> { ["event_type"] = "WeaponDrawn", ["audio_category"] = "weapon_draw" }));
            state.Apply(ObservationFor("visual_cup", ObservationChannel.Visual, "cup_01", 1f, 10d,
                new Dictionary<string, string> { ["visibility_state"] = "current_visible", ["semantic_tags"] = "cup" }));
            state.Apply(ObservationFor("expired", ObservationChannel.Audio, string.Empty, 1f, 1d,
                new Dictionary<string, string> { ["event_type"] = "Speech" }));

            ContextSnapshot snapshot = new ContextSnapshotBuilder().Build(state, "bring the book", 2d, 2);

            Assert.That(snapshot.Observations.Select(value => value.SourceId), Does.Contain("weapon"));
            Assert.That(snapshot.Observations.Select(value => value.SubjectEntityId), Does.Contain("book_01"));
            Assert.That(snapshot.Observations.Select(value => value.SourceId), Does.Not.Contain("expired"));
            Assert.That(snapshot.DroppedObservations, Is.EqualTo(1));
        }

        [Test]
        public void PlannerFacts_UseOnlyCurrentObserverLocalEvidence()
        {
            PerceptionAggregator state = new("npc_01");
            state.Apply(ObservationFor("visible", ObservationChannel.Visual, "cup_01", 1f, double.PositiveInfinity,
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }));
            state.Apply(ObservationFor("weapon", ObservationChannel.Audio, string.Empty, 0.5f, 10d,
                new Dictionary<string, string> { ["event_type"] = "WeaponDrawn" }));

            PlannerFacts facts = new PlannerFactBuilder().Build(state, 2d);

            Assert.That(facts.Has("visible:cup_01"), Is.True);
            Assert.That(facts.Has("heard:WeaponDrawn"), Is.True);
            Assert.That(facts.Has("danger_nearby"), Is.True);
            Assert.That(facts.Has("visible:book_01"), Is.False);
        }

        [Test]
        public void PromptText_ContainsUnknownMarkerButNeverAnUnknownValue()
        {
            PerceptionAggregator state = new("npc_01");
            Observation audio = new("obs_audio", "npc_01", ObservationSourceType.GameplayEvent, "audio_secret",
                ObservationChannel.Audio, string.Empty,
                new Dictionary<string, string> { ["event_type"] = "DoorOpened" },
                new[] { "actor_entity_id", "exact_position" }, 0.5f, 1, 1d, 10d);
            state.Apply(audio);

            string prompt = new ContextSnapshotBuilder().Build(state, string.Empty, 2d).ToPromptText();

            Assert.That(prompt, Does.Contain("unknown=actor_entity_id,exact_position"));
            Assert.That(prompt, Does.Not.Contain("player_01"));
            Assert.That(prompt, Does.Not.Contain("secret_position"));
        }

        [Test]
        public void StructuredPerception_ExportsOnlySelectedObserverLocalEntities()
        {
            PerceptionAggregator state = new("npc_01");
            state.Apply(ObservationFor("visible_potion", ObservationChannel.Visual, "potion_blue_7", 1f, 10d,
                new Dictionary<string, string>
                {
                    ["visibility_state"] = "current_visible",
                    ["entity_kind"] = "Item",
                    ["display_name"] = "蓝色药瓶",
                    ["aliases"] = "药瓶,蓝色药瓶",
                    ["semantic_tags"] = "potion,consumable",
                    ["capabilities"] = "pick_up,give",
                }));

            PerceptionSnapshotDto dto = new ContextSnapshotBuilder().Build(state, "药瓶", 2d).ToPerceptionDto();

            Assert.That(dto.observer_entity_id, Is.EqualTo("npc_01"));
            Assert.That(dto.entities, Has.Length.EqualTo(1));
            Assert.That(dto.entities[0].entity_id, Is.EqualTo("potion_blue_7"));
            Assert.That(dto.entities[0].kind, Is.EqualTo("item"));
            Assert.That(dto.entities[0].aliases, Does.Contain("药瓶"));
            Assert.That(dto.entities[0].capabilities, Does.Contain("pick_up"));
        }

        [Test]
        public void GenericChineseConcept_KeepsAllVisibleSemanticMatchesInsideObservationBudget()
        {
            PerceptionAggregator state = new("npc_01");
            state.Apply(ObservationFor("cup_01", ObservationChannel.Visual, "cup_01", 1f, 10d,
                ItemFields("一号杯", "cup,drinkware"), 1d));
            state.Apply(ObservationFor("cup_02", ObservationChannel.Visual, "cup_02", 1f, 10d,
                ItemFields("二号杯", "cup,drinkware"), 1d));
            for (int index = 0; index < 5; index++)
                state.Apply(ObservationFor($"other_{index}", ObservationChannel.Visual, $"other_{index}", 1f, 10d,
                    ItemFields($"其他物品{index}", "other"), 2d + index));

            PerceptionSnapshotDto dto = new ContextSnapshotBuilder().Build(
                state, "把杯子拿给我", 8d, maxObservations: 5).ToPerceptionDto();

            CollectionAssert.AreEquivalent(new[] { "cup_01", "cup_02" }, dto.entities
                .Where(entity => entity.semantic_tags.Contains("cup"))
                .Select(entity => entity.entity_id));
        }

        [Test]
        public void M2RuntimeMetadataShape_SplitsBothCupCandidatesForGenericRequest()
        {
            PerceptionAggregator state = new("npc_01");
            Dictionary<string, string> first = new(ItemFields("一号杯", "cup,drinkware"))
            {
                ["aliases"] = "一号杯,1号杯",
            };
            Dictionary<string, string> second = new(ItemFields("二号杯", "cup,drinkware"))
            {
                ["aliases"] = "二号杯,2号杯",
            };
            state.Apply(ObservationFor("cup_01", ObservationChannel.Visual, "cup_01", 1f, 10d, first));
            state.Apply(ObservationFor("cup_02", ObservationChannel.Visual, "cup_02", 1f, 10d, second));

            PerceptionSnapshotDto dto = new ContextSnapshotBuilder().Build(
                state, "把杯子拿给我", 2d, maxObservations: 5).ToPerceptionDto();

            Assert.That(dto.entities.Select(entity => entity.entity_id),
                Is.EquivalentTo(new[] { "cup_01", "cup_02" }));
            Assert.That(dto.entities, Has.All.Matches<PerceivedEntityDto>(entity =>
                entity.kind == "item" && entity.semantic_tags.SequenceEqual(new[] { "cup", "drinkware" }) &&
                entity.capabilities.SequenceEqual(new[] { "pick_up" }) && entity.visibility == "current_visible"));
            Assert.That(dto.entities.Single(entity => entity.entity_id == "cup_01").aliases,
                Is.EqualTo(new[] { "一号杯", "1号杯" }));
            Assert.That(dto.entities.Single(entity => entity.entity_id == "cup_02").aliases,
                Is.EqualTo(new[] { "二号杯", "2号杯" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneFacts_FourSeats_IncludeEmptySeatAndRespectFourSeatBudget(bool seatedObserver)
        {
            PerceptionAggregator state = new("npc_01");
            foreach (string id in new[] { "seat_table1_a", "seat_table1_b", "seat_table2_c", "seat_table2_d" })
            {
                var fields = new Dictionary<string, string>
                {
                    ["visibility_state"] = "current_visible", ["entity_kind"] = "Environment",
                    ["semantic_tags"] = "seat", ["seat_occupancy"] = id == "seat_table2_d" ? "empty" : "occupied"
                };
                if (id == "seat_table1_b") fields["seat_opposite_id"] = "seat_table1_a";
                state.Apply(new Observation("obs_" + id, "npc_01", ObservationSourceType.ContinuousScan,
                    "visual_" + id, ObservationChannel.Visual, id, fields, new string[0], 1f, 1, 1d, 10d));
            }
            var self = seatedObserver ? new SelfSceneStateDto { posture = "seated", seat_entity_id = "seat_table1_a" } : null;
            var seats = new ContextSnapshotBuilder().Build(state, "有空位吗", 2d, selfState: self).SceneFacts.observed_seats;
            Assert.That(seats, Has.Length.EqualTo(4));
            Assert.That(new SceneFactsDto { self = self, observed_seats = seats }.IsValid, Is.True);
            Assert.That(seats.Single(x => x.seat_entity_id == "seat_table2_d").occupancy, Is.EqualTo("empty"));
            Assert.That(seats.Single(x => x.seat_entity_id == "seat_table2_d").occupant_entity_id, Is.Null);
            if (seatedObserver)
            {
                Assert.That(seats.Any(x => x.relation_to_self == "self"), Is.True);
                Assert.That(seats.Any(x => x.relation_to_self == "opposite"), Is.True);
            }
            state.Apply(new Observation("obs_fifth", "npc_01", ObservationSourceType.ContinuousScan,
                "visual_fifth", ObservationChannel.Visual, "seat_table3_a",
                new Dictionary<string, string> { ["visibility_state"] = "current_visible",
                    ["entity_kind"] = "Environment", ["semantic_tags"] = "seat", ["seat_occupancy"] = "empty" },
                new string[0], 1f, 1, 1d, 10d));
            Assert.That(new ContextSnapshotBuilder().Build(state, "有空位吗", 2d).SceneFacts.observed_seats, Has.Length.EqualTo(4));
            Assert.That(new SceneFactsDto { observed_seats = seats.Concat(new[] {
                new ObservedSeatDto { seat_entity_id = "seat_table3_a", occupancy = "empty" } }).ToArray() }.IsValid, Is.False);
        }

        private static IReadOnlyDictionary<string, string> ItemFields(string displayName, string tags) =>
            new Dictionary<string, string>
            {
                ["visibility_state"] = "current_visible",
                ["entity_kind"] = "Item",
                ["display_name"] = displayName,
                ["semantic_tags"] = tags,
                ["capabilities"] = "pick_up",
            };

        private static Observation ObservationFor(string sourceId, ObservationChannel channel, string subjectId, float confidence,
            double expiresAt, IReadOnlyDictionary<string, string> fields, double observedAt = 1d) =>
            new($"obs_{sourceId}", "npc_01", ObservationSourceType.GameplayEvent, sourceId, channel, subjectId,
                fields, new string[0], confidence, 1, observedAt, expiresAt);
    }
}
