using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Entry point for self, system and explicitly addressed information; it is not a physical sensor.</summary>
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore))]
    public sealed class DirectObservationReceiver : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore stateStore;

        public string ObserverEntityId => observer == null ? string.Empty : observer.EntityId;

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (stateStore == null) stateStore = GetComponent<PerceivedStateStore>();
        }

        public ObservationApplyResult Receive(GameplayEvent gameplayEvent, string subjectEntityId,
            IReadOnlyDictionary<string, string> knownFields, IReadOnlyCollection<string> unknownFields)
        {
            if (gameplayEvent == null || stateStore == null || string.IsNullOrWhiteSpace(ObserverEntityId))
                return new ObservationApplyResult(ObservationApplyCode.Invalid, null, 0);

            Observation observation = new(
                $"obs_{ObserverEntityId}_{gameplayEvent.EventId}_direct",
                ObserverEntityId,
                ObservationSourceType.Direct,
                gameplayEvent.EventId,
                ObservationChannel.Direct,
                subjectEntityId,
                knownFields,
                unknownFields,
                1f,
                gameplayEvent.WorldRevision,
                Time.realtimeSinceStartupAsDouble,
                double.PositiveInfinity,
                PerceptionLifecycleState.Tracked,
                gameplayEvent.EventId,
                gameplayEvent.OccurredAt,
                traceId: gameplayEvent.TraceId);
            return stateStore.Apply(observation);
        }
    }
}
