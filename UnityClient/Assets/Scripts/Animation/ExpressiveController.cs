using System;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    public enum DialoguePresentationState { Idle, Listening, Talking }

    /// <summary>Per-NPC outward presentation only; no gameplay, cognition or perception authority.</summary>
    [DisallowMultipleComponent]
    public sealed class ExpressiveController : MonoBehaviour
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private MotionChannelCoordinator motionChannels;
        [SerializeField] private SpeechPresentationLifecycle speech;
        [SerializeField] private GestureController gesturePlayback;
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private FacingController facing;
        private bool listening;
        private string sessionId;
        private WorldEntity attentionTarget;
        public DialoguePresentationState State { get; private set; }
        public string ExpressionHint { get; private set; } = "neutral";
        public string GestureHint { get; private set; } = "none";
        public string AttentionHint { get; private set; }
        public CognitivePresentationContext PresentationContext { get; private set; }
        public MotionChannelCoordinator MotionChannels => motionChannels;
        public SpeechPresentationLifecycle Speech => speech;

        private void Awake()
        {
            if (owner == null) owner = GetComponent<WorldEntity>();
            if (motionChannels == null) motionChannels = GetComponent<MotionChannelCoordinator>();
            if (speech == null) speech = GetComponent<SpeechPresentationLifecycle>();
        }
        private void OnEnable()
        {
            if (speech != null) { speech.SpeechStarted += HandleStarted; speech.SpeechEnded += HandleEnded; }
            if (dialogue != null)
            {
                dialogue.ActiveSessionChanged += HandleSessionChanged;
                dialogue.PresentationVisibilityChanged += HandleVisibilityChanged;
            }
            RefreshSession();
        }
        private void OnDisable()
        {
            Cleanup();
            if (speech != null) { speech.SpeechStarted -= HandleStarted; speech.SpeechEnded -= HandleEnded; }
            if (dialogue != null)
            {
                dialogue.ActiveSessionChanged -= HandleSessionChanged;
                dialogue.PresentationVisibilityChanged -= HandleVisibilityChanged;
            }
        }
        public void Configure(WorldEntity entity, MotionChannelCoordinator channels,
            SpeechPresentationLifecycle lifecycle, DialogueController controller = null,
            EntityRegistry registry = null, GestureController playback = null)
        {
            OnDisable();
            owner = entity; motionChannels = channels; speech = lifecycle;
            dialogue = controller; entityRegistry = registry; gesturePlayback = playback;
            if (isActiveAndEnabled) OnEnable();
        }
        public void ReceivePresentedDecision(CognitiveDecision decision, CognitivePresentationContext context)
        {
            if (!isActiveAndEnabled || owner == null || decision == null || context.OwnerAgentEntityId != owner.EntityId) return;
            // End the old interval before installing hints, so End cannot erase the replacement.
            speech?.Cancel(); ClearHints();
            PresentationContext = context;
            ExpressionHint = decision.emotion ?? "neutral"; GestureHint = decision.gesture ?? "none";
            if (context.AllowsAttentionTarget(decision.look_at) && entityRegistry != null &&
                entityRegistry.TryGetEntity(decision.look_at, out WorldEntity target) && target.isActiveAndEnabled && target.gameObject.activeInHierarchy)
            { AttentionHint = decision.look_at; attentionTarget = target; }
            if (gesturePlayback != null && gesturePlayback.CanPlay(GestureHint) && motionChannels != null &&
                motionChannels.TryBeginGesture(GestureHint, decision.decision_kind == CognitiveDecisionKinds.ActionProposal,
                    Time.unscaledTimeAsDouble, gesturePlayback.PlaybackTimeout(GestureHint), out long id) && !gesturePlayback.TryPlayApprovedGesture(GestureHint, id))
                motionChannels.CompleteGesture(id);
        }
        private void HandleStarted(SpeechPresentation value) => State = DialoguePresentationState.Talking;
        private void HandleEnded(SpeechPresentation value)
        {
            ClearHints(); State = listening ? DialoguePresentationState.Listening : DialoguePresentationState.Idle;
        }
        private void HandleSessionChanged(DialogueParticipant value) => RefreshSession();
        private void HandleVisibilityChanged(bool visible)
        {
            if (!visible && sessionId != null) { speech?.Cancel(); ClearHints(); }
            RefreshSession();
        }
        private void RefreshSession()
        {
            bool selected = dialogue != null && dialogue.HasActiveSession && dialogue.SelectedTarget != null &&
                dialogue.SelectedTarget.GetComponent<WorldEntity>() == owner;
            string current = selected ? dialogue.CurrentConversationId : null;
            if (!string.Equals(current, sessionId, StringComparison.Ordinal))
            { speech?.Cancel(); ClearHints(); sessionId = current; }
            listening = selected && dialogue.IsPresentationVisible;
            if (speech == null || !speech.IsSpeaking) State = listening ? DialoguePresentationState.Listening : DialoguePresentationState.Idle;
            RefreshDialogueFacing(selected);
        }
        private void RefreshDialogueFacing(bool selected)
        {
            if (facing == null) return;
            WorldEntity speaker = null;
            bool allowed = isActiveAndEnabled && selected && dialogue.IsPresentationVisible &&
                entityRegistry != null && entityRegistry.TryGetEntity(dialogue.ActiveSpeakerEntityId, out speaker) &&
                speaker != null && speaker.isActiveAndEnabled && speaker.gameObject.activeInHierarchy;
            facing.SetDialogueTarget(allowed ? speaker.transform : null, allowed);
        }
        private void Update()
        {
            RefreshSession(); // Also observes conversation rotation on emergency/transport loss.
            if (AttentionHint != null && (attentionTarget == null || !attentionTarget.isActiveAndEnabled ||
                !attentionTarget.gameObject.activeInHierarchy || entityRegistry == null ||
                !entityRegistry.TryGetEntity(AttentionHint, out WorldEntity current) || current != attentionTarget))
            { AttentionHint = null; attentionTarget = null; }
        }
        private void ClearHints()
        {
            ExpressionHint = "neutral"; GestureHint = "none"; AttentionHint = null; attentionTarget = null;
            PresentationContext = default; motionChannels?.CancelGesture();
        }
        public void Cleanup()
        {
            listening = false; sessionId = null; speech?.Cancel(); ClearHints(); State = DialoguePresentationState.Idle;
            facing?.SetDialogueTarget(null, false);
        }
    }
}
