using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class HearingSensorMathTests
    {
        [Test]
        public void Intensity_FallsOffAndStopsOutsideConfiguredRange()
        {
            Assert.That(HearingSensorMath.CalculateIntensity(1f, 0f, 10f), Is.EqualTo(1f));
            Assert.That(HearingSensorMath.CalculateIntensity(1f, 5f, 10f), Is.EqualTo(0.5f));
            Assert.That(HearingSensorMath.CalculateIntensity(1f, 10.1f, 10f), Is.EqualTo(0f));
        }

        [TestCase(0f, 0f, 1f, "front")]
        [TestCase(1f, 0f, 0f, "right")]
        [TestCase(-1f, 0f, 0f, "left")]
        [TestCase(0f, 0f, -1f, "rear")]
        public void DirectionBand_OnlyDisclosesApproximateDirection(float x, float y, float z, string expected)
        {
            Assert.That(HearingSensorMath.DirectionBand(Vector3.forward, new Vector3(x, y, z)), Is.EqualTo(expected));
        }

        [Test]
        public void DistanceBand_UsesNearMidFarRatherThanExactDistance()
        {
            Assert.That(HearingSensorMath.DistanceBand(2f, 10f), Is.EqualTo("near"));
            Assert.That(HearingSensorMath.DistanceBand(5f, 10f), Is.EqualTo("mid"));
            Assert.That(HearingSensorMath.DistanceBand(9f, 10f), Is.EqualTo("far"));
        }
    }
}
