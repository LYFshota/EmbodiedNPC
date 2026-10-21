using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class AgentEventFoundationTests
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
        public void DirectTarget_RoutesOnlyToMatchingReceiver()
        {
            AgentEventReceiver merchant = Receiver("npc_merchant");
            AgentEventReceiver bystander = Receiver("npc_bystander");
            AgentEventRouter router = Router(new[] { merchant, bystander });
            AgentEvent agentEvent = new("event_01", AgentEventTypes.PlayerUtterance, "player_01",
                "npc_merchant", string.Empty, null, 1d, 2);

            Assert.That(router.Route(agentEvent), Is.EqualTo(1));
            Assert.That(merchant.LastEvent, Is.SameAs(agentEvent));
            Assert.That(bystander.LastEvent, Is.Null);
        }

        [Test]
        public void UnregisteredReceiver_IsNotRoutableForDirectOrAreaEvents()
        {
            AgentEventReceiver receiver = Receiver("npc_merchant");
            WorldEntity area = Entity("area_counter", "cafe.area.counter", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(receiver.ReceiverEntity, area);
            AgentEventRouter router = Router(new[] { receiver }, new[]
            {
                new AgentEventAreaSubscription(AgentEventTypes.PlayerEnteredArea, area, receiver),
            }, registry);
            AgentEvent direct = new("event_direct", AgentEventTypes.PlayerUtterance, "player_01",
                receiver.EntityId, string.Empty, null, 1d);
            AgentEvent areaEvent = new("event_area", AgentEventTypes.PlayerEnteredArea, "player_01",
                string.Empty, area.EntityId, null, 2d);
            Assert.That(router.Route(direct), Is.EqualTo(1));
            Assert.That(router.Route(areaEvent), Is.EqualTo(1));
            Assert.That(registry.Unregister(receiver.ReceiverEntity), Is.True);

            Assert.That(router.Route(direct), Is.Zero);
            Assert.That(router.Route(areaEvent), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisabledRegisteredReceiver_IsNotRoutable(bool deactivateGameObject)
        {
            AgentEventReceiver receiver = Receiver("npc_merchant");
            EntityRegistry registry = ReadyRegistry(receiver.ReceiverEntity);
            AgentEventRouter router = Router(new[] { receiver }, registry: registry);
            AgentEvent direct = new("event_direct", AgentEventTypes.PlayerUtterance, "player_01",
                receiver.EntityId, string.Empty, null, 1d);
            Assert.That(router.Route(direct), Is.EqualTo(1));
            if (deactivateGameObject) receiver.gameObject.SetActive(false);
            else receiver.enabled = false;

            Assert.That(router.Route(direct), Is.Zero);
            Assert.That(receiver.Receive(direct), Is.False);
        }

        [Test]
        public void RouterWithoutReadyRegistry_FailsClosed()
        {
            AgentEventReceiver receiver = Receiver("npc_merchant");
            AgentEventRouter router = Component<AgentEventRouter>("EventRouter");
            router.Configure(new[] { receiver }, null);
            AgentEvent direct = new("event_direct", AgentEventTypes.PlayerUtterance, "player_01",
                receiver.EntityId, string.Empty, null, 1d);

            Assert.That(router.Route(direct), Is.Zero);
        }

        [Test]
        public void PlayerAreaTransition_RoutesSubscribedLeaveAndEnterWithoutDuplicates()
        {
            WorldEntity player = Entity("player_01", "character.player", EntityKind.Agent, AgentRole.Player);
            WorldEntity entrance = Entity("area_cafe_entrance", "cafe.area.entrance", EntityKind.Area);
            WorldEntity counter = Entity("area_cafe_counter", "cafe.area.counter", EntityKind.Area);
            EntityRegistry registry = ReadyRegistry(player, entrance, counter);
            AreaMembershipService membership = Component<AreaMembershipService>("AreaMembership");
            membership.Configure(registry);
            AgentEventReceiver merchant = Receiver("npc_cafe_01");
            registry.Register(merchant.ReceiverEntity);
            AgentEventRouter router = Router(new[] { merchant }, new[]
            {
                new AgentEventAreaSubscription(AgentEventTypes.PlayerLeftArea, entrance, merchant),
                new AgentEventAreaSubscription(AgentEventTypes.PlayerEnteredArea, counter, merchant),
            }, registry);
            AreaMembershipAgentEventBridge bridge = Component<AreaMembershipAgentEventBridge>("AreaBridge");
            bridge.Configure(membership, router);

            Assert.That(membership.TryEnterArea(player, entrance, out _), Is.True);
            Assert.That(membership.TryEnterArea(player, counter, out _), Is.True);
            Assert.That(membership.TryEnterArea(player, counter, out _), Is.True);

            Assert.That(merchant.RecentEvents, Has.Count.EqualTo(2));
            Assert.That(merchant.RecentEvents[0].EventType, Is.EqualTo(AgentEventTypes.PlayerLeftArea));
            Assert.That(merchant.RecentEvents[0].AreaEntityId, Is.EqualTo("area_cafe_entrance"));
            Assert.That(merchant.LastEvent.EventType, Is.EqualTo(AgentEventTypes.PlayerEnteredArea));
            Assert.That(merchant.LastEvent.SourceEntityId, Is.EqualTo("player_01"));
            Assert.That(merchant.LastEvent.AreaEntityId, Is.EqualTo("area_cafe_counter"));
        }
        [Test]
        public void AuthoritativeGoalOutcomes_MapToCompletedAndFailedEvents()
        {
            AgentEventReceiver actor = Receiver("npc_cafe_01");
            AgentEventRouter router = Router(new[] { actor });
            ActionOutcomeAgentEventBridge bridge = Component<ActionOutcomeAgentEventBridge>("ActionBridge");
            bridge.Configure(null, router);
            GoalOutcome succeeded = new("outcome_01", "proposal_01", 1, "trace_01", "open_door",
                "npc_cafe_01", "door_01", string.Empty, PlanExecutionStatus.Succeeded, string.Empty, 11, 20d);
            GoalOutcome failed = new("outcome_02", "proposal_02", 1, "trace_02", "bring_object",
                "npc_cafe_01", "cup_01", "player_01", PlanExecutionStatus.Failed, "PATH_INVALID", 11, 21d);

            Assert.That(bridge.Publish(succeeded), Is.EqualTo(1));
            Assert.That(bridge.Publish(failed), Is.EqualTo(1));

            Assert.That(actor.RecentEvents, Has.Count.EqualTo(2));
            Assert.That(actor.RecentEvents[0].EventType, Is.EqualTo(AgentEventTypes.ActionCompleted));
            Assert.That(actor.RecentEvents[0].TryGetPayload(AgentEventPayloadKeys.ActionId, out string actionId), Is.True);
            Assert.That(actionId, Is.EqualTo("open_door"));
            Assert.That(actor.LastEvent.EventType, Is.EqualTo(AgentEventTypes.ActionFailed));
            Assert.That(actor.LastEvent.TryGetPayload(AgentEventPayloadKeys.FailureCode, out string failure), Is.True);
            Assert.That(failure, Is.EqualTo("PATH_INVALID"));
            Assert.That(actor.LastEvent.TryGetPayload(AgentEventPayloadKeys.DestinationEntityId,
                out string destination), Is.True);
            Assert.That(destination, Is.EqualTo("player_01"));
            Assert.That(actor.LastEvent.TryGetPayload(AgentEventPayloadKeys.ProposalId,
                out string proposalId), Is.True);
            Assert.That(proposalId, Is.EqualTo("proposal_02"));
            Assert.That(actor.LastEvent.TryGetPayload(AgentEventPayloadKeys.ProposalRevision,
                out string proposalRevision), Is.True);
            Assert.That(proposalRevision, Is.EqualTo("1"));
            Assert.That(actor.LastEvent.TryGetPayload(AgentEventPayloadKeys.TraceId,
                out string traceId), Is.True);
            Assert.That(traceId, Is.EqualTo("trace_02"));
        }

        [Test]
        public void StaticProfile_UsesExactRuleThenIgnoreFallback()
        {
            EventInterestProfile profile = Profile(new EventInterestRule(
                AgentEventTypes.PlayerEnteredArea, EventInterestDecision.Wake, "area_cafe_counter"));
            AgentEvent matching = new("event_match", AgentEventTypes.PlayerEnteredArea, "player_01",
                string.Empty, "area_cafe_counter", null, 1d);
            AgentEvent unmatched = new("event_other", AgentEventTypes.PlayerLeftArea, "player_01",
                string.Empty, "area_cafe_entrance", null, 2d);

            Assert.That(EventInterestEvaluator.Evaluate(matching, "npc_cafe_01", profile),
                Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(EventInterestEvaluator.Evaluate(unmatched, "npc_cafe_01", profile),
                Is.EqualTo(EventInterestDecision.Ignore));
        }

        [Test]
        public void DirectUtterance_WakesOnlyTargetSelf()
        {
            EventInterestProfile profile = Profile();
            AgentEvent utterance = new("event_utterance", AgentEventTypes.PlayerUtterance, "player_01",
                "npc_cafe_01", string.Empty, null, 1d);

            Assert.That(EventInterestEvaluator.Evaluate(utterance, "npc_cafe_01", profile),
                Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(EventInterestEvaluator.Evaluate(utterance, "npc_cafe_regular_01", profile),
                Is.EqualTo(EventInterestDecision.Ignore));
            Assert.That(EventInterestEvaluator.Evaluate(utterance, "npc_cafe_01", null),
                Is.EqualTo(EventInterestDecision.Ignore));
        }

        [Test]
        public void SelfActionMandatoryRules_ReturnRecordAndWake()
        {
            EventInterestProfile profile = Profile(
                new EventInterestRule(AgentEventTypes.ActionCompleted, EventInterestDecision.Ignore,
                    configuredRelevance: EventInterestRelevance.SourceSelf),
                new EventInterestRule(AgentEventTypes.ActionFailed, EventInterestDecision.Ignore,
                    configuredRelevance: EventInterestRelevance.SourceSelf));
            AgentEvent completed = new("event_completed", AgentEventTypes.ActionCompleted, "npc_cafe_01",
                "npc_cafe_01", string.Empty, null, 1d);
            AgentEvent failed = new("event_failed", AgentEventTypes.ActionFailed, "npc_cafe_01",
                "npc_cafe_01", string.Empty, null, 2d);

            Assert.That(EventInterestEvaluator.Evaluate(completed, "npc_cafe_01", profile),
                Is.EqualTo(EventInterestDecision.Record));
            Assert.That(EventInterestEvaluator.Evaluate(failed, "npc_cafe_01", profile),
                Is.EqualTo(EventInterestDecision.Wake));
        }

        [Test]
        public void BehaviorModeOverride_CanReplaceStaticDecisionUpOrDown()
        {
            EventInterestProfile profile = Profile(new EventInterestRule(
                AgentEventTypes.PlayerEnteredArea, EventInterestDecision.Wake, "area_cafe_counter"));
            BehaviorModeInterestOverrideProfile overrides = ModeOverrides(
                new BehaviorModeInterestOverride(BehaviorModeIds.Working,
                    new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                        EventInterestDecision.Wake, "area_cafe_counter")),
                new BehaviorModeInterestOverride(BehaviorModeIds.OffDuty,
                    new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                        EventInterestDecision.Record, "area_cafe_counter")));
            AgentEvent counterEvent = new("event_counter", AgentEventTypes.PlayerEnteredArea, "player_01",
                string.Empty, "area_cafe_counter", null, 1d);

            Assert.That(EventInterestEvaluator.Evaluate(counterEvent, "npc_cafe_01", profile,
                BehaviorModeIds.Working, overrides), Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(EventInterestEvaluator.Evaluate(counterEvent, "npc_cafe_01", profile,
                BehaviorModeIds.OffDuty, overrides), Is.EqualTo(EventInterestDecision.Record));
        }

        [Test]
        public void OffDutyOverride_CannotLowerMandatoryDirectOrSelfEvents()
        {
            EventInterestProfile profile = Profile();
            BehaviorModeInterestOverrideProfile overrides = ModeOverrides(
                new BehaviorModeInterestOverride(BehaviorModeIds.OffDuty,
                    new EventInterestRule(AgentEventTypes.PlayerUtterance, EventInterestDecision.Ignore,
                        configuredRelevance: EventInterestRelevance.TargetSelf),
                    new EventInterestRule(AgentEventTypes.ActionFailed, EventInterestDecision.Ignore,
                        configuredRelevance: EventInterestRelevance.SourceSelf),
                    new EventInterestRule(AgentEventTypes.ActionCompleted, EventInterestDecision.Ignore,
                        configuredRelevance: EventInterestRelevance.SourceSelf)));
            AgentEvent utterance = new("event_direct", AgentEventTypes.PlayerUtterance, "player_01",
                "npc_cafe_01", string.Empty, null, 1d);
            AgentEvent failed = new("event_failed", AgentEventTypes.ActionFailed, "npc_cafe_01",
                string.Empty, string.Empty, null, 2d);
            AgentEvent completed = new("event_completed", AgentEventTypes.ActionCompleted, "npc_cafe_01",
                string.Empty, string.Empty, null, 3d);

            Assert.That(EventInterestEvaluator.Evaluate(utterance, "npc_cafe_01", profile,
                BehaviorModeIds.OffDuty, overrides), Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(EventInterestEvaluator.Evaluate(failed, "npc_cafe_01", profile,
                BehaviorModeIds.OffDuty, overrides), Is.EqualTo(EventInterestDecision.Wake));
            Assert.That(EventInterestEvaluator.Evaluate(completed, "npc_cafe_01", profile,
                BehaviorModeIds.OffDuty, overrides), Is.EqualTo(EventInterestDecision.Record));
        }

        [Test]
        public void MissingBehaviorMode_KeepsStaticProfileDecision()
        {
            EventInterestProfile profile = Profile(new EventInterestRule(
                AgentEventTypes.PlayerEnteredArea, EventInterestDecision.Record, "area_cafe_counter"));
            BehaviorModeInterestOverrideProfile overrides = ModeOverrides(
                new BehaviorModeInterestOverride(BehaviorModeIds.OffDuty,
                    new EventInterestRule(AgentEventTypes.PlayerEnteredArea,
                        EventInterestDecision.Ignore, "area_cafe_counter")));
            AgentEvent counterEvent = new("event_counter", AgentEventTypes.PlayerEnteredArea, "player_01",
                string.Empty, "area_cafe_counter", null, 1d);

            Assert.That(EventInterestEvaluator.Evaluate(counterEvent, "npc_regular", profile,
                modeOverrides: overrides), Is.EqualTo(EventInterestDecision.Record));
        }

        [Test]
        public void CurrentBehaviorMode_ExplicitlySwitchesStableModeIds()
        {
            CurrentBehaviorMode mode = Component<CurrentBehaviorMode>("BehaviorMode");
            string transition = string.Empty;
            mode.ModeChanged += (previous, current) => transition = $"{previous}->{current}";

            Assert.That(mode.TryValidateConfiguration(out string error), Is.True, error);
            Assert.That(mode.CurrentModeId, Is.EqualTo(BehaviorModeIds.Working));
            Assert.That(mode.TrySetMode("Off Duty"), Is.False);
            Assert.That(mode.TrySetMode(BehaviorModeIds.OffDuty), Is.True);
            Assert.That(mode.CurrentModeId, Is.EqualTo(BehaviorModeIds.OffDuty));
            Assert.That(transition, Is.EqualTo("working->off_duty"));
        }

        private AgentEventRouter Router(AgentEventReceiver[] receivers,
            AgentEventAreaSubscription[] subscriptions = null, EntityRegistry registry = null)
        {
            registry ??= ReadyRegistry(receivers.Select(receiver => receiver.ReceiverEntity).ToArray());
            AgentEventRouter router = Component<AgentEventRouter>("EventRouter");
            router.Configure(receivers, subscriptions, registry);
            Assert.That(router.TryValidateConfiguration(out string error), Is.True, error);
            return router;
        }

        private AgentEventReceiver Receiver(string entityId)
        {
            WorldEntity entity = Entity(entityId, "character.npc", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = entity.gameObject.AddComponent<AgentEventReceiver>();
            receiver.Configure(entity);
            return receiver;
        }

        private EventInterestProfile Profile(params EventInterestRule[] rules)
        {
            EventInterestProfile profile = ScriptableObject.CreateInstance<EventInterestProfile>();
            profile.Configure(EventInterestDecision.Ignore, rules);
            profiles.Add(profile);
            Assert.That(profile.TryValidateConfiguration(out string error), Is.True, error);
            return profile;
        }

        private BehaviorModeInterestOverrideProfile ModeOverrides(
            params BehaviorModeInterestOverride[] overrides)
        {
            BehaviorModeInterestOverrideProfile profile =
                ScriptableObject.CreateInstance<BehaviorModeInterestOverrideProfile>();
            profile.Configure(overrides);
            profiles.Add(profile);
            Assert.That(profile.TryValidateConfiguration(out string error), Is.True, error);
            return profile;
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
