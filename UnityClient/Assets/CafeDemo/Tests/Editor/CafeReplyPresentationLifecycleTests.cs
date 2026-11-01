using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Perception;
using UnityEngine.AI;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEditor;
using System.IO;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Explicitly released responses exercise real client ingress without sockets, storage or model calls.
    public sealed class ReplyLifecycleFakeBackend : BackendWebSocketClient
    {
        public readonly List<CognitiveRequest> Requests = new();
        public readonly List<CognitiveRequest> Cancels = new();
        public Func<CognitiveRequest, Task<bool>> ForwardRequest, ForwardCancel;
        public override bool IsConnected => isActiveAndEnabled;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest r)
        { Requests.Add(r); return ForwardRequest?.Invoke(r) ?? Task.FromResult(true); }
        public override Task<bool> SendCognitiveCancelAsync(CognitiveRequest r) { Cancels.Add(r); return ForwardCancel?.Invoke(r) ?? Task.FromResult(true); }
        public override Task<bool> SendExperienceAppendRequestAsync(ExperienceAppendRequest r) => Task.FromResult(false);
        public bool Reply(CognitiveResponse r) => ProcessIncomingJson(JsonConvert.SerializeObject(r));
    }

    public sealed class CafeReplyPresentationLifecycleTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        protected override bool ClearSceneAfterTest => true;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private ReplyLifecycleFakeBackend fake;
        private DialogueController dialogue;
        private CognitiveRunDriver driver;
        private AgentOrchestrator runs;
        private TextUtteranceInput input;
        private readonly List<PresentedCognitiveUtterance> shown = new();
        private readonly List<ExperienceDraft> drafts = new();
        private readonly List<CognitiveRequestFeedback> feedback = new();
        [OneTimeSetUp] public void ImportMissingCharacterDependencies()
        {
            // A fresh checkout can import VRM before its package shaders. Repair only failed imports,
            // as the existing action lifecycle fixture does; never save the scene or change source assets.
            foreach (string path in new[] { "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/VRMCompatibilityTest/Xingmang.vrm", "Assets/CafeDemo/V2/Characters/薇薇.vrm",
                "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeDemo/V2/Characters/Ao.vrm",
                "Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb" })
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null && File.Exists(path))
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
        private static void Set(object target, string field, object value)
        {
            var type = target.GetType();
            while (type != null && type.GetField(field, Private) == null) type = type.BaseType;
            Assert.That(type, Is.Not.Null, field); type.GetField(field, Private).SetValue(target, value);
        }

        [UnitySetUp] public IEnumerator PrepareReplyFixture()
        {
            foreach (var source in Object.FindObjectsByType<TaskConsiderationSource>()) source.enabled = false;
            foreach (var source in Object.FindObjectsByType<DialogueContinuationScheduler>()) source.enabled = false;
            foreach (var source in Object.FindObjectsByType<TaskRuntimeBridge>()) source.enabled = false;
            foreach (var entity in Object.FindObjectsByType<WorldEntity>())
                if (entity.IsAgent(AgentRole.Npc))
                {
                    entity.GetComponent<CognitiveRunDriver>().enabled = false;
                    entity.GetComponent<AgentOrchestrator>().CancelCurrentRun();
                }
            ControlledPerception();
            var root = new GameObject("ReplyLifecycle_InMemoryOnly"); root.SetActive(false);
            fake = root.AddComponent<ReplyLifecycleFakeBackend>();
            Set(fake, "connectOnStart", false); Set(fake, "serverUrl", "ws://127.0.0.1:0/ws");
            root.SetActive(true);
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            driver = merchant.GetComponent<CognitiveRunDriver>(); runs = merchant.GetComponent<AgentOrchestrator>();
            driver.Configure(merchant, runs, merchant.GetComponent<CognitiveContextComposer>(), fake,
                merchant.GetComponent<DialogueBubbleCognitivePresenter>()); driver.enabled = true;
            Set(dialogue, "backendClient", fake);
            Set(merchant.GetComponent<CognitiveDialogueSubmissionHandler>(), "backendClient", fake);
            var adapter = new DialogueExperienceAdapter(drafts.Add, Object.FindAnyObjectByType<EntityRegistry>());
            Set(dialogue, "dialogueExperienceAdapter", adapter);
            driver.UtterancePresented += shown.Add; driver.UtterancePresented += adapter.Handle;
            driver.RequestFeedback += feedback.Add;
            input = Object.FindAnyObjectByType<TextUtteranceInput>();
            Place(GameObject.Find("CounterFrontPoint").transform.position);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return null;
            shown.Clear(); drafts.Clear(); feedback.Clear();
        }

        private CognitiveRequest Send(string text = "你好")
        {
            int count = fake.Requests.Count;
            Assert.That(input.SubmitText(text), Is.True);
            Assert.That(fake.Requests.Count, Is.EqualTo(count + 1));
            Assert.That(driver.PendingRequest, Is.SameAs(fake.Requests.Last()));
            return fake.Requests.Last();
        }
        private static CognitiveResponse Reply(CognitiveRequest r, string text = "我喜欢画街头的招牌。") => new() {
            message_type = "cognitive_response", schema_version = r.schema_version,
            task_result = r.schema_version == "1.1" ? "none" : null,
            request_id = r.request_id, trace_id = r.trace_id, run_id = r.run.run_id, agent_entity_id = r.run.agent_entity_id,
            decision = new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = text, gesture = "none" } };
        private void AssertNoNpcEvidence()
        { Assert.That(shown, Is.Empty); Assert.That(drafts.Any(d => d.EventType == "dialogue_response"), Is.False); }
        private void AssertWorldReply(CognitiveRequest request)
        {
            var evidence = shown.Single(v => v.RequestId == request.request_id);
            Assert.That(evidence.OwnerAgentEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(evidence.PlayerEntityId, Is.EqualTo(player.GetComponent<WorldEntity>().EntityId));
            var ledger = drafts.Single(d => d.EventType == "dialogue_response" && d.RequestId == request.request_id);
            Assert.That(ledger.ConversationId, Is.EqualTo(evidence.ConversationId));
            Assert.That(ledger.NaturalLanguagePayload, Is.EqualTo(evidence.Utterance));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Count(m => m.Text == evidence.Utterance), Is.EqualTo(1));
        }

        private CognitiveRequest StartDrinkGoal(List<GoalOutcome> outcomes, List<GoalPlanPresentation> terminal)
        {
            var runner = merchant.GetComponent<GoalPlanRunner>();
            var station = merchant.GetComponent<DrinkPreparationCapability>().Station;
            Assert.That(station, Is.Not.Null, "Use the configured station, including a disabled one from this test.");
            station.enabled = true;
            runner.GoalOutcomePublished += outcomes.Add;
            runner.PresentationReady += value => { if (value.Outcome.HasValue) terminal.Add(value); };
            var adapter = new GoalOutcomeExperienceAdapter(merchant.EntityId, drafts.Add,
                Object.FindAnyObjectByType<EntityRegistry>(), world);
            runner.GoalOutcomePublished += adapter.Handle;
            var request = Send("请现在给我做一杯拿铁。");
            var response = Reply(request, "好，我开始做拿铁。");
            response.decision.decision_kind = CognitiveDecisionKinds.ActionProposal;
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "prepare_drink",
                bindings = new Dictionary<string, string> { ["drink_type"] = "latte" } };
            response.validated_goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
            Assert.That(fake.Reply(response), Is.True);
            Assert.That(runner.HasActiveTask && driver.HasPendingActionOutcome, Is.True, driver.LastActionHandoffResult);
            return request;
        }

        private static void ReplayTerminal(CognitiveRunDriver target, GoalPlanPresentation terminal) =>
            typeof(CognitiveRunDriver).GetMethod("HandleGoalPresentation", Private).Invoke(target, new object[] { terminal });

        private IEnumerator AwaitGoal(List<GoalOutcome> outcomes)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 18;
            while (outcomes.Count == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Succeeded), outcomes[0].FailureCode);
        }

        private void AssertTerminalEvidence(CognitiveRequest request, GoalPlanPresentation terminal, bool displayed)
        {
            Assert.That(terminal.Outcome.HasValue, Is.True);
            var outcome = terminal.Outcome.Value;
            Assert.That(outcome.TraceId, Is.EqualTo(request.trace_id));
            Assert.That(outcome.ProposalId, Is.EqualTo("proposal_" + request.request_id));
            Assert.That(outcome.ActorEntityId, Is.EqualTo(merchant.EntityId));
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            Assert.That(bubbles.GetMessages(merchant.EntityId).Count(m => m.Text == terminal.Message), Is.EqualTo(displayed ? 1 : 0));
            Assert.That(bubbles.GetMessages(customer.EntityId).Any(m => m.Text == terminal.Message), Is.False);
            Assert.That(drafts.Count(d => d.SourceType == "goal_outcome"), Is.EqualTo(1));
            // The existing Goal evidence owns terminal results; do not manufacture a second model reply.
            Assert.That(shown.Count(s => s.RequestId == request.request_id), Is.EqualTo(1));
            Assert.That(drafts.Count(d => d.EventType == "dialogue_response" && d.RequestId == request.request_id), Is.EqualTo(1));
            ReplayTerminal(driver, terminal);
            Assert.That(bubbles.GetMessages(merchant.EntityId).Count(m => m.Text == terminal.Message), Is.EqualTo(displayed ? 1 : 0));
            Assert.That(drafts.Count(d => d.SourceType == "goal_outcome"), Is.EqualTo(1));
        }

        private static string SegmentationScreenshotPath(string name)
        {
            string folder = Path.Combine(Application.temporaryCachePath, "DialogueSegmentationValidation");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, name);
        }

        [UnityTest] public IEnumerator SegmentedReply_UnscaledBubbles_OneCleanEvidenceAndRequest()
        {
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            float oldScale = Time.timeScale;
            var gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            var sizeProperty = gameView.GetType().GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int previousSize = (int)sizeProperty.GetValue(gameView);
            try
            {
                sizeProperty.SetValue(gameView, 3); // Existing 1920x1080 entry, shared with NpcWaitingBubbleTests.
                gameView.Focus(); gameView.Repaint(); yield return null; yield return null;
                Assert.That(Screen.width, Is.EqualTo(1920)); Assert.That(Screen.height, Is.EqualTo(1080));
                Time.timeScale = 0f;
                foreach (string text in new[] { "欢迎光临。", "欢迎光临。[[PAUSE]]今天想聊什么？",
                    "欢迎光临。[[PAUSE]]今天阳光很好。[[PAUSE]]想聊什么？",
                    "欢迎光临。\n今天想聊什么？", "欢迎光临。\r\n今天阳光很好。\r\n想聊什么？",
                    string.Concat(Enumerable.Repeat("奶香柔和，又不至于甜到盖住咖啡香。", 8)) })
                {
                    bubbles.ClearMessages(); shown.Clear(); drafts.Clear();
                    var request = Send(); int sends = fake.Requests.Count;
                    Assert.That(fake.Reply(Reply(request, text)), Is.True);
                    Assert.That(merchant.GetComponent<SpeechPresentationLifecycle>().Current.Utterance,
                        Is.EqualTo(DialoguePresentationText.PlainText(text)));
                    string[] segments = DialoguePresentationText.Segments(text);
                    Assert.That(DialoguePresentationText.HistorySegments(DialoguePresentationText.PlainText(text)),
                        Is.EqualTo(segments), "Live and persisted history use the same explicit boundaries.");
                    Assert.That(bubbles.GetMessages(merchant.EntityId).Select(m => m.Text), Is.EqualTo(segments.Take(1)));
                    for (int i = 1; i < segments.Length; i++)
                    {
                        double previous = bubbles.GetMessages(merchant.EntityId).Last().CreatedAt;
                        while (Time.unscaledTimeAsDouble < previous + 1.9) yield return null;
                        Assert.That(bubbles.GetMessages(merchant.EntityId).Count, Is.EqualTo(i));
                        double deadline = previous + 3;
                        while (bubbles.GetMessages(merchant.EntityId).Count == i && Time.unscaledTimeAsDouble < deadline)
                            yield return null;
                        Assert.That(bubbles.GetMessages(merchant.EntityId).Count, Is.EqualTo(i + 1));
                        double interval = bubbles.GetMessages(merchant.EntityId).Last().CreatedAt - previous;
                        Assert.That(interval, Is.InRange(1.99, 2.4), "Unscaled interval");
                        Debug.Log("[SegmentedReply] interval=" + interval.ToString("F4"));
                    }
                    Assert.That(bubbles.GetMessages(merchant.EntityId).Select(m => m.Text), Is.EqualTo(segments));
                    Assert.That(shown, Has.Count.EqualTo(1));
                    Assert.That(shown.Single().Utterance, Is.EqualTo(DialoguePresentationText.PlainText(text)));
                    Assert.That(drafts.Count(d => d.EventType == "dialogue_response"), Is.EqualTo(1));
                    Assert.That(drafts.Single(d => d.EventType == "dialogue_response").NaturalLanguagePayload,
                        Is.EqualTo(shown.Single().Utterance));
                    Assert.That(fake.Requests, Has.Count.EqualTo(sends));
                    yield return null;
                    bubbles.Refresh(Time.unscaledTimeAsDouble);
                    var views = bubbles.GetComponentsInChildren<DialogueBubbleView>()
                        .Where(v => v.Message.SpeakerEntityId == merchant.EntityId).ToArray();
                    foreach (var view in views)
                        Assert.That(view.GetComponentInChildren<TMPro.TMP_Text>().text, Does.Not.Contain("[[PAUSE]]"));
                    if (text.Length > 100 && segments.Length == 1)
                    {
                        var label = views.Single().GetComponentInChildren<TMPro.TMP_Text>();
                        label.ForceMeshUpdate();
                        Assert.That(label.textInfo.lineCount, Is.GreaterThan(1), "Automatic wrapping stays in one bubble.");
                    }
                    for (int i = 0; i < views.Length; i++)
                        for (int j = i + 1; j < views.Length; j++)
                            Assert.That(views[i].ScreenRect.Overlaps(views[j].ScreenRect), Is.False);
                    if (segments.Length == 3)
                    {
                        // Same frozen CafeDemoV2 frame, only the old/new head clearance changes.
                        Set(bubbles, "headSpacing", 10f); bubbles.Refresh(Time.unscaledTimeAsDouble);
                        var before = views.Select(v => v.RectTransform.anchoredPosition).ToArray();
                        ScreenCapture.CaptureScreenshot(SegmentationScreenshotPath("bubbles-before.png"));
                        double until = Time.unscaledTimeAsDouble + .3; while (Time.unscaledTimeAsDouble < until) yield return null;
                        Set(bubbles, "headSpacing", 0f); bubbles.Refresh(Time.unscaledTimeAsDouble);
                        for (int i = 0; i < views.Length; i++)
                            Assert.That(before[i].y - views[i].RectTransform.anchoredPosition.y, Is.EqualTo(10f).Within(.1));
                        ScreenCapture.CaptureScreenshot(SegmentationScreenshotPath("bubbles-after.png"));
                        until = Time.unscaledTimeAsDouble + .3; while (Time.unscaledTimeAsDouble < until) yield return null;
                        foreach (var view in bubbles.GetComponentsInChildren<DialogueBubbleView>())
                        {
                            var anchor = Object.FindObjectsByType<DialogueBubbleAnchor>()
                                .Single(a => a.SpeakerEntityId == view.Message.SpeakerEntityId);
                            if (bubbles.TryGetHeadRect(anchor, out Rect head))
                                Assert.That(view.ScreenRect.yMin, Is.GreaterThanOrEqualTo(head.yMax - .1));
                        }
                        Debug.Log("[SegmentedReply] CafeDemoV2 head clearance reduced by 10 UI units; canvas scale=" +
                            bubbles.PresentationCanvas.scaleFactor + "; screen=" + Screen.width + "x" + Screen.height);
                        // Isolate the player's own clearance from cross-speaker bubble avoidance.
                        bubbles.ClearMessages();
                        yield return null; // Existing views are destroyed at the end of the frame.
                        string playerId = player.GetComponent<WorldEntity>().EntityId;
                        bubbles.PushMessage(playerId, "你好。", DialogueSpeakerKind.Player);
                        Set(bubbles, "headSpacing", 10f); bubbles.Refresh(Time.unscaledTimeAsDouble);
                        var playerView = bubbles.GetComponentsInChildren<DialogueBubbleView>().Single();
                        float playerBefore = playerView.RectTransform.anchoredPosition.y;
                        ScreenCapture.CaptureScreenshot(SegmentationScreenshotPath("player-before.png"));
                        until = Time.unscaledTimeAsDouble + .3; while (Time.unscaledTimeAsDouble < until) yield return null;
                        Set(bubbles, "headSpacing", 0f); bubbles.Refresh(Time.unscaledTimeAsDouble);
                        Assert.That(playerBefore - playerView.RectTransform.anchoredPosition.y, Is.EqualTo(10f).Within(.1));
                        var playerAnchor = player.GetComponent<DialogueBubbleAnchor>();
                        Assert.That(bubbles.TryGetHeadRect(playerAnchor, out Rect playerHead), Is.True);
                        Assert.That(playerView.ScreenRect.yMin, Is.GreaterThanOrEqualTo(playerHead.yMax - .1));
                        ScreenCapture.CaptureScreenshot(SegmentationScreenshotPath("player-after.png"));
                        until = Time.unscaledTimeAsDouble + .3; while (Time.unscaledTimeAsDouble < until) yield return null;
                        Debug.Log("[SegmentedReply] Isolated player clearance reduced by 10 pixels; head/hair bounds clear.");
                    }
                }
            }
            finally { Time.timeScale = oldScale; sizeProperty.SetValue(gameView, previousSize); }
        }

        [UnityTest] public IEnumerator SegmentedReply_EmptyOverflowAndDisable_DoNotLeakMarkersOrDelayedBubbles()
        {
            Assert.That(DialoguePresentationText.Segments("[[PAUSE]]  [[PAUSE]]"), Is.Empty);
            Assert.That(DialoguePresentationText.Segments(" a [[PAUSE]] [[PAUSE]] b [[PAUSE]] c [[PAUSE]] d "),
                Is.EqualTo(new[] { "a", "b", "c\nd" }));
            foreach (string text in new[] { "甲[[PAUSE]]乙[[PAUSE]]丙[[PAUSE]]丁", "甲\n乙\n丙\n丁",
                "甲\r\n乙\r\n丙\r\n丁", "甲\n[[PAUSE]]\r\n乙\r丙\n丁" })
            {
                Assert.That(DialoguePresentationText.Segments(text), Is.EqualTo(new[] { "甲", "乙", "丙\n丁" }));
                Assert.That(DialoguePresentationText.HistorySegments(DialoguePresentationText.PlainText(text)),
                    Is.EqualTo(new[] { "甲", "乙", "丙\n丁" }));
            }
            Assert.That(DialoguePresentationText.Segments("\r\n [[PAUSE]] \n"), Is.Empty);
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>(); bubbles.ClearMessages();
            var presenter = merchant.GetComponent<DialogueBubbleCognitivePresenter>();
            Assert.That(presenter.TryPresentCognitiveUtterance("[[PAUSE]] [[PAUSE]]"), Is.False);
            Assert.That(presenter.TryPresentCognitiveUtterance("第一段。[[PAUSE]]第二段。"), Is.True);
            presenter.enabled = false;
            double until = Time.unscaledTimeAsDouble + 2.2; while (Time.unscaledTimeAsDouble < until) yield return null;
            Assert.That(bubbles.GetMessages(merchant.EntityId).Select(m => m.Text), Is.EqualTo(new[] { "第一段。" }));
            bubbles.ClearMessages();
            Assert.That(bubbles.BubbleLifetimeSeconds, Is.EqualTo(20f), "CafeDemoV2 serialized lifetime.");
            double created = Time.unscaledTimeAsDouble;
            string playerId = player.GetComponent<WorldEntity>().EntityId;
            bubbles.PushMessage(merchant.EntityId, "NPC", DialogueSpeakerKind.Npc, created);
            bubbles.PushMessage(playerId, "玩家第一行\n玩家第二行", DialogueSpeakerKind.Player, created);
            bubbles.Refresh(created + 19.99);
            Assert.That(bubbles.GetMessages(merchant.EntityId).Count(), Is.EqualTo(1));
            Assert.That(bubbles.GetMessages(playerId).Single().Text, Is.EqualTo("玩家第一行\n玩家第二行"));
            bubbles.Refresh(created + 20);
            Assert.That(bubbles.GetMessages(merchant.EntityId), Is.Empty);
            Assert.That(bubbles.GetMessages(playerId), Is.Empty);
            Debug.Log("[SegmentedReply] NPC/player bubbles retained before 20 seconds and expired at 20 seconds.");
        }

        [UnityTest] public IEnumerator SegmentedReply_HistoryParagraphs_RenderOnceAndSurviveReopen()
        {
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>(FindObjectsInactive.Include);
            var scroll = (ScrollRect)typeof(DialogueHistoryPanel).GetField("scrollView", Private).GetValue(history);
            var gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            var sizeProperty = gameView.GetType().GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int previousSize = (int)sizeProperty.GetValue(gameView);
            Set(history, "backendClient", fake);
            history.enabled = true;
            try
            {
                sizeProperty.SetValue(gameView, 3); gameView.Focus(); gameView.Repaint();
                yield return null; yield return null;
                string playerText = "玩家第一行\n玩家第二行";
                string[] replies = { "单段回复。", "先说第一部分。[[PAUSE]]再说第二部分。\n补充第三部分。",
                    "先讲发生了什么。\r\n最后讲自己的想法。" };
                foreach (string text in replies)
                {
                    var request = Send(playerText);
                    Assert.That(fake.Reply(Reply(request, text)), Is.True);
                }
                Assert.That(shown.Count, Is.EqualTo(3));
                var responses = drafts.Where(d => d.EventType == "dialogue_response").ToArray();
                Assert.That(responses.Length, Is.EqualTo(3), "One saved draft per complete NPC reply.");
                Assert.That(responses.Select(d => d.NaturalLanguagePayload),
                    Is.EqualTo(replies.Select(DialoguePresentationText.PlainText)));
                var items = drafts.Select((d, i) => new DialogueHistoryItem {
                    event_id = "history-ui-event-" + i, experience_seq = i + 1, timestamp = i + 1,
                    actor = d.EventType == "dialogue_response" ? merchant.EntityId : "player_01",
                    role = d.EventType == "dialogue_response" ? "npc" : "player", text = d.NaturalLanguagePayload }).ToArray();
                Assert.That(items.Length, Is.EqualTo(6));
                var page = new DialogueHistoryPage { observer_id = merchant.EntityId,
                    conversation_id = dialogue.CurrentConversationId, items = items, next_after_seq = items.Length };
                history.Toggle(); history.StopAllCoroutines();
                Assert.That(history.AppendPage(page), Is.True);
                Assert.That(history.AppendPage(page), Is.False, "Replayed old page cannot duplicate history.");
                var cached = (List<ChatHistoryMessage>)typeof(DialogueHistoryPanel).GetField("messages", Private).GetValue(history);
                string identity = JsonConvert.SerializeObject(cached);
                var expected = new[] { playerText, replies[0], playerText, "先说第一部分。", "再说第二部分。",
                    "补充第三部分。", playerText, "先讲发生了什么。", "最后讲自己的想法。" };
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 1) { history.Close(); history.Toggle(); history.StopAllCoroutines(); }
                    yield return null; Canvas.ForceUpdateCanvases();
                    var views = scroll.content.GetComponentsInChildren<ChatHistoryMessageView>();
                    Assert.That(views.Length, Is.EqualTo(9), "All history segments render immediately.");
                    Assert.That(views.Select(v => v.transform.Find("Bubble/Message").GetComponent<TMPro.TMP_Text>().text), Is.EqualTo(expected));
                    Assert.That(views.Count(v => v.transform.Find("Speaker").gameObject.activeSelf), Is.EqualTo(6));
                    Assert.That(cached.Count, Is.EqualTo(6));
                    Assert.That(JsonConvert.SerializeObject(cached), Is.EqualTo(identity), "Rendering preserves IDs, sequence and source text.");
                    Assert.That(history.DisplayText, Does.Not.Contain("[[PAUSE]]"));
                    for (int i = 1; i < views.Length; i++)
                    {
                        var above = (RectTransform)views[i - 1].transform;
                        var below = (RectTransform)views[i].transform;
                        Assert.That(below.offsetMax.y, Is.LessThanOrEqualTo(above.offsetMin.y - 5));
                    }
                    Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
                    Assert.That(scroll.verticalNormalizedPosition, Is.EqualTo(0).Within(.01));
                    scroll.verticalNormalizedPosition = 1;
                    float position = scroll.content.anchoredPosition.y;
                    typeof(DialogueHistoryPanel).GetMethod("Render", Private).Invoke(history, null);
                    Assert.That(scroll.content.anchoredPosition.y, Is.EqualTo(position).Within(.1));
                    if (pass == 1)
                    {
                        ScreenCapture.CaptureScreenshot(SegmentationScreenshotPath("history-segmented.png"));
                        double until = Time.unscaledTimeAsDouble + .3; while (Time.unscaledTimeAsDouble < until) yield return null;
                    }
                }
                Assert.That(fake.Requests.Count, Is.EqualTo(3));
                Assert.That(drafts.Count(d => d.EventType == "dialogue_response"), Is.EqualTo(3));
                Assert.That(DialoguePresentationText.HistorySegments("甲\r\n\r\n乙[[PAUSE]]丙"), Is.EqualTo(new[] { "甲", "乙", "丙" }));
                Assert.That(DialoguePresentationText.HistorySegments("没有段落边界的旧回复。"), Is.EqualTo(new[] { "没有段落边界的旧回复。" }));
                Debug.Log("[SegmentedHistory] single/two/three parts, one name per turn, reopen, dedupe and scroll PASS; 3 responses remain 3 saved drafts.");
            }
            finally { history.Close(); history.enabled = false; sizeProperty.SetValue(gameView, previousSize); }
        }

        [UnityTest] public IEnumerator GoalResult_ClosedInput_PresentsOnceFromOriginalNpc()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal);
            dialogue.HideDialoguePresentation("close_button");
            yield return AwaitGoal(outcomes);
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("displayed"));
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            Assert.That(world.GetDrinkCount(player.GetComponent<WorldEntity>().EntityId, "latte"), Is.EqualTo(1));
            AssertTerminalEvidence(request, terminal.Single(), true);
        }

        [UnityTest] public IEnumerator GoalResult_SwitchedNpc_PreservesOtherNpcPendingReply()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal);
            Place(customer.transform.position + Vector3.forward);
            Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            string conversation = dialogue.CurrentConversationId;
            var other = customer.GetComponent<CognitiveRunDriver>();
            other.Configure(customer, customer.GetComponent<AgentOrchestrator>(), customer.GetComponent<CognitiveContextComposer>(),
                fake, customer.GetComponent<DialogueBubbleCognitivePresenter>());
            Set(customer.GetComponent<CognitiveDialogueSubmissionHandler>(), "backendClient", fake); other.enabled = true;
            Assert.That(input.SubmitText("你喜欢什么？"), Is.True);
            var next = fake.Requests.Last(); var nextRun = customer.GetComponent<AgentOrchestrator>().CurrentRun;
            yield return AwaitGoal(outcomes);
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("displayed"));
            Assert.That(dialogue.SelectedTarget.Entity, Is.SameAs(customer));
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(other.PendingRequest, Is.SameAs(next)); Assert.That(nextRun.State, Is.EqualTo(CognitiveRunState.Running));
            AssertTerminalEvidence(request, terminal.Single(), true);
            Assert.That(fake.Reply(Reply(next, "我喜欢读短篇。")), Is.True);
            Assert.That(nextRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(customer.EntityId)
                .Count(m => m.Text == "我喜欢读短篇。"), Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator GoalResult_SameNpcNewRun_IsNotSettledOrOverwrittenAndNeverReplayed()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal);
            var chat = Send("你有什么兴趣？"); var run = runs.CurrentRun;
            yield return AwaitGoal(outcomes);
            Assert.That(runs.CurrentRun, Is.SameAs(run)); Assert.That(driver.PendingRequest, Is.SameAs(chat));
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("lifecycle_unavailable"));
            AssertTerminalEvidence(request, terminal.Single(), false);
            // This captured IN_PROGRESS reply is stale after the Goal settled. A fresh chat still works.
            fake.Reply(Reply(chat)); Assert.That(run.State, Is.EqualTo(CognitiveRunState.Failed));
            var fresh = Send("现在可以接着聊了。"); fake.Reply(Reply(fresh));
            AssertWorldReply(fresh); AssertTerminalEvidence(request, terminal.Single(), false);
        }

        [UnityTest] public IEnumerator GoalResult_HiddenInputFailure_UsesRealOutcomeAndDoesNotClaimSuccess()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal); dialogue.HideDialoguePresentation();
            var station = merchant.GetComponent<DrinkPreparationCapability>().Station; station.enabled = false;
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (outcomes.Count == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(outcomes, Has.Count.EqualTo(1)); Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Failed));
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("cognition_pending"));
            var feedback = fake.Requests.Last();
            Assert.That(feedback.action_feedback, Is.Not.Null);
            Assert.That(feedback.action_feedback.original_request_id, Is.EqualTo(request.request_id));
            Assert.That(feedback.action_feedback.original_run_id, Is.EqualTo(request.run.run_id));
            Assert.That(feedback.action_feedback.outcome_id, Is.EqualTo(outcomes.Single().OutcomeId));
            Assert.That(world.GetDrinkCount(player.GetComponent<WorldEntity>().EntityId, "latte"), Is.Zero);
            Assert.That(terminal.Single().Message, Is.EqualTo("这次没能完成饮品制作。"));
            var reply = OutcomeReply(feedback);
            Assert.That(fake.Reply(reply), Is.True); Assert.That(fake.Reply(reply), Is.True);
            AssertWorldReply(feedback);
            Assert.That(drafts.Count(d => d.SourceType == "goal_outcome"), Is.EqualTo(1));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Any(m => m.Text == terminal.Single().Message), Is.False, "Do not present a second failure explanation.");
            ReplayTerminal(driver, terminal.Single()); Assert.That(fake.Requests.Count(r => r.action_feedback != null), Is.EqualTo(1));
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            var capture=Path.Combine(Path.GetTempPath(),"EmbodiedNPC-C3-Feedback"); Directory.CreateDirectory(capture);
            File.WriteAllText(Path.Combine(capture,"request.json"),feedback.Serialize());
            File.WriteAllText(Path.Combine(capture,"appends.json"),JsonConvert.SerializeObject(drafts.Select(ExperienceAppendRequest.FromDraft).ToArray()));

        }

        private static CognitiveResponse OutcomeReply(CognitiveRequest request, string selection = null)
        {
            var response=Reply(request,"这杯没做成。你想换个目标，还是先聊聊？");
            response.decision.dialogue_turn_policy="WAIT_FOR_PLAYER";
            response.decision.recovery_alternative_id=selection;
            return response;
        }

        private IEnumerator FailDrink(List<GoalOutcome> outcomes,List<GoalPlanPresentation> terminal)
        {
            StartDrinkGoal(outcomes,terminal);
            merchant.GetComponent<DrinkPreparationCapability>().Station.enabled=false;
            double until=Time.realtimeSinceStartupAsDouble+18;
            while(outcomes.Count==0 && Time.realtimeSinceStartupAsDouble<until)yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Failed));
            Assert.That(driver.PendingRequest?.action_feedback,Is.Not.Null);
            Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_ProviderFailureFallsBackOnceWithoutInventingSpokenEvidence()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var request=driver.PendingRequest;
            Assert.That(driver.TryConsumeFailure(new CognitiveTransportFailure(request.request_id,request.run.run_id,
                request.run.agent_entity_id,"COGNITIVE_PROVIDER_TIMEOUT",request.trace_id)),Is.True);
            Assert.That(driver.LastActionOutcomePresentationResult,Is.EqualTo("displayed"));
            Assert.That(shown.Any(x=>x.RequestId==request.request_id),Is.False);
            Assert.That(drafts.Any(x=>x.RequestId==request.request_id),Is.False);
            ReplayTerminal(driver,terminal.Single());fake.Reply(OutcomeReply(request));
            Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Count(m=>m.Text==terminal.Single().Message),Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_DisconnectTerminatesOnceAndLateResponseCannotReplay()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var request=driver.PendingRequest;
            fake.enabled=false;yield return null;
            Assert.That(driver.PendingRequest,Is.Null);Assert.That(runs.HasActiveRun,Is.False);
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask,Is.False);
            Assert.That(shown.Any(x=>x.RequestId==request.request_id),Is.False);
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Count(m=>m.Text==terminal.Single().Message),Is.EqualTo(1));
            fake.enabled=true;fake.Reply(OutcomeReply(request));ReplayTerminal(driver,terminal.Single());
            Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
            Assert.That(outcomes,Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_EnglishProviderFailureHasLocalizedTruthfulFallback()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var request=driver.PendingRequest;request.reply_language="en-US";
            driver.TryConsumeFailure(new CognitiveTransportFailure(request.request_id,request.run.run_id,
                request.run.agent_entity_id,"COGNITIVE_PROVIDER_TIMEOUT",request.trace_id));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Count(m=>m.Text=="I couldn't complete that attempt. Please clarify what you would like."),Is.EqualTo(1));
            Assert.That(shown.Any(x=>x.RequestId==request.request_id),Is.False);
            Assert.That(outcomes,Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_ClientTimeoutCancelsSingleOwnedAttemptAndShowsOneRealResult()
        {
            Set(driver,"responseTimeoutSeconds",.75f);
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var request=driver.PendingRequest;
            double until=Time.realtimeSinceStartupAsDouble+4;
            while(driver.PendingRequest!=null && Time.realtimeSinceStartupAsDouble<until)yield return null;
            Assert.That(driver.PendingRequest,Is.Null);
            Assert.That(fake.Cancels.Count(r=>r.request_id==request.request_id),Is.EqualTo(1));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                .Count(m=>m.Text==terminal.Single().Message),Is.EqualTo(1));
            fake.Reply(OutcomeReply(request));Assert.That(shown.Any(u=>u.RequestId==request.request_id),Is.False);
            Assert.That(outcomes,Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_NewChatPreemptsOutcomeRunAndLateReplyCannotOverwriteIt()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var old=driver.PendingRequest;
            var current=Send("先聊点别的吧。");var run=runs.CurrentRun;
            fake.Reply(OutcomeReply(old));Assert.That(driver.PendingRequest,Is.SameAs(current));
            Assert.That(runs.CurrentRun,Is.SameAs(run));Assert.That(run.State,Is.EqualTo(CognitiveRunState.Running));
            Assert.That(shown.Any(x=>x.RequestId==old.request_id),Is.False);
            fake.Reply(Reply(current));AssertWorldReply(current);Assert.That(outcomes,Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_SwitchInputRetainsOriginalNpcAndOriginalConversation()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var old=driver.PendingRequest;
            string original=dialogue.CurrentConversationId;
            Place(customer.transform.position+Vector3.forward);Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()),Is.True);
            string current=dialogue.CurrentConversationId;fake.Reply(OutcomeReply(old));AssertWorldReply(old);
            Assert.That(shown.Single(x=>x.RequestId==old.request_id).ConversationId,Is.EqualTo(original));
            Assert.That(dialogue.SelectedTarget.Entity,Is.SameAs(customer));Assert.That(dialogue.CurrentConversationId,Is.EqualTo(current));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(customer.EntityId)
                .Any(m=>m.Text==OutcomeReply(old).decision.utterance),Is.False);
        }

        [UnityTest] public IEnumerator Feedback_ForgedAlternativeNeverExecutesOrRecordsGeneratedText()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var old=driver.PendingRequest;
            fake.Reply(OutcomeReply(old,"invented_target"));
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask,Is.False);
            Assert.That(shown.Any(x=>x.RequestId==old.request_id),Is.False);
            Assert.That(outcomes,Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator Feedback_PresenterFailureDoesNotRecordGeneratedText()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            yield return FailDrink(outcomes,terminal);var old=driver.PendingRequest;
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().enabled=false;fake.Reply(OutcomeReply(old));
            Assert.That(shown.Any(x=>x.RequestId==old.request_id),Is.False);
            Assert.That(drafts.Any(x=>x.EventType=="dialogue_response" && x.RequestId==old.request_id),Is.False);
            Assert.That(outcomes,Has.Count.EqualTo(1));
        }
        [UnityTest] public IEnumerator Feedback_ExplicitGoalCancellationNeverRequestsRecovery()
        {
            var outcomes=new List<GoalOutcome>();var terminal=new List<GoalPlanPresentation>();
            StartDrinkGoal(outcomes,terminal);merchant.GetComponent<GoalPlanRunner>().CancelCurrentPlan();yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(fake.Requests.Any(r=>r.action_feedback!=null),Is.False);
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask,Is.False);
        }

        // Fault injection is limited to one selected executor step. Successful skills, safety recovery,
        // alternative admission, planning, the recovered Give and all world commits remain real.
        private sealed class FailingGive : ImmediatePlanActionRunner
        {
            public override PlannerActionType ActionType=>PlannerActionType.Give;
            protected override SkillResult Execute(PlanActionExecutionContext c)=>c.WorldState.RecordAttempt(
                c.SkillId,c.TraceId,"Give",c.Bindings.Actor,c.Bindings.Target,c.Bindings.Destination,
                SkillStatus.Failed,"C3_GIVE_UNAVAILABLE");
        }
        private sealed class FailingPickUp : ImmediatePlanActionRunner
        {
            public override PlannerActionType ActionType=>PlannerActionType.PickUp;
            protected override SkillResult Execute(PlanActionExecutionContext c)=>c.WorldState.RecordAttempt(
                c.SkillId,c.TraceId,"PickUp",c.Bindings.Actor,c.Bindings.Target,null,
                SkillStatus.Failed,"C3_PICKUP_UNAVAILABLE");
        }
        private void ObserveForExecution(WorldEntity target)
        {
            double now=Time.realtimeSinceStartupAsDouble;var fields=new Dictionary<string,string>{{"visibility_state","current_visible"}};
            target.GetComponent<PerceivableEntity>()?.WriteVisibleFields(fields);
            merchant.GetComponent<PerceivedStateStore>().Apply(new Observation("c3_"+Guid.NewGuid().ToString("N"),merchant.EntityId,
                ObservationSourceType.Direct,"c3_execution_fixture",ObservationChannel.Visual,target.EntityId,fields,Array.Empty<string>(),
                1,world.WorldRevision,now,now+120));
            merchant.GetComponent<PerceivedStateStore>().SpatialMemory.Observe(target.EntityId,target.transform.position,now,world.WorldRevision,fields);
        }

        [UnityTest] public IEnumerator Feedback_AuthorizedAlternativeExecutesOnce()=>RunRecoveryFault(false,false);
        [UnityTest] public IEnumerator Feedback_PartialFailurePreservesCommittedEffectsAndTerminates()=>RunRecoveryFault(true,false);
        [UnityTest] public IEnumerator Feedback_AlternativeInvalidatedBeforeReplyTerminatesWithoutExecution()=>RunRecoveryFault(false,true);
        private IEnumerator RunRecoveryFault(bool partial,bool invalidateAlternative)
        {
            var registry=Object.FindAnyObjectByType<EntityRegistry>();Assert.That(registry.TryGetEntity("cup_01",out var first),Is.True);
            var secondObject=Object.Instantiate(first.gameObject);secondObject.name="C3_AuthorizedSecondCup";
            var second=secondObject.GetComponent<WorldEntity>();second.Configure("cup_recovery_02",first.DefinitionId,EntityKind.Object,AgentRole.None);
            registry.Register(second);second.transform.position=first.transform.position+Vector3.right*.35f;
            Place(GameObject.Find("CounterFrontPoint").transform.position);
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(first.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            var vision=merchant.GetComponent<VisionSensor>();vision.enabled=true;vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds,Does.Contain(first.EntityId),"Keep the authored cup's real sight evidence.");
            ObserveForExecution(first);ObserveForExecution(second);ObserveForExecution(player.GetComponent<WorldEntity>());
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            var runner=merchant.GetComponent<GoalPlanRunner>();var executor=typeof(GoalPlanRunner).GetField("executor",Private).GetValue(runner);
            var set=(PlanActionRunnerSet)executor.GetType().GetField("runnerSet",Private).GetValue(executor);
            var runners=(Dictionary<PlannerActionType,IPlanActionRunner>)typeof(PlanActionRunnerSet).GetField("runners",Private).GetValue(set);
            var faultType=partial?PlannerActionType.Give:PlannerActionType.PickUp;
            var realRunner=runners[faultType];runners[faultType]=partial?(IPlanActionRunner)new FailingGive():new FailingPickUp();
            var outcomes=new List<GoalOutcome>();runner.GoalOutcomePublished+=outcomes.Add;
            var terminal=new List<GoalPlanPresentation>();runner.PresentationReady+=p=>{if(p.Outcome.HasValue)terminal.Add(p);};
            var effects=new List<SkillResult>();world.SkillResultPublished+=effects.Add;
            var request=Send("请把这组已授权杯子中的一只拿给我。");var response=Reply(request,"我试试。");
            response.decision.decision_kind="ACTION_PROPOSAL";response.decision.action_proposal=new CognitiveActionProposal {
                action_id="bring_object",bindings=new Dictionary<string,string>{{"target",first.EntityId},{"destination","player_01"}}};
            response.validated_goal=new GoalProposal {type="bring_object",target_entity_id=first.EntityId,destination_entity_id="player_01"};
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(first.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            vision.ScanNow();
            Assert.That(fake.Reply(response),Is.True);
            Set(runner,"activeAuthorization",new ExecutionAuthorization("bring_object",ActionSelectionMode.Any,
                new[]{first.EntityId,second.EntityId},ActionConfirmationPolicy.None,0,world.WorldRevision));
            Assert.That(runner.HasActiveTask,Is.True,driver.LastActionHandoffResult);
            double deadline=Time.realtimeSinceStartupAsDouble+25;
            while(outcomes.Count==0 && Time.realtimeSinceStartupAsDouble<deadline)yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Failed));
            var feedback=driver.PendingRequest;Assert.That(feedback?.action_feedback,Is.Not.Null);
            Assert.That(feedback.action_feedback.completed_actions,Is.GreaterThan(0));
            Assert.That(effects.Count(e=>e.SkillType=="PickUp" && e.TargetEntityId==first.EntityId && e.Status==SkillStatus.Succeeded),Is.EqualTo(partial?1:0));
            int committed=world.WorldRevision;
            if(partial)
            {
                Assert.That(committed,Is.GreaterThan(request.context.runtime_state.world_revision));
                Assert.That(first.GetComponent<EmbodiedNPC.Interaction.HeldItemState>().HolderEntityId,Is.EqualTo(merchant.EntityId));
                fake.Reply(OutcomeReply(feedback));AssertWorldReply(feedback);
                Assert.That(world.WorldRevision,Is.EqualTo(committed));
                Assert.That(first.GetComponent<EmbodiedNPC.Interaction.HeldItemState>().HolderEntityId,Is.EqualTo(merchant.EntityId));
                Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(runner.HasActiveTask,Is.False);
                Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
                yield break;
            }
            Assert.That(feedback.action_feedback.allowed_alternatives.Select(a=>a.goal.target_entity_id),Is.EqualTo(new[]{second.EntityId}));
            runners[faultType]=realRunner;
            var clearPosition=merchant.transform.position+Vector3.forward*.9f;
            clearPosition.y=second.transform.position.y;second.transform.position=clearPosition;
            Assert.That(NavMesh.SamplePosition(merchant.transform.position+Vector3.forward*.7f,out var approachHit,1,NavMesh.AllAreas),Is.True);
            var approach=new GameObject("C3_RecoveryApproach").transform;approach.SetParent(second.transform,true);approach.position=approachHit.position;
            var grab=new GameObject("C3_RecoveryGrab").transform;grab.SetParent(second.transform,false);
            var anchors=second.GetComponent<EmbodiedNPC.Interaction.InteractionAnchorProvider>();
            Set(anchors,"pickupApproachPoint",approach);Set(anchors,"grabPoint",grab);
            Assert.That(new NavMeshPlannerNavigationProbe(merchant.GetComponent<NavMeshAgent>()).HasCompletePath(merchant.transform.position,approach.position),Is.True);
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(second.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            vision.ScanNow();Assert.That(vision.CurrentVisibleEntityIds,Does.Contain(second.EntityId));
            ObserveForExecution(second);ObserveForExecution(player.GetComponent<WorldEntity>());
            if(invalidateAlternative)
            {
                Assert.That(registry.Unregister(second),Is.True);second.gameObject.SetActive(false);
                fake.Reply(OutcomeReply(feedback,"bring_"+second.EntityId));yield return null;
                Assert.That(runner.HasActiveTask,Is.False);Assert.That(outcomes,Has.Count.EqualTo(1));
                Assert.That(world.WorldRevision,Is.EqualTo(committed));
                Assert.That(shown.Any(u=>u.RequestId==feedback.request_id),Is.False);
                Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId)
                    .Count(m=>m.Text==terminal.Single().Message),Is.EqualTo(1));
                yield break;
            }
            try { Assert.That(fake.Reply(OutcomeReply(feedback,"bring_"+second.EntityId)),Is.True); }
            catch(Exception error) { Assert.Fail("Recovery ingress: "+error); }
            Assert.That(runner.HasActiveTask,Is.True,"Recovery admission: "+driver.LastActionOutcomePresentationResult);
            double until=Time.realtimeSinceStartupAsDouble+20;while(outcomes.Count<2 && Time.realtimeSinceStartupAsDouble<until)yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(2));Assert.That(outcomes[1].Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(outcomes[1].ProposalId,Is.EqualTo(outcomes[0].ProposalId));Assert.That(outcomes[1].ProposalRevision,Is.EqualTo(1));
            Assert.That(outcomes[1].TraceId,Is.EqualTo(outcomes[0].TraceId));
            Assert.That(second.GetComponent<EmbodiedNPC.Interaction.HeldItemState>().HolderEntityId,Is.EqualTo("player_01"));
            Assert.That(world.WorldRevision,Is.GreaterThan(committed));
            fake.Reply(OutcomeReply(feedback,"bring_"+second.EntityId));yield return null;
            Assert.That(outcomes,Has.Count.EqualTo(2));Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
            Assert.That(effects.Count(e=>e.SkillType=="PickUp" && e.TargetEntityId==first.EntityId && e.Status==SkillStatus.Succeeded),Is.Zero);
        }

        [UnityTest] public IEnumerator GoalResult_ChangedPlayerIdentity_DoesNotSpeakOrReplayWhenRestored()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal); var identity = request.recall_identity.current_subjects
                .Single(i => i.entity_id == player.GetComponent<WorldEntity>().EntityId);
            string original = identity.persistent_id; identity.persistent_id = "expired_test_player";
            yield return AwaitGoal(outcomes);
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("lifecycle_unavailable"));
            identity.persistent_id = original; AssertTerminalEvidence(request, terminal.Single(), false);
        }

        [UnityTest] public IEnumerator GoalResult_DisabledSpeakerOrChangedSession_DoesNotPresent()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal);
            request.context.runtime_state.source_session_id = "session_expired_test";
            yield return AwaitGoal(outcomes);
            Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("lifecycle_unavailable"));
            AssertTerminalEvidence(request, terminal.Single(), false);
            driver.enabled = false; ReplayTerminal(driver, terminal.Single());
            AssertTerminalEvidence(request, terminal.Single(), false);
        }

        [UnityTest] public IEnumerator GoalResult_DisabledWorldEntity_RejectsTerminalAndReplay()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal); merchant.enabled = false;
            merchant.GetComponent<GoalPlanRunner>().CancelCurrentPlan();
            Assert.That(outcomes, Has.Count.EqualTo(1));
            merchant.enabled = true; AssertTerminalEvidence(request, terminal.Single(), false);
            yield return null;
        }

        [UnityTest] public IEnumerator GoalResult_ChangedWorldScene_RejectsTerminalAndReplay()
        {
            var outcomes = new List<GoalOutcome>(); var terminal = new List<GoalPlanPresentation>();
            var request = StartDrinkGoal(outcomes, terminal); var original = merchant.gameObject.scene;
            var other = SceneManager.CreateScene("GoalResultOtherWorld");
            merchant.transform.SetParent(null, true); SceneManager.MoveGameObjectToScene(merchant.gameObject, other);
            merchant.GetComponent<GoalPlanRunner>().CancelCurrentPlan();
            Assert.That(outcomes, Has.Count.EqualTo(1));
            SceneManager.MoveGameObjectToScene(merchant.gameObject, original);
            AssertTerminalEvidence(request, terminal.Single(), false);
            yield return SceneManager.UnloadSceneAsync(other);
        }

        [UnityTest] public IEnumerator ConversationFacing_RearAcceptedSpeechTurnsBody_InputOpenAloneDoesNot()
        {
            var facing = merchant.GetComponentInChildren<FacingController>();
            var agent = merchant.GetComponent<NavMeshAgent>();
            var expressive = merchant.GetComponent<ExpressiveController>();
            Vector3 direction = player.transform.position - merchant.transform.position; direction.y = 0;
            merchant.transform.rotation = Quaternion.LookRotation(-direction);
            Quaternion before = merchant.transform.rotation; Vector3 position = merchant.transform.position;
            yield return Wait(.3f);
            Assert.That(Quaternion.Angle(before, merchant.transform.rotation), Is.LessThan(.1f), "E/input alone is not accepted conversation.");
            var r = Send("从后面叫住你，聊一会儿");
            dialogue.HideDialoguePresentation("outside_click");
            yield return Wait(.25f);
            float moved = Quaternion.Angle(before, merchant.transform.rotation);
            Assert.That(moved, Is.GreaterThan(0f).And.LessThan(70f), "Root turns smoothly, not by teleporting.");
            yield return Wait(2f);
            Assert.That(Vector3.Angle(merchant.transform.forward, direction), Is.LessThan(25f));
            Assert.That(Vector3.Distance(position, merchant.transform.position), Is.LessThan(.05f));
            Assert.That(agent.hasPath, Is.False); Assert.That(agent.updateRotation, Is.True);
            Assert.That(fake.Reply(Reply(r)), Is.True); yield return null;
            AssertWorldReply(r);
            Assert.That(expressive.ConversationSpeakerId, Is.EqualTo(player.GetComponent<WorldEntity>().EntityId));
            Assert.That(expressive.Speech.IsSpeaking, Is.True);
            var attention = merchant.GetComponentInChildren<AttentionController>(); attention.Tick(.1f);
            Assert.That(attention.CurrentTargetEntityId, Is.EqualTo(player.GetComponent<WorldEntity>().EntityId));
            expressive.Speech.Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            yield return null;
            Assert.That(expressive.ConversationSpeakerId, Is.Null, "Expired attention is cleared, not revived by input focus.");
            merchant.transform.rotation = Quaternion.LookRotation(-direction); before = merchant.transform.rotation;
            dialogue.ShowDialoguePresentation(); yield return Wait(.2f);
            Assert.That(Quaternion.Angle(before, merchant.transform.rotation), Is.LessThan(.1f));
            dialogue.ClearSession(); yield return null;
            Assert.That(expressive.ConversationSpeakerId, Is.Null); Assert.That(facing.IsBodyTurning, Is.False);
            Assert.That(agent.updateRotation, Is.True);
            Debug.Log("[NaturalFacingCafe] rear_accepted=true smooth_root=true hidden_input_speech=true position_preserved=true released=true");
        }

        [UnityTest] public IEnumerator ConversationFacing_FrontSmallAngleAndProtectedOwnersDoNotRotateRoot()
        {
            var facing = merchant.GetComponentInChildren<FacingController>();
            var channels = merchant.GetComponent<MotionChannelCoordinator>();
            var agent = merchant.GetComponent<NavMeshAgent>(); agent.ResetPath();
            Vector3 direction = player.transform.position - merchant.transform.position; direction.y = 0;
            merchant.transform.rotation = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 20, 0);
            var r = Send(); yield return null;
            Quaternion before = merchant.transform.rotation;
            facing.Tick(1); Assert.That(Quaternion.Angle(before, merchant.transform.rotation), Is.LessThan(.01f));
            foreach (float side in new[] { -90f, 90f })
            {
                merchant.transform.rotation = Quaternion.LookRotation(direction) * Quaternion.Euler(0, side, 0);
                Quaternion lateral = merchant.transform.rotation;
                facing.SetDialogueTarget(player.transform, true, true); facing.Tick(.1f);
                Assert.That(Quaternion.Angle(lateral, merchant.transform.rotation), Is.GreaterThan(0).And.LessThanOrEqualTo(9.01f));
                for (int tick = 0; tick < 20; tick++) facing.Tick(.1f);
                Assert.That(Vector3.Angle(merchant.transform.forward, direction), Is.LessThan(25f));
            }
            merchant.transform.rotation = Quaternion.LookRotation(-direction); before = merchant.transform.rotation;
            foreach (string owner in new[] { "formal", "carry", "interaction", "navigation", "emergency" })
            {
                if (owner == "formal") channels.BeginTaskMotion();
                if (owner == "carry") channels.SetCarryPresentationActive(true);
                if (owner == "interaction") channels.SetInteractionActive(true);
                if (owner == "navigation") channels.SetNavigationActive(true);
                if (owner == "emergency") channels.BeginEmergencyMotion();
                facing.SetDialogueTarget(player.transform, true, true); facing.Tick(1);
                Assert.That(Quaternion.Angle(before, merchant.transform.rotation), Is.LessThan(.01f), owner);
                Assert.That(agent.updateRotation, Is.True, owner);
                channels.EndTaskMotion(); channels.SetCarryPresentationActive(false); channels.SetInteractionActive(false);
                channels.SetNavigationActive(false); channels.EndEmergencyMotion();
            }
            // Another rotation owner already disabled agent rotation. Never overwrite or restore its flag.
            agent.updateRotation = false; facing.SetDialogueTarget(player.transform, true, true); facing.Tick(1);
            Assert.That(merchant.transform.rotation, Is.EqualTo(before));
            Assert.That(agent.updateRotation, Is.False); agent.updateRotation = true;
            driver.CancelPendingDialogueRequest(); yield return null;
        }

        [UnityTest] public IEnumerator ConversationFacing_AuthoredSeatedNpcsNeverTurnRoot_FromAnySide()
        {
            int checkedCount = 0;
            foreach (var npc in Object.FindObjectsByType<WorldEntity>())
            {
                var posture = npc.GetComponent<PerceptionContextProvider>();
                if (posture == null || !posture.HasAssignedFixedSeat) continue;
                var facing = npc.GetComponentInChildren<FacingController>();
                Assert.That(facing, Is.Not.Null);
                Quaternion before = npc.transform.rotation; Vector3 position = npc.transform.position;
                var target = new GameObject("SeatedFacingTarget");
                foreach (float yaw in new[] { 0f, 90f, 180f })
                {
                    target.transform.position = npc.transform.position + Quaternion.Euler(0,yaw,0) * npc.transform.forward * 1.5f;
                    facing.SetDialogueTarget(target.transform, true, true); facing.Tick(3);
                    Assert.That(npc.transform.rotation, Is.EqualTo(before)); Assert.That(npc.transform.position, Is.EqualTo(position));
                    Assert.That(facing.IsBodyTurning, Is.False);
                }
                facing.ResetPresentation(); Object.Destroy(target); checkedCount++;
            }
            Assert.That(checkedCount, Is.GreaterThanOrEqualTo(2));
            Debug.Log("[NaturalFacingCafe] seated_front_side_back_root_unchanged=" + checkedCount);
            yield return null;
        }

        [UnityTest] public IEnumerator ConversationFacing_MovingRoutineYieldsForRearChat_ThenResumes()
        {
            var routine = merchant.GetComponent<ReturnToHomeRoutine>();
            var admission = merchant.GetComponent<NpcDefaultRoutineController>();
            var agent = merchant.GetComponent<NavMeshAgent>();
            var facing = merchant.GetComponentInChildren<FacingController>();
            var outcomes = new List<EmbodiedNPC.Planning.GoalOutcome>();
            merchant.GetComponent<EmbodiedNPC.Planning.GoalPlanRunner>().GoalOutcomePublished += outcomes.Add;
            dialogue.ClearSession(); admission.ReleaseEngagement();
            Set(admission, "engagementHoldSeconds", .4f); Set(admission, "resumeDelay", .2f);
            Assert.That(NavMesh.SamplePosition(routine.HomePosition + Vector3.back * 3f, out var hit, .8f, agent.areaMask), Is.True);
            Assert.That(agent.Warp(hit.position), Is.True);
            double until = Time.realtimeSinceStartupAsDouble + 8;
            while ((!routine.OwnsPath || agent.velocity.magnitude < .05f) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(routine.OwnsPath && agent.hasPath, Is.True);
            Place(merchant.transform.position - merchant.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Vector3 direction = player.transform.position - merchant.transform.position; direction.y = 0;
            merchant.transform.rotation = Quaternion.LookRotation(-direction);
            var r = Send("从背后叫住你，聊聊吧");
            yield return Wait(2f);
            Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
            Assert.That(Vector3.Angle(merchant.transform.forward, direction), Is.LessThan(25f));
            Assert.That(fake.Reply(Reply(r)), Is.True); yield return null;
            AssertWorldReply(r);
            dialogue.HideDialoguePresentation("escape"); yield return null;
            Assert.That(merchant.GetComponent<ExpressiveController>().ConversationSpeakerId, Is.Not.Null);
            Assert.That(routine.OwnsPath, Is.False);
            dialogue.ClearSession(); merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            until = Time.realtimeSinceStartupAsDouble + 5;
            while (!routine.OwnsPath && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(routine.OwnsPath, Is.True);
            Assert.That(agent.updateRotation, Is.True); Assert.That(outcomes, Is.Empty);
            Assert.That(facing.IsBodyTurning, Is.False);
            Debug.Log("[NaturalFacingCafe] walking_routine_yielded=true rear_turn=true resumed=true fake_goal_outcomes=0");
        }

        [UnityTest] public IEnumerator ConversationFacing_AssignedSeatBlocksEvenAnIdleNavigationAgent()
        {
            var provider = merchant.GetComponent<PerceptionContextProvider>();
            var seat = Object.FindObjectsByType<FixedSeatBinding>().First();
            Set(seat, "assignedOccupant", merchant); Set(provider, "selfSeat", seat);
            Assert.That(provider.HasAssignedFixedSeat, Is.True);
            var facing = merchant.GetComponentInChildren<FacingController>();
            var agent = merchant.GetComponent<NavMeshAgent>(); agent.ResetPath();
            Vector3 direction = player.transform.position - merchant.transform.position; direction.y = 0;
            merchant.transform.rotation = Quaternion.LookRotation(-direction);
            Quaternion before = merchant.transform.rotation;
            facing.SetDialogueTarget(player.transform, true, true); facing.Tick(3);
            Assert.That(merchant.transform.rotation, Is.EqualTo(before));
            Assert.That(facing.IsBodyTurning, Is.False); Assert.That(agent.updateRotation, Is.True);
            yield return null;
        }

        private static string HiddenReason(DialogueController d) =>
            (string)typeof(DialogueController).GetProperty("LastPresentationHideReason")?.GetValue(d);
        private IEnumerator Click(Vector2 point)
        {
            var mouse = InputSystem.AddDevice<Mouse>("ReplyLifecycleMouse");
            try
            {
                InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
                yield return null; yield return null;
                InputSystem.QueueStateEvent(mouse, new MouseState { position = point }); yield return null;
            }
            finally { InputSystem.RemoveDevice(mouse); }
        }

        [UnityTest] public IEnumerator OpenDialogue_ThreeDelayedRounds_PresentsExactlyOnceAndRecordsActualText()
        {
            string conversation = dialogue.CurrentConversationId;
            for (int i = 0; i < 3; ++i)
            {
                int count = fake.Requests.Count;
                input.InputField.text = "第" + i + "轮，你喜欢画什么？";
                var button = (Button)typeof(TextUtteranceInput).GetField("submitButton", Private).GetValue(input);
                var rect = (RectTransform)button.transform;
                Vector2 centre = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
                yield return Click(centre);
                Assert.That(fake.Requests.Count, Is.EqualTo(count + 1), "Actual Send button must accept one turn.");
                var r = fake.Requests.Last();
                yield return Wait(1.4f);
                Assert.That(dialogue.IsPresentationVisible, Is.True);
                Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
                Assert.That(fake.Reply(Reply(r, "这是第" + i + "轮回答。")), Is.True);
                yield return null;
                Assert.That(runs.HasActiveRun, Is.False);
                Assert.That(shown.Count, Is.EqualTo(i + 1));
                Assert.That(shown.Last().RequestId, Is.EqualTo(r.request_id));
                Assert.That(drafts.Last(d => d.EventType == "dialogue_response").NaturalLanguagePayload,
                    Is.EqualTo(shown.Last().Utterance));
                fake.Reply(Reply(r));
                Assert.That(shown.Count, Is.EqualTo(i + 1));
            }
        }

        [UnityTest] public IEnumerator OpenDialogue_WaitsOverTenSeconds_WithoutAutomaticHideOrRoutineNavigation()
        {
            var r = Send(); Vector3 position = merchant.transform.position;
            yield return Wait(10.5f);
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(merchant.GetComponent<ReturnToHomeRoutine>().OwnsPath, Is.False);
            Assert.That(Vector3.Distance(position, merchant.transform.position), Is.LessThan(.1f));
            Assert.That(fake.Reply(Reply(r)), Is.True); yield return null;
            Assert.That(shown, Has.Count.EqualTo(1));
        }

        // Retires the old hidden-UI suppression requirement. Model generation still is not speech evidence.
        [UnityTest] public IEnumerator OutsideClickWhileWaiting_PresentsWorldSpeechOnceWithoutReopeningInput()
        {
            var r = Send(); yield return Wait(1.1f);
            var mode = Object.FindAnyObjectByType<DialogueInputModeController>();
            Vector2 outside = new(Screen.width * .9f, Screen.height * .8f);
            Assert.That(mode.IsOverDialogueUi(outside), Is.False);
            yield return Click(outside);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            yield return Wait(.5f); fake.Reply(Reply(r)); yield return null;
            Debug.Log("[ReplyLifecycleProbe] reason=" + HiddenReason(dialogue) + " terminal=" + runs.LastRun.State +
                " feedback=" + feedback.Last().Status + ":" + feedback.Last().Reason);
            Assert.That(feedback.Last().Status, Is.EqualTo("response_received"));
            Assert.That(runs.LastRun.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(HiddenReason(dialogue), Is.EqualTo("outside_click"));
            AssertWorldReply(r);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            dialogue.ShowDialoguePresentation(); yield return null;
            fake.Reply(Reply(r)); Assert.That(shown, Has.Count.EqualTo(1));
        }

        [UnityTest] public IEnumerator CloseButtonWhileWaiting_StillPresentsAndRecordsWorldSpeech()
        {
            var r = Send(); yield return Wait(.4f);
            input.CancelInput(); yield return Wait(.4f);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            fake.Reply(Reply(r)); yield return null;
            Assert.That(feedback.Last().Status, Is.EqualTo("response_received"));
            Assert.That(HiddenReason(dialogue), Is.EqualTo("close_button"));
            AssertWorldReply(r); Assert.That(driver.PendingRequest, Is.Null);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
        }

        [UnityTest] public IEnumerator EscapeWhileWaiting_ClosesInputButDoesNotCancelAcceptedSpeech()
        {
            var r = Send(); yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape)); yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState()); yield return null;
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            fake.Reply(Reply(r)); yield return null;
            Assert.That(HiddenReason(dialogue), Is.EqualTo("cancel_input"));
            Assert.That(feedback.Last().Status, Is.EqualTo("response_received")); AssertWorldReply(r);
        }

        [UnityTest] public IEnumerator OutOfInputRangeWhileWaiting_IsNotAWorldSpeechOrHearingGate()
        {
            var r = Send(); Place(merchant.transform.position + Vector3.forward * 6f);
            yield return null; yield return null;
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            fake.Reply(Reply(r)); yield return null;
            Assert.That(HiddenReason(dialogue), Is.EqualTo("out_of_range"));
            Assert.That(feedback.Last().Status, Is.EqualTo("response_received"));
            AssertWorldReply(r);
        }

        [UnityTest] public IEnumerator SwitchNpcWhileWaiting_OldNpcSpeaksInOriginalScopeWithoutTouchingNewInput()
        {
            var r = Send(); var oldConversation = dialogue.CurrentConversationId;
            Place(customer.transform.position + Vector3.forward);
            Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            string newConversation = dialogue.CurrentConversationId;
            Assert.That(newConversation, Is.Not.EqualTo(oldConversation));
            var otherDriver = customer.GetComponent<CognitiveRunDriver>();
            otherDriver.Configure(customer, customer.GetComponent<AgentOrchestrator>(),
                customer.GetComponent<CognitiveContextComposer>(), fake, customer.GetComponent<DialogueBubbleCognitivePresenter>());
            Set(customer.GetComponent<CognitiveDialogueSubmissionHandler>(), "backendClient", fake);
            otherDriver.enabled = true;
            Assert.That(input.SubmitText("你呢？"), Is.True);
            var otherRequest = fake.Requests.Last();
            Assert.That(otherRequest.run.agent_entity_id, Is.EqualTo(customer.EntityId));
            fake.Reply(Reply(r)); yield return null;
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(newConversation));
            AssertWorldReply(r);
            Assert.That(shown.Single().ConversationId, Is.EqualTo(oldConversation));
            Assert.That(drafts.Single(d => d.EventType == "dialogue_response").ConversationId, Is.EqualTo(oldConversation));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(customer.EntityId)
                .Any(m => m.Text == Reply(r).decision.utterance), Is.False);
            Assert.That(dialogue.ActiveSpeakerEntityId, Is.EqualTo(player.GetComponent<WorldEntity>().EntityId));
            Assert.That(otherDriver.PendingRequest, Is.SameAs(otherRequest));
            Assert.That(customer.GetComponent<AgentOrchestrator>().CurrentRun.RunId, Is.EqualTo(otherRequest.run.run_id));
            fake.Reply(Reply(otherRequest, "我也在这里。"));
            Assert.That(customer.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
        }

        [UnityTest] public IEnumerator ReopenBeforeOrAfterArrival_NeverDuplicatesWorldSpeech()
        {
            var r = Send(); dialogue.HideDialoguePresentation();
            dialogue.ShowDialoguePresentation(); yield return Wait(.3f);
            Assert.That(fake.Reply(Reply(r)), Is.True); yield return null;
            Assert.That(shown, Has.Count.EqualTo(1));
            var next = Send(); dialogue.HideDialoguePresentation(); fake.Reply(Reply(next)); yield return null;
            dialogue.ShowDialoguePresentation(); fake.Reply(Reply(next)); yield return null;
            Assert.That(shown, Has.Count.EqualTo(2));
            Assert.That(drafts.Count(d => d.EventType == "dialogue_response"), Is.EqualTo(2));
        }

        [UnityTest] public IEnumerator RejectedInputSelection_DoesNotCancelOriginalAcceptedSpeech()
        {
            var r = Send(); yield return Wait(.2f);
            Assert.That(dialogue.SelectTarget(null), Is.False);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            Assert.That(driver.PendingRequest, Is.SameAs(r));
            fake.Reply(Reply(r)); yield return null;
            AssertWorldReply(r);
        }

        [UnityTest] public IEnumerator ExplicitRequestCancellation_RejectsLateReplyWithoutNpcEvidence()
        {
            var r = Send(); yield return Wait(.3f);
            Assert.That(driver.CancelPendingDialogueRequest(), Is.True);
            Assert.That(driver.CancelPendingDialogueRequest(), Is.False);
            fake.Reply(Reply(r)); yield return null;
            Assert.That(runs.LastRun.State, Is.EqualTo(CognitiveRunState.Cancelled));
            Assert.That(driver.PendingRequest, Is.Null); AssertNoNpcEvidence();
            Assert.That(dialogue.IsPresentationVisible, Is.True);
        }

        [UnityTest] public IEnumerator ExplicitWorldInteractionEnd_RejectsLateReplyWithoutNpcEvidence()
        {
            var r = Send(); dialogue.ClearSession(); yield return Wait(.3f);
            fake.Reply(Reply(r)); yield return null;
            Assert.That(runs.LastRun.State, Is.EqualTo(CognitiveRunState.Cancelled));
            AssertNoNpcEvidence();
        }

        [UnityTest] public IEnumerator DisabledNpc_RejectsLateReplyWithoutNpcEvidence()
        {
            var r = Send(); merchant.gameObject.SetActive(false); yield return null;
            merchant.gameObject.SetActive(true); // Re-enabling must not resurrect the invalidated request.
            fake.Reply(Reply(r)); yield return null;
            AssertNoNpcEvidence();
        }

        [UnityTest] public IEnumerator ChangedWorldScene_RejectsLateReplyWithoutNpcEvidence()
        {
            var r = Send(); var original = merchant.gameObject.scene;
            var other = SceneManager.CreateScene("ReplyOtherWorld");
            merchant.transform.SetParent(null, true);
            SceneManager.MoveGameObjectToScene(merchant.gameObject, other);
            fake.Reply(Reply(r)); AssertNoNpcEvidence();
            SceneManager.MoveGameObjectToScene(merchant.gameObject, original);
            yield return SceneManager.UnloadSceneAsync(other);
        }

        [UnityTest] public IEnumerator HiddenInput_RealCafeBubbleViewAndSpeechInterval_AreActive()
        {
            var r = Send(); dialogue.HideDialoguePresentation("outside_click");
            yield return Wait(.5f); fake.Reply(Reply(r)); yield return null;
            AssertWorldReply(r);
            var view = Object.FindObjectsByType<DialogueBubbleView>()
                .Single(v => v.Message.SpeakerEntityId == merchant.EntityId && v.Message.Text == shown.Single().Utterance);
            Assert.That(view.gameObject.activeInHierarchy, Is.True);
            Assert.That(view.GetComponentInChildren<TMPro.TMP_Text>().text, Is.EqualTo(shown.Single().Utterance));
            Assert.That(view.ScreenRect.width, Is.GreaterThan(0));
            Assert.That(merchant.GetComponent<SpeechPresentationLifecycle>().IsSpeaking, Is.True);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            // A fresh accepted turn provides the real RecentDialogue cutoff for the evidence replay.
            dialogue.ShowDialoguePresentation(); Send("你刚才说了什么？");
            driver.CancelPendingDialogueRequest("test_completed");
            foreach (var draft in drafts.Where(d => d.EventType == "dialogue_input" || d.EventType == "dialogue_response"))
                Debug.Log("[WorldSpeechExperienceWire] " + ExperienceAppendRequest.FromDraft(draft).Serialize());
            Debug.Log("[WorldSpeechCafeSmoke] observed_before_reopen: input_visible=false bubble_active=true npc=" + merchant.EntityId +
                " request=" + r.request_id + " conversation=" + shown.Single().ConversationId + " text=" + shown.Single().Utterance);
        }

        [UnityTest] public IEnumerator BackendFailureAndPresenterFailure_HaveAccurateDifferentFeedback()
        {
            var r = Send();
            Assert.That(driver.TryConsumeFailure(new CognitiveTransportFailure(r.request_id, r.run.run_id,
                merchant.EntityId, "UPSTREAM_ERROR", r.trace_id, "UPSTREAM_ERROR")), Is.True);
            Assert.That(feedback.Last().Status, Is.EqualTo("failed"));
            string model = EmbodiedNPC.Settings.P0FeedbackText.Dialogue("UPSTREAM_ERROR").Chinese;
            string presentation = EmbodiedNPC.Settings.P0FeedbackText.Dialogue("PRESENTATION_UNAVAILABLE").Chinese;
            string closed = EmbodiedNPC.Settings.P0FeedbackText.Dialogue("DIALOGUE_PRESENTATION_HIDDEN").Chinese;
            Assert.That(presentation, Does.Not.Contain("模型设置"));
            Assert.That(closed, Does.Not.Contain("模型设置")); Assert.That(closed, Is.Not.EqualTo(model));
            var next = Send(); merchant.GetComponent<DialogueBubbleCognitivePresenter>().enabled = false;
            fake.Reply(Reply(next)); yield return null;
            Assert.That(feedback.Last().Status, Is.EqualTo("failed"));
            Assert.That(feedback.Last().Reason, Is.EqualTo("PRESENTATION_UNAVAILABLE")); AssertNoNpcEvidence();
        }
    }
}
