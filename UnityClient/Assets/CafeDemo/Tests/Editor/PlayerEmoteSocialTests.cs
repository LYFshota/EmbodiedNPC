using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Real authored observers, vision, Animator start and Cognition; only transport and poses are controlled.</summary>
    public abstract class PlayerEmoteSocialFixture : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        protected PlayerEmoteAgentEventBridge[] observers;
        protected readonly Dictionary<string, CounterFakeBackend> transports = new();
        protected readonly List<PlayerEmotePerformed> starts = new();
        protected readonly Vector3 origin = new(0, 50, 0);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [UnitySetUp] public IEnumerator PrepareSocial()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "EmoteSocial_TestFloor";
            floor.transform.position = origin + Vector3.down * .5f;
            floor.transform.localScale = new Vector3(20, 1, 20);
            Place(origin);
            observers = Object.FindObjectsByType<PlayerEmoteAgentEventBridge>(FindObjectsSortMode.None)
                .OrderBy(b => b.Observer.EntityId).ToArray();
            Assert.That(observers.Length, Is.GreaterThanOrEqualTo(2));
            foreach (var bridge in observers)
            {
                var owner = bridge.Observer;
                foreach (var behaviour in owner.GetComponents<MonoBehaviour>())
                    if (behaviour is DailyBehaviorController || behaviour is ReturnToHomeRoutine ||
                        behaviour is NpcDefaultRoutineController || behaviour is VisualObservationAgentEventBridge ||
                        behaviour is ProximitySensor) behaviour.enabled = false;
                var nav = owner.GetComponent<NavMeshAgent>(); if (nav != null) nav.enabled = false;
                var runs = owner.GetComponent<AgentOrchestrator>(); runs.CancelCurrentRun();
                var motion = owner.GetComponent<MotionChannelCoordinator>();
                if (motion != null)
                { motion.EndEmergencyMotion(); motion.EndTaskMotion(); motion.SetNavigationActive(false); motion.SetInteractionActive(false); motion.SetCarryPresentationActive(false); }
                owner.GetComponent<SpeechPresentationLifecycle>()?.Cancel();
                var root = new GameObject("EmoteSocial_InMemoryTransport"); root.SetActive(false);
                var fake = root.AddComponent<CounterFakeBackend>();
                var fields = new SerializedObject(fake);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                fields.ApplyModifiedPropertiesWithoutUndo(); root.SetActive(true);
                var driver = owner.GetComponent<CognitiveRunDriver>();
                var presenter = (MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour", Private).GetValue(driver);
                driver.Configure(owner, runs, owner.GetComponent<CognitiveContextComposer>(), fake, presenter);
                transports.Add(owner.EntityId, fake);
            }
            // Two visible social participants; the remaining NPCs face away outside the response range.
            Pose(merchant, new Vector3(0, 0, 2)); Pose(customer, new Vector3(-2, 0, 0));
            int index = 0;
            foreach (var bridge in observers.Where(b => b.Observer != merchant && b.Observer != customer))
            { bridge.Observer.transform.position = origin + new Vector3(8 + index++, 0, 0); bridge.Observer.transform.rotation = Quaternion.LookRotation(Vector3.right); }
            emote.EmotePerformed += starts.Add;
            yield return Wait(.2f);
            foreach (var bridge in observers) bridge.RefreshVision();
            Assert.That(Bridge(merchant).CanSee("player_01"), Is.True);
            Assert.That(Bridge(customer).CanSee("player_01"), Is.True);
        }
        protected PlayerEmoteAgentEventBridge Bridge(WorldEntity owner) => observers.Single(b => b.Observer == owner);
        private void Pose(WorldEntity owner, Vector3 offset)
        {
            owner.transform.position = origin + offset;
            owner.transform.rotation = Quaternion.LookRotation(-offset);
            Physics.SyncTransforms();
        }
        protected IEnumerator Perform(string emoteId)
        {
            int before = starts.Count;
            Assert.That(emote.TryPlay(emoteId), Is.True);
            Assert.That(starts.Count, Is.EqualTo(before), "Selection alone must not publish before visual state entry.");
            yield return Wait(.4f);
            Assert.That(starts.Count, Is.EqualTo(before + 1));
        }
        protected void AssertSingleRequest(WorldEntity expected)
        {
            Assert.That(transports.Sum(p => p.Value.Requests.Count), Is.EqualTo(1));
            var request = transports[expected.EntityId].Requests.Single();
            Assert.That(request.IsValid, Is.True);
            Assert.That(request.context.current_event.occurred_at, Is.LessThanOrEqualTo(request.context.runtime_state.captured_at),
                "AgentEvent must use the execution snapshot clock; UTC seconds are rejected by the real Backend.");
            Assert.That(Bridge(expected).LastObservation.OccurredAt, Is.GreaterThan(1_000_000_000d),
                "Keep the original UTC evidence used by the existing observation history adapter.");
            var history = EmbodiedNPC.Memory.ObservationExperienceAdapter.Create(Bridge(expected).LastObservation,
                registry: Object.FindAnyObjectByType<EntityRegistry>());
            Assert.That(history.StructuredPayload["event_utc"], Is.EqualTo(Bridge(expected).LastObservation.OccurredAt));
            Assert.That(history.ClockDomain, Is.EqualTo("unity_realtime"));
            Assert.That(history.SourceSessionId, Is.EqualTo(EmbodiedNPC.Memory.ClientSessionIdentity.Current));
            Assert.That(history.OccurredAt, Is.LessThanOrEqualTo(Time.realtimeSinceStartupAsDouble));
            Assert.That(request.context.current_event.event_type, Is.EqualTo("player.emote"));
            Assert.That(transports[expected.EntityId].Reply(new CognitiveResponse {
                message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = expected.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.NoAction }
            }), Is.True, "Scripted NO_ACTION is a legal independent social choice.");
            Assert.That(expected.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
            foreach (var transport in transports.Values)
            { Assert.That(transport.ConnectionAttempts, Is.Zero); transport.Requests.Clear(); }
        }
        protected void AssertBystander(WorldEntity owner)
        {
            var bridge = Bridge(owner);
            Assert.That(bridge.LastObservation, Is.Not.Null);
            Assert.That(bridge.LastObservation.Channel, Is.EqualTo(ObservationChannel.Visual));
            Assert.That(bridge.LastObservation.SubjectEntityId, Is.Empty, "Transient social facts must not overwrite actor state.");
            Assert.That(bridge.LastEvent.Payload[AgentEventPayloadKeys.SocialResponseEligible], Is.EqualTo("false"));
            Assert.That(owner.GetComponent<AgentEventReceiver>().LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(transports[owner.EntityId].Requests, Is.Empty);
        }

    }

    public sealed class PlayerEmoteSocialTests : PlayerEmoteSocialFixture
    {
        [UnityTest] public IEnumerator T1_WaveAtMerchant_OnlyMerchantMayWake()
        {
            player.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            yield return Perform("wave");
            Assert.That(starts.Single().TargetEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(starts.Single().ResponderEntityId, Is.EqualTo(merchant.EntityId));
            AssertBystander(customer); AssertSingleRequest(merchant);
            foreach (var bridge in observers.Where(b => b.Observer != merchant && b.Observer != customer))
                Assert.That(bridge.EventCount, Is.Zero, "NPCs facing away cannot observe the action.");
            int count = Bridge(merchant).EventCount;
            Bridge(merchant).SendMessage("HandleEmote", starts.Single());
            Assert.That(Bridge(merchant).EventCount, Is.EqualTo(count), "Duplicate visual starts must not wake twice.");
        }
        [UnityTest] public IEnumerator T2_WaveAtCustomer_UsesBodyFacingInsteadOfCamera()
        {
            // The merchant sees the actor but not the intended customer behind this local occluder.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = origin + new Vector3(-1, 1.5f, 1);
            wall.transform.localScale = new Vector3(.5f, 3, .5f);
            Physics.SyncTransforms();
            player.transform.rotation = Quaternion.LookRotation(Vector3.left);
            Camera.main.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            yield return Perform("wave");
            Assert.That(starts.Single().TargetEntityId, Is.EqualTo(customer.EntityId));
            Assert.That(Bridge(merchant).LastEvent.TargetEntityId, Is.Empty, "An unseen target must not leak through the observer event.");
            Assert.That(Bridge(merchant).LastObservation.UnknownFields, Does.Contain("target_entity_id"));
            Assert.That(Bridge(merchant).LastObservation.KnownFields.ContainsKey("target_entity_id"), Is.False);
            AssertBystander(merchant); AssertSingleRequest(customer);
        }
        [UnityTest] public IEnumerator T3_WaveAtEmptySpace_NoTargetOrWake()
        {
            player.transform.rotation = Quaternion.LookRotation(Vector3.back);
            yield return Perform("wave");
            Assert.That(starts.Single().TargetEntityId, Is.Empty);
            Assert.That(starts.Single().ResponderEntityId, Is.Empty);
            AssertBystander(merchant); AssertBystander(customer);
            Assert.That(transports.Sum(p => p.Value.Requests.Count), Is.Zero);
            Assert.That(Bridge(customer).LastObservation.UnknownFields, Does.Contain("target_entity_id"));
        }
        [UnityTest] public IEnumerator T4_CheerFlex_ObservableSemantics_AtMostOneUnaddressedResponse()
        {
            foreach (string id in new[] { "cheer", "proud" })
            {
                // A busy merchant can observe but cannot win the expressive social response opportunity.
                merchant.GetComponent<MotionChannelCoordinator>().SetInteractionActive(true);
                yield return Perform(id);
                var performed = starts.Last();
                Assert.That(performed.EmoteId, Is.EqualTo(id == "proud" ? "flex" : "cheer"));
                Assert.That(performed.TargetEntityId, Is.Empty);
                Assert.That(performed.ResponderEntityId, Is.EqualTo(customer.EntityId));
                AssertBystander(merchant);
                var projected = Bridge(customer).LastEvent;
                Assert.That(projected.TargetEntityId, Is.Empty);
                Assert.That(projected.Payload["emote_meaning"], Does.Contain(id == "proud" ? "ability_and_personality_unknown" : "cause_unknown"));
                Assert.That(Bridge(customer).LastObservation.UnknownFields, Does.Contain("target_entity_id"));
                AssertSingleRequest(customer);
                emote.Cancel(); yield return Wait(.15f);
            }
        }
        [UnityTest] public IEnumerator T5_RapidEmotes_KeepClockOwnershipAndOnePresentedHistoryPerReply()
        {
            var shown = new List<PresentedCognitiveUtterance>();
            var drafts = new List<EmbodiedNPC.Memory.ExperienceDraft>();
            var registry = Object.FindAnyObjectByType<EntityRegistry>();
            foreach (var bridge in observers)
            {
                var history = new EmbodiedNPC.Memory.DialogueExperienceAdapter(drafts.Add, registry);
                bridge.Observer.GetComponent<CognitiveRunDriver>().UtterancePresented += shown.Add;
                bridge.Observer.GetComponent<CognitiveRunDriver>().UtterancePresented += history.Handle;
            }
            player.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            foreach (string id in new[] { "wave", "cheer", "proud" })
            {
                // Deliberately keep earlier requests pending while the next visual action starts.
                yield return Perform(id);
                emote.Cancel(); yield return Wait(.05f);
            }
            Assert.That(starts.Select(s => s.EmoteId), Is.EqualTo(new[] { "wave", "cheer", "flex" }));
            Assert.That(starts[0].ResponderEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(starts[1].ResponderEntityId, Is.EqualTo(customer.EntityId));
            Assert.That(starts[2].ResponderEntityId, Is.Empty, "Both visible NPCs already own a Run.");
            var requests = transports.Values.SelectMany(t => t.Requests).ToArray();
            Assert.That(requests.Length, Is.EqualTo(2));
            foreach (var request in requests)
            {
                Assert.That(request.context.current_event.occurred_at, Is.LessThanOrEqualTo(request.context.runtime_state.captured_at));
                Assert.That(request.context.current_event.event_id,
                    Does.EndWith(":" + request.run.agent_entity_id));
                var reply = new CognitiveResponse { message_type = "cognitive_response", schema_version = request.schema_version,
                    request_id = request.request_id, trace_id = request.trace_id, run_id = request.run.run_id,
                    agent_entity_id = request.run.agent_entity_id,
                    decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                        utterance = "快速动作回礼 " + request.run.agent_entity_id } };
                Assert.That(transports[request.run.agent_entity_id].Reply(reply), Is.True);
                Assert.That(transports[request.run.agent_entity_id].Reply(reply), Is.True);
            }
            Assert.That(shown.Count, Is.EqualTo(2));
            Assert.That(shown.Select(s => s.RequestId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(drafts.Count, Is.EqualTo(2));
            Assert.That(drafts.All(d => d.IsValid && d.EventType == "social_response" && d.ClockDomain == "unity_realtime"), Is.True);
            Assert.That(drafts.Select(d => d.SourceEventId).Distinct().Count(), Is.EqualTo(2));
            foreach (var reply in shown)
            {
                Assert.That(reply.SourceEventId, Does.EndWith(":" + reply.OwnerAgentEntityId));
                Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(reply.OwnerAgentEntityId)
                    .Count(m => m.Text == reply.Utterance), Is.EqualTo(1));
            }
            Assert.That(observers.All(b => !b.Observer.GetComponent<AgentOrchestrator>().HasActiveRun), Is.True);
        }

    }
}
