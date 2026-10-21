using System;
using System.Collections.Generic;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests
{
    public sealed class DecisionRecordingPresenter : MonoBehaviour, ICognitiveDecisionPresenter
    {
        public int Count { get; private set; }
        public CognitiveDecision Decision { get; private set; }
        public CognitivePresentationContext Context { get; private set; }
        public bool TryPresentCognitiveUtterance(string text) => throw new InvalidOperationException("Wrong seam");
        public bool TryPresentCognitiveDecision(CognitiveDecision decision, CognitivePresentationContext context)
        { Count++; Decision = decision; Context = context; return true; }
    }

    public sealed class ExpressiveFoundationTests
    {
        private readonly List<Object> objects = new();
        private GameObject Root(string id = "npc_test")
        {
            var root = new GameObject(id); objects.Add(root);
            root.AddComponent<WorldEntity>().Configure(id, "character.npc.test", EntityKind.Agent, AgentRole.Npc);
            return root;
        }
        [TearDown] public void TearDown()
        { foreach (Object value in objects) if (value != null) Object.DestroyImmediate(value); objects.Clear(); }

        [Test] public void GestureLease_PersistsUntilMatchingCompletion()
        {
            var channel = Root().AddComponent<MotionChannelCoordinator>();
            Assert.That(channel.TryBeginGesture("wave", false, 10, 3, out long id), Is.True);
            Assert.That(channel.ActiveBodyChannel, Is.EqualTo(MotionChannel.Gesture));
            Assert.That(channel.CompleteGesture(id + 1), Is.False);
            Assert.That(channel.TryBeginGesture("nod", false, 10, 3, out _), Is.False);
            Assert.That(channel.CompleteGesture(id), Is.True);
            Assert.That(channel.CompleteGesture(id), Is.False);
            Assert.That(channel.ActiveBodyChannel, Is.EqualTo(MotionChannel.None));
        }
        [Test] public void MotionPriority_PreemptsWithoutReplayAndKeepsPhasesIndependent()
        {
            var channel = Root().AddComponent<MotionChannelCoordinator>();
            channel.TryBeginGesture("wave", false, 0, 3, out _);
            channel.SetNavigationActive(true);
            Assert.That(channel.ActiveGestureId, Is.Zero);
            Assert.That(channel.OccupiedChannels, Is.EqualTo(MotionChannel.Locomotion));
            channel.SetInteractionActive(true);
            Assert.That(channel.ActiveBodyChannel, Is.EqualTo(MotionChannel.Interaction));
            Assert.That(channel.TryBeginGesture("nod", false, 0, 3, out _), Is.False);
            channel.SetInteractionActive(false);
            Assert.That(channel.ActiveBodyChannel, Is.EqualTo(MotionChannel.Locomotion));
            channel.BeginEmergencyMotion();
            Assert.That(channel.ActiveBodyChannel, Is.EqualTo(MotionChannel.Emergency));
            channel.EndEmergencyMotion();
            Assert.That(channel.ActiveGestureId, Is.Zero);
        }
        [Test] public void GestureTimeout_DisableAndInvalidClockCannotLeaveLease()
        {
            var channel = Root().AddComponent<MotionChannelCoordinator>();
            Assert.That(channel.TryBeginGesture("wave", false, double.NaN, 3, out _), Is.False);
            Assert.That(channel.TryBeginGesture("wave", false, 0, float.NaN, out _), Is.False);
            Assert.That(channel.TryBeginGesture("wave", true, 0, 3, out _), Is.False);
            channel.TryBeginGesture("wave", false, 10, 1000, out _);
            channel.Tick(20); Assert.That(channel.ActiveGestureId, Is.Zero);
            channel.TryBeginGesture("wave", false, 20, 3, out _);
            // EditMode does not run MonoBehaviour lifecycle; exercise the callback here,
            // with real disable/teardown covered separately in the Cafe Play session.
            Callback(channel, "OnDisable"); Assert.That(channel.ActiveGestureId, Is.Zero);
        }
        [Test] public void LegacyTaskAndGestureWrappersRemainUsable()
        {
            var channel = Root().AddComponent<MotionChannelCoordinator>(); int approved = 0;
            channel.GestureApproved += _ => approved++;
            channel.BeginTaskMotion(); Assert.That(channel.RequestGesture("wave", false), Is.False);
            channel.EndTaskMotion(); Assert.That(channel.RequestGesture("wave", false), Is.True);
            Assert.That(approved, Is.EqualTo(1)); Assert.That(channel.ActiveGestureId, Is.Not.Zero);
            channel.CancelGesture();
        }
        [Test] public void SpeechReplacementAndCancel_EmitExactlyOnceEndBeforeNextStart()
        {
            var speech = Root().AddComponent<SpeechPresentationLifecycle>(); var events = new List<string>();
            speech.SpeechStarted += value => events.Add("S" + value.Id);
            speech.SpeechEnded += value => events.Add("E" + value.Id);
            long first = speech.BeginPresentedText("Hello", 0);
            long second = speech.BeginPresentedText("Again", 1);
            Assert.That(speech.EndPlayback(first), Is.False);
            speech.Cancel(); speech.Cancel(); speech.Tick(100);
            Assert.That(events, Is.EqualTo(new[] { "S" + first, "E" + first, "S" + second, "E" + second }));
        }
        [Test] public void SpeechTimer_IsBoundedAndExplicitPlaybackUsesRealFinish()
        {
            var speech = Root().AddComponent<SpeechPresentationLifecycle>();
            Assert.That(speech.TextDuration("a"), Is.InRange(1.2f, 6f));
            Assert.That(speech.TextDuration(new string('x', 10000)), Is.EqualTo(6f));
            speech.BeginPresentedText("Hello", 10); speech.Tick(10.1); Assert.That(speech.IsSpeaking, Is.True);
            speech.Tick(16); Assert.That(speech.IsSpeaking, Is.False);
            long playback = speech.BeginPlayback("TTS seam"); speech.Tick(10000);
            Assert.That(speech.IsSpeaking, Is.True); Assert.That(speech.EndPlayback(playback), Is.True);
        }
        [Test] public void SpeechDisableAndDestroy_EndExactlyOnce()
        {
            var root = Root(); var speech = root.AddComponent<SpeechPresentationLifecycle>(); int ends = 0;
            speech.SpeechEnded += _ => ends++;
            speech.BeginPlayback("Hello"); Callback(speech, "OnDisable");
            Assert.That(ends, Is.EqualTo(1)); Callback(speech, "OnDestroy"); Assert.That(ends, Is.EqualTo(1));
        }
        [Test] public void PerNpcHintsSpeechAndChannelsAreIsolatedAndCleanupResets()
        {
            var first = Root("first"); var second = Root("second");
            var a = Attach(first); var b = Attach(second);
            a.ReceivePresentedDecision(Decision(), Context("first")); a.Speech.BeginPresentedText("Hello", 0);
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Talking));
            Assert.That(a.ExpressionHint, Is.EqualTo("happy"));
            Assert.That(b.State, Is.EqualTo(DialoguePresentationState.Idle));
            a.MotionChannels.TryBeginGesture("wave", false, 0, 3, out _);
            Assert.That(b.MotionChannels.TryBeginGesture("nod", false, 0, 3, out _), Is.True);
            b.ReceivePresentedDecision(Decision(), Context("first"));
            Assert.That(b.ExpressionHint, Is.EqualTo("neutral"));
            Callback(a, "OnDisable"); Assert.That(a.Speech.IsSpeaking, Is.False);
            Assert.That(a.ExpressionHint, Is.EqualTo("neutral")); Assert.That(a.MotionChannels.ActiveGestureId, Is.Zero);
            Assert.That(b.MotionChannels.ActiveGestureId, Is.Not.Zero);
        }
        [Test] public void UnavailableAdapterDoesNotReportPlaybackOrAcquireChannel()
        {
            var root = Root(); var expressive = Attach(root);
            var playback = root.AddComponent<GestureController>();
            expressive.Configure(root.GetComponent<WorldEntity>(), expressive.MotionChannels, expressive.Speech, playback: playback);
            Assert.That(playback.CanPlay("wave"), Is.False);
            Assert.That(playback.CanPlay("nod"), Is.False);
            expressive.ReceivePresentedDecision(Decision(), Context("npc_test"));
            Assert.That(expressive.MotionChannels.ActiveGestureId, Is.Zero);
            Assert.That(playback.TryPlayApprovedGesture("wave", 0), Is.False);
        }
        [TestCase(5d)] [TestCase(9d)]
        public void BubbleLifetime_DifferentSpeakersExpireIndependently(double customerCreatedAt)
        {
            var manager = BubbleManager();
            manager.PushMessage("merchant", "first", DialogueSpeakerKind.Npc, 0);
            manager.PushMessage("customer", "second", DialogueSpeakerKind.Npc, customerCreatedAt);
            manager.Refresh(9);
            Assert.That(manager.GetMessages("merchant").Count, Is.EqualTo(1));
            Assert.That(manager.GetMessages("customer").Count, Is.EqualTo(1));
            manager.Refresh(10);
            Assert.That(manager.GetMessages("merchant"), Is.Empty);
            Assert.That(manager.GetMessages("customer")[0].CreatedAt, Is.EqualTo(customerCreatedAt));
            manager.Refresh(customerCreatedAt + 10);
            Assert.That(manager.GetMessages("customer"), Is.Empty);
        }

        [Test] public void BubbleLifetime_SameSpeakerMessagesExpireIndependently()
        {
            var manager = BubbleManager();
            manager.PushMessage("merchant", "first", DialogueSpeakerKind.Npc, 0);
            var second = manager.PushMessage("merchant", "second", DialogueSpeakerKind.Npc, 4);
            manager.Refresh(10);
            Assert.That(manager.GetMessages("merchant").Count, Is.EqualTo(1));
            Assert.That(manager.GetMessages("merchant")[0].MessageId, Is.EqualTo(second.MessageId));
            manager.Refresh(14);
            Assert.That(manager.GetMessages("merchant"), Is.Empty);
        }

        [Test] public void BubbleLifetime_OffscreenViewStillExpiresAndIsDestroyed()
        {
            var manager = BubbleManager();
            var canvasRoot = new GameObject("Canvas", typeof(Canvas)); objects.Add(canvasRoot);
            var cameraRoot = new GameObject("Camera", typeof(Camera)); objects.Add(cameraRoot);
            var templateRoot = new GameObject("Template", typeof(RectTransform), typeof(DialogueBubbleView));
            templateRoot.transform.SetParent(manager.transform);
            manager.Configure(canvasRoot.GetComponent<Canvas>(), cameraRoot.GetComponent<Camera>(),
                templateRoot.GetComponent<DialogueBubbleView>());
            var message = manager.PushMessage("player", "offscreen", DialogueSpeakerKind.Player, 0);
            var view = manager.GetComponentInChildren<DialogueBubbleView>();
            manager.Refresh(9);
            Assert.That(view.gameObject.activeSelf, Is.False, "Missing anchor hides the view only.");
            Assert.That(manager.GetMessages("player")[0].CreatedAt, Is.EqualTo(message.CreatedAt));
            manager.Refresh(10);
            Assert.That(manager.GetMessages("player"), Is.Empty);
            Assert.That(view == null, Is.True, "Expiry must destroy the hidden view.");
        }

        [Test] public void BubbleLifetime_ClearMessagesStillClearsAllSpeakers()
        {
            var manager = BubbleManager();
            manager.PushMessage("player", "first", DialogueSpeakerKind.Player, 0);
            manager.PushMessage("merchant", "second", DialogueSpeakerKind.Npc, 4);
            manager.ClearMessages();
            Assert.That(manager.GetMessages("player"), Is.Empty);
            Assert.That(manager.GetMessages("merchant"), Is.Empty);
        }

        private DialogueBubbleManager BubbleManager()
        {
            var layer = new GameObject("Bubbles", typeof(RectTransform)); objects.Add(layer);
            var manager = layer.AddComponent<DialogueBubbleManager>();
            var serialized = new SerializedObject(manager);
            var lifetime = serialized.FindProperty("bubbleLifetimeSeconds");
            lifetime.floatValue = 10f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return manager;
        }

        [Test] public void MissingBubbleTemplateIsPresentationFailureAndDoesNotStartSpeech()
        {
            var root = Root(); var expressive = Attach(root);
            var layer = new GameObject("Bubbles", typeof(RectTransform)); objects.Add(layer);
            var manager = layer.AddComponent<DialogueBubbleManager>();
            var presenter = root.AddComponent<DialogueBubbleCognitivePresenter>();
            presenter.Configure(root.GetComponent<WorldEntity>(), manager);
            Assert.That(presenter.TryPresentCognitiveDecision(Decision(), Context("npc_test")), Is.False);
            Assert.That(expressive.Speech.IsSpeaking, Is.False);
            Assert.That(expressive.ExpressionHint, Is.EqualTo("neutral"));
        }
        [Test] public void AttentionContextNeverIncludesEventSourceByItself()
        {
            var request = BareRequest("npc_test");
            request.context.current_event = new CognitiveEventDto { source_entity_id = "hidden" };
            var context = new CognitivePresentationContext(request, "hidden", "conversation");
            Assert.That(context.AllowsAttentionTarget("hidden"), Is.False);
            Assert.That(context.AllowsAttentionTarget(null), Is.False);
        }
        [TestCase("request")] [TestCase("trace")] [TestCase("run")] [TestCase("agent")]
        public void DriverStaleCorrelationCannotDeliverMetadata(string mismatch)
        {
            SetupDriver(out var driver, out var presenter, out var request);
            var response = Response(request);
            switch (mismatch) { case "request": response.request_id = "stale"; break;
                case "trace": response.trace_id = "stale"; break; case "run": response.run_id = "stale"; break;
                case "agent": response.agent_entity_id = "other"; break; }
            Assert.That(driver.TryConsumeResponse(response), Is.False); Assert.That(presenter.Count, Is.Zero);
            Assert.That(driver.PendingRequest, Is.SameAs(request));
        }
        [Test] public void OwnedDirectResponseDeliversCopyWithCorrelationAndNoActionIsSilent()
        {
            SetupDriver(out var driver, out var presenter, out var request); var response = Response(request);
            Assert.That(driver.TryConsumeResponse(response), Is.True); Assert.That(presenter.Count, Is.EqualTo(1));
            Assert.That(presenter.Decision, Is.Not.SameAs(response.decision));
            Assert.That(presenter.Decision.emotion, Is.EqualTo("happy"));
            Assert.That(presenter.Decision.gesture, Is.EqualTo("wave"));
            Assert.That(presenter.Decision.look_at, Is.EqualTo("player_01"));
            Assert.That(presenter.Context.RequestId, Is.EqualTo(request.request_id));
            Assert.That(presenter.Context.TraceId, Is.EqualTo(request.trace_id));
            Assert.That(presenter.Context.RunId, Is.EqualTo(request.run.run_id));
            SetupDriver(out var silentDriver, out var silent, out var silentRequest);
            var noAction = Response(silentRequest); noAction.decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.NoAction };
            Assert.That(silentDriver.TryConsumeResponse(noAction), Is.True); Assert.That(silent.Count, Is.Zero);
        }
        [Test] public void PassiveActionRejectionNeverDeliversMisleadingMetadata()
        {
            SetupDriver(out var driver, out var presenter, out var request); var response = Response(request);
            response.decision.decision_kind = CognitiveDecisionKinds.ActionProposal;
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                bindings = new Dictionary<string, string> { ["target"] = "cup_01", ["destination"] = "player_01" } };
            response.validated_goal = new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" };
            Assert.That(response.IsValid, Is.True);
            Assert.That(driver.TryConsumeResponse(response), Is.False); Assert.That(presenter.Count, Is.Zero);
        }
        private ExpressiveController Attach(GameObject root)
        {
            var channel = root.AddComponent<MotionChannelCoordinator>(); var speech = root.AddComponent<SpeechPresentationLifecycle>();
            var expressive = root.AddComponent<ExpressiveController>(); expressive.Configure(root.GetComponent<WorldEntity>(), channel, speech); return expressive;
        }
        private static void Callback(MonoBehaviour component, string method) => component.GetType()
            .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(component, null);
        private static CognitiveDecision Decision() => new() { decision_kind = CognitiveDecisionKinds.DirectResponse,
            utterance = "Hello", emotion = "happy", gesture = "wave", look_at = "player_01" };
        private static CognitiveRequest BareRequest(string id) => new() { request_id = "request", trace_id = "trace",
            run = new CognitiveRunDto { run_id = "run", agent_entity_id = id }, context = new CognitiveContextSnapshot() };
        private static CognitivePresentationContext Context(string id) => new(BareRequest(id), "player_01", "conversation");
        private static CognitiveResponse Response(CognitiveRequest request) => new() { message_type = "cognitive_response", schema_version = "1.0",
            request_id = request.request_id, trace_id = request.trace_id, run_id = request.run.run_id,
            agent_entity_id = request.run.agent_entity_id, decision = Decision() };
        private void SetupDriver(out CognitiveRunDriver driver, out DecisionRecordingPresenter presenter, out CognitiveRequest request)
        {
            var root = Root(Guid.NewGuid().ToString("N")); var entity = root.GetComponent<WorldEntity>();
            var profile = ScriptableObject.CreateInstance<EventInterestProfile>(); objects.Add(profile); profile.Configure(EventInterestDecision.Ignore);
            var receiver = root.AddComponent<AgentEventReceiver>(); receiver.Configure(entity, profile: profile);
            var bridge = root.AddComponent<WakeCognitiveTriggerBridge>(); bridge.Configure(receiver);
            var orchestrator = root.AddComponent<AgentOrchestrator>(); orchestrator.Configure(entity, bridge);
            var composer = root.AddComponent<CognitiveContextComposer>(); presenter = root.AddComponent<DecisionRecordingPresenter>();
            driver = root.AddComponent<CognitiveRunDriver>(); driver.Configure(entity, orchestrator, composer, null, presenter);
            var trigger = new CognitiveTrigger("trigger", CognitiveTriggerTypes.Event, entity.EntityId, "event", 1d, AgentEventTypes.PlayerEnteredArea);
            Assert.That(orchestrator.TryStartRun(trigger, out CognitiveRun run), Is.True);
            var triggerDto = CognitiveTriggerDto.From(trigger);
            request = new CognitiveRequest { request_id = "request", trace_id = "trace", run = CognitiveRunDto.From(run), trigger = triggerDto,
                context = new CognitiveContextSnapshot { run_id = run.RunId, agent_entity_id = entity.EntityId, trigger = triggerDto,
                    current_event = new CognitiveEventDto { event_id = "event", event_type = AgentEventTypes.PlayerEnteredArea, source_entity_id = "player_01" },
                    character = new CognitiveCharacterDto { character_id = "cafe.merchant", display_name = "Merchant", role_summary = "Merchant", backstory = "Works at a cafe", personality = "Reserved", speaking_style = "Friendly", boundaries = "No invented facts" },
                    perception = PerceptionSnapshotDto.Empty(entity.EntityId) } };
            Assert.That(driver.TryOwnRequest(request), Is.True);
        }
    }
}
