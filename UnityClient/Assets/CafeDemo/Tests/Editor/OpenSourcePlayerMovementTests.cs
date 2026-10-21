using System.Collections;
using EmbodiedNPC.Cognition;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace CafeDemo.Tests
{
    // Uses the existing offline fixture, actual input adapter and unchanged movement limits.
    public sealed class OpenSourcePlayerMovementTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        [UnityTest] public IEnumerator OfflineActualMovement_IdleWalkStopAndTurn()
        {
            ControlledPerception(); int events=0,runs=0;emote.EmotePerformed+=_=>events++;
            merchant.GetComponent<AgentOrchestrator>().RunStarted+=_=>runs++;
            customer.GetComponent<AgentOrchestrator>().RunStarted+=_=>runs++;
            Keys();yield return Wait(.2f);
            Assert.That(locomotion.NormalizedSpeed,Is.Zero);Assert.That(animator.GetFloat("Speed"),Is.LessThan(.01f));
            var start=player.transform.position;var foot=animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation;
            Keys(Key.W);yield return Wait(.75f);
            Assert.That(Vector3.Distance(start,player.transform.position),Is.GreaterThan(.5f));
            Assert.That(locomotion.NormalizedSpeed,Is.GreaterThan(.9f));Assert.That(animator.GetFloat("Speed"),Is.GreaterThan(.9f));
            Assert.That(Quaternion.Angle(foot,animator.GetBoneTransform(HumanBodyBones.RightFoot).localRotation),Is.GreaterThan(5));
            var forward=player.transform.rotation;Keys(Key.D);yield return Wait(.3f);
            Assert.That(Quaternion.Angle(forward,player.transform.rotation),Is.GreaterThan(30));
            Keys();yield return Wait(.35f);
            Assert.That(locomotion.NormalizedSpeed,Is.LessThan(.01f));Assert.That(animator.GetFloat("Speed"),Is.LessThan(.05f));
            Assert.That(events,Is.Zero);Assert.That(runs,Is.Zero);Assert.That(animator.applyRootMotion,Is.False);
        }
    }
}
