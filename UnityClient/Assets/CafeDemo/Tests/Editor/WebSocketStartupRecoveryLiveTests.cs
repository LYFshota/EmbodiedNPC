using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Opt-in full Cafe acceptance; a repository-external rig owns readiness and Backend lifetime.
    public sealed class WebSocketStartupRecoveryLiveTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [UnityTest]
        public IEnumerator CafeStartup_HttpReadinessRearmsExhaustedTransport()
        {
            string root = Path.Combine(Path.GetTempPath(), "embodiednpc-ws-startup");
            string selection = Path.Combine(root, "case.txt");
            if (!File.Exists(selection)) Assert.Ignore("Requires an isolated externally controlled Backend on port 8018.");
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var sceneBackend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var serialized = new SerializedObject(sceneBackend);
            Assert.That(serialized.FindProperty("maxConnectAttempts").intValue, Is.EqualTo(3));
            Assert.That(serialized.FindProperty("retryDelaySeconds").floatValue, Is.EqualTo(1f));
            Assert.That(serialized.FindProperty("connectTimeoutSeconds").floatValue, Is.EqualTo(5f));
            serialized.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:8018/ws";
            serialized.ApplyModifiedPropertiesWithoutUndo(); // In-memory fixture only; never save the scene.
            // Editor test-runner domain reload can stall asynchronous callbacks for several seconds.
            // Start both production owners together after that stall, with all policy defaults intact.
            sceneBackend.enabled = false;
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled = false;
            yield return new EnterPlayMode();
            yield return null;
            yield return null;
            // Construct observers/closures after domain reload, not in the pre-Play iterator.
            yield return ObserveStartup();
        }

        private IEnumerator ObserveStartup()
        {
            string root = Path.Combine(Path.GetTempPath(), "embodiednpc-ws-startup");
            string mode = File.ReadAllText(Path.Combine(root, "case.txt")).Trim();
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            backend.enabled = true;
            panel.enabled = true;
            yield return null;
            yield return null;
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            var input = Object.FindAnyObjectByType<TextUtteranceInput>(FindObjectsInactive.Include);
            Assert.That(backend, Is.Not.Null);
            Assert.That(panel, Is.Not.Null);
            Assert.That(dialogue, Is.Not.Null);
            Assert.That(input, Is.Not.Null);
            var inputSerialized = new SerializedObject(input);
            var field = (TMP_InputField)inputSerialized.FindProperty("inputField").objectReferenceValue;
            var send = (Button)inputSerialized.FindProperty("submitButton").objectReferenceValue;
            var resolvedField = typeof(CafeDemoDeveloperPanel).GetField("startupConfigResolved", Private);
            var taskField = typeof(BackendWebSocketClient).GetField("connectionTask", Private);
            object firstTask = taskField.GetValue(backend);
            object firstLease = typeof(BackendWebSocketClient).GetField("lifetime", Private).GetValue(backend);
            int connectedEvents = backend.ConnectionStatus == "CONNECTED" ? 1 : 0;
            backend.ConnectionStatusChanged += status => { if (status == "CONNECTED") connectedEvents++; };
            bool exhausted = false;
            bool offlineInput = false;
            string conversation = null;
            string previous = "";
            double deadline = Time.realtimeSinceStartupAsDouble + 25;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                bool resolved = (bool)resolvedField.GetValue(panel);
                string sample = "attempts=" + backend.ConnectionAttempts + " status=" + backend.ConnectionStatus +
                    " IsConnected=" + backend.IsConnected + " resolved=" + resolved + " setupReady=" + dialogue.IsSetupReady +
                    " submissionReady=" + input.IsSubmissionReady + " send=" + send.interactable;
                if (sample != previous)
                {
                    File.AppendAllText(Path.Combine(root, mode + "-samples.txt"), Time.realtimeSinceStartup.ToString("F3") + " " + sample + "\n");
                    previous = sample;
                }
                if (!exhausted && backend.ConnectionStatus == "OFFLINE" && backend.ConnectionAttempts == 3 &&
                    ((Task)taskField.GetValue(backend)).IsCompleted)
                {
                    exhausted = true;
                    if (mode == "race" || mode == "unconfigured")
                    {
                        Assert.That(resolved, Is.False, "The initial round must exhaust before the HTTP readiness deadline.");
                        File.WriteAllText(Path.Combine(root, mode + "-ready.flag"), sample);
                    }
                }
                if (mode == "race" && resolved && dialogue.IsSetupReady && !backend.IsConnected && !offlineInput)
                {
                    OpenDialogue(panel, dialogue);
                    field.text = "你好";
                    Assert.That(input.IsSubmissionReady, Is.False);
                    Assert.That(send.interactable, Is.False);
                    conversation = dialogue.CurrentConversationId;
                    offlineInput = true;
                    ScreenCapture.CaptureScreenshot(Path.Combine(root, "race-send-disabled.png"));
                }
                if (resolved && (mode == "offline" ? exhausted : backend.IsConnected && backend.ConnectionStatus == "CONNECTED")) break;
                yield return null;
            }
            Assert.That((bool)resolvedField.GetValue(panel), Is.True);
            if (mode == "offline")
            {
                Assert.That(exhausted, Is.True);
                Assert.That(backend.IsConnected, Is.False);
                Assert.That(dialogue.IsSetupReady, Is.False);
                Assert.That(input.IsSubmissionReady, Is.False);
                Assert.That(taskField.GetValue(backend), Is.SameAs(firstTask));
                Assert.That(typeof(BackendWebSocketClient).GetField("lifetime", Private).GetValue(backend), Is.SameAs(firstLease));
                double stable = Time.realtimeSinceStartupAsDouble + .75;
                while (Time.realtimeSinceStartupAsDouble < stable) yield return null;
                Assert.That(backend.ConnectionStatus, Is.EqualTo("OFFLINE"));
                Assert.That(backend.ConnectionAttempts, Is.EqualTo(3));
            }
            else
            {
                Assert.That(backend.IsConnected, Is.True);
                Assert.That(backend.ConnectionStatus, Is.EqualTo("CONNECTED"));
                Assert.That(connectedEvents, Is.EqualTo(1));
                Assert.That(backend.ConnectionAttempts, Is.EqualTo(1));
                if (mode == "already") Assert.That(taskField.GetValue(backend), Is.SameAs(firstTask));
                else Assert.That(exhausted, Is.True);
                if (mode == "unconfigured")
                {
                    Assert.That(dialogue.IsSetupReady, Is.False);
                    Assert.That(dialogue.HasActiveSession, Is.False);
                    Assert.That(panel.IsOpen, Is.True);
                }
                else
                {
                    Assert.That(dialogue.IsSetupReady, Is.True);
                    if (mode == "race")
                    {
                        Assert.That(offlineInput, Is.True);
                        Assert.That(input.CurrentText, Is.EqualTo("你好"));
                        Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
                        Assert.That(dialogue.LastLifecycleReason, Is.Not.EqualTo("transport_reconnected"));
                    }
                    else { OpenDialogue(panel, dialogue); field.text = "你好"; }
                    Assert.That(input.IsSubmissionReady, Is.True);
                    Assert.That(send.interactable, Is.True);
                    ScreenCapture.CaptureScreenshot(Path.Combine(root, mode + "-send-enabled.png"));
                    yield return null;
                    var orchestrator = dialogue.SelectedTarget.GetComponent<AgentOrchestrator>();
                    double quiet = Time.realtimeSinceStartupAsDouble + 5;
                    while (orchestrator.HasActiveRun && Time.realtimeSinceStartupAsDouble < quiet) yield return null;
                    Assert.That(orchestrator.HasActiveRun, Is.False);
                    int accepted = 0;
                    dialogue.DialogueSubmissionAccepted += utterance => accepted++;
                    int sentBefore = backend.CognitiveRequestSendCount;
                    send.onClick.Invoke();
                    double sentDeadline = Time.realtimeSinceStartupAsDouble + 3;
                    while (backend.CognitiveRequestSendCount == sentBefore && Time.realtimeSinceStartupAsDouble < sentDeadline) yield return null;
                    Assert.That(accepted, Is.EqualTo(1));
                    Assert.That(backend.CognitiveRequestSendCount, Is.EqualTo(sentBefore + 1));
                    File.WriteAllText(Path.Combine(root, mode + "-submitted.txt"), "accepted=1 cognitiveRequestSent=1 textPreserved=true");
                    if (mode == "already")
                    {
                        // An open dialogue recovers automatically while keeping runtime loss semantics.
                        field.text = "断线后保留";
                        string beforeLoss = dialogue.CurrentConversationId;
                        Task disconnect = backend.DisconnectAsync();
                        double lossDeadline = Time.realtimeSinceStartupAsDouble + 5;
                        while (!disconnect.IsCompleted && Time.realtimeSinceStartupAsDouble < lossDeadline) yield return null;
                        Assert.That(disconnect.IsCompleted, Is.True);
                        Assert.That(disconnect.IsFaulted, Is.False);
                        Assert.That(dialogue.HasActiveSession, Is.True);
                        Assert.That(dialogue.CurrentConversationId, Is.Not.EqualTo(beforeLoss));
                        string rotated = dialogue.CurrentConversationId;
                        Assert.That(input.CurrentText, Is.EqualTo("断线后保留"));
                        double reconnectDeadline = Time.realtimeSinceStartupAsDouble + 7;
                        while ((!backend.IsConnected || !send.interactable) && Time.realtimeSinceStartupAsDouble < reconnectDeadline) yield return null;
                        Assert.That(backend.IsConnected, Is.True);
                        Assert.That(connectedEvents, Is.EqualTo(2));
                        Assert.That(dialogue.CurrentConversationId, Is.EqualTo(rotated));
                        Assert.That(dialogue.LastLifecycleReason, Is.EqualTo("transport_reconnected"));
                        Assert.That(input.CurrentText, Is.EqualTo("断线后保留"));
                        Assert.That(send.interactable, Is.True);
                        File.WriteAllText(Path.Combine(root, "runtime-loss-regression.txt"), "rotation=once automaticRearm=true textPreserved=true");
                    }
                }
            }
            yield return null;
            yield return new ExitPlayMode();
        }

        private static void OpenDialogue(CafeDemoDeveloperPanel panel, DialogueController dialogue)
        {
            panel.Close();
            var player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            player.Controller.enabled = false;
            player.transform.position = new Vector3(-.8f, .02f, 1.25f);
            player.Controller.enabled = true;
            Physics.SyncTransforms();
            var merchant = Object.FindObjectsByType<DialogueParticipant>(FindObjectsSortMode.None)
                .Single(p => p.Entity.EntityId == "npc_cafe_01");
            Assert.That(dialogue.SelectTarget(merchant), Is.True);
        }

        [UnityTearDown]
        public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
