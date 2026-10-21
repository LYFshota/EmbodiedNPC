using System;
using System.Collections.Generic;
using System.Globalization;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>
    /// Observer-local, periodic visual scan. It intentionally does not choose a reaction or read
    /// private WorldState fields; it only emits evidence that survives the range/FOV/occlusion checks.
    /// </summary>
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore))]
    public sealed class VisionSensor : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore stateStore;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private WorldStateService worldState;
        [SerializeField, Min(0.1f)] private float scanIntervalSeconds = 0.25f;
        // Entry/maintain thresholds form hysteresis: maintain applies only to an
        // already tracked target. Origin and direction come from this sensor.
        [SerializeField, Min(0f)] private float enterDistance = 10f;
        [SerializeField, Min(0f)] private float maintainDistance = 11f;
        [SerializeField, Range(0f, 180f)] private float enterHalfFovDegrees = 60f;
        [SerializeField, Range(0f, 180f)] private float maintainHalfFovDegrees = 65f;
        [SerializeField, Min(0f)] private float eyeHeight = 1.5f;
        [SerializeField] private LayerMask occlusionMask = ~0;
        // Unscaled seconds: Lost -> Remembered -> Forgotten. Remembered retains
        // past observation, never current visibility or a license to read the world.
        [SerializeField, Min(0f)] private float lostToRememberedSeconds = 0.5f;
        [SerializeField, Min(0f)] private float rememberedLifetimeSeconds = 8f;
        [SerializeField] private bool drawGizmos = true;

        private readonly Dictionary<string, VisualTrack> tracks = new(StringComparer.Ordinal);
        private readonly HashSet<string> visibleThisScan = new(StringComparer.Ordinal);
        private readonly HashSet<string> currentVisibleEntityIds = new(StringComparer.Ordinal);
        private readonly RaycastHit[] raycastHits = new RaycastHit[8];
        private readonly List<PerceivableEntity> seatCandidates = new();
        private double nextScanAt;
        private EntityRegistry subscribedRegistry;

        public IReadOnlyCollection<string> CurrentVisibleEntityIds => currentVisibleEntityIds;
        public float EnterDistance => enterDistance;
        public float EnterHalfFovDegrees => enterHalfFovDegrees;
        public float EyeHeight => eyeHeight;

        private sealed class VisualTrack
        {
            public PerceptionLifecycleState State;
            public double StateChangedAt;
        }

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (stateStore == null) stateStore = GetComponent<PerceivedStateStore>();
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            if (worldState == null) worldState = FindAnyObjectByType<WorldStateService>();
        }

        private void OnEnable()
        {
            nextScanAt = 0d;
            SubscribeRegistry();
        }

        private void OnDisable()
        {
            if (subscribedRegistry != null)
            {
                subscribedRegistry.EntityUnregistered -= HandleEntityUnregistered;
                subscribedRegistry = null;
            }
            // Stopping observation must not leave previously seen seats/presence current.
            if (stateStore == null || observer == null) return;
            foreach (KeyValuePair<string, VisualTrack> pair in tracks)
                if (pair.Value.State == PerceptionLifecycleState.Tracked)
                    AdvanceMissingTrack(pair.Key, pair.Value, Time.unscaledTimeAsDouble);
            currentVisibleEntityIds.Clear();
        }

        private void SubscribeRegistry()
        {
            if (entityRegistry == null || subscribedRegistry == entityRegistry) return;
            if (subscribedRegistry != null)
                subscribedRegistry.EntityUnregistered -= HandleEntityUnregistered;
            entityRegistry.EntityUnregistered += HandleEntityUnregistered;
            subscribedRegistry = entityRegistry;
        }

        private void HandleEntityUnregistered(WorldEntity entity)
        {
            if (entity == null) return;
            string entityId = entity.EntityId;
            if (tracks.TryGetValue(entityId, out VisualTrack track) &&
                track.State == PerceptionLifecycleState.Tracked)
                AdvanceMissingTrack(entityId, track, Time.unscaledTimeAsDouble);
            currentVisibleEntityIds.Remove(entityId);
        }

        private void Update()
        {
            double now = Time.unscaledTimeAsDouble;
            if (now < nextScanAt) return;
            nextScanAt = now + scanIntervalSeconds;
            ScanNow();
        }

        [ContextMenu("Scan vision now")]
        public void ScanNow()
        {
            if (observer == null || stateStore == null || entityRegistry == null || !entityRegistry.IsReady) return;
            double now = Time.unscaledTimeAsDouble;
            Vector3 origin = transform.position + Vector3.up * eyeHeight;
            visibleThisScan.Clear();
            seatCandidates.Clear();

            foreach (WorldEntity candidate in entityRegistry.RegisteredEntities)
            {
                if (candidate == null || candidate == observer || !candidate.isActiveAndEnabled) continue;
                PerceivableEntity perceivable = candidate.GetComponent<PerceivableEntity>();
                if (perceivable == null || !perceivable.isActiveAndEnabled) continue;
                if (candidate.TryGetComponent(out FixedSeatBinding _)) { seatCandidates.Add(perceivable); continue; }
                ScanCandidate(perceivable, origin, now);
            }

            // Ordinary bodies are checked first. A seated body can establish visible occupied
            // seat space only when that body passed this very scan's original range/FOV/LOS rules.
            foreach (PerceivableEntity seatCandidate in seatCandidates)
            {
                FixedSeatBinding binding = seatCandidate.GetComponent<FixedSeatBinding>();
                Transform visibleOccupant = binding.TryGetCurrentOccupant(out WorldEntity occupant) &&
                    visibleThisScan.Contains(occupant.EntityId) ? occupant.transform : null;
                ScanCandidate(seatCandidate, origin, now, visibleOccupant);
            }

            foreach (KeyValuePair<string, VisualTrack> pair in tracks)
            {
                if (visibleThisScan.Contains(pair.Key)) continue;
                AdvanceMissingTrack(pair.Key, pair.Value, now);
            }
        }

        private void ScanCandidate(PerceivableEntity perceivable, Vector3 origin, double now, Transform visibleOccupant = null)
        {
                WorldEntity candidate = perceivable.Entity;
                if (candidate == null) return;
                string targetId = candidate.EntityId;
                if (string.IsNullOrWhiteSpace(targetId)) return;

                bool wasTracked = tracks.TryGetValue(targetId, out VisualTrack track) &&
                    track.State == PerceptionLifecycleState.Tracked;
                float distance = wasTracked ? maintainDistance : enterDistance;
                float halfFov = wasTracked ? maintainHalfFovDegrees : enterHalfFovDegrees;
                Vector3 target = perceivable.VisualTargetPosition;
                if (!VisionSensorMath.IsWithinRange(origin, target, distance) ||
                    !VisionSensorMath.IsInsideHorizontalFov(origin, transform.forward, target, halfFov) ||
                    !HasClearLineOfSight(origin, target, candidate.transform, visibleOccupant)) return;

                visibleThisScan.Add(targetId);
                currentVisibleEntityIds.Add(targetId);
                track ??= new VisualTrack();
                tracks[targetId] = track;
                track.State = PerceptionLifecycleState.Tracked;
                track.StateChangedAt = now;
                PublishVisualObservation(perceivable, targetId, target, now, PerceptionLifecycleState.Tracked);
        }

        private void AdvanceMissingTrack(string targetId, VisualTrack track, double now)
        {
            if (track.State == PerceptionLifecycleState.Tracked)
            {
                track.State = PerceptionLifecycleState.Lost;
                track.StateChangedAt = now;
                currentVisibleEntityIds.Remove(targetId);
                PublishLifecycleObservation(targetId, now, track.State);
            }
            else if (track.State == PerceptionLifecycleState.Lost && now - track.StateChangedAt >= lostToRememberedSeconds)
            {
                track.State = PerceptionLifecycleState.Remembered;
                track.StateChangedAt = now;
                PublishLifecycleObservation(targetId, now, track.State);
            }
            else if (track.State == PerceptionLifecycleState.Remembered && now - track.StateChangedAt >= rememberedLifetimeSeconds)
            {
                track.State = PerceptionLifecycleState.Forgotten;
                track.StateChangedAt = now;
                PublishLifecycleObservation(targetId, now, track.State);
            }
        }

        private void PublishVisualObservation(PerceivableEntity perceivable, string targetId, Vector3 target,
            double now, PerceptionLifecycleState lifecycle)
        {
            var knownFields = new Dictionary<string, string>
            {
                ["visibility_state"] = "current_visible",
                ["last_seen_position"] = FormatPosition(target)
            };
            perceivable.WriteVisibleFields(knownFields);
            if (perceivable.TryGetComponent(out FixedSeatBinding seatBinding))
                seatBinding.WriteObservedFields(knownFields, visibleThisScan);
            int revision = worldState == null ? Math.Max(0, stateStore.Aggregator.LatestWorldRevision) : worldState.WorldRevision;
            // Only range/FOV/LOS success reaches here; lifecycle transitions cannot refresh memory.
            stateStore.SpatialMemory?.Observe(targetId, target, now, revision, knownFields);
            stateStore.ApplyOrRefresh(CreateObservation(targetId, knownFields, Array.Empty<string>(), now, lifecycle));
        }

        private void PublishLifecycleObservation(string targetId, double now, PerceptionLifecycleState lifecycle)
        {
            string state = lifecycle == PerceptionLifecycleState.Lost ? "lost" :
                lifecycle == PerceptionLifecycleState.Remembered ? "remembered" : "forgotten";
            var knownFields = new Dictionary<string, string> { ["visibility_state"] = state };
            stateStore.ApplyOrRefresh(CreateObservation(targetId, knownFields, Array.Empty<string>(), now, lifecycle));
        }

        private Observation CreateObservation(string targetId, IReadOnlyDictionary<string, string> knownFields,
            IReadOnlyCollection<string> unknownFields, double now, PerceptionLifecycleState lifecycle)
        {
            int revision = worldState == null ? Math.Max(0, stateStore.Aggregator.LatestWorldRevision) : worldState.WorldRevision;
            return new Observation($"obs_{observer.EntityId}_visual_{targetId}", observer.EntityId,
                ObservationSourceType.ContinuousScan, $"visual:{targetId}", ObservationChannel.Visual, targetId,
                knownFields, unknownFields, 1f, revision, now,
                lifecycle == PerceptionLifecycleState.Remembered ? now + rememberedLifetimeSeconds : double.PositiveInfinity,
                lifecycle);
        }

        private bool HasClearLineOfSight(Vector3 origin, Vector3 target, Transform targetRoot, Transform visibleOccupant = null)
            => HasClearLineOfSight(origin, target, transform, targetRoot, occlusionMask, raycastHits, visibleOccupant);

        /// <summary>Same occlusion policy for a bounded social targeting query, without creating a sensor.</summary>
        public static bool HasClearLineOfSight(Vector3 origin, Vector3 target, Transform observerRoot,
            Transform targetRoot, LayerMask mask, RaycastHit[] hits, Transform visibleOccupant = null)
        {
            Vector3 direction = target - origin;
            float distance = direction.magnitude;
            if (distance < 0.001f) return true;
            int hitCount = Physics.RaycastNonAlloc(origin, direction / distance, hits, distance, mask,
                QueryTriggerInteraction.Ignore);
            if (hitCount == hits.Length) return false; // Saturated query cannot establish clear LOS.
            float closestDistance = float.PositiveInfinity;
            Collider closestCollider = null;
            for (int index = 0; index < hitCount; index++)
            {
                Collider collider = hits[index].collider;
                if (collider == null || collider.transform.IsChildOf(observerRoot)) continue;
                if (hits[index].distance >= closestDistance) continue;
                closestDistance = hits[index].distance;
                closestCollider = collider;
            }
            return closestCollider == null || closestCollider.transform == targetRoot || closestCollider.transform.IsChildOf(targetRoot) ||
                (visibleOccupant != null && closestCollider.transform.IsChildOf(visibleOccupant));
        }

        private static string FormatPosition(Vector3 position) => string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1:F2},{2:F2}", position.x, position.y, position.z);

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;
            Vector3 origin = transform.position + Vector3.up * eyeHeight;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
            Gizmos.DrawWireSphere(origin, enterDistance);
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.25f);
            Gizmos.DrawWireSphere(origin, maintainDistance);
            DrawFovBoundary(origin, enterHalfFovDegrees, enterDistance, Color.cyan);
            DrawFovBoundary(origin, maintainHalfFovDegrees, maintainDistance, Color.green);
        }

        private void DrawFovBoundary(Vector3 origin, float halfAngle, float range, Color color)
        {
            Quaternion leftRotation = Quaternion.AngleAxis(-halfAngle, Vector3.up);
            Quaternion rightRotation = Quaternion.AngleAxis(halfAngle, Vector3.up);
            Gizmos.color = color;
            Gizmos.DrawLine(origin, origin + leftRotation * transform.forward * range);
            Gizmos.DrawLine(origin, origin + rightRotation * transform.forward * range);
        }
    }
}
