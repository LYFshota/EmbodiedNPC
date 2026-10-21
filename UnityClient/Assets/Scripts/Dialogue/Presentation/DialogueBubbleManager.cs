using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class DialogueBubbleManager : MonoBehaviour
    {
        private sealed class Entry
        {
            public string WaitingRequest, WaitingTrace, WaitingConversation;
            public bool IsWaiting => WaitingRequest != null;
            public DialogueBubbleMessage Message;
            public DialogueBubbleView View;
            public Vector2 Position;
        }

        [SerializeField] private Canvas rootCanvas;
        [SerializeField] private Camera sceneCamera;
        [SerializeField] private DialogueBubbleView bubbleTemplate;
        [SerializeField] private DialogueBubbleVisualStyle playerStyle = new();
        [SerializeField] private DialogueBubbleVisualStyle npcStyle = new()
        {
            backgroundColor = new Color(0.22f, 0.14f, 0.08f, 0.94f),
        };
        [SerializeField, Range(1, 3)] private int maxMessagesPerSpeaker = 3;
        [FormerlySerializedAs("inactivityLifetimeSeconds")]
        [SerializeField, Min(0.1f)] private float bubbleLifetimeSeconds = 15f;
        [SerializeField, Min(0f)] private float verticalSpacing = 10f;

        private readonly Dictionary<string, DialogueBubbleAnchor> anchors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Entry>> entriesBySpeaker = new(StringComparer.Ordinal);
        private long nextSequence;

        public static DialogueBubbleManager Active { get; private set; }
        public float BubbleLifetimeSeconds => bubbleLifetimeSeconds;
        // Offscreen layout may hide a rendered bubble later; it does not end the speech interval.
        public bool CanPresentText => bubbleTemplate != null && bubbleTemplate.CanPresentText &&
            rootCanvas != null && rootCanvas.isActiveAndEnabled;
        public int VisibleBubbleCount => entriesBySpeaker.Values.SelectMany(x => x).Count(x => x.View != null && x.View.gameObject.activeSelf);

        private RectTransform LayerRect => (RectTransform)transform;

        private void Awake()
        {
            if (rootCanvas == null) rootCanvas = GetComponentInParent<Canvas>();
            if (sceneCamera == null) sceneCamera = Camera.main;
            if (bubbleTemplate != null) bubbleTemplate.gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            Active = this;
            foreach (DialogueBubbleAnchor anchor in FindObjectsByType<DialogueBubbleAnchor>(FindObjectsSortMode.None))
                RegisterAnchor(anchor);
        }

        private void OnDisable()
        {
            foreach (string speaker in entriesBySpeaker.Keys.ToArray()) RemoveWaiting(speaker);
            if (Active == this) Active = null;
        }

        private void LateUpdate() => Refresh(Time.unscaledTimeAsDouble);

        public void Configure(Canvas canvas, Camera camera, DialogueBubbleView template)
        {
            rootCanvas = canvas;
            sceneCamera = camera;
            bubbleTemplate = template;
            if (bubbleTemplate != null) bubbleTemplate.gameObject.SetActive(false);
        }

        public void RegisterAnchor(DialogueBubbleAnchor anchor)
        {
            if (anchor == null || string.IsNullOrWhiteSpace(anchor.SpeakerEntityId)) return;
            anchors[anchor.SpeakerEntityId] = anchor;
        }

        public void UnregisterAnchor(DialogueBubbleAnchor anchor)
        {
            if (anchor != null && anchors.TryGetValue(anchor.SpeakerEntityId, out DialogueBubbleAnchor current) && current == anchor)
            {
                anchors.Remove(anchor.SpeakerEntityId);
                RemoveWaiting(anchor.SpeakerEntityId);
            }
        }

        // UI-only correlation. No speech/history/event pipeline is involved.
        public bool ShowWaiting(string speaker, string request, string trace, string conversation)
        {
            if (!isActiveAndEnabled || !CanPresentText || string.IsNullOrEmpty(request) || string.IsNullOrEmpty(trace) ||
                string.IsNullOrEmpty(conversation) || !anchors.TryGetValue(speaker ?? "", out var anchor) ||
                anchor == null || !anchor.isActiveAndEnabled) return false;
            var existing = Waiting(speaker, request, trace, conversation);
            if (existing != null) return true;
            RemoveWaiting(speaker);
            var entry = AddMessage(speaker, "...", DialogueSpeakerKind.Npc, Time.unscaledTimeAsDouble);
            entry.WaitingRequest = request; entry.WaitingTrace = trace; entry.WaitingConversation = conversation;
            return true;
        }

        public void ClearWaiting(string speaker, string request, string trace, string conversation)
        {
            var entry = Waiting(speaker, request, trace, conversation);
            if (entry == null) return;
            entriesBySpeaker[speaker].Remove(entry);
            RemoveView(entry);
            if (entriesBySpeaker[speaker].Count == 0) entriesBySpeaker.Remove(speaker);
        }

        private Entry Waiting(string speaker, string request, string trace, string conversation) =>
            entriesBySpeaker.TryGetValue(speaker ?? "", out var entries) ? entries.Find(e => e.IsWaiting &&
                e.WaitingRequest == request && e.WaitingTrace == trace && e.WaitingConversation == conversation) : null;

        private void RemoveWaiting(string speaker)
        {
            if (!entriesBySpeaker.TryGetValue(speaker, out var entries)) return;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].IsWaiting) { RemoveView(entries[i]); entries.RemoveAt(i); }
            if (entries.Count == 0) entriesBySpeaker.Remove(speaker);
        }

        private static void RemoveView(Entry entry)
        {
            if (entry.View == null) return;
            entry.View.gameObject.SetActive(false); // Destroy is deferred in Play Mode; hide immediately.
            DestroyObject(entry.View.gameObject);
        }

        public DialogueBubbleMessage PushReply(string speaker, string text, string request, string trace, string conversation)
        {
            if (string.IsNullOrWhiteSpace(text)) return default;
            var entry = Waiting(speaker, request, trace, conversation);
            if (entry == null) return PushMessage(speaker, text, DialogueSpeakerKind.Npc);
            entry.Message = new DialogueBubbleMessage(entry.Message.MessageId, speaker, text,
                Time.unscaledTimeAsDouble, ++nextSequence, DialogueSpeakerKind.Npc);
            entry.WaitingRequest = entry.WaitingTrace = entry.WaitingConversation = null;
            entry.View?.SetMessage(entry.Message, npcStyle);
            var entries = entriesBySpeaker[speaker]; entries.Remove(entry); entries.Add(entry);
            TrimMessages(entries);
            return entry.Message;
        }

        public DialogueBubbleMessage PushMessage(string speakerEntityId, string text, DialogueSpeakerKind kind) =>
            PushMessage(speakerEntityId, text, kind, Time.unscaledTimeAsDouble);

        public DialogueBubbleMessage PushMessage(string speakerEntityId, string text, DialogueSpeakerKind kind, double now)
        {
            if (string.IsNullOrWhiteSpace(speakerEntityId) || string.IsNullOrWhiteSpace(text)) return default;
            Entry entry = AddMessage(speakerEntityId, text, kind, now);
            TrimMessages(entriesBySpeaker[speakerEntityId]);
            return entry.Message;
        }

        private Entry AddMessage(string speakerEntityId, string text, DialogueSpeakerKind kind, double now)
        {
            long sequence = ++nextSequence;
            DialogueBubbleMessage message = new(Guid.NewGuid().ToString("N"), speakerEntityId,
                kind == DialogueSpeakerKind.Npc ? text : text.Trim(), now, sequence, kind);
            if (!entriesBySpeaker.TryGetValue(speakerEntityId, out List<Entry> entries))
            {
                entries = new List<Entry>();
                entriesBySpeaker.Add(speakerEntityId, entries);
            }

            DialogueBubbleView view = bubbleTemplate == null ? null : Instantiate(bubbleTemplate, transform, false);
            if (view != null)
            {
                view.name = $"Bubble_{speakerEntityId}_{sequence}";
                view.gameObject.SetActive(true);
                view.SetMessage(message, kind == DialogueSpeakerKind.Player ? playerStyle : npcStyle);
            }
            var entry = new Entry { Message = message, View = view };
            entries.Add(entry);
            return entry;
        }

        private void TrimMessages(List<Entry> entries)
        {
            while (entries.Count(e => !e.IsWaiting) > maxMessagesPerSpeaker)
            {
                Entry oldest = entries.Find(e => !e.IsWaiting);
                entries.Remove(oldest); RemoveView(oldest);
            }
        }

        public IReadOnlyList<DialogueBubbleMessage> GetMessages(string speakerEntityId) =>
            entriesBySpeaker.TryGetValue(speakerEntityId ?? string.Empty, out List<Entry> entries)
                ? entries.Select(x => x.Message).ToArray()
                : Array.Empty<DialogueBubbleMessage>();

        public IReadOnlyList<Rect> GetVisibleScreenRects() => entriesBySpeaker.Values.SelectMany(x => x)
            .Where(x => x.View != null && x.View.gameObject.activeSelf).OrderBy(x => x.Message.Sequence)
            .Select(x => x.View.ScreenRect).ToArray();

        public void Refresh(double now)
        {
            RemoveExpiredMessages(now);
            LayoutVisibleBubbles();
        }

        private void RemoveExpiredMessages(double now)
        {
            foreach ((string speakerId, List<Entry> entries) in entriesBySpeaker.ToArray())
            {
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    Entry entry = entries[i];
                    if (entry.IsWaiting || now - entry.Message.CreatedAt < bubbleLifetimeSeconds) continue;
                    entries.RemoveAt(i);
                    if (entry.View != null) DestroyObject(entry.View.gameObject);
                }
                if (entries.Count == 0) entriesBySpeaker.Remove(speakerId);
            }
        }

        public void ClearMessages()
        {
            foreach (Entry entry in entriesBySpeaker.Values.SelectMany(x => x))
                if (entry.View != null) DestroyObject(entry.View.gameObject);
            entriesBySpeaker.Clear();
        }

        private void LayoutVisibleBubbles()
        {
            if (sceneCamera == null || rootCanvas == null) return;
            var visible = new List<Entry>();

            foreach ((string speakerId, List<Entry> speakerEntries) in entriesBySpeaker)
            {
                bool hasAnchor = anchors.TryGetValue(speakerId, out DialogueBubbleAnchor anchor) && anchor != null;
                Vector3 screen = hasAnchor ? sceneCamera.WorldToScreenPoint(anchor.AnchorPoint.position) : Vector3.zero;
                bool onScreen = hasAnchor && screen.z > 0f && screen.x >= 0f && screen.x <= Screen.width &&
                                screen.y >= 0f && screen.y <= Screen.height;
                Vector2 local = Vector2.zero;
                if (onScreen) RectTransformUtility.ScreenPointToLocalPointInRectangle(LayerRect, screen,
                    rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : sceneCamera, out local);
                float bottom = local.y + verticalSpacing;
                // Only the owner's head defines its baseline. Other characters may
                // project higher without moving this speaker's entire conversation.
                if (onScreen && TryGetHeadRect(anchor, out Rect headRect))
                    bottom = Mathf.Max(bottom, headRect.yMax + verticalSpacing);

                for (int i = speakerEntries.Count - 1; i >= 0; i--)
                {
                    Entry entry = speakerEntries[i];
                    if (entry.View == null) continue;
                    entry.View.gameObject.SetActive(onScreen);
                    if (!onScreen) continue;
                    Vector2 size = entry.View.RectTransform.rect.size;
                    entry.Position = new Vector2(local.x, bottom + size.y * 0.5f);
                    visible.Add(entry);
                }
            }

            visible.Sort((a, b) => b.Message.Sequence.CompareTo(a.Message.Sequence));
            if (visible.Count == 0) return;

            // Newest first: a speaker's own history always grows upward, but time
            // order between different speakers matters only on actual intersection.
            for (int olderIndex = 0; olderIndex < visible.Count; olderIndex++)
            {
                Entry older = visible[olderIndex];
                Rect olderRect = ToRect(older);
                // An upward move can hit a previously checked bubble. Restart until
                // clear; movement is monotonic and each obstacle is crossed at most once.
                for (int pass = 0; pass <= olderIndex; pass++)
                {
                    bool moved = false;
                    for (int newerIndex = 0; newerIndex < olderIndex; newerIndex++)
                    {
                        Entry newer = visible[newerIndex];
                        Rect newerRect = ToRect(newer);
                        bool sameSpeaker = older.Message.SpeakerEntityId == newer.Message.SpeakerEntityId;
                        // Include the visual gap in the collision test, on both axes.
                        Rect padded = Rect.MinMaxRect(newerRect.xMin, newerRect.yMin - verticalSpacing,
                            newerRect.xMax, newerRect.yMax + verticalSpacing);
                        if ((!sameSpeaker && !olderRect.Overlaps(padded)) ||
                            olderRect.yMin >= newerRect.yMax + verticalSpacing) continue;
                        older.Position.y += newerRect.yMax + verticalSpacing - olderRect.yMin;
                        olderRect = ToRect(older);
                        moved = true;
                    }
                    if (!moved) break;
                }
                // Do not clamp down or sideways at screen edges: that would detach the
                // bubble from its speaker or cover the head. The screen crops overflow.
                older.View.RectTransform.anchoredPosition = older.Position;
                older.View.ScreenRect = ToRect(older);
            }
        }

        private bool TryGetHeadRect(DialogueBubbleAnchor anchor, out Rect rect)
        {
            rect = default;
            Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
            // Conservative head/hair volume, projected at the current camera angle.
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = anchor.HeadPosition + new Vector3(
                    (i & 1) == 0 ? -0.3f : 0.3f, (i & 2) == 0 ? -0.35f : 0.4f,
                    (i & 4) == 0 ? -0.3f : 0.3f);
                Vector3 screen = sceneCamera.WorldToScreenPoint(point);
                if (screen.z <= sceneCamera.nearClipPlane) return false;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(LayerRect, screen,
                    rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : sceneCamera, out Vector2 local);
                min = Vector2.Min(min, local); max = Vector2.Max(max, local);
            }
            rect = Rect.MinMaxRect(min.x - 16f, min.y - 16f, max.x + 16f, max.y + 16f);
            return rect.Overlaps(LayerRect.rect);
        }

        private static Rect ToRect(Entry entry)
        {
            Vector2 size = entry.View.RectTransform.rect.size;
            return new Rect(entry.Position - size * 0.5f, size);
        }

        private static void DestroyObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
