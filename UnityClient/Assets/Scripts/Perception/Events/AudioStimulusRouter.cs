using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using EmbodiedNPC.Events;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Maps configured event profiles to stimuli and routes each stimulus only to registered hearing sensors.</summary>
    public sealed class AudioStimulusRouter : MonoBehaviour
    {
        [SerializeField] private GameplayEventHub eventHub;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private PerceptionEventProfile[] eventProfiles = Array.Empty<PerceptionEventProfile>();

        private readonly Dictionary<GameplayEventType, PerceptionEventProfile> profilesByEventType = new();
        private long nextStimulusSequence;
        public PerceptionEventProfile SpeechProfile => profilesByEventType.TryGetValue(GameplayEventType.Speech, out var profile) ? profile : null;

        private void Awake()
        {
            if (eventHub == null) eventHub = GetComponent<GameplayEventHub>();
            if (entityRegistry == null) entityRegistry = GetComponent<EntityRegistry>();
            RebuildProfileLookup();
        }

        private void OnEnable()
        {
            if (eventHub != null) eventHub.GameplayEventPublished += Route;
        }

        private void OnDisable()
        {
            if (eventHub != null) eventHub.GameplayEventPublished -= Route;
        }

        private void RebuildProfileLookup()
        {
            profilesByEventType.Clear();
            foreach (PerceptionEventProfile profile in eventProfiles)
            {
                if (profile == null || profile.EventType == GameplayEventType.Unknown) continue;
                profilesByEventType[profile.EventType] = profile;
            }
        }

        public void ConfigureSpeech(EntityRegistry registry, PerceptionEventProfile speechProfile)
        {
            entityRegistry = registry;
            eventProfiles = new[] { speechProfile };
            RebuildProfileLookup();
        }

        // Route only speech already presented by gameplay. The returned count is
        // applied bystander observations, not candidates inside the nominal radius.
        // This path does not publish a cognition wake event.
        public int RouteSpeech(AgentEvent speech, Vector3 origin)
        {
            if (!isActiveAndEnabled || speech?.EventType != AgentEventTypes.SpeechPresented ||
                entityRegistry == null || !entityRegistry.IsReady ||
                !profilesByEventType.TryGetValue(GameplayEventType.Speech, out var profile) ||
                !entityRegistry.TryGetEntity(speech.SourceEntityId, out var speaker)) return 0;
            entityRegistry.TryGetEntity(speech.TargetEntityId, out var recipient);
            int applied = 0;
            foreach (var entity in entityRegistry.RegisteredEntities)
            {
                if (entity == null || !entity.isActiveAndEnabled || !entity.IsAgent(AgentRole.Npc) ||
                    entity.EntityId == speech.SourceEntityId || entity.EntityId == speech.TargetEntityId) continue;
                var sensor = entity.GetComponent<HearingSensor>();
                if (sensor != null && sensor.isActiveAndEnabled && sensor.ReceiveSpeech(speech, origin, profile,
                    speaker, recipient).IsApplied) applied++;
            }
            return applied;
        }

        private void Route(GameplayEvent gameplayEvent)
        {
            if (gameplayEvent == null || entityRegistry == null || !entityRegistry.IsReady ||
                !profilesByEventType.TryGetValue(gameplayEvent.EventType, out PerceptionEventProfile profile)) return;

            AudioStimulus stimulus = new($"stim_audio_{++nextStimulusSequence:D6}", gameplayEvent, profile,
                Time.unscaledTimeAsDouble);
            foreach (WorldEntity entity in entityRegistry.RegisteredEntities)
            {
                if (entity == null || !entity.isActiveAndEnabled) continue;
                HearingSensor sensor = entity.GetComponent<HearingSensor>();
                if (sensor != null && sensor.isActiveAndEnabled) sensor.Receive(stimulus);
            }
        }
    }
}
