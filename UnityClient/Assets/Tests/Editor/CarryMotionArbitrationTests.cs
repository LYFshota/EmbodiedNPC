using EmbodiedNPC.Animation;
using NUnit.Framework;
using UnityEngine;
using System.Collections;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests
{
    public sealed class CarryMotionArbitrationTests
    {
        private GameObject actor;
        private MotionChannelCoordinator channels;
        [SetUp] public void Create() { actor = new GameObject("Carry arbitration"); channels = actor.AddComponent<MotionChannelCoordinator>(); }
        [TearDown] public void Destroy() => Object.DestroyImmediate(actor);
        [TestCase("wave")][TestCase("nod")]
        public void Holding_CancelsOnce_BlocksAdmission_ReleaseAllowsFreshLease(string gesture)
        {
            Assert.That(channels.TryBeginGesture(gesture, false, 0, 3, out long previous), Is.True);
            int ended = 0; channels.GestureEnded += _ => ended++;
            channels.SetCarryPresentationActive(true); channels.SetCarryPresentationActive(true);
            Assert.That(ended, Is.EqualTo(1)); Assert.That(channels.ActiveGestureId, Is.Zero);
            Assert.That(channels.IsTaskMotionActive, Is.False);
            Assert.That(channels.TryBeginGesture(gesture, false, 1, 3, out _), Is.False);
            channels.SetCarryPresentationActive(false);
            Assert.That(channels.TryBeginGesture(gesture, false, 2, 3, out long fresh), Is.True);
            Assert.That(channels.CompleteGesture(previous), Is.False); Assert.That(channels.ActiveGestureId, Is.EqualTo(fresh));
        }
        [UnityTest] public IEnumerator Disable_ClearsVisualOccupancy_WithoutReplayingGesture()
        {
            Object.DestroyImmediate(actor);
            yield return new EnterPlayMode();
            Create();
            channels.SetCarryPresentationActive(true); actor.SetActive(false);
            Assert.That(channels.IsCarryPresentationActive, Is.False);
            actor.SetActive(true); Assert.That(channels.ActiveGestureId, Is.Zero);
            yield return new ExitPlayMode();
        }
        [Test] public void Emergency_DoesNotInventAReleasedItem()
        {
            channels.SetCarryPresentationActive(true); channels.BeginEmergencyMotion(); channels.EndEmergencyMotion();
            Assert.That(channels.IsCarryPresentationActive, Is.True);
            Assert.That(channels.TryBeginGesture("wave", false, 1, 3, out _), Is.False);
        }
    }
}
