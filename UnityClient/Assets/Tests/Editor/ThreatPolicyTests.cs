using System.Collections.Generic;
using EmbodiedNPC.Perception;
using NUnit.Framework;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class ThreatPolicyTests
    {
        [Test]
        public void NearWeaponDrawn_RequestsDeterministicRetreat()
        {
            Observation observation = ThreatObservation("near", 0.6f, 10d);

            ThreatDecision decision = new ThreatPolicy().Evaluate(observation, 2d);

            Assert.That(decision.Response, Is.EqualTo(ThreatResponseKind.RetreatAndGuard));
            Assert.That(decision.ReasonCode, Is.EqualTo("NEARBY_WEAPON_DRAWN"));
            Assert.That(decision.ObservationId, Is.EqualTo(observation.ObservationId));
        }

        [TestCase("mid")]
        [TestCase("far")]
        public void NonNearAudioWeapon_IsIgnored(string distanceBand)
        {
            ThreatDecision decision = new ThreatPolicy().Evaluate(ThreatObservation(distanceBand, 0.8f, 10d), 2d);
            Assert.That(decision.ShouldInterrupt, Is.False);
            Assert.That(decision.ReasonCode, Is.EqualTo("THREAT_NOT_NEAR"));
        }

        [Test]
        public void ExpiredThreat_IsIgnored()
        {
            ThreatDecision decision = new ThreatPolicy().Evaluate(ThreatObservation("near", 1f, 1d), 2d);
            Assert.That(decision.ShouldInterrupt, Is.False);
            Assert.That(decision.ReasonCode, Is.EqualTo("OBSERVATION_EXPIRED"));
        }

        [Test]
        public void DirectAttack_DoesNotRequireAudioDistanceDisclosure()
        {
            Observation observation = new("obs_attack", "npc_01", ObservationSourceType.Direct, "damage_01",
                ObservationChannel.Direct, "npc_01",
                new Dictionary<string, string> { ["event_type"] = "Attack" }, new string[0], 1f, 1, 1d, 10d);

            ThreatDecision decision = new ThreatPolicy().Evaluate(observation, 2d);

            Assert.That(decision.Response, Is.EqualTo(ThreatResponseKind.RetreatAndGuard));
            Assert.That(decision.ReasonCode, Is.EqualTo("NEARBY_ATTACK"));
        }

        private static Observation ThreatObservation(string distance, float confidence, double expiresAt) =>
            new("obs_weapon", "npc_01", ObservationSourceType.GameplayEvent, "stim_weapon",
                ObservationChannel.Audio, string.Empty,
                new Dictionary<string, string>
                {
                    ["event_type"] = "WeaponDrawn",
                    ["approximate_distance"] = distance,
                    ["approximate_direction"] = "front",
                }, new string[0], confidence, 1, 1d, expiresAt, PerceptionLifecycleState.Detected);
    }
}
