using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbodiedNPC.Perception
{
    /// <summary>Aggregated current knowledge for one perceived entity, owned by one NPC store.</summary>
    public sealed class PerceivedEntityRecord
    {
        private readonly Dictionary<string, string> knownFields = new();
        private readonly HashSet<string> unknownFields = new();

        public PerceivedEntityRecord(string entityId) => EntityId = entityId;
        public string EntityId { get; }
        public PerceptionLifecycleState LifecycleState { get; private set; } = PerceptionLifecycleState.Detected;
        public int LastWorldRevision { get; private set; } = -1;
        public int LastPerceptionRevision { get; private set; }
        public double LastObservedAt { get; private set; }
        public float Confidence { get; private set; }
        public IReadOnlyDictionary<string, string> KnownFields => new ReadOnlyDictionary<string, string>(knownFields);
        public IReadOnlyCollection<string> UnknownFields => unknownFields;

        internal void Apply(Observation observation)
        {
            foreach (KeyValuePair<string, string> pair in observation.KnownFields)
            {
                knownFields[pair.Key] = pair.Value;
                unknownFields.Remove(pair.Key);
            }
            foreach (string field in observation.UnknownFields)
                if (!knownFields.ContainsKey(field)) unknownFields.Add(field);

            LifecycleState = observation.LifecycleState;
            LastWorldRevision = observation.WorldRevision;
            LastPerceptionRevision = observation.PerceptionRevision;
            LastObservedAt = observation.ObservedAt;
            Confidence = observation.Confidence;
        }

        /// <summary>Records sensor freshness without turning a repeated scan into a new cognitive revision.</summary>
        internal void RefreshFreshness(Observation observation)
        {
            LastWorldRevision = observation.WorldRevision > LastWorldRevision ? observation.WorldRevision : LastWorldRevision;
            LastObservedAt = observation.ObservedAt;
        }
    }
}
