using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.World;
using TMPro;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    /// <summary>Rare, local visual accents. Never publishes dialogue, memory or world events.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(300)]
    public sealed class DialogueEmotionIconPresenter : MonoBehaviour
    {
        [SerializeField, Min(1)] private float cooldownSeconds = 18f;
        [SerializeField, Range(1, 1.5f)] private float durationSeconds = 1.35f;
        [SerializeField, Range(28, 60)] private float fontSize = 44f;
        [SerializeField, Min(0)] private float sideGap = 8f;
        private WorldEntity owner;
        private ExpressiveController expressive;
        private SpeechPresentationLifecycle speech;
        private DialogueBubbleManager bubbles;
        private DialogueBubbleAnchor anchor;
        private DialogueController dialogue;
        private RectTransform view;
        private TMP_Text label;
        private CanvasGroup opacity;
        private double began, nextAllowed;
        private long speechId;
        private string conversation;
        private bool preview;
        public string ActiveCue { get; private set; }
        public bool IsVisible => view != null && view.gameObject.activeInHierarchy && opacity.alpha > 0;
        public float Alpha => opacity == null ? 0 : opacity.alpha;
        public Rect VisibleRect { get; private set; }
        public int PresentationCount { get; private set; }

        public void Configure(WorldEntity entity, ExpressiveController expression, SpeechPresentationLifecycle lifecycle,
            DialogueBubbleManager manager, DialogueController controller)
        {
            Unsubscribe(); ClearPresentation();
            owner = entity; expressive = expression; speech = lifecycle; bubbles = manager;
            dialogue = controller; anchor = entity == null ? null : entity.GetComponent<DialogueBubbleAnchor>();
            if (isActiveAndEnabled) Subscribe();
        }

        public bool TryPresent(string cue, CognitivePresentationContext context, double now)
        {
            ClearPresentation(); // A replacement may suppress the old accent, never queue another one.
            if (!isActiveAndEnabled || owner == null || context.OwnerAgentEntityId != owner.EntityId ||
                !double.IsFinite(now) || now < nextAllowed || !HasCue(cue) || speech == null || !speech.IsSpeaking ||
                expressive == null || !expressive.isActiveAndEnabled || !CurrentSession(context.ConversationId)) return false;
            if (!Begin(cue, now)) return false;
            nextAllowed = now + Mathf.Max(1, cooldownSeconds);
            speechId = speech.Current.Id; conversation = context.ConversationId;
            PresentationCount++;
            return true;
        }

        private bool CurrentSession(string id) => dialogue != null && dialogue.isActiveAndEnabled &&
            dialogue.IsPresentationVisible && dialogue.HasActiveSession && dialogue.SelectedTarget != null &&
            dialogue.SelectedTarget.Entity == owner && dialogue.CurrentConversationId == id;

        private static bool HasCue(string cue) => cue == "happy" || cue == "confused" || cue == "surprised" || cue == "thinking";

        private bool Begin(string cue, double now)
        {
            if (!HasCue(cue) || bubbles == null || !bubbles.isActiveAndEnabled || anchor == null ||
                bubbles.PresentationCanvas == null || bubbles.PresentationFont == null) return false;
            if (view == null)
            {
                var go = new GameObject("EmotionIcon_" + owner.EntityId, typeof(RectTransform), typeof(CanvasGroup), typeof(TextMeshProUGUI));
                go.transform.SetParent(bubbles.transform, false);
                view = (RectTransform)go.transform; opacity = go.GetComponent<CanvasGroup>(); label = go.GetComponent<TMP_Text>();
                view.anchorMin = view.anchorMax = view.pivot = new Vector2(.5f, .5f);
                view.sizeDelta = new Vector2(60, 64);
                label.font = bubbles.PresentationFont; label.fontSize = fontSize; label.fontStyle = FontStyles.Bold;
                label.alignment = TextAlignmentOptions.Center; label.richText = false; label.raycastTarget = false;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.outlineColor = new Color(1, .97f, .86f, .95f); label.outlineWidth = .14f;
                opacity.blocksRaycasts = opacity.interactable = false;
            }
            string glyph = cue switch { "happy" => "♪", "confused" => "?", "surprised" => "!", _ => "…" };
            if (!label.font.HasCharacter(glyph[0], true, true))
                glyph = cue == "happy" ? "*" : cue == "thinking" ? "..." : glyph;
            label.text = glyph;
            label.color = cue switch {
                "happy" => new Color(.78f, .53f, .19f), "confused" => new Color(.34f, .56f, .66f),
                "surprised" => new Color(.83f, .43f, .29f), _ => new Color(.53f, .47f, .65f) };
            ActiveCue = cue; began = now; opacity.alpha = 0;
            view.gameObject.SetActive(false);
            return true;
        }

        public void Tick(double now)
        {
            if (ActiveCue == null) return;
            double age = now - began;
            if (!double.IsFinite(now) || age < 0 || age >= durationSeconds || !isActiveAndEnabled ||
                owner == null || !owner.isActiveAndEnabled || bubbles == null || !bubbles.isActiveAndEnabled ||
                bubbles.PresentationCanvas == null || !bubbles.PresentationCanvas.isActiveAndEnabled ||
                (!preview && (speech == null || !speech.IsSpeaking || speech.Current.Id != speechId || !CurrentSession(conversation))))
            { ClearPresentation(); return; }
            float t = (float)age;
            opacity.alpha = Mathf.Min(Mathf.SmoothStep(0, 1, t / .16f), Mathf.SmoothStep(0, 1, (durationSeconds - t) / .3f));
            Layout(t / durationSeconds);
        }

        private void Layout(float progress)
        {
            var camera = bubbles.SceneCamera;
            if (camera == null || anchor == null ||
                !bubbles.TryGetHeadRect(anchor, out Rect head)) { view.gameObject.SetActive(false); return; }
            Vector2 size = view.sizeDelta;
            // Bubbles reserve a generous hair/head volume. Use half its width for this small
            // accent, always on the same side; bubble layout must never move the icon.
            Vector2 point = new(head.center.x + head.width * .25f + sideGap + size.x / 2,
                head.center.y + head.height * .2f + Mathf.SmoothStep(0, 12, progress));
            Rect candidate = new(point - size / 2, size);
            view.anchoredPosition = point; VisibleRect = candidate;
            // This brief accent keeps its position and lifetime even when other UI overlaps.
            view.gameObject.SetActive(true);
        }

        public void ClearPresentation()
        {
            ActiveCue = null; preview = false; speechId = 0; conversation = null; VisibleRect = default;
            if (view != null) { opacity.alpha = 0; view.gameObject.SetActive(false); }
        }
        private void VisibilityChanged(bool visible) { if (!visible) ClearPresentation(); }
        private void SessionChanged(DialogueParticipant target) { if (target == null || target.Entity != owner) ClearPresentation(); }
        private void SpeechEnded(SpeechPresentation ended) { if (!preview && ended.Id == speechId) ClearPresentation(); }
        private void Subscribe()
        {
            if (dialogue != null) { dialogue.PresentationVisibilityChanged += VisibilityChanged; dialogue.ActiveSessionChanged += SessionChanged; }
            if (speech != null) speech.SpeechEnded += SpeechEnded;
        }
        private void Unsubscribe()
        {
            if (dialogue != null) { dialogue.PresentationVisibilityChanged -= VisibilityChanged; dialogue.ActiveSessionChanged -= SessionChanged; }
            if (speech != null) speech.SpeechEnded -= SpeechEnded;
        }
        private void OnEnable() => Subscribe();
        private void OnDisable() { Unsubscribe(); ClearPresentation(); }
        private void LateUpdate() => Tick(Time.unscaledTimeAsDouble);
        private void OnDestroy() { if (view != null) Destroy(view.gameObject); }

#if UNITY_EDITOR
        // Explicit Editor-only visual audition; publishes no fake reply and does not consume game cooldown.
        public bool PreviewForEditor(string cue)
        {
            ClearPresentation();
            if (!Application.isPlaying || !Begin(cue, Time.unscaledTimeAsDouble)) return false;
            preview = true; return true;
        }
#endif
    }
}
