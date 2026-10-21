using System.Collections;
using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CafeDemo.Tests
{
    public sealed class CafeVisionTests : PlayerPresentationFixture
    {
        [UnityTest]
        public IEnumerator VisiblePlayer_RefreshesEvidence_ThenLeavingViewRetainsSpatialMemory()
        {
            ControlledPerception();
            merchant.GetComponent<EmbodiedNPC.Routines.NpcDefaultRoutineController>().enabled = false;
            var sensor = merchant.GetComponent<VisionSensor>();
            var store = merchant.GetComponent<PerceivedStateStore>();
            Place(new Vector3(-.8f, .02f, 2f));
            Physics.SyncTransforms();
            sensor.ScanNow();
            Assert.That(sensor.CurrentVisibleEntityIds, Does.Contain("player_01"));
            Assert.That(store.Aggregator.Entities["player_01"].LifecycleState,
                Is.EqualTo(PerceptionLifecycleState.Tracked));
            int revision = store.Aggregator.PerceptionRevision;
            Place(new Vector3(-.6f, .02f, 2f));
            sensor.ScanNow();
            Assert.That(store.Aggregator.PerceptionRevision, Is.GreaterThan(revision));
            Place(merchant.transform.position + Vector3.right * 20f);
            sensor.ScanNow();
            Assert.That(sensor.CurrentVisibleEntityIds, Does.Not.Contain("player_01"));
            Assert.That(store.Aggregator.Entities["player_01"].LifecycleState,
                Is.EqualTo(PerceptionLifecycleState.Lost));
            Assert.That(store.SpatialMemory.TryGet("player_01", Time.realtimeSinceStartupAsDouble, out _), Is.True);
            yield return null;
        }
    }
}
