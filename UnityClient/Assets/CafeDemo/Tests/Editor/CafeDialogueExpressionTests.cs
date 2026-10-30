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
    public sealed class CafeDialogueExpressionTests
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
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = merchant.transform.position + merchant.transform.forward * 1.5f;
            cc.enabled = true; Physics.SyncTransforms();
            yield return null;
        }
        [UnityTearDown] public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private static IEnumerator Wait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until) yield return null;
        }
        private bool Present(WorldEntity owner, string gesture = "none") =>
            owner.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(
                new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                    utterance = "Hello, welcome to the cafe. Take your time choosing a drink.", gesture = gesture },
                new CognitivePresentationContext(new CognitiveRequest { request_id = "er4", trace_id = "er4",
                    run = new CognitiveRunDto { run_id = "er4", agent_entity_id = owner.EntityId } },
                    player.EntityId, dialogue.CurrentConversationId));

        [Test] public void Assets_StandaloneLoopAndHeadLegRootMaskExcluded()
        {
            const string p = "Assets/CafeDemo/Animations/ThirdParty/KayKit/Idle_Relaxed.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
            Assert.That(clip.isHumanMotion && clip.isLooping, Is.True);
            Assert.That(clip.length, Is.EqualTo(1.066667f).Within(.001f));
            Assert.That(clip.events, Is.Empty);
            Assert.That(AssetDatabase.GetDependencies(p, true), Is.EqualTo(new[] { p }));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/CafeDemo/Animations/CafeExpressive.controller");
            Assert.That(controller.layers.Select(l => l.name), Is.EqualTo(new[] { "Base Layer", "Dialogue", "GestureUpperBody", CarryPresenter.LayerName }));
            var mask = controller.layers[1].avatarMask;
            Assert.That(controller.layers[1].stateMachine.defaultState.motion, Is.SameAs(clip));
            foreach (var part in new[] { AvatarMaskBodyPart.Root, AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg })
                Assert.That(mask.GetHumanoidBodyPartActive(part), Is.False);
            foreach (var part in new[] { AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers })
                Assert.That(mask.GetHumanoidBodyPartActive(part), Is.True);
            Assert.That(controller.parameters.Select(x => x.name), Is.EqualTo(new[] { "Speed" }));
        }
        [UnityTest] public IEnumerator SpeechTruth_BlendReplacementSuppressionAndHiddenCleanup()
        {
            var expression = merchant.GetComponent<ExpressiveController>();
            var presenter = merchant.GetComponentInChildren<DialogueAnimationPresenter>();
            Assert.That(expression.State, Is.EqualTo(DialoguePresentationState.Idle));
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(expression.State, Is.EqualTo(DialoguePresentationState.Listening));
            Assert.That(Present(merchant), Is.True);
            presenter.Tick(.1f); Assert.That(presenter.ActivationWeight, Is.InRange(.3f, .5f));
            presenter.Tick(.3f); Assert.That(presenter.ActivationWeight, Is.EqualTo(1));
            Assert.That(Present(merchant), Is.True); presenter.Tick(.01f);
            Assert.That(presenter.ActivationWeight, Is.EqualTo(1));
            var channels = expression.MotionChannels;
            channels.SetNavigationActive(true); presenter.Tick(.2f);
            Assert.That(presenter.CurrentWeight, Is.Zero); Assert.That(expression.Speech.IsSpeaking, Is.True);
            channels.SetNavigationActive(false); presenter.Tick(.25f); Assert.That(presenter.ActivationWeight, Is.EqualTo(1));
            channels.BeginTaskMotion(); presenter.Tick(.2f); Assert.That(presenter.CurrentWeight, Is.Zero);
            Assert.That(expression.Speech.IsSpeaking, Is.True); channels.EndTaskMotion();
            channels.SetInteractionActive(true); presenter.Tick(.2f); Assert.That(presenter.CurrentWeight, Is.Zero);
            Assert.That(expression.Speech.IsSpeaking, Is.True); channels.SetInteractionActive(false);
            channels.BeginEmergencyMotion(); presenter.Tick(.2f); Assert.That(presenter.CurrentWeight, Is.Zero);
            Assert.That(expression.Speech.IsSpeaking, Is.True); channels.EndEmergencyMotion();
            presenter.Tick(.25f); dialogue.HideDialoguePresentation(); presenter.Tick(.2f);
            // Closing the input surface intentionally does not terminate displayed world speech.
            Assert.That(expression.Speech.IsSpeaking, Is.True);
            Assert.That(presenter.ActivationWeight, Is.EqualTo(1));
            expression.Speech.Cancel(); presenter.Tick(.2f);
            Assert.That(presenter.CurrentWeight, Is.Zero); Assert.That(expression.Speech.IsSpeaking, Is.False);
            presenter.enabled = false; Assert.That(presenter.CurrentWeight, Is.Zero);
            yield return null;
        }
        [TestCase("neutral"), TestCase("happy"), TestCase("fear"), TestCase("curious")]
        [TestCase("thoughtful"), TestCase("concerned"), TestCase("surprised")]
        public void DeliveryTones_AcceptedWithoutChangingActionAuthority(string tone)
        {
            Assert.That(new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = "Hello.", emotion = tone, gesture = "none" }.IsValid, Is.True);
            Assert.That(new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = "Hello.", emotion = "unregistered" }.IsValid, Is.False);
        }
        [UnityTest] public IEnumerator LongSpeech_HasBoundedAccentsAndRealRest_ThenClears()
        {
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(Present(merchant), Is.True);
            var expression = merchant.GetComponent<ExpressiveController>();
            var motion = merchant.GetComponentInChildren<DialogueAnimationPresenter>();
            // Real playback lifecycle, not a bubble-retention timer, owns the full long sentence.
            expression.Speech.BeginPlayback("A long explanation with room to pause and listen.");
            float maximum = 0; int rests = 0;
            for (int i = 0; i < 240; ++i)
            {
                motion.Tick(.05f);
                maximum = Mathf.Max(maximum, motion.CurrentWeight);
                if (i > 10 && motion.CurrentWeight < .001f) rests++;
                Assert.That(motion.HeadPitchAccent, Is.InRange(-3f, 3f));
                Assert.That(motion.HeadTiltAccent, Is.InRange(-3f, 3f));
            }
            Assert.That(maximum, Is.GreaterThan(.1f).And.LessThanOrEqualTo(.5f));
            Assert.That(rests, Is.GreaterThan(35), "Long speech must contain visible rests, not a constant loop.");
            Assert.That(motion.ActivationWeight, Is.EqualTo(1));
            Assert.That(merchant.GetComponentInChildren<Animator>().GetLayerWeight(1), Is.EqualTo(motion.CurrentWeight));
            expression.Speech.Cancel(); motion.Tick(1);
            Assert.That(motion.CurrentWeight, Is.Zero);
            Assert.That(motion.HeadPitchAccent, Is.Zero); Assert.That(motion.HeadTiltAccent, Is.Zero);
            yield return null;
        }
        [UnityTest] public IEnumerator Nod_RealCompletionRetainsAttentionAndReacquiresGaze_BothNpcs()
        {
            foreach (var owner in new[] { merchant, customer })
            {
                var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
                player.transform.position = owner.transform.position + owner.transform.forward * 1.5f; cc.enabled = true;
                Assert.That(dialogue.SelectTarget(owner.GetComponent<DialogueParticipant>()), Is.True);
                yield return Wait(.4f);
                var gaze = owner.GetComponentInChildren<HeadGazeController>();
                var attention = owner.GetComponentInChildren<AttentionController>();
                var gesture = owner.GetComponent<GestureController>();
                var channels = owner.GetComponent<MotionChannelCoordinator>();
                var animation = owner.GetComponentInChildren<DialogueAnimationPresenter>();
                int ended = 0; channels.GestureEnded += _ => ended++;
                Assert.That(Present(owner, "nod"), Is.True);
                long id = channels.ActiveGestureId; Assert.That(id, Is.Not.Zero);
                yield return Wait(.35f);
                Assert.That(gesture.IsNodActive, Is.True); Assert.That(gesture.NodPitch, Is.GreaterThan(4), owner.EntityId + " elapsed=" + gesture.NodElapsed);
                Assert.That(gaze.CurrentWeight, Is.Zero); Assert.That(attention.CurrentTargetEntityId, Is.EqualTo(player.EntityId));
                Assert.That(animation.ActivationWeight, Is.GreaterThan(.9f));
                yield return Wait(.4f);
                Assert.That(channels.ActiveGestureId, Is.Zero); Assert.That(ended, Is.EqualTo(1));
                Assert.That(channels.CompleteGesture(id), Is.False);
                yield return Wait(.3f); Assert.That(gaze.CurrentWeight, Is.GreaterThan(.8f));
            }
            Assert.That(customer.GetComponent<NavMeshAgent>(), Is.Null);
            Assert.That(customer.GetComponent<GoalPlanRunner>(), Is.Null);
            Assert.That(customer.GetComponentInChildren<LocomotionPresenter>(), Is.Null);
            Assert.That(customer.GetComponentInChildren<Animator>().GetFloat("Speed"), Is.Zero);
        }
        [UnityTest] public IEnumerator Nod_CancelTimeoutDisableNeverReplay()
        {
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            var gesture = merchant.GetComponent<GestureController>(); var channels = merchant.GetComponent<MotionChannelCoordinator>();
            Assert.That(Present(merchant, "nod"), Is.True); channels.SetNavigationActive(true);
            Assert.That(gesture.IsNodActive, Is.False); Assert.That(channels.ActiveGestureId, Is.Zero);
            Assert.That(Present(merchant, "nod"), Is.True); Assert.That(channels.ActiveGestureId, Is.Zero);
            channels.SetNavigationActive(false); yield return Wait(.8f);
            Assert.That(channels.ActiveGestureId, Is.Zero);
            Assert.That(Present(merchant, "nod"), Is.True);
            channels.Tick(Time.unscaledTimeAsDouble + gesture.PlaybackTimeout("nod") + .01f);
            Assert.That(channels.ActiveGestureId, Is.Zero); Assert.That(gesture.IsNodActive, Is.False);
            Assert.That(Present(merchant, "nod"), Is.True); gesture.enabled = false;
            Assert.That(channels.ActiveGestureId, Is.Zero); gesture.enabled = true;
            yield return Wait(.8f); Assert.That(gesture.IsNodActive, Is.False);
        }
        [UnityTest] public IEnumerator TalkingAndNod_SameFrameRootPerceptionAndOtherNpcIsolation()
        {
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            var sensor = merchant.GetComponent<VisionSensor>(); sensor.ScanNow();
            var state = merchant.GetComponent<PerceivedStateStore>();
            var root = merchant.transform.position; var rotation = merchant.transform.rotation; var forward = sensor.transform.forward;
            int revision = state.Aggregator.PerceptionRevision; var visible = sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray();
            Assert.That(Present(merchant, "nod"), Is.True);
            merchant.GetComponent<GestureController>().Tick(.35f);
            merchant.GetComponentInChildren<HeadGazeController>().Tick(.15f);
            merchant.GetComponentInChildren<DialogueAnimationPresenter>().Tick(.3f);
            Assert.That(customer.GetComponent<MotionChannelCoordinator>().ActiveGestureId, Is.Zero);
            Assert.That(customer.GetComponentInChildren<DialogueAnimationPresenter>().CurrentWeight, Is.Zero);
            Assert.That(merchant.transform.position, Is.EqualTo(root)); Assert.That(merchant.transform.rotation, Is.EqualTo(rotation));
            Assert.That(sensor.transform.forward, Is.EqualTo(forward)); sensor.ScanNow();
            Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(revision));
            Assert.That(sensor.CurrentVisibleEntityIds.OrderBy(x => x).ToArray(), Is.EqualTo(visible));
            yield return null;
        }
    }
}
