using EmbodiedNPC.Animation;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class FacingLocomotionTests
    {
        private GameObject root, speaker;
        private Transform visual;
        private FacingController facing;
        private MotionChannelCoordinator channels;
        private Quaternion neutral;

        [SetUp] public void SetUp()
        {
            root = new GameObject("Actor");
            root.transform.rotation = Quaternion.Euler(0, 30, 0);
            visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            neutral = visual.localRotation = Quaternion.Euler(5, 20, 0);
            channels = root.AddComponent<MotionChannelCoordinator>();
            facing = visual.gameObject.AddComponent<FacingController>();
            facing.Configure(visual, root.transform, channels);
            speaker = new GameObject("Authorized speaker");
            speaker.transform.position = root.transform.TransformDirection(Vector3.left) * 2;
            facing.SetDialogueTarget(speaker.transform, true);
        }
        [TearDown] public void TearDown()
        { Object.DestroyImmediate(root); Object.DestroyImmediate(speaker); }

        [Test] public void Facing_IsBoundedAgainstAuthoredNeutral_AndLeavesActorUntouched()
        {
            Quaternion actor = root.transform.rotation;
            facing.Tick(.1f);
            Assert.That(Quaternion.Angle(neutral, visual.localRotation), Is.EqualTo(12f).Within(.01));
            facing.Tick(10);
            Assert.That(Quaternion.Angle(neutral, visual.localRotation), Is.EqualTo(55f).Within(.01));
            Assert.That(root.transform.rotation, Is.EqualTo(actor));
            Assert.That(facing.NeutralLocalRotation, Is.EqualTo(neutral));
        }
        [Test] public void Facing_EqualElapsedTimeHasEqualRotation_NoOvershoot()
        {
            for (int i = 0; i < 10; i++) facing.Tick(.01f);
            Quaternion fine = visual.localRotation;
            facing.ResetPresentation(); facing.SetDialogueTarget(speaker.transform, true); facing.Tick(.1f);
            Assert.That(Quaternion.Angle(fine, visual.localRotation), Is.LessThan(.01));
            facing.Tick(10); Quaternion reached = visual.localRotation; facing.Tick(10);
            Assert.That(Quaternion.Angle(reached, visual.localRotation), Is.LessThan(.01));
        }
        [Test] public void Facing_TargetLossAndReleaseReturnSmoothly_ResetRestoresNeutral()
        {
            facing.Tick(1); speaker.SetActive(false); facing.Tick(.1f);
            Assert.That(facing.IsDialogueFacingActive, Is.False);
            Assert.That(Quaternion.Angle(neutral, visual.localRotation), Is.EqualTo(43f).Within(.01));
            facing.Tick(1); Assert.That(Quaternion.Angle(neutral, visual.localRotation), Is.LessThan(.01));
            speaker.SetActive(true); facing.SetDialogueTarget(speaker.transform, true); facing.Tick(1);
            facing.SetDialogueTarget(null, false); facing.Tick(1);
            Assert.That(Quaternion.Angle(neutral, visual.localRotation), Is.LessThan(.01));
            facing.SetDialogueTarget(speaker.transform, true); facing.Tick(1); facing.ResetPresentation();
            Assert.That(visual.localRotation, Is.EqualTo(neutral));
        }
        [Test] public void Facing_MotionOwnersSuppressAndReleaseReacquires_GestureCoexists()
        {
            channels.TryBeginGesture("nod", false, 0, 3, out _); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.True);
            channels.SetNavigationActive(true); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.False);
            channels.SetNavigationActive(false); channels.BeginTaskMotion(); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.False);
            channels.EndTaskMotion(); channels.SetInteractionActive(true); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.False);
            channels.SetInteractionActive(false); channels.BeginEmergencyMotion(); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.False);
            channels.EndEmergencyMotion(); facing.Tick(1);
            Assert.That(facing.IsDialogueFacingActive, Is.True);
        }
        [Test] public void Facing_RejectsActorAsVisual_AndInvalidDelta()
        {
            facing.Tick(float.NaN); Assert.That(visual.localRotation, Is.EqualTo(neutral));
            facing.Tick(-1); Assert.That(visual.localRotation, Is.EqualTo(neutral));
            facing.Configure(root.transform, root.transform, channels);
            Quaternion actor = root.transform.rotation;
            facing.SetDialogueTarget(speaker.transform, true); facing.Tick(1);
            Assert.That(root.transform.rotation, Is.EqualTo(actor));
        }
        [TestCase(0f, 50f, 0f)]
        [TestCase(.04f, 0f, 0f)]
        [TestCase(1f, 0f, .5f)]
        [TestCase(5f, 0f, 1f)]
        public void Speed_UsesOnlyPlanarVelocityAndDeadzone(float x, float y, float expected)
        { Assert.That(LocomotionPresenter.ProjectSpeed(new Vector3(x, y, 0), 2, .05f), Is.EqualTo(expected)); }
        [Test] public void Navigation_PendingAccelerationArrivalStopAndLegacyGate()
        {
            Assert.That(LocomotionPresenter.IsNavigationInProgress(false, true, false, 0, .1f, 0, .05f), Is.True);
            Assert.That(LocomotionPresenter.IsNavigationInProgress(false, false, true, 2, .1f, 0, .05f), Is.True);
            Assert.That(LocomotionPresenter.IsNavigationInProgress(false, false, true, float.PositiveInfinity, .1f, 0, .05f), Is.True);
            Assert.That(LocomotionPresenter.IsNavigationInProgress(false, false, true, .12f, .1f, .04f, .05f), Is.False);
            Assert.That(LocomotionPresenter.IsNavigationInProgress(false, false, false, 0, .1f, .5f, .05f), Is.True);
            Assert.That(LocomotionPresenter.IsNavigationInProgress(true, true, true, 2, .1f, 1, .05f), Is.False);
            channels.BeginTaskMotion(); channels.SetNavigationActive(true); channels.SetNavigationActive(false);
            Assert.That(channels.ActiveBodyChannel, Is.EqualTo(MotionChannel.Locomotion));
        }
        [Test] public void MissingAgentAndAnimator_AreSafeAndDoNotCancelIdleGestureRepeatedly()
        {
            var loco = visual.gameObject.AddComponent<LocomotionPresenter>(); loco.Configure(null, channels);
            channels.SetNavigationActive(true); loco.Tick(.1f);
            Assert.That(channels.IsNavigationActive, Is.False);
            channels.TryBeginGesture("wave", false, 0, 3, out long id);
            loco.Tick(.1f); loco.Tick(.1f);
            Assert.That(channels.ActiveGestureId, Is.EqualTo(id));
            Assert.That(loco.NormalizedSpeed, Is.Zero); Assert.That(loco.HasSpeedParameter, Is.False);
        }
    }
}
