using EmbodiedNPC.Perception;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class VisionConeDebugGeometryTests
    {
        [Test]
        public void Build_CreatesDashedBoundaryCenterAndArcWithinRange()
        {
            var segments = VisionConeDebugGeometry.Build(10f, 60f, 0.35f, 0.22f);

            Assert.That(segments.Count, Is.GreaterThan(20));
            foreach (VisionDebugSegment segment in segments)
            {
                Assert.That(segment.Start.magnitude, Is.LessThanOrEqualTo(10.001f));
                Assert.That(segment.End.magnitude, Is.LessThanOrEqualTo(10.001f));
                Assert.That(segment.End.sqrMagnitude + 0.001f,
                    Is.GreaterThanOrEqualTo(segment.Start.sqrMagnitude));
            }
        }

        [Test]
        public void Build_WithInvalidRange_ReturnsNoSegments()
        {
            Assert.That(VisionConeDebugGeometry.Build(0f, 60f, 0.35f, 0.22f), Is.Empty);
        }
    }
}
