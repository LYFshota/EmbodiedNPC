using UnityEngine;

namespace EmbodiedNPC.Perception
{
    public static class HearingSensorMath
    {
        public static float CalculateIntensity(float baseIntensity, float distance, float hearingRange)
        {
            if (hearingRange <= 0f || distance > hearingRange) return 0f;
            return Mathf.Clamp01(baseIntensity) * Mathf.Clamp01(1f - Mathf.Max(0f, distance) / hearingRange);
        }

        public static string DirectionBand(Vector3 observerForward, Vector3 originOffset)
        {
            originOffset.y = 0f;
            observerForward.y = 0f;
            if (originOffset.sqrMagnitude < 0.0001f || observerForward.sqrMagnitude < 0.0001f) return "here";
            float signedAngle = Vector3.SignedAngle(observerForward.normalized, originOffset.normalized, Vector3.up);
            float absolute = Mathf.Abs(signedAngle);
            if (absolute <= 45f) return "front";
            if (absolute >= 135f) return "rear";
            return signedAngle > 0f ? "right" : "left";
        }

        public static string DistanceBand(float distance, float hearingRange)
        {
            if (hearingRange <= 0f) return "unknown";
            float normalized = Mathf.Clamp01(distance / hearingRange);
            return normalized < 0.34f ? "near" : normalized < 0.67f ? "mid" : "far";
        }
    }
}
