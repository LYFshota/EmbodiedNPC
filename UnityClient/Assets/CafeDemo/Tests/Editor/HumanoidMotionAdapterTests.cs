using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using NUnit.Framework;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class HumanoidMotionAdapterTests
    {
        HumanoidMotionAdapter[] adapters;
        readonly List<GameObject> temporary = new();
        float previousCaptureDelta;
        string Evidence => Path.GetFullPath(Path.Combine(Application.dataPath, "../../MotionAdapterEvidence"));
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene(MotionAdapterTestSceneSetup.ScenePath);
            yield return new EnterPlayMode();
            previousCaptureDelta = Time.captureDeltaTime; Time.captureDeltaTime = 1f / 60;
            adapters = Object.FindObjectsByType<HumanoidMotionAdapter>(FindObjectsSortMode.None).OrderBy(x => x.name).ToArray();
            yield return Frames(3);
            Assert.That(adapters.Length, Is.EqualTo(2));
            foreach (var adapter in adapters) Assert.That(adapter.LastDiagnostic, Does.StartWith("Humanoid bound"));
        }
        [UnityTearDown] public IEnumerator Close()
        {
            foreach (var go in temporary) if (go != null) Object.Destroy(go);
            temporary.Clear(); Time.captureDeltaTime = previousCaptureDelta;
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }
        void Capture(HumanoidMotionAdapter adapter, string name)
        {
            Directory.CreateDirectory(Evidence);
            var a = adapter.Animator; var go = new GameObject("Evidence camera"); var camera = go.AddComponent<Camera>();
            camera.transform.position = adapter.transform.position + new Vector3(0, 1, 3.8f);
            camera.transform.LookAt(adapter.transform.position + Vector3.up * .95f); camera.fieldOfView = 34;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .18f, .22f);
            var rt = RenderTexture.GetTemporary(480, 640, 24); var old = RenderTexture.active;
            var image = new Texture2D(480, 640, TextureFormat.RGB24, false);
            try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, 480, 640), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(Evidence, adapter.name + "-" + name + ".png"), image.EncodeToPNG()); }
            finally { RenderTexture.active = old; camera.targetTexture = null; RenderTexture.ReleaseTemporary(rt); Object.Destroy(image); Object.Destroy(go); }
        }
        static long Wave(HumanoidMotionAdapter adapter)
        {
            var gesture = adapter.GetComponent<GestureController>(); var motion = adapter.GetComponent<MotionChannelCoordinator>();
            Assert.That(gesture.CanPlay("wave"), Is.True);
            Assert.That(motion.TryBeginGesture("wave", false, Time.unscaledTimeAsDouble, gesture.PlaybackTimeout("wave"), out long id), Is.True);
            Assert.That(gesture.TryPlayApprovedGesture("wave", id), Is.True); return id;
        }
        static void Finite(Animator a)
        {
            foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.RightHand })
            { var t = a.GetBoneTransform(bone); Assert.That(float.IsFinite(t.position.sqrMagnitude) && float.IsFinite(t.rotation.x), Is.True); }
        }
        [UnityTest] public IEnumerator TwoRealVrms_IdleWalkTalkingWaveCarryNodAndExpression()
        {
            var rows = new List<string>();
            foreach (var adapter in adapters)
            {
                var a = adapter.Animator; var rootPosition = adapter.transform.position; var rootRotation = adapter.transform.rotation;
                var speech = adapter.GetComponent<EmbodiedNPC.Dialogue.Presentation.SpeechPresentationLifecycle>();
                var gesture = adapter.GetComponent<GestureController>(); var motion = adapter.GetComponent<MotionChannelCoordinator>();
                var talk = adapter.GetComponent<DialogueAnimationPresenter>(); var face = adapter.GetComponent<CafeVrmExpressionPresenter>();
                var gaze = adapter.GetComponentInChildren<HeadGazeController>();
                Assert.That(gaze.Head, Is.SameAs(a.GetBoneTransform(HumanBodyBones.Head)));
                yield return Frames(8); Capture(adapter, "idle");
                adapter.GetComponent<LocomotionPresenter>().enabled = false;
                a.SetFloat("Speed", 1); yield return Frames(35); Finite(a); Capture(adapter, "walk");
                Assert.That(a.GetCurrentAnimatorClipInfo(0).Any(x => x.clip.name.ToLowerInvariant().Contains("walk")), Is.True);
                a.SetFloat("Speed", 0); yield return Frames(5);
                speech.BeginPlayback("Offline motion presentation"); yield return Frames(25);
                Assert.That(talk.ActivationWeight, Is.GreaterThan(.9f)); Capture(adapter, "talk");
                Wave(adapter); yield return Frames(25);
                Assert.That(gesture.WaveEntered && gesture.IsWaveActive, Is.True); Assert.That(talk.CurrentWeight, Is.Zero); Capture(adapter, "wave");
                float deadline = Time.realtimeSinceStartup + 5;
                while (gesture.IsWaveActive && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(gesture.IsWaveActive, Is.False); yield return Frames(20);
                Assert.That(talk.ActivationWeight, Is.GreaterThan(.9f));
                Assert.That(motion.TryBeginGesture("nod", false, Time.unscaledTimeAsDouble, 2, out long nod), Is.True);
                Assert.That(gesture.TryPlayApprovedGesture("nod", nod), Is.True); yield return Frames(18);
                Assert.That(gesture.IsNodActive, Is.True); Finite(a); motion.CancelGesture();
                // Test-only ownership fixture. Production ownership/transactions are unchanged and regressed separately.
                var item = new GameObject("Offline held fixture"); temporary.Add(item);
                var cupVisual = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/CafeDemo/Prefabs/cup-coffee_0.095_0.095_0.126.prefab"), adapter.transform.Find("HandHoldPoint"));
                temporary.Add(cupVisual); cupVisual.transform.localPosition = Vector3.zero; cupVisual.transform.localRotation = Quaternion.identity;
                foreach (var collider in cupVisual.GetComponentsInChildren<Collider>()) collider.enabled = false;
                item.AddComponent<WorldEntity>().Configure("motion-item-" + adapter.name, "motion-item", EntityKind.Object);
                var held = item.AddComponent<HeldItemState>(); var so = new SerializedObject(held);
                so.FindProperty("holderEntityId").stringValue = adapter.GetComponent<WorldEntity>().EntityId; so.ApplyModifiedPropertiesWithoutUndo();
                Object.FindAnyObjectByType<EntityRegistry>().RebuildRegistry(); yield return Frames(25);
                Assert.That(adapter.GetComponent<CarryPresenter>().CurrentWeight, Is.GreaterThan(.9f));
                Assert.That(motion.TryBeginGesture("wave", false, Time.unscaledTimeAsDouble, 3, out _), Is.False);
                Assert.That(talk.CurrentWeight, Is.Zero); Capture(adapter, "carry");
                var hand = adapter.GetComponentInChildren<CarryAnchorFollower>(); hand.FollowPose();
                Assert.That(Vector3.Distance(adapter.transform.Find("HandHoldPoint").position, a.GetBoneTransform(HumanBodyBones.RightHand).position), Is.LessThan(.001f));
                so.FindProperty("holderEntityId").stringValue = ""; so.ApplyModifiedPropertiesWithoutUndo(); yield return Frames(25);
                cupVisual.SetActive(false);
                Assert.That(motion.IsCarryPresentationActive, Is.False); Assert.That(talk.ActivationWeight, Is.GreaterThan(.9f));
                bool blink = false;
                for (int i = 0; i < 600; i++) { face.Tick(.02f); if (face.BlinkWeight > .75f) { blink = true; break; } }
                Assert.That(face.HasBlink && blink, Is.True, adapter.name);
                a.GetComponent<Vrm10Instance>().Runtime.Process(); Capture(adapter, "blink");
                face.Tick(.3f); Assert.That(face.BlinkWeight, Is.Zero);
                speech.Cancel(); yield return Frames(25); Assert.That(talk.CurrentWeight, Is.Zero);
                Assert.That(adapter.transform.position, Is.EqualTo(rootPosition)); Assert.That(adapter.transform.rotation, Is.EqualTo(rootRotation));
                Finite(a); rows.Add(adapter.name + " humanScale=" + a.humanScale + " modelScale=" + a.transform.lossyScale +
                    " neck=" + (gaze.Neck != null) + " blink=" + face.HasBlink + " happy=" + face.HasHappy + " relaxed=" + face.HasRelaxed);
            }
            File.WriteAllLines(Path.Combine(Evidence, "two-characters.txt"), rows);
        }

        [UnityTest] public IEnumerator RebindBetweenRealModels_CleansOldPoseAndKeepsAuthorityAndOffsets()
        {
            var adapter = adapters[0]; var old = adapter.Animator; var owner = adapter.GetComponent<WorldEntity>();
            string id = owner.EntityId; var rootPosition = adapter.transform.position; var rootRotation = adapter.transform.rotation;
            var visual = old.transform.parent; var controller = old.runtimeAnimatorController;
            var candidate = Object.Instantiate(adapters[1].Animator.gameObject, visual); temporary.Add(candidate);
            candidate.name = "Replacement model"; var next = candidate.GetComponent<Animator>();
            next.runtimeAnimatorController = controller; next.applyRootMotion = false; yield return Frames(3);
            var hand = adapter.GetComponentInChildren<CarryAnchorFollower>();
            hand.Configure(old, adapter.transform.Find("HandHoldPoint"), new Vector3(.01f, .02f, -.03f), Vector3.zero);
            Wave(adapter); yield return Frames(15);
            Assert.That(adapter.TryBind(next, out var error), Is.True, error);
            Assert.That(old.GetLayerWeight(old.GetLayerIndex(GestureController.WaveLayerName)), Is.Zero);
            old.gameObject.SetActive(false); yield return Frames(3);
            Assert.That(adapter.GetComponentInChildren<HeadGazeController>().Head, Is.SameAs(next.GetBoneTransform(HumanBodyBones.Head)));
            Assert.That(hand.RightHand, Is.SameAs(next.GetBoneTransform(HumanBodyBones.RightHand)));
            hand.FollowPose(); Assert.That(Vector3.Distance(adapter.transform.Find("HandHoldPoint").position,
                hand.RightHand.TransformPoint(new Vector3(.01f, .02f, -.03f))), Is.LessThan(.001f));
            Assert.That(adapter.TryBind(next, out error), Is.True, error); // Repeat binding cannot double-subscribe gesture playback.
            Assert.That(adapter.GetComponent<MotionChannelCoordinator>().RequestGesture("wave", false), Is.True);
            yield return Frames(20); Assert.That(adapter.GetComponent<GestureController>().IsWaveActive, Is.True);
            Assert.That(adapter.GetComponent<DrinkPreparationPresenter>().IsReady, Is.True);
            Assert.That(adapter.GetComponent<CounterCleaningPresenter>().IsReady, Is.True);
            Assert.That(owner.EntityId, Is.EqualTo(id)); Assert.That(adapter.transform.position, Is.EqualTo(rootPosition));
            Assert.That(adapter.transform.rotation, Is.EqualTo(rootRotation)); Finite(next); Capture(adapter, "rebound-wave");
            Assert.That(adapter.GetComponent<CafeVrmExpressionPresenter>().HasBlink, Is.True);
        }

        [UnityTest] public IEnumerator GazeAndFacing_BothModels_UseTheirOwnHeadAndStableWrapper()
        {
            foreach (var adapter in adapters)
            {
                var a = adapter.Animator; var wrapper = a.transform.parent; var rootPosition = adapter.transform.position;
                var rootRotation = adapter.transform.rotation; var motion = adapter.GetComponent<MotionChannelCoordinator>();
                var gaze = adapter.GetComponentInChildren<HeadGazeController>();
                var point = new GameObject("Offline gaze point"); temporary.Add(point);
                point.transform.position = gaze.Head.position + wrapper.TransformDirection(new Vector3(.65f, .05f, 2));
                var attention = wrapper.gameObject.AddComponent<AttentionController>();
                attention.Configure(adapter.GetComponent<WorldEntity>(), null, Object.FindAnyObjectByType<EntityRegistry>(), null,
                    adapter.GetComponent<ExpressiveController>(), motion, wrapper);
                attention.ConfigureIdleLook(new[] { point.transform }, new Vector2(.1f, .1f));
                gaze.Configure(attention, a, wrapper, adapter.transform);
                yield return Frames(55);
                Assert.That(attention.Source, Is.EqualTo(AttentionSource.EnvironmentIdle));
                Assert.That(gaze.CurrentWeight, Is.GreaterThan(.8f));
                Assert.That(gaze.CurrentAngles.x, Is.GreaterThan(3).And.LessThan(25)); Finite(a); Capture(adapter, "gaze");
                attention.enabled = false; gaze.ResetPresentation();
                var facing = wrapper.GetComponent<FacingController>(); facing.SetDialogueTarget(point.transform, true);
                yield return Frames(30); Assert.That(facing.IsDialogueFacingActive, Is.True);
                Assert.That(Quaternion.Angle(wrapper.localRotation, facing.NeutralLocalRotation), Is.GreaterThan(5).And.LessThan(55));
                // This fixture has no NavMeshAgent. Stop its zero-speed projection before injecting navigation occupancy.
                adapter.GetComponent<LocomotionPresenter>().enabled = false;
                motion.SetNavigationActive(true); yield return Frames(35);
                Assert.That(facing.IsDialogueFacingActive, Is.False);
                Assert.That(Quaternion.Angle(wrapper.localRotation, facing.NeutralLocalRotation), Is.LessThan(.1f));
                motion.SetNavigationActive(false);
                Assert.That(adapter.transform.position, Is.EqualTo(rootPosition)); Assert.That(adapter.transform.rotation, Is.EqualTo(rootRotation));
            }
        }

        [UnityTest] public IEnumerator InvalidOrBusyCandidate_LeavesExistingRigAndLeaseUntouched()
        {
            var adapter = adapters[0]; var original = adapter.Animator; var motion = adapter.GetComponent<MotionChannelCoordinator>();
            long id = Wave(adapter); yield return Frames(5);
            var bad = new GameObject("Invalid animator"); temporary.Add(bad); var a = bad.AddComponent<Animator>();
            Assert.That(adapter.TryBind(a, out _), Is.False); Assert.That(adapter.Animator, Is.SameAs(original));
            Assert.That(motion.ActiveGestureId, Is.EqualTo(id));
            foreach (var busy in new[] { "navigation", "task", "interaction", "emergency", "carry" })
            {
                motion.CancelGesture();
                if (busy == "navigation") motion.SetNavigationActive(true);
                if (busy == "task") motion.BeginTaskMotion();
                if (busy == "interaction") motion.SetInteractionActive(true);
                if (busy == "emergency") motion.BeginEmergencyMotion();
                if (busy == "carry") motion.SetCarryPresentationActive(true);
                Assert.That(adapter.TryBind(original, out _), Is.False, busy);
                Assert.That(adapter.Animator, Is.SameAs(original));
                motion.SetNavigationActive(false); motion.EndTaskMotion(); motion.SetInteractionActive(false);
                motion.EndEmergencyMotion(); motion.SetCarryPresentationActive(false);
            }
            original.transform.localScale = new Vector3(1, 2, 1);
            Assert.That(adapter.TryBind(original, out _), Is.False);
        }
    }
}
