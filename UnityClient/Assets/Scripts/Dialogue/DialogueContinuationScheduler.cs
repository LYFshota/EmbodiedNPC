using System;
using System.Collections.Generic;
using System.Globalization;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using UnityEngine;

namespace EmbodiedNPC.Dialogue
{
    /// <summary>One optional, ACK-gated opportunity for the currently selected dialogue. No retries.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueContinuationScheduler : MonoBehaviour
    {
        [SerializeField] private bool featureEnabled;
        [SerializeField, Min(0.1f)] private float waitSeconds = 4f;
        [SerializeField] private DialogueController controller;
        [SerializeField] private TextUtteranceInput textInput;
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private AgentEventRouter router;

        private CognitiveContextComposer composer;
        private CognitiveRunDriver driver;
        private AgentOrchestrator orchestrator;
        private CognitiveRequest parent;
        private CognitiveRequest activeRequest;
        private PresentedCognitiveUtterance candidate;
        private ValidatedExperienceAppendResult anchor;
        private string persistenceFailure;
        private string eventId;
        private bool eventClaimed;
        private bool attempted;
        private double dueAt;
        private string previousText = string.Empty;
        private string latestDisplayedRequest;
        private string latestDisplayedSession;
        private bool subscribed;

        public bool FeatureEnabled => featureEnabled;
        public int AttemptsThisTurn => attempted ? 1 : 0;
        public string LastStatus { get; private set; } = "disabled";
        public CognitiveRequest ActiveRequest => activeRequest;

        private void OnEnable() => Subscribe();
        private void OnDisable() { Invalidate("component_disabled"); Unsubscribe(); Unbind(); }
        private void LateUpdate() => Tick(Time.realtimeSinceStartupAsDouble);

        public void Configure(DialogueController dialogue, TextUtteranceInput input,
            BackendWebSocketClient client, AgentEventRouter eventRouter)
        {
            Invalidate("reconfigured"); Unsubscribe(); Unbind();
            controller = dialogue; textInput = input; backendClient = client; router = eventRouter;
            Subscribe(); Bind(controller?.SelectedTarget);
        }

        public void SetFeatureEnabled(bool enabled)
        {
            if (featureEnabled == enabled) return;
            featureEnabled = enabled;
            Invalidate(enabled ? "enabled_waiting_for_new_player_turn" : "disabled");
        }

        private bool SessionMatches => isActiveAndEnabled && featureEnabled && controller != null &&
            controller.isActiveAndEnabled && controller.HasActiveSession && controller.IsPresentationVisible &&
            backendClient != null && backendClient.IsConnected && parent != null &&
            controller.SelectedTarget?.Entity?.EntityId == parent.run.agent_entity_id &&
            controller.ActiveSpeakerEntityId == parent.context.relevant_player?.player_entity_id &&
            controller.CurrentConversationId == Payload(parent.context.current_event, "conversation_id") &&
            controller.CheckEligibility(controller.SelectedTarget).IsEligible;

        private bool Editing => !string.IsNullOrEmpty(textInput?.CurrentText) ||
                                !string.IsNullOrEmpty(UnityEngine.Input.compositionString);

        public void Tick(double now)
        {
            if (parent == null) return;
            if (!SessionMatches) { Invalidate("session_unavailable"); return; }
            string text = textInput?.CurrentText ?? string.Empty;
            if (Editing || text != previousText)
            {
                previousText = text; dueAt = now + waitSeconds;
                if (activeRequest != null) Invalidate("player_editing");
                return;
            }
            if (attempted || candidate == null || anchor == null || now < dueAt ||
                orchestrator == null || orchestrator.HasActiveRun || router == null) return;
            attempted = true; // Routing failure, silence, cancellation and errors all consume the sole attempt.
            eventId = "agent_event_" + Guid.NewGuid().ToString("N");
            eventClaimed = false;
            var payload = new Dictionary<string, string> {
                ["conversation_id"] = controller.CurrentConversationId,
                ["player_entity_id"] = controller.ActiveSpeakerEntityId,
                ["parent_request_id"] = parent.request_id,
                ["parent_event_id"] = parent.context.current_event.event_id,
                ["continuation_index"] = "1", ["last_response_event_id"] = anchor.EventId,
                ["last_response_seq"] = anchor.ExperienceSequence.ToString(CultureInfo.InvariantCulture),
                ["source_session_id"] = ClientSessionIdentity.Current,
            };
            var previousRun = orchestrator.LastRun;
            int routed = router.Route(new AgentEvent(eventId, AgentEventTypes.DialogueContinue,
                parent.run.agent_entity_id, parent.run.agent_entity_id, string.Empty, payload, now,
                backendClient.CurrentWorldRevision, "cognitive-trace-" + Guid.NewGuid().ToString("N")));
            LastStatus = routed == 1 && !ReferenceEquals(previousRun, orchestrator.LastRun) && activeRequest != null
                ? "generating" : "route_not_started";
        }

        public bool TryClaimEvent(AgentEvent evt)
        {
            if (!SessionMatches || !attempted || eventClaimed || evt.EventId != eventId ||
                evt.SourceEntityId != parent.run.agent_entity_id || evt.TargetEntityId != evt.SourceEntityId ||
                !evt.TryGetPayload("parent_request_id", out string parentId) || parentId != parent.request_id ||
                !evt.TryGetPayload("parent_event_id", out string parentEvent) || parentEvent != parent.context.current_event.event_id)
                return false;
            eventClaimed = true;
            return true;
        }

        public void RegisterRequest(CognitiveRequest request)
        {
            if (eventClaimed && request?.context?.current_event?.event_id == eventId) activeRequest = request;
        }

        public bool CanCommit(CognitiveRequest request) => SessionMatches && !Editing &&
            ReferenceEquals(activeRequest, request) && request.context.current_event.event_id == eventId &&
            composer.HasCurrentParticipantIdentities(request);

        public void AddDisplayedResponseDependency(IDictionary<string, string> payload, string agent, string conversation)
        {
            if (!featureEnabled || latestDisplayedRequest == null || composer == null || controller?.SelectedTarget?.Entity?.EntityId != agent ||
                controller.CurrentConversationId != conversation) return;
            payload["required_response_request_id"] = latestDisplayedRequest;
            payload["required_response_session_id"] = latestDisplayedSession;
        }

        private void HandleValidated(UserUtterance _) => Invalidate("player_input");
        private void HandleAccepted(UserUtterance _)
        {
            if (!featureEnabled) return;
            Bind(controller.SelectedTarget);
            CognitiveRequest request = composer?.LastRequest;
            if (request?.context?.current_event?.event_type != AgentEventTypes.PlayerUtterance) return;
            parent = request; attempted = false; candidate = null; anchor = null;
            persistenceFailure = null;
            eventId = null; eventClaimed = false; activeRequest = null;
            previousText = string.Empty;
            LastStatus = "awaiting_reply";
        }

        private void HandlePresented(PresentedCognitiveUtterance presented)
        {
            if (!featureEnabled || !presented.IsDialogueEvidence || parent == null ||
                presented.ConversationId != controller.CurrentConversationId ||
                presented.OwnerAgentEntityId != parent.run.agent_entity_id ||
                presented.PlayerEntityId != controller.ActiveSpeakerEntityId) return;
            latestDisplayedRequest = presented.RequestId; latestDisplayedSession = ClientSessionIdentity.Current;
            if (presented.ContinuationEvent != null) { candidate = null; return; }
            if (presented.RequestId != parent.request_id) return;
            if (presented.DialogueTurnPolicy != "MAY_CONTINUE" || presented.EpisodeDirective == EpisodeDirectives.End)
            { candidate = null; LastStatus = "wait_for_player"; return; }
            candidate = presented;
            dueAt = presented.PresentedAt + waitSeconds;
            // A full append queue can settle synchronously in an earlier presentation
            // subscriber. Preserve that exact parent settlement regardless of ordering.
            if (persistenceFailure != null) { candidate = null; LastStatus = "persistence_failed_" + persistenceFailure; }
            else LastStatus = anchor != null ? "waiting" : "awaiting_ack";
        }

        private void HandleAppendSettled(ExperienceDraftDto draft, ValidatedExperienceAppendResult result, string status)
        {
            if (parent == null || draft.event_type != "dialogue_response" ||
                draft.request_id != parent.request_id || draft.trace_id != parent.trace_id ||
                draft.observer_id != parent.run.agent_entity_id ||
                draft.conversation_id != Payload(parent.context.current_event, "conversation_id")) return;
            if (result?.IsPersisted == true) { anchor = result; if (candidate != null) LastStatus = "waiting"; }
            else { persistenceFailure = status; candidate = null; anchor = null; LastStatus = "persistence_failed_" + status; }
        }

        private void HandleSettled(CognitiveRequest request, string status)
        {
            if (!ReferenceEquals(request, activeRequest)) return;
            activeRequest = null; candidate = null; anchor = null;
            LastStatus = status;
        }

        private void Invalidate(string reason)
        {
            var request = activeRequest;
            activeRequest = null; eventId = null; candidate = null; anchor = null; parent = null;
            persistenceFailure = null;
            if (request != null) driver?.CancelContinuation(request.request_id);
            LastStatus = reason;
        }
        private void HandleSession(DialogueParticipant target)
        {
            Invalidate("session_changed"); latestDisplayedRequest = null; latestDisplayedSession = null; Bind(target);
        }
        private void HandleVisibility(bool visible) { if (!visible) Invalidate("ui_hidden"); }
        private void HandleConnection(string _) { if (!backendClient.IsConnected) Invalidate("disconnected"); }

        private void Bind(DialogueParticipant target)
        {
            var next = target == null ? null : target.GetComponent<CognitiveContextComposer>();
            if (next == composer) return;
            Unbind(); composer = next;
            if (composer == null) return;
            composer.SetContinuationScheduler(this);
            driver = target.GetComponent<CognitiveRunDriver>(); orchestrator = target.GetComponent<AgentOrchestrator>();
            if (driver != null) { driver.UtterancePresented += HandlePresented; driver.ContinuationSettled += HandleSettled; }
        }
        private void Unbind()
        {
            if (driver != null) { driver.UtterancePresented -= HandlePresented; driver.ContinuationSettled -= HandleSettled; }
            if (composer?.ContinuationScheduler == this) composer.SetContinuationScheduler(null);
            driver = null; composer = null; orchestrator = null;
        }
        private void Subscribe()
        {
            if (subscribed || !isActiveAndEnabled || controller == null || backendClient == null) return;
            controller.ValidatedUtteranceReady += HandleValidated; controller.DialogueSubmissionAccepted += HandleAccepted;
            controller.ActiveSessionChanged += HandleSession; controller.PresentationVisibilityChanged += HandleVisibility;
            backendClient.ConnectionStatusChanged += HandleConnection;
            backendClient.ExperienceAppendSettled += HandleAppendSettled;
            subscribed = true; Bind(controller.SelectedTarget);
        }
        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (controller != null) {
                controller.ValidatedUtteranceReady -= HandleValidated; controller.DialogueSubmissionAccepted -= HandleAccepted;
                controller.ActiveSessionChanged -= HandleSession; controller.PresentationVisibilityChanged -= HandleVisibility; }
            if (backendClient != null) { backendClient.ConnectionStatusChanged -= HandleConnection;
                backendClient.ExperienceAppendSettled -= HandleAppendSettled; }
            subscribed = false;
        }
        private static string Payload(CognitiveEventDto evt, string key)
        {
            foreach (var pair in evt?.payload ?? Array.Empty<CognitiveKeyValueDto>()) if (pair.key == key) return pair.value;
            return null;
        }
    }
}
