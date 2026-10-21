using System;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Scene-local authoritative event boundary for systems that have changed World State or gameplay.</summary>
    public sealed class GameplayEventHub : MonoBehaviour
    {
        [SerializeField] private WorldStateService worldState;
        private long nextSequence;

        public event Action<GameplayEvent> GameplayEventPublished;

        private void Awake()
        {
            if (worldState == null) worldState = FindAnyObjectByType<WorldStateService>();
        }

        public GameplayEvent Publish(GameplayEventType eventType, Vector3 position, string actorEntityId = "",
            string targetEntityId = "", string traceId = "")
        {
            int worldRevision = worldState == null ? 0 : worldState.WorldRevision;
            GameplayEvent gameplayEvent = new($"evt_gameplay_{++nextSequence:D6}", eventType, actorEntityId,
                targetEntityId, traceId, position, worldRevision, Time.unscaledTimeAsDouble);
            GameplayEventPublished?.Invoke(gameplayEvent);
            return gameplayEvent;
        }

    }
}
