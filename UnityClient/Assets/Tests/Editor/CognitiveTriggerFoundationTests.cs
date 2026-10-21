using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class CognitiveTriggerFoundationTests
    {
        private readonly List<GameObject> objects = new();
        private readonly List<ScriptableObject> profiles = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
            foreach (ScriptableObject value in profiles)
                if (value != null) Object.DestroyImmediate(value);
            profiles.Clear();
        }

        [Test]
        public void Wake_CreatesLightweightEventTriggerWithUniqueId()
        {
            EventInterestProfile profile = Profile(
                new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                    EventInterestDecision.Wake, "area_cafe_counter"));
            AgentEventReceiver receiver = Receiver(profile);
            WakeCognitiveTriggerBridge bridge = Bridge(receiver);
            int createdCount = 0;
            bridge.CognitiveTriggerCreated += _ => createdCount++;
            AgentEvent first = Event("event_counter_01", AgentEventTypes.PlayerEnteredArea,
                "area_cafe_counter");
            AgentEvent second = Event("event_counter_02", AgentEventTypes.PlayerEnteredArea,
                "area_cafe_counter");

            Assert.That(receiver.Receive(first), Is.True);
            CognitiveTrigger firstTrigger = bridge.LastTrigger;
            Assert.That(firstTrigger, Is.Not.Null);
            Assert.That(firstTrigger.TriggerId, Is.Not.Empty);
            Assert.That(firstTrigger.TriggerType, Is.EqualTo(CognitiveTriggerTypes.Event));
            Assert.That(firstTrigger.AgentEntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(firstTrigger.SourceEventId, Is.EqualTo(first.EventId));
            Assert.That(firstTrigger.CreatedAt, Is.GreaterThan(0d));
            Assert.That(firstTrigger.Reason, Is.EqualTo(first.EventType));

            Assert.That(receiver.Receive(second), Is.True);
            Assert.That(bridge.LastTrigger.TriggerId, Is.Not.EqualTo(firstTrigger.TriggerId));
            Assert.That(bridge.LastTrigger.SourceEventId, Is.EqualTo(second.EventId));
            Assert.That(createdCount, Is.EqualTo(2));
        }

        [Test]
        public void RecordAndIgnore_DoNotCreateTrigger()
        {
            EventInterestProfile profile = Profile(
                new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                    EventInterestDecision.Record, "area_record"),
                new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                    EventInterestDecision.Ignore, "area_ignore"));
            AgentEventReceiver receiver = Receiver(profile);
            WakeCognitiveTriggerBridge bridge = Bridge(receiver);
            int createdCount = 0;
            bridge.CognitiveTriggerCreated += _ => createdCount++;

            Assert.That(receiver.Receive(Event("event_record", AgentEventTypes.PlayerEnteredArea,
                "area_record")), Is.True);
            Assert.That(receiver.Receive(Event("event_ignore", AgentEventTypes.PlayerEnteredArea,
                "area_ignore")), Is.True);

            Assert.That(createdCount, Is.Zero);
            Assert.That(bridge.LastTrigger, Is.Null);
        }

        [Test]
        public void FastPath_DoesNotCreateNormalTrigger()
        {
            EventInterestProfile profile = Profile(
                new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                    EventInterestDecision.FastPath, "area_fast"));
            AgentEventReceiver receiver = Receiver(profile);
            WakeCognitiveTriggerBridge bridge = Bridge(receiver);

            Assert.That(receiver.Receive(Event("event_fast", AgentEventTypes.PlayerEnteredArea,
                "area_fast")), Is.True);
            Assert.That(receiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.FastPath));
            Assert.That(bridge.LastTrigger, Is.Null);
        }

        [Test]
        public void OffDutyDirectUtterance_MandatoryWakeCreatesTrigger()
        {
            EventInterestProfile profile = Profile();
            CurrentBehaviorMode mode = Component<CurrentBehaviorMode>("BehaviorMode");
            Assert.That(mode.TrySetMode(BehaviorModeIds.OffDuty), Is.True);
            BehaviorModeInterestOverrideProfile overrides = ModeOverrides(
                new BehaviorModeInterestOverride(BehaviorModeIds.OffDuty,
                    new EventInterestRule(AgentEventTypes.PlayerUtterance, EventInterestDecision.Ignore,
                        configuredRelevance: EventInterestRelevance.TargetSelf)));
            AgentEventReceiver receiver = Receiver(profile, mode, overrides);
            WakeCognitiveTriggerBridge bridge = Bridge(receiver);
            AgentEvent utterance = new("event_direct", AgentEventTypes.PlayerUtterance, "player_01",
                "npc_cafe_01", string.Empty, null, 1d);

            Assert.That(receiver.Receive(utterance), Is.True);
            Assert.That(receiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(bridge.LastTrigger, Is.Not.Null);
            Assert.That(bridge.LastTrigger.SourceEventId, Is.EqualTo(utterance.EventId));
        }

        private AgentEventReceiver Receiver(EventInterestProfile profile,
            CurrentBehaviorMode mode = null, BehaviorModeInterestOverrideProfile overrides = null)
        {
            WorldEntity entity = Component<WorldEntity>("Merchant");
            entity.Configure("npc_cafe_01", "character.npc.merchant", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = entity.gameObject.AddComponent<AgentEventReceiver>();
            receiver.Configure(entity, profile: profile, behaviorMode: mode,
                behaviorModeOverrides: overrides);
            return receiver;
        }

        private WakeCognitiveTriggerBridge Bridge(AgentEventReceiver receiver)
        {
            WakeCognitiveTriggerBridge bridge = receiver.gameObject.AddComponent<WakeCognitiveTriggerBridge>();
            bridge.Configure(receiver);
            Assert.That(bridge.TryValidateConfiguration(out string error), Is.True, error);
            return bridge;
        }

        private EventInterestProfile Profile(params EventInterestRule[] rules)
        {
            EventInterestProfile profile = ScriptableObject.CreateInstance<EventInterestProfile>();
            profile.Configure(EventInterestDecision.Ignore, rules);
            profiles.Add(profile);
            return profile;
        }

        private BehaviorModeInterestOverrideProfile ModeOverrides(
            params BehaviorModeInterestOverride[] overrides)
        {
            BehaviorModeInterestOverrideProfile profile =
                ScriptableObject.CreateInstance<BehaviorModeInterestOverrideProfile>();
            profile.Configure(overrides);
            profiles.Add(profile);
            return profile;
        }

        private AgentEvent Event(string id, string type, string areaEntityId) =>
            new(id, type, "player_01", string.Empty, areaEntityId, null, 1d);

        private T Component<T>(string name) where T : Component
        {
            GameObject value = new(name);
            objects.Add(value);
            return value.AddComponent<T>();
        }
    }
}
