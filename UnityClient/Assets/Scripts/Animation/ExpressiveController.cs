using System;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using EmbodiedNPC.Routines;
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
        private WorldEntity attentionTarget, conversationSpeaker;
        private string acceptedConversation;
        private AgentOrchestrator orchestrator;
        private NpcDefaultRoutineController routine;
        public string ConversationSpeakerId => HasConversationAttention ? conversationSpeaker.EntityId : null;
        private bool HasConversationAttention => conversationSpeaker != null &&
            conversationSpeaker.isActiveAndEnabled && conversationSpeaker.gameObject.activeInHierarchy &&
            dialogue != null && dialogue.isActiveAndEnabled && dialogue.HasActiveSession &&
            dialogue.CurrentConversationId == acceptedConversation && dialogue.SelectedTarget != null &&
            dialogue.SelectedTarget.Entity == owner && dialogue.ActiveSpeakerEntityId == conversationSpeaker.EntityId &&
            dialogue.CheckEligibility(dialogue.SelectedTarget).IsEligible &&
            IsConversationHeld;
        private bool IsConversationHeld => dialogue != null && dialogue.IsPresentationVisible ||
            speech != null && speech.IsSpeaking || orchestrator != null && orchestrator.HasActiveRun ||
            routine != null && routine.IsEngagementHeld;
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
                dialogue.DialogueSubmissionAccepted += HandleAcceptedUtterance;
            }
            orchestrator = owner == null ? null : owner.GetComponent<AgentOrchestrator>();
            routine = owner == null ? null : owner.GetComponent<NpcDefaultRoutineController>();
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
                dialogue.DialogueSubmissionAccepted -= HandleAcceptedUtterance;
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
        private void HandleVisibilityChanged(bool visible) => RefreshSession();
        private void HandleAcceptedUtterance(UserUtterance utterance)
        {
            if (utterance == null || owner == null || utterance.targetAgentId != owner.EntityId ||
                dialogue == null || utterance.conversationId != dialogue.CurrentConversationId ||
                entityRegistry == null || !entityRegistry.TryGetEntity(utterance.speakerEntityId, out var speaker) ||
                !speaker.IsAgent(AgentRole.Player)) return;
            conversationSpeaker = speaker; acceptedConversation = utterance.conversationId;
            RefreshSession();
        }
        private void RefreshSession()
        {
            bool selected = dialogue != null && dialogue.HasActiveSession && dialogue.SelectedTarget != null &&
                dialogue.SelectedTarget.GetComponent<WorldEntity>() == owner;
            string current = selected ? dialogue.CurrentConversationId : null;
            if (!string.Equals(current, sessionId, StringComparison.Ordinal))
            { ClearHints(); conversationSpeaker = null; acceptedConversation = null; sessionId = current; }
            if (conversationSpeaker != null && (!conversationSpeaker.isActiveAndEnabled ||
                !conversationSpeaker.gameObject.activeInHierarchy || !selected || !IsConversationHeld ||
                !dialogue.CheckEligibility(dialogue.SelectedTarget).IsEligible || entityRegistry == null ||
                !entityRegistry.TryGetEntity(conversationSpeaker.EntityId, out var registered) || registered != conversationSpeaker ||
                motionChannels != null && (motionChannels.IsFormalTaskMotionActive || motionChannels.IsEmergencyMotionActive ||
                    motionChannels.IsInteractionMotionActive || motionChannels.IsCarryPresentationActive)))
            { conversationSpeaker = null; acceptedConversation = null; }
            listening = selected && dialogue.IsPresentationVisible;
            if (speech == null || !speech.IsSpeaking) State = listening ? DialoguePresentationState.Listening : DialoguePresentationState.Idle;
            RefreshDialogueFacing(selected);
        }
        private void RefreshDialogueFacing(bool selected)
        {
            if (facing == null) return;
            WorldEntity speaker = null;
            bool confirmed = HasConversationAttention;
            bool allowed = isActiveAndEnabled && selected &&
                (confirmed || dialogue.IsPresentationVisible) &&
                entityRegistry != null && entityRegistry.TryGetEntity(dialogue.ActiveSpeakerEntityId, out speaker) &&
                speaker != null && speaker.isActiveAndEnabled && speaker.gameObject.activeInHierarchy;
            facing.SetDialogueTarget(allowed ? speaker.transform : null, allowed, confirmed);
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
            listening = false; sessionId = acceptedConversation = null; conversationSpeaker = null; speech?.Cancel(); ClearHints(); State = DialoguePresentationState.Idle;
            facing?.SetDialogueTarget(null, false);
        }
    }
}
