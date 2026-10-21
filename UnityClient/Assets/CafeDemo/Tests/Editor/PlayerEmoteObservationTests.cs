using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerEmoteObservationTests : PlayerPresentationFixture
    {
        [UnityTest] public IEnumerator WaveThenDialogue_DoesNotBlockInputPersistence()
        {
            // Use the real Cafe observer/presenter/queue and a separate strict Backend with a controlled model.
            string endpoint = Environment.GetEnvironmentVariable("EMBODIEDNPC_TEST_BACKEND_URL");
            Assert.That(endpoint, Is.Not.Null.And.Not.Empty, "This persistence test requires an isolated Backend.");
            ControlledPerception();
            EvidenceFor(merchant, "current_visible"); EvidenceFor(customer, "lost");
            Place(new Vector3(-.8f, .02f, 1.25f));
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(BackendWebSocketClient).GetField("serverUrl", flags).SetValue(backend, endpoint);
            backend.EnsureConnected();
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!backend.IsConnected && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(backend.IsConnected, Is.True);
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var orchestrator = merchant.GetComponent<AgentOrchestrator>();
            var presentations = new List<PresentedCognitiveUtterance>();
            var appends = new Dictionary<string, JObject>();
            var acknowledgements = new HashSet<string>();
            var errors = new List<string>();
            // Capture pending payloads before their ACK removes them, including inside presentation/submission callbacks.
            Action capture = () =>
            {
                object coordinator = typeof(BackendWebSocketClient).GetField("experienceWrites", flags).GetValue(backend);
                if (coordinator == null) return;
                var pending = (IList)coordinator.GetType().GetField("pending", flags).GetValue(coordinator);
                foreach (object entry in pending)
                {
                    var request = (ExperienceAppendRequest)entry.GetType().GetProperty("Request").GetValue(entry);
                    appends[request.request_id] = JObject.Parse(request.Serialize());
                }
            };
            Action<PresentedCognitiveUtterance> presented = value => { presentations.Add(value); capture(); };
            Action<ValidatedExperienceAppendResult> acknowledged = value =>
            { if (value.IsPersisted) acknowledgements.Add(value.RequestId); };
            object socket = typeof(BackendWebSocketClient).GetField("websocket", flags).GetValue(backend);
            var messageEvent = socket.GetType().GetEvent("OnMessage");
            Action<byte[]> incoming = bytes =>
            {
                var message = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                if ((string)message["message_type"] == "error") errors.Add((string)message["reason"]);
            };
            driver.UtterancePresented += presented;
            backend.ValidatedExperienceAppendResultReceived += acknowledged;
            var receiver = Delegate.CreateDelegate(messageEvent.EventHandlerType, incoming.Target, incoming.Method);
            messageEvent.AddEventHandler(socket, receiver);
            try
            {
                yield return Wait(.2f);
                Assert.That(player.IsInputSuspended, Is.False, "Wave requires the normal gameplay input mode.");
                InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.G));
                yield return null;
                yield return null;
                Assert.That(emote.IsActive, Is.True, $"G must enter Wave: device={keyboard.enabled}, animator={animator.isInitialized}");
                yield return Wait(.4f); Keys();
                deadline = Time.realtimeSinceStartup + 8f;
                while ((presentations.Count == 0 || orchestrator.HasActiveRun) && Time.realtimeSinceStartup < deadline)
                { capture(); yield return null; }
                Assert.That(presentations, Has.Count.EqualTo(1));
                var wave = presentations[0];
                var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
                Assert.That(request.context.current_event.event_type, Is.EqualTo("player.emote"));
                Assert.That(request.context.current_event.payload.Any(x => x.key == "conversation_id"), Is.False);
                Assert.That(wave.SourceEventId, Is.EqualTo(merchant.GetComponent<PlayerEmoteAgentEventBridge>().LastEvent.EventId));
                Assert.That(wave.ConversationId, Is.Empty);
                Assert.That(wave.IsDialogueEvidence, Is.False);
                Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Last().Text,
                    Is.EqualTo(wave.Utterance), "Non-dialogue output must still be presented.");
                capture();
                Assert.That(appends.Values.Any(x => (string)x["draft"]["event_type"] == "dialogue_response" &&
                    (string)x["draft"]["request_id"] == wave.RequestId), Is.False);
                Assert.That(orchestrator.HasActiveRun, Is.False);

                var dialogue = Object.FindAnyObjectByType<DialogueController>();
                Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
                Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().SubmitText("Wave 后立即进行正常对话"), Is.True);
                capture();
                deadline = Time.realtimeSinceStartup + 8f;
                while ((presentations.Count < 2 || backend.PendingExperienceCount > 0 || orchestrator.HasActiveRun) &&
                    Time.realtimeSinceStartup < deadline) { capture(); yield return null; }
                Assert.That(presentations, Has.Count.EqualTo(2));
                Assert.That(presentations[1].IsDialogueEvidence, Is.True);
                Assert.That(presentations[1].ConversationId, Is.EqualTo(dialogue.CurrentConversationId));
                var dialogueAppends = appends.Where(x => (string)x.Value["draft"]["conversation_id"] ==
                    dialogue.CurrentConversationId).ToArray();
                Assert.That(dialogueAppends.Select(x => (string)x.Value["draft"]["event_type"]),
                    Is.EquivalentTo(new[] { "dialogue_input", "dialogue_response" }));
                foreach (var append in dialogueAppends) Assert.That(acknowledgements, Does.Contain(append.Key));
                Assert.That(appends.Values.Any(x => (string)x["draft"]["event_type"] == "dialogue_response" &&
                    string.IsNullOrWhiteSpace((string)x["draft"]["conversation_id"])), Is.False);
                Assert.That(backend.PendingExperienceCount, Is.Zero);
                Assert.That(orchestrator.HasActiveRun, Is.False);
                Assert.That(errors, Is.Empty);
            }
            finally
            {
                Keys(); driver.UtterancePresented -= presented;
                backend.ValidatedExperienceAppendResultReceived -= acknowledged; messageEvent.RemoveEventHandler(socket, receiver);
            }
        }

        private void EvidenceFor(WorldEntity observer, string visibility, bool nearby = false)
        {
            var fields = new Dictionary<string, string>();
            if (visibility != null) fields["visibility_state"] = visibility;
            if (nearby) fields["proximity_state"] = "nearby";
            var store = observer.GetComponent<PerceivedStateStore>();
            store.ApplyOrRefresh(new Observation("p1:" + observer.EntityId, observer.EntityId,
                ObservationSourceType.ContinuousScan, "p1:player", ObservationChannel.Visual, "player_01",
                fields, Array.Empty<string>(), 1, world.WorldRevision, Time.unscaledTimeAsDouble,
                double.PositiveInfinity));
        }
        private static PlayerEmotePerformed Performed() => new("p1-value", "player_01", "wave", 10d);

        [UnityTest] public IEnumerator VisibleMerchant_InvisibleCustomer_ExactlyOneTargetedWakeAndRequest()
        {
            ControlledPerception(); EvidenceFor(merchant, "current_visible"); EvidenceFor(customer, "lost");
            int merchantRuns = 0, customerRuns = 0;
            merchant.GetComponent<AgentOrchestrator>().RunStarted += _ => merchantRuns++;
            customer.GetComponent<AgentOrchestrator>().RunStarted += _ => customerRuns++;
            int revision = world.WorldRevision;
            Keys(Key.G); yield return Wait(.4f);
            var bridge = merchant.GetComponent<PlayerEmoteAgentEventBridge>();
            Assert.That(bridge.EventCount, Is.EqualTo(1)); Assert.That(customer.GetComponent<PlayerEmoteAgentEventBridge>().EventCount, Is.Zero);
            Assert.That(merchantRuns, Is.EqualTo(1)); Assert.That(customerRuns, Is.Zero);
            Assert.That(merchant.GetComponent<AgentEventReceiver>().LastInterestDecision, Is.EqualTo(EventInterestDecision.Wake));
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request, Is.Not.Null); Assert.That(request.IsValid, Is.True);
            Assert.That(request.context.current_event.event_type, Is.EqualTo("player.emote"));
            Assert.That(request.context.current_event.source_entity_id, Is.EqualTo("player_01"));
            Assert.That(request.context.current_event.target_entity_id, Is.EqualTo(merchant.EntityId));
            Assert.That(request.context.current_event.payload.Single(kv => kv.key == "emote_id").value, Is.EqualTo("wave"));
            Assert.That(request.context.current_event.event_id, Is.EqualTo(bridge.LastEvent.EventId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); Keys();
        }

        [Test] public void NearbyOnlyAndRememberedEvidence_DoNotProject()
        {
            ControlledPerception(); EvidenceFor(merchant, "lost", true); EvidenceFor(customer, "remembered", true);
            Assert.That(merchant.GetComponent<PlayerEmoteAgentEventBridge>().TryProject(Performed(), out _), Is.False);
            Assert.That(customer.GetComponent<PlayerEmoteAgentEventBridge>().TryProject(Performed(), out _), Is.False);
        }

        [Test] public void BothVisible_InterestDoesNotPromoteBystandersToTargets()
        {
            // Actual Animator/vision/single-request coverage lives in PlayerEmoteSocialTests.T1/T2.
            // This former regression explicitly expected collective greetings; keep the authored-rule guard instead.
            ControlledPerception();
            foreach (var owner in new[] { merchant, customer })
            {
                var payload = new Dictionary<string, string> {
                    [AgentEventPayloadKeys.EmoteId] = "wave",
                    [AgentEventPayloadKeys.SocialObserver] = owner.EntityId,
                    [AgentEventPayloadKeys.SocialResponseEligible] = owner == merchant ? "true" : "false"
                };
                var observed = new AgentEvent("emote:" + owner.EntityId, "player.emote", "player_01", merchant.EntityId, "", payload, 10);
                Assert.That(EventInterestEvaluator.Evaluate(observed, owner.EntityId, owner.GetComponent<AgentEventReceiver>().InterestProfile),
                    Is.EqualTo(owner == merchant ? EventInterestDecision.Wake : EventInterestDecision.Record));
            }
        }

        [UnityTest] public IEnumerator InvisibleNearby_NoEventNoWaveRun()
        {
            ControlledPerception(); EvidenceFor(merchant, "lost", true); EvidenceFor(customer, "remembered", true);
            int runs = 0; merchant.GetComponent<AgentOrchestrator>().RunStarted += _ => runs++;
            customer.GetComponent<AgentOrchestrator>().RunStarted += _ => runs++;
            Keys(Key.G); yield return Wait(.4f); Keys();
            Assert.That(merchant.GetComponent<PlayerEmoteAgentEventBridge>().EventCount, Is.Zero);
            Assert.That(customer.GetComponent<PlayerEmoteAgentEventBridge>().EventCount, Is.Zero); Assert.That(runs, Is.Zero);
        }

        [Test] public void ObserverMismatchAndUnknownActor_NoProjection_NoMandatoryFloorChange()
        {
            ControlledPerception(); EvidenceFor(merchant, "current_visible");
            var bridge = merchant.GetComponent<PlayerEmoteAgentEventBridge>();
            Assert.That(bridge.TryProject(new PlayerEmotePerformed("p1-other", "unknown", "wave", 10), out _), Is.False);
            bridge.Configure(emote, merchant, customer.GetComponent<PerceivedStateStore>(), Object.FindAnyObjectByType<AgentEventRouter>(), world);
            Assert.That(bridge.TryProject(Performed(), out _), Is.False);
            var empty = ScriptableObject.CreateInstance<EventInterestProfile>(); empty.Configure(EventInterestDecision.Ignore);
            Assert.That(EventInterestEvaluator.Evaluate(new AgentEvent("p1", "player.emote", "player_01", merchant.EntityId, "", null, 10), merchant.EntityId, empty), Is.EqualTo(EventInterestDecision.Ignore));
            Object.Destroy(empty);
        }

        [UnityTest] public IEnumerator BridgeDisableAndConfigure_SubscriptionsStayBalanced()
        {
            ControlledPerception(); EvidenceFor(merchant, "current_visible"); EvidenceFor(customer, "lost");
            var bridge = merchant.GetComponent<PlayerEmoteAgentEventBridge>(); bridge.enabled = false;
            Keys(Key.G); yield return Wait(2.6f); Keys(); Assert.That(bridge.EventCount, Is.Zero);
            bridge.enabled = true;
            bridge.Configure(emote,merchant,merchant.GetComponent<PerceivedStateStore>(),Object.FindAnyObjectByType<AgentEventRouter>(),world);
            bridge.Configure(emote,merchant,merchant.GetComponent<PerceivedStateStore>(),Object.FindAnyObjectByType<AgentEventRouter>(),world);
            Keys(Key.G); yield return Wait(.4f); Keys(); Assert.That(bridge.EventCount, Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator ActualVision_MainCamera_VisibleAndHiddenWave()
        {
            // Keep real sensors and their range/FOV/occlusion path. No injected visibility fields here.
            Place(new Vector3(-.8f, .02f, 2f));
            customer.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                customer.transform.position - player.transform.position, Vector3.up));
            yield return Wait(.4f);
            merchant.GetComponent<VisionSensor>().ScanNow(); customer.GetComponent<VisionSensor>().ScanNow();
            Assert.That(merchant.GetComponent<VisionSensor>().CurrentVisibleEntityIds, Does.Contain("player_01"));
            Assert.That(customer.GetComponent<VisionSensor>().CurrentVisibleEntityIds, Does.Not.Contain("player_01"));
            merchant.GetComponent<AgentOrchestrator>().CancelCurrentRun(); customer.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            int customerWaveRuns = 0;
            customer.GetComponent<AgentOrchestrator>().RunStarted += _ => {
                if (customer.GetComponent<WakeCognitiveTriggerBridge>().LastTrigger.Reason == "player.emote") customerWaveRuns++;
            };
            Keys(Key.G); yield return Wait(.5f); Capture("visible-merchant-wave"); Keys();
            Assert.That(merchant.GetComponent<PlayerEmoteAgentEventBridge>().EventCount, Is.EqualTo(1));
            Assert.That(customer.GetComponent<PlayerEmoteAgentEventBridge>().EventCount, Is.Zero); Assert.That(customerWaveRuns, Is.Zero);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request.context.current_event.event_type, Is.EqualTo("player.emote"));
            Assert.That(request.context.current_event.payload.Single(k => k.key == "emote_id").value, Is.EqualTo("wave"));
        }
    }
}
