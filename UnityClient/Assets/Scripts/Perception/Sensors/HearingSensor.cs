using System;
using System.Collections.Generic;
using EmbodiedNPC.World;
using EmbodiedNPC.Events;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    public enum HearingIdentityMode { VisualRequired, EventIdentity }

    /// <summary>Converts routed sound into observer-local evidence. Participant identity depends on the explicit visual/event identity policy.</summary>
    [RequireComponent(typeof(WorldEntity), typeof(PerceivedStateStore))]
    public sealed class HearingSensor : MonoBehaviour
    {
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore stateStore;
        [SerializeField, Range(0f, 1f)] private float minimumIntensity = 0.1f;
        [SerializeField] private float earHeight = 1.5f;
        [SerializeField] private HearingIdentityMode identityMode = HearingIdentityMode.VisualRequired;
        [Tooltip("Optional solid walls/partitions that block intelligible speech. Independent of visual recognition.")]
        [SerializeField] private Collider[] speechOccluders = Array.Empty<Collider>();

        public HearingIdentityMode IdentityMode => identityMode;
        public Vector3 EarPosition => transform.position + Vector3.up * earHeight;
        public void ConfigureIdentity(HearingIdentityMode mode) => identityMode = mode;

        private void Awake()
        {
            if (observer == null) observer = GetComponent<WorldEntity>();
            if (stateStore == null) stateStore = GetComponent<PerceivedStateStore>();
        }

        // A profile radius is only nominal propagation: intensity, occlusion and
        // routing must all pass before an actual bystander Observation is applied.
        public ObservationApplyResult ReceiveSpeech(AgentEvent speech, Vector3 origin,
            PerceptionEventProfile profile, WorldEntity speaker, WorldEntity recipient)
        {
            var invalid = new ObservationApplyResult(ObservationApplyCode.Invalid, null,
                stateStore?.Aggregator?.PerceptionRevision ?? 0);
            if (!isActiveAndEnabled || observer == null || stateStore?.Aggregator == null ||
                speech?.EventType != AgentEventTypes.SpeechPresented || profile == null ||
                speaker == null || speaker.EntityId != speech.SourceEntityId ||
                observer.EntityId == speech.SourceEntityId || observer.EntityId == speech.TargetEntityId ||
                !speech.TryGetPayload("text", out string text) || string.IsNullOrWhiteSpace(text) || text.Length > 500 ||
                string.IsNullOrWhiteSpace(speech.TraceId)) return invalid;
            Vector3 ears = transform.position + Vector3.up * earHeight;
            float distance = Vector3.Distance(ears, origin);
            float intensity = HearingSensorMath.CalculateIntensity(profile.BaseIntensity, distance, profile.HearingRange);
            if (intensity <= 0f || intensity < minimumIntensity) return invalid;
            if (IsSpeechOccluded(origin, ears)) return invalid;
            var fields = new Dictionary<string, string>
            {
                ["event_type"] = AgentEventTypes.HeardSpeech, ["observer_entity_id"] = observer.EntityId,
                ["text"] = text, ["social_role"] = "bystander", ["occurrence_id"] = speech.EventId,
                ["source_channel"] = "Audio", ["model_content_is_not_world_truth"] = "true"
            };
            foreach (string key in new[] { "parent_source_event_id", "request_id", "run_id" })
                if (speech.TryGetPayload(key, out string value) && !string.IsNullOrWhiteSpace(value)) fields[key] = value;
            var unknown = new List<string> { "exact_position" };
            // Strict mode retains visual identification. The Demo can explicitly trust a legal speech event.
            var vision = GetComponent<VisionSensor>();
            bool Recognized(WorldEntity entity)
            {
                if (entity == null || vision == null || !vision.isActiveAndEnabled) return false;
                foreach (string id in vision.CurrentVisibleEntityIds) if (id == entity.EntityId) return true;
                return false;
            }
            bool eventIdentity = identityMode == HearingIdentityMode.EventIdentity;
            bool speakerKnown = eventIdentity || Recognized(speaker);
            if (speakerKnown)
            {
                fields["actor_entity_id"] = speaker.EntityId;
                fields["actor_epistemic_status"] = eventIdentity ? "identified_from_speech_event" : "identified_from_visual_observation";
            }
            else unknown.Add("actor_entity_id");
            Vector3 direction = recipient == null ? Vector3.zero : recipient.transform.position - speaker.transform.position;
            direction.y = 0f;
            Vector3 forward = speaker.transform.forward; forward.y = 0f;
            bool targetKnown = eventIdentity
                ? recipient != null && recipient.EntityId == speech.TargetEntityId && recipient.isActiveAndEnabled &&
                    recipient.EntityKind == EntityKind.Agent
                : speakerKnown && Recognized(recipient) && direction.sqrMagnitude > .001f &&
                    Vector3.Dot(forward.normalized, direction.normalized) >= .5f;
            if (targetKnown)
            {
                fields["target_entity_id"] = recipient.EntityId;
                fields["target_epistemic_status"] = eventIdentity ? "identified_from_speech_event" : "inferred_from_visible_interaction";
            }
            else unknown.Add("target_entity_id");
            double now = Time.realtimeSinceStartupAsDouble;
            return stateStore.Apply(new Observation($"{speech.EventId}_{observer.EntityId}_audio", observer.EntityId,
                ObservationSourceType.GameplayEvent, speech.EventId, ObservationChannel.Audio, string.Empty,
                fields, unknown, intensity, speech.WorldRevision ?? Math.Max(0, stateStore.Aggregator.LatestWorldRevision),
                now, now + profile.ObservationTtlSeconds, PerceptionLifecycleState.Detected,
                speech.EventId, speech.OccurredAt, traceId: speech.TraceId));
        }

        private bool IsSpeechOccluded(Vector3 origin, Vector3 ears)
        {
            Vector3 offset = ears - origin;
            float distance = offset.magnitude;
            if (distance <= .001f || speechOccluders == null) return false;
            var ray = new Ray(origin, offset / distance);
            foreach (Collider wall in speechOccluders)
                if (wall != null && wall.enabled && wall.gameObject.activeInHierarchy && !wall.isTrigger &&
                    wall.Raycast(ray, out _, distance)) return true;
            return false;
        }

        public ObservationApplyResult Receive(AudioStimulus stimulus)
        {
            if (stimulus == null || observer == null || stateStore == null || string.IsNullOrWhiteSpace(observer.EntityId))
                return new ObservationApplyResult(ObservationApplyCode.Invalid, null, stateStore == null ? 0 : stateStore.Aggregator.PerceptionRevision);

            Vector3 earPosition = transform.position + Vector3.up * earHeight;
            float distance = Vector3.Distance(earPosition, stimulus.Origin);
            float intensity = HearingSensorMath.CalculateIntensity(stimulus.Profile.BaseIntensity, distance, stimulus.Profile.HearingRange);
            if (intensity < minimumIntensity)
                return new ObservationApplyResult(ObservationApplyCode.Invalid, null, stateStore.Aggregator.PerceptionRevision);

            Dictionary<string, string> knownFields = new()
            {
                ["event_type"] = stimulus.GameplayEvent.EventType.ToString(),
                ["audio_category"] = stimulus.Profile.AudioCategory,
                ["approximate_direction"] = HearingSensorMath.DirectionBand(transform.forward, stimulus.Origin - earPosition),
                ["approximate_distance"] = HearingSensorMath.DistanceBand(distance, stimulus.Profile.HearingRange),
                ["intensity"] = intensity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            };
            string[] unknownFields = { "actor_entity_id", "target_entity_id", "exact_position" };
            double now = Time.unscaledTimeAsDouble;
            Observation observation = new($"obs_{observer.EntityId}_{stimulus.StimulusId}_audio", observer.EntityId,
                ObservationSourceType.GameplayEvent, stimulus.StimulusId, ObservationChannel.Audio, string.Empty,
                knownFields, unknownFields, intensity, stimulus.GameplayEvent.WorldRevision, now,
                now + stimulus.Profile.ObservationTtlSeconds, PerceptionLifecycleState.Detected,
                stimulus.GameplayEvent.EventId, stimulus.GameplayEvent.OccurredAt);
            return stateStore.Apply(observation);
        }
    }
}
