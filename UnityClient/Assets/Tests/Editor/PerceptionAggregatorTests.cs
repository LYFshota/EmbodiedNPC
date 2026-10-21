using System.Collections.Generic;
using EmbodiedNPC.Perception;
using NUnit.Framework;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class PerceptionAggregatorTests
    {
        [Test]
        public void SameEvent_CreatesIndependentObserverLocalObservations()
        {
            GameplayEvent gameplayEvent = Event("evt_001", 3);
            var first = new PerceptionAggregator("npc_a");
            var second = new PerceptionAggregator("npc_b");

            ObservationApplyResult firstResult = first.Apply(ObservationFor(gameplayEvent, "npc_a", "cup_01", "visible"));
            ObservationApplyResult secondResult = second.Apply(ObservationFor(gameplayEvent, "npc_b", "cup_01", "heard"));

            Assert.That(firstResult.IsApplied, Is.True);
            Assert.That(secondResult.IsApplied, Is.True);
            Assert.That(first.Entities["cup_01"].KnownFields["evidence"], Is.EqualTo("visible"));
            Assert.That(second.Entities["cup_01"].KnownFields["evidence"], Is.EqualTo("heard"));
        }

        [Test]
        public void DuplicateEventForObserver_DoesNotCreateAnotherObservation()
        {
            var state = new PerceptionAggregator("npc_a");
            Observation observation = ObservationFor(Event("evt_duplicate", 2), "npc_a", "cup_01", "visible");

            state.Apply(observation);
            ObservationApplyResult duplicate = state.Apply(ObservationFor(Event("evt_duplicate", 2), "npc_a", "cup_01", "visible"));

            Assert.That(duplicate.Code, Is.EqualTo(ObservationApplyCode.Duplicate));
            Assert.That(state.Observations.Count, Is.EqualTo(1));
            Assert.That(state.DuplicateCount, Is.EqualTo(1));
        }

        [Test]
        public void OlderWorldRevision_CannotOverwriteNewerPerceivedRecord()
        {
            var state = new PerceptionAggregator("npc_a");
            state.Apply(ObservationFor(Event("evt_new", 5), "npc_a", "cup_01", "new"));

            ObservationApplyResult stale = state.Apply(ObservationFor(Event("evt_old", 4), "npc_a", "cup_01", "old"));

            Assert.That(stale.Code, Is.EqualTo(ObservationApplyCode.StaleWorldRevision));
            Assert.That(state.Entities["cup_01"].KnownFields["evidence"], Is.EqualTo("new"));
            Assert.That(state.StaleCount, Is.EqualTo(1));
        }

        [Test]
        public void UnknownFields_RemainUnknownUntilDirectlyObserved()
        {
            var state = new PerceptionAggregator("npc_a");
            Observation observation = new("obs_unknown", "npc_a", ObservationSourceType.GameplayEvent,
                "evt_unknown", ObservationChannel.Manual, "door_01",
                new Dictionary<string, string> { ["event_type"] = "door_sound" },
                new[] { "actor_id", "exact_position" }, 0.5f, 1, 1d, 5d);

            state.Apply(observation);

            PerceivedEntityRecord record = state.Entities["door_01"];
            Assert.That(record.KnownFields.ContainsKey("actor_id"), Is.False);
            Assert.That(record.UnknownFields, Does.Contain("actor_id"));
            Assert.That(record.UnknownFields, Does.Contain("exact_position"));
        }

        [Test]
        public void Observation_PreservesOriginalEventIdentityAndOccurrenceTime()
        {
            Observation observation = new("obs_audio", "npc_a", ObservationSourceType.GameplayEvent,
                "stimulus_17", ObservationChannel.Audio, string.Empty,
                new Dictionary<string, string> { ["event_type"] = "DoorOpened" },
                new[] { "actor_entity_id" }, 0.7f, 5, 12d, 20d,
                PerceptionLifecycleState.Detected, "evt_door_17", 10d);

            Assert.That(observation.SourceId, Is.EqualTo("stimulus_17"));
            Assert.That(observation.SourceEventId, Is.EqualTo("evt_door_17"));
            Assert.That(observation.OccurredAt, Is.EqualTo(10d));
            Assert.That(observation.ObservedAt, Is.EqualTo(12d));
        }

        [Test]
        public void ContinuousVisualSource_RefreshesCurrentObservationWithoutBecomingDuplicate()
        {
            var state = new PerceptionAggregator("npc_a");
            Observation first = new("obs_visual_cup", "npc_a", ObservationSourceType.ContinuousScan,
                "visual:cup_01", ObservationChannel.Visual, "cup_01",
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }, new string[0],
                1f, 2, 1d, double.PositiveInfinity, PerceptionLifecycleState.Tracked);
            Observation refreshed = new("obs_visual_cup", "npc_a", ObservationSourceType.ContinuousScan,
                "visual:cup_01", ObservationChannel.Visual, "cup_01",
                new Dictionary<string, string> { ["visibility_state"] = "lost" }, new string[0],
                1f, 2, 2d, double.PositiveInfinity, PerceptionLifecycleState.Lost);

            state.ApplyOrRefresh(first);
            ObservationApplyResult result = state.ApplyOrRefresh(refreshed);

            Assert.That(result.IsApplied, Is.True);
            Assert.That(state.Observations.Count, Is.EqualTo(1));
            Assert.That(state.DuplicateCount, Is.EqualTo(0));
            Assert.That(state.Entities["cup_01"].LifecycleState, Is.EqualTo(PerceptionLifecycleState.Lost));
            Assert.That(state.Entities["cup_01"].KnownFields["visibility_state"], Is.EqualTo("lost"));
        }

        [Test]
        public void UnchangedContinuousRefresh_DoesNotAdvancePerceptionRevision()
        {
            var state = new PerceptionAggregator("npc_a");
            Observation first = new("obs_visual_cup", "npc_a", ObservationSourceType.ContinuousScan,
                "visual:cup_01", ObservationChannel.Visual, "cup_01",
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }, new string[0],
                1f, 2, 1d, double.PositiveInfinity, PerceptionLifecycleState.Tracked);
            Observation unchanged = new("obs_visual_cup", "npc_a", ObservationSourceType.ContinuousScan,
                "visual:cup_01", ObservationChannel.Visual, "cup_01",
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }, new string[0],
                1f, 2, 2d, double.PositiveInfinity, PerceptionLifecycleState.Tracked);

            state.ApplyOrRefresh(first);
            ObservationApplyResult result = state.ApplyOrRefresh(unchanged);

            Assert.That(result.Code, Is.EqualTo(ObservationApplyCode.Unchanged));
            Assert.That(state.PerceptionRevision, Is.EqualTo(1));
            Assert.That(state.Entities["cup_01"].LastObservedAt, Is.EqualTo(2d));
        }

        private static GameplayEvent Event(string id, int revision) =>
            new(id, GameplayEventType.Manual, "player_01", "cup_01", "trace", default, revision, 1d);

        private static Observation ObservationFor(GameplayEvent gameplayEvent, string observer, string subject, string evidence) =>
            new($"obs_{observer}_{gameplayEvent.EventId}", observer, ObservationSourceType.GameplayEvent,
                gameplayEvent.EventId, ObservationChannel.Manual, subject,
                new Dictionary<string, string> { ["evidence"] = evidence }, new[] { "actor_id" },
                0.8f, gameplayEvent.WorldRevision, 1d, 5d);
    }
}
