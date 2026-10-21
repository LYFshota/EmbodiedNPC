using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.Events;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class VisualObservationAgentEventBridgeTests
    {
        private readonly List<GameObject> objects = new();
        private VisualObservationAgentEventBridge bridge;
        private WorldEntity merchant;
        private WorldEntity player;
        private WorldEntity secondPlayer;
        private WorldEntity bystander;

        [SetUp]
        public void SetUp()
        {
            merchant = Entity("npc_merchant", AgentRole.Npc);
            player = Entity("player_01", AgentRole.Player);
            secondPlayer = Entity("player_02", AgentRole.Player);
            bystander = Entity("npc_bystander", AgentRole.Npc);
            var registry = Component<EntityRegistry>("Registry");
            SetField(registry, "autoDiscoverSceneEntities", false);
            SetField(registry, "entities", new List<WorldEntity> { merchant, player, secondPlayer, bystander });
            registry.RebuildRegistry();
            Assert.That(registry.IsReady, Is.True);
            bridge = merchant.gameObject.AddComponent<VisualObservationAgentEventBridge>();
            bridge.Configure(merchant, merchant.GetComponent<PerceivedStateStore>(), registry, null, 20f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void FirstVisualPresence_EmitsBecameVisibleOnce()
        {
            ObservationApplyResult visual = Visual(player, 1d);
            Assert.That(visual.Observation.TraceId, Is.Null);
            Assert.That(bridge.TryProject(visual, out AgentEvent first), Is.True);
            Assert.That(first.EventType, Is.EqualTo(AgentEventTypes.EntityBecameVisible));
            Assert.That(first.SourceEntityId, Is.EqualTo(player.EntityId));
            Assert.That(first.TargetEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(first.WorldRevision, Is.EqualTo(7));
            Assert.That(first.SourceObservationId, Is.EqualTo(visual.Observation.ObservationId));
            Assert.That(first.TraceId, Is.Not.Empty);
            Assert.That(first.TraceId, Is.Not.EqualTo(first.EventId));
            CognitiveEventDto projected = CognitiveEventDto.From(first);
            Assert.That(projected.source_observation_id, Is.EqualTo(visual.Observation.ObservationId));
            Assert.That(projected.trace_id, Is.EqualTo(first.TraceId));
        }

        [Test]
        public void ContinuousVisibleRefresh_DoesNotEmitAgain()
        {
            Assert.That(bridge.TryProject(Visual(player, 1d), out _), Is.True);
            Assert.That(bridge.TryProject(Visual(player, 2d), out _), Is.False);
            Assert.That(bridge.TryProject(Visual(player, 30d), out _), Is.False);
        }

        [Test]
        public void ShortVisualLoss_ReappearsWithinThreshold_NoNewEvent()
        {
            Assert.That(bridge.TryProject(Visual(player, 1d), out _), Is.True);
            Assert.That(bridge.TryProject(Visual(player, 5d, PerceptionLifecycleState.Lost), out _), Is.False);
            Assert.That(bridge.TryProject(Visual(player, 20d), out _), Is.False);
        }

        [Test]
        public void LongVisualLoss_ReappearsAfterThreshold_EmitsAgain()
        {
            Assert.That(bridge.TryProject(Visual(player, 1d), out AgentEvent first), Is.True);
            Assert.That(bridge.TryProject(Visual(player, 5d, PerceptionLifecycleState.Lost), out _), Is.False);
            Assert.That(bridge.TryProject(Visual(player, 25d), out AgentEvent second), Is.True);
            Assert.That(second.EventId, Is.Not.EqualTo(first.EventId));
        }

        [Test]
        public void LateLostObservation_DoesNotRestartReengagementThreshold()
        {
            Assert.That(bridge.TryProject(Visual(player, 10d), out AgentEvent first), Is.True);
            Assert.That(bridge.TryProject(Visual(player, 25d, PerceptionLifecycleState.Lost), out _), Is.False);
            Assert.That(bridge.TryProject(Visual(player, 31d), out AgentEvent second), Is.True);
            Assert.That(second.EventId, Is.Not.EqualTo(first.EventId));
        }

        [Test]
        public void DifferentPlayers_HaveIndependentPresenceState()
        {
            Assert.That(bridge.TryProject(Visual(player, 1d), out _), Is.True);
            Assert.That(bridge.TryProject(Visual(secondPlayer, 2d), out AgentEvent second), Is.True);
            Assert.That(second.SourceEntityId, Is.EqualTo(secondPlayer.EntityId));
        }

        [Test]
        public void NonPlayerVisualObservation_DoesNotUseDemoPlayerPath()
        {
            Assert.That(bridge.TryProject(Visual(bystander, 1d), out _), Is.False);
            Assert.That(bridge.TryProject(Visual(player, 2d, PerceptionLifecycleState.Tracked,
                ObservationChannel.Proximity), out _), Is.False);
            Assert.That(bridge.TryProject(new ObservationApplyResult(ObservationApplyCode.Unchanged,
                Visual(player, 3d).Observation, 1), out _), Is.False);
        }

        [Test]
        public void MerchantBecameVisibleInterest_RecordsWithoutWaking()
        {
            Assert.That(Decision(AgentEventTypes.EntityBecameVisible), Is.EqualTo(EventInterestDecision.Record));
        }

        [Test]
        public void MerchantCounterEntered_NoLongerWakes()
        {
            Assert.That(Decision(AgentEventTypes.PlayerEnteredArea, "area_cafe_counter"),
                Is.EqualTo(EventInterestDecision.Record));
        }

        [Test]
        public void DirectPlayerUtterance_StillMandatoryWake()
        {
            Assert.That(Decision(AgentEventTypes.PlayerUtterance), Is.EqualTo(EventInterestDecision.Wake));
        }

        [Test]
        public void VisualPlayerEvidence_ProjectsSupportedWireKind()
        {
            var fields = new Dictionary<string, string> {
                ["entity_kind"] = "Agent", ["agent_role"] = "Player", ["visibility_state"] = "current_visible"
            };
            var observation = new Observation("player_visual", merchant.EntityId,
                ObservationSourceType.ContinuousScan, "visual:player_01", ObservationChannel.Visual,
                player.EntityId, fields, new string[0], 1f, 0, 1d, 10d);
            var context = new ContextSnapshot("context", merchant.EntityId, 1, null,
                new[] { observation }, 0, 2d);
            Assert.That(context.ToPerceptionDto().entities[0].kind,
                Is.EqualTo("player"));
        }

        private EventInterestDecision Decision(string eventType, string area = "")
        {
            const string root = "Assets/CafeDemo/Profiles/";
            var profile = AssetDatabase.LoadAssetAtPath<EventInterestProfile>(root + "MerchantEventInterestProfile.asset");
            var overrides = AssetDatabase.LoadAssetAtPath<BehaviorModeInterestOverrideProfile>(
                root + "MerchantBehaviorModeInterestOverrides.asset");
            Assert.That(profile, Is.Not.Null);
            Assert.That(overrides, Is.Not.Null);
            var agentEvent = new AgentEvent("test_event", eventType, player.EntityId, merchant.EntityId,
                area, null, 1d);
            return EventInterestEvaluator.Evaluate(agentEvent, merchant.EntityId, profile,
                BehaviorModeIds.Working, overrides);
        }

        private ObservationApplyResult Visual(WorldEntity subject, double at,
            PerceptionLifecycleState state = PerceptionLifecycleState.Tracked,
            ObservationChannel channel = ObservationChannel.Visual)
        {
            var fields = new Dictionary<string, string> {
                ["visibility_state"] = state == PerceptionLifecycleState.Tracked ? "current_visible" : "lost"
            };
            var observation = new Observation("visual_" + subject.EntityId + "_" + at,
                merchant.EntityId, ObservationSourceType.ContinuousScan, "visual:" + subject.EntityId,
                channel, subject.EntityId, fields, new string[0], 1f, 7, at,
                double.PositiveInfinity, state);
            return new ObservationApplyResult(ObservationApplyCode.Applied, observation, 1);
        }

        private WorldEntity Entity(string id, AgentRole role)
        {
            var entity = Component<WorldEntity>(id);
            entity.Configure(id, "character." + id, EntityKind.Agent, role);
            return entity;
        }

        private T Component<T>(string name) where T : Component
        {
            var value = new GameObject(name);
            objects.Add(value);
            return value.AddComponent<T>();
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
