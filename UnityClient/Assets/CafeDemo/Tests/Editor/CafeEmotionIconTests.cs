using System.Collections;
using System.IO;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeEmotionIconContractTests
    {
        [TestCase("happy"), TestCase("confused"), TestCase("surprised"), TestCase("thinking")]
        public void DriverPresentationCopyPreservesExplicitIcon(string cue)
        {
            var original = new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = "Hello", expression_icon = cue };
            var copy = (CognitiveDecision)typeof(CognitiveRunDriver).GetMethod("CopyDecisionForPresentation",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { original });
            Assert.That(copy, Is.Not.SameAs(original));
            Assert.That(copy.expression_icon, Is.EqualTo(cue));
        }

        [TestCase(null), TestCase("none"), TestCase("happy"), TestCase("confused"), TestCase("surprised"), TestCase("thinking")]
        public void OnlyBoundedOptionalVisualCuesAccepted(string cue)
        {
            Assert.That(new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = "Hi?", expression_icon = cue }.IsValid, Is.True);
            Assert.That(new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = "Hi", expression_icon = "<b>!</b>" }.IsValid, Is.False);
            Assert.That(new CognitiveDecision { decision_kind = "NO_ACTION", expression_icon = "happy" }.IsValid, Is.False);
        }
    }

    public sealed class CafeEmotionIconSceneTests
    {
        private WorldEntity[] actors;
        private WorldEntity player;
        private DialogueController dialogue;
        private DialogueBubbleManager bubbles;
        private int request;
        private static string Evidence => System.Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../BuildValidation/EmotionIcons-20261030"));
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode(); AnimationSceneTestIsolation.Ready();
            var all = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            actors = all.Where(a => a.GetComponent<ExpressiveController>() != null).OrderBy(a => a.EntityId).ToArray();
            player = all.Single(a => a.EntityId == "player_01");
            // The inspection camera sits behind the test player; hide its renderers only in this fixture.
            foreach (var renderer in player.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
            foreach (var actor in actors) actor.GetComponent<CognitiveRunDriver>().enabled = false;
            dialogue = Object.FindAnyObjectByType<DialogueController>(); bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            var orbit = Camera.main.GetComponent<CafeDemoOrbitCamera>(); if (orbit != null) orbit.enabled = false;
            var game = EditorWindow.GetWindow(System.Type.GetType("UnityEditor.GameView,UnityEditor"));
            game.Show(); game.maximized = true; game.Focus();
            Camera.main.fieldOfView = 65;
            yield return null;
            yield return null;
        }
        [UnityTearDown] public IEnumerator Close() { if (Application.isPlaying) yield return new ExitPlayMode(); }
        private void Select(WorldEntity actor)
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = actor.transform.position + actor.transform.forward * 1.3f; cc.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(actor.GetComponent<DialogueParticipant>()), Is.True);
            // Close inspection is not an occlusion test: the opposite seated NPC can fill this camera.
            foreach (var mesh in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
                mesh.enabled = mesh.transform.IsChildOf(actor.transform);
            var head = actor.GetComponent<DialogueBubbleAnchor>().HeadPosition;
            Camera.main.transform.position = head + actor.transform.forward * 1.2f + Vector3.up * .05f;
            Camera.main.transform.LookAt(head + Vector3.up * .05f);
        }
        private void Present(WorldEntity actor, string cue = null)
        {
            string id = "icon-" + ++request;
            Assert.That(actor.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(
                new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = "这是隔离的视觉预览，不是真实经历。你好？！",
                    emotion = "happy", gesture = "none", expression_icon = cue },
                new CognitivePresentationContext(new CognitiveRequest { request_id = id, trace_id = id,
                    run = new CognitiveRunDto { run_id = id, agent_entity_id = actor.EntityId } }, player.EntityId, dialogue.CurrentConversationId)), Is.True);
        }
        private static IEnumerator Advance(float seconds)
        { float until = Time.time + seconds; while (Time.time < until) yield return null; }

        [UnityTest] public IEnumerator OrdinaryTenRepliesSilent_ExplicitCuesCooldownAndNpcIsolation()
        {
            var a = actors[0]; Select(a);
            var icon = a.GetComponent<DialogueEmotionIconPresenter>();
            var context = new CognitivePresentationContext(new CognitiveRequest { request_id = "not-presented", trace_id = "not-presented",
                run = new CognitiveRunDto { run_id = "not-presented", agent_entity_id = a.EntityId } }, player.EntityId, dialogue.CurrentConversationId);
            Assert.That(icon.TryPresent("happy", context, Time.unscaledTimeAsDouble), Is.False, "A plan without presented speech is silent.");
            for (int i = 0; i < 10; ++i) { Present(a); icon.Tick(Time.unscaledTimeAsDouble + .2); Assert.That(icon.ActiveCue, Is.Null); }
            Assert.That(icon.PresentationCount, Is.Zero, "Happy emotion and punctuation alone must never trigger icons.");
            var wrongOwner = new CognitivePresentationContext(new CognitiveRequest { request_id = "wrong-owner", trace_id = "wrong-owner",
                run = new CognitiveRunDto { run_id = "wrong-owner", agent_entity_id = actors[1].EntityId } }, player.EntityId, dialogue.CurrentConversationId);
            Assert.That(icon.TryPresent("happy", wrongOwner, Time.unscaledTimeAsDouble), Is.False);
            Present(a, "happy"); Assert.That(icon.PresentationCount, Is.EqualTo(1));
            Present(a, "confused"); Assert.That(icon.ActiveCue, Is.Null); Assert.That(icon.PresentationCount, Is.EqualTo(1));
            var b = actors[1]; Select(b); Present(b, "confused");
            Assert.That(b.GetComponent<DialogueEmotionIconPresenter>().PresentationCount, Is.EqualTo(1), "Cooldowns must be NPC-local.");
            Assert.That(icon.IsVisible, Is.False);
            yield return null;
        }

        [UnityTest] public IEnumerator ReplacementCloseCancelDisableAndTaskRemainIndependent()
        {
            var a = actors[0]; Select(a); Present(a, "thinking");
            var icon = a.GetComponent<DialogueEmotionIconPresenter>();
            var speech = a.GetComponent<SpeechPresentationLifecycle>();
            var channels = a.GetComponent<MotionChannelCoordinator>();
            int revision = Object.FindAnyObjectByType<WorldStateService>().WorldRevision;
            channels.BeginTaskMotion(); Assert.That(icon.ActiveCue, Is.EqualTo("thinking"));
            dialogue.HideDialoguePresentation(); Assert.That(icon.ActiveCue, Is.Null);
            Assert.That(speech.IsSpeaking, Is.True, "Clearing an icon must not cancel world speech.");
            Assert.That(channels.IsTaskMotionActive, Is.True); channels.EndTaskMotion();
            Assert.That(icon.PreviewForEditor("surprised"), Is.True); icon.ClearPresentation(); Assert.That(icon.ActiveCue, Is.Null);
            Assert.That(icon.PreviewForEditor("happy"), Is.True); icon.enabled = false; Assert.That(icon.IsVisible, Is.False);
            icon.enabled = true; Assert.That(icon.ActiveCue, Is.Null);
            Assert.That(Object.FindAnyObjectByType<WorldStateService>().WorldRevision, Is.EqualTo(revision));
            yield return null;
        }

        [UnityTest] public IEnumerator FourCuesFourModels_FadeFollowFixedPlacementAndExpire_VisualSamples()
        {
            Assert.That(actors.Length, Is.EqualTo(4)); Directory.CreateDirectory(Evidence);
            foreach (var actor in actors)
            {
                Select(actor);
                var icon = actor.GetComponent<DialogueEmotionIconPresenter>();
                var fields = new SerializedObject(icon); fields.FindProperty("cooldownSeconds").floatValue = 1f; fields.ApplyModifiedPropertiesWithoutUndo();
                foreach (string cue in new[] { "happy", "confused", "surprised", "thinking" })
                {
                    bubbles.ClearMessages(); Present(actor, cue);
                    Assert.That(icon.ActiveCue, Is.EqualTo(cue));
                    yield return Advance(.25f);
                    bubbles.TryGetHeadRect(actor.GetComponent<DialogueBubbleAnchor>(), out Rect diagnosticHead);
                    var diagnosticLayer = (RectTransform)bubbles.transform;
                    string diagnostic = actor.EntityId + ":" + cue + " cue=" + icon.ActiveCue + " alpha=" + icon.Alpha +
                        " head=" + diagnosticHead + " layer=" + diagnosticLayer.rect + " prompt=" +
                        Object.FindAnyObjectByType<PlayerDialogueInteractor>().IsPromptVisible +
                        " bubbles=" + string.Join(";", bubbles.GetVisibleScreenRects()) + " visible=" + dialogue.IsPresentationVisible;
                    if (!icon.IsVisible) File.WriteAllText(Path.Combine(Evidence, "placement-failure.txt"), diagnostic);
                    Assert.That(icon.IsVisible, Is.True, diagnostic);
                    Assert.That(icon.Alpha, Is.GreaterThan(.9f));
                    Assert.That(bubbles.TryGetHeadRect(actor.GetComponent<DialogueBubbleAnchor>(), out Rect head), Is.True);
                    Assert.That(icon.VisibleRect.Contains(head.center), Is.False);
                    Assert.That(icon.VisibleRect.xMin, Is.GreaterThan(head.center.x).And.LessThan(head.xMax),
                        "The icon must sit closer than the generous dialogue-bubble head margin.");
                    // Deliberately overlap a bubble with the accent: it must neither switch sides nor hide.
                    var bubbleView = bubbles.GetComponentsInChildren<DialogueBubbleView>().First(v => v.isActiveAndEnabled);
                    Rect originalBubbleRect = bubbleView.ScreenRect;
                    double instant = Time.unscaledTimeAsDouble; icon.Tick(instant);
                    Rect stable = icon.VisibleRect;
                    typeof(DialogueBubbleView).GetProperty("ScreenRect").SetValue(bubbleView, stable);
                    icon.Tick(instant);
                    Assert.That(icon.IsVisible, Is.True);
                    Assert.That(icon.VisibleRect, Is.EqualTo(stable), "Bubble changes must not move the icon.");
                    typeof(DialogueBubbleView).GetProperty("ScreenRect").SetValue(bubbleView, originalBubbleRect);
                    var image = ScreenCapture.CaptureScreenshotAsTexture();
                    File.WriteAllBytes(Path.Combine(Evidence, actor.EntityId + "-" + cue + ".png"), image.EncodeToPNG()); Object.Destroy(image);
                    Vector2 before = icon.VisibleRect.center;
                    Camera.main.transform.position += Camera.main.transform.right * .12f;
                    yield return Advance(.2f);
                    Assert.That(Vector2.Distance(icon.VisibleRect.center, before), Is.GreaterThan(2));
                    yield return Advance(.68f); Assert.That(icon.Alpha, Is.LessThan(.95f));
                    yield return Advance(.28f); Assert.That(icon.ActiveCue, Is.Null);
                }
            }
        }
    }
}
