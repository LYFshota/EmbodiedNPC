using System.Collections;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Animation;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    public sealed class DialogueBubbleCognitivePresenter : MonoBehaviour, ICognitiveDecisionPresenter
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private DialogueBubbleManager manager;
        [SerializeField] private SpeechPresentationLifecycle speech;
        [SerializeField] private ExpressiveController expressive;
        private DialogueEmotionIconPresenter emotionIcon;

        public string SpeakerEntityId => owner == null ? string.Empty : owner.EntityId;

        private void Awake()
        {
            if (owner == null) owner = GetComponent<WorldEntity>();
            if (speech == null) speech = GetComponent<SpeechPresentationLifecycle>();
            if (expressive == null) expressive = GetComponent<ExpressiveController>();
            if (manager == null) manager = DialogueBubbleManager.Active ?? FindAnyObjectByType<DialogueBubbleManager>();
            ConfigureIcon();
        }

        public void Configure(WorldEntity entity, DialogueBubbleManager bubbleManager)
        {
            owner = entity;
            manager = bubbleManager;
            speech = GetComponent<SpeechPresentationLifecycle>();
            expressive = GetComponent<ExpressiveController>();
            ConfigureIcon();
        }

        private void ConfigureIcon()
        {
            if (expressive == null) return;
            emotionIcon = GetComponent<DialogueEmotionIconPresenter>() ?? gameObject.AddComponent<DialogueEmotionIconPresenter>();
            emotionIcon.Configure(owner, expressive, speech, manager, FindAnyObjectByType<DialogueController>());
        }

        public bool TryPresentCognitiveUtterance(string utterance)
        {
            if (!TryShow(utterance)) return false;
            emotionIcon?.ClearPresentation();
            speech?.BeginPresentedText(DialoguePresentationText.PlainText(utterance), Time.unscaledTimeAsDouble);
            return true;
        }

        public bool TryPresentCognitiveDecision(CognitiveDecision decision, CognitivePresentationContext context)
        {
            if (owner == null || context.OwnerAgentEntityId != owner.EntityId || decision == null ||
                !TryShow(decision.utterance, context)) return false;
            expressive?.ReceivePresentedDecision(decision, context);
            speech?.BeginPresentedText(DialoguePresentationText.PlainText(decision.utterance), Time.unscaledTimeAsDouble);
            emotionIcon?.TryPresent(decision.expression_icon, context, Time.unscaledTimeAsDouble);
            return true;
        }

        private bool TryShow(string utterance, CognitivePresentationContext? context = null)
        {
            if (!isActiveAndEnabled || string.IsNullOrWhiteSpace(utterance) || owner == null ||
                manager == null || !manager.isActiveAndEnabled || !manager.CanPresentText) return false;
            string[] segments = DialoguePresentationText.Segments(utterance);
            if (segments.Length == 0) return false;
            bool shown = context.HasValue
                ? manager.PushReply(owner.EntityId, segments[0], context.Value.RequestId, context.Value.TraceId, context.Value.ConversationId).IsValid
                : manager.PushMessage(owner.EntityId, segments[0], DialogueSpeakerKind.Npc).IsValid;
            if (shown && segments.Length > 1) StartCoroutine(ShowRemaining(segments, manager, owner.EntityId));
            return shown;
        }

        private IEnumerator ShowRemaining(string[] segments, DialogueBubbleManager target, string speaker)
        {
            for (int i = 1; i < segments.Length; i++)
            {
                yield return new WaitForSecondsRealtime(2.0f);
                if (target == null || !target.isActiveAndEnabled || !target.CanPresentText) yield break;
                target.PushMessage(speaker, segments[i], DialogueSpeakerKind.Npc);
            }
        }

        private void OnDisable() { StopAllCoroutines(); emotionIcon?.ClearPresentation(); speech?.Cancel(); }
    }
}
