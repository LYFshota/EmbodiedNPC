using EmbodiedNPC.Cognition;
using EmbodiedNPC.Planning;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class TargetRediscoveryTests
    {
        private sealed class Driver : ITargetRediscoveryDriver
        {
            public string VisibleId;
            public bool MoveAllowed = true;
            public bool Arrived = true;
            public bool BadPath;
            public Vector3 Destination;
            public int Moves, Stops;
            public float Turned;
            public void Scan() { }
            public bool CanSee(string id) => id == VisibleId;
            public bool Face(Vector3 position, float degrees) => true;
            public bool MoveTo(Vector3 position) { Destination = position; Moves++; return MoveAllowed; }
            public bool HasArrived => Arrived;
            public bool PathFailed => BadPath;
            public void Turn(float degrees) => Turned += degrees;
            public void Stop() => Stops++;
        }

        [Test]
        public void FreshSameIdentity_CompletesWithoutWalking()
        {
            var driver = new Driver { VisibleId = "cup_01" };
            var search = new TargetRediscovery(driver);
            search.Begin("cup_01", new Vector3(2, 1, 3), 0);
            search.Tick(0, 0.1f);
            Assert.That(search.Status, Is.EqualTo(RediscoveryStatus.Succeeded));
            Assert.That(driver.Moves, Is.Zero);
        }

        [Test]
        public void DifferentCup_DoesNotConfirm_UsesCapturedPositionOnly()
        {
            var driver = new Driver { VisibleId = "cup_02", Arrived = false };
            var search = new TargetRediscovery(driver);
            var position = new Vector3(2, 1, 3);
            search.Begin("cup_01", position, 0);
            search.Tick(0, 0.1f);
            Assert.That(driver.Destination, Is.EqualTo(position));
            Assert.That(search.Status, Is.EqualTo(RediscoveryStatus.Searching));
            search.Tick(16, 0.1f);
            Assert.That(search.FailureCode, Is.EqualTo("REDISCOVERY_TIMEOUT"));
            Assert.That(driver.Moves, Is.EqualTo(1));
        }

        [Test]
        public void EmptyOldLocation_ScansOnceThenStops()
        {
            var driver = new Driver();
            var search = new TargetRediscovery(driver);
            search.Begin("cup_01", Vector3.zero, 0);
            for (int i = 0; i < 80 && search.Status == RediscoveryStatus.Searching; i++) search.Tick(i * 0.1, 0.1f);
            Assert.That(search.Status, Is.EqualTo(RediscoveryStatus.Failed));
            Assert.That(search.FailureCode, Is.EqualTo("TARGET_NOT_REDISCOVERED"));
            Assert.That(driver.Turned, Is.InRange(360f, 372f));
            Assert.That(driver.Moves, Is.EqualTo(1));
        }

        [Test]
        public void Cancel_StopsMotionAndCannotResumeOnLateObservation()
        {
            var driver = new Driver { Arrived = false };
            var search = new TargetRediscovery(driver);
            search.Begin("cup_01", Vector3.zero, 0);
            search.Tick(0, 0.1f);
            search.Cancel("INTERRUPTED_BY_THREAT");
            driver.VisibleId = "cup_01";
            search.Tick(1, 0.1f);
            Assert.That(search.Status, Is.EqualTo(RediscoveryStatus.Cancelled));
            Assert.That(search.FailureCode, Is.EqualTo("INTERRUPTED_BY_THREAT"));
            Assert.That(driver.Stops, Is.EqualTo(1));
        }

        [Test]
        public void DriverDisabledDuringScan_ReportsPathFailureNotMissingObject()
        {
            var driver = new Driver();
            var search = new TargetRediscovery(driver);
            search.Begin("cup_01", Vector3.zero, 0);
            search.Tick(0, 0.1f);
            search.Tick(0.1, 0.1f);
            driver.BadPath = true;
            search.Tick(0.2, 0.1f);
            Assert.That(search.FailureCode, Is.EqualTo("REDISCOVERY_PATH_UNAVAILABLE"));
            Assert.That(driver.Turned, Is.Zero);
        }

        [Test]
        public void SpatialFailures_AreReportedWithoutGlobalAlternatives()
        {
            foreach (string code in new[] { "TARGET_NOT_REDISCOVERED", "REDISCOVERY_PATH_UNAVAILABLE",
                         "TARGET_NOT_CURRENTLY_VISIBLE", "TARGET_LOCATION_UNKNOWN" })
            {
                Assert.That(ActionFeedbackPolicy.ShouldRequestCognition(code, PlanExecutionStatus.Failed), Is.True);
                Assert.That(ActionFeedbackPolicy.IsSpatialEvidenceFailure(code), Is.True);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void UnavailableOrBrokenPath_FailsWithoutUnboundedRetry(bool moveAllowed, bool badPath)
        {
            var driver = new Driver { MoveAllowed = moveAllowed, BadPath = badPath };
            var search = new TargetRediscovery(driver);
            search.Begin("cup_01", Vector3.zero, 0);
            search.Tick(0, 0.1f);
            search.Tick(0.2, 0.1f);
            Assert.That(search.FailureCode, Is.EqualTo("REDISCOVERY_PATH_UNAVAILABLE"));
            Assert.That(driver.Moves, Is.EqualTo(1));
        }
    }
}
