using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [Serializable]
    public sealed class DialogueHistoryItem
    {
        public string event_id;
        public long experience_seq;
        public string role;
        public string actor;
        public string text;
        public double timestamp;
    }

    [Serializable]
    public sealed class DialogueHistoryPage
    {
        public string observer_id;
        public string conversation_id;
        public DialogueHistoryItem[] items;
        public long next_after_seq;
        public bool has_more;
    }

    /// <summary>Read-only ledger projection plus witnessed emote presentations retained in this play session.</summary>
    public sealed class DialogueHistoryPanel : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private BackendWebSocketClient backendClient;
        [SerializeField] private Button historyButton;
        [SerializeField] private GameObject panel;
        [SerializeField] private ScrollRect scrollView;
        [SerializeField] private TMP_Text historyText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private ChatHistoryMessageView messageTemplate;
        private readonly StringBuilder display = new();
        private sealed class Scope
        {
            public string Observer, Conversation, Name;
            public long Cursor;
        }
        private readonly List<Scope> scopes = new();
        private readonly List<ChatHistoryMessage> messages = new();
        private readonly HashSet<string> loadedMessages = new(StringComparer.Ordinal);
        private ChatHudLayout layout;
        private Coroutine polling;
        private long afterSeq;
        private long localSequence;
        private readonly List<PerceivedStateStore> perceptionSources = new();
        private readonly List<CognitiveRunDriver> presentationSources = new();
        private readonly Dictionary<string, ChatHistoryMessage> emoteStarts = new(StringComparer.Ordinal);
        private readonly HashSet<string> answeredEmotes = new(StringComparer.Ordinal);

        public bool IsOpen => panel != null && panel.activeSelf;
        public long LastSequence => afterSeq;
        public string DisplayText => historyText == null ? string.Empty : historyText.text;

        private void Awake()
        {
            layout = new ChatHudLayout(transform, panel, scrollView, historyText, historyButton);
            layout.Bind(Toggle, messageTemplate);
        }
#if UNITY_EDITOR
        public ChatHistoryMessageView BakeLayoutForEditor()
        {
            var editorLayout = new ChatHudLayout(transform, panel, scrollView, historyText, historyButton);
            editorLayout.Build(Toggle);
            Canvas.ForceUpdateCanvases();
            editorLayout.Render(new[] { new ChatHistoryMessage { Speaker = "NPC", Text = "聊天内容" } });
            var template = scrollView.content.GetChild(0).gameObject.AddComponent<ChatHistoryMessageView>();
            template.Configure();
            return template;
        }
#endif

        private void OnEnable()
        {
            historyButton.onClick.AddListener(Toggle);
            dialogueController.ActiveSessionChanged += OnSessionChanged;
            dialogueController.PresentationVisibilityChanged += OnVisibilityChanged;
            SettingsUiLanguage.Changed += RefreshLanguage;
            SubscribeEmoteHistory();
            RememberSession();
            Close();
        }

        private void OnDisable()
        {
            historyButton.onClick.RemoveListener(Toggle);
            dialogueController.ActiveSessionChanged -= OnSessionChanged;
            dialogueController.PresentationVisibilityChanged -= OnVisibilityChanged;
            SettingsUiLanguage.Changed -= RefreshLanguage;
            UnsubscribeEmoteHistory();
            Close();
        }

        private void SubscribeEmoteHistory()
        {
            UnsubscribeEmoteHistory();
            foreach (var store in FindObjectsByType<PerceivedStateStore>(FindObjectsSortMode.None))
            {
                store.ObservationApplied += OnEmoteObserved;
                perceptionSources.Add(store);
            }
            foreach (var driver in FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None))
            {
                driver.UtterancePresented += OnEmoteReplyPresented;
                presentationSources.Add(driver);
            }
        }
        private void UnsubscribeEmoteHistory()
        {
            foreach (var store in perceptionSources) if (store != null) store.ObservationApplied -= OnEmoteObserved;
            foreach (var driver in presentationSources) if (driver != null) driver.UtterancePresented -= OnEmoteReplyPresented;
            perceptionSources.Clear(); presentationSources.Clear();
        }
        private static string EmoteKey(string observerId, string eventId) => observerId + "\n" + eventId;
        private static string Field(Observation observation, string key) =>
            observation.KnownFields.TryGetValue(key, out string value) ? value : string.Empty;
        private static string EmoteText(string id) => id switch
        {
            "wave" => SettingsUiLanguage.T("表情[挥手]", "Emote [Wave]"),
            "cheer" => SettingsUiLanguage.T("表情[庆祝]", "Emote [Cheer]"),
            "flex" => SettingsUiLanguage.T("表情[得意]", "Emote [Flex]"),
            _ => string.Empty
        };
        private void OnEmoteObserved(ObservationApplyResult result)
        {
            Observation observed = result.Observation;
            if (!result.IsApplied || observed == null || observed.SourceType != ObservationSourceType.GameplayEvent ||
                observed.Channel != ObservationChannel.Visual || Field(observed, "event_type") != AgentEventTypes.PlayerEmote ||
                Field(observed, AgentEventPayloadKeys.SocialObserver) != observed.ObserverEntityId ||
                string.IsNullOrEmpty(Field(observed, "actor_entity_id")) || string.IsNullOrEmpty(observed.TraceId)) return;
            string id = Field(observed, AgentEventPayloadKeys.EmoteId);
            if (string.IsNullOrEmpty(EmoteText(id))) return;
            // A directed wave belongs to its target, even if that NPC chooses not to reply.
            // An untargeted expression belongs only to its already selected, genuinely observing candidate.
            bool associated = id == "wave" ? Field(observed, "social_role") == "addressed" &&
                Field(observed, "target_entity_id") == observed.ObserverEntityId :
                Field(observed, AgentEventPayloadKeys.SocialResponseEligible) == "true";
            if (!associated) return;
            string key = EmoteKey(observed.ObserverEntityId, observed.SourceEventId);
            if (emoteStarts.ContainsKey(key)) return;
            long order = ++localSequence;
            var message = new ChatHistoryMessage { Sequence = order, DisplayOrder = order, Timestamp = observed.OccurredAt,
                ObserverEntityId = observed.ObserverEntityId, IsPlayer = true, Speaker = SettingsUiLanguage.T("我", "Me"),
                Text = EmoteText(id), EmoteObservation = observed, EventId = observed.SourceEventId, TraceId = observed.TraceId };
            emoteStarts.Add(key, message); messages.Add(message);
            if (IsOpen) Render();
        }
        private void OnEmoteReplyPresented(PresentedCognitiveUtterance presented)
        {
            if (presented == null || string.IsNullOrWhiteSpace(presented.RequestId) ||
                string.IsNullOrWhiteSpace(presented.RunId) || string.IsNullOrWhiteSpace(presented.Utterance)) return;
            string key = EmoteKey(presented.OwnerAgentEntityId, presented.SourceEventId);
            if (!emoteStarts.TryGetValue(key, out ChatHistoryMessage start) || answeredEmotes.Contains(key) ||
                start.EmoteObservation.TraceId != presented.TraceId ||
                Field(start.EmoteObservation, "actor_entity_id") != presented.PlayerEntityId) return;
            // This signal comes only after Driver ownership checks and successful visible presentation.
            answeredEmotes.Add(key);
            long order = ++localSequence;
            messages.Add(new ChatHistoryMessage { Sequence = order, DisplayOrder = order,
                Timestamp = Math.Max(start.Timestamp + .000001d, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d),
                ObserverEntityId = presented.OwnerAgentEntityId, Speaker = ResolveNpcName(presented.OwnerAgentEntityId),
                Text = presented.Utterance, EmoteObservation = start.EmoteObservation,
                RequestId = presented.RequestId, RunId = presented.RunId,
                EventId = presented.SourceEventId, TraceId = presented.TraceId });
            if (IsOpen) Render();
        }
        private string ResolveNpcName(string entityId)
        {
            var driver = presentationSources.Find(value => value != null &&
                value.GetComponent<EmbodiedNPC.World.WorldEntity>()?.EntityId == entityId);
            var composer = driver == null ? null : driver.GetComponent<CognitiveContextComposer>();
            if (composer != null && composer.CharacterDefinition != null)
                return CharacterProfileResolver.Resolve(composer.CharacterDefinition, CharacterOverrideStore.Shared).display_name;
            return scopes.Find(scope => scope.Observer == entityId)?.Name ?? entityId;
        }

        private void Update()
        {
            // Also remember rotations while the history drawer is closed.
            RememberSession();
            if (IsOpen && !dialogueController.HasActiveSession) Close();
        }

        private void OnSessionChanged(DialogueParticipant participant)
        {
            RememberSession();
            // Selection changes the input recipient, not the player's combined history.
        }
        private void RememberSession()
        {
            if (!dialogueController.HasActiveSession || dialogueController.SelectedTarget == null) return;
            string observer = dialogueController.SelectedTarget.Entity.EntityId;
            string conversation = dialogueController.CurrentConversationId;
            if (scopes.Any(s => s.Observer == observer && s.Conversation == conversation)) return;
            var entity = dialogueController.SelectedTarget.GetComponent<PerceivableEntity>();
            var composer = dialogueController.SelectedTarget.GetComponent<CognitiveContextComposer>();
            scopes.Add(new Scope { Observer = observer, Conversation = conversation,
                Name = composer != null && composer.CharacterDefinition != null ?
                    CharacterProfileResolver.Resolve(composer.CharacterDefinition, CharacterOverrideStore.Shared).display_name :
                    entity == null ? dialogueController.SelectedTarget.name : entity.DisplayName });
        }
        private void RefreshLanguage()
        {
            layout.RefreshLanguage();
            if (IsOpen) Render();
        }
        private void OnVisibilityChanged(bool visible)
        {
            if (!visible) Close();
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        private void Open()
        {
            Close();
            if (!dialogueController.HasActiveSession || !dialogueController.IsPresentationVisible) return;
            RememberSession();
            panel.SetActive(true);
            layout.SetExpanded(true);
            Render();
            statusText.text = SettingsUiLanguage.T("加载中…", "Loading…");
            polling = StartCoroutine(Poll());
        }

        public void Close()
        {
            if (polling != null) StopCoroutine(polling);
            polling = null;
            panel.SetActive(false);
            layout?.SetExpanded(false);
            afterSeq = 0;
            display.Clear();
            historyText.text = string.Empty;
        }

        private IEnumerator Poll()
        {
            while (IsOpen)
            {
                bool more = false;
                bool failed = false;
                // Snapshot permits a newly selected scope to join the next pass safely.
                foreach (Scope scope in scopes.ToArray())
                {
                    string url = backendClient.DialogueHistoryUrl + "?observer_id=" +
                        Uri.EscapeDataString(scope.Observer) + "&conversation_id=" +
                        Uri.EscapeDataString(scope.Conversation) + "&after_seq=" + scope.Cursor;
                    using (UnityWebRequest request = UnityWebRequest.Get(url))
                    {
                        if (!EmbodiedNPC.Settings.LocalBackendAccess.TryAuthorize(request, out _))
                        { failed = true; continue; }
                        request.timeout = 5;
                        yield return request.SendWebRequest();
                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            DialogueHistoryPage page = null;
                            try { page = JsonUtility.FromJson<DialogueHistoryPage>(request.downloadHandler.text); }
                            catch (ArgumentException) { /* Keep the last valid display; retry the read later. */ }
                            if (AppendPage(page)) more |= page.has_more;
                            else failed = true;
                        }
                        else failed = true;
                    }
                }
                statusText.text = failed ? SettingsUiLanguage.T("记录暂不可用，正在重试…", "History unavailable; retrying…") :
                    messages.Count > 0 ? string.Empty : SettingsUiLanguage.T("暂无聊天记录", "No messages yet");
                if (!more) yield return new WaitForSecondsRealtime(0.5f);
                else yield return null;
            }
        }

        public bool AppendPage(DialogueHistoryPage page)
        {
            if (!IsOpen || !dialogueController.HasActiveSession || page == null || page.items == null) return false;
            Scope scope = scopes.Find(s => s.Observer == page.observer_id && s.Conversation == page.conversation_id);
            if (scope == null) return false;
            long next = scope.Cursor;
            foreach (DialogueHistoryItem item in page.items)
            {
                if (item == null || item.experience_seq <= next ||
                    (item.role != "player" && item.role != "npc")) return false;
                next = item.experience_seq;
            }
            if (page.next_after_seq != next || (page.has_more && next == scope.Cursor)) return false;
            if (page.items.Length == 0) return true;

            foreach (DialogueHistoryItem item in page.items)
            {
                string key = scope.Observer + "\n" + (string.IsNullOrEmpty(item.event_id)
                    ? scope.Conversation + "\n" + item.experience_seq : item.event_id);
                if (!loadedMessages.Add(key)) continue;
                messages.Add(new ChatHistoryMessage { Sequence = item.experience_seq, Timestamp = item.timestamp,
                    DisplayOrder = ++localSequence, ObserverEntityId = scope.Observer,
                    ConversationId = scope.Conversation, EventId = item.event_id,
                    Speaker = item.role == "player" ? SettingsUiLanguage.T("我", "Me") : scope.Name,
                    IsPlayer = item.role == "player", Text = item.text ?? string.Empty });
            }
            scope.Cursor = next;
            afterSeq = next;
            Render();
            return true;
        }

        private void Render()
        {
            Canvas.ForceUpdateCanvases();
            bool atBottom = display.Length == 0 || scrollView.content.rect.height <= scrollView.viewport.rect.height ||
                scrollView.verticalNormalizedPosition <= .02f;
            float previousOffset = scrollView.content.anchoredPosition.y;
            // Ledger sequences are observer-local. Equal timestamps keep stable first-seen order
            // rather than comparing unrelated NPC sequence counters.
            var ordered = messages.OrderBy(m => m.Timestamp).ThenBy(m => m.DisplayOrder).ToList();
            display.Clear();
            foreach (var message in ordered)
            {
                if (message.EmoteObservation != null && message.IsPlayer)
                    message.Text = EmoteText(Field(message.EmoteObservation, AgentEventPayloadKeys.EmoteId));
                if (display.Length > 0) display.Append("\n\n");
                display.Append(message.IsPlayer ? "Player: " : "NPC: ").Append(message.IsPlayer ? SettingsUiLanguage.T("我", "Me") :
                    SettingsUiLanguage.NpcDisplayName(message.ObserverEntityId, message.Speaker)).Append("\n")
                    .Append(message.IsPlayer ? message.Text : DialoguePresentationText.PlainText(message.Text));
            }
            historyText.richText = false;
            historyText.text = display.ToString();
            layout.Render(ordered);
            Canvas.ForceUpdateCanvases();
            if (atBottom) scrollView.verticalNormalizedPosition = 0;
            else scrollView.content.anchoredPosition = new Vector2(0, previousOffset);
        }
    }
}
