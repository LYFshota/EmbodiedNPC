using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Routines;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Holds a real CognitiveRunDriver request without a socket, provider, persistence or paid call.
    public sealed class GazeWaitingTransport : BackendWebSocketClient
    {
        public override bool IsConnected => true;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest request) => Task.FromResult(true);
    }

    public sealed class PlayerDialogueGazeTests
    {
        const string Key = "PlayerGaze.Tests.";
        static string Evidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_GAZE_EVIDENCE") ??
            Path.Combine(Path.GetTempPath(), "EmbodiedNPC-PlayerGaze");
        CafeDemoPlayerController player;
        PlayerVrmAppearance appearance;
        HeadGazeController gaze;
        DialogueController dialogue;
        DialogueParticipant npc, other;
        Quaternion rootRotation, cameraRotation;
        Vector3 cameraPosition;
        Camera sceneCamera;
        float previousCapture;

        [UnitySetUp] public IEnumerator Load()
        {
            SessionState.SetString(Key + "previousAvatar", Environment.GetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT") ?? "");
            SessionState.SetString(Key + "root", Path.Combine(Path.GetTempPath(), "EmbodiedNPC-Gaze-" + Guid.NewGuid().ToString("N")));
            Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT", SessionState.GetString(Key + "root", ""));
            Directory.CreateDirectory(Evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>()) trace.enabled = false;
            foreach (var routine in Object.FindObjectsByType<DailyBehaviorController>()) routine.enabled = false;
            foreach (var routine in Object.FindObjectsByType<NpcDefaultRoutineController>()) routine.enabled = false;
            foreach (var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>()) trigger.ConfigureDemo(false, 15, 35);
            yield return new EnterPlayMode();
            previousCapture = Time.captureDeltaTime; Time.captureDeltaTime = 1f / 60;
            AnimationSceneTestIsolation.Ready();
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(settings, (Func<bool>)(() => true));
            settings.ApplyStartupConfig(new EmbodiedNPC.Settings.ModelConfigView { configured = true }); settings.Close();
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            player.enabled = false; player.Controller.enabled = false;
            player.transform.SetPositionAndRotation(new Vector3(1000, 0, 1000), Quaternion.identity);
            player.GetComponent<PlayerLocomotionPresenter>().enabled = false;
            appearance = player.GetComponent<PlayerVrmAppearance>(); gaze = player.GetComponent<HeadGazeController>();
            Assert.IsNotNull(gaze); Assert.IsTrue(gaze.IsRigReady); Assert.IsFalse(appearance.IsCustom);
            var targets = Object.FindObjectsByType<DialogueParticipant>();
            npc = targets.Single(x => x.Entity.EntityId == "npc_cafe_01");
            other = targets.Single(x => x.Entity.EntityId == "npc_cafe_regular_01");
            foreach (var agent in Object.FindObjectsByType<NavMeshAgent>()) agent.enabled = false;
            foreach (var target in targets)
            {
                target.transform.position = player.transform.position + new Vector3(15, 0, 15);
                foreach (var facing in target.GetComponentsInChildren<FacingController>()) facing.enabled = false;
            }
            Place(npc, 0); Place(other, -35);
            dialogue = Object.FindAnyObjectByType<DialogueController>(); dialogue.ClearSession(); dialogue.SetSetupReady(true);
            Object.FindAnyObjectByType<CafeDemoOrbitCamera>().enabled = false;
            sceneCamera = Camera.main; cameraPosition = sceneCamera.transform.position; cameraRotation = sceneCamera.transform.rotation;
            rootRotation = player.transform.rotation;
            yield return Frames(4);
        }
        [UnityTearDown] public IEnumerator Close()
        {
            Time.captureDeltaTime = previousCapture;
            if (Application.isPlaying) yield return new ExitPlayMode();
            string old = SessionState.GetString(Key + "previousAvatar", "");
            Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT", old == "" ? null : old);
        }
        static IEnumerator Frames(int count) { for (int i = 0; i < count; ++i) yield return null; }
        void Place(DialogueParticipant target, float yaw, float height = 0)
        {
            target.transform.position = player.transform.position + Quaternion.Euler(0, yaw, 0) * Vector3.forward * 2 + Vector3.up * height;
            Physics.SyncTransforms();
        }
        void Begin(DialogueParticipant target)
        {
            Assert.IsTrue(dialogue.SelectTarget(target), dialogue.LastEligibility.FailureCode.ToString());
            Assert.IsTrue(dialogue.IsPresentationVisible);
        }
        void UnchangedControls()
        {
            Assert.Less(Quaternion.Angle(rootRotation, player.transform.rotation), .001f);
            Assert.Less(Vector3.Distance(cameraPosition, sceneCamera.transform.position), .001f);
            Assert.Less(Quaternion.Angle(cameraRotation, sceneCamera.transform.rotation), .001f);
        }
        void Capture(string name)
        {
            var go = new GameObject("Gaze evidence camera"); var camera = go.AddComponent<Camera>();
            var image = new Texture2D(720, 720, TextureFormat.RGB24, false);
            var rt = RenderTexture.GetTemporary(720, 720, 24); var previous = RenderTexture.active;
            var parts = player.GetComponentsInChildren<Transform>(true); var layers = parts.Select(x => x.gameObject.layer).ToArray();
            foreach (var part in parts) part.gameObject.layer = 31;
            camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.23f, .26f, .3f);
            camera.transform.position = player.transform.position + new Vector3(0, 1.7f, 2.6f);
            camera.transform.LookAt(gaze.Head.position + Vector3.up * .03f); camera.fieldOfView = 30;
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, 720, 720), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(Evidence, name + ".png"), image.EncodeToPNG());
                File.WriteAllText(Path.Combine(Evidence, name + ".json"), JsonUtility.ToJson(new GazeSample {
                    angles = gaze.CurrentAngles, weight = gaze.CurrentWeight, head = gaze.Head.rotation,
                    neck = gaze.Neck == null ? Quaternion.identity : gaze.Neck.rotation, avatar = appearance.CurrentName }));
            }
            finally
            {
                for (int i = 0; i < parts.Length; i++) if (parts[i] != null) parts[i].gameObject.layer = layers[i];
                RenderTexture.active = previous; camera.targetTexture = null; RenderTexture.ReleaseTemporary(rt); Object.Destroy(image); Object.Destroy(go);
            }
        }
        [Serializable] class GazeSample { public Vector2 angles; public float weight; public Quaternion head, neck; public string avatar; }

        [UnityTest] public IEnumerator DefaultConversation_TracksSidesWaitsSwitchesAndReleases()
        {
            Assert.AreEqual(0, gaze.CurrentWeight); Capture("xingmang-neutral");
            foreach (float yaw in new[] { 0, -45, 45 })
            {
                Place(npc, yaw); Begin(npc);
                yield return Frames(1); Assert.Less(gaze.CurrentWeight, 1, "No instant acquisition");
                yield return Frames(65);
                Assert.Greater(gaze.CurrentWeight, .95f); Assert.That(gaze.CurrentAngles.x, Is.EqualTo(yaw).Within(7));
                Capture("xingmang-" + yaw); UnchangedControls();
                dialogue.ClearSession(); yield return Frames(30); Assert.AreEqual(0, gaze.CurrentWeight);
            }
            Place(npc, 30); Begin(npc); yield return Frames(60);
            var fake = new GameObject("Offline waiting transport").AddComponent<GazeWaitingTransport>(); fake.enabled = false;
            var driver = npc.GetComponent<CognitiveRunDriver>();
            driver.Configure(npc.Entity, npc.GetComponent<AgentOrchestrator>(), npc.GetComponent<CognitiveContextComposer>(), fake, driver.Presenter as MonoBehaviour);
            driver.enabled = true; npc.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            Assert.IsTrue(npc.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("gaze-wait", AgentEventTypes.PlayerUtterance,
                "player_01", npc.Entity.EntityId, "", new Dictionary<string,string> { ["text"] = "浣犲ソ", ["conversation_id"] = dialogue.CurrentConversationId }, Time.unscaledTimeAsDouble)));
            yield return Frames(4); Assert.IsNotNull(driver.PendingRequest, "Real driver holds a synthetic unanswered request");
            yield return Frames(60); Assert.Greater(gaze.CurrentWeight, .95f); Capture("xingmang-waiting");
            var prior = gaze.Head.rotation; float maxStep = 0;
            for (int i = 0; i < 90; ++i)
            {
                Place(npc, Mathf.Lerp(30, -30, i / 89f));
                player.transform.position += Vector3.right * .001f;
                yield return null;
                maxStep = Mathf.Max(maxStep, Quaternion.Angle(prior, gaze.Head.rotation)); prior = gaze.Head.rotation;
                if (i % 3 == 0) Capture("motion-" + (i / 3).ToString("D3"));
            }
            Assert.Less(maxStep, 8, "Bounded actual head movement while both actors move");
            Assert.Less(gaze.CurrentAngles.x, -20); UnchangedControls();
            Begin(other); var before = gaze.CurrentAngles; yield return Frames(1);
            Assert.Less(Vector2.Distance(before, gaze.CurrentAngles), 4, "Target switch remains gradual");
            yield return Frames(60); Capture("xingmang-switch");
            Object.FindAnyObjectByType<CafeUiFlowController>().RequestCancel();
            Assert.IsFalse(dialogue.IsPresentationVisible); Assert.IsTrue(dialogue.HasActiveSession, "ESC retains existing session semantics");
            yield return Frames(1); Assert.Greater(gaze.CurrentWeight, 0); yield return Frames(30); Assert.AreEqual(0, gaze.CurrentWeight);
            Capture("xingmang-after-esc");
            Place(other, 30); Begin(other); yield return Frames(50);
            other.transform.position += Vector3.forward * 8; yield return Frames(35);
            Assert.IsFalse(dialogue.IsPresentationVisible); Assert.AreEqual(0, gaze.CurrentWeight, "Out-of-range conversation releases gaze");
            File.WriteAllText(Path.Combine(Evidence, "motion-metrics.json"), "{\"max_head_degrees_per_frame\":" +
                maxStep.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"sample_hz\":60,\"captured_every_frames\":3}");
            Place(other, 135); Begin(other); yield return Frames(65); Assert.AreEqual(0, gaze.CurrentWeight, "No backwards neck twist");
            Place(other, 10, 2); yield return Frames(65); Assert.LessOrEqual(gaze.CurrentAngles.y, 25); Capture("xingmang-pitch-limit");
            other.gameObject.SetActive(false); yield return Frames(30); Assert.AreEqual(0, gaze.CurrentWeight);
            other.gameObject.SetActive(true); yield return Frames(40); Assert.Greater(gaze.CurrentWeight, 0);
            Object.Destroy(other.gameObject); yield return Frames(35); Assert.AreEqual(0, gaze.CurrentWeight);
            Assert.IsFalse(dialogue.HasActiveSession); UnchangedControls();
        }

        [UnityTest] public IEnumerator ImportedVrm_RebindsAndCoexistsWithWalkWaveExpression()
        {
            var originalHead = gaze.Head;
            Place(npc, 35); Begin(npc); yield return Frames(60); dialogue.HideDialoguePresentation(); yield return Frames(30);
            var task = appearance.ImportPathAsync(Path.GetFullPath("Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm"));
            float deadline = Time.realtimeSinceStartup + 75;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted); Assert.IsTrue(task.Result, appearance.Diagnostic);
            Assert.IsTrue(appearance.IsCustom); Assert.AreNotSame(originalHead, gaze.Head);
            Assert.AreSame(appearance.CurrentAnimator.GetBoneTransform(HumanBodyBones.Head), gaze.Head);
            Assert.AreEqual(0, gaze.CurrentWeight);
            Begin(npc); yield return Frames(65); Assert.Greater(gaze.CurrentAngles.x, 25); Capture("custom-idle-gaze");
            appearance.CurrentAnimator.SetFloat("Speed", .6f); yield return Frames(60);
            Assert.Greater(gaze.CurrentWeight, .95f); Capture("custom-walk-gaze"); appearance.CurrentAnimator.SetFloat("Speed", 0);
            // Existing emote admission is preserved: release the input lock only in this controlled fixture.
            player.Controller.enabled = true;
            var movementSettings = new SerializedObject(player); movementSettings.FindProperty("gravity").floatValue = 0;
            movementSettings.ApplyModifiedPropertiesWithoutUndo();
            player.enabled = true; player.SetDialogueInputSuspended(false);
            var emote = player.GetComponent<PlayerEmoteController>();
            Assert.IsTrue(emote.TryWave()); yield return Frames(25);
            Assert.IsTrue(emote.IsActive); Assert.Greater(gaze.CurrentWeight, .95f); Capture("custom-wave-gaze");
            yield return Frames(240); player.SetDialogueInputSuspended(false);
            Assert.IsTrue(emote.TryPlay("cheer")); yield return Frames(25);
            var face = player.GetComponent<CafeVrmExpressionPresenter>();
            if (face != null && face.HasHappy) Assert.Greater(face.HappyWeight, .1f);
            Assert.Greater(gaze.CurrentWeight, .95f); Capture("custom-expression-gaze");
            yield return Frames(240); player.enabled = false; player.Controller.enabled = false;
            dialogue.ClearSession(); yield return Frames(35); Assert.AreEqual(0, gaze.CurrentWeight);
            Assert.IsTrue(appearance.RestoreDefault(), appearance.Diagnostic);
            Assert.AreSame(originalHead, gaze.Head); Begin(npc); yield return Frames(65); Capture("xingmang-restored-gaze");
            Assert.Greater(gaze.CurrentWeight, .95f); UnchangedControls();
        }
    }
}
