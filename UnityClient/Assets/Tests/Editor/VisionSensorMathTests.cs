using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class VisionSensorMathTests
    {
        [Test]
        public void TargetInFrontInsideRangeAndFov_IsAccepted()
        {
            Assert.That(VisionSensorMath.IsWithinRange(Vector3.zero, new Vector3(0f, 0f, 9.9f), 10f), Is.True);
            Assert.That(VisionSensorMath.IsInsideHorizontalFov(Vector3.zero, Vector3.forward,
                new Vector3(5f, 0f, 5f), 60f), Is.True);
        }

        [Test]
        public void TargetBehindOrOutsideRange_IsRejected()
        {
            Assert.That(VisionSensorMath.IsWithinRange(Vector3.zero, new Vector3(0f, 0f, 10.1f), 10f), Is.False);
            Assert.That(VisionSensorMath.IsInsideHorizontalFov(Vector3.zero, Vector3.forward,
                Vector3.back, 60f), Is.False);
        }

        [Test]
        public void MaintainFov_IsWiderThanEnterFov()
        {
            Vector3 targetAtSixtyTwoDegrees = Quaternion.AngleAxis(62f, Vector3.up) * Vector3.forward;
            Assert.That(VisionSensorMath.IsInsideHorizontalFov(Vector3.zero, Vector3.forward, targetAtSixtyTwoDegrees, 60f), Is.False);
            Assert.That(VisionSensorMath.IsInsideHorizontalFov(Vector3.zero, Vector3.forward, targetAtSixtyTwoDegrees, 65f), Is.True);
        }
    }
}
