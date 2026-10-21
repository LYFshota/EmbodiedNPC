using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class AttentionControllerTests
    {
        private readonly List<GameObject> objects = new();
        private WorldEntity owner, player;
        private PerceivedStateStore state;
        private EntityRegistry registry;
        private AttentionController attention;
        private MotionChannelCoordinator channels;
        private int sequence;
        private static void Field(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        private WorldEntity Entity(string id, AgentRole role)
        {
            var go = new GameObject(id); objects.Add(go);
            var entity = go.AddComponent<WorldEntity>(); entity.Configure(id, id, EntityKind.Agent, role); return entity;
        }
        [SetUp] public void SetUp()
        {
            owner = Entity("attention_owner", AgentRole.Npc); player = Entity("attention_player", AgentRole.Player);
            state = owner.gameObject.AddComponent<PerceivedStateStore>();
            // EditMode does not run Awake. Install the real observer-local aggregator explicitly.
            Field(state, "observer", owner); Field(state, "aggregator", new PerceptionAggregator(owner.EntityId));
            channels = owner.gameObject.AddComponent<MotionChannelCoordinator>();
            var reg = new GameObject("Registry"); objects.Add(reg); registry = reg.AddComponent<EntityRegistry>();
            Field(registry, "autoDiscoverSceneEntities", false);
            Field(registry, "entities", new List<WorldEntity> { owner, player }); registry.RebuildRegistry();
            var body = new GameObject("Visual"); objects.Add(body); body.transform.SetParent(owner.transform, false);
            attention = body.AddComponent<AttentionController>();
            attention.Configure(owner, state, registry, null, null, channels, body.transform);
            Move(3.9f, 0);
        }
        [TearDown] public void TearDown()
        { for (int i = objects.Count - 1; i >= 0; i--) Object.DestroyImmediate(objects[i]); objects.Clear(); }
        private void Move(float distance, float yaw) => player.transform.position =
            Quaternion.Euler(0, yaw, 0) * Vector3.forward * distance;
        private void Evidence(string visible = "current_visible", string proximity = "out_of_range")
        {
            sequence++;
            state.ApplyOrRefresh(new Observation(sequence.ToString(), owner.EntityId, ObservationSourceType.ContinuousScan,
                "sensor", ObservationChannel.Visual, player.EntityId,
                new Dictionary<string, string> { ["visibility_state"] = visible, ["proximity_state"] = proximity },
                null, 1, 0, sequence, sequence + 10));
        }
        [Test] public void RegistryOnlyPlayer_CannotAcquire()
        { attention.Tick(1); Assert.That(attention.Source, Is.EqualTo(AttentionSource.None)); }
        [TestCase("current_visible", "out_of_range")]
        [TestCase("lost", "nearby")]
        public void CurrentObserverEvidence_AllowsAcquire(string visible, string proximity)
        {
            Evidence(visible, proximity); attention.Tick(.05f); Assert.That(attention.HasGazePoint, Is.False);
            attention.Tick(.25f); Assert.That(attention.Source, Is.EqualTo(AttentionSource.CasualPlayer));
        }
        [TestCase("lost")][TestCase("remembered")][TestCase("forgotten")]
        public void NonCurrentEvidence_DoesNotAcquire(string visible)
        { Evidence(visible); attention.Tick(1); Assert.That(attention.HasGazePoint, Is.False); }
        [Test] public void DistanceHysteresis_MaintainsThenReleases()
        {
            Evidence(); attention.Tick(.3f); Move(4.1f, 0); attention.Tick(.3f);
            Assert.That(attention.IsLiveTracking, Is.True); Move(5.1f, 0); attention.Tick(.1f);
            Assert.That(attention.IsLiveTracking, Is.False); attention.Tick(.21f);
            Assert.That(attention.HasGazePoint, Is.False);
        }
        [Test] public void AngleHysteresis_MaintainsThenReleases()
        {
            Move(3, 44); Evidence(); attention.Tick(.3f); Move(3, 50); attention.Tick(.3f);
            Assert.That(attention.IsLiveTracking, Is.True); Move(3, 56); attention.Tick(.31f);
            Assert.That(attention.HasGazePoint, Is.False);
        }
        [Test] public void LostEvidence_FreezesPointEvenWhenHiddenTransformMoves()
        {
            Evidence(); attention.Tick(.3f); Vector3 last = attention.CurrentGazePoint;
            Evidence("lost"); player.transform.position = new Vector3(100, 50, 100); attention.Tick(.1f);
            Assert.That(attention.IsLiveTracking, Is.False);
            Assert.That(attention.CurrentGazePoint, Is.EqualTo(last)); attention.Tick(.21f);
            Assert.That(attention.HasGazePoint, Is.False);
        }
        [Test] public void RapidPass_ResetsAcquireDelay()
        {
            Evidence(); attention.Tick(.05f); Evidence("lost"); attention.Tick(.1f);
            Evidence(); attention.Tick(.25f); Assert.That(attention.HasGazePoint, Is.False);
            attention.Tick(.05f); Assert.That(attention.HasGazePoint, Is.True);
        }
        [Test] public void MotionSuppression_DistinguishesFormalTaskFromNavigation()
        {
            Evidence(); channels.SetNavigationActive(true); attention.Tick(.3f);
            Assert.That(attention.HasGazePoint, Is.True); channels.BeginTaskMotion(); attention.Tick(1);
            Assert.That(attention.HasGazePoint, Is.False); channels.EndTaskMotion(); channels.SetInteractionActive(true);
            attention.Tick(1); Assert.That(attention.HasGazePoint, Is.False);
            channels.SetInteractionActive(false); channels.BeginEmergencyMotion(); attention.Tick(1);
            Assert.That(attention.HasGazePoint, Is.False); channels.EndEmergencyMotion(); attention.Tick(.3f);
            Assert.That(attention.HasGazePoint, Is.True);
        }
        [Test] public void Unregister_StopsLiveTracking_AndResetClearsState()
        {
            Evidence(); attention.Tick(.3f); registry.Unregister(player); player.transform.position = Vector3.one * 50;
            attention.Tick(.1f); Assert.That(attention.IsLiveTracking, Is.False);
            attention.ResetAttention(); Assert.That(attention.CurrentTargetEntityId, Is.Null);
        }
        [Test] public void OtherObserverStore_CannotSupplyCandidates()
        {
            Evidence(); Field(state, "observer", player); attention.Tick(1);
            Assert.That(attention.HasGazePoint, Is.False);
        }
        [Test] public void DestroyedTarget_ClearsIdentityWithoutReadingItsTransform()
        {
            Evidence(); attention.Tick(.3f); Object.DestroyImmediate(player.gameObject); attention.Tick(.01f);
            Assert.That(attention.CurrentTargetEntityId, Is.Null); Assert.That(attention.HasGazePoint, Is.False);
        }
        [Test] public void EqualDistanceCandidates_UseStableEntityId()
        {
            var second = Entity("attention_a", AgentRole.Player); registry.Register(second);
            second.transform.position = player.transform.position;
            Evidence(); state.Apply(new Observation("second", owner.EntityId, ObservationSourceType.ContinuousScan,
                "second_sensor", ObservationChannel.Visual, second.EntityId,
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }, null, 1, 0, 1, 10));
            attention.Tick(.3f); Assert.That(attention.CurrentTargetEntityId, Is.EqualTo(second.EntityId));
        }
    }
}
