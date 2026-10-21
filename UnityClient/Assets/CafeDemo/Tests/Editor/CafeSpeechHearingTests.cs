using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeSpeechHearingTests : PlayerEmoteSocialFixture
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private EmoteEvidenceFakeBackend fake;
        private DialogueController dialogue;
        private readonly List<Observation> heard = new();
        private static bool Hearing(JObject value) => (string)value["draft"]?["structured_payload"]?["known_fields"]?["event_type"] == "heard_speech";
        private IEnumerator OfflineSpeech(WorldEntity target = null)
        {
            var root = new GameObject("Hearing_OfflineTransport"); root.SetActive(false);
            fake = root.AddComponent<EmoteEvidenceFakeBackend>();
            var fields = new SerializedObject(fake); fields.FindProperty("connectOnStart").boolValue = false;
            fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
            fields.ApplyModifiedPropertiesWithoutUndo(); root.SetActive(true);
            var registry = Object.FindAnyObjectByType<EntityRegistry>();
            foreach (var observer in observers)
            {
                var entity = observer.Observer;
                var store = entity.GetComponent<PerceivedStateStore>(); store.enabled = false;
                typeof(PerceivedStateStore).GetField("observationExperience", Private).SetValue(store,
                    new ObservationExperienceAdapter(d => fake.EnqueueExperience(d), entityRegistry: registry));
                typeof(PerceivedStateStore).GetField("dialogueExperience", Private).SetValue(store,
                    new DialogueExperienceAdapter(d => fake.EnqueueExperience(d), registry));
                store.enabled = true;
                store.ObservationApplied += r => { if (r.Observation.KnownFields.TryGetValue("event_type", out var type) && type == "heard_speech") heard.Add(r.Observation); };
                var driver = entity.GetComponent<CognitiveRunDriver>();
                driver.Configure(entity, entity.GetComponent<AgentOrchestrator>(), entity.GetComponent<CognitiveContextComposer>(),
                    fake, (MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour", Private).GetValue(driver));
                entity.GetComponent<CognitiveDialogueSubmissionHandler>().Configure(entity, entity.GetComponent<AgentOrchestrator>(),
                    Object.FindAnyObjectByType<EmbodiedNPC.Events.AgentEventRouter>(), fake);
            }
            dialogue = Object.FindAnyObjectByType<DialogueController>(); dialogue.enabled = false;
            fields = new SerializedObject(dialogue); fields.FindProperty("backendClient").objectReferenceValue = fake;
            fields.ApplyModifiedPropertiesWithoutUndo();
            typeof(DialogueController).GetField("dialogueExperienceAdapter", Private).SetValue(dialogue,
                new DialogueExperienceAdapter(d => fake.EnqueueExperience(d), registry));
            dialogue.enabled = true; dialogue.SetSetupReady(true);
            var direct = target ?? merchant;
            player.transform.rotation = Quaternion.LookRotation(direct.transform.position - player.transform.position);
            foreach (var observer in observers) observer.RefreshVision();
            Assert.That(Object.FindObjectsByType<HearingSensor>(FindObjectsSortMode.None).Length, Is.EqualTo(4));
            Assert.That(Object.FindAnyObjectByType<CafeSpeechHearingBridge>(), Is.Not.Null);
            Assert.That(dialogue.SelectTarget(direct.GetComponent<DialogueParticipant>()), Is.True);
            yield return null;
        }
        private void Submit(string text) => Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().SubmitText(text), Is.True);
        private void Reply(CognitiveRequest request, string text) => Assert.That(fake.Reply(new CognitiveResponse {
            message_type="cognitive_response", schema_version="1.0", request_id=request.request_id,
            trace_id=request.trace_id, run_id=request.run.run_id, agent_entity_id=request.run.agent_entity_id,
            decision=new CognitiveDecision { decision_kind=text == null ? CognitiveDecisionKinds.NoAction : CognitiveDecisionKinds.DirectResponse, utterance=text }
        }), Is.True);

        [UnityTest] public IEnumerator T1_AcceptedSpeechAndShownReply_ExportForIsolatedLedger()
        {
            yield return OfflineSpeech();
            Submit("薇薇，帮我做一杯拿铁。");
            Assert.That(fake.Requests, Has.Count.EqualTo(1));
            var first = fake.Requests.Single();
            Assert.That(heard.Count(o => o.ObserverEntityId == customer.EntityId), Is.EqualTo(1));
            Submit("忙碌期间应被拒绝的输入");
            Assert.That(fake.Requests, Has.Count.EqualTo(1));
            Assert.That(heard.Count(o => o.ObserverEntityId == customer.EntityId), Is.EqualTo(1));
            const string reply = "好呀，我这就给你做。";
            Reply(first, reply); Reply(first, reply);
            var local = heard.Where(o => o.ObserverEntityId == customer.EntityId).ToArray();
            Assert.That(local.Length, Is.EqualTo(2));
            Assert.That(local[0].KnownFields["actor_entity_id"], Is.EqualTo("player_01"));
            Assert.That(local[1].KnownFields["actor_entity_id"], Is.EqualTo(merchant.EntityId));
            Assert.That(local[0].SourceEventId, Is.Not.EqualTo(local[1].SourceEventId));
            Assert.That(local[0].TraceId, Is.EqualTo(local[1].TraceId));
            Assert.That(local[0].KnownFields["run_id"], Is.EqualTo(first.run.run_id));
            Assert.That(local[1].KnownFields["run_id"], Is.EqualTo(first.run.run_id));
            Assert.That(local[1].KnownFields["text"], Is.EqualTo(reply));
            Assert.That(local.All(o => o.Channel == ObservationChannel.Audio), Is.True);
            Assert.That(fake.Requests, Has.Count.EqualTo(1), "Overhearing cannot wake another NPC.");
            Assert.That(heard.Any(o => o.ObserverEntityId == merchant.EntityId), Is.False);
            merchant.GetComponent<SpeechPresentationLifecycle>()?.Cancel();
            Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            Submit("你知道我刚才点了什么吗？");
            var question = fake.Requests.Last();
            int before = heard.Count;
            Reply(question, null);
            Assert.That(heard, Has.Count.EqualTo(before), "NO_ACTION must not create NPC speech.");
            yield return Wait(3f);
            Assert.That(fake.Appends.Count(Hearing), Is.GreaterThanOrEqualTo(2));
            var directory = Path.Combine(Path.GetTempPath(), "EmbodiedNPC-Hearing-P1F-20261016");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "t1.json"), JsonConvert.SerializeObject(new {
                captured_at=DateTimeOffset.UtcNow, appends=fake.Appends.Where(a => Hearing(a) ||
                    (string)a["draft"]["event_type"] == "dialogue_input").ToArray(),
                next_request=JObject.Parse(question.Serialize()), observer=customer.EntityId, reply
            }, Formatting.Indented));
            Assert.That(fake.ConnectionAttempts, Is.Zero);
        }

        [UnityTest] public IEnumerator T2_DistancePerUtteranceAndUnknownParticipants()
        {
            yield return OfflineSpeech();
            // A nearby witness can hear text without visually identifying either participant.
            var unknown = observers.First(o => o.Observer != merchant && o.Observer != customer).Observer;
            unknown.GetComponent<HearingSensor>().ConfigureIdentity(HearingIdentityMode.VisualRequired);
            Assert.That(unknown.GetComponent<HearingSensor>().IdentityMode, Is.EqualTo(HearingIdentityMode.VisualRequired));
            unknown.transform.position = origin + Vector3.back * 2f;
            unknown.GetComponent<VisionSensor>().enabled = false;
            Submit("薇薇，帮我做一杯拿铁。");
            Assert.That(heard.Count(o => o.ObserverEntityId == customer.EntityId), Is.EqualTo(1));
            var anonymous = heard.Single(o => o.ObserverEntityId == unknown.EntityId);
            Assert.That(anonymous.KnownFields.ContainsKey("actor_entity_id"), Is.False);
            Assert.That(anonymous.KnownFields.ContainsKey("target_entity_id"), Is.False);
            Assert.That(anonymous.UnknownFields, Does.Contain("target_entity_id"));
            var far = observers.First(o => o.Observer != merchant && o.Observer != customer && o.Observer != unknown).Observer;
            Assert.That(heard.Any(o => o.ObserverEntityId == far.EntityId), Is.False);
            customer.transform.position = origin + Vector3.right * 20f;
            Reply(fake.Requests.Single(), "好呀，我这就给你做。");
            Assert.That(heard.Count(o => o.ObserverEntityId == customer.EntityId), Is.EqualTo(1));
            Assert.That(fake.Requests, Has.Count.EqualTo(1));
            yield return Wait(3f);
            foreach (var append in fake.Appends.Where(Hearing))
            {
                Assert.That((string)append["draft"]["provenance"], Is.EqualTo("HEARD"));
                Assert.That(append["draft"]["conversation_id"].Type, Is.EqualTo(JTokenType.Null));
            }
            Assert.That(fake.Appends.Where(Hearing).Select(a => (string)a["draft"]["observer_id"] + ":" +
                (string)a["draft"]["source_event_id"]).Distinct().Count(), Is.EqualTo(fake.Appends.Count(Hearing)));
            Assert.That(fake.ConnectionAttempts, Is.Zero);
        }

        [UnityTest] public IEnumerator T1_EventIdentity_ShenSpeechHeardByWenWithoutVision()
        {
            yield return OfflineSpeech(customer);
            var listener = observers.Single(o => o.Observer.EntityId == "npc_cafe_companion_01").Observer;
            listener.transform.position = origin + Vector3.forward * 2f;
            listener.GetComponent<VisionSensor>().enabled = false;
            Assert.That(listener.GetComponent<HearingSensor>().IdentityMode, Is.EqualTo(HearingIdentityMode.EventIdentity),
                "CafeDemoV2 must serialize EventIdentity, rather than relying on a test override.");
            Submit("知意，早啊，今天过得怎么样？");
            var first = fake.Requests.Single();
            const string reply = "早。我挺好的，坐着慢慢喝点东西。你呢？";
            Reply(first, reply);
            var local = heard.Where(o => o.ObserverEntityId == listener.EntityId).ToArray();
            Assert.That(local.Length, Is.EqualTo(2));
            Assert.That(local[0].KnownFields["actor_entity_id"], Is.EqualTo("player_01"));
            Assert.That(local[0].KnownFields["target_entity_id"], Is.EqualTo(customer.EntityId));
            Assert.That(local[1].KnownFields["actor_entity_id"], Is.EqualTo(customer.EntityId));
            Assert.That(local[1].KnownFields["target_entity_id"], Is.EqualTo("player_01"));
            foreach (var observation in local)
            {
                Assert.That(observation.Channel, Is.EqualTo(ObservationChannel.Audio));
                Assert.That(observation.KnownFields["actor_epistemic_status"], Is.EqualTo("identified_from_speech_event"));
                Assert.That(observation.KnownFields["target_epistemic_status"], Is.EqualTo("identified_from_speech_event"));
            }
            Assert.That(fake.Requests, Has.Count.EqualTo(1), "Bystanders must remain silent.");
            customer.GetComponent<SpeechPresentationLifecycle>()?.Cancel();
            Assert.That(dialogue.SelectTarget(listener.GetComponent<DialogueParticipant>()), Is.True);
            Submit("你有听到我刚刚和知意聊什么吗？");
            var followup = fake.Requests.Last(); Reply(followup, null);
            yield return Wait(3f);
            var directory = Path.Combine(Path.GetTempPath(), "EmbodiedNPC-Hearing-P1F-20261016");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "event_identity_t1.json"), JsonConvert.SerializeObject(new {
                appends=fake.Appends.Where(a => (string)a["draft"]["observer_id"] == listener.EntityId &&
                    (Hearing(a) || (string)a["draft"]["event_type"] == "dialogue_input")).ToArray(),
                next_request=JObject.Parse(followup.Serialize()), observer=listener.EntityId, speaker=customer.EntityId, reply
            }, Formatting.Indented));
            Assert.That(fake.ConnectionAttempts, Is.Zero);
        }
    }
}
