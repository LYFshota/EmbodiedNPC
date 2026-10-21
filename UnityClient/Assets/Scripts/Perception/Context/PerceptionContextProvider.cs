using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>MonoBehaviour boundary used by dialogue; it exposes only a bounded snapshot of this NPC's own state.</summary>
    [RequireComponent(typeof(PerceivedStateStore))]
    public sealed class PerceptionContextProvider : MonoBehaviour
    {
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField, Min(1)] private int maxObservations = 5;
        [SerializeField] private FixedSeatBinding selfSeat;
        private VisionSensor vision;
        private EmbodiedNPC.World.WorldEntity observer;
        private ContextSnapshotBuilder builder;

        private void Awake()
        {
            if (perceivedState == null) perceivedState = GetComponent<PerceivedStateStore>();
            builder = new ContextSnapshotBuilder();
            vision = GetComponent<VisionSensor>();
            observer = GetComponent<EmbodiedNPC.World.WorldEntity>();
        }

        public ContextSnapshot BuildSnapshot(string playerUtterance)
        {
            if (perceivedState == null || perceivedState.Aggregator == null) return null;
            // Refresh at request creation: a movement between periodic scans must not keep live seat facts.
            if (vision != null && vision.isActiveAndEnabled) vision.ScanNow();
            return builder.Build(perceivedState.Aggregator, playerUtterance, Time.unscaledTimeAsDouble, maxObservations,
                perceivedState.SpatialMemory, selfSeat == null ? null : selfSeat.ReadSelf(observer));
        }
    }
}
