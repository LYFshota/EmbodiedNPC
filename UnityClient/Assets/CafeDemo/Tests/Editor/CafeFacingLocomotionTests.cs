using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeFacingLocomotionTests
    {
        private WorldEntity merchant, customer, player;
        private DialogueController dialogue;
        [Test] public void CafeWalkAssets_AreStandaloneHumanoidAndMerchantOnly()
        {
            const string clipPath = "Assets/CafeDemo/Animations/ThirdParty/Quaternius/Walk_Loop.anim";
            const string controllerPath = "Assets/CafeDemo/Animations/CafeExpressive.controller";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            Assert.That(clip, Is.Not.Null); Assert.That(clip.isHumanMotion, Is.True);
            Assert.That(clip.legacy, Is.False); Assert.That(clip.isLooping, Is.True);
            Assert.That(clip.events, Is.Empty); Assert.That(clip.averageSpeed.sqrMagnitude, Is.LessThan(.0001f));
            Assert.That(AssetDatabase.GetDependencies(clipPath, true), Is.EqualTo(new[] { clipPath }));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            Assert.That(controller.parameters.Single().name, Is.EqualTo("Speed"));
            Assert.That(controller.parameters.Single().type, Is.EqualTo(AnimatorControllerParameterType.Float));
            var tree = controller.layers[0].stateMachine.defaultState.motion as BlendTree;
            Assert.That(tree, Is.Not.Null); Assert.That(tree.blendType, Is.EqualTo(BlendTreeType.Simple1D));
            Assert.That(tree.blendParameter, Is.EqualTo("Speed"));
            Assert.That(tree.children.Select(c => c.motion.name), Is.EqualTo(new[] { "Idle", "Walk_Loop" }));
            var merchantPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/Characters/NPCVisual.prefab");
            var merchantAnimator = merchantPrefab.GetComponentInChildren<Animator>();
            Assert.That(merchantAnimator.runtimeAnimatorController, Is.SameAs(controller));
            Assert.That(merchantAnimator.applyRootMotion, Is.False);
            Assert.That(merchantAnimator.avatar.isHuman && merchantAnimator.avatar.isValid, Is.True);
            var regularPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/Characters/RegularCustomerVisual.prefab");
            Assert.That(regularPrefab.GetComponentInChildren<Animator>().runtimeAnimatorController, Is.SameAs(controller));
            Assert.That(regularPrefab.GetComponentInChildren<LocomotionPresenter>(), Is.Null);
            Assert.That(regularPrefab.GetComponentInChildren<NavMeshAgent>(), Is.Null);
        }
        [UnitySetUp] public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            yield return new EnterPlayMode();
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.ApplyStartupConfig(new ModelConfigView { configured = true }); settings.Close();
            var entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            customer = entities.Single(e => e.EntityId == "npc_cafe_regular_01");
            player = entities.Single(e => e.EntityId == "player_01");
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            float until = Time.realtimeSinceStartup + 10;
            while ((merchant.GetComponent<AgentOrchestrator>().HasActiveRun || customer.GetComponent<AgentOrchestrator>().HasActiveRun) &&
                   Time.realtimeSinceStartup < until) yield return null;
            Assert.That(merchant.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
            Assert.That(customer.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
        }
        [UnityTearDown] public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private void MovePlayer(Vector3 position)
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = position; cc.enabled = true; Physics.SyncTransforms();
        }

        [UnityTest] public IEnumerator AttentionPriority_CurrentEvidenceAndNpcIsolation()
        {
            var owner = customer;
            var attention = owner.GetComponentInChildren<AttentionController>();
            var other = merchant.GetComponentInChildren<AttentionController>();
            var expression = owner.GetComponent<ExpressiveController>();
            var state = owner.GetComponent<PerceivedStateStore>();
            var cup = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e => e.EntityId == "cup_01");
            MovePlayer(owner.transform.position + owner.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
            yield return null;
            attention.Tick(.01f); Assert.That(attention.Source, Is.EqualTo(AttentionSource.Dialogue));
            var beforeOther = other.Source;
            var request = new CognitiveRequest { request_id = "gaze", trace_id = "gaze", run = new CognitiveRunDto
                { run_id = "gaze", agent_entity_id = owner.EntityId }, context = new CognitiveContextSnapshot
                { perception = new PerceptionSnapshotDto { entities = new[] { new PerceivedEntityDto { entity_id = cup.EntityId } } } } };
            var context = new CognitivePresentationContext(request, player.EntityId, dialogue.CurrentConversationId);
            var decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse, utterance = "The cup is over there.", look_at = cup.EntityId };
            Assert.That(owner.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(decision, context), Is.True);
            // Request-time allowance alone is insufficient: no current observer evidence => dialogue fallback.
            state.ApplyOrRefresh(new Observation("no-cup", owner.EntityId, ObservationSourceType.ContinuousScan,
                "gaze-cup", ObservationChannel.Visual, cup.EntityId,
                new Dictionary<string, string> { ["visibility_state"] = "lost", ["proximity_state"] = "out_of_range" },
                null, 1, 0, 1, 100));
            attention.Tick(.01f); Assert.That(attention.Source, Is.EqualTo(AttentionSource.Dialogue));
            state.ApplyOrRefresh(new Observation("visible-cup", owner.EntityId, ObservationSourceType.ContinuousScan,
                "gaze-cup", ObservationChannel.Visual, cup.EntityId,
                new Dictionary<string, string> { ["visibility_state"] = "current_visible" }, null, 1, 0, 2, 100));
            attention.Tick(.01f); Assert.That(attention.Source, Is.EqualTo(AttentionSource.ExplicitLookAt));
            Assert.That(attention.CurrentTargetEntityId, Is.EqualTo(cup.EntityId));
            Assert.That(other.Source, Is.EqualTo(beforeOther));
            expression.Speech.Tick(Time.unscaledTimeAsDouble + 10); attention.Tick(.01f);
            Assert.That(attention.Source, Is.EqualTo(AttentionSource.Dialogue));
            dialogue.HideDialoguePresentation(); attention.Tick(.01f);
            Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.Dialogue));
            dialogue.ShowDialoguePresentation(); attention.Tick(.01f);
            Assert.That(attention.Source, Is.EqualTo(AttentionSource.Dialogue));
            dialogue.ClearSession(); attention.Tick(.01f);
            Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.Dialogue));
            attention.enabled = false; Assert.That(attention.CurrentTargetEntityId, Is.Null);
        }

        [UnityTest] public IEnumerator BothHumanoidRigs_AddToCurrentPose_RestoreWithoutDriftOrPerceptionEffects()
        {
            foreach (var owner in new[] { merchant, customer })
            {
                var gaze = owner.GetComponentInChildren<HeadGazeController>();
                var attention = owner.GetComponentInChildren<AttentionController>();
                var animator = owner.GetComponentInChildren<Animator>();
                var facing = owner.GetComponentInChildren<FacingController>();
                var sensor = owner.GetComponent<VisionSensor>();
                var state = owner.GetComponent<PerceivedStateStore>();
                Assert.That(gaze.IsRigReady, Is.True); Assert.That(gaze.Neck, Is.Not.Null);
                MovePlayer(owner.transform.position + Quaternion.AngleAxis(20, Vector3.up) * owner.transform.forward * 1.3f);
                Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
                yield return null;
                attention.Tick(.01f); gaze.ResetPresentation();
                var headPose = gaze.Head.localRotation; var neckPose = gaze.Neck.localRotation;
                var rootRotation = owner.transform.rotation; var bodyRotation = facing.Visual.localRotation;
                var forward = sensor.transform.forward; sensor.ScanNow();
                int revision = state.Aggregator.PerceptionRevision;
                var visible = sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray();
                gaze.Tick(.05f); Assert.That(gaze.CurrentWeight, Is.InRange(.01f, .21f));
                for (int i = 0; i < 100; i++) gaze.Tick(.01f);
                Assert.That(Quaternion.Angle(headPose, gaze.Head.localRotation), Is.LessThan(55));
                Assert.That(owner.transform.rotation, Is.EqualTo(rootRotation));
                Assert.That(facing.Visual.localRotation, Is.EqualTo(bodyRotation));
                Assert.That(sensor.transform.forward, Is.EqualTo(forward));
                sensor.ScanNow(); Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(revision));
                Assert.That(sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray(), Is.EqualTo(visible));
                gaze.enabled = false;
                Assert.That(Quaternion.Angle(gaze.Head.localRotation, headPose), Is.LessThan(.05f));
                Assert.That(Quaternion.Angle(gaze.Neck.localRotation, neckPose), Is.LessThan(.05f));
                gaze.enabled = true;
                if (owner == merchant) animator.SetFloat("Speed", 1);
                animator.Update(.17f); var fresh = gaze.Head.localRotation;
                gaze.Tick(1); gaze.ResetPresentation();
                Assert.That(Quaternion.Angle(gaze.Head.localRotation, fresh), Is.LessThan(.05f), "Restore current animation, not an authored T-pose");
                gaze.Tick(1); owner.gameObject.SetActive(false);
                Assert.That(gaze.CurrentWeight, Is.Zero);
                Assert.That(attention.CurrentTargetEntityId, Is.Null);
                Assert.That(Quaternion.Angle(gaze.Head.localRotation, fresh), Is.LessThan(.05f));
                owner.gameObject.SetActive(true);
                if (owner == merchant) animator.SetFloat("Speed", 0);
                attention.enabled = false; gaze.Tick(1); Assert.That(gaze.CurrentWeight, Is.Zero);
                attention.enabled = true; dialogue.ClearSession();
                yield return null;
            }
        }

        [UnityTest] public IEnumerator BothNpcDialogue_UsesAuthorizedSpeaker_ReturnsNeutralAndDoesNotRotatePerception()
        {
            Assert.That(customer.GetComponent<NavMeshAgent>(), Is.Null);
            Assert.That(customer.GetComponent<GoalPlanRunner>(), Is.Null);
            Assert.That(customer.GetComponent<CognitiveActionHandoff>(), Is.Null);
            Assert.That(customer.GetComponentInChildren<LocomotionPresenter>(), Is.Null);
            foreach (var owner in new[] { merchant, customer })
            {
                var facing = owner.GetComponentInChildren<FacingController>();
                var sensor = owner.GetComponent<VisionSensor>();
                var state = owner.GetComponent<PerceivedStateStore>();
                var animator = owner.GetComponentInChildren<Animator>();
                Assert.That(facing.Visual, Is.Not.SameAs(owner.transform));
                Assert.That(animator.applyRootMotion, Is.False);
                MovePlayer(owner.transform.position + owner.transform.right * 1.2f);
                Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
                Assert.That(dialogue.ActiveSpeakerEntityId, Is.EqualTo(player.EntityId));
                yield return null; // The formal session reaches the presentation owner.
                facing.ResetPresentation();
                // Re-resolve the existing session through the owner, without injecting a target.
                yield return null;
                Quaternion rootRotation = owner.transform.rotation;
                Vector3 logicalForward = sensor.transform.forward;
                sensor.ScanNow(); string[] visible = sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray();
                int revision = state.Aggregator.PerceptionRevision;
                facing.Tick(.1f);
                Assert.That(facing.IsDialogueFacingActive, Is.True);
                Assert.That(Quaternion.Angle(facing.NeutralLocalRotation, facing.Visual.localRotation), Is.GreaterThan(1));
                Assert.That(Quaternion.Angle(facing.NeutralLocalRotation, facing.Visual.localRotation), Is.LessThanOrEqualTo(55.01));
                Assert.That(owner.transform.rotation, Is.EqualTo(rootRotation));
                Assert.That(sensor.transform, Is.SameAs(owner.transform));
                Assert.That(sensor.transform.forward, Is.EqualTo(logicalForward));
                Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(revision), "Facing alone publishes no observation");
                // Same-frame scan: no world/time movement can explain a changed FOV or revision.
                sensor.ScanNow();
                Assert.That(sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray(), Is.EqualTo(visible));
                Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(revision));
                dialogue.HideDialoguePresentation(); facing.Tick(1);
                Assert.That(facing.IsDialogueFacingActive, Is.False);
                Assert.That(Quaternion.Angle(facing.NeutralLocalRotation, facing.Visual.localRotation), Is.LessThan(.01));
                dialogue.ShowDialoguePresentation(); yield return null;
                Assert.That(facing.IsDialogueFacingActive, Is.True);
                player.GetComponent<WorldEntity>().enabled = false; yield return null; facing.Tick(1);
                Assert.That(facing.IsDialogueFacingActive, Is.False);
                player.GetComponent<WorldEntity>().enabled = true;
                dialogue.ClearSession(); yield return null;
                Assert.That(dialogue.ActiveSpeakerEntityId, Is.Null);
                owner.gameObject.SetActive(false);
                Assert.That(facing.Visual.localRotation, Is.EqualTo(facing.NeutralLocalRotation));
                owner.gameObject.SetActive(true);
            }
        }

        [UnityTest] public IEnumerator AcceptedBringObject_ProjectsRealSpeed_YieldsFacingAndReacquiresAfterOutcome()
        {
            var facing = merchant.GetComponentInChildren<FacingController>();
            var loco = merchant.GetComponentInChildren<LocomotionPresenter>();
            var agent = merchant.GetComponent<NavMeshAgent>();
            var channels = merchant.GetComponent<MotionChannelCoordinator>();
            var cup = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e => e.EntityId == "cup_01");
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            MovePlayer(merchant.transform.position + merchant.transform.right * 1.2f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return null;
            merchant.GetComponent<VisionSensor>().ScanNow();
            Assert.That(merchant.GetComponent<VisionSensor>().CurrentVisibleEntityIds, Does.Contain("cup_01"));
            Assert.That(facing.IsDialogueFacingActive, Is.True);
            bool updateRotation = agent.updateRotation;
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = false;
            var world = Object.FindAnyObjectByType<WorldStateService>(); int revision = world.WorldRevision;
            var ev = new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance, player.EntityId, merchant.EntityId, "",
                new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "把这个杯子拿给我" }, Time.realtimeSinceStartupAsDouble, revision);
            Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(ev), Is.True);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            var response = new CognitiveResponse { message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.ActionProposal, utterance = "我去拿杯子。",
                    emotion = "neutral", gesture = "none", action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                        bindings = new Dictionary<string,string> { ["target"] = "cup_01", ["destination"] = player.EntityId } } },
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = player.EntityId } };
            var run = merchant.GetComponent<AgentOrchestrator>().CurrentRun;
            Assert.That(driver.TryConsumeResponse(response), Is.True);
            facing.Tick(.1f); Assert.That(facing.IsDialogueFacingActive, Is.False);
            var animator = merchant.GetComponentInChildren<Animator>();
            var attention = merchant.GetComponentInChildren<AttentionController>();
            float maxSpeed = 0, maxAnimatorSpeed = 0; bool sawNavigation = false, testedDisableAndDialogue = false;
            float until = Time.realtimeSinceStartup + 30;
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < until)
            {
                loco.Tick(Time.deltaTime); facing.Tick(Time.deltaTime);
                float realSpeed = Vector3.ProjectOnPlane(agent.velocity, Vector3.up).magnitude;
                Assert.That(loco.PlanarSpeed, Is.EqualTo(realSpeed).Within(.001));
                Assert.That(loco.NormalizedSpeed, Is.InRange(0f, 1f));
                maxSpeed = Mathf.Max(maxSpeed, loco.NormalizedSpeed); sawNavigation |= loco.NavigationActive;
                maxAnimatorSpeed = Mathf.Max(maxAnimatorSpeed, animator.GetFloat("Speed"));
                Assert.That(channels.IsNavigationActive, Is.EqualTo(loco.NavigationActive));
                Assert.That(facing.IsDialogueFacingActive, Is.False, "Action retains body ownership");
                attention.Tick(Time.deltaTime); Assert.That(attention.HasGazePoint, Is.False, "Formal action suppresses ordinary gaze");
                Assert.That(agent.updateRotation, Is.EqualTo(updateRotation));
                if (loco.NavigationActive && !testedDisableAndDialogue)
                {
                    testedDisableAndDialogue = true;
                    dialogue.ClearSession();
                    Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
                    Assert.That(driver.HasPendingActionOutcome, Is.True, "Opening dialogue does not interrupt the action");
                    loco.enabled = false;
                    Assert.That(channels.IsNavigationActive, Is.False);
                    Assert.That(loco.NormalizedSpeed, Is.Zero);
                    Assert.That(animator.GetFloat("Speed"), Is.Zero);
                    facing.Tick(.1f);
                    Assert.That(facing.IsDialogueFacingActive, Is.False, "Legacy task gate remains after projection disable");
                    loco.enabled = true; loco.Tick(Time.deltaTime);
                    Assert.That(channels.IsNavigationActive, Is.True);
                }
                yield return null;
            }
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(maxSpeed, Is.GreaterThan(.1f)); Assert.That(sawNavigation, Is.True);
            Assert.That(maxAnimatorSpeed, Is.GreaterThan(.05f));
            Assert.That(cup.GetComponent<HeldItemState>().HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            // The world outcome can precede native NavMesh velocity settling.
            // Keep projecting that real motion; reacquire only after it actually ends.
            until = Time.realtimeSinceStartup + 2;
            do
            {
                yield return null; loco.Tick(Time.deltaTime); facing.Tick(Time.deltaTime);
            } while (channels.IsTaskMotionActive && Time.realtimeSinceStartup < until);
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(channels.IsTaskMotionActive, Is.False,
                $"navigation={loco.NavigationActive}; body={channels.ActiveBodyChannel}; stopped={agent.isStopped}; " +
                $"pending={agent.pathPending}; hasPath={agent.hasPath}; remaining={agent.remainingDistance}; " +
                $"speed={loco.PlanarSpeed}; task={merchant.GetComponent<GoalPlanRunner>().HasActiveTask}");
            Assert.That(loco.NavigationActive, Is.False); Assert.That(loco.NormalizedSpeed, Is.Zero);
            Assert.That(facing.IsDialogueFacingActive, Is.True);
            attention.Tick(.01f); Assert.That(attention.Source, Is.EqualTo(AttentionSource.Dialogue));
            Assert.That(loco.HasSpeedParameter, Is.True);
            until = Time.realtimeSinceStartup + 1;
            while (animator.GetFloat("Speed") > .01f && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(animator.GetFloat("Speed"), Is.LessThanOrEqualTo(.01f));
            loco.enabled = false; Assert.That(channels.IsNavigationActive, Is.False);
        }
    }
}
