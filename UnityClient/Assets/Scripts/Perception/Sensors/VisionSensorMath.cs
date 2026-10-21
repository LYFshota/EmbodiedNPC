using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Allocation-free horizontal range/FOV checks kept independent from Unity scene queries.</summary>
    public static class VisionSensorMath
    {
        public static bool IsWithinRange(Vector3 origin, Vector3 target, float range)
        {
            if (range < 0f) return false;
            return (target - origin).sqrMagnitude <= range * range;
        }

        public static bool IsInsideHorizontalFov(Vector3 origin, Vector3 forward, Vector3 target, float halfAngleDegrees)
        {
            Vector3 flatDirection = Vector3.ProjectOnPlane(target - origin, Vector3.up);
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flatDirection.sqrMagnitude < 0.0001f || flatForward.sqrMagnitude < 0.0001f) return true;
            float threshold = Mathf.Cos(Mathf.Clamp(halfAngleDegrees, 0f, 180f) * Mathf.Deg2Rad);
            return Vector3.Dot(flatForward.normalized, flatDirection.normalized) >= threshold;
        }
    }
}
