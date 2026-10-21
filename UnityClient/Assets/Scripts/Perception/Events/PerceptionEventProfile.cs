using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Authoring data that turns one authoritative gameplay event into a bounded hearing stimulus.</summary>
    [CreateAssetMenu(menuName = "Embodied NPC/Perception/Event Profile", fileName = "PerceptionEventProfile")]
    public sealed class PerceptionEventProfile : ScriptableObject
    {
        [SerializeField] private GameplayEventType eventType = GameplayEventType.Unknown;
        [SerializeField, Min(0.1f)] private float hearingRange = 8f;
        [SerializeField, Range(0f, 1f)] private float baseIntensity = 0.8f;
        [SerializeField, Min(0.1f)] private float observationTtlSeconds = 5f;
        [SerializeField] private string audioCategory = "world_event";

        public GameplayEventType EventType => eventType;
        public float HearingRange => hearingRange;
        public float BaseIntensity => baseIntensity;
        public float ObservationTtlSeconds => observationTtlSeconds;
        public string AudioCategory => string.IsNullOrWhiteSpace(audioCategory) ? eventType.ToString() : audioCategory;
    }
}
