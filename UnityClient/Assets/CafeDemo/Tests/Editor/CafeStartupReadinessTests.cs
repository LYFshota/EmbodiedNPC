using System;
using System.Collections;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Run the actual startup owner with a deterministic clock and HTTP completion seam.
    public sealed class CafeStartupReadinessTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private CafeDemoDeveloperPanel panel;
        private DialogueController dialogue;
        private FirstRunModelSetup form;
        private float now;
        private int requests, active, maxActive, disposed;
        private bool previousEnglish;
        private System.Threading.Tasks.TaskCompletionSource<bool> transportGate;

        [SetUp]
        public void SetUp()
        {
            now = 0;
            requests = active = maxActive = disposed = 0;
            previousEnglish = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(false);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            // EditMode doesn't invoke OnEnable; reproduce its dynamic knowledge-page preparation.
            panel.EnsureKnowledgePage();
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            form = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            // Policy tests never open a real transport; the active-task guard is exercised separately.
            transportGate = new System.Threading.Tasks.TaskCompletionSource<bool>();
            typeof(BackendWebSocketClient).GetField("connectionTask", Private).SetValue(
                Object.FindAnyObjectByType<BackendWebSocketClient>(), transportGate.Task);
            Set("startupClock", (Func<float>)(() => now));
            Set("startupTransportReady", (Func<bool>)(() => true));
            dialogue.SetSetupReady(false);
            form.ShowStartupConnecting();
        }

        [TearDown]
        public void TearDown()
        {
            transportGate.TrySetResult(true);
            SettingsUiLanguage.SetEnglish(previousEnglish);
        }

        private object Get(string name) => typeof(CafeDemoDeveloperPanel).GetField(name, Private).GetValue(panel);
        private void Set(string name, object value) => typeof(CafeDemoDeveloperPanel).GetField(name, Private).SetValue(panel, value);
        private void Call(string name, params object[] args) => typeof(CafeDemoDeveloperPanel).GetMethod(name, Private).Invoke(panel, args);
        private IEnumerator Owner() => (IEnumerator)typeof(CafeDemoDeveloperPanel).GetMethod("ResolveStartupConfig", Private).Invoke(panel, null);
        private string Status => ((TMP_Text)new SerializedObject(form).FindProperty("statusText").objectReferenceValue).text;
        private void Connected() => Call("OnStartupConnectionStatusChanged", "CONNECTED");

        private IEnumerator Response(Action<ModelConfigView, string> done, ModelConfigView config, string error, bool delayed = false)
        {
            requests++; active++; maxActive = Math.Max(active, maxActive);
            try
            {
                if (delayed) yield return null;
                done(config, error);
            }
            finally { active--; disposed++; }
        }

        private void Use(Func<int, string> error, bool configured = true, bool delayed = false)
        {
            Set("startupGetConfig", (Func<Action<ModelConfigView, string>, int, IEnumerator>)((done, timeout) =>
            {
                Assert.That(timeout, Is.InRange(1, 6));
                string code = error(requests);
                return Response(done, code == null ? new ModelConfigView { configured = configured } : null, code, delayed);
            }));
        }

        private void Finish(IEnumerator owner)
        {
            for (int i = 0; i < 100 && owner.MoveNext(); i++) now += .1f;
            Assert.That(Get("startupConfigResolved"), Is.True);
            Assert.That(maxActive, Is.LessThanOrEqualTo(1));
            Assert.That(active, Is.Zero);
        }

        [TestCase("BACKEND_UNAVAILABLE")]
        [TestCase("TIMEOUT")]
        public void TransientFirstFailure_RemainsPendingThenSucceedsOnce(string error)
        {
            Use(n => n == 0 ? error : null);
            var owner = Owner();
            Assert.That(owner.MoveNext(), Is.True);
            Assert.That(Get("startupConfigResolved"), Is.False);
            Assert.That(dialogue.IsSetupReady, Is.False);
            Assert.That(Status, Does.Contain("正在连接"));
            Finish(owner);
            Assert.That(requests, Is.EqualTo(2));
            Assert.That(dialogue.IsSetupReady, Is.True);
            Assert.That(Get("startupGeneration"), Is.EqualTo(1), "One finalization only.");
        }

        [Test]
        public void ConfiguredButTransportPending_KeepsAdmissionUntilConnected()
        {
            bool connected = false;
            Set("startupTransportReady", (Func<bool>)(() => connected));
            Call("HoldStartupAdmission");
            Use(n => null);
            Finish(Owner());
            Assert.That(dialogue.IsSetupReady, Is.False);
            Assert.That(Object.FindAnyObjectByType<CafeDemoPlayerController>().IsInputSuspended, Is.True);
            connected = true;
            Connected();
            Assert.That(dialogue.IsSetupReady, Is.True);
            Assert.That(Object.FindAnyObjectByType<CafeDemoPlayerController>().IsInputSuspended, Is.False);
            Assert.That(panel.IsOpen, Is.False, "Healthy startup must not open the model page.");
        }

        [Test]
        public void TransientExhaustion_EndsAtSixSecondsAndShowsFailure()
        {
            Use(n => "BACKEND_UNAVAILABLE");
            Finish(Owner());
            Assert.That(now, Is.InRange(6f, 6.2f));
            Assert.That(requests, Is.InRange(10, 12));
            Assert.That(dialogue.IsSetupReady, Is.False);
            Assert.That(Status, Does.Contain("无法连接"));
            Connected();
            Assert.That(Get("startupRetryWake"), Is.False);
            Assert.That(Get("startupGeneration"), Is.EqualTo(1));
        }

        [Test]
        public void ConfiguredFalse_ResolvesWithoutRetryOrUnavailableUi()
        {
            Use(n => null, false);
            Finish(Owner());
            Assert.That(requests, Is.EqualTo(1));
            Assert.That(now, Is.Zero);
            Assert.That(dialogue.IsSetupReady, Is.False);
            Assert.That(Status, Does.Contain("没有已保存"));
        }

        [TestCase("HTTP_ERROR")]
        [TestCase("INVALID_RESPONSE")]
        [TestCase("MODEL_CONFIG_READ_FAILED")]
        public void PermanentError_DoesNotRetry(string error)
        {
            Use(n => error);
            Finish(Owner());
            Assert.That(requests, Is.EqualTo(1));
            Assert.That(dialogue.IsSetupReady, Is.False);
        }

        [Test]
        public void ConnectedStorm_WakesOneOwnerBeforeIntervalAndNeverReloadsAfterSuccess()
        {
            Use(n => n == 0 ? "BACKEND_UNAVAILABLE" : null, delayed: true);
            var owner = Owner();
            Assert.That(owner.MoveNext(), Is.True); // First request in flight.
            Connected(); Connected();
            Assert.That(Get("startupRetryWake"), Is.False);
            Assert.That(owner.MoveNext(), Is.True); // Transient response, scheduled wait.
            Connected(); Connected(); Connected();
            Assert.That(owner.MoveNext(), Is.True); // Immediate retry, no fake time elapsed.
            Assert.That(requests, Is.EqualTo(2));
            Assert.That(now, Is.Zero);
            Connected(); Connected();
            Finish(owner);
            var model = (TMP_InputField)new SerializedObject(form).FindProperty("mainModel").objectReferenceValue;
            model.text = "user-unsaved-edit";
            Connected(); Connected();
            Assert.That(Get("startupRetryWake"), Is.False);
            Assert.That(requests, Is.EqualTo(2));
            Assert.That(model.text, Is.EqualTo("user-unsaved-edit"));
            Assert.That(Get("startupGeneration"), Is.EqualTo(1));
        }

        [Test]
        public void DisableDuringWait_InvalidatesOldOwnerAndReenableSubscribesOnce()
        {
            Call("OnEnable");
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var eventField = typeof(BackendWebSocketClient).GetField("ConnectionStatusChanged", Private);
            Func<int> subscriptions = () => Array.FindAll(
                ((Delegate)eventField.GetValue(backend))?.GetInvocationList() ?? new Delegate[0],
                x => x.Target == panel).Length;
            Assert.That(subscriptions(), Is.EqualTo(1));
            Use(n => "BACKEND_UNAVAILABLE");
            var owner = Owner();
            Assert.That(owner.MoveNext(), Is.True);
            Call("OnDisable");
            Assert.That(subscriptions(), Is.Zero);
            Assert.That(owner.MoveNext(), Is.False);
            Assert.That(Get("startupConfigResolved"), Is.False);
            Assert.That(dialogue.IsSetupReady, Is.False);
            now = 1;
            Call("OnEnable");
            Assert.That(subscriptions(), Is.EqualTo(1));
            Use(n => null);
            Finish(Owner());
            Assert.That(dialogue.IsSetupReady, Is.True);
            Call("OnDisable");
            Assert.That(subscriptions(), Is.Zero);
        }

        [Test]
        public void DisableInFlight_DisposesHttpAndRejectsLateCallback()
        {
            Action<ModelConfigView, string> late = null;
            Set("startupGetConfig", (Func<Action<ModelConfigView, string>, int, IEnumerator>)((done, timeout) =>
            { late = done; return Response(done, new ModelConfigView { configured = true }, null, true); }));
            var owner = Owner();
            Assert.That(owner.MoveNext(), Is.True);
            Call("OnDisable");
            Assert.That(disposed, Is.EqualTo(1));
            late(new ModelConfigView { configured = true }, null);
            Assert.That(owner.MoveNext(), Is.False);
            Assert.That(Get("startupConfigResolved"), Is.False);
            Assert.That(dialogue.IsSetupReady, Is.False);
        }

        [Test]
        public void SlowHttp_IsDisposedAtOuterDeadline()
        {
            Use(n => null, delayed: true);
            var owner = Owner();
            Assert.That(owner.MoveNext(), Is.True);
            now = 6;
            Assert.That(owner.MoveNext(), Is.False);
            Assert.That(disposed, Is.EqualTo(1));
            Assert.That(Get("startupConfigResolved"), Is.True);
            Assert.That(dialogue.IsSetupReady, Is.False);
            Assert.That(Status, Does.Contain("连接检查超时"));
        }

        [TestCase("BACKEND_UNAVAILABLE")]
        [TestCase("TIMEOUT")]
        [TestCase("HTTP_ERROR")]
        [TestCase("INVALID_RESPONSE")]
        [TestCase("MODEL_CONFIG_READ_FAILED")]
        public void HttpFailure_NeverRearmsAnExhaustedTransport(string error)
        {
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            typeof(BackendWebSocketClient).GetField("connectionTask", Private).SetValue(backend,
                System.Threading.Tasks.Task.CompletedTask);
            Set("startupBackend", backend);
            Use(n => error);
            Finish(Owner());
            Assert.That(backend.ConnectionAttempts, Is.Zero);
            Assert.That(typeof(BackendWebSocketClient).GetField("lifetime", Private).GetValue(backend), Is.Null);
        }
    }

    // Opt-in acceptance against a separately controlled local Backend. Never owns its process.
    public sealed class CafeStartupReadinessLiveTests
    {
        [UnityTest]
        public IEnumerator CafeStartup_RealBackendReadinessAndTransport()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embodiednpc-startup-readiness");
            string selection = System.IO.Path.Combine(root, "live-case.txt");
            if (!System.IO.File.Exists(selection)) Assert.Ignore("Requires an externally controlled Backend acceptance environment.");
            string mode = System.IO.File.ReadAllText(selection).Trim();
            Assert.That(mode, Is.EqualTo("race").Or.EqualTo("offline").Or.EqualTo("already"));
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            yield return new EnterPlayMode();
            // EnterPlayMode reloads the domain; reconstruct external selection afterwards.
            root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embodiednpc-startup-readiness");
            mode = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "live-case.txt")).Trim();
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var form = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var resolvedField = typeof(CafeDemoDeveloperPanel).GetField("startupConfigResolved", flags);
            var transientField = typeof(CafeDemoDeveloperPanel).GetField("startupTransientFailure", flags);
            var statusField = typeof(FirstRunModelSetup).GetField("lastStatusChinese", flags);
            bool pendingObserved = false;
            float began = Time.realtimeSinceStartup;
            float deadline = began + 10f;
            string previous = "";
            while (Time.realtimeSinceStartup < deadline)
            {
                bool resolved = (bool)resolvedField.GetValue(panel);
                bool transient = (bool)transientField.GetValue(panel);
                string status = (string)statusField.GetValue(form);
                string sample = "resolved=" + resolved + " setup=" + dialogue.IsSetupReady +
                    " transient=" + transient + " ws=" + backend.ConnectionStatus +
                    " connected=" + backend.IsConnected + " panel=" + panel.IsOpen + " status=" + status;
                if (sample != previous)
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(root, mode + "-live-samples.txt"),
                        Time.realtimeSinceStartup.ToString("F3") + " " + sample + "\n");
                    previous = sample;
                }
                if (transient && !pendingObserved)
                {
                    pendingObserved = true;
                    Assert.That(resolved, Is.False);
                    Assert.That(dialogue.IsSetupReady, Is.False);
                    Assert.That(status, Does.Contain("正在连接"));
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(root, mode + "-live-pending.png"));
                    if (mode == "race") System.IO.File.WriteAllText(System.IO.Path.Combine(root, "race-release"), sample);
                }
                if (resolved && (mode == "offline" || (backend.IsConnected && backend.ConnectionStatus == "CONNECTED"))) break;
                yield return null;
            }
            Assert.That((bool)resolvedField.GetValue(panel), Is.True);
            Assert.That(panel.IsOpen, Is.True, "Startup keeps the Model/API page open.");
            Assert.That(new SerializedObject(panel).FindProperty("modelPage").objectReferenceValue as GameObject,
                Is.Not.Null);
            var modelPage = (GameObject)new SerializedObject(panel).FindProperty("modelPage").objectReferenceValue;
            Assert.That(modelPage.activeSelf, Is.True);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(root, mode + "-live-model.png"));
            if (mode == "offline")
            {
                Assert.That(pendingObserved, Is.True);
                Assert.That(dialogue.IsSetupReady, Is.False);
                Assert.That((string)statusField.GetValue(form), Does.Contain("无法连接"));
                float ownerStarted = (float)typeof(CafeDemoDeveloperPanel).GetField("startupConfigStartedAt", flags).GetValue(panel);
                float elapsed = Time.realtimeSinceStartup - ownerStarted;
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "offline-live-duration.txt"), elapsed.ToString("F3"));
                Assert.That(elapsed, Is.InRange(5f, 8f), "Measure the owner's deadline, not test-runner domain reload latency.");
                var generation = typeof(CafeDemoDeveloperPanel).GetField("startupGeneration", flags);
                int finalized = (int)generation.GetValue(panel);
                float stableUntil = Time.realtimeSinceStartup + .6f;
                while (Time.realtimeSinceStartup < stableUntil) yield return null;
                Assert.That((int)generation.GetValue(panel), Is.EqualTo(finalized), "No repeated finalization after exhaustion.");
            }
            else
            {
                if (mode == "race") Assert.That(pendingObserved, Is.True);
                Assert.That(dialogue.IsSetupReady, Is.True);
                Assert.That(backend.IsConnected, Is.True);
                Assert.That(backend.ConnectionStatus, Is.EqualTo("CONNECTED"));
                yield return null;
                panel.Close();
                var player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
                player.Controller.enabled = false;
                player.transform.position = new Vector3(-.8f, .02f, 1.25f);
                player.Controller.enabled = true;
                Physics.SyncTransforms();
                yield return null;
                Assert.That(player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>().TryBeginDialogue(), Is.True);
                Assert.That(dialogue.HasActiveSession, Is.True);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(root, mode + "-live-dialogue.png"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, mode + "-live-dialogue.txt"),
                    "setup=true IsConnected=true ConnectionStatus=CONNECTED dialogue=true");
            }
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
    }
}
