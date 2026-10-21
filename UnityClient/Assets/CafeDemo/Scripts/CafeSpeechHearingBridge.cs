using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Only accepted input and successful presentation become spatial speech stimuli.</summary>
    public sealed class CafeSpeechHearingBridge : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private AudioStimulusRouter router;
        [SerializeField] private CognitiveRunDriver[] drivers = Array.Empty<CognitiveRunDriver>();
        private bool subscribed;

        public void Configure(DialogueController controller, EntityRegistry entities,
            AudioStimulusRouter audioRouter, CognitiveRunDriver[] sources)
        {
            Unsubscribe(); dialogue = controller; registry = entities; router = audioRouter;
            drivers = sources ?? Array.Empty<CognitiveRunDriver>();
            if (isActiveAndEnabled) Subscribe();
        }
        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void Subscribe()
        {
            if (subscribed) return;
            if (dialogue != null) dialogue.DialogueSubmissionAccepted += Accepted;
            foreach (var driver in drivers) if (driver != null) driver.UtterancePresented += Presented;
            subscribed = true;
        }
        private void Unsubscribe()
        {
            if (!subscribed) return;
            if (dialogue != null) dialogue.DialogueSubmissionAccepted -= Accepted;
            foreach (var driver in drivers) if (driver != null) driver.UtterancePresented -= Presented;
            subscribed = false;
        }
        private void Accepted(UserUtterance input)
        {
            if (input == null || registry == null || !registry.TryGetEntity(input.targetAgentId, out var target)) return;
            var turn = target.GetComponent<CognitiveDialogueSubmissionHandler>()?.LastAcceptedTurn;
            if (turn == null || turn.IsResponse || turn.Text != input.text || turn.SpeakerEntityId != input.speakerEntityId ||
                turn.TargetAgentId != input.targetAgentId || turn.ConversationId != input.conversationId) return;
            var request = target.GetComponent<CognitiveContextComposer>()?.LastRequest;
            string run = request?.request_id == turn.RequestId && request.context?.current_event?.event_id == turn.SourceEventId
                ? request.run?.run_id : null;
            Publish("speech_input_" + turn.SourceEventId, turn.SpeakerEntityId, turn.TargetAgentId,
                turn.Text, turn.SourceEventId, turn.TraceId, turn.RequestId, run, turn.OccurredAt, turn.WorldRevision);
        }
        private void Presented(PresentedCognitiveUtterance shown)
        {
            if (shown == null || !shown.IsDialogueEvidence || shown.SourceEvent == null ||
                (shown.SourceEvent.event_type != AgentEventTypes.PlayerUtterance &&
                 shown.SourceEvent.event_type != AgentEventTypes.DialogueContinue)) return;
            Publish("speech_reply_" + shown.RunId + "_" + shown.SourceEventId,
                shown.OwnerAgentEntityId, shown.PlayerEntityId, shown.Utterance, shown.SourceEventId,
                shown.TraceId, shown.RequestId, shown.RunId, shown.PresentedAt, shown.WorldRevision);
        }
        private void Publish(string occurrence, string speakerId, string targetId, string text,
            string source, string trace, string request, string run, double at, int revision)
        {
            if (router == null || registry == null || !registry.TryGetEntity(speakerId, out var speaker) ||
                string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(trace)) return;
            var payload = new Dictionary<string, string> { ["text"] = text,
                ["parent_source_event_id"] = source, ["request_id"] = request, ["run_id"] = run ?? string.Empty };
            router.RouteSpeech(new AgentEvent(occurrence, AgentEventTypes.SpeechPresented,
                speakerId, targetId, string.Empty, payload, at, revision, trace),
                speaker.transform.position + Vector3.up * 1.5f);
        }
    }
}
