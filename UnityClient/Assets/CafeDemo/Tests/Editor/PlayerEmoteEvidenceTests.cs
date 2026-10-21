using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
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
    public sealed class EmoteEvidenceFakeBackend : BackendWebSocketClient
    {
        public readonly List<CognitiveRequest> Requests = new();
        public readonly List<JObject> Appends = new();
        public override bool IsConnected => isActiveAndEnabled;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest request)
        { Requests.Add(request); return Task.FromResult(true); }
        public override Task<bool> SendCognitiveCancelAsync(CognitiveRequest request) => Task.FromResult(true);
        public override Task<bool> SendExperienceAppendRequestAsync(ExperienceAppendRequest request)
        {
            Appends.Add(JObject.Parse(request.Serialize()));
            // Transport-only ACK permits the real queue to drain; Python tests separately ingest these exact drafts into SQLite.
            ProcessIncomingJson(JsonConvert.SerializeObject(new { message_type = "experience_append_result", schema_version = "1.0",
                request_id = request.request_id, trace_id = request.trace_id, event_id = "exp_test_" + Appends.Count,
                observer_id = request.draft.observer_id, experience_seq = Appends.Count, status = "inserted", reason = "STORED" }));
            return Task.FromResult(true);
        }
        public bool Reply(CognitiveResponse response) => ProcessIncomingJson(JsonConvert.SerializeObject(response));
    }

    public sealed class PlayerEmoteEvidenceTests : PlayerEmoteSocialFixture
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static string EvidencePath => Path.Combine(Path.GetTempPath(), "EmbodiedNPC-P1C-20261016", "evidence.json");
        [UnityTest] public IEnumerator Capture_RealEmoteShownReplyAndFollowingInput_ThroughExistingQueue()
        {
            var root = new GameObject("P1C_OfflineTransport"); root.SetActive(false);
            var fake = root.AddComponent<EmoteEvidenceFakeBackend>();
            var fields = new SerializedObject(fake); fields.FindProperty("connectOnStart").boolValue = false;
            fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws"; fields.ApplyModifiedPropertiesWithoutUndo(); root.SetActive(true);
            var registry = Object.FindAnyObjectByType<EntityRegistry>();
            var shown = new List<PresentedCognitiveUtterance>();
            foreach (var bridge in observers)
            {
                var owner = bridge.Observer; var store = owner.GetComponent<PerceivedStateStore>();
                store.enabled = false;
                typeof(PerceivedStateStore).GetField("observationExperience", Private).SetValue(store,
                    new ObservationExperienceAdapter(draft => fake.EnqueueExperience(draft), entityRegistry: registry));
                typeof(PerceivedStateStore).GetField("dialogueExperience", Private).SetValue(store,
                    new DialogueExperienceAdapter(draft => fake.EnqueueExperience(draft), registry));
                var driver = owner.GetComponent<CognitiveRunDriver>();
                driver.Configure(owner, owner.GetComponent<AgentOrchestrator>(), owner.GetComponent<CognitiveContextComposer>(), fake,
                    (MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour", Private).GetValue(driver));
                driver.UtterancePresented += shown.Add;
                store.enabled = true;
                owner.GetComponent<CognitiveDialogueSubmissionHandler>().Configure(owner, owner.GetComponent<AgentOrchestrator>(),
                    Object.FindAnyObjectByType<AgentEventRouter>(), fake);
            }
            // Shen cannot see the action; Wen is a genuine visible bystander.
            customer.transform.position = origin + Vector3.right * 8; customer.transform.rotation = Quaternion.LookRotation(Vector3.right);
            var witness = observers.Single(b => b.Observer.EntityId == "npc_cafe_companion_01").Observer;
            witness.transform.position = origin + Vector3.left * 2; witness.transform.rotation = Quaternion.LookRotation(Vector3.right);
            Physics.SyncTransforms();
            var history = Object.FindAnyObjectByType<DialogueHistoryPanel>(FindObjectsInactive.Include); history.enabled = true;
            player.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            yield return Perform("wave");
            var request = fake.Requests.Single();
            var reply = new CognitiveResponse { message_type="cognitive_response", schema_version="1.0", request_id=request.request_id,
                trace_id=request.trace_id, run_id=request.run.run_id, agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision { decision_kind=CognitiveDecisionKinds.DirectResponse, utterance="你好呀，欢迎来店里！今天想喝点什么，还是先坐会儿？" } };
            Assert.That(fake.Reply(reply), Is.True); Assert.That(fake.Reply(reply), Is.True);
            Assert.That(shown, Has.Count.EqualTo(1));
            // A duplicate notification must not create another durable reply.
            typeof(PerceivedStateStore).GetField("dialogueExperience", Private).GetValue(merchant.GetComponent<PerceivedStateStore>())
                .GetType().GetMethod("Handle", new[] { typeof(PresentedCognitiveUtterance) })
                .Invoke(typeof(PerceivedStateStore).GetField("dialogueExperience", Private).GetValue(merchant.GetComponent<PerceivedStateStore>()), new object[] { shown[0] });
            emote.Cancel(); merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            dialogue.enabled=false; fields=new SerializedObject(dialogue); fields.FindProperty("backendClient").objectReferenceValue=fake;
            fields.ApplyModifiedPropertiesWithoutUndo(); dialogue.enabled=true; dialogue.SetSetupReady(true);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            history.Toggle();
            Assert.That(history.DisplayText, Does.Contain("表情[挥手]").And.Contain(reply.decision.utterance));
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().SubmitText("我刚刚做了什么动作"), Is.True);
            yield return Wait(2f);
            var exported = fake.Appends.Where(a => (string)a["draft"]["event_type"] is "social_response" or "dialogue_input" ||
                (string)a["draft"]["structured_payload"]?["known_fields"]?["event_type"] == "player.emote").ToArray();
            Assert.That(exported.Count(a=>(string)a["draft"]["event_type"]=="social_response"), Is.EqualTo(1));
            Assert.That(exported.Any(a=>(string)a["draft"]["observer_id"]==customer.EntityId), Is.False);
            Assert.That(exported.Any(a=>(string)a["draft"]["observer_id"]==witness.EntityId), Is.True);
            Assert.That(fake.Requests, Has.Count.EqualTo(2));
            Assert.That(fake.ConnectionAttempts, Is.Zero);
            Directory.CreateDirectory(Path.GetDirectoryName(EvidencePath));
            File.WriteAllText(EvidencePath, JsonConvert.SerializeObject(new { captured_at=DateTimeOffset.UtcNow,
                appends=exported, next_request=JObject.Parse(fake.Requests.Last().Serialize()), unseen_npc=customer.EntityId,
                witness_npc=witness.EntityId, actually_presented=shown.Single().Utterance }, Formatting.Indented));
            Debug.Log("P1C evidence: " + EvidencePath);
        }
    }
}
