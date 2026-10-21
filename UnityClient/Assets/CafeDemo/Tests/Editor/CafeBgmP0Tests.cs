using System.Collections;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Only these three offline checks run; no Backend service or real inference is required.
    public sealed class CafeBgmP0Tests
    {
        private const string ScenePath = "Assets/Scenes/CafeDemoV2.unity";
        private const string Session = "CafeBgmP0Tests.";
        private CafeBackgroundMusic music;
        private AudioSource source;
        private CafeDemoDeveloperPanel settings;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);
            Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, Is.False);
            SessionState.SetBool(Session + "hadVolume", PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetFloat(Session + "volume", PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session + "muted", EditorUtility.audioMasterMute);
            string previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            SessionState.SetString(Session + "scene", string.IsNullOrEmpty(previousScene) ? ScenePath : previousScene);
            PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            EditorUtility.audioMasterMute = false;
            PrepareOfflineScene();
            yield return new EnterPlayMode();
            BindRuntime();
            yield return Wait(.5f);
        }

        private static void PrepareOfflineScene()
        {
            EditorSceneManager.OpenScene(ScenePath);
            foreach (var backend in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsSortMode.None))
            {
                var fields = new SerializedObject(backend);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                fields.ApplyModifiedPropertiesWithoutUndo();
                backend.enabled = false;
            }
            foreach (var recorder in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None))
                recorder.enabled = false;
            // Deliberately not saved: port 8000 and production scene configuration are never exercised.
        }

        private void BindRuntime()
        {
            music = Object.FindAnyObjectByType<CafeBackgroundMusic>();
            source = music.GetComponent<AudioSource>();
            settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.ApplyStartupConfig(new ModelConfigView { configured = false });
            settings.ShowOverview();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var device in InputSystem.devices.Where(d => d.name.StartsWith("CafeBgmTest")).ToArray())
                InputSystem.RemoveDevice(device);
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (SessionState.GetBool(Session + "hadVolume", false))
                PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey, SessionState.GetFloat(Session + "volume", .5f));
            else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            PlayerPrefs.Save();
            EditorUtility.audioMasterMute = SessionState.GetBool(Session + "muted", false);
            EditorSceneManager.OpenScene(SessionState.GetString(Session + "scene", ScenePath));
            SessionState.EraseBool(Session + "hadVolume");
            SessionState.EraseFloat(Session + "volume");
            SessionState.EraseBool(Session + "muted");
            SessionState.EraseString(Session + "scene");
        }

        [UnityTest]
        public IEnumerator T1_Playback2DAndOneLoopBoundary()
        {
            Assert.That(source.clip.name, Is.EqualTo("little_cafe"));
            Assert.That(source.clip.length, Is.InRange(150, 160));
            Assert.That(source.clip.channels, Is.EqualTo(2));
            Assert.That(source.clip.loadType, Is.EqualTo(AudioClipLoadType.Streaming));
            Assert.That(source.isPlaying, Is.True);
            Assert.That(source.volume, Is.EqualTo(.5f));
            Assert.That(source.loop, Is.True);
            Assert.That(source.playOnAwake, Is.False);
            Assert.That(source.spatialBlend, Is.Zero);
            Assert.That(source.spatialize, Is.False);
            Assert.That(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            var player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            var character = player.GetComponent<CharacterController>();
            character.enabled = false;
            player.transform.position += new Vector3(1, 0, 1);
            character.enabled = true;
            Camera.main.transform.rotation *= Quaternion.Euler(0, 80, 0);
            yield return Wait(.3f);
            Assert.That(source.volume, Is.EqualTo(.5f));
            var samples = new float[1024];
            // Readback needs a DSP buffer to fill; seek past any authored opening silence/fade.
            source.time = 10;
            source.GetOutputData(samples, 0);
            yield return Wait(.4f);
            source.GetOutputData(samples, 0);
            Assert.That(samples.Any(s => Mathf.Abs(s) > .00001f), Is.True, "Decoded source output must contain audio.");
            source.time = source.clip.length - .6f;
            yield return Wait(1.2f);
            Assert.That(source.isPlaying, Is.True);
            Assert.That(source.time, Is.InRange(0, 3), "Playback must wrap across the actual clip boundary.");
            yield return Wait(.4f);
            source.GetOutputData(samples, 0);
            Assert.That(samples.Any(s => Mathf.Abs(s) > .00001f), Is.True, "Output must resume after the seam.");
            Debug.Log("[CafeBgm P0] T1: Streaming stereo 44100Hz, 156.056s; volume=0.5; one source/listener; nonzero decoded output before/after one real loop boundary.");
        }

        [UnityTest]
        public IEnumerator T2_SliderLiveVolumeAndPlayRestartPersistence()
        {
            var slider = VolumeSlider();
            Assert.That(slider.value, Is.EqualTo(.5f));
            Assert.That(Percentage().text, Is.EqualTo("50%"));
            slider.value = 0;
            Assert.That(source.volume, Is.Zero);
            Assert.That(Percentage().text, Is.EqualTo("0%"));
            slider.value = 1;
            Assert.That(source.volume, Is.EqualTo(1));
            Assert.That(Percentage().text, Is.EqualTo("100%"));
            slider.value = .73f;
            Assert.That(source.volume, Is.EqualTo(.73f).Within(.0001f));
            Assert.That(Percentage().text, Is.EqualTo("73%"));
            settings.Close(); settings.Open();
            Assert.That(VolumeSlider().value, Is.EqualTo(.73f).Within(.0001f));
            Assert.That(source.isPlaying, Is.True);
            yield return Wait(.6f);
            Assert.That(PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey), Is.EqualTo(.73f).Within(.0001f));
            yield return new ExitPlayMode();
            PrepareOfflineScene();
            yield return new EnterPlayMode();
            BindRuntime();
            yield return Wait(.3f);
            Assert.That(source.volume, Is.EqualTo(.73f).Within(.0001f));
            Assert.That(VolumeSlider().value, Is.EqualTo(.73f).Within(.0001f));
            Assert.That(Percentage().text, Is.EqualTo("73%"));
            Assert.That(Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
            Debug.Log("[CafeBgm P0] T2: clean default50%, live0/100/73%, close/reopen73%, second Play source/UI73%; preferences restored by teardown.");
        }

        [UnityTest]
        public IEnumerator T3_SettingsDragDialogueAndSemanticIsolation()
        {
            int heard = 0, runs = 0;
            foreach (var store in Object.FindObjectsByType<PerceivedStateStore>(FindObjectsSortMode.None))
                store.ObservationApplied += r =>
                {
                    if (r.Observation != null && r.Observation.KnownFields.TryGetValue("event_type", out var type) && type == "heard_speech") heard++;
                };
            foreach (var owner in Object.FindObjectsByType<AgentOrchestrator>(FindObjectsSortMode.None))
                owner.RunStarted += _ => runs++;
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            Assert.That(backend.enabled, Is.False);
            Assert.That(backend.HttpBaseUrl, Is.EqualTo("http://127.0.0.1:0"));
            settings.Close();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            var player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            var target = Object.FindObjectsByType<DialogueParticipant>(FindObjectsSortMode.None)
                .Single(p => p.Entity.EntityId == "npc_cafe_01");
            var character = player.GetComponent<CharacterController>();
            character.enabled = false;
            player.transform.position = target.transform.position + Vector3.right * 1.5f;
            character.enabled = true;
            dialogue.SetSetupReady(true);
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(
                "\\[Dialogue\\] LIFECYCLE reason=conversation_created; conversation=player_01:npc_cafe_01:"));
            Assert.That(dialogue.SelectTarget(target), Is.True, dialogue.CheckEligibility(target).FailureCode.ToString());
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(source.isPlaying, Is.True);
            settings.Open();
            yield return Wait(.3f);
            var orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            Assert.That(orbit.IsInputSuspended, Is.True);
            Canvas.ForceUpdateCanvases();
            var slider = VolumeSlider();
            var rect = (RectTransform)slider.transform;
            var canvas = slider.GetComponentInParent<Canvas>();
            Vector2 point = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, rect.TransformPoint(new Vector3(rect.rect.xMin + rect.rect.width * .7f, 0)));
            var pointer = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
            var cameraRotation = Camera.main.transform.rotation;
            ExecuteEvents.Execute(slider.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(slider.gameObject, pointer, ExecuteEvents.dragHandler);
            ExecuteEvents.Execute(slider.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            yield return Wait(.25f);
            Assert.That(source.volume, Is.InRange(.6f, .8f));
            Assert.That(Quaternion.Angle(cameraRotation, Camera.main.transform.rotation), Is.LessThan(.05f));
            var keyboard = InputSystem.AddDevice<Keyboard>("CafeBgmTestKeyboard");
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape)); InputSystem.Update();
            settings.SendMessage("Update");
            Assert.That(settings.IsOpen, Is.False);
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(source.isPlaying, Is.True);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
            settings.SendMessage("Update");
            settings.Open();
            VolumeSlider().value = .5f;
            Canvas.ForceUpdateCanvases();
            var capture = System.IO.Path.GetFullPath("Captures/BgmP0/settings-volume.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(capture));
            ScreenCapture.CaptureScreenshot(capture);
            yield return Wait(.5f);
            Assert.That(heard, Is.Zero);
            Assert.That(runs, Is.Zero);
            Assert.That(backend.CognitiveRequestSendCount, Is.Zero);
            Assert.That(backend.ConnectionAttempts, Is.Zero);
            LogAssert.NoUnexpectedReceived();
            Debug.Log("[CafeBgm P0] T3: Settings drag leaves camera stable; ESC restores dialogue; playback continues; heard_speech=0, RunStarted=0, model sends=0, backend connects=0.");
        }

        private static Slider VolumeSlider() => Object.FindObjectsByType<Slider>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(s => s.name == "BgmVolumeSlider");
        private static TMP_Text Percentage() => Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Single(t => t.name == "BgmVolumePercent");
        private static IEnumerator Wait(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }
    }
}
