using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using UnityEngine;

namespace CafeDemo
{
    /// <summary> Cafe-only adapter from validated dialogue input into the shared Runtime v2 event chain. </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    [RequireComponent(typeof(AgentOrchestrator))]
    public sealed class CognitiveDialogueSubmissionHandler : MonoBehaviour, IDialogueSubmissionHandler
    {
        [SerializeField] private WorldEntity targetEntity;
        [SerializeField] private AgentOrchestrator orchestrator;
        [SerializeField] private CognitiveContextComposer contextComposer;
        [SerializeField] private AgentEventRouter router;
        [SerializeField] private BackendWebSocketClient backendClient;
        private DialogueContinuationScheduler continuationScheduler;

        public AgentEvent LastSubmittedEvent { get; private set; }
        public AcceptedDialogueTurn LastAcceptedTurn { get; private set; }

        private void Awake() => ResolveReferences();

        public void Configure(WorldEntity target, AgentOrchestrator runOrchestrator,
            AgentEventRouter eventRouter, BackendWebSocketClient client)
        {
            targetEntity = target;
            orchestrator = runOrchestrator;
            router = eventRouter;
            backendClient = client;
        }

        public DialogueSubmissionResult Submit(UserUtterance utterance)
        {
            LastAcceptedTurn = null;
            if (utterance == null || !utterance.IsValid || targetEntity == null || router == null ||
                !string.Equals(utterance.targetAgentId, targetEntity.EntityId, StringComparison.Ordinal))
                return DialogueSubmissionResult.Reject("COGNITIVE_EVENT_NOT_ROUTED");
            if (orchestrator?.CurrentRun?.IsServiceZoneConsideration == true &&
                !(targetEntity.GetComponent<EmbodiedNPC.Routines.ServiceZoneDwellCognitionSource>()?.IsConfiguredPlayer(utterance.speakerEntityId) ?? false))
                return DialogueSubmissionResult.Reject("COGNITIVE_EVENT_NOT_ROUTED");
            if (orchestrator?.CurrentRun?.IsCounterCleaningConsideration == true &&
                !(targetEntity.GetComponent<EmbodiedNPC.Routines.CounterCleaningCognitionSource>()?.IsConfiguredPlayer(utterance.speakerEntityId) ?? false))
                return DialogueSubmissionResult.Reject("COGNITIVE_EVENT_NOT_ROUTED");
            targetEntity.GetComponent<CognitiveRunDriver>()?.CancelLowPrioritySystemConsideration("player_input");
            if (orchestrator != null && orchestrator.HasActiveRun)
                return DialogueSubmissionResult.Reject("COGNITIVE_RUN_BUSY");

            CognitiveRun previousRun = orchestrator == null ? null : orchestrator.LastRun;
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentEventPayloadKeys.Text] = utterance.text,
                [AgentEventPayloadKeys.ConversationId] = utterance.conversationId,
            };
            if (continuationScheduler == null) continuationScheduler = FindAnyObjectByType<DialogueContinuationScheduler>();
            continuationScheduler?.AddDisplayedResponseDependency(payload, targetEntity.EntityId, utterance.conversationId);
            AgentEvent agentEvent = new($"agent_event_{Guid.NewGuid():N}", AgentEventTypes.PlayerUtterance,
                utterance.speakerEntityId, targetEntity.EntityId, string.Empty, payload,
                Time.realtimeSinceStartupAsDouble, backendClient == null ? 0 : backendClient.CurrentWorldRevision,
                traceId: $"cognitive-trace-{Guid.NewGuid():N}");

            if (router.Route(agentEvent) != 1)
                return DialogueSubmissionResult.Reject("COGNITIVE_EVENT_NOT_ROUTED");
            LastSubmittedEvent = agentEvent;
            if (orchestrator == null || ReferenceEquals(orchestrator.LastRun, previousRun))
                return DialogueSubmissionResult.Reject("COGNITIVE_RUN_NOT_STARTED");

            CognitiveRequest request = contextComposer == null ? null : contextComposer.LastRequest;
            bool requestMatchesEvent = request?.context?.current_event != null &&
                string.Equals(request.context.current_event.event_id, agentEvent.EventId, StringComparison.Ordinal);
            string requestId = requestMatchesEvent ? request.request_id : agentEvent.EventId;
            string traceId = requestMatchesEvent ? request.trace_id : agentEvent.TraceId;
            LastAcceptedTurn = new AcceptedDialogueTurn(false, requestId, traceId,
                utterance.conversationId, utterance.speakerEntityId, targetEntity.EntityId, utterance.text,
                agentEvent.WorldRevision ?? 0, agentEvent.OccurredAt, Time.realtimeSinceStartupAsDouble,
                agentEvent.EventId);
            return DialogueSubmissionResult.AcceptWithTurn(LastAcceptedTurn);
        }

        private void ResolveReferences()
        {
            if (targetEntity == null) targetEntity = GetComponent<WorldEntity>();
            if (orchestrator == null) orchestrator = GetComponent<AgentOrchestrator>();
            if (contextComposer == null) contextComposer = GetComponent<CognitiveContextComposer>();
            if (router == null) router = FindAnyObjectByType<AgentEventRouter>();
            if (backendClient == null) backendClient = FindAnyObjectByType<BackendWebSocketClient>();
        }

        private void OnValidate() => ResolveReferences();
    }
}
