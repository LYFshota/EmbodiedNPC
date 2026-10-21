using System;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Transient spatial evidence. It contains raw event facts only until a sensor discloses a safe subset.</summary>
    public sealed class AudioStimulus
    {
        public AudioStimulus(string stimulusId, GameplayEvent gameplayEvent, PerceptionEventProfile profile, double createdAt)
        {
            if (string.IsNullOrWhiteSpace(stimulusId)) throw new ArgumentException("stimulusId is required.", nameof(stimulusId));
            GameplayEvent = gameplayEvent ?? throw new ArgumentNullException(nameof(gameplayEvent));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            StimulusId = stimulusId;
            CreatedAt = createdAt;
        }

        public string StimulusId { get; }
        public GameplayEvent GameplayEvent { get; }
        public PerceptionEventProfile Profile { get; }
        public double CreatedAt { get; }
        public Vector3 Origin => GameplayEvent.Position;
    }
}
