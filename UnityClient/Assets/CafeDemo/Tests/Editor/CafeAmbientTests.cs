using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    [DefaultExecutionOrder(10000)]
    public sealed class AmbientGripContinuityProbe : MonoBehaviour
    {
        public CafeAmbientPresenter Presenter;
        public Transform Hand;
        private long instance;
        private Vector3 contactPosition;
        private Quaternion contactRotation;
        public int Samples { get; private set; }
        public float MaxPositionDrift { get; private set; }
        public float MaxRotationDrift { get; private set; }
        private void LateUpdate()
        {
            if (Presenter == null || !Presenter.IsCupAttached) return;
            Vector3 position = Hand.InverseTransformPoint(Presenter.Cup.position);
            Quaternion rotation = Quaternion.Inverse(Hand.rotation) * Presenter.Cup.rotation;
            if (instance != Presenter.Instance)
            { instance = Presenter.Instance; contactPosition = position; contactRotation = rotation; }
            Samples++;
            MaxPositionDrift = Mathf.Max(MaxPositionDrift, Vector3.Distance(contactPosition, position));
            MaxRotationDrift = Mathf.Max(MaxRotationDrift, Quaternion.Angle(contactRotation, rotation));
        }
    }
    public sealed class CafeAmbientAssetTests
    {
        [Test] public void DrinkIsHumanoidWithRealFingerCurvesAndNoBodyHeadRootOrLegs()
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/CafeDemo/Animations/CafeSeatedDrink.anim");
            Assert.That(clip.isHumanMotion, Is.True); Assert.That(clip.length, Is.EqualTo(6.2f).Within(.001f));
            Assert.That(clip.events, Is.Empty);
            var curves = AnimationUtility.GetCurveBindings(clip);
            Assert.That(curves.Any(x => x.propertyName == "RightHand.Index.2 Stretched"), Is.True);
            Assert.That(curves.All(x => x.type == typeof(Animator) && x.propertyName.StartsWith("Right")), Is.True);
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>("Assets/CafeDemo/Animations/CafeConversationArms.mask");
            foreach (var part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head,
                AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg }) Assert.That(mask.GetHumanoidBodyPartActive(part), Is.False);
        }
    }
    public sealed class AmbientLeaseTests
    {
        private GameObject actor;
        private MotionChannelCoordinator motion;
        private SpeechPresentationLifecycle a, b;
        [SetUp] public void Setup() { actor = new GameObject("lease"); motion = actor.AddComponent<MotionChannelCoordinator>();
            a = actor.AddComponent<SpeechPresentationLifecycle>(); b = new GameObject("other").AddComponent<SpeechPresentationLifecycle>(); }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(b.gameObject); Object.DestroyImmediate(actor); }
        [Test] public void ReplacementRejectsStaleCompletionAndCannotDoubleAcquire()
        {
            Assert.That(motion.TryBeginAmbient(a, "drink", AmbientBodyUse.Arms, out long old), Is.True);
            Assert.That(motion.TryBeginAmbient(b, "wipe", AmbientBodyUse.Arms, out _), Is.False);
            motion.CancelAmbient("dialogue"); motion.CancelAmbient("again");
            Assert.That(motion.TryBeginAmbient(b, "wipe", AmbientBodyUse.Arms, out long next), Is.True);
            Assert.That(motion.CompleteAmbient(a, old), Is.False);
            Assert.That(motion.OwnsAmbient(b, next), Is.True);
            Assert.That(motion.CompleteAmbient(b, next), Is.True);
            Assert.That(motion.CompleteAmbient(b, next), Is.False);
        }
        [TestCase("task")] [TestCase("interaction")] [TestCase("carry")] [TestCase("emergency")]
        public void FormalOccupancyRevokesBeforeObserver(string kind)
        {
            motion.TryBeginAmbient(a, "drink", AmbientBodyUse.Arms, out long id);
            long seen = -1; motion.AmbientEnded += (_, _) => seen = motion.ActiveAmbientId;
            if (kind == "task") motion.BeginTaskMotion();
            if (kind == "interaction") motion.SetInteractionActive(true);
            if (kind == "carry") motion.SetCarryPresentationActive(true);
            if (kind == "emergency") motion.BeginEmergencyMotion();
            Assert.That(seen, Is.Zero); Assert.That(motion.OwnsAmbient(a, id), Is.False);
            Assert.That(motion.TryBeginAmbient(a, "drink", AmbientBodyUse.Arms, out _), Is.False);
        }
        [Test] public void NodCoexistsWaveRevokesAndOwnerDisableExpires()
        {
            motion.TryBeginAmbient(a, "drink", AmbientBodyUse.Arms, out long id);
            Assert.That(motion.TryBeginGesture("nod", false, 0, 1, out long nod), Is.True);
            Assert.That(motion.OwnsAmbient(a, id), Is.True); motion.CompleteGesture(nod);
            Assert.That(motion.TryBeginGesture("wave", false, 0, 1, out long wave), Is.True);
            Assert.That(motion.ActiveAmbientId, Is.Zero); motion.CompleteGesture(wave);
            motion.TryBeginAmbient(a, "drink", AmbientBodyUse.Arms, out _);
            a.enabled = false; motion.Tick(1); Assert.That(motion.ActiveAmbientId, Is.Zero);
        }
    }

    public sealed class CafeAmbientTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        protected override bool EnableAmbient => true;
        protected override bool ClearSceneAfterTest => true;
        private DailyBehaviorController vivi, wen;
        private CafeAmbientPresenter drink;
        private WorldEntity companion;
        private NpcDefaultRoutineController admission;
        private DialogueController dialogue;
        private static string Evidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ?? Path.GetFullPath("../AmbientEvidence");
        private void Prepare(bool keepTaskChecks = false)
        {
            foreach (var npc in Object.FindObjectsByType<WorldEntity>().Where(x => x.IsAgent(AgentRole.Npc)))
            {
                npc.GetComponent<CognitiveRunDriver>().enabled = false;
                npc.GetComponent<AgentOrchestrator>().CancelCurrentRun();
                var vision = npc.GetComponent<VisionSensor>(); if (vision != null) vision.enabled = false;
                var bridge = npc.GetComponent<EmbodiedNPC.Events.VisualObservationAgentEventBridge>(); if (bridge != null) bridge.enabled = false;
                var source = npc.GetComponent<TaskConsiderationSource>(); if (source != null && !keepTaskChecks) source.enabled = false;
            }
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>()) trace.enabled = false;
            companion = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "npc_cafe_companion_01");
            vivi = merchant.GetComponent<DailyBehaviorController>(); wen = companion.GetComponent<DailyBehaviorController>();
            admission = merchant.GetComponent<NpcDefaultRoutineController>();
            drink = companion.GetComponent<CafeAmbientPresenter>();
            dialogue = Object.FindAnyObjectByType<DialogueController>(); dialogue.ClearSession("ambient_offline");
            admission.ReleaseEngagement(); companion.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            companion.GetComponent<SpeechPresentationLifecycle>().Cancel();
            foreach (var camera in Object.FindObjectsByType<CafeDemoOrbitCamera>()) camera.enabled = false;
            Place(new Vector3(1, .03f, -1.5f));
        }
        private static void AssertOffline()
        {
            foreach (var backend in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            { Assert.That(backend.ConnectionAttempts, Is.Zero); Assert.That(backend.CognitiveRequestSendCount, Is.Zero); Assert.That(backend.IsConnected, Is.False); }
        }
        private void AtWipePoint(string name = "AmbientCounterEast")
        {
            var point = GameObject.Find(name).transform;
            merchant.GetComponent<ReturnToHomeRoutine>().SuspendRoutine();
            var agent = merchant.GetComponent<NavMeshAgent>(); agent.ResetPath();
            Assert.That(agent.Warp(point.position), Is.True);
            merchant.GetComponent<MotionChannelCoordinator>().SetNavigationActive(false);
            merchant.transform.rotation = point.rotation;
        }
        private static void Capture(string name, Transform actor, bool side = false, bool work = false)
        {
            Directory.CreateDirectory(Evidence);
            var camera = Camera.main;
            camera.transform.position = actor.position + actor.forward * (side ? .45f : 1.55f) + actor.right * (side ? 1.6f : .7f) + Vector3.up * 1.25f;
            camera.transform.LookAt(actor.position + Vector3.up * .85f);
            camera.fieldOfView = 45;
            if (work)
            {
                camera.transform.position = actor.position - actor.forward * .3f + actor.right * .75f + Vector3.up * 1.9f;
                camera.transform.LookAt(actor.position + actor.forward * .4f + Vector3.up * .95f);
            }
            var old = camera.targetTexture; var rt = new RenderTexture(960, 720, 24); var previous = RenderTexture.active;
            try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                var image = new Texture2D(960, 720, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 960, 720), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(Evidence, name + ".png"), image.EncodeToPNG()); Object.Destroy(image); }
            finally { camera.targetTexture = old; RenderTexture.active = previous; rt.Release(); Object.Destroy(rt); }
        }
        [UnityTest] public IEnumerator Jiang_DrinkFitsHisAvatarAndRestoresHisOwnCup()
        {
            Prepare(); yield return Wait(.4f);
            var actor = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "npc_cafe_quiet_01");
            var daily = actor.GetComponent<DailyBehaviorController>(); var presenter = actor.GetComponent<CafeAmbientPresenter>();
            var rig = actor.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
            var cup = presenter.Cup; var parent = cup.parent; var position = cup.localPosition; var rotation = cup.localRotation;
            var root = actor.transform.position; var hips = rig.GetBoneTransform(HumanBodyBones.Hips); var hipPosition = hips.position;
            var foot = rig.GetBoneTransform(HumanBodyBones.RightFoot); var footPosition = foot.position;
            var hand = rig.GetBoneTransform(HumanBodyBones.RightHand);
            var probe = actor.gameObject.AddComponent<AmbientGripContinuityProbe>(); probe.Presenter = presenter; probe.Hand = hand;
            int revision = world.WorldRevision; bool attached = false;
            Assert.That(cup, Is.Not.SameAs(drink.Cup));
            Assert.That(rig.runtimeAnimatorController, Is.TypeOf<AnimatorOverrideController>());
            Capture("jiang-table-cup", actor.transform);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                Assert.That(daily.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True, daily.LastAmbientReason);
                string phase = null; long lease = daily.AmbientInstance; bool capturedHold = false;
                for (float end = Time.realtimeSinceStartup + 9; daily.AmbientInstance != 0 && Time.realtimeSinceStartup < end;)
                {
                    attached |= presenter.IsCupAttached;
                    if (cycle == 0 && phase != presenter.Phase)
                    { phase = presenter.Phase; Capture("jiang-" + phase, actor.transform); Capture("jiang-side-" + phase, actor.transform, true); }
                    Vector3 fingers = (rig.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position - hand.position).normalized;
                    if (presenter.Elapsed >= 1.1f && presenter.Elapsed < 1.3f)
                    {
                        Assert.That(Vector3.Dot(fingers, actor.transform.forward), Is.GreaterThan(.65f));
                        Assert.That(Mathf.Abs(fingers.y), Is.LessThan(.55f));
                    }
                    if (presenter.Elapsed >= 2.5f && presenter.Elapsed < 4)
                    {
                        if (cycle == 0 && !capturedHold && presenter.Elapsed >= 2.8f)
                        { capturedHold = true; Capture("jiang-sip-hold", actor.transform); Capture("jiang-side-sip-hold", actor.transform, true); }
                        Assert.That(Vector3.Angle(hand.position - rig.GetBoneTransform(HumanBodyBones.RightLowerArm).position, fingers), Is.LessThan(65));
                        Assert.That(cup.GetComponentsInChildren<Renderer>().Max(x => x.bounds.max.y) - rig.GetBoneTransform(HumanBodyBones.Head).position.y,
                            Is.LessThan(.06f), "The rim must not rise to eye level.");
                    }
                    Assert.That(rig.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.SeatedIdle"), Is.True);
                    Assert.That(Vector3.Distance(hips.position, hipPosition), Is.LessThan(.02f));
                    Assert.That(Vector3.Distance(foot.position, footPosition), Is.LessThan(.025f));
                    yield return null;
                }
                Assert.That(daily.AmbientInstance, Is.Zero); Assert.That(cup.parent, Is.EqualTo(parent));
                Assert.That(cup.localPosition, Is.EqualTo(position)); Assert.That(Quaternion.Angle(cup.localRotation, rotation), Is.LessThan(.01f));
                Assert.That(rig.GetLayerWeight(rig.GetLayerIndex("AmbientDrinkArms")), Is.Zero);
                Assert.That(presenter.TickAmbient(lease, 100), Is.False);
            }
            Assert.That(attached, Is.True); Assert.That(probe.Samples, Is.GreaterThan(20));
            Assert.That(probe.MaxPositionDrift, Is.LessThan(.0001f)); Assert.That(probe.MaxRotationDrift, Is.LessThan(.1f));
            Assert.That(actor.transform.position, Is.EqualTo(root)); Assert.That(world.WorldRevision, Is.EqualTo(revision));
            File.WriteAllText(Path.Combine(Evidence, "jiang-grip.txt"), $"samples={probe.Samples} positionDrift={probe.MaxPositionDrift:F7} rotationDrift={probe.MaxRotationDrift:F5}");
            AssertOffline();
        }
        [UnityTest] public IEnumerator Jiang_ConversationReturnsCupAndDisableRevokesIt()
        {
            Prepare(); yield return Wait(.4f);
            var actor = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "npc_cafe_quiet_01");
            var daily = actor.GetComponent<DailyBehaviorController>(); var presenter = actor.GetComponent<CafeAmbientPresenter>();
            var coordinator = actor.GetComponent<MotionChannelCoordinator>();
            var cup = presenter.Cup; var rest = cup.localPosition;
            foreach (float phase in new[] { .4f, 1.5f, 2.8f, 4.7f, 5.5f })
            {
                Assert.That(daily.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
                yield return Wait(phase); long lease = daily.AmbientInstance;
                Place(actor.transform.position + actor.transform.forward * 1.1f);
                Assert.That(dialogue.SelectTarget(actor.GetComponent<DialogueParticipant>()), Is.True);
                yield return null; yield return null;
                Assert.That(coordinator.IsAmbientRecovering, Is.True);
                float maxStep = 0; Vector3 previous = cup.position;
                for (float end = Time.realtimeSinceStartup + 3.5f; daily.AmbientInstance != 0 && Time.realtimeSinceStartup < end;)
                { maxStep = Mathf.Max(maxStep, Vector3.Distance(cup.position, previous)); previous = cup.position; yield return null; }
                Assert.That(daily.AmbientInstance, Is.Zero); Assert.That(maxStep, Is.LessThan(.06f));
                Assert.That(cup.localPosition, Is.EqualTo(rest)); Assert.That(presenter.TickAmbient(lease, 100), Is.False);
                dialogue.ClearSession("jiang_return_complete"); actor.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
                Place(new Vector3(1, .03f, -1.5f)); yield return Wait(.2f);
            }
            Assert.That(daily.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True); yield return Wait(2.8f);
            Assert.That(presenter.IsCupAttached, Is.True); actor.gameObject.SetActive(false); yield return null;
            Assert.That(cup != null && cup.gameObject.activeInHierarchy, Is.True); Assert.That(cup.localPosition, Is.EqualTo(rest));
            Assert.That(coordinator.ActiveAmbientId, Is.Zero); AssertOffline();
        }
        [UnityTest] public IEnumerator Jiang_AndWenScheduleDrinksIndependentlyInRealTime()
        {
            Prepare(); yield return Wait(.4f);
            var actor = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "npc_cafe_quiet_01");
            var daily = actor.GetComponent<DailyBehaviorController>();
            var wenStarts = new System.Collections.Generic.List<float>(); var jiangStarts = new System.Collections.Generic.List<float>();
            int wenCount = wen.AmbientStartedCount, jiangCount = daily.AmbientStartedCount;
            float began = Time.realtimeSinceStartup;
            for (float end = began + 205; Time.realtimeSinceStartup < end && (wenStarts.Count < 2 || jiangStarts.Count < 2);)
            {
                if (wen.AmbientStartedCount != wenCount) { wenCount = wen.AmbientStartedCount; wenStarts.Add(Time.realtimeSinceStartup - began); }
                if (daily.AmbientStartedCount != jiangCount) { jiangCount = daily.AmbientStartedCount; jiangStarts.Add(Time.realtimeSinceStartup - began); }
                yield return null;
            }
            File.WriteAllText(Path.Combine(Evidence, "independent-drink-timing.txt"),
                "wen=" + string.Join(",", wenStarts.Select(x => x.ToString("F2"))) + "\njiang=" + string.Join(",", jiangStarts.Select(x => x.ToString("F2"))));
            Assert.That(wenStarts.Count, Is.GreaterThanOrEqualTo(2)); Assert.That(jiangStarts.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(jiangStarts.Zip(wenStarts, (a,b) => Mathf.Abs(a-b)).Max(), Is.GreaterThan(3), "Independent schedules must not start in lockstep.");
            Assert.That(jiangStarts[1] - jiangStarts[0], Is.InRange(43.2f, 100)); AssertOffline();
        }
        [UnityTest] public IEnumerator Shen_CloseSideApproachUsesRealSeatCameraAndSensors()
        {
            Prepare(); yield return Wait(.3f);
            var attention = customer.GetComponentInChildren<AttentionController>();
            var gaze = customer.GetComponentInChildren<HeadGazeController>();
            var rig = customer.GetComponentInChildren<Animator>();
            // Keep the player inside the café. The wall-side mirror position is
            // inside Wall_Front and CharacterController pushes it outside the room.
            Place(customer.transform.position + customer.transform.forward * .9f - customer.transform.right * 1.3f);
            customer.GetComponent<VisionSensor>().enabled = true;
            var camera = Camera.main;
            camera.transform.position = customer.transform.position + customer.transform.forward * 3 + customer.transform.right * 3.3f + Vector3.up * 1.6f;
            camera.transform.LookAt(gaze.Head.position);
            Physics.SyncTransforms(); yield return Wait(1.2f);
            var head = rig.GetBoneTransform(HumanBodyBones.Head);
            var vision = customer.GetComponent<VisionSensor>();
            Vector3 origin = customer.transform.position + Vector3.up * vision.EyeHeight;
            Vector3 visibleTarget = player.GetComponent<PerceivableEntity>().VisualTargetPosition;
            Vector3 ray = visibleTarget - origin;
            string hits = string.Join(",", Physics.RaycastAll(origin, ray.normalized, ray.magnitude, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => !h.transform.IsChildOf(customer.transform)).Select(h => h.transform.name + ":" + h.distance.ToString("F3")));
            bool visible = vision.CurrentVisibleEntityIds.Contains("player_01");
            File.WriteAllText(Path.Combine(Evidence, "shen-side-approach.txt"),
                $"source={attention.Source} weight={gaze.CurrentWeight:F3} angles={gaze.CurrentAngles} player={player.transform.position:F3} npc={customer.transform.position:F3} camera={camera.transform.position:F3} head={head.position:F3} visible={visible} yaw={Vector3.Angle(customer.transform.forward, Vector3.ProjectOnPlane(ray, Vector3.up)):F2} origin={origin:F3} target={visibleTarget:F3} hits={hits}");
            Capture("shen-side-approach", customer.transform);
            Assert.That(attention.Source, Is.EqualTo(AttentionSource.CasualPlayer));
            Assert.That(gaze.CurrentWeight, Is.GreaterThan(.3f));
            Assert.That(Mathf.Abs(gaze.CurrentAngles.x), Is.GreaterThan(25)); AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_DefaultSchedulerWalksThenWipesWithBlockedTaskChecksEnabled()
        {
            Prepare(true); yield return Wait(.3f);
            var taskChecks = merchant.GetComponent<TaskConsiderationSource>();
            Assert.That(taskChecks.enabled, Is.True);
            merchant.GetComponent<VisionSensor>().enabled = true;
            Place(merchant.transform.position + merchant.transform.forward * 2.2f);
            Assert.That(vivi.Profile.AmbientInterval, Is.EqualTo(new Vector2(18, 38)));
            bool moved = false, wiped = false;
            float began = Time.realtimeSinceStartup;
            for (float until = began + 85; Time.realtimeSinceStartup < until;)
            {
                moved |= vivi.ActiveAmbientActivity == DailyAmbientActivity.CounterMove;
                if (vivi.ActiveAmbientActivity == DailyAmbientActivity.CounterWipe &&
                    merchant.GetComponent<CounterCleaningPresenter>().PropVisible)
                { wiped = true; break; }
                yield return null;
            }
            File.WriteAllText(Path.Combine(Evidence, "vivi-natural-scheduler.txt"),
                $"moved={moved} wiped={wiped} waited={Time.realtimeSinceStartup - began:F2} taskCheck={taskChecks.LastStatus} pendingCheck={taskChecks.HasPendingCheck} taskReservation={taskChecks.HasPendingOpportunity} mode={vivi.CurrentMode} activity={vivi.CurrentActivity} reason={vivi.LastAmbientReason} position={merchant.transform.position:F3}");
            Assert.That(moved, Is.True); Assert.That(wiped, Is.True, "Authored default scheduling must reach real wiping without test-starting it.");
            Assert.That(taskChecks.HasPendingCheck, Is.True, "Retain the blocked formal check, but do not let it starve ambient.");
            Assert.That(taskChecks.HasPendingOpportunity, Is.False);
            yield return Wait(2); Capture("vivi-naturally-triggered-wipe", merchant.transform, true, true);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().PropVisible, Is.True); AssertOffline();
        }
        [UnityTest] public IEnumerator Wen_CompleteDrinkTwiceKeepsSeatFeetAndWorld()
        {
            Prepare(); yield return Wait(.3f);
            var cup = drink.Cup; var parent = cup.parent; var position = cup.localPosition; var rotation = cup.localRotation;
            var rig = companion.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
            var gripProbe = companion.gameObject.AddComponent<AmbientGripContinuityProbe>();
            gripProbe.Presenter = drink; gripProbe.Hand = rig.GetBoneTransform(HumanBodyBones.RightHand);
            var hips = rig.GetBoneTransform(HumanBodyBones.Hips); var hipPosition = hips.position;
            var foot = rig.GetBoneTransform(HumanBodyBones.RightFoot).position;
            var root = companion.transform.position; int revision = world.WorldRevision; bool attached = false;
            for (int loop = 0; loop < 2; loop++)
            {
                Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True, wen.LastAmbientReason);
                long id = wen.AmbientInstance; string phase = null; int frame = 0;
                float until = Time.realtimeSinceStartup + 9;
                while (wen.AmbientInstance != 0 && Time.realtimeSinceStartup < until)
                {
                    attached |= drink.IsCupAttached;
                    if (loop == 0 && phase != drink.Phase)
                    { phase = drink.Phase; Capture("wen-" + phase, companion.transform); Capture("wen-side-" + phase, companion.transform, true); }
                    if (loop == 0 && drink.Elapsed >= frame * .25f)
                    {
                        Capture("wen-motion-" + frame.ToString("D2"), companion.transform, true);
                        File.AppendAllText(Path.Combine(Evidence,"drink-timing.txt"), $"frame={frame} elapsed={drink.Elapsed:F3} clip={rig.GetCurrentAnimatorStateInfo(rig.GetLayerIndex("AmbientDrinkArms")).normalizedTime * 6.2f:F3} head={rig.GetBoneTransform(HumanBodyBones.Head).position:F3} hand={rig.GetBoneTransform(HumanBodyBones.RightHand).position:F3} cup={cup.position:F3}\n");
                        frame++;
                    }
                    Assert.That(rig.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.SeatedIdle"), Is.True);
                    Vector3 fingers = (rig.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position -
                        rig.GetBoneTransform(HumanBodyBones.RightHand).position).normalized;
                    if (drink.Elapsed >= 1.1f && drink.Elapsed <= 1.3f)
                    {
                        // Observe the actual retargeted hand, not merely the authored curves.
                        Assert.That(Vector3.Dot(fingers, companion.transform.forward), Is.GreaterThan(.65f), "Contact must wrap the cup side rather than point fingers upward.");
                        Assert.That(Mathf.Abs(fingers.y), Is.LessThan(.55f));
                    }
                    if (drink.Elapsed >= 2.5f && drink.Elapsed <= 4)
                    {
                        Vector3 forearm = rig.GetBoneTransform(HumanBodyBones.RightHand).position -
                            rig.GetBoneTransform(HumanBodyBones.RightLowerArm).position;
                        Assert.That(Vector3.Angle(forearm, fingers), Is.LessThan(65), "The lifted wrist must follow the forearm without folding back.");
                    }
                    Assert.That(companion.GetComponent<PerceptionContextProvider>().HasAssignedFixedSeat, Is.True);
                    Assert.That(Vector3.Distance(hips.position, hipPosition), Is.LessThan(.02f));
                    Assert.That(Vector3.Distance(rig.GetBoneTransform(HumanBodyBones.RightFoot).position, foot), Is.LessThan(.025f));
                    yield return null;
                }
                Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(drink.Instance, Is.Zero);
                Assert.That(cup.parent, Is.EqualTo(parent)); Assert.That(cup.localPosition, Is.EqualTo(position));
                Assert.That(Quaternion.Angle(cup.localRotation, rotation), Is.LessThan(.01f));
                Assert.That(rig.GetLayerWeight(rig.GetLayerIndex("AmbientDrinkArms")), Is.Zero);
                Assert.That(drink.TickAmbient(id, 3), Is.False);
            }
            Assert.That(attached, Is.True); Assert.That(companion.transform.position, Is.EqualTo(root));
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
            Assert.That(gripProbe.Samples, Is.GreaterThan(20));
            Assert.That(gripProbe.MaxPositionDrift, Is.LessThan(.0001f), "Cup slipped relative to the actual evaluated hand.");
            Assert.That(gripProbe.MaxRotationDrift, Is.LessThan(.1f), "Cup rotated independently of the held grip.");
            File.WriteAllText(Path.Combine(Evidence, "grip-continuity.txt"),
                $"samples={gripProbe.Samples} positionDriftMeters={gripProbe.MaxPositionDrift:F7} rotationDriftDegrees={gripProbe.MaxRotationDrift:F5}");
        }
        [UnityTest] public IEnumerator Wen_InterruptEveryPhaseRejectsLateWritesAndCleansDisable()
        {
            Prepare(); yield return Wait(.3f);
            var cup = drink.Cup; var parent = cup.parent; var position = cup.localPosition;
            foreach (float seconds in new[] { .4f, 1.4f, 2.8f, 4.7f, 5.5f })
            {
                Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
                long old = wen.AmbientInstance;
                float end = Time.realtimeSinceStartup + 9;
                while (drink.Elapsed < seconds && Time.realtimeSinceStartup < end) yield return null;
                Assert.That(drink.Elapsed, Is.GreaterThanOrEqualTo(seconds));
                companion.GetComponent<MotionChannelCoordinator>().BeginTaskMotion();
                Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(cup.parent, Is.EqualTo(parent));
                Assert.That(cup.localPosition, Is.EqualTo(position)); Assert.That(drink.TickAmbient(old, 3), Is.False);
                companion.GetComponent<MotionChannelCoordinator>().EndTaskMotion();
                Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
                long current = wen.AmbientInstance; drink.EndAmbient(old);
                Assert.That(drink.Instance, Is.EqualTo(current)); wen.StopAmbient("reset");
            }
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True); yield return Wait(1.5f);
            drink.enabled = false; Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(cup.parent, Is.EqualTo(parent));
            drink.enabled = true; yield return null;
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True); yield return Wait(1.5f);
            companion.gameObject.SetActive(false); Assert.That(cup.parent, Is.EqualTo(parent));
            Assert.That(cup.localPosition, Is.EqualTo(position)); AssertOffline();
        }
        [UnityTest] public IEnumerator Wen_NodCoexistsWaveAndConversationRevokeWithoutModeChange()
        {
            Prepare(); yield return Wait(.3f);
            var motion = companion.GetComponent<MotionChannelCoordinator>();
            var gesture = companion.GetComponent<GestureController>();
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
            long id = wen.AmbientInstance; motion.RequestGesture("nod", false); yield return Wait(.2f);
            Assert.That(wen.AmbientInstance, Is.EqualTo(id)); yield return Wait(.8f);
            motion.RequestGesture("wave", false); Assert.That(wen.AmbientInstance, Is.Zero);
            yield return Wait(2.6f);
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
            yield return Wait(1.5f); long conversationId = wen.AmbientInstance;
            Place(companion.transform.position + companion.transform.forward * 1.2f);
            Assert.That(dialogue.SelectTarget(companion.GetComponent<DialogueParticipant>()), Is.True);
            yield return null; yield return null;
            Assert.That(wen.AmbientInstance, Is.EqualTo(conversationId)); Assert.That(motion.IsAmbientRecovering, Is.True);
            for (float end = Time.realtimeSinceStartup + 3; wen.AmbientInstance != 0 && Time.realtimeSinceStartup < end;) yield return null;
            Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(wen.CurrentMode, Is.EqualTo(DailyBehaviorMode.Leisure)); AssertOffline();
        }
        [UnityTest] public IEnumerator Wen_ConversationReturnsFromEveryPhaseAndTaskPreemptsRecovery()
        {
            Prepare(); yield return Wait(.3f);
            var motion = companion.GetComponent<MotionChannelCoordinator>();
            var cup = drink.Cup; Vector3 rest = cup.localPosition;
            float largestStep = 0;
            foreach (float phase in new[] { .4f, 1.4f, 2.8f, 4.7f, 5.5f })
            {
                Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
                long id = wen.AmbientInstance;
                for (float until = Time.realtimeSinceStartup + 8; drink.Elapsed < phase && Time.realtimeSinceStartup < until;) yield return null;
                Vector3 lastCup = cup.position; motion.RequestAmbientEnd("conversation");
                Assert.That(motion.ActiveAmbientId, Is.EqualTo(id));
                Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.False);
                int frame = 0; float nextCapture = Time.time;
                for (float until = Time.realtimeSinceStartup + 3.5f; wen.AmbientInstance != 0 && Time.realtimeSinceStartup < until;)
                {
                    if (phase == 2.8f && Time.time >= nextCapture)
                    { Capture("wen-interrupt-" + (frame++).ToString("D2"), companion.transform, true); nextCapture += .1f; }
                    yield return null;
                    largestStep = Mathf.Max(largestStep, Vector3.Distance(lastCup, cup.position)); lastCup = cup.position;
                }
                Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(cup.localPosition, Is.EqualTo(rest));
                Assert.That(drink.TickAmbient(id, .5f), Is.False);
            }
            Assert.That(largestStep, Is.LessThan(.06f), "Conversation return teleported the held cup.");
            File.WriteAllText(Path.Combine(Evidence, "recovery-motion.txt"), "largestCupFrameStep=" + largestStep);
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True); yield return Wait(2.7f);
            motion.RequestAmbientEnd("conversation"); Assert.That(motion.IsAmbientRecovering, Is.True);
            motion.BeginTaskMotion(); Assert.That(wen.AmbientInstance, Is.Zero); Assert.That(cup.localPosition, Is.EqualTo(rest));
            Assert.That(motion.IsAmbientRecovering, Is.False); motion.EndTaskMotion(); AssertOffline();
        }
        [UnityTest] public IEnumerator SpeechIsNotConversationAndSceneUnloadReleasesCup()
        {
            Prepare(); yield return Wait(1.2f);
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True);
            long id = wen.AmbientInstance;
            companion.GetComponent<SpeechPresentationLifecycle>().BeginPlayback("只是动作说明，不是玩家交谈");
            yield return Wait(1.5f);
            Assert.That(wen.AmbientInstance, Is.EqualTo(id));
            Assert.That(companion.GetComponentInChildren<DialogueAnimationPresenter>().CurrentWeight, Is.Zero);
            Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True);
            merchant.GetComponent<SpeechPresentationLifecycle>().BeginPlayback("看看另一边工作台");
            var agent = merchant.GetComponent<NavMeshAgent>(); bool walked = false;
            for (float end = Time.realtimeSinceStartup + 2; Time.realtimeSinceStartup < end;)
            { walked |= agent.velocity.sqrMagnitude > .01f; yield return null; }
            Assert.That(walked, Is.True); Assert.That(vivi.AmbientInstance, Is.Not.Zero);
            vivi.StopAmbient("reset"); wen.StopAmbient("reset");
            companion.GetComponent<SpeechPresentationLifecycle>().Cancel();
            Assert.That(wen.TryStartAmbient(DailyAmbientActivity.SeatedDrink), Is.True); yield return Wait(1.5f);
            var cup = drink.Cup; var parent = cup.parent;
            var motion = companion.GetComponent<MotionChannelCoordinator>();
            var temporary = SceneManager.CreateScene("AmbientUnloadAcceptance");
            companion.transform.SetParent(null, true);
            SceneManager.MoveGameObjectToScene(companion.gameObject, temporary);
            AssertOffline(); yield return SceneManager.UnloadSceneAsync(temporary);
            Assert.That(cup != null, Is.True); Assert.That(cup.parent, Is.EqualTo(parent));
            Assert.That(cup.parent.name, Is.EqualTo("DailyBehaviorAnchors"));
            Assert.That(motion == null, Is.True);
        }
        [UnityTest] public IEnumerator Vivi_ConversationPausesTravelThenContinuesToSameWorkPoint()
        {
            Prepare(); yield return Wait(1.2f);
            AtWipePoint("AmbientCounterEast");
            typeof(DailyBehaviorController).GetField("ambientWorkPoints", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(vivi, new[] { new DailyAmbientWorkPoint { point = GameObject.Find("AmbientCounterWest").transform, allowWipe = true } });
            var coordinator = merchant.GetComponent<MotionChannelCoordinator>();
            var routine = merchant.GetComponent<ReturnToHomeRoutine>();
            var agent = merchant.GetComponent<NavMeshAgent>();
            Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True);
            long lease = vivi.AmbientInstance; Vector3 target = routine.DailyDestination;
            for (float end = Time.realtimeSinceStartup + 5; agent.velocity.sqrMagnitude < .01f && Time.realtimeSinceStartup < end;) yield return null;
            yield return Wait(.6f);
            Place(merchant.transform.position - merchant.transform.forward * 1.1f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(.3f); Vector3 paused = merchant.transform.position;
            Assert.That(coordinator.IsAmbientNavigationPaused, Is.True);
            Assert.That(routine.OwnsPath, Is.False);
            yield return Wait(2);
            Assert.That(Vector3.Distance(paused, merchant.transform.position), Is.LessThan(.025f));
            Assert.That(vivi.AmbientInstance, Is.EqualTo(lease));
            Capture("vivi-travel-paused", merchant.transform);
            dialogue.ClearSession("resume_travel"); admission.ReleaseEngagement(); Place(new Vector3(1, .03f, -1.5f));
            bool resumed = false;
            for (float end = Time.realtimeSinceStartup + 25; vivi.AmbientInstance == lease && Time.realtimeSinceStartup < end;)
            {
                if (!coordinator.IsAmbientNavigationPaused)
                {
                    resumed = true;
                    Assert.That(Vector3.Distance(routine.DailyDestination, target), Is.LessThan(.001f), "Do not fall back to the previous idle point.");
                }
                yield return null;
            }
            Assert.That(resumed, Is.True); Assert.That(vivi.LastAmbientReason, Is.EqualTo("completed"));
            Assert.That(Vector3.ProjectOnPlane(merchant.transform.position - target, Vector3.up).magnitude, Is.LessThan(.21f));
            for (float end = Time.realtimeSinceStartup + 5; vivi.ActiveAmbientActivity != DailyAmbientActivity.CounterWipe && Time.realtimeSinceStartup < end;) yield return null;
            Assert.That(vivi.ActiveAmbientActivity, Is.EqualTo(DailyAmbientActivity.CounterWipe));
            yield return Wait(1); Capture("vivi-resumed-at-original-target", merchant.transform, true, true);
            AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_PausedTravelStillRevokesOnFormalTaskModeDisableAndEmergency()
        {
            Prepare(); yield return Wait(1.2f);
            var coordinator = merchant.GetComponent<MotionChannelCoordinator>();
            var routine = merchant.GetComponent<ReturnToHomeRoutine>();
            var runner = merchant.GetComponent<GoalPlanRunner>();
            for (int cause = 0; cause < 4; cause++)
            {
                AtWipePoint("AmbientCounterEast");
                Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True);
                long lease = vivi.AmbientInstance;
                Place(merchant.transform.position - merchant.transform.forward * 1.1f);
                Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
                yield return Wait(.2f); Assert.That(coordinator.IsAmbientNavigationPaused, Is.True);
                if (cause == 0)
                {
                    var goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
                    typeof(GoalProposal).GetField("RequestingPlayerId", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(goal, "player_01");
                    Assert.That(runner.TrySubmitGoal(goal, "paused-travel", "paused-travel", 1, world.WorldRevision, "player_01"), Is.True);
                }
                else if (cause == 1) coordinator.BeginEmergencyMotion();
                else if (cause == 2) vivi.enabled = false;
                else vivi.AmbientEnabled = false;
                Assert.That(coordinator.ActiveAmbientId, Is.Zero); Assert.That(coordinator.IsAmbientNavigationPaused, Is.False);
                Assert.That(coordinator.ResumeAmbientNavigation(merchant.GetComponent<CafeAmbientPresenter>(), lease), Is.False);
                Assert.That(merchant.GetComponent<CafeAmbientPresenter>().TickAmbient(lease, 100), Is.False);
                dialogue.ClearSession("paused_travel_cancelled"); admission.ReleaseEngagement(); Place(new Vector3(1, .03f, -1.5f));
                if (cause == 0)
                {
                    for (float end = Time.realtimeSinceStartup + 20; runner.HasActiveTask && Time.realtimeSinceStartup < end;) yield return null;
                    Assert.That(runner.HasActiveTask, Is.False);
                    merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); admission.ReleaseEngagement();
                }
                coordinator.EndEmergencyMotion(); vivi.enabled = true; vivi.AmbientEnabled = true;
                yield return Wait(1.2f);
            }
            // A changed daily mode invalidates the old move as soon as dialogue releases it.
            AtWipePoint(); Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True);
            long old = vivi.AmbientInstance;
            Place(merchant.transform.position - merchant.transform.forward * 1.1f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(.2f);
            merchant.GetComponent<DailyBehaviorDemoTrigger>().RequestMode(DailyBehaviorMode.Rest);
            dialogue.ClearSession("mode_changed"); admission.ReleaseEngagement(); Place(new Vector3(1, .03f, -1.5f));
            yield return Wait(1.3f);
            Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(vivi.AmbientInstance, Is.Zero);
            Assert.That(coordinator.ResumeAmbientNavigation(merchant.GetComponent<CafeAmbientPresenter>(), old), Is.False);
            AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_WipeConversationStopsWithoutChangingModeOrWorld()
        {
            Prepare(); yield return Wait(1.2f);
            AtWipePoint();
            Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterWipe), Is.True);
            yield return Wait(.7f);
            Place(merchant.transform.position - Vector3.forward * 1.1f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            int revision = world.WorldRevision; yield return null; yield return null;
            Assert.That(merchant.GetComponent<MotionChannelCoordinator>().IsAmbientRecovering, Is.True);
            yield return Wait(.9f);
            Assert.That(vivi.AmbientInstance, Is.Zero);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().IsPlaying, Is.False);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().PropVisible, Is.False);
            Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }
        [UnityTest] public IEnumerator IdleScheduler_RandomCooldownRunsRealActivitiesWithoutCognition()
        {
            Prepare(); yield return Wait(1.2f);
            // Observe production timing: no direct TryStart calls and no time-scale acceleration.
            int revision = world.WorldRevision; int first = vivi.AmbientStartedCount;
            float end = Time.realtimeSinceStartup + 100;
            while (Time.realtimeSinceStartup < end) yield return null;
            Assert.That(vivi.AmbientStartedCount - first, Is.GreaterThanOrEqualTo(2));
            Assert.That(vivi.AmbientStartedCount - first, Is.LessThanOrEqualTo(5));
            Assert.That(wen.AmbientCompletedCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            File.WriteAllText(Path.Combine(Evidence, "idle-observation.txt"),
                $"100 real seconds. Vivi started={vivi.AmbientStartedCount}, completed={vivi.AmbientCompletedCount}; Wen started={wen.AmbientStartedCount}, completed={wen.AmbientCompletedCount}; world_revision={world.WorldRevision}; backend requests=0.");
            AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_MultipleWorkPointsWipeAndModeChanges()
        {
            Prepare(); yield return Wait(1.2f);
            var routine = merchant.GetComponent<ReturnToHomeRoutine>(); var source = merchant.GetComponent<DailyBehaviorDemoTrigger>();
            var agent = merchant.GetComponent<NavMeshAgent>(); var initial = merchant.transform.position; int revision = world.WorldRevision;
            for (int i = 0; i < 3; i++)
            {
                Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True, vivi.LastAmbientReason);
                float until = Time.realtimeSinceStartup + 16; bool moved = false; float minimumBackZ = float.PositiveInfinity;
                while (vivi.AmbientInstance != 0 && Time.realtimeSinceStartup < until)
                {
                    moved |= agent.velocity.sqrMagnitude > .01f;
                    var position = merchant.transform.position;
                    if (position.x >= -1.85f) minimumBackZ = Mathf.Min(minimumBackZ, position.z);
                    Assert.That(position.z > 4.3f || position.x < -1.85f, Is.True, "Counter route entered the central aisle at " + position);
                    Assert.That(agent.isOnNavMesh, Is.True);
                    yield return null;
                }
                File.AppendAllText(Path.Combine(Evidence,"navigation-margin.txt"), $"move={i} minimum_back_z={minimumBackZ:F3}\n");
                Assert.That(vivi.AmbientInstance, Is.Zero); Assert.That(moved, Is.True);
                Capture("vivi-point-" + i, merchant.transform);
            }
            AtWipePoint(); Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterWipe), Is.True);
            yield return Wait(.9f); Capture("vivi-wipe", merchant.transform, true, true);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().IsPlaying, Is.True);
            source.RequestMode(DailyBehaviorMode.Rest); yield return Wait(.2f);
            Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(vivi.AmbientInstance, Is.Zero);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().PropVisible, Is.False);
            source.RequestMode(DailyBehaviorMode.Work); source.RequestMode(DailyBehaviorMode.Rest); source.RequestMode(DailyBehaviorMode.Work);
            yield return Wait(1.4f); Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_LegalModeChangeKeepsInstanceInvalidBindingCancels()
        {
            Prepare(); yield return Wait(1.2f);
            AtWipePoint();
            Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterWipe), Is.True);
            var profile = Object.Instantiate(vivi.Profile);
            profile.ConfigureAmbient(new[] { new DailyAmbientBinding(DailyBehaviorMode.Work, DailyAmbientActivity.CounterWipe),
                new DailyAmbientBinding(DailyBehaviorMode.Rest, DailyAmbientActivity.CounterWipe) }, new Vector2(20, 30), new Vector2(3, 4));
            typeof(DailyBehaviorController).GetField("profile", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(vivi, profile);
            long id = vivi.AmbientInstance;
            merchant.GetComponent<DailyBehaviorDemoTrigger>().RequestMode(DailyBehaviorMode.Rest); yield return Wait(.15f);
            Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(vivi.AmbientInstance, Is.EqualTo(id));
            profile.ConfigureAmbient(Array.Empty<DailyAmbientBinding>(), new Vector2(20, 30), new Vector2(3, 4));
            yield return null; Assert.That(vivi.AmbientInstance, Is.Zero); Object.Destroy(profile); AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_TwoClearSurfacesKeepHandsOnCounterAndDoNotCommitCleaning()
        {
            Prepare(); yield return Wait(1.2f); int revision = world.WorldRevision;
            var rig = merchant.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
            var cloth = merchant.GetComponent<CounterCleaningPresenter>().AmbientRag;
            var restPosition = cloth.localPosition; var restRotation = cloth.localRotation;
            foreach (string point in new[] { "AmbientCounterEast", "AmbientCounterWest" })
            {
                AtWipePoint(point);
                Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterWipe), Is.True);
                yield return Wait(1.1f); Capture("vivi-wipe-" + point, merchant.transform, true, true);
                var hand = rig.GetBoneTransform(HumanBodyBones.RightHand);
                var finger = rig.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
                var rag = merchant.GetComponent<CounterCleaningPresenter>().AmbientRag;
                var leftHand = rig.GetBoneTransform(HumanBodyBones.LeftHand);
                var hips = rig.GetBoneTransform(HumanBodyBones.Hips);
                File.AppendAllText(Path.Combine(Evidence,"wipe-free-hand.txt"), point + " left=" + leftHand.position.ToString("F3") + " hips=" + hips.position.ToString("F3") + "\n");
                Assert.That(leftHand.position.y - hips.position.y, Is.LessThan(.12f), "The free hand should hang beside the body, not float above the counter.");
                Assert.That(Vector3.Dot(leftHand.position - hips.position, merchant.transform.forward), Is.LessThan(.22f));
                Capture("vivi-relaxed-free-arm-" + point, merchant.transform);
                File.AppendAllText(Path.Combine(Evidence,"wipe-contact.txt"), point + " actor=" + merchant.transform.position.ToString("F3") + " wrist=" + hand.position.ToString("F3") + " finger=" + finger.position.ToString("F3") + " cloth=" + rag.position.ToString("F3") + "\n");
                Assert.That(finger.position.y, Is.InRange(1.02f, 1.075f));
                Assert.That(rag.position.y, Is.InRange(1.0f, 1.055f));
                Assert.That(rag.position.z, Is.InRange(point.EndsWith("West") ? 2.95f : 3.69f, point.EndsWith("West") ? 3.4f : 4.27f));
                yield return Wait(3);
                Assert.That(leftHand.position.y - hips.position.y, Is.LessThan(.12f));
                Assert.That(vivi.AmbientInstance, Is.Not.Zero, "Wiping should continue beyond one short clip.");
                Assert.That(merchant.GetComponent<CounterCleaningPresenter>().PropVisible, Is.True);
                Assert.That(finger.position.y, Is.InRange(1.02f, 1.075f));
                Capture("vivi-sustained-" + point, merchant.transform, true, true);
                for (float until = Time.realtimeSinceStartup + 10; vivi.AmbientInstance != 0 && Time.realtimeSinceStartup < until;) yield return null;
                Assert.That(vivi.AmbientInstance, Is.Zero);
                Assert.That(cloth.localPosition, Is.EqualTo(restPosition));
                Assert.That(Quaternion.Angle(cloth.localRotation, restRotation), Is.LessThan(.01f));
            }
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }
        [UnityTest] public IEnumerator Vivi_TaskPreemptsMoveAndDeferredModeResumesAfterTask()
        {
            Prepare(); yield return Wait(1.2f);
            Assert.That(vivi.TryStartAmbient(DailyAmbientActivity.CounterMove), Is.True); yield return Wait(.3f);
            long old = vivi.AmbientInstance;
            var goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
            typeof(GoalProposal).GetField("RequestingPlayerId", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(goal, "player_01");
            var runner = merchant.GetComponent<GoalPlanRunner>();
            Assert.That(runner.TrySubmitGoal(goal, "ambient-task", "ambient-task", 1, world.WorldRevision, "player_01"), Is.True);
            Assert.That(vivi.AmbientInstance, Is.Zero);
            var source = merchant.GetComponent<DailyBehaviorDemoTrigger>(); source.RequestMode(DailyBehaviorMode.Rest);
            yield return Wait(.15f); Assert.That(runner.HasActiveTask, Is.True);
            Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work), "Existing mode semantics defer while formal task owns motion.");
            float until = Time.realtimeSinceStartup + 20;
            while (runner.HasActiveTask && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(runner.HasActiveTask, Is.False);
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); admission.ReleaseEngagement();
            yield return Wait(1.3f); Assert.That(vivi.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(merchant.GetComponent<CafeAmbientPresenter>().TickAmbient(old, 10), Is.False);
            Assert.That(world.GetDrinkCount("player_01", "latte"), Is.EqualTo(1)); AssertOffline();
        }
    }
}
