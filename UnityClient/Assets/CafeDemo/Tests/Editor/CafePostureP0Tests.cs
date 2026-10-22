using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Real-frame V2 posture regression; never saves the scene or changes world authority.</summary>
    public sealed class CafePostureP0Tests
    {
        const string Animations = "Assets/CafeDemo/Animations/";
        // Whole-arm hang includes the elbow offset needed to clear the trousers.
        // Upper-arm abduction, elbow offset and thigh clearance are asserted separately.
        const float MaxRelaxedArmHang = 13f;
        readonly List<Object> temporary = new List<Object>();
        readonly List<Sample> samples = new List<Sample>();
        WorldEntity[] actors;
        WorldEntity player;
        DialogueController dialogue;
        float oldCapture;
        string Evidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ??
            Path.Combine(Path.GetTempPath(), "EmbodiedNPC-PostureP0");
        static readonly HumanBodyBones[] Torso = { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest };
        [Serializable] sealed class Sample
        {
            public string actor, mode;
            public float time, lean, leftArm, rightArm, leftKnee, rightKnee, leftFootY, rightFootY;
        }
        [Serializable] sealed class EvidenceRows { public Sample[] rows; }

        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode();
            AnimationSceneTestIsolation.Ready();
            actors = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None)
                .Where(a => a.EntityId == "player_01" || a.GetComponent<ExpressiveController>() != null)
                .OrderBy(a => a.EntityId).ToArray();
            player = actors.Single(a => a.EntityId == "player_01");
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(settings, (Func<bool>)(() => true));
            settings.ApplyStartupConfig(new EmbodiedNPC.Settings.ModelConfigView { configured = true }); settings.Close();
            dialogue.SetSetupReady(true);
            foreach (var actor in actors)
                if (actor.GetComponent<EmbodiedNPC.Cognition.CognitiveRunDriver>() is var driver && driver != null)
                    driver.enabled = false;
            oldCapture = Time.captureDeltaTime;
            Time.captureDeltaTime = 1f / 60;
            Directory.CreateDirectory(Evidence);
            yield return Frames(3);
        }
        [UnityTearDown] public IEnumerator Close()
        {
            File.WriteAllText(Path.Combine(Evidence, TestContext.CurrentContext.Test.Name + ".json"),
                JsonUtility.ToJson(new EvidenceRows { rows = samples.ToArray() }, true));
            foreach (var item in temporary) if (item != null) Object.Destroy(item);
            temporary.Clear(); samples.Clear();
            Time.captureDeltaTime = oldCapture;
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
        static Animator Rig(WorldEntity actor) => actor.GetComponentInChildren<Animator>();
        static IEnumerator Frames(int count) { for (int i = 0; i < count; i++) yield return null; }
        static float Arm(Animator a, bool left) => Vector3.Angle(
            a.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position -
            a.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm).position, -a.transform.up);
        static float Knee(Animator a, bool left)
        {
            var hip = a.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
            var knee = a.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
            var foot = a.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            return Vector3.Angle(hip.position - knee.position, foot.position - knee.position);
        }
        void Record(Animator a, string name, string mode)
        {
            foreach (var bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
                HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
            {
                var t = a.GetBoneTransform(bone); Assert.That(t, Is.Not.Null, name + " " + bone);
                Assert.That(float.IsFinite(t.position.sqrMagnitude) && float.IsFinite(t.localRotation.x) &&
                    float.IsFinite(t.localRotation.y) && float.IsFinite(t.localRotation.z) && float.IsFinite(t.localRotation.w),
                    Is.True, name + " " + bone);
            }
            Vector3 up = a.transform.InverseTransformDirection(a.GetBoneTransform(HumanBodyBones.Chest).position -
                a.GetBoneTransform(HumanBodyBones.Hips).position).normalized;
            samples.Add(new Sample { actor = name, mode = mode, time = Time.time,
                lean = Mathf.Atan2(-up.z, up.y) * Mathf.Rad2Deg, leftArm = Arm(a, true), rightArm = Arm(a, false),
                leftKnee = Knee(a, true), rightKnee = Knee(a, false),
                leftFootY = a.GetBoneTransform(HumanBodyBones.LeftFoot).position.y,
                rightFootY = a.GetBoneTransform(HumanBodyBones.RightFoot).position.y });
        }
        void Capture(string name, Animator[] rigs, params float[] angles)
        {
            const int width = 360, height = 540;
            foreach (float angle in angles.Length == 0 ? new[] { 0f, 45f, 90f } : angles)
            {
                var go = new GameObject("PostureEvidenceCamera"); var camera = go.AddComponent<Camera>();
                camera.fieldOfView = 37; camera.nearClipPlane = .03f; camera.farClipPlane = 2000;
                camera.cullingMask = 1 << 31; camera.backgroundColor = new Color(.18f, .2f, .23f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                var rt = RenderTexture.GetTemporary(width, height, 24);
                var sheet = new Texture2D(width * rigs.Length, height, TextureFormat.RGB24, false);
                var previous = RenderTexture.active;
                try
                {
                    camera.targetTexture = rt;
                    for (int i = 0; i < rigs.Length; i++)
                    {
                        var a = rigs[i];
                        var head = a.GetBoneTransform(HumanBodyBones.Head).position;
                        var feet = (a.GetBoneTransform(HumanBodyBones.LeftFoot).position + a.GetBoneTransform(HumanBodyBones.RightFoot).position) * .5f;
                        var center = (head + feet) * .5f;
                        float span = Mathf.Max(1.1f, head.y - feet.y + (name.Contains("player-") || name.StartsWith("gesture-") ? .8f : .35f));
                        Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * a.transform.forward;
                        camera.transform.position = center + direction * (span * 1.75f);
                        camera.transform.LookAt(center);
                        // Capture the live posed model without counter/table occlusion; restore layers immediately.
                        var renderObjects = a.GetComponentsInChildren<Renderer>().Select(r => r.gameObject).Distinct().ToArray();
                        var layers = renderObjects.Select(r => r.layer).ToArray();
                        try
                        {
                            foreach (var item in renderObjects) item.layer = 31;
                            camera.Render(); RenderTexture.active = rt;
                            sheet.ReadPixels(new Rect(0, 0, width, height), i * width, 0);
                        }
                        finally { for (int j = 0; j < renderObjects.Length; j++) renderObjects[j].layer = layers[j]; }
                    }
                    sheet.Apply(); File.WriteAllBytes(Path.Combine(Evidence, name + "-" + angle + ".png"), sheet.EncodeToPNG());
                }
                finally
                {
                    camera.targetTexture = null; RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt); Object.Destroy(sheet); Object.Destroy(go);
                }
            }
        }
        Animator Reference(Animator source)
        {
            var go = Object.Instantiate(source.gameObject); temporary.Add(go); go.name = "PostureBaseReference";
            go.transform.position = new Vector3(1000, 0, 1000);
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true)) b.enabled = false;
            var a = go.GetComponent<Animator>(); a.cullingMode = AnimatorCullingMode.AlwaysAnimate; a.speed = 0;
            return a;
        }
        static float TorsoDifference(Animator actual, Animator reference)
        {
            for (int i = 1; i < reference.layerCount; i++) reference.SetLayerWeight(i, 0);
            var state = actual.GetCurrentAnimatorStateInfo(0);
            reference.SetFloat("Speed", actual.GetFloat("Speed"));
            reference.Play(state.fullPathHash, 0, state.normalizedTime); reference.Update(0);
            float difference = 0;
            foreach (var bone in Torso)
            {
                var a = actual.GetBoneTransform(bone); var b = reference.GetBoneTransform(bone);
                if (a != null && b != null) difference = Mathf.Max(difference, Quaternion.Angle(a.localRotation, b.localRotation));
            }
            return difference;
        }
        void Select(WorldEntity actor)
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = actor.transform.position + actor.transform.forward * 1.3f; cc.enabled = true;
            Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(actor.GetComponent<DialogueParticipant>()), Is.True);
        }
        bool Present(WorldEntity actor, string gesture = "none") =>
            actor.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(
                new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse, utterance = "这是姿态组合验收。", emotion = "happy", gesture = gesture },
                new CognitivePresentationContext(new CognitiveRequest { request_id = "posture-p0", trace_id = "posture-p0",
                    run = new CognitiveRunDto { run_id = "posture-p0", agent_entity_id = actor.EntityId } },
                    player.EntityId, dialogue.CurrentConversationId));

        [UnityTest] public IEnumerator FormalV2_IdleBeforeAfter_AndSpeechGestureRecovery()
        {
            Assert.That(actors.Length, Is.EqualTo(5));
            var world = Object.FindAnyObjectByType<WorldStateService>(); int revision = world.WorldRevision;
            var controllers = actors.Select(a => Rig(a).runtimeAnimatorController).ToArray();
            var relaxed = AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "ThirdParty/KayKit/Idle_Relaxed.anim");
            for (int i = 0; i < actors.Length; i++)
            {
                var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(actors[i] == player ?
                    "Assets/Pack_Character_LowPoly/Animations/Idle.anim" : Animations + "ThirdParty/KayKit/Idle_A.anim");
                var baseline = new AnimatorOverrideController(controllers[i]); temporary.Add(baseline);
                if (baseline.animationClips.Contains(relaxed)) baseline[relaxed] = old;
                var quiet = AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "ThirdParty/KayKit/Idle_Quiet.anim");
                foreach (var standingClip in baseline.animationClips.Where(c => c.name.StartsWith("Idle_Quiet")).Distinct().ToArray()) baseline[standingClip] = old;
                Rig(actors[i]).runtimeAnimatorController = baseline;
            }
            yield return Frames(190);
            foreach (var actor in actors) Record(Rig(actor), actor.EntityId, "before");
            Capture("formal-before", actors.Select(Rig).ToArray());
            for (int i = 0; i < actors.Length; i++)
            {
                actors[i].GetComponentInChildren<HeadGazeController>()?.ResetPresentation();
                Rig(actors[i]).runtimeAnimatorController = controllers[i]; Rig(actors[i]).Rebind();
            }
            for (int frame = 0; frame < 195; frame++)
            {
                yield return null;
                if (frame % 10 == 0) foreach (var actor in actors) Record(Rig(actor), actor.EntityId, "after");
            }
            Capture("formal-after", actors.Select(Rig).ToArray());
            foreach (var actor in actors.Where(a => a != player))
            {
                Select(actor); yield return Frames(3);
                Assert.That(actor.GetComponent<ExpressiveController>().State, Is.EqualTo(DialoguePresentationState.Listening));
                var a = Rig(actor); var reference = Reference(a);
                Assert.That(Present(actor), Is.True); yield return Frames(20);
                var talking = actor.GetComponentInChildren<DialogueAnimationPresenter>();
                Assert.That(talking.CurrentWeight, Is.GreaterThan(.9f));
                Assert.That(TorsoDifference(a, reference), Is.LessThan(.25f), actor.EntityId + " talking moved torso");
                Record(a, actor.EntityId, "talking");
                Assert.That(Present(actor, "wave"), Is.True); yield return Frames(20);
                Assert.That(actor.GetComponent<GestureController>().WaveWeight, Is.GreaterThan(.9f));
                Assert.That(talking.CurrentWeight, Is.LessThan(.01f));
                Assert.That(TorsoDifference(a, reference), Is.LessThan(.25f), actor.EntityId + " waving moved torso");
                Record(a, actor.EntityId, "wave");
                Capture("gesture-" + actor.EntityId, new[] { a });
                actor.GetComponent<MotionChannelCoordinator>().CancelGesture(); yield return Frames(20);
                Assert.That(actor.GetComponent<GestureController>().WaveWeight, Is.Zero.Within(.001f));
                Assert.That(Present(actor, "nod"), Is.True); yield return Frames(18);
                Assert.That(actor.GetComponent<GestureController>().IsNodActive, Is.True);
                Assert.That(TorsoDifference(a, reference), Is.LessThan(.25f), actor.EntityId + " nod moved torso");
                yield return Frames(45);
                Assert.That(actor.GetComponent<MotionChannelCoordinator>().ActiveGestureId, Is.Zero);
                dialogue.HideDialoguePresentation(); yield return Frames(20);
                Assert.That(talking.CurrentWeight, Is.Zero.Within(.001f));
                Assert.That(actor.GetComponent<ExpressiveController>().State, Is.EqualTo(DialoguePresentationState.Idle));
            }
            Assert.That(world.WorldRevision, Is.EqualTo(revision), "Presentation must not commit a world result.");
        }

        [UnityTest] public IEnumerator FiveHumanoids_ContinuousStandingAndWalkReturn()
        {
            var models = new[] { "Assets/CafeDemo/V2/Characters/薇薇.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/CafeDemo/V2/Characters/Ao.vrm", "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/VRMCompatibilityTest/Xingmang.vrm" };
            var rigs = new List<Animator>();
            for (int i = 0; i < models.Length; i++)
            {
                var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(models[i])); temporary.Add(go);
                go.transform.position = new Vector3(1000 + i * 3, 0, 1000);
                var a = go.GetComponent<Animator>();
                a.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Animations +
                    (i == 0 ? "CafeV2Expressive" : i == 4 ? "CafeV2Player" : "CafeExpressive") + ".controller");
                a.applyRootMotion = false; a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                Assert.That(a.avatar.isValid && a.isHuman, Is.True); rigs.Add(a);
            }
            for (int frame = 0; frame < 195; frame++)
            {
                yield return null;
                if (frame % 10 == 0) for (int i = 0; i < rigs.Count; i++) Record(rigs[i], models[i], "standing");
            }
            Capture("five-standing", rigs.ToArray());
            foreach (var a in rigs) a.SetFloat("Speed", 1);
            yield return Frames(85); Capture("five-walk", rigs.ToArray());
            foreach (var a in rigs) a.SetFloat("Speed", 0);
            yield return Frames(60);
            for (int i = 0; i < rigs.Count; i++)
            {
                Record(rigs[i], models[i], "return-idle");
                Assert.That(Vector3.Distance(rigs[i].transform.position, new Vector3(1000 + i * 3, 0, 1000)), Is.LessThan(.001f));
                Assert.That(Mathf.Max(Arm(rigs[i], true), Arm(rigs[i], false)), Is.LessThan(25), models[i] + " did not recover relaxed arms");
            }
            Capture("five-return-idle", rigs.ToArray());
        }

        [UnityTest] public IEnumerator PlayerEmotes_WaveTorsoIsolation_AndCancelRecovery()
        {
            foreach (var bridge in Object.FindObjectsByType<PlayerEmoteAgentEventBridge>(FindObjectsSortMode.None)) bridge.enabled = false;
            var emote = player.GetComponent<PlayerEmoteController>(); var a = Rig(player); var reference = Reference(a);
            foreach (string id in new[] { "wave", "cheer", "proud" })
            {
                Assert.That(emote.TryPlay(id), Is.True, id);
                float maximum = 0, armMotion = 0; var arm = a.GetBoneTransform(HumanBodyBones.RightUpperArm);
                Quaternion last = arm.localRotation;
                for (int frame = 0; frame < 65; frame++)
                {
                    yield return null;
                    armMotion += Quaternion.Angle(last, arm.localRotation); last = arm.localRotation;
                    if (frame > 12) maximum = Mathf.Max(maximum, TorsoDifference(a, reference));
                }
                Record(a, player.EntityId, id); Capture("player-" + id, new[] { a });
                File.WriteAllText(Path.Combine(Evidence, "player-" + id + "-torso.txt"), maximum.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
                emote.Cancel(); yield return Frames(20);
                Assert.That(emote.IsActive, Is.False);
                for (int layer = 1; layer < a.layerCount; layer++) Assert.That(a.GetLayerWeight(layer), Is.Zero.Within(.001f));
                Assert.That(armMotion, Is.GreaterThan(8), id + " must retain real arm motion");
                Assert.That(TorsoDifference(a, reference), Is.LessThan(.25f), id + " did not restore torso");
                Assert.That(Mathf.Max(Arm(a,true),Arm(a,false)),Is.LessThan(MaxRelaxedArmHang),id+" did not restore near-body Idle");
                AssertSagittalPose(StandingArmPose());
                if (id == "wave") Assert.That(maximum, Is.LessThan(.25f), "Wave must not override the relaxed base torso");
            }
        }

        [UnityTest] public IEnumerator FormalV2_ActualPlayerAndNavMeshMovement_StopRestoresIdle()
        {
            bool background = Application.runInBackground;
            var inputBackground = InputSystem.settings.backgroundBehavior;
            var editorInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            var keyboard = InputSystem.AddDevice<Keyboard>("PostureP0Keyboard");
            var orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>(); bool orbitEnabled = orbit.enabled;
            try
            {
                Application.runInBackground = true;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.EnableDevice(keyboard); orbit.enabled = false; Camera.main.transform.rotation = Quaternion.identity;
                var movement = player.GetComponent<CafeDemoPlayerController>();
                var view = (Transform)new SerializedObject(movement).FindProperty("view").objectReferenceValue;
                view.rotation = Quaternion.identity;
                Assert.That(NavMesh.SamplePosition(new Vector3(1.9f, 0, 4.5f), out NavMeshHit playerStart, .5f, NavMesh.AllAreas), Is.True);
                var control = player.GetComponent<CharacterController>(); control.enabled = false;
                player.transform.position = playerStart.position + Vector3.up * .03f; control.enabled = true; Physics.SyncTransforms();
                yield return Frames(20); var origin = player.transform.position;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); InputSystem.Update();
                var moveAction = (InputAction)typeof(CafeDemoPlayerController).GetField("move",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(movement);
                Debug.Log("[PostureMove] queued input=" + moveAction.ReadValue<Vector2>() + "; controls=" + string.Join(",", moveAction.controls.Select(c => c.path)));
                float peakSpeed = 0;
                for (int frame = 0; frame < 36; frame++)
                {
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); InputSystem.Update();
                    yield return null;
                    peakSpeed = Mathf.Max(peakSpeed, player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed);
                    if (frame == 18)
                    { Record(Rig(player), player.EntityId, "actual-walk"); Capture("actual-player-walk", new[] { Rig(player) }); }
                }
                Debug.Log("[PostureMove] W=" + keyboard.wKey.isPressed + "; suspended=" + movement.IsInputSuspended +
                    "; presentation=" + movement.IsPresentationInputSuppressed + "; speed=" + control.velocity + "; position=" + player.transform.position);
                Assert.That(Vector3.ProjectOnPlane(player.transform.position - origin, Vector3.up).magnitude, Is.GreaterThan(.25f));
                Assert.That(peakSpeed, Is.GreaterThan(.3f), "Real movement must drive the existing locomotion presenter before stopping at the wall.");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
                yield return Frames(60);
                Assert.That(player.GetComponent<PlayerLocomotionPresenter>().NormalizedSpeed, Is.LessThan(.02f));
                Assert.That(Mathf.Max(Arm(Rig(player), true), Arm(Rig(player), false)), Is.LessThan(MaxRelaxedArmHang));
                AssertSagittalPose(StandingArmPose());
                Record(Rig(player), player.EntityId, "actual-stop");

                var merchant = actors.Single(a => a.EntityId == "npc_cafe_01"); var agent = merchant.GetComponent<NavMeshAgent>();
                // Controlled path request only; task-authorized movement is covered by the existing bring-object test.
                merchant.GetComponent<EmbodiedNPC.Routines.NpcDefaultRoutineController>().enabled = false;
                merchant.GetComponent<EmbodiedNPC.Routines.ReturnToHomeRoutine>().enabled = false;
                var home = merchant.transform.position;
                foreach (var point in new[] { new Vector3(.9f, 0, 4.5f), home })
                {
                    Assert.That(NavMesh.SamplePosition(point, out NavMeshHit hit, .5f, agent.areaMask), Is.True);
                    agent.isStopped = false; Assert.That(agent.SetDestination(hit.position), Is.True);
                    float maxSpeed = 0; int frames = 0;
                    do
                    {
                        yield return null; frames++;
                        maxSpeed = Mathf.Max(maxSpeed, merchant.GetComponentInChildren<LocomotionPresenter>().NormalizedSpeed);
                    } while ((agent.pathPending || agent.remainingDistance > agent.stoppingDistance + .08f || agent.velocity.magnitude > .05f) && frames < 1200);
                    Assert.That(frames, Is.LessThan(1200)); Assert.That(agent.pathStatus, Is.EqualTo(NavMeshPathStatus.PathComplete));
                    Assert.That(maxSpeed, Is.GreaterThan(.3f)); yield return Frames(40);
                    Assert.That(merchant.GetComponentInChildren<LocomotionPresenter>().NormalizedSpeed, Is.LessThan(.03f));
                    Assert.That(Mathf.Max(Arm(Rig(merchant), true), Arm(Rig(merchant), false)), Is.LessThan(25));
                    Record(Rig(merchant), merchant.EntityId, "actual-nav-stop");
                }
                Capture("actual-merchant-return", new[] { Rig(merchant) });
            }
            finally
            {
                InputSystem.RemoveDevice(keyboard); InputSystem.settings.backgroundBehavior = inputBackground;
                InputSystem.settings.editorInputBehaviorInPlayMode = editorInputBehavior;
                Application.runInBackground = background; orbit.enabled = orbitEnabled;
            }
        }

        [Serializable] sealed class QuietFrame
        {
            public string actor, mode; public float time, hipsY, hipsZ, chestY, chestZ, armForward, armAngle, indexCurl, thumbGap, lean;
        }
        [Serializable] sealed class QuietRows { public QuietFrame[] rows; }
        QuietFrame QuietPose(WorldEntity actor, string mode)
        {
            var a = Rig(actor);
            Vector3 Local(HumanBodyBones bone) => a.transform.InverseTransformPoint(a.GetBoneTransform(bone).position);
            Vector3 hips = Local(HumanBodyBones.Hips), chest = Local(HumanBodyBones.Chest);
            float Forward(bool left) => Vector3.Dot(a.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position -
                a.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm).position, a.transform.forward);
            float Curl(bool left)
            {
                var x = Local(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
                var y = Local(left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate);
                var z = Local(left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
                return Vector3.Angle(y-x,z-y);
            }
            float Gap(bool left) => Vector3.Distance(Local(left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal),
                Local(left ? HumanBodyBones.LeftIndexIntermediate : HumanBodyBones.RightIndexIntermediate));
            return new QuietFrame {actor=actor.EntityId,mode=mode,time=Time.realtimeSinceStartup,hipsY=hips.y,hipsZ=hips.z,chestY=chest.y,chestZ=chest.z,
                armForward=(Forward(true)+Forward(false))*.5f,armAngle=(Arm(a,true)+Arm(a,false))*.5f,indexCurl=(Curl(true)+Curl(false))*.5f,
                thumbGap=(Gap(true)+Gap(false))*.5f,lean=Mathf.Atan2(chest.z-hips.z,chest.y-hips.y)*Mathf.Rad2Deg};
        }
        void CaptureHands(string name, Animator[] rigs)
        {
            const int size=480;
            var go=new GameObject("QuietHandEvidenceCamera");var cam=go.AddComponent<Camera>();cam.fieldOfView=37;cam.nearClipPlane=.005f;
            cam.cullingMask=1<<31;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.18f,.2f,.23f);
            var rt=RenderTexture.GetTemporary(size,size,24);var sheet=new Texture2D(size*rigs.Length*2,size,TextureFormat.RGB24,false);
            var previous=RenderTexture.active;
            try
            {
                cam.targetTexture=rt;
                for(int i=0;i<rigs.Length;i++) for(int side=0;side<2;side++)
                {
                    var a=rigs[i];bool left=side==0;
                    var hand=a.GetBoneTransform(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
                    var finger=a.GetBoneTransform(left?HumanBodyBones.LeftIndexDistal:HumanBodyBones.RightIndexDistal);
                    var center=(hand.position+finger.position)*.5f;
                    float scale=Vector3.Distance(a.GetBoneTransform(HumanBodyBones.Head).position,a.GetBoneTransform(HumanBodyBones.LeftFoot).position)/1.4f;
                    cam.transform.position=center+(a.transform.forward*.65f+a.transform.right*(left?-1:1)).normalized*.43f*scale;cam.transform.LookAt(center);
                    var objects=a.GetComponentsInChildren<Renderer>().Select(r=>r.gameObject).Distinct().ToArray();var layers=objects.Select(o=>o.layer).ToArray();
                    try {foreach(var o in objects)o.layer=31;cam.Render();RenderTexture.active=rt;sheet.ReadPixels(new Rect(0,0,size,size),(i*2+side)*size,0);}
                    finally {for(int j=0;j<objects.Length;j++)objects[j].layer=layers[j];}
                }
                sheet.Apply();File.WriteAllBytes(Path.Combine(Evidence,name+".png"),sheet.EncodeToPNG());
            }
            finally {cam.targetTexture=null;RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);Object.Destroy(sheet);Object.Destroy(go);}
        }
        [UnityTest] public IEnumerator FormalV2_QuietIdle_TenSeconds_ReducesSwayAndRelaxesHands()
        {
            var quiet=AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations+"ThirdParty/KayKit/Idle_Quiet.anim");
            var relaxed=AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations+"ThirdParty/KayKit/Idle_Relaxed.anim");
            Assert.That(quiet.isHumanMotion && quiet.isLooping,Is.True);Assert.That(quiet.length,Is.EqualTo(3.2f).Within(.002f));Assert.That(quiet.events,Is.Empty);
            var standing=new[]{actors.Single(a=>a.EntityId=="npc_cafe_01"),player};
            var controllers=standing.Select(a=>Rig(a).runtimeAnimatorController).ToArray();
            var positions=standing.Select(a=>a.transform.position).ToArray();
            var rows=new List<QuietFrame>();
            var world=Object.FindAnyObjectByType<WorldStateService>();int revision=world.WorldRevision;
            Time.captureDeltaTime=0;
            try
            {
                foreach(string mode in new[]{"before","quiet"})
                {
                    for(int i=0;i<standing.Length;i++)
                    {
                        var controller=new AnimatorOverrideController(controllers[i]);temporary.Add(controller);
                        if(mode=="before") foreach(var standingClip in controller.animationClips.Where(c=>c.name.StartsWith("Idle_Quiet")).Distinct().ToArray()) controller[standingClip]=relaxed;
                        Rig(standing[i]).runtimeAnimatorController=controller;Rig(standing[i]).Rebind();
                    }
                    yield return Frames(20);
                    Capture("quiet-"+mode,standing.Select(Rig).ToArray());CaptureHands("hands-"+mode,standing.Select(Rig).ToArray());
                    float start=Time.realtimeSinceStartup;
                    while(Time.realtimeSinceStartup-start<10f)
                    {
                        yield return null;
                        foreach(var actor in standing)rows.Add(QuietPose(actor,mode));
                    }
                    Capture("quiet-"+mode+"-10seconds",standing.Select(Rig).ToArray());
                }
                File.WriteAllText(Path.Combine(Evidence,"quiet-ten-seconds.json"),JsonUtility.ToJson(new QuietRows{rows=rows.ToArray()},true));
                foreach(var actor in standing)
                {
                    var before=rows.Where(x=>x.actor==actor.EntityId&&x.mode=="before").ToArray();
                    var after=rows.Where(x=>x.actor==actor.EntityId&&x.mode=="quiet").ToArray();
                    float Range(QuietFrame[] data,Func<QuietFrame,float> select)=>data.Max(select)-data.Min(select);
                    float oldSway=Range(before,x=>x.chestZ),newSway=Range(after,x=>x.chestZ);
                    Assert.That(before.Last().time-before.First().time,Is.GreaterThan(9.8f));Assert.That(after.Last().time-after.First().time,Is.GreaterThan(9.8f));
                    Assert.That(newSway,Is.LessThan(oldSway*.5f),actor.EntityId+" torso sway reduction");
                    Assert.That(newSway,Is.GreaterThan(.00005f),actor.EntityId+" should retain subtle life");
                    if(actor==player)
                        Assert.That(after.Max(x=>Mathf.Abs(x.armForward)),Is.LessThan(.025f),"Player wrists should hang beneath shoulders in side view");
                    else
                        Assert.That(after.Average(x=>x.armForward),Is.LessThan(before.Average(x=>x.armForward)-.015f),actor.EntityId+" arms still too far forward");
                    Assert.That(after.Average(x=>x.indexCurl),Is.GreaterThan(before.Average(x=>x.indexCurl)+5),actor.EntityId+" fingers not relaxed");
                    Assert.That(after.Average(x=>x.thumbGap),Is.LessThan(before.Average(x=>x.thumbGap)+.001f),actor.EntityId+" thumb moved further from index");
                    Assert.That(after.Max(x=>x.armAngle),Is.LessThan(25));Assert.That(Range(after,x=>x.hipsY),Is.LessThan(.006f));
                    Assert.That(after.Average(x=>x.lean),Is.InRange(-8f,12f),actor.EntityId+" new torso lean");
                }
                for(int i=0;i<standing.Length;i++)Assert.That(Vector3.Distance(positions[i],standing[i].transform.position),Is.LessThan(.015f));
                Assert.That(world.WorldRevision,Is.EqualTo(revision));
            }
            finally
            {
                for(int i=0;i<standing.Length;i++){Rig(standing[i]).runtimeAnimatorController=controllers[i];Rig(standing[i]).Rebind();}
            }
        }

        [Serializable] sealed class WaveFollowEvidence
        {
            public float sourceLeftMax, relaxedLeftMax, followRatio, mainRightErrorMax, torsoErrorMax;
            public int sampledFrames;
        }
        static float BoneDifference(Animator a, Animator b, HumanBodyBones[] bones)
        {
            float maximum=0;
            foreach(var bone in bones)maximum=Mathf.Max(maximum,Quaternion.Angle(a.GetBoneTransform(bone).localRotation,b.GetBoneTransform(bone).localRotation));
            return maximum;
        }
        [UnityTest] public IEnumerator PlayerWave_LeftFollowsIdleAtFifteenPercent_RightArmUnchanged()
        {
            foreach(var bridge in Object.FindObjectsByType<PlayerEmoteAgentEventBridge>(FindObjectsSortMode.None))bridge.enabled=false;
            var source=AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations+"ThirdParty/KayKit/Waving.anim");
            var relaxed=AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations+"ThirdParty/KayKit/Waving_PlayerRelaxed.anim");
            Assert.That(relaxed.isHumanMotion,Is.True);Assert.That(relaxed.length,Is.EqualTo(source.length).Within(.0001f));Assert.That(relaxed.events,Is.Empty);
            // The authored right arm, root, body and legs retain every source key/tangent.
            foreach(var binding in AnimationUtility.GetCurveBindings(source))
            {
                string property=binding.propertyName;
                if(property.StartsWith("Left Arm ")||property.StartsWith("Left Forearm ")||property.StartsWith("Left Hand ")||property.StartsWith("Left Shoulder ")||property.StartsWith("LeftHand."))continue;
                var oldKeys=AnimationUtility.GetEditorCurve(source,binding).keys;var newKeys=AnimationUtility.GetEditorCurve(relaxed,binding).keys;
                Assert.That(newKeys.Length,Is.EqualTo(oldKeys.Length),property);
                for(int k=0;k<oldKeys.Length;k++)
                {
                    Assert.That(newKeys[k].time,Is.EqualTo(oldKeys[k].time).Within(.000001f),property);
                    Assert.That(newKeys[k].value,Is.EqualTo(oldKeys[k].value).Within(.000001f),property);
                    Assert.That(newKeys[k].inTangent,Is.EqualTo(oldKeys[k].inTangent).Within(.000001f),property);
                    Assert.That(newKeys[k].outTangent,Is.EqualTo(oldKeys[k].outTangent).Within(.000001f),property);
                }
            }
            var a=Rig(player);var emote=player.GetComponent<PlayerEmoteController>();var baseRig=Reference(a);var sourceRig=Reference(a);
            var authored=new AnimatorOverrideController(a.runtimeAnimatorController);temporary.Add(authored);authored[relaxed]=source;
            sourceRig.runtimeAnimatorController=authored;sourceRig.Rebind();
            int layer=a.GetLayerIndex(PlayerEmoteController.WaveLayerName);Assert.That(layer,Is.GreaterThan(0));
            var left=new[]{HumanBodyBones.LeftShoulder,HumanBodyBones.LeftUpperArm,HumanBodyBones.LeftLowerArm,HumanBodyBones.LeftHand,
                HumanBodyBones.LeftThumbProximal,HumanBodyBones.LeftThumbIntermediate,HumanBodyBones.LeftThumbDistal,
                HumanBodyBones.LeftIndexProximal,HumanBodyBones.LeftIndexIntermediate,HumanBodyBones.LeftIndexDistal};
            var right=new[]{HumanBodyBones.RightShoulder,HumanBodyBones.RightUpperArm,HumanBodyBones.RightLowerArm,HumanBodyBones.RightHand,
                HumanBodyBones.RightThumbProximal,HumanBodyBones.RightIndexProximal,HumanBodyBones.RightIndexIntermediate};
            yield return Frames(5);Capture("wave15-idle-before",new[]{a},0f,90f,180f);
            Assert.That(emote.TryWave(),Is.True);var evidence=new WaveFollowEvidence();
            for(int frame=0;frame<190;frame++)
            {
                yield return null;
                var wave=a.GetCurrentAnimatorStateInfo(layer);float weight=a.GetLayerWeight(layer);
                if(!wave.IsName(PlayerEmoteController.WaveLayerName+".Wave")||weight<.9f||wave.normalizedTime>.92f)continue;
                evidence.torsoErrorMax=Mathf.Max(evidence.torsoErrorMax,TorsoDifference(a,baseRig));
                var locomotion=a.GetCurrentAnimatorStateInfo(0);sourceRig.SetFloat("Speed",a.GetFloat("Speed"));
                for(int i=1;i<sourceRig.layerCount;i++)sourceRig.SetLayerWeight(i,i==layer?weight:0);
                sourceRig.Play(locomotion.fullPathHash,0,locomotion.normalizedTime);
                sourceRig.Play(wave.fullPathHash,layer,wave.normalizedTime);sourceRig.Update(0);
                evidence.sourceLeftMax=Mathf.Max(evidence.sourceLeftMax,BoneDifference(sourceRig,baseRig,left));
                evidence.relaxedLeftMax=Mathf.Max(evidence.relaxedLeftMax,BoneDifference(a,baseRig,left));
                evidence.mainRightErrorMax=Mathf.Max(evidence.mainRightErrorMax,BoneDifference(a,sourceRig,right));
                evidence.sampledFrames++;
                if(frame==45)
                {
                    Capture("wave15-player",new[]{a},0f,90f,180f);Capture("wave15-source-reference",new[]{sourceRig},0f,90f,180f);
                }
            }
            evidence.followRatio=evidence.relaxedLeftMax/evidence.sourceLeftMax;
            File.WriteAllText(Path.Combine(Evidence,"wave-left-follow.json"),JsonUtility.ToJson(evidence,true));
            Assert.That(evidence.sampledFrames,Is.GreaterThan(50));Assert.That(evidence.sourceLeftMax,Is.GreaterThan(10),"Original non-wave arm deviation must be reproduced");
            Assert.That(evidence.followRatio,Is.InRange(.1f,.2f),"Non-wave arm should retain only 10-20% of original deviation from current Idle");
            Assert.That(evidence.mainRightErrorMax,Is.LessThan(.25f),"Main waving side changed");Assert.That(evidence.torsoErrorMax,Is.LessThan(.25f),"Wave moved torso");
            Assert.That(emote.IsActive,Is.False,"Full Wave must exit normally");
            for(int i=1;i<a.layerCount;i++)Assert.That(a.GetLayerWeight(i),Is.Zero.Within(.001f));
            TorsoDifference(a,baseRig); // Match the current Idle phase before comparing the restored hand.
            Assert.That(BoneDifference(a,baseRig,left),Is.LessThan(.3f),"Non-wave hand did not return to Idle");
            Assert.That(Mathf.Max(Arm(a,true),Arm(a,false)),Is.LessThan(MaxRelaxedArmHang),"Wave exit did not restore near-body Idle");
            AssertSagittalPose(StandingArmPose());
            Record(a,player.EntityId,"wave15-return");Capture("wave15-idle-after",new[]{a},0f,90f,180f);
        }

        [Serializable] sealed class NearBodyPose
        {
            public float time, leftUpperAbduction, rightUpperAbduction, leftElbowOut, rightElbowOut,
                leftHandThighGap, rightHandThighGap, leftHangAngle, rightHangAngle, indexCurl,
                leftWristForward, rightWristForward, leftElbowForward, rightElbowForward;
        }
        [Serializable] sealed class NearBodyRows { public NearBodyPose[] rows; }
        NearBodyPose StandingArmPose()
        {
            var a=Rig(player);
            Vector3 Bone(HumanBodyBones id)=>a.GetBoneTransform(id).position;
            float Forward(bool left,bool elbow)
            {
                var shoulder=Bone(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
                var endpoint=Bone(elbow?(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm):(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand));
                return Vector3.Dot(endpoint-shoulder,a.transform.forward);
            }
            float Upper(bool left)
            {
                var v=Bone(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm)-Bone(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm);
                return Mathf.Atan2(Mathf.Abs(Vector3.Dot(v,a.transform.right)),-Vector3.Dot(v,a.transform.up))*Mathf.Rad2Deg;
            }
            float Elbow(bool left)=>Mathf.Abs(Vector3.Dot(Bone(left?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm)-
                Bone(left?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm),a.transform.right));
            float Gap(bool left)
            {
                var hand=Bone(left?HumanBodyBones.LeftHand:HumanBodyBones.RightHand);
                var hip=Bone(left?HumanBodyBones.LeftUpperLeg:HumanBodyBones.RightUpperLeg);var knee=Bone(left?HumanBodyBones.LeftLowerLeg:HumanBodyBones.RightLowerLeg);
                float t=Mathf.Clamp01(Vector3.Dot(hip-hand,a.transform.up)/Vector3.Dot(hip-knee,a.transform.up));
                return Vector3.Dot(hand-Vector3.Lerp(hip,knee,t),a.transform.right)*(left?-1:1);
            }
            return new NearBodyPose {leftWristForward=Forward(true,false),rightWristForward=Forward(false,false),leftElbowForward=Forward(true,true),rightElbowForward=Forward(false,true),time=Time.time,leftUpperAbduction=Upper(true),rightUpperAbduction=Upper(false),
                leftElbowOut=Elbow(true),rightElbowOut=Elbow(false),leftHandThighGap=Gap(true),rightHandThighGap=Gap(false),
                leftHangAngle=Arm(a,true),rightHangAngle=Arm(a,false),indexCurl=QuietPose(player,"near-body").indexCurl};
        }
        static void AssertSagittalPose(NearBodyPose p)
        {
            Assert.That(p.leftWristForward,Is.InRange(-.025f,.025f),"Left wrist must hang beneath shoulder, not behind it");
            Assert.That(p.rightWristForward,Is.InRange(-.025f,.025f),"Right wrist must hang beneath shoulder, not behind it");
            Assert.That(p.leftElbowForward,Is.InRange(-.02f,.02f),"Left upper arm tilts backward/forward");
            Assert.That(p.rightElbowForward,Is.InRange(-.02f,.02f),"Right upper arm tilts backward/forward");
        }
        [UnityTest] public IEnumerator PlayerIdle_ArmsHangNearBody_FrontBackOblique()
        {
            var poses=new List<NearBodyPose>();yield return Frames(25);
            for(int frame=0;frame<220;frame++)
            {
                yield return null;
                if(frame%4==0)poses.Add(StandingArmPose());
                if(frame==100)
                {
                    Capture("near-body-idle",new[]{Rig(player)},0f,45f,90f,180f,270f);
                    CaptureHands("near-body-hands",new[]{Rig(player)});
                }
            }
            File.WriteAllText(Path.Combine(Evidence,"near-body-poses.json"),JsonUtility.ToJson(new NearBodyRows {rows=poses.ToArray()},true));
            Record(Rig(player),player.EntityId,"near-body-idle");
            Assert.That(poses.Max(x=>Mathf.Max(x.leftHangAngle,x.rightHangAngle)),Is.LessThan(MaxRelaxedArmHang),"Arms still form a visible V");
            Assert.That(poses.Min(x=>Mathf.Min(x.leftHangAngle,x.rightHangAngle)),Is.GreaterThan(3),"Keep a small relaxed clearance");
            Assert.That(poses.Max(x=>Mathf.Max(x.leftUpperAbduction,x.rightUpperAbduction)),Is.LessThan(12),"Upper arms remain abducted");
            Assert.That(poses.Max(x=>Mathf.Max(x.leftElbowOut,x.rightElbowOut)),Is.LessThan(.05f),"Elbows remain too far outside shoulders");
            Assert.That(poses.Min(x=>Mathf.Min(x.leftHandThighGap,x.rightHandThighGap)),Is.GreaterThan(.045f),"Wrist center too close to thigh centerline");
            Assert.That(poses.Max(x=>Mathf.Max(x.leftHandThighGap,x.rightHandThighGap)),Is.LessThan(.12f),"Hands remain too far from thighs");
            foreach(var pose in poses)AssertSagittalPose(pose);
            Assert.That(poses.Average(x=>x.indexCurl),Is.InRange(24,30),"Keep the accepted light finger curl");
        }
    }
}
