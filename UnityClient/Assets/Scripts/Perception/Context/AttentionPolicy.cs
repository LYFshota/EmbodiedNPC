using System;
using System.Collections.Generic;

namespace EmbodiedNPC.Perception
{
    /// <summary>Deterministic M6 attention ranking. It ranks evidence; it never invents missing fields.</summary>
    public sealed class AttentionPolicy
    {
        public float Score(Observation observation, string playerUtterance, double now)
        {
            if (observation == null || observation.ExpiresAt < now) return float.NegativeInfinity;
            float score = observation.Confidence * 20f;
            score += observation.SourceType == ObservationSourceType.Direct ? 30f : 0f;
            score += observation.Channel == ObservationChannel.Audio ? 5f : 0f;
            score += (float)Math.Max(0d, 20d - Math.Max(0d, now - observation.ObservedAt));
            if (observation.KnownFields.TryGetValue("event_type", out string eventType) &&
                string.Equals(eventType, GameplayEventType.WeaponDrawn.ToString(), StringComparison.Ordinal)) score += 100f;
            if (MatchesRequest(observation, playerUtterance)) score += 50f;
            return score;
        }

        private static bool MatchesRequest(Observation observation, string playerUtterance)
        {
            if (string.IsNullOrWhiteSpace(playerUtterance)) return false;
            if (!string.IsNullOrWhiteSpace(observation.SubjectEntityId) &&
                playerUtterance.IndexOf(observation.SubjectEntityId, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (KeyValuePair<string, string> field in observation.KnownFields)
            {
                if (field.Key != "semantic_tags" || string.IsNullOrWhiteSpace(field.Value)) continue;
                foreach (string tag in field.Value.Split(','))
                    if (SemanticRequestMatcher.Matches(playerUtterance, tag)) return true;
            }
            return false;
        }
    }
}
