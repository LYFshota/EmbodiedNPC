using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Short-range observer-local awareness. Unlike vision it has no FOV or occlusion rule.</summary>
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore))]
    public sealed class ProximitySensor : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore stateStore;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private WorldStateService worldState;
        [SerializeField, Min(0.1f)] private float scanIntervalSeconds = 0.25f;
        [SerializeField, Min(0f)] private float range = 1.5f;

        private readonly HashSet<string> currentNearbyEntityIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> nearbyEntityIdsLastScan = new(StringComparer.Ordinal);
        private double nextScanAt;
        private long nextContactSequence;

        public IReadOnlyCollection<string> CurrentNearbyEntityIds => currentNearbyEntityIds;
        public float Range => range;

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (stateStore == null) stateStore = GetComponent<PerceivedStateStore>();
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            if (worldState == null) worldState = FindAnyObjectByType<WorldStateService>();
        }

        private void OnEnable() => nextScanAt = 0d;

        private void Update()
        {
            double now = Time.unscaledTimeAsDouble;
            if (now < nextScanAt) return;
            nextScanAt = now + scanIntervalSeconds;
            ScanNow();
        }

        [ContextMenu("Scan proximity now")]
        public void ScanNow()
        {
            if (observer == null || stateStore == null || entityRegistry == null || !entityRegistry.IsReady) return;
            currentNearbyEntityIds.Clear();
            double now = Time.unscaledTimeAsDouble;
            foreach (WorldEntity candidate in entityRegistry.RegisteredEntities)
            {
                if (candidate == null || candidate == observer || !candidate.isActiveAndEnabled) continue;
                PerceivableEntity perceivable = candidate.GetComponent<PerceivableEntity>();
                if (perceivable == null || !perceivable.isActiveAndEnabled) continue;
                if ((perceivable.VisualAnchor.position - transform.position).sqrMagnitude > range * range) continue;

                currentNearbyEntityIds.Add(candidate.EntityId);
                var knownFields = new Dictionary<string, string>
                {
                    ["proximity_state"] = "nearby"
                };
                perceivable.WriteVisibleFields(knownFields);
                stateStore.ApplyOrRefresh(CreateObservation(candidate.EntityId, ObservationChannel.Proximity,
                    $"proximity:{candidate.EntityId}", knownFields, now));
            }

            foreach (string targetId in nearbyEntityIdsLastScan)
            {
                if (currentNearbyEntityIds.Contains(targetId)) continue;
                var knownFields = new Dictionary<string, string> { ["proximity_state"] = "out_of_range" };
                stateStore.ApplyOrRefresh(CreateObservation(targetId, ObservationChannel.Proximity,
                    $"proximity:{targetId}", knownFields, now));
            }
            nearbyEntityIdsLastScan.Clear();
            nearbyEntityIdsLastScan.UnionWith(currentNearbyEntityIds);
        }

        /// <summary>Integration point for interaction or collision systems that have authoritative contact evidence.</summary>
        public ObservationApplyResult ReportContact(WorldEntity target)
        {
            if (target == null || observer == null || stateStore == null || string.IsNullOrWhiteSpace(target.EntityId))
                return new ObservationApplyResult(ObservationApplyCode.Invalid, null, stateStore == null ? 0 : stateStore.Aggregator.PerceptionRevision);
            var knownFields = new Dictionary<string, string> { ["contact_state"] = "contacted" };
            return stateStore.Apply(CreateObservation(target.EntityId, ObservationChannel.Contact,
                $"contact:{target.EntityId}:{++nextContactSequence}", knownFields, Time.unscaledTimeAsDouble));
        }

        private Observation CreateObservation(string targetId, ObservationChannel channel, string sourceId,
            IReadOnlyDictionary<string, string> knownFields, double now)
        {
            int revision = worldState == null ? Math.Max(0, stateStore.Aggregator.LatestWorldRevision) : worldState.WorldRevision;
            return new Observation($"obs_{observer.EntityId}_{sourceId.Replace(':', '_')}", observer.EntityId,
                ObservationSourceType.ContinuousScan, sourceId, channel, targetId, knownFields, Array.Empty<string>(),
                1f, revision, now, double.PositiveInfinity, PerceptionLifecycleState.Tracked);
        }
    }
}
