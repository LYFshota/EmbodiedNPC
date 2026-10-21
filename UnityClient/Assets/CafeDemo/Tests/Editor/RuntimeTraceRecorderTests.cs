using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class RuntimeTraceRecorderTests
    {
        [Test] public void P3E_PlayerTraceProjectsTechnicalDataAndStopsAtByteLimit()
        {
            string path = Path.Combine(Path.GetTempPath(), "p3e-trace-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                using (var writer = new RuntimeTraceJsonlWriter(path,"synthetic-session","CafeDemoV2",true,2048))
                {
                    Assert.IsTrue(writer.TryWrite("cognition","cognitive_response","npc_cafe_01",null,null,null,null,null,
                        new { utterance="PRIVATE_SYNTHETIC_DIALOGUE", prompt="PRIVATE_SYNTHETIC_PROMPT", failure_code="COGNITIVE_PROMPT_BUDGET_EXCEEDED" },DateTime.UtcNow,0));
                    string first = string.Join("\n",ReadLinesWhileWriting(path));
                    Assert.That(first,Does.Not.Contain("PRIVATE_SYNTHETIC"));
                    Assert.That(first,Does.Contain("COGNITIVE_PROMPT_BUDGET_EXCEEDED"));
                    for (int i=0;i<20 && writer.IsOpen;i++) writer.TryWrite("session","test",null,null,null,null,null,null,new {},DateTime.UtcNow,i);
                    Assert.AreEqual("TRACE_SIZE_LIMIT",writer.LastError);Assert.IsFalse(writer.IsOpen);
                }
                Assert.LessOrEqual(new FileInfo(path).Length,2048);
            }
            finally { if(File.Exists(path))File.Delete(path); }
        }
        [Test]
        public void WriterFlushesIndependentJsonLinesWithOneSessionAndIncreasingSequence()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cafe-trace-writer-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                using (var writer = new RuntimeTraceJsonlWriter(path, "session_test", "CafeDemoV2"))
                {
                    for (int index = 0; index < 3; index++)
                        Assert.That(writer.TryWrite("cognition", "event_" + index, "npc_01", null, null,
                            null, null, null, new { index }, DateTime.UtcNow, index), Is.True);
                    Assert.That(ReadLinesWhileWriting(path).Length, Is.EqualTo(3), "Each event must flush before dispose.");
                }
                JObject[] lines = File.ReadAllLines(path).Select(JObject.Parse).ToArray();
                Assert.That(lines.Select(line => (int)line["seq"]), Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(lines.All(line => (string)line["session_id"] == "session_test" &&
                    (string)line["category"] == "cognition" &&
                    (string)line["schema_version"] == "runtime-trace/1.0"), Is.True);
                Assert.That(lines.Select(line => (string)line["kind"]),
                    Is.EqualTo(new[] { "event_0", "event_1", "event_2" }));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [UnityTest]
        public IEnumerator CafeRecorderProjectsRequestAndBothResponseKindsWithoutDuplicateSubscriptions()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            yield return new EnterPlayMode();
            RuntimeTraceRecorder recorder = Object.FindAnyObjectByType<RuntimeTraceRecorder>();
            Assert.That(recorder, Is.Not.Null);
            string path = recorder.CurrentTracePath;
            Assert.That(File.Exists(path), Is.True);

            GameObject source = new("TraceTestComposer");
            CognitiveContextComposer composer = source.AddComponent<CognitiveContextComposer>();
            recorder.enabled = false;
            recorder.enabled = true;
            path = recorder.CurrentTracePath;
            recorder.RefreshTraceSubscriptions();
            recorder.RefreshTraceSubscriptions();
            var created = (Action<CognitiveRequest>)typeof(CognitiveContextComposer)
                .GetField("CognitiveRequestCreated", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(composer);
            Assert.That(created, Is.Not.Null);
            created(Request());

            MethodInfo recordResponse = typeof(RuntimeTraceRecorder).GetMethod("RecordResponse",
                BindingFlags.Instance | BindingFlags.NonPublic);
            recordResponse.Invoke(recorder, new object[] { Response(false) });
            recordResponse.Invoke(recorder, new object[] { Response(true) });
            JObject[] lines = ReadLinesWhileWriting(path).Select(JObject.Parse).ToArray();
            JObject[] requests = lines.Where(line => (string)line["kind"] == "cognitive_request_created" &&
                (string)line["request_id"] == "request_trace_test").ToArray();
            Assert.That(requests.Length, Is.EqualTo(1));
            Assert.That((string)requests[0]["request_id"], Is.EqualTo("request_trace_test"));
            Assert.That((string)requests[0]["data"]["perception"]["entities"][0]["entity_id"], Is.EqualTo("cup_01"));
            Assert.That((string)requests[0]["data"]["capability_action_ids"][0], Is.EqualTo("bring_object"));
            JObject[] responses = lines.Where(line => (string)line["kind"] == "cognitive_response" &&
                (string)line["request_id"] == "request_trace_test").ToArray();
            Assert.That(responses.Length, Is.EqualTo(2));
            Assert.That((string)responses[0]["data"]["decision_kind"], Is.EqualTo("DIRECT_RESPONSE"));
            Assert.That(responses[0]["data"]["validated_goal"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)responses[1]["data"]["action_id"], Is.EqualTo("bring_object"));
            Assert.That((string)responses[1]["data"]["bindings"]["target"], Is.EqualTo("cup_01"));
            Assert.That((string)responses[1]["data"]["validated_goal"]["target_entity_id"], Is.EqualTo("cup_01"));

            Object.Destroy(source);
            yield return new ExitPlayMode();
            JObject[] completed = File.ReadAllLines(path).Select(JObject.Parse).ToArray();
            Assert.That(completed.Count(line => (string)line["kind"] == "session_started"), Is.EqualTo(1));
            Assert.That(completed.Count(line => (string)line["kind"] == "session_stopped"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator UnwritableTracePathWarnsWithoutAffectingAnotherReceiver()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            yield return new EnterPlayMode();
            string blockedParent = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cafe-trace-blocked-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blockedParent, "occupied");
            try
            {
                GameObject debug = new("BlockedTrace");
                debug.SetActive(false);
                RuntimeTraceRecorder blocked = debug.AddComponent<RuntimeTraceRecorder>();
                typeof(RuntimeTraceRecorder).GetField("outputDirectoryOverride",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(blocked, blockedParent);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "\\[RuntimeTrace\\] recorder stopped after"));
                debug.SetActive(true);
                Assert.That(blocked.CurrentTracePath, Is.Empty);

                AgentEventReceiver receiver = Object.FindObjectsByType<AgentEventReceiver>(FindObjectsSortMode.None)
                    .First(value => value != null && value.isActiveAndEnabled);
                var agentEvent = new AgentEvent("trace_fail_open_event", AgentEventTypes.PlayerUtterance,
                    "player_01", receiver.EntityId, string.Empty, new Dictionary<string, string>
                    { [AgentEventPayloadKeys.Text] = "你好" }, 1d);
                Assert.That(receiver.Receive(agentEvent), Is.True);
                Object.Destroy(debug);
            }
            finally { File.Delete(blockedParent); }
            yield return new ExitPlayMode();
        }

        private static string[] ReadLinesWhileWriting(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).ToArray();
        }

        private static CognitiveRequest Request() => new()
        {
            request_id = "request_trace_test", trace_id = "trace_test",
            run = new CognitiveRunDto { run_id = "run_test", agent_entity_id = "npc_01",
                trigger_id = "trigger_test", trigger_type = "event" },
            trigger = new CognitiveTriggerDto { trigger_id = "trigger_test", trigger_type = "event",
                agent_entity_id = "npc_01" },
            context = new CognitiveContextSnapshot
            {
                run_id = "run_test", agent_entity_id = "npc_01",
                perception = new PerceptionSnapshotDto { observer_entity_id = "npc_01", perception_revision = 4,
                    entities = new[] { new PerceivedEntityDto { entity_id = "cup_01", kind = "item",
                        display_name = "Cup", visibility = "current_visible",
                        semantic_tags = new[] { "cup" }, capabilities = new[] { "pickup" } } } },
                capabilities = new CognitiveCapabilityManifestDto { available_actions = new[]
                { new CognitiveActionCapabilityDto { action_id = "bring_object", description = "Bring object" } } },
            },
        };

        private static CognitiveResponse Response(bool action) => new()
        {
            request_id = "request_trace_test", trace_id = "trace_test", run_id = "run_test",
            agent_entity_id = "npc_01",
            decision = action ? new CognitiveDecision
            {
                decision_kind = CognitiveDecisionKinds.ActionProposal,
                action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                    bindings = new Dictionary<string, string> { ["target"] = "cup_01", ["destination"] = "player_01" } },
            } : new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = "你好" },
            validated_goal = action ? new GoalProposal { type = "bring_object", target_entity_id = "cup_01",
                destination_entity_id = "player_01" } : null,
        };
    }
}
