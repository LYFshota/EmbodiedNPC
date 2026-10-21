using EmbodiedNPC.Animation;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class HeadGazeTests
    {
        private GameObject actor, body;
        [SetUp] public void SetUp()
        { actor = new GameObject("Actor"); body = new GameObject("Visual"); body.transform.SetParent(actor.transform, false); }
        [TearDown] public void TearDown() { Object.DestroyImmediate(actor); }
        private Vector3 Solve(float yaw, float pitch = 0) => HeadGazeController.SolveAngles(actor.transform,
            body.transform, Vector3.zero, Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward * 3);
        [TestCase(20, 1)][TestCase(40, .5f)][TestCase(60, 0)]
        public void YawSoftFade_Bounded(float yaw, float weight)
        { Vector3 value = Solve(yaw); Assert.That(value.z, Is.EqualTo(weight).Within(.001)); Assert.That(Mathf.Abs(value.x), Is.LessThanOrEqualTo(50)); }
        [TestCase(-60, -20)][TestCase(60, 25)]
        public void PitchClamps(float input, float expected)
        { Assert.That(Solve(0, input).y, Is.EqualTo(expected).Within(.001)); }
        [Test] public void HeadAngle_IsRelativeToCurrentBody()
        { body.transform.localRotation = Quaternion.Euler(0, 40, 0); Assert.That(Solve(55).x, Is.EqualTo(15).Within(.001)); }
        [Test] public void CombinedYaw_CapsRootToGaze()
        { body.transform.localRotation = Quaternion.Euler(0, 55, 0); Assert.That(Solve(95).x, Is.EqualTo(20).Within(.001)); }
        [Test] public void ArbitraryActorOrientation_PreservesBodyRelativeAngles()
        {
            actor.transform.rotation = Quaternion.Euler(0, 123, 0); body.transform.localRotation = Quaternion.Euler(0, 40, 0);
            Vector3 point = actor.transform.TransformDirection(Quaternion.Euler(0, 55, 0) * Vector3.forward);
            Assert.That(HeadGazeController.SolveAngles(actor.transform, body.transform, Vector3.zero, point).x,
                Is.EqualTo(15).Within(.001));
        }
        [Test] public void FacingResolvesBeforeAttentionAndHead()
        {
            int Order(System.Type type) => ((DefaultExecutionOrder)System.Attribute.GetCustomAttribute(type, typeof(DefaultExecutionOrder))).order;
            Assert.That(Order(typeof(FacingController)), Is.LessThan(Order(typeof(AttentionController))));
            Assert.That(Order(typeof(AttentionController)), Is.LessThan(Order(typeof(HeadGazeController))));
        }
    }
}
