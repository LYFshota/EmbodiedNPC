using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeCarryTests
    {
        private WorldEntity merchant, player, cup, customer;
        private CarryPresenter carry;
        private CarryAnchorFollower follower;
        private Animator animator;
        private WorldStateService world;
        private Transform hold, grab;
        private HeldItemState held;
        [UnitySetUp] public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            // These scene tests inject decisions; they neither need a model nor own Backend lifetime.
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode();
            var entities = Object.FindObjectsByType<WorldEntity>();
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            player = entities.Single(e => e.EntityId == "player_01");
            cup = entities.Single(e => e.EntityId == "cup_01");
            customer = entities.Single(e => e.EntityId == "npc_cafe_regular_01");
            carry = merchant.GetComponent<CarryPresenter>();
            follower = merchant.GetComponentInChildren<CarryAnchorFollower>();
            animator = merchant.GetComponentInChildren<Animator>();
            world = Object.FindAnyObjectByType<WorldStateService>();
            hold = merchant.transform.Find("HandHoldPoint"); grab = cup.transform.Find("GrabPoint");
            held = cup.GetComponent<HeldItemState>();
            AnimationSceneTestIsolation.Ready();
            yield return null;
            float until = Time.realtimeSinceStartup + 5;
            while (merchant.GetComponent<AgentOrchestrator>().HasActiveRun && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(merchant.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
        }
        [UnityTearDown] public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private static IEnumerator Wait(float seconds)
        { float until = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < until) yield return null; }
        private void PickUp()
        {
            Assert.That(world.TryPickUp("er5-pickup", "er5", merchant, cup, held, hold, grab).Status, Is.EqualTo(SkillStatus.Succeeded));
        }
        private void Give()
        {
            var destinationHold = player.transform.Find("PlayerHoldPoint");
            Assert.That(world.TryTransferOwnership("er5-give", "er5", merchant, cup, player, held,
                destinationHold, destinationHold).Status, Is.EqualTo(SkillStatus.Succeeded));
            AssertStored();
        }

        private void AssertStored()
        {
            Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(cup.transform.parent, Is.SameAs(player.transform.Find("PlayerHoldPoint")));
            Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled), Is.True);
            Assert.That(cup.GetComponent<Collider>().enabled, Is.False);
            Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled, Is.False);
            Assert.That(cup.gameObject.activeInHierarchy && cup.isActiveAndEnabled && held.isActiveAndEnabled, Is.True);
            Assert.That(Object.FindAnyObjectByType<EntityRegistry>().TryGetEntity(cup.EntityId, out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(cup));
        }

        [Test] public void Assets_StandaloneLoopSeparateMaskAndExplicitSceneWiring()
        {
            const string path = "Assets/CafeDemo/Animations/ThirdParty/KayKit/Holding_B.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            Assert.That(clip.isHumanMotion && clip.isLooping, Is.True);
            Assert.That(clip.length, Is.EqualTo(1f).Within(.001f)); Assert.That(clip.events, Is.Empty);
            Assert.That(AssetDatabase.GetDependencies(path, true), Is.EqualTo(new[] { path }));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/CafeDemo/Animations/CafeExpressive.controller");
            Assert.That(controller.parameters.Select(p => p.name), Is.EqualTo(new[] { "Speed" }));
            Assert.That(controller.layers.Select(l => l.name), Is.EqualTo(new[] { "Base Layer", "Dialogue", "GestureUpperBody", CarryPresenter.LayerName }));
            var layer = controller.layers.Last();
            Assert.That(layer.defaultWeight, Is.Zero); Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override));
            // Carry may use the torso; conversational arms must not replace the base torso pose.
            Assert.That(layer.avatarMask, Is.Not.SameAs(controller.layers[1].avatarMask));
            Assert.That(controller.layers[1].avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Body), Is.False);
            Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Root), Is.False);
            Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Head), Is.False);
            Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg), Is.False);
            Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg), Is.False);
            foreach (var part in new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers })
                Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(part), Is.True);
            Assert.That(layer.stateMachine.defaultState.motion, Is.SameAs(clip));
            Assert.That(layer.stateMachine.defaultState.writeDefaultValues, Is.False);
            Assert.That(layer.stateMachine.defaultState.speed, Is.EqualTo(1));
            Assert.That(carry, Is.Not.Null); Assert.That(follower.RightHand, Is.SameAs(animator.GetBoneTransform(HumanBodyBones.RightHand)));
            Assert.That(new SerializedObject(merchant.GetComponent<GoalPlanRunner>()).FindProperty("actorHoldPoint").objectReferenceValue, Is.SameAs(hold));
            Assert.That(hold.parent, Is.SameAs(merchant.transform));
            Assert.That(customer.GetComponent<CarryPresenter>(), Is.Null);
            Assert.That(customer.GetComponentInChildren<CarryAnchorFollower>(), Is.Null);
            Assert.That(animator.applyRootMotion, Is.False);
        }

        [UnityTest] public IEnumerator Ownership_PickupGiveAndParentIsNotTruth()
        {
            carry.Tick(.2f); Assert.That(carry.IsCarrying, Is.False);
            int revision = world.WorldRevision;
            PickUp(); carry.Tick(.1f);
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 1));
            Assert.That(held.HolderEntityId, Is.EqualTo(merchant.EntityId)); Assert.That(cup.transform.parent, Is.SameAs(hold));
            Assert.That(carry.IsCarrying, Is.True); Assert.That(carry.CarriedItemId, Is.EqualTo(cup.EntityId));
            Assert.That(carry.TargetWeight, Is.EqualTo(1)); Assert.That(carry.CurrentWeight, Is.InRange(.01f, .99f));
            yield return Wait(.25f); Assert.That(carry.CurrentWeight, Is.EqualTo(1));
            Give(); carry.Tick(.1f);
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2)); Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(carry.TargetWeight, Is.Zero); Assert.That(carry.CurrentWeight, Is.InRange(.01f, .99f));
            cup.transform.SetParent(hold, true); // Deliberately misleading visual hierarchy; ownership remains Player.
            carry.Tick(.2f); Assert.That(carry.IsCarrying, Is.False); Assert.That(carry.CurrentWeight, Is.Zero);
            Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId));
        }

        [UnityTest] public IEnumerator Follower_FinalAnimatorPoseWithoutMovingActorRoot()
        {
            PickUp(); yield return Wait(.3f);
            Vector3 root = merchant.transform.position; Quaternion rotation = merchant.transform.rotation;
            var offset = new SerializedObject(follower).FindProperty("positionOffset").vector3Value;
            var angle = new SerializedObject(follower).FindProperty("rotationOffset").vector3Value;
            var hand = follower.RightHand;
            Vector3 first = hand.position;
            animator.Update(.4f); follower.FollowPose();
            Assert.That(Vector3.Distance(first, hand.position), Is.GreaterThan(.0001f));
            Assert.That(Vector3.Distance(hold.position, hand.TransformPoint(offset)), Is.LessThan(.0001f));
            Quaternion expectedRotation = hand.rotation * Quaternion.Euler(angle);
            if (new SerializedObject(follower).FindProperty("keepUpright").boolValue)
            {
                expectedRotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(expectedRotation * Vector3.forward, Vector3.up), Vector3.up);
                Assert.That(Vector3.Angle(hold.up, Vector3.up), Is.LessThan(.01f));
            }
            Assert.That(Quaternion.Angle(hold.rotation, expectedRotation), Is.LessThan(.01f));
            Assert.That(Vector3.Distance(grab.position, hold.position), Is.LessThan(.0001f));
            Assert.That(merchant.transform.position, Is.EqualTo(root)); Assert.That(merchant.transform.rotation, Is.EqualTo(rotation));
            follower.enabled = false; Vector3 frozen = hold.position;
            animator.Update(.2f); follower.FollowPose(); Assert.That(hold.position, Is.EqualTo(frozen));
            follower.enabled = true; follower.FollowPose();
            Assert.That(Vector3.Distance(hold.position, hand.TransformPoint(offset)), Is.LessThan(.0001f));
            follower.Configure(animator, merchant.transform, Vector3.zero, Vector3.zero);
            Assert.That(follower.IsReady, Is.False); follower.FollowPose(); Assert.That(merchant.transform.position, Is.EqualTo(root));
        }

        [UnityTest] public IEnumerator Disable_ClearsWeightAndReenableReadsTruthAgain()
        {
            PickUp(); yield return Wait(.3f); Assert.That(carry.CurrentWeight, Is.EqualTo(1));
            carry.enabled = false; Assert.That(carry.CurrentWeight, Is.Zero);
            Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(CarryPresenter.LayerName)), Is.Zero);
            Assert.That(held.HolderEntityId, Is.EqualTo(merchant.EntityId));
            carry.enabled = true; yield return Wait(.3f); Assert.That(carry.CurrentWeight, Is.EqualTo(1));
            Give(); yield return Wait(.3f); Assert.That(carry.CurrentWeight, Is.Zero);
        }

        [Test] public void MultipleItems_StableIdSelectionAndSingleWarning()
        {
            var registry = Object.FindAnyObjectByType<EntityRegistry>();
            var extra = new List<GameObject>();
            try
            {
                foreach (string id in new[] { "er5_z", "er5_a" })
                {
                    var go = new GameObject(id); extra.Add(go);
                    var entity = go.AddComponent<WorldEntity>(); entity.Configure(id, id, EntityKind.Object, AgentRole.None);
                    registry.Register(entity); var item = go.AddComponent<HeldItemState>();
                    Assert.That(world.TryPickUp(id, id, merchant, entity, item, hold, go.transform).Status, Is.EqualTo(SkillStatus.Succeeded));
                }
                LogAssert.Expect(LogType.Warning, new Regex("\\[Carry\\] Multiple held items.*er5_a"));
                carry.Tick(.1f); carry.Tick(.1f);
                Assert.That(carry.CarriedItemId, Is.EqualTo("er5_a"));
            }
            finally { foreach (var go in extra) { registry.Unregister(go.GetComponent<WorldEntity>()); Object.Destroy(go); } }
        }

        [Test] public void Projection_DoesNotWriteAuthorityNavigationVisionOrCognition()
        {
            PickUp();
            var agent = merchant.GetComponent<NavMeshAgent>(); var sensor = merchant.GetComponent<VisionSensor>();
            var state = merchant.GetComponent<PerceivedStateStore>(); var orchestrator = merchant.GetComponent<AgentOrchestrator>();
            Vector3 root = merchant.transform.position, destination = agent.destination, forward = sensor.transform.forward;
            Quaternion rotation = merchant.transform.rotation; int revision = world.WorldRevision, perception = state.Aggregator.PerceptionRevision;
            var visible = sensor.CurrentVisibleEntityIds.ToArray(); var run = orchestrator.CurrentRun;
            bool stopped = agent.isStopped, path = agent.hasPath, pending = agent.pathPending;
            carry.Tick(.2f); follower.FollowPose();
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); Assert.That(held.HolderEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(merchant.transform.position, Is.EqualTo(root)); Assert.That(merchant.transform.rotation, Is.EqualTo(rotation));
            Assert.That(agent.destination, Is.EqualTo(destination)); Assert.That(agent.isStopped, Is.EqualTo(stopped));
            Assert.That(agent.hasPath, Is.EqualTo(path)); Assert.That(agent.pathPending, Is.EqualTo(pending));
            Assert.That(sensor.transform.forward, Is.EqualTo(forward)); Assert.That(sensor.CurrentVisibleEntityIds, Is.EqualTo(visible));
            Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(perception)); Assert.That(orchestrator.CurrentRun, Is.SameAs(run));
            Assert.That(customer.GetComponentInChildren<Animator>().GetLayerWeight(animator.GetLayerIndex(CarryPresenter.LayerName)), Is.Zero);
        }

        [UnityTest] public IEnumerator AcceptedBringObject_ActualCarryWalkTurnGiveAndFormalSuppression()
        {
            var agent = merchant.GetComponent<NavMeshAgent>();
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = new Vector3(-2.2f,.03f,-.8f); cc.enabled = true; Physics.SyncTransforms();
            // Establish a real player event and owning request before consuming the bounded decision.
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = false;
            merchant.GetComponent<VisionSensor>().ScanNow();
            int revision = world.WorldRevision;
            GoalOutcome? outcome = null;
            merchant.GetComponent<GoalPlanRunner>().GoalOutcomePublished += value => outcome = value;
            var ev = new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance, player.EntityId, merchant.EntityId, "",
                new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "把这个杯子拿给我" }, Time.realtimeSinceStartupAsDouble, revision);
            Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(ev), Is.True);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            var response = new CognitiveResponse { message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.ActionProposal, utterance = "我去拿杯子。",
                    emotion = "neutral", gesture = "none", action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                        bindings = new Dictionary<string,string> { ["target"] = cup.EntityId, ["destination"] = player.EntityId } } },
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = cup.EntityId, destination_entity_id = player.EntityId } };
            Assert.That(driver.TryConsumeResponse(response), Is.True);
            var channels = merchant.GetComponent<MotionChannelCoordinator>();
            var loco = merchant.GetComponentInChildren<LocomotionPresenter>();
            int layer = animator.GetLayerIndex(CarryPresenter.LayerName), samples = 0;
            bool sawPickup = false, sawWalk = false, sawTurn = false;
            Quaternion firstCarryRotation = Quaternion.identity;
            float until = Time.realtimeSinceStartup + 30;
            string evidence = Path.Combine(Path.GetTempPath(), "embodiednpc-er5");
            bool capture = Directory.Exists(evidence) && File.Exists(Path.Combine(evidence, "capture.flag"));
            var camera = Camera.main;
            if (capture) camera.GetComponent<CafeDemoOrbitCamera>().enabled = false;
            var lines = new List<string>();
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < until)
            {
                if (held.HolderEntityId == merchant.EntityId)
                {
                    Assert.That(cup.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled), Is.True);
                    Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled, Is.True);
                    Assert.That(world.WorldRevision, Is.EqualTo(revision + 1));
                    Assert.That(cup.transform.parent, Is.SameAs(hold));
                    if (!sawPickup) { sawPickup = true; firstCarryRotation = merchant.transform.rotation; }
                    if (carry.CurrentWeight > .9f && loco.PlanarSpeed > .05f)
                    {
                        sawWalk = true;
                        Assert.That(animator.GetFloat("Speed"), Is.GreaterThan(0));
                        Assert.That(animator.GetCurrentAnimatorStateInfo(0).fullPathHash, Is.EqualTo(Animator.StringToHash("Base Layer.Locomotion")));
                        Assert.That(animator.GetCurrentAnimatorStateInfo(layer).fullPathHash, Is.EqualTo(Animator.StringToHash(CarryPresenter.LayerName + ".Carry")));
                        Assert.That(Quaternion.Angle(grab.rotation, hold.rotation), Is.LessThan(.01f));
                        Assert.That(Vector3.Distance(grab.position, hold.position), Is.LessThan(.0001f));
                        Assert.That(merchant.GetComponentInChildren<DialogueAnimationPresenter>().CurrentWeight, Is.Zero);
                        Assert.That(merchant.GetComponentInChildren<HeadGazeController>().CurrentWeight, Is.Zero);
                        Assert.That(channels.ActiveGestureId, Is.Zero);
                    }
                    sawTurn |= Quaternion.Angle(firstCarryRotation, merchant.transform.rotation) > 20;
                    samples++;
                }
                Assert.That(animator.applyRootMotion, Is.False);
                if (capture)
                {
                    camera.transform.position = merchant.transform.position + new Vector3(-1.8f,1.55f,-2.2f);
                    camera.transform.LookAt(merchant.transform.position + Vector3.up * .85f);
                    lines.Add($"{Time.realtimeSinceStartup:F3},{held.HolderEntityId},{world.WorldRevision},{carry.CurrentWeight:F3},{loco.PlanarSpeed:F3},{merchant.transform.position},{merchant.transform.eulerAngles.y:F2}");
                    if (samples > 0 && samples % 8 == 0)
                        ScreenCapture.CaptureScreenshot(Path.Combine(evidence, $"carry-{samples:D4}.png"));
                }
                yield return null;
            }
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(sawPickup && sawWalk && sawTurn, Is.True, $"pickup={sawPickup}, walk={sawWalk}, turn={sawTurn}");
            Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId)); Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            Assert.That(cup.transform.parent, Is.SameAs(player.transform.Find("PlayerHoldPoint")));
            AssertStored();
            yield return Wait(.4f); Assert.That(carry.CurrentWeight, Is.Zero); Assert.That(carry.IsCarrying, Is.False);
            Assert.That(outcome.HasValue, Is.True);
            Assert.That(outcome.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(animator.GetLayerWeight(layer), Is.Zero);
            if (capture)
            {
                File.WriteAllLines(Path.Combine(evidence, "bring-object-samples.csv"), lines);
                ScreenCapture.CaptureScreenshot(Path.Combine(evidence, "after-give.png"));
                yield return null;
            }
        }
    }
}
