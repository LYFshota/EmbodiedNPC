using System.Collections;
using System.Collections.Generic;
using System.IO;
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
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeV2NaturalInteractionTests
    {
        private WorldEntity[] actors;
        private WorldEntity player, cup;
        private DialogueController dialogue;
        private WorldStateService world;
        private string Evidence => Path.GetFullPath(Path.Combine(Application.dataPath,
            "../../InternalDocs/Reports/NaturalInteractionP0Evidence"));

        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            // No transport, startup HTTP, model or production Backend is involved in these scene tests.
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode();
            AnimationSceneTestIsolation.Ready();
            actors = Object.FindObjectsByType<WorldEntity>()
                .Where(x => x.GetComponent<ExpressiveController>() != null).OrderBy(x => x.EntityId).ToArray();
            var all = Object.FindObjectsByType<WorldEntity>();
            player = all.Single(x => x.EntityId == "player_01"); cup = all.Single(x => x.EntityId == "cup_01");
            dialogue = Object.FindAnyObjectByType<DialogueController>(); world = Object.FindAnyObjectByType<WorldStateService>();
            foreach (var actor in actors) actor.GetComponent<CognitiveRunDriver>().enabled = false;
            yield return null;
        }
        [UnityTearDown] public IEnumerator Close() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private WorldEntity Merchant => actors.Single(x => x.EntityId == "npc_cafe_01");
        private void Select(WorldEntity actor)
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = actor.transform.position + actor.transform.forward * 1.3f; cc.enabled = true;
            Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(actor.GetComponent<DialogueParticipant>()), Is.True);
        }
        private bool Present(WorldEntity actor, string emotion = "happy", string gesture = "none") =>
            actor.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(
                new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                    utterance = "这是隔离的表现回归测试。", emotion = emotion, gesture = gesture },
                new CognitivePresentationContext(new CognitiveRequest { request_id = "natural-p0", trace_id = "natural-p0",
                    run = new CognitiveRunDto { run_id = "natural-p0", agent_entity_id = actor.EntityId } },
                    player.EntityId, dialogue.CurrentConversationId));

        [UnityTest] public IEnumerator Faces_BindingCapability_BlinkSmileFade_SeatsAndAuthorityUnchanged()
        {
            Assert.That(actors.Length, Is.EqualTo(4));
            var audit = new List<string>();
            foreach (var candidate in actors)
            {
                var character = candidate.GetComponentInChildren<Animator>(); var model = character.GetComponent<Vrm10Instance>();
                foreach (var pair in model.Vrm.Expression.Clips)
                {
                    foreach (var binding in pair.Clip.MorphTargetBindings)
                    {
                        var node = string.IsNullOrEmpty(binding.RelativePath) ? model.transform : model.transform.Find(binding.RelativePath);
                        var renderer = node == null ? null : node.GetComponent<SkinnedMeshRenderer>();
                        audit.Add(candidate.EntityId + " preset=" + pair.Preset + " node=" + binding.RelativePath +
                            " index=" + binding.Index + " valid=" + (renderer != null && renderer.sharedMesh != null && binding.Index < renderer.sharedMesh.blendShapeCount) +
                            " shape=" + (renderer != null && renderer.sharedMesh != null && binding.Index < renderer.sharedMesh.blendShapeCount ? renderer.sharedMesh.GetBlendShapeName(binding.Index) : "missing"));
                    }
                }
            }
            Directory.CreateDirectory(Evidence); File.WriteAllLines(Path.Combine(Evidence, "runtime-binding-diagnostic.txt"), audit);
            audit.Clear();
            foreach (var actor in actors)
            {
                var face = actor.GetComponent<CafeVrmExpressionPresenter>(); Assert.That(face, Is.Not.Null);
                var animator = actor.GetComponentInChildren<Animator>();
                Assert.That(animator.avatar.isHuman && animator.avatar.isValid, Is.True);
                var position = actor.transform.position; var rotation = actor.transform.rotation;
                Select(actor); Assert.That(Present(actor), Is.True);
                face.Tick(.2f);
                bool supported = actor.EntityId != "npc_cafe_regular_01";
                Assert.That(face.HasBlink, Is.EqualTo(supported), actor.EntityId);
                Assert.That(face.HasHappy, Is.EqualTo(supported), actor.EntityId);
                Assert.That(face.HappyWeight, supported ? Is.GreaterThan(0).And.LessThan(.3f) : Is.Zero);
                int revision = world.WorldRevision;
                bool closed = false;
                for (int i = 0; i < 400; i++)
                {
                    face.Tick(.02f);
                    if (face.BlinkWeight > .75f) { closed = true; break; }
                }
                Assert.That(closed, Is.EqualTo(supported));
                if (supported)
                {
                    var model = animator.GetComponent<Vrm10Instance>(); model.Runtime.Process();
                    var binding = model.Vrm.Expression.Blink.MorphTargetBindings[0];
                    var node = string.IsNullOrEmpty(binding.RelativePath) ? model.transform : model.transform.Find(binding.RelativePath);
                    Assert.That(node.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(binding.Index), Is.GreaterThan(50));
                    yield return null; // Skinned rendering refreshes once per engine frame.
                    Capture(actor, actor.EntityId + "-blink"); face.Tick(.2f); model.Runtime.Process();
                }
                Assert.That(face.BlinkWeight, Is.Zero);
                Assert.That(Present(actor, "neutral"), Is.True); face.Tick(.1f);
                actor.GetComponent<ExpressiveController>().Speech.Cancel(); face.Tick(1f);
                Assert.That(face.HappyWeight, Is.Zero);
                if (supported) animator.GetComponent<Vrm10Instance>().Runtime.Process();
                Assert.That(world.WorldRevision, Is.EqualTo(revision));
                Assert.That(actor.transform.position, Is.EqualTo(position)); Assert.That(actor.transform.rotation, Is.EqualTo(rotation));
                var controller = (AnimatorController)animator.runtimeAnimatorController;
                audit.Add(actor.EntityId + " blink=" + face.HasBlink + " happy=" + face.HasHappy + " relaxed=" + face.HasRelaxed +
                    " avatar=" + animator.avatar.name + " layers=" + string.Join(",", controller.layers.Select(x => x.name)) +
                    " clips=" + string.Join(",", controller.animationClips.Select(x => x.name)));
                yield return null;
                Capture(actor, actor.EntityId + "-rest");
                if (supported)
                {
                    Assert.That(Present(actor), Is.True); face.Tick(.6f);
                    animator.GetComponent<Vrm10Instance>().Runtime.Process(); yield return null;
                    Capture(actor, actor.EntityId + "-smile");
                    // Continuous real Animator/LateUpdate frames; synthetic dialogue only, no real-model claim.
                    for (int frame = 0; frame < 6; frame++)
                    { yield return new WaitForSeconds(.08f); Capture(actor, actor.EntityId + "-talk-" + frame); }
                }
                if (actor != Merchant)
                    Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.SeatedIdle"), Is.True);
                yield return null;
            }
            Directory.CreateDirectory(Evidence); File.WriteAllLines(Path.Combine(Evidence, "runtime-face-audit.txt"), audit);
        }

        [UnityTest] public IEnumerator Face_ExternalOwner_MouthLookAt_DisableReenable_AndMissingModel()
        {
            var actor = Merchant; Select(actor);
            var animator = actor.GetComponentInChildren<Animator>(); var mixer = animator.GetComponent<Vrm10Instance>().Runtime.Expression;
            var face = actor.GetComponent<CafeVrmExpressionPresenter>();
            var mouth = new ExpressionKey(ExpressionPreset.aa); var look = new ExpressionKey(ExpressionPreset.lookLeft);
            mixer.SetWeight(mouth, .33f); mixer.SetWeight(look, .27f);
            Assert.That(Present(actor), Is.True); face.Tick(.5f);
            mixer.SetWeight(new ExpressionKey(ExpressionPreset.happy), .42f); face.Tick(.2f);
            Assert.That(face.HasHappy, Is.False); face.enabled = false;
            Assert.That(mixer.GetWeight(new ExpressionKey(ExpressionPreset.happy)), Is.EqualTo(.42f));
            Assert.That(mixer.GetWeight(mouth), Is.EqualTo(.33f));
            // Some models use bone LookAt and have no lookLeft key: preserve exactly what their mixer accepted.
            float acceptedLook = mixer.GetWeight(look); Assert.That(acceptedLook, Is.InRange(0, .27f));
            Assert.That(mixer.GetWeight(new ExpressionKey(ExpressionPreset.blink)), Is.Zero);
            mixer.SetWeight(new ExpressionKey(ExpressionPreset.happy), 0); face.enabled = true;
            Assert.That(face.HasHappy, Is.True); face.Tick(.6f); Assert.That(face.HappyWeight, Is.GreaterThan(0));
            actor.gameObject.SetActive(false); Assert.That(face.HappyWeight, Is.Zero);
            Assert.That(mixer.GetWeight(new ExpressionKey(ExpressionPreset.happy)), Is.Zero);
            actor.gameObject.SetActive(true); yield return null; Assert.That(face.HasHappy, Is.True);
            face.Configure(actor.GetComponent<ExpressiveController>(), null); face.Tick(1);
            Assert.That(face.HasHappy || face.HasBlink || face.HasRelaxed, Is.False);
            Assert.That(mixer.GetWeight(mouth), Is.EqualTo(.33f)); Assert.That(mixer.GetWeight(look), Is.EqualTo(acceptedLook));
        }

        [UnityTest] public IEnumerator Carry_BlocksGestureAndTalking_ReleaseRestores_WithoutOwnershipSideEffects()
        {
            var actor = Merchant; Select(actor);
            var channels = actor.GetComponent<MotionChannelCoordinator>(); var carry = actor.GetComponent<CarryPresenter>();
            var talking = actor.GetComponentInChildren<DialogueAnimationPresenter>();
            var held = cup.GetComponent<HeldItemState>(); var hold = actor.transform.Find("HandHoldPoint");
            Assert.That(world.TryPickUp("natural-pickup", "natural", actor, cup, held, hold,
                cup.transform.Find("GrabPoint")).Status, Is.EqualTo(SkillStatus.Succeeded));
            int revision = world.WorldRevision; carry.Tick(.2f);
            Assert.That(channels.IsCarryPresentationActive, Is.True);
            Assert.That(Present(actor, "happy", "wave"), Is.True);
            Assert.That(channels.ActiveGestureId, Is.Zero); talking.Tick(.3f); Assert.That(talking.CurrentWeight, Is.Zero);
            Assert.That(held.HolderEntityId, Is.EqualTo(actor.EntityId)); Assert.That(world.WorldRevision, Is.EqualTo(revision));
            yield return null; Capture(actor, "merchant-carry");
            var destination = player.transform.Find("PlayerHoldPoint");
            Assert.That(world.TryTransferOwnership("natural-give", "natural", actor, cup, player, held,
                destination, destination).Status, Is.EqualTo(SkillStatus.Succeeded));
            carry.Tick(.2f); Assert.That(channels.IsCarryPresentationActive, Is.False);
            Assert.That(Present(actor, "neutral", "nod"), Is.True); Assert.That(channels.ActiveGestureId, Is.Not.Zero);
            channels.SetNavigationActive(true); Assert.That(channels.ActiveGestureId, Is.Zero);
            channels.SetNavigationActive(false); Assert.That(Present(actor, "happy", "wave"), Is.True);
            talking.Tick(.3f); Assert.That(talking.CurrentWeight, Is.Zero);
            channels.CancelGesture(); talking.Tick(.3f); Assert.That(talking.CurrentWeight, Is.EqualTo(1));
            dialogue.HideDialoguePresentation(); talking.Tick(.3f); Assert.That(talking.CurrentWeight, Is.Zero);
            Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 1));
        }

        [UnityTest] public IEnumerator SwitchingNpc_ConsecutiveReplies_CancelAndDisable_ClearFaceAndGesture()
        {
            foreach (var actor in actors)
            {
                Select(actor); Assert.That(Present(actor, "happy", "nod"), Is.True);
                var face = actor.GetComponent<CafeVrmExpressionPresenter>(); face.Tick(.3f);
                Assert.That(Present(actor, "neutral"), Is.True);
                Assert.That(actor.GetComponent<MotionChannelCoordinator>().ActiveGestureId, Is.Zero);
                dialogue.HideDialoguePresentation(); face.Tick(1);
                Assert.That(face.HappyWeight, Is.Zero);
                actor.GetComponent<ExpressiveController>().enabled = false; face.Tick(1);
                Assert.That(face.RelaxedWeight, Is.Zero);
                actor.GetComponent<ExpressiveController>().enabled = true;
                yield return null;
            }
        }
        [UnityTest] public IEnumerator Gaze_SwitchingTargets_HasBoundedListeningAndTalkingAngularSpeed()
        {
            foreach (var actor in actors)
            {
                Select(actor); yield return null;
                var gaze = actor.GetComponentInChildren<HeadGazeController>();
                var attention = actor.GetComponentInChildren<AttentionController>();
                gaze.ResetPresentation(); attention.Tick(.3f);
                Vector2 first = gaze.CurrentAngles; gaze.Tick(.02f);
                Assert.That(Vector2.Distance(first, gaze.CurrentAngles), Is.LessThanOrEqualTo(85 * .02f + .001f));
                Assert.That(Present(actor), Is.True);
                first = gaze.CurrentAngles; gaze.Tick(.02f);
                Assert.That(Vector2.Distance(first, gaze.CurrentAngles), Is.LessThanOrEqualTo(65 * .02f + .001f));
                attention.enabled = false; gaze.Tick(.5f);
                Assert.That(gaze.CurrentWeight, Is.Zero); Assert.That(gaze.CurrentAngles, Is.EqualTo(Vector2.zero));
                attention.enabled = true;
            }
        }
        [UnityTest] public IEnumerator V2_BringObject_UsesGoalPipeline_AndPresentationCannotCommitSuccess()
        {
            var actor = Merchant;
            foreach (var npc in actors) npc.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            Select(actor);
            actor.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - actor.transform.position, Vector3.up));
            actor.GetComponent<VisionSensor>().ScanNow();
            int revision = world.WorldRevision;
            GoalOutcome? outcome = null;
            var planner = actor.GetComponent<GoalPlanRunner>(); planner.GoalOutcomePublished += value => outcome = value;
            Assert.That(actor.GetComponent<AgentEventReceiver>().Receive(new AgentEvent(System.Guid.NewGuid().ToString("N"),
                AgentEventTypes.PlayerUtterance, player.EntityId, actor.EntityId, "",
                new Dictionary<string, string> { [AgentEventPayloadKeys.Text] = "把杯子拿给我" },
                Time.realtimeSinceStartupAsDouble, revision)), Is.True);
            var request = actor.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request, Is.Not.Null);
            var driver = actor.GetComponent<CognitiveRunDriver>(); driver.enabled = true;
            Assert.That(driver.TryOwnRequest(request), Is.True);
            Assert.That(driver.TryConsumeResponse(new CognitiveResponse
            {
                message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = actor.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.ActionProposal,
                    utterance = "我去拿杯子。", emotion = "neutral", gesture = "none",
                    action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                        bindings = new Dictionary<string, string> { ["target"] = cup.EntityId, ["destination"] = player.EntityId } } },
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = cup.EntityId, destination_entity_id = player.EntityId }
            }), Is.True);
            var held = cup.GetComponent<HeldItemState>(); var carry = actor.GetComponent<CarryPresenter>();
            bool picked = false, walked = false; float deadline = Time.realtimeSinceStartup + 40;
            int frame = 0; float nextCapture = 0;
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (held.HolderEntityId == actor.EntityId)
                {
                    picked = true;
                    walked |= actor.GetComponentInChildren<LocomotionPresenter>().PlanarSpeed > .05f && carry.CurrentWeight > .8f;
                    Assert.That(actor.GetComponent<MotionChannelCoordinator>().ActiveGestureId, Is.Zero);
                    if (Time.realtimeSinceStartup >= nextCapture && frame < 12)
                    { Capture(actor, "bring-carry-" + frame++); nextCapture = Time.realtimeSinceStartup + .1f; }
                }
            }
            Assert.That(driver.HasPendingActionOutcome, Is.False); Assert.That(outcome.HasValue, Is.True);
            Assert.That(outcome.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(picked && walked, Is.True); Assert.That(held.HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(x => !x.enabled), Is.True);
            yield return null; carry.Tick(.3f);
            Assert.That(carry.CurrentWeight, Is.Zero); Assert.That(actor.GetComponent<MotionChannelCoordinator>().IsCarryPresentationActive, Is.False);
        }
        private void Capture(WorldEntity actor, string name)
        {
            var camera = Camera.main;
            if (camera == null) return;
            var orbit = camera.GetComponent<CafeDemoOrbitCamera>(); bool enabled = orbit != null && orbit.enabled;
            if (orbit != null) orbit.enabled = false;
            Vector3 position = camera.transform.position; Quaternion rotation = camera.transform.rotation;
            var animator = actor.GetComponentInChildren<Animator>(); var head = animator.GetBoneTransform(HumanBodyBones.Head);
            camera.transform.position = head.position + actor.transform.forward * 1.15f + Vector3.up * .05f;
            camera.transform.LookAt(head.position - Vector3.up * .13f);
            var previous = camera.targetTexture; var active = RenderTexture.active;
            // The close inspection camera can enter the test player's head; hide only those renderers for capture.
            var playerRenderers = player.GetComponentsInChildren<Renderer>();
            var playerVisibility = playerRenderers.Select(x => x.enabled).ToArray();
            foreach (var renderer in playerRenderers) renderer.enabled = false;
            var rt = new RenderTexture(800, 800, 24); var texture = new Texture2D(800, 800, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, 800, 800), 0, 0); texture.Apply();
                Directory.CreateDirectory(Evidence); File.WriteAllBytes(Path.Combine(Evidence, name + ".png"), texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previous; RenderTexture.active = active;
                camera.transform.SetPositionAndRotation(position, rotation);
                for (int i = 0; i < playerRenderers.Length; i++) playerRenderers[i].enabled = playerVisibility[i];
                if (orbit != null) orbit.enabled = enabled;
                Object.Destroy(rt); Object.Destroy(texture);
            }
        }
    }
}
