using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Opt-in only. The ordinary scene and its network/configuration lifecycle are not modified.
    [InitializeOnLoad]
    internal static class PlayerWalkCaptureIsolation
    {
        internal const string Key = "EMBODIEDNPC_PLAYER_WALK_EVIDENCE";
        static PlayerWalkCaptureIsolation() => EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.ExitingEditMode || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Key))) return;
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (component is BackendWebSocketClient backend)
                {
                    var so = new SerializedObject(backend); so.FindProperty("connectOnStart").boolValue = false;
                    so.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws"; so.ApplyModifiedPropertiesWithoutUndo();
                }
                if (component is BackendWebSocketClient or ModelConfigClient or FirstRunModelSetup or CafeDemoDeveloperPanel or
                    CognitiveRunDriver or TaskConsiderationSource or RuntimeTraceRecorder or CafePerceptionDebugVisualizer or
                    EmbodiedNPC.Dialogue.Presentation.DialogueHistoryPanel || component.GetType().Name is
                    "DailyBehaviorDemoTrigger" or "CounterCleaningCognitionSource" or "ServiceZoneDwellCognitionSource") component.enabled = false;
            }
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("player-walk-isolated", "Walk test", 1, DateTimeOffset.UnixEpoch);
        };
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void IsolateProfiles()
        {
            string path = Environment.GetEnvironmentVariable(Key); if (string.IsNullOrEmpty(path)) return;
            typeof(CharacterOverrideStore).GetField("shared", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, new CharacterOverrideStore(Path.Combine(path, "private-character-overrides.json")));
        }
    }

    [Explicit("Requires an external EMBODIEDNPC_PLAYER_WALK_EVIDENCE root and offline visual validation.")]
    public sealed class PlayerWalkCadenceTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        private readonly List<string> temporaryAssets = new();
        private readonly List<string> samples = new();
        private readonly List<string> frameTimes = new();
        private float oldCapture;
        private int oldTarget, oldVsync;
        private bool clockSet;
        private string root;
        private Camera captureCamera;
        private Vector3 start, direction;
        private float playback;
        private string phase;
        private int frames;
        private float captureAt;
        private AnimatorController original;

        [UnityTearDown] public IEnumerator CleanupCadence()
        {
            if (clockSet) { Time.captureDeltaTime = oldCapture; Application.targetFrameRate = oldTarget; QualitySettings.vSyncCount = oldVsync; }
            foreach (string path in temporaryAssets) AssetDatabase.DeleteAsset(path);
            temporaryAssets.Clear(); yield break;
        }
        [UnityTest] public IEnumerator ComparePlayerOnlyCadenceAndInputLocks() => CaptureSequence(new[] { 3.25f, 2.75f, 2.4f, 2.1f });
        [UnityTest] public IEnumerator VerifyFinalCadenceAndInputLocks() => CaptureSequence(new[] { 3.25f, 2.75f });
        private IEnumerator CaptureSequence(float[] candidates)
        {
            root = Environment.GetEnvironmentVariable(PlayerWalkCaptureIsolation.Key);
            Assert.That(root, Is.Not.Null.And.Not.Empty, "Explicit isolated evidence root required.");
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include).All(x => !x.enabled && !x.IsConnected), Is.True);
            Assert.That(animator.isHuman && !animator.applyRootMotion, Is.True);
            original = (AnimatorController)animator.runtimeAnimatorController;
            Assert.That(((BlendTree)original.layers[0].stateMachine.defaultState.motion).children[1].timeScale,Is.EqualTo(2.75f));
            var movement=new SerializedObject(player);
            Assert.That(movement.FindProperty("moveSpeed").floatValue,Is.EqualTo(2.6f));
            Assert.That(movement.FindProperty("acceleration").floatValue,Is.EqualTo(10f));
            Assert.That(movement.FindProperty("deceleration").floatValue,Is.EqualTo(18f));
            Assert.That(movement.FindProperty("turnSpeed").floatValue,Is.EqualTo(420f));
            oldCapture = Time.captureDeltaTime; oldTarget = Application.targetFrameRate; oldVsync = QualitySettings.vSyncCount; clockSet = true;
            Time.captureDeltaTime = 1f / 60; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;
            captureCamera = Camera.main; captureCamera.GetComponent<CafeDemoOrbitCamera>().enabled = false;
            // The disabled offline settings host never ran OnEnable's initial panel hide.
            // Reproduce that UI-only initialization without invoking its network startup/save lifecycle.
            var settingsHost=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            ((GameObject)typeof(CafeDemoDeveloperPanel).GetField("panelRoot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settingsHost)).SetActive(false);
            typeof(CafeDemoDeveloperPanel).GetField("startupConfigResolved",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(settingsHost,true);
            samples.Add("playback,phase,time,speed,normalized,anim,lfx,lfy,lfz,rfx,rfy,rfz,rootx,rootz");
            frameTimes.Add("playback,file,time");
            ChooseClearStrip();
            foreach (float candidate in candidates)
            {
                playback = candidate; ClonePlayback(candidate); frames = 0;
                Directory.CreateDirectory(Path.Combine(root, candidate.ToString("0.00", CultureInfo.InvariantCulture)));
                ResetPosition(); phase = "idle"; yield return Observe(.3f);
                Assert.That(locomotion.NormalizedSpeed, Is.LessThan(.02f));
                phase = "start"; Keys(Key.W); yield return Observe(.4f);
                phase = "steady"; yield return Observe(1.15f);
                Assert.That(player.Controller.velocity.magnitude, Is.InRange(2.58f, 2.65f), "Full speed must be measured, not inferred from input.");
                Assert.That(locomotion.NormalizedSpeed, Is.GreaterThan(.99f));
                phase = "stop"; Keys(); yield return Observe(.4f);
                Assert.That(locomotion.NormalizedSpeed, Is.LessThan(.02f));
                phase = "left"; Keys(Key.A); yield return Observe(.4f);
                phase = "right"; Keys(Key.D); yield return Observe(.45f);
                phase = "stop-turn"; Keys(); yield return Observe(.35f);
            }
            // Same real menu/dialogue input adapters as normal gameplay, without connecting a Backend.
            var flow = Object.FindAnyObjectByType<CafeUiFlowController>();
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().Close();
            Object.FindAnyObjectByType<DialogueController>().SetSetupReady(true);
            var panel=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();var dlg=Object.FindAnyObjectByType<DialogueController>();
            File.WriteAllText(Path.Combine(root,"menu-guard.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new {
                bound=typeof(CafeUiFlowController).GetField("bound",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(flow),
                flowActive=flow.isActiveAndEnabled,flow.IsMenuOpen,panel.IsOpen,panel.IsStartupAdmissionHeld,dlg.IsSetupReady,dlg.IsPresentationVisible,
                emoteBusy=Object.FindAnyObjectByType<PlayerEmotePanel>()?.BlocksOtherUi},Newtonsoft.Json.Formatting.Indented));
            Assert.That(flow, Is.Not.Null); flow.OpenMenu(); Assert.That(flow.IsMenuOpen, Is.True);
            phase = "menu-lock"; Keys(Key.W); yield return Observe(.25f);
            Assert.That(player.Controller.velocity.magnitude, Is.LessThan(.05f)); Keys(); flow.Resume();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            phase = "dialogue-lock"; Keys(Key.W); yield return Observe(.25f);
            Assert.That(player.Controller.velocity.magnitude, Is.LessThan(.05f)); Keys(); dialogue.HideDialoguePresentation();
            File.WriteAllLines(Path.Combine(root, "motion.csv"), samples);
            File.WriteAllLines(Path.Combine(root, "frame-times.csv"), frameTimes);
            animator.runtimeAnimatorController = original;
            File.WriteAllText(Path.Combine(root, "playmode-result.json"), "{\"playmode\":true,\"network_disabled\":true,\"full_speed\":2.6,\"menu_lock\":true,\"dialogue_lock\":true}");
        }
        private void ChooseClearStrip()
        {
            float best = 0;
            foreach (var position in new[] { new Vector3(-2.5f,.03f,.6f),new Vector3(-2.5f,.03f,1.1f),new Vector3(-3f,.03f,.5f),new Vector3(0,.03f,1) })
            foreach (var dir in new[] {Vector3.right,Vector3.forward,Vector3.left})
            {
                float distance = Physics.CapsuleCastAll(position+Vector3.up*.4f,position+Vector3.up*1.5f,.3f,dir,7,~0,QueryTriggerInteraction.Ignore)
                    .Where(x => !x.collider.transform.IsChildOf(player.transform)).Select(x=>x.distance).DefaultIfEmpty(7).Min();
                if (distance > best) { best = distance; start = position; direction = dir; }
            }
            Assert.That(best, Is.GreaterThan(4.2f), "Need a clear actual Cafe floor strip.");
            File.WriteAllText(Path.Combine(root,"strip.json"),JsonUtility.ToJson(new Strip {start=start,direction=direction,clearance=best}));
        }
        [Serializable] private class Strip { public Vector3 start,direction; public float clearance; }
        private void ResetPosition()
        {
            Keys(); player.SetDialogueInputSuspended(true); player.Controller.enabled = false;
            player.transform.SetPositionAndRotation(start,Quaternion.LookRotation(direction)); player.Controller.enabled = true;
            player.SetDialogueInputSuspended(false); captureCamera.transform.rotation=Quaternion.LookRotation(direction); Physics.SyncTransforms();
            animator.Rebind(); animator.Update(0); captureAt = Time.time;
        }
        private void ClonePlayback(float value)
        {
            string path = "Assets/PlayerWalkTrial-" + Guid.NewGuid().ToString("N") + ".controller";
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(original), path), Is.True); temporaryAssets.Add(path);
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            var tree=(BlendTree)controller.layers[0].stateMachine.defaultState.motion;
            var children=tree.children; children[1].timeScale=value; tree.children=children;
            EditorUtility.SetDirty(tree); AssetDatabase.SaveAssetIfDirty(tree); animator.runtimeAnimatorController=controller;
        }
        private IEnumerator Observe(float duration)
        {
            float until=Time.time+duration, deadline=Time.realtimeSinceStartup+60;
            while(Time.time<until && Time.realtimeSinceStartup<deadline)
            {
                yield return null;
                var left=animator.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                var right=animator.GetBoneTransform(HumanBodyBones.RightFoot).position;
                var position=player.transform.position;
                samples.Add(string.Join(",",new[]{playback.ToString("F2",CultureInfo.InvariantCulture),phase,Time.time.ToString("F5",CultureInfo.InvariantCulture),
                    Vector3.ProjectOnPlane(player.Controller.velocity,Vector3.up).magnitude.ToString("F4",CultureInfo.InvariantCulture),
                    locomotion.NormalizedSpeed.ToString("F4",CultureInfo.InvariantCulture),animator.GetFloat("Speed").ToString("F4",CultureInfo.InvariantCulture),
                    left.x.ToString("F5",CultureInfo.InvariantCulture),left.y.ToString("F5",CultureInfo.InvariantCulture),left.z.ToString("F5",CultureInfo.InvariantCulture),
                    right.x.ToString("F5",CultureInfo.InvariantCulture),right.y.ToString("F5",CultureInfo.InvariantCulture),right.z.ToString("F5",CultureInfo.InvariantCulture),
                    position.x.ToString("F5",CultureInfo.InvariantCulture),position.z.ToString("F5",CultureInfo.InvariantCulture)}));
                if (Time.time >= captureAt) { CaptureFrame(); captureAt=Time.time+1f/15; }
            }
            Assert.That(Time.time,Is.GreaterThanOrEqualTo(until)); File.WriteAllLines(Path.Combine(root,"motion.csv"),samples);
        }
        private void CaptureFrame()
        {
            var oldPosition=captureCamera.transform.position;var oldRotation=captureCamera.transform.rotation;var oldTarget=captureCamera.targetTexture;float fov=captureCamera.fieldOfView;
            var target=RenderTexture.GetTemporary(640,480,24);var previous=RenderTexture.active;var image=new Texture2D(640,480,TextureFormat.RGB24,false);
            var windows=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w is SceneView || w.GetType().Name=="GameView").ToArray();
            var gizmos=windows.Select(w=>w.GetType().GetProperty("drawGizmos",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)).ToArray();
            var priorGizmos=windows.Select((w,i)=>gizmos[i]==null?false:(bool)gizmos[i].GetValue(w)).ToArray();
            try
            {
                for(int i=0;i<windows.Length;i++)gizmos[i]?.SetValue(windows[i],false);
                var focus=player.transform.position+Vector3.up*.85f;
                foreach(var line in Object.FindObjectsByType<LineRenderer>())line.enabled=false;
                captureCamera.transform.position=focus-direction*1.8f+Vector3.up*1.25f;
                captureCamera.transform.LookAt(focus);captureCamera.fieldOfView=48;captureCamera.targetTexture=target;
                captureCamera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,640,480),0,0);image.Apply();
                string file=$"{frames++:D4}-{phase}.png";
                File.WriteAllBytes(Path.Combine(root,playback.ToString("0.00",CultureInfo.InvariantCulture),file),image.EncodeToPNG());
                frameTimes.Add(playback.ToString("F2",CultureInfo.InvariantCulture)+","+file+","+Time.time.ToString("F6",CultureInfo.InvariantCulture));
                File.WriteAllLines(Path.Combine(root,"frame-times.csv"),frameTimes);
            }
            finally {for(int i=0;i<windows.Length;i++)gizmos[i]?.SetValue(windows[i],priorGizmos[i]);captureCamera.targetTexture=oldTarget;captureCamera.fieldOfView=fov;captureCamera.transform.SetPositionAndRotation(oldPosition,oldRotation);RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);Object.Destroy(image);}
        }
    }
}
