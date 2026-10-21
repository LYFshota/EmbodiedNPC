using System.Collections;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
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
    public sealed class CafeNpcWaveTests
    {
        private WorldEntity merchant, customer, player;
        private DialogueController dialogue;
        [UnitySetUp] public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode();
            AnimationSceneTestIsolation.Ready();
            var entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            customer = entities.Single(e => e.EntityId == "npc_cafe_regular_01");
            player = entities.Single(e => e.EntityId == "player_01");
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            Select(merchant);
            yield return Wait(.4f);
        }
        [UnityTearDown] public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until) yield return null;
        }
        private void Select(WorldEntity owner)
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = owner.transform.position + Quaternion.AngleAxis(30, Vector3.up) * owner.transform.forward * 1.5f;
            cc.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
        }
        private bool Present(WorldEntity owner) => owner.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(
            new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = new string('a', 90), gesture = "wave", look_at = player.EntityId },
            new CognitivePresentationContext(new CognitiveRequest { request_id = "er4b", trace_id = "er4b",
                run = new CognitiveRunDto { run_id = "er4b", agent_entity_id = owner.EntityId } },
                player.EntityId, dialogue.CurrentConversationId));
        private static long Start(WorldEntity owner)
        {
            var gesture = owner.GetComponent<GestureController>(); var channels = owner.GetComponent<MotionChannelCoordinator>();
            Assert.That(gesture.CanPlay("wave"), Is.True);
            Assert.That(channels.TryBeginGesture("wave", false, Time.unscaledTimeAsDouble, gesture.PlaybackTimeout("wave"), out long id), Is.True);
            Assert.That(gesture.TryPlayApprovedGesture("wave", id), Is.True);
            return id;
        }

        [Test] public void Assets_StandaloneOneShotSharedMaskAndNoSourceDependency()
        {
            const string path = "Assets/CafeDemo/Animations/ThirdParty/KayKit/Waving.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            Assert.That(clip.isHumanMotion, Is.True); Assert.That(clip.isLooping, Is.False);
            Assert.That(clip.length, Is.EqualTo(2.133333f).Within(.001f)); Assert.That(clip.events, Is.Empty);
            Assert.That(AssetDatabase.GetDependencies(path, true), Is.EqualTo(new[] { path }));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/CafeDemo/Animations/CafeExpressive.controller");
            var layer = controller.layers.Single(l => l.name == GestureController.WaveLayerName);
            Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override));
            Assert.That(layer.defaultWeight, Is.Zero); Assert.That(layer.avatarMask, Is.SameAs(controller.layers[1].avatarMask));
            foreach (var part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg })
                Assert.That(layer.avatarMask.GetHumanoidBodyPartActive(part), Is.False);
            var states = layer.stateMachine.states.Select(s => s.state).ToArray();
            Assert.That(states.Select(s => s.name), Is.EquivalentTo(new[] { "Empty", "Wave" }));
            Assert.That(layer.stateMachine.defaultState.name, Is.EqualTo("Empty"));
            Assert.That(states.Single(s => s.name == "Empty").motion, Is.Null);
            Assert.That(states.Single(s => s.name == "Wave").motion, Is.SameAs(clip));
            Assert.That(states.Single(s => s.name == "Wave").speed, Is.EqualTo(GestureController.WaveStateSpeed));
            Assert.That(states.All(s => !s.writeDefaultValues && s.transitions.Length == 0), Is.True);
            Assert.That(controller.parameters.Select(p => p.name), Is.EqualTo(new[] { "Speed" }));
        }
        [UnityTest] public IEnumerator ActualStateEndBeforeDeadline_ExactlyOnceTalkingPhaseGazeAndFacing_BothNpcs()
        {
            foreach (var owner in new[] { merchant, customer })
            {
                Select(owner); yield return Wait(.4f);
                var channels = owner.GetComponent<MotionChannelCoordinator>(); var gesture = owner.GetComponent<GestureController>();
                var expression = owner.GetComponent<ExpressiveController>(); var animator = owner.GetComponentInChildren<Animator>();
                var talking = owner.GetComponentInChildren<DialogueAnimationPresenter>(); var gaze = owner.GetComponentInChildren<HeadGazeController>();
                var attention = owner.GetComponentInChildren<AttentionController>(); var facing = owner.GetComponentInChildren<FacingController>();
                expression.Speech.BeginPlayback("ongoing speech"); yield return Wait(.3f);
                float phase = animator.GetCurrentAnimatorStateInfo(1).normalizedTime;
                double started = Time.unscaledTimeAsDouble; float timeout = gesture.PlaybackTimeout("wave");
                int ended = 0; float endingPhase = 0;
                channels.GestureEnded += _ => { ended++;
                    endingPhase = animator.GetCurrentAnimatorStateInfo(2).normalizedTime; };
                long id = Start(owner);
                yield return Wait(.5f);
                Assert.That(gesture.WaveEntered && gesture.IsWaveActive, Is.True); Assert.That(gesture.IsNodActive, Is.False);
                Assert.That(gesture.WaveWeight, Is.EqualTo(1)); Assert.That(talking.CurrentWeight, Is.Zero);
                Assert.That(gaze.CurrentWeight, Is.GreaterThan(.8f)); Assert.That(attention.CurrentTargetEntityId, Is.EqualTo(player.EntityId));
                Assert.That(facing.IsDialogueFacingActive, Is.True);
                while (channels.ActiveGestureId != 0 && Time.unscaledTimeAsDouble - started < timeout + .2f) yield return null;
                Assert.That(ended, Is.EqualTo(1)); Assert.That(endingPhase, Is.GreaterThanOrEqualTo(1));
                // The adapter's subscribed handler clears WaveEntered first; state phase proves actual completion.
                Assert.That(Time.unscaledTimeAsDouble - started, Is.LessThan(timeout - .2f));
                Assert.That(channels.CompleteGesture(id), Is.False);
                yield return Wait(.3f);
                Assert.That(gesture.WaveWeight, Is.Zero); Assert.That(animator.GetCurrentAnimatorStateInfo(2).fullPathHash,
                    Is.EqualTo(Animator.StringToHash(GestureController.EmptyStateName)));
                Assert.That(expression.Speech.IsSpeaking, Is.True); Assert.That(talking.CurrentWeight, Is.GreaterThan(.9f));
                Assert.That(animator.GetCurrentAnimatorStateInfo(1).normalizedTime, Is.GreaterThan(phase + .5f));
                expression.Speech.EndPlayback(expression.Speech.Current.Id); yield return Wait(.3f);
                Assert.That(expression.State, Is.EqualTo(DialoguePresentationState.Listening)); Assert.That(talking.CurrentWeight, Is.Zero);
                Assert.That(ended, Is.EqualTo(1));
            }
            Assert.That(customer.GetComponent<NavMeshAgent>(), Is.Null); Assert.That(customer.GetComponent<GoalPlanRunner>(), Is.Null);
            Assert.That(customer.GetComponent<CognitiveActionHandoff>(), Is.Null); Assert.That(customer.GetComponentInChildren<LocomotionPresenter>(), Is.Null);
            Assert.That(customer.GetComponentInChildren<Animator>().GetFloat("Speed"), Is.Zero);
        }
        [UnityTest] public IEnumerator TimeoutForStalledPlaybackUsesDeadline_AndSpeedIsAccountedFor()
        {
            var gesture = merchant.GetComponent<GestureController>(); var channels = merchant.GetComponent<MotionChannelCoordinator>();
            var animator = merchant.GetComponentInChildren<Animator>();
            float normal = gesture.PlaybackTimeout("wave"); animator.speed = .5f;
            Assert.That(gesture.PlaybackTimeout("wave"), Is.EqualTo((normal - .75f) * 2 + .75f).Within(.001f));
            animator.speed = 1; double start = Time.unscaledTimeAsDouble; long id = Start(merchant);
            animator.Update(.1f); gesture.Tick(.1f); Assert.That(gesture.WaveEntered, Is.True);
            int ended = 0; channels.GestureEnded += _ => ended++;
            // No Animator.Update/yield: playback is intentionally stalled while advancing only coordinator time.
            channels.Tick(start + normal - .1f); Assert.That(channels.ActiveGestureId, Is.EqualTo(id));
            Assert.That(animator.GetCurrentAnimatorStateInfo(2).normalizedTime, Is.LessThan(1));
            channels.Tick(start + normal + .1f); Assert.That(channels.ActiveGestureId, Is.Zero);
            Assert.That(ended, Is.EqualTo(1)); Assert.That(channels.CompleteGesture(id), Is.False);
            yield return Wait(.2f); Assert.That(gesture.WaveWeight, Is.Zero);
        }
        [UnityTest] public IEnumerator HigherPriorityCancelRejectsOverlapAndNeverQueuesOrReplays()
        {
            var gesture = merchant.GetComponent<GestureController>(); var channels = merchant.GetComponent<MotionChannelCoordinator>();
            for (int priority = 0; priority < 4; priority++)
            {
                long oldId = Start(merchant); yield return Wait(.2f);
                Assert.That(channels.TryBeginGesture("nod", false, Time.unscaledTimeAsDouble, 2, out _), Is.False);
                if (priority == 0) channels.BeginEmergencyMotion();
                if (priority == 1) channels.SetInteractionActive(true);
                if (priority == 2) channels.BeginTaskMotion();
                if (priority == 3) channels.SetNavigationActive(true);
                Assert.That(channels.ActiveGestureId, Is.Zero); Assert.That(channels.CompleteGesture(oldId), Is.False);
                Assert.That(channels.TryBeginGesture("wave", false, Time.unscaledTimeAsDouble, 3, out _), Is.False);
                yield return Wait(.2f); Assert.That(gesture.WaveWeight, Is.Zero);
                channels.EndEmergencyMotion(); channels.SetInteractionActive(false); channels.EndTaskMotion(); channels.SetNavigationActive(false);
                yield return Wait(.2f); Assert.That(gesture.IsWaveActive, Is.False); Assert.That(channels.ActiveGestureId, Is.Zero);
            }
            Start(merchant); yield return Wait(.2f); gesture.enabled = false;
            Assert.That(channels.ActiveGestureId, Is.Zero); Assert.That(gesture.WaveWeight, Is.Zero);
            Assert.That(merchant.GetComponentInChildren<Animator>().GetLayerWeight(2), Is.Zero);
            gesture.enabled = true; yield return Wait(.3f); Assert.That(gesture.IsWaveActive, Is.False);
        }
        [UnityTest] public IEnumerator MissingResourcesFailedEntryAndControllerLossReleaseSafely()
        {
            var gesture = merchant.GetComponent<GestureController>(); var animator = merchant.GetComponentInChildren<Animator>();
            var channels = merchant.GetComponent<MotionChannelCoordinator>(); var so = new SerializedObject(gesture);
            var clip = so.FindProperty("waveClip").objectReferenceValue;
            so.FindProperty("waveClip").objectReferenceValue = null; so.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(gesture.CanPlay("wave"), Is.False);
            Assert.That(Present(merchant), Is.True); Assert.That(channels.ActiveGestureId, Is.Zero);
            so.FindProperty("waveClip").objectReferenceValue = clip; so.ApplyModifiedPropertiesWithoutUndo();
            var cafeController = animator.runtimeAnimatorController;
            var idleController = new AnimatorController { name = "MissingWaveLayerTestFixture" };
            idleController.AddParameter("Speed", AnimatorControllerParameterType.Float);
            idleController.AddLayer("Base Layer");
            var idleState = idleController.layers[0].stateMachine.AddState("Idle");
            idleState.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/CafeDemo/Animations/ThirdParty/Quaternius/Idle_Loop.anim");
            idleController.layers[0].stateMachine.defaultState = idleState;
            // Match the configured controller, so the missing-layer gate is exercised independently.
            so.FindProperty("waveController").objectReferenceValue = idleController; so.ApplyModifiedPropertiesWithoutUndo();
            animator.runtimeAnimatorController = idleController; animator.Rebind(); animator.Update(0);
            Assert.That(animator.GetLayerIndex(GestureController.WaveLayerName), Is.EqualTo(-1));
            Assert.That(gesture.CanPlay("wave"), Is.False);
            so.FindProperty("waveController").objectReferenceValue = cafeController; so.ApplyModifiedPropertiesWithoutUndo();
            animator.runtimeAnimatorController = cafeController; animator.Rebind(); animator.Update(0);
            Object.Destroy(idleController);
            Assert.That(gesture.CanPlay("wave"), Is.True);
            Start(merchant); animator.Update(.01f); // Evaluate the queued Play before forcing the failed-entry state.
            animator.Play(Animator.StringToHash(GestureController.EmptyStateName), 2); animator.Update(.01f);
            Assert.That(animator.GetCurrentAnimatorStateInfo(2).fullPathHash, Is.EqualTo(Animator.StringToHash(GestureController.EmptyStateName)));
            gesture.Tick(.6f); Assert.That(channels.ActiveGestureId, Is.Zero); yield return Wait(.2f);
            Start(merchant); var controller = animator.runtimeAnimatorController; animator.runtimeAnimatorController = null;
            Assert.That(gesture.CanPlay("wave"), Is.False); gesture.Tick(.01f);
            Assert.That(channels.ActiveGestureId, Is.Zero);
            animator.runtimeAnimatorController = controller; animator.Rebind(); animator.Update(0); yield return Wait(.2f);
            Assert.That(gesture.WaveWeight, Is.Zero);
        }
        [UnityTest] public IEnumerator PresentationLeavesRootNavPerceptionWorldAndOtherNpcUntouched_BothDirections()
        {
            foreach (var owner in new[] { merchant, customer })
            {
                Select(owner); yield return Wait(.4f);
                var other = owner == merchant ? customer : merchant;
                var sensor = owner.GetComponent<VisionSensor>(); sensor.ScanNow();
                var store = owner.GetComponent<PerceivedStateStore>(); var world = Object.FindAnyObjectByType<WorldStateService>();
                int revision = store.Aggregator.PerceptionRevision, worldRevision = world.WorldRevision;
                var visible = sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray();
                var root = owner.transform.position; var rotation = owner.transform.rotation; var forward = sensor.transform.forward;
                var nav = owner.GetComponent<NavMeshAgent>(); Vector3 destination = nav == null ? Vector3.zero : nav.destination;
                Assert.That(Present(owner), Is.True); var gesture = owner.GetComponent<GestureController>();
                owner.GetComponentInChildren<Animator>().Update(.4f); gesture.Tick(.4f);
                owner.GetComponentInChildren<HeadGazeController>().Tick(.15f);
                owner.GetComponentInChildren<DialogueAnimationPresenter>().Tick(.3f);
                Assert.That(gesture.IsWaveActive, Is.True);
                Assert.That(owner.transform.position, Is.EqualTo(root)); Assert.That(owner.transform.rotation, Is.EqualTo(rotation));
                Assert.That(sensor.transform.forward, Is.EqualTo(forward)); sensor.ScanNow();
                Assert.That(store.Aggregator.PerceptionRevision, Is.EqualTo(revision));
                Assert.That(sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray(), Is.EqualTo(visible));
                Assert.That(world.WorldRevision, Is.EqualTo(worldRevision));
                if (nav != null) Assert.That(nav.destination, Is.EqualTo(destination));
                Assert.That(other.GetComponent<MotionChannelCoordinator>().ActiveGestureId, Is.Zero);
                Assert.That(other.GetComponentInChildren<DialogueAnimationPresenter>().CurrentWeight, Is.Zero);
                owner.GetComponent<ExpressiveController>().Speech.Cancel();
            }
        }
    }
}
