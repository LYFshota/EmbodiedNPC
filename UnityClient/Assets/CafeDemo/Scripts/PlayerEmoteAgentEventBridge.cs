using System;
using System.Collections.Generic;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Each observer gates the semantic start on its own current visual evidence.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerEmoteAgentEventBridge : MonoBehaviour
    {
        [SerializeField] private PlayerEmoteController source;
        [SerializeField] private WorldEntity observer;
        [SerializeField] private PerceivedStateStore perceivedState;
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private WorldStateService worldState;
        private bool subscribed;
        public int EventCount { get; private set; }
        public AgentEvent LastEvent { get; private set; }
        public Observation LastObservation { get; private set; }
        public WorldEntity Observer => isActiveAndEnabled ? observer : null;

        public void RefreshVision()
        {
            var vision = observer == null ? null : observer.GetComponent<VisionSensor>();
            if (isActiveAndEnabled && vision != null && vision.isActiveAndEnabled) vision.ScanNow();
        }
        public bool CanSee(string entityId)
        {
            var vision = observer == null ? null : observer.GetComponent<VisionSensor>();
            if (!isActiveAndEnabled || vision == null || !vision.isActiveAndEnabled) return false;
            foreach (string visible in vision.CurrentVisibleEntityIds) if (visible == entityId) return true;
            return false;
        }
        public bool CanRespond
        {
            get
            {
                if (!isActiveAndEnabled || observer == null || !observer.isActiveAndEnabled) return false;
                var motion = observer.GetComponent<MotionChannelCoordinator>();
                var runs = observer.GetComponent<AgentOrchestrator>();
                var speech = observer.GetComponent<SpeechPresentationLifecycle>();
                var daily = observer.GetComponent<DailyBehaviorController>();
                var dialogue = FindAnyObjectByType<DialogueController>();
                return !(motion != null && (motion.IsTaskMotionActive || motion.IsEmergencyMotionActive || motion.IsCarryPresentationActive)) &&
                    !(runs != null && runs.HasActiveRun) && !(speech != null && speech.IsSpeaking) &&
                    !(daily != null && daily.isActiveAndEnabled && (daily.IsActivitySuspended || daily.IsRestingAtWindow)) &&
                    !(dialogue != null && dialogue.IsPresentationVisible && dialogue.SelectedTarget?.Entity == observer);
            }
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        public void Configure(PlayerEmoteController player, WorldEntity owner, PerceivedStateStore state,
            AgentEventRouter eventRouter, WorldStateService world)
        {
            Unsubscribe(); source = player; observer = owner; perceivedState = state; router = eventRouter; worldState = world;
            if (isActiveAndEnabled) Subscribe();
        }
        public bool TryProject(PlayerEmotePerformed performed, out AgentEvent agentEvent)
        {
            agentEvent = null;
            if (performed == null || (performed.EmoteId != "wave" && performed.EmoteId != "cheer" && performed.EmoteId != "flex") || observer == null ||
                perceivedState == null || perceivedState.Aggregator == null ||
                !string.Equals(perceivedState.ObserverEntityId, observer.EntityId, StringComparison.Ordinal)) return false;
            RefreshVision();
            if (!CanSee(performed.ActorEntityId)) return false;
            bool addressed = performed.EmoteId == "wave" && performed.TargetEntityId == observer.EntityId;
            bool targetVisible = performed.EmoteId == "wave" && !string.IsNullOrEmpty(performed.TargetEntityId) &&
                (addressed || CanSee(performed.TargetEntityId));
            var fields = new Dictionary<string, string>
            {
                ["event_type"] = AgentEventTypes.PlayerEmote,
                ["actor_entity_id"] = performed.ActorEntityId,
                [AgentEventPayloadKeys.EmoteId] = performed.EmoteId,
                ["emote_meaning"] = performed.EmoteId == "wave" ? "waving_greeting" :
                    performed.EmoteId == "cheer" ? "celebrating_or_expressing_joy_cause_unknown" : "flexing_or_playfully_showing_off_ability_and_personality_unknown",
                ["social_role"] = addressed ? "addressed" : "bystander",
                [AgentEventPayloadKeys.SocialObserver] = observer.EntityId,
                [AgentEventPayloadKeys.SocialResponseEligible] = performed.ResponderEntityId == observer.EntityId && CanRespond ? "true" : "false"
            };
            if (targetVisible)
            {
                fields["target_entity_id"] = performed.TargetEntityId;
                fields["target_epistemic_status"] = "inferred_from_visible_facing";
            }
            agentEvent = new AgentEvent($"{performed.EventId}:{observer.EntityId}", AgentEventTypes.PlayerEmote,
                performed.ActorEntityId, targetVisible ? performed.TargetEntityId : string.Empty, string.Empty, fields,
                // AgentEvent and execution snapshots use Unity realtime. Keep the source UTC in observation history below.
                Time.realtimeSinceStartupAsDouble, worldState == null ? null : worldState.WorldRevision,
                performed.TraceId, $"{performed.EventId}:visual:{observer.EntityId}");
            return true;
        }
        private void HandleEmote(PlayerEmotePerformed performed)
        {
            if (!TryProject(performed, out AgentEvent value)) return;
            var observation = new Observation(value.SourceObservationId, observer.EntityId,
                // Event-scoped evidence: never merge a transient emote/target into the actor's current entity record.
                ObservationSourceType.GameplayEvent, performed.EventId, ObservationChannel.Visual, string.Empty,
                value.Payload, string.IsNullOrEmpty(value.TargetEntityId) ?
                    new[] { "target_entity_id", "target_epistemic_status" } : Array.Empty<string>(), 1f,
                worldState == null ? Math.Max(0, perceivedState.Aggregator.LatestWorldRevision) : worldState.WorldRevision,
                Time.unscaledTimeAsDouble, Time.unscaledTimeAsDouble + 8d, sourceEventId: value.EventId,
                occurredAt: performed.OccurredAt, traceId: performed.TraceId);
            if (!perceivedState.Apply(observation).IsApplied) return;
            LastObservation = observation; LastEvent = value; EventCount++;
            router?.RouteObserved(value, observer.EntityId);
        }
        private void Subscribe()
        {
            if (subscribed || source == null) return;
            source.EmotePerformed += HandleEmote; subscribed = true;
        }
        private void Unsubscribe()
        {
            if (subscribed && source != null) source.EmotePerformed -= HandleEmote;
            subscribed = false;
        }
    }
}
