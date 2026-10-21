using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Exactly the three P0 scenarios. Fixture service owns no Backend/DB/provider resources.
    public sealed class TitleSceneP0Tests
    {
        private const string Session = "TitleP0.Tests.";
        private static string Evidence => TestContext.CurrentContext.Test.MethodName.StartsWith("P2_")
            ? "D:/EmbodiedNPC-PlayerExperienceP2-20261017/evidence" : Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/TitleP0"));
        private Keyboard keyboard;
        private static void Mode(string value) => File.WriteAllText(Path.Combine(Evidence, "mode.txt"), value);
        private static void Field(Object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        private static int Count<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsSortMode.None).Length;
        private static Button Button(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(x => x.name == name);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!File.Exists(Path.Combine(Evidence, "fake-ready.txt"))) Assert.Ignore("Start TitleP0FakeService.py on loopback 18846 first.");
            Assert.IsFalse(EditorApplication.isPlaying,"Preserve an existing user Play");
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty,"Preserve unsaved scene work");
            SessionState.SetString(Session + "scene", SceneManager.GetActiveScene().path);
            SessionState.SetBool(Session + "hadVolume", PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetFloat(Session + "volume", PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session + "mute", EditorUtility.audioMasterMute);
            EditorUtility.audioMasterMute = false;
            Mode("ready");
            EditorSceneManager.OpenScene("Assets/Scenes/TitleScene.unity");
            PatchTransport(Find<BackendWebSocketClient>());
            yield return new EnterPlayMode();
            SceneManager.sceneLoaded += PatchCafe;
            keyboard = InputSystem.AddDevice<Keyboard>();
            yield return WaitFor(() => Find<TitleScreenController>()?.CanEnter == true, 12);
        }
        private static void PatchTransport(BackendWebSocketClient backend)
        {
            Field(backend, "serverUrl", "ws://127.0.0.1:18846/ws");
        }
        private static void PatchCafe(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "CafeDemoV2") return;
            PatchTransport(Find<BackendWebSocketClient>()); // sceneLoaded occurs before scene Start/network admission.
            foreach (var recorder in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None)) recorder.enabled = false;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            SceneManager.sceneLoaded -= PatchCafe;
            if (keyboard != null) InputSystem.RemoveDevice(keyboard);
            foreach (var transport in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsSortMode.None))
            {
                var close = transport.DisconnectAsync();
                float until = Time.realtimeSinceStartup + 5;
                while (!close.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (SessionState.GetBool(Session + "hadVolume", false)) PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey, SessionState.GetFloat(Session + "volume", .5f));
            else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            PlayerPrefs.Save();
            EditorUtility.audioMasterMute = SessionState.GetBool(Session + "mute", false);
            Mode("ready");
            var previous = SessionState.GetString(Session + "scene", "Assets/Scenes/CafeDemoV2.unity");
            yield return null; // Allow editor-side native handles to settle after ExitPlayMode's domain reload.
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous) ? "Assets/Scenes/CafeDemoV2.unity" : previous);
        }

        [UnityTest]
        public IEnumerator P2_TitleDiagnosticsHiddenByDefault_SharedExportAndOfflineRecovery()
        {
            var title=Find<TitleScreenController>();
            var progress=(StartupProgressDisplay)typeof(TitleScreenController).GetField("startupProgress",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(title);
            Assert.IsFalse(progress.Root.activeSelf); Assert.IsTrue(title.CanEnter);
            Button("DiagnosticsDetails").onClick.Invoke(); Assert.IsTrue(progress.Root.activeSelf);
            Button("DiagnosticsDetails").onClick.Invoke(); Assert.IsFalse(progress.Root.activeSelf);
            Assert.IsTrue(Button("TitleExportDiagnostics").interactable);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"p2-title-1920.png")); yield return new WaitForSecondsRealtime(.2f);
            title.OpenSettings(); yield return null; yield return null;
            var host=Find<TitleSettingsHost>(); yield return WaitFor(()=>!host.IsBusy,5);
            Assert.IsTrue(host.Navigation.GeneralPage.GetComponentsInChildren<Button>(true).Any(b=>b.name=="ExportDiagnostics"));
            Assert.IsTrue(host.TryClose());
            Mode("httpfail"); title.Retry(); yield return WaitFor(()=>title.State==TitleStartupState.Unavailable,12);
            Assert.IsFalse(title.CanEnter); Assert.IsFalse(progress.Root.activeSelf);
            Assert.IsTrue(Button("TitleExportDiagnostics").interactable,"Export cannot depend on Backend readiness.");
            Mode("ready"); title.Retry(); yield return WaitFor(()=>title.CanEnter,12);
            Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
        }

        [UnityTest]
        public IEnumerator T1_TitleLifecycle_NoCafeWorld_OneAudioAndUi()
        {
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
            Assert.That(Count<WorldStateService>(), Is.Zero);
            Assert.That(Count<CognitiveRunDriver>(), Is.Zero);
            Assert.That(Count<GoalPlanRunner>(), Is.Zero);
            Assert.That(Count<PerceivedStateStore>(), Is.Zero);
            Assert.That(Count<CharacterController>(), Is.Zero);
            Assert.That(Count<UnityEngine.AI.NavMeshAgent>(), Is.Zero);
            Assert.That(Count<EventSystem>(), Is.EqualTo(1));
            Assert.That(Count<AudioListener>(), Is.EqualTo(1));
            Assert.That(Count<CafeBackgroundMusic>(), Is.EqualTo(1));
            Assert.That(Find<CafeBackgroundMusic>().GetComponent<AudioSource>().isPlaying, Is.True);
            Assert.That(Find<BackendWebSocketClient>().PendingExperienceCount, Is.Zero);
            Assert.That(Find<BackendWebSocketClient>().CognitiveRequestSendCount, Is.Zero);
            int before = (int)JObject.Parse(File.ReadAllText(Path.Combine(Evidence,"stats.json")))["messages"];
            Button("Instructions").onClick.Invoke();
            Assert.That(GameObject.Find("InstructionsModal"), Is.Not.Null);
            yield return Escape();
            Assert.That(GameObject.Find("MainMenu"), Is.Not.Null);
            Find<TitleScreenController>().OpenSettings();
            yield return WaitFor(() => !Find<TitleSettingsHost>().IsBusy, 5);
            Find<TitleSettingsHost>().Navigation.ShowAudio();
            var volume = Find<TitleSettingsHost>().Navigation.AudioPage.GetComponentInChildren<Slider>();
            volume.value = .31f;
            Assert.That(Find<CafeBackgroundMusic>().Volume, Is.EqualTo(.31f).Within(.001f));
            yield return Escape();
            Assert.That(Find<TitleSettingsHost>().IsOpen, Is.False);
            Assert.That(PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey), Is.EqualTo(.31f).Within(.001f));
            Assert.That((int)JObject.Parse(File.ReadAllText(Path.Combine(Evidence,"stats.json")))["messages"], Is.EqualTo(before));
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"T1-title.png"));
            yield return new WaitForSecondsRealtime(.2f);
        }

        [UnityTest]
        public IEnumerator T2_RealClientFakeResponses_AdmissionAndOriginalForm()
        {
            var title = Find<TitleScreenController>();
            Assert.That(title.State, Is.EqualTo(TitleStartupState.Ready));
            title.OpenSettings(); yield return null; yield return null;
            var form = Find<FirstRunModelSetup>();
            yield return WaitFor(() => !Find<TitleSettingsHost>().IsBusy, 5);
            title.Retry(); yield return WaitFor(() => title.State == TitleStartupState.Ready, 12);
            Assert.That(Find<TitleSettingsHost>().IsOpen, Is.True, "A background recheck must not close manually opened Settings.");
            title.EnterCafe(); Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("TitleScene"), "Modal Settings owns input until closed.");
            var command = (JObject)typeof(FirstRunModelSetup).GetMethod("BuildCombinedProfileCommand", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
            Assert.That((int)command["expected_revision"], Is.EqualTo(7));
            Assert.That((string)command["main"]["key_action"], Is.EqualTo("retain"));
            Assert.That((bool)command["embedding_enabled"], Is.False);
            Assert.That(command["main"]["kind"].ToString(), Is.EqualTo("main"));
            // Exercise the real form's activation signal, then the Title host's wait/recheck/return flow.
            form.ApplyRuntimeStatus(new RuntimeStatusView { model_config = new ModelConfigStatus { configured = true },
                active_provider = new ActiveProviderStatus { available = true } }, false);
            yield return WaitFor(() => !Find<TitleSettingsHost>().IsOpen && title.CanEnter, 12);
            foreach (var sample in new[] { ("unconfigured",TitleStartupState.Unconfigured), ("pending",TitleStartupState.PendingApplication), ("providerfail",TitleStartupState.Unavailable), ("httpfail",TitleStartupState.Unavailable) })
            {
                Mode(sample.Item1); title.Retry();
                yield return WaitFor(() => title.State != TitleStartupState.Checking && title.State != TitleStartupState.Connecting, 12);
                Assert.That(title.State, Is.EqualTo(sample.Item2), sample.Item1);
                Assert.That(title.CanEnter, Is.False, sample.Item1);
                title.EnterCafe(); yield return null;
                Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("TitleScene"));
                if (sample.Item1 == "unconfigured")
                {
                    Assert.That(Find<TitleSettingsHost>().IsOpen, Is.True);
                    yield return WaitFor(() => !Find<TitleSettingsHost>().IsBusy, 5);
                    yield return Escape();
                }
            }
            Mode("ready"); title.Retry(); yield return WaitFor(() => title.CanEnter, 12);
            var disconnect = Find<BackendWebSocketClient>().DisconnectAsync();
            while (!disconnect.IsCompleted) yield return null;
            Assert.That(title.CanEnter, Is.False, "HTTP/model success must not admit an offline WS.");
            Assert.That(title.State, Is.EqualTo(TitleStartupState.Unavailable));
            title.EnterCafe(); Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("TitleScene"));
        }

        [UnityTest]
        public IEnumerator T3_EnterCafeOnce_NewConnectionAndExistingInput()
        {
            var title = Find<TitleScreenController>();
            var initialBackend = Find<BackendWebSocketClient>();
            Button("EnterCafe").onClick.Invoke(); Button("EnterCafe").onClick.Invoke();
            yield return WaitFor(() => SceneManager.GetActiveScene().name == "CafeDemoV2", 25);
            yield return WaitFor(() => Find<DialogueController>().IsSetupReady, 12);
            Assert.That(Count<TitleScreenController>(), Is.Zero);
            Assert.That(Count<TitleSettingsHost>(), Is.Zero);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
            Assert.That(Count<EventSystem>(), Is.EqualTo(1));
            Assert.That(Count<AudioListener>(), Is.EqualTo(1));
            Assert.That(Count<BackendWebSocketClient>(), Is.EqualTo(1));
            Assert.That(ReferenceEquals(Find<BackendWebSocketClient>(), initialBackend), Is.False);
            Assert.That(initialBackend == null, Is.True, "The Title-owned client must have been destroyed.");
            Assert.That(Find<BackendWebSocketClient>().IsConnected, Is.True);
            Assert.That(Count<CafeBackgroundMusic>(), Is.EqualTo(1));
            Assert.That(Find<CafeBackgroundMusic>().GetComponent<AudioSource>().isPlaying, Is.True);
            Assert.That(Find<CafeDemoDeveloperPanel>().IsOpen, Is.False);
            Assert.That(Find<CafeDemoPlayerController>().IsInputSuspended, Is.False);
            var player = Find<CafeDemoPlayerController>();
            Vector3 before = player.transform.position;
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.W)); InputSystem.Update();
            yield return new WaitForSecondsRealtime(.4f);
            InputSystem.QueueStateEvent(keyboard,new KeyboardState()); InputSystem.Update();
            Assert.That(Vector3.Distance(before,player.transform.position), Is.GreaterThan(.03f));
            // The spawn is deliberately outside dialogue range. Stage the existing proximity interaction
            // after independently checking real WASD movement; never change production scene positions.
            var npc = Object.FindObjectsByType<DialogueParticipant>(FindObjectsSortMode.None)
                .First(x => x.Entity.EntityId == "npc_cafe_regular_01");
            var body = player.GetComponent<CharacterController>(); body.enabled = false;
            player.transform.position = npc.transform.position + new Vector3(0, 0, -1.2f);
            body.enabled = true;
            Field(Find<CafeDemoOrbitCamera>(), "yaw", 0f);
            Field(Find<CafeDemoOrbitCamera>(), "pitch", 12f);
            yield return null; yield return null;
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            Assert.That(interactor.TryBeginDialogue(), Is.True, "Existing E interaction candidate must remain available.");
            yield return null;
            var input = Find<TextUtteranceInput>();
            Assert.That(input.IsSubmissionReady, Is.True);
            var field = Find<DialogueController>().DialogueInputField;
            field.text = "未发送的测试草稿";
            var settings = Find<CafeDemoDeveloperPanel>(); settings.Open(); yield return null; settings.Close(); yield return null;
            Assert.That(field.text, Is.EqualTo("未发送的测试草稿"));
            Assert.That(input.IsInputFocused, Is.True);
            Assert.That((int)JObject.Parse(File.ReadAllText(Path.Combine(Evidence,"stats.json")))["max_ws"], Is.EqualTo(1));
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"T3-cafe.png"));
            yield return new WaitForSecondsRealtime(.2f);
        }
        private IEnumerator Escape()
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Escape)); InputSystem.Update();
            // EditMode EnterPlayMode tests inject after the normal player Update (existing input-test convention).
            var title = Find<TitleScreenController>();
            if (title != null) title.SendMessage("Update");
            yield return null;
            InputSystem.QueueStateEvent(keyboard,new KeyboardState()); InputSystem.Update(); yield return null;
        }
        private static IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(condition(), Is.True, "P0 timeout after " + seconds + " seconds.");
        }
    }
}
