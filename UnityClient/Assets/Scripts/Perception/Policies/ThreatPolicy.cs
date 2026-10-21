using System;

namespace EmbodiedNPC.Perception
{
    public enum ThreatResponseKind
    {
        Ignore,
        StopAndGuard,
        RetreatAndGuard,
    }

    /// <summary>Deterministic M6.5 decision derived only from one NPC's current Observation.</summary>
    public readonly struct ThreatDecision
    {
        public ThreatDecision(ThreatResponseKind response, string reasonCode, string observationId,
            string approximateDirection)
        {
            Response = response;
            ReasonCode = reasonCode ?? string.Empty;
            ObservationId = observationId ?? string.Empty;
            ApproximateDirection = approximateDirection ?? string.Empty;
        }

        public ThreatResponseKind Response { get; }
        public string ReasonCode { get; }
        public string ObservationId { get; }
        public string ApproximateDirection { get; }
        public bool ShouldInterrupt => Response != ThreatResponseKind.Ignore;

        public static ThreatDecision Ignored(Observation observation, string reasonCode) =>
            new(ThreatResponseKind.Ignore, reasonCode, observation?.ObservationId, string.Empty);
    }

    /// <summary>
    /// Small, replayable threat rule set. It never reads World State or calls the backend.
    /// Audio threats must be near; direct/contact threats are already observer-local evidence.
    /// </summary>
    public sealed class ThreatPolicy
    {
        private readonly float minimumConfidence;

        public ThreatPolicy(float minimumConfidence = 0.25f)
        {
            this.minimumConfidence = Math.Clamp(minimumConfidence, 0f, 1f);
        }

        public ThreatDecision Evaluate(Observation observation, double evaluatedAt)
        {
            if (observation == null) return ThreatDecision.Ignored(null, "OBSERVATION_MISSING");
            if (observation.ExpiresAt < evaluatedAt) return ThreatDecision.Ignored(observation, "OBSERVATION_EXPIRED");
            if (observation.Confidence < minimumConfidence) return ThreatDecision.Ignored(observation, "CONFIDENCE_TOO_LOW");
            if (!observation.KnownFields.TryGetValue("event_type", out string eventType))
                return ThreatDecision.Ignored(observation, "EVENT_TYPE_UNKNOWN");

            bool weaponDrawn = string.Equals(eventType, GameplayEventType.WeaponDrawn.ToString(), StringComparison.Ordinal);
            bool attack = string.Equals(eventType, GameplayEventType.Attack.ToString(), StringComparison.Ordinal) ||
                          string.Equals(eventType, "DamageTaken", StringComparison.Ordinal);
            if (!weaponDrawn && !attack) return ThreatDecision.Ignored(observation, "EVENT_NOT_THREATENING");

            if (observation.Channel == ObservationChannel.Audio &&
                (!observation.KnownFields.TryGetValue("approximate_distance", out string distance) ||
                 !string.Equals(distance, "near", StringComparison.Ordinal)))
                return ThreatDecision.Ignored(observation, "THREAT_NOT_NEAR");

            observation.KnownFields.TryGetValue("approximate_direction", out string direction);
            return new ThreatDecision(ThreatResponseKind.RetreatAndGuard,
                attack ? "NEARBY_ATTACK" : "NEARBY_WEAPON_DRAWN",
                observation.ObservationId, direction);
        }
    }
}
