using System.Collections;
using System;
using System.IO;
using System.Linq;
using EmbodiedNPC.Dialogue;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using UniVRM10;

namespace CafeDemo.Tests
{
    public sealed class PlayerExpressionTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        protected override bool ClearSceneAfterTest => true;
        private CafeVrmExpressionPresenter Face => player.GetComponent<CafeVrmExpressionPresenter>();
        private PlayerEmotePanel Panel => Object.FindAnyObjectByType<PlayerEmotePanel>();
        private static string FaceEvidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ?? Path.GetFullPath("../PlayerFaceEvidence");
        private void PrepareFace()
        {
            foreach (var bridge in Object.FindObjectsByType<PlayerEmoteAgentEventBridge>()) bridge.enabled = false;
            foreach (var driver in Object.FindObjectsByType<EmbodiedNPC.Cognition.CognitiveRunDriver>()) driver.enabled = false;
            foreach (var agent in Object.FindObjectsByType<EmbodiedNPC.Cognition.AgentOrchestrator>()) agent.CancelCurrentRun();
            Object.FindAnyObjectByType<CafeDemoOrbitCamera>().enabled = false;
            Directory.CreateDirectory(FaceEvidence);
        }
        private void AssertBase()
        {
            Assert.That(Face.HappyWeight, Is.EqualTo(.07f).Within(.001f));
            Assert.That(Face.RelaxedWeight, Is.EqualTo(.015f).Within(.001f));
        }
        private void CaptureFace(string name)
        {
            var model = animator.GetComponent<Vrm10Instance>(); model.Runtime.Process();
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            var camera = Camera.main; camera.transform.position = head.position + player.transform.forward * .8f + Vector3.up * .04f;
            camera.transform.LookAt(head.position + Vector3.up * .035f); camera.fieldOfView = 28;
            var previous = camera.targetTexture; var active = RenderTexture.active;
            var rt = new RenderTexture(800, 800, 24); var image = new Texture2D(800, 800, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, 800, 800), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(FaceEvidence, name + ".png"), image.EncodeToPNG());
            }
            finally { camera.targetTexture = previous; RenderTexture.active = active; rt.Release(); Object.Destroy(rt); Object.Destroy(image); }
        }
        private static void AssertOffline()
        {
            foreach (var backend in Object.FindObjectsByType<EmbodiedNPC.Communication.BackendWebSocketClient>())
            { Assert.That(backend.ConnectionAttempts, Is.Zero); Assert.That(backend.CognitiveRequestSendCount, Is.Zero); }
        }
        [UnityTest] public IEnumerator PlayerFace_BaseAndThreeRealMenuButtonsBlendAndReturnWithBlink()
        {
            PrepareFace(); yield return Wait(.5f);
            File.WriteAllText(Path.Combine(FaceEvidence, "capabilities.txt"), $"happy={Face.HasHappy} relaxed={Face.HasRelaxed} blink={Face.HasBlink}");
            Assert.That(Face.HasHappy && Face.HasRelaxed && Face.HasBlink, Is.True); AssertBase();
            Face.enabled = false; CaptureFace("neutral-reference"); Face.enabled = true; yield return Wait(.4f);
            CaptureFace("base-smile"); int revision = world.WorldRevision;
            foreach (string id in new[] { "wave", "cheer", "proud" })
            {
                Panel.Open(); Assert.That(Panel.IsOpen, Is.True);
                var button = Panel.GetComponentsInChildren<Button>(true).Single(x => x.name == (id == "wave" ? "WaveButton" : id == "cheer" ? "CheerButton" : "ProudButton"));
                float baseline = Face.HappyWeight; button.onClick.Invoke();
                Assert.That(emote.IsActive, Is.True); Assert.That(Panel.IsOpen, Is.False);
                Assert.That(Face.HappyWeight, Is.EqualTo(baseline), "Expression must not jump on UI activation.");
                Face.Tick(.03f); Assert.That(Face.HappyWeight, Is.GreaterThan(baseline).And.LessThan(id == "wave" ? .22f : id == "cheer" ? .45f : .18f));
                yield return Wait(.35f);
                Assert.That(Face.HappyWeight, Is.EqualTo(id == "wave" ? .22f : id == "cheer" ? .45f : .18f).Within(.001f));
                Assert.That(Face.RelaxedWeight, Is.EqualTo(id == "wave" ? .02f : id == "cheer" ? .04f : .10f).Within(.001f));
                CaptureFace(id + "-face");
                for (float end = Time.realtimeSinceStartup + 9; emote.IsActive && Time.realtimeSinceStartup < end;) yield return null;
                Assert.That(emote.IsActive, Is.False); yield return Wait(.4f); AssertBase(); CaptureFace(id + "-restored");
            }
            bool blinked = false;
            for (int i = 0; i < 400; i++) { Face.Tick(.02f); if (Face.BlinkWeight > .75f) { blinked = true; break; } }
            Assert.That(blinked, Is.True);
            var model = animator.GetComponent<Vrm10Instance>(); model.Runtime.Process();
            var binding = model.Vrm.Expression.Blink.MorphTargetBindings[0];
            var node = string.IsNullOrEmpty(binding.RelativePath) ? model.transform : model.transform.Find(binding.RelativePath);
            Assert.That(node.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(binding.Index), Is.GreaterThan(50));
            yield return null; // Let the skinned rendering buffer consume the evaluated blink.
            CaptureFace("base-blink");
            Face.Tick(.2f); Assert.That(Face.BlinkWeight, Is.Zero); AssertBase();
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }
        [UnityTest] public IEnumerator PlayerFace_RapidRequestsCancelPanelCloseFocusAndDisableRestoreBase()
        {
            PrepareFace(); yield return Wait(.5f);
            Assert.That(emote.TryPlay("unknown"), Is.False); AssertBase();
            string[] choices = { "wave", "cheer", "proud" };
            for (int i = 0; i < 9; i++)
            {
                Assert.That(emote.TryPlay(choices[i % 3]), Is.True); yield return Wait(.06f);
                Assert.That(emote.TryPlay(choices[(i + 1) % 3]), Is.False, "Retain the current action controller's debounce contract.");
                float before = Face.HappyWeight; emote.Cancel();
                Assert.That(Face.HappyWeight, Is.EqualTo(before));
                Face.Tick(.02f); Assert.That(Face.HappyWeight, Is.GreaterThanOrEqualTo(.07f));
                yield return Wait(.35f); AssertBase();
            }
            Panel.Open(); Panel.Close(); yield return Wait(.35f); AssertBase();
            Panel.Open(); Assert.That(Panel.Select("cheer"), Is.True); yield return Wait(.3f);
            Panel.enabled = false; yield return Wait(.35f); Assert.That(emote.IsActive, Is.False); AssertBase(); Panel.enabled = true;
            Panel.Open(); Assert.That(Panel.Select("proud"), Is.True); yield return Wait(.3f);
            Panel.SendMessage("OnApplicationFocus", false); yield return Wait(.35f); Assert.That(emote.IsActive, Is.False); AssertBase();
            Assert.That(emote.TryWave(), Is.True); yield return Wait(.3f); emote.enabled = false;
            yield return Wait(.35f); AssertBase(); emote.enabled = true; AssertOffline();
        }
        [UnityTest] public IEnumerator PlayerFace_RebindReleasesOldModelAndRespectsExternalFaceOwner()
        {
            PrepareFace(); yield return Wait(.5f);
            var original = animator; var oldMixer = original.GetComponent<Vrm10Instance>().Runtime.Expression;
            var happyKey = new ExpressionKey(ExpressionPreset.happy);
            var copy = Object.Instantiate(original.gameObject, original.transform.parent); var candidate = copy.GetComponent<Animator>();
            yield return Wait(.2f);
            var adapter = player.GetComponent<HumanoidMotionAdapter>();
            Assert.That(adapter.TryBind(candidate, out string error), Is.True, error);
            original.gameObject.SetActive(false); animator = candidate;
            yield return Wait(.5f); AssertBase(); Assert.That(oldMixer.GetWeight(happyKey), Is.Zero);
            var mixer = candidate.GetComponent<Vrm10Instance>().Runtime.Expression;
            var mouth = new ExpressionKey(ExpressionPreset.aa); var look = new ExpressionKey(ExpressionPreset.lookLeft);
            mixer.SetWeight(mouth, .33f); mixer.SetWeight(look, .27f);
            Assert.That(emote.TryPlay("cheer"), Is.True); yield return Wait(.35f);
            Assert.That(mixer.GetWeight(mouth), Is.EqualTo(.33f)); Assert.That(mixer.GetWeight(look), Is.EqualTo(.27f));
            mixer.SetWeight(happyKey, .6f); yield return Wait(.1f); emote.Cancel(); yield return Wait(.4f);
            Assert.That(mixer.GetWeight(happyKey), Is.EqualTo(.6f), "Yield instead of fighting another writer.");
            original.gameObject.SetActive(true); yield return null;
            Assert.That(adapter.TryBind(original, out error), Is.True, error); animator = original;
            Object.Destroy(copy); yield return Wait(.5f); AssertBase(); AssertOffline();
        }
    }

    public sealed class PlayerEmotePanelTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;

        private PlayerEmotePanel Panel => Object.FindAnyObjectByType<PlayerEmotePanel>();
        private static void CaptureUi(string name)
        {
            string folder = Path.Combine(Path.GetTempPath(), "EmbodiedNPC-EmoteUI-20261015");
            Directory.CreateDirectory(folder);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + ".png"));
        }
        private void Press(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys)); InputSystem.Update();
            // Match the existing EditMode-to-PlayMode fixture's synthetic input timing.
            Panel.SendMessage("Update");
        }
        private void OfflineWaveObservers()
        {
            foreach (var bridge in Object.FindObjectsByType<PlayerEmoteAgentEventBridge>(FindObjectsSortMode.None)) bridge.enabled = false;
        }

        [UnityTest] public IEnumerator ThreeButtons_PlayRealHumanoidMotion_AndReleaseControl()
        {
            OfflineWaveObservers();
            var panel = Panel; var orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.GetComponentsInChildren<PlayerEmoteIcon>(true).All(icon => icon.GetComponent<CanvasRenderer>() != null), Is.True);
            var buttons = panel.GetComponentsInChildren<Button>(true).Where(b => b.name.EndsWith("Button")).ToArray();
            Assert.That(buttons.Select(b => b.name), Is.EquivalentTo(new[] { "WaveButton", "CheerButton", "ProudButton" }));
            Assert.That(panel.transform.Find("EmoteHint").gameObject.activeSelf, Is.True);
            CaptureUi("closed"); yield return null;
            foreach (string name in new[] { "WaveButton", "CheerButton", "ProudButton" })
            {
                Press(Key.G);
                Assert.That(panel.IsOpen, Is.True); Assert.That(emote.IsActive, Is.False, "G opens the panel, not the old immediate Wave.");
                Assert.That(player.IsInputSuspended && orbit.IsInputSuspended, Is.True);
                Assert.That(Cursor.visible, Is.True); Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                Press();
                Canvas.ForceUpdateCanvases();
                var bounds = new Vector3[4]; ((RectTransform)panel.transform.Find("EmoteWindow")).GetWorldCorners(bounds);
                Assert.That(bounds.All(p => p.x >= 0 && p.x <= Screen.width && p.y >= 0 && p.y <= Screen.height), Is.True);
                CaptureUi("open"); yield return null;
                Vector3 origin = player.transform.position;
                buttons.Single(b => b.name == name).onClick.Invoke();
                Assert.That(panel.IsOpen, Is.False); Assert.That(emote.IsActive, Is.True);
                Assert.That(player.IsInputSuspended, Is.True);
                Press(Key.G); Assert.That(panel.IsOpen, Is.False, "An active emote must not reopen the menu."); Press();
                var arm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                float motion = 0; Quaternion last = arm.localRotation;
                float deadline = Time.realtimeSinceStartup + 9;
                float captureAt = Time.realtimeSinceStartup + .6f;
                bool captured = false;
                while (emote.IsActive && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    motion += Quaternion.Angle(last, arm.localRotation); last = arm.localRotation;
                    if (!captured && Time.realtimeSinceStartup >= captureAt) { CaptureUi(name); captured = true; }
                }
                Assert.That(emote.IsActive, Is.False);
                Assert.That(motion, Is.GreaterThan(8), name + " must actually animate the retargeted arm.");
                Assert.That(Vector2.Distance(new Vector2(origin.x, origin.z), new Vector2(player.transform.position.x, player.transform.position.z)), Is.LessThan(.02f));
                Assert.That(player.IsInputSuspended || orbit.IsInputSuspended, Is.False);
                Assert.That(animator.GetLayerWeight(animator.GetLayerIndex(PlayerEmoteController.LayerName)), Is.EqualTo(0).Within(.001));
            }
        }

        [UnityTest] public IEnumerator ToggleEscapeAndDisable_DoNotStealUiOrLeaveInputLocked()
        {
            OfflineWaveObservers();
            var panel = Panel; var orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            Press(Key.G); Assert.That(panel.IsOpen, Is.True); Press();
            Press(Key.G); Assert.That(panel.IsOpen, Is.False); Assert.That(player.IsInputSuspended, Is.False); Press();
            panel.Open(); Press(Key.Escape);
            Assert.That(panel.IsOpen, Is.False); Assert.That(panel.BlocksOtherUi, Is.True, "Closing Escape is consumed for this frame.");
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.Open(); Assert.That(settings.IsOpen, Is.False, "The same Escape must not open Settings."); Press();
            yield return null;
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            player.SetDialogueInputSuspended(true); panel.Open(); Assert.That(panel.IsOpen, Is.False);
            player.SetDialogueInputSuspended(false);
            panel.Open(); Assert.That(panel.Select("cheer"), Is.True);
            yield return null;
            panel.enabled = false;
            Assert.That(emote.IsActive, Is.False); Assert.That(player.IsInputSuspended || orbit.IsInputSuspended, Is.False);
            panel.enabled = true;
            panel.Open(); Assert.That(panel.IsOpen, Is.True); panel.Close();
            Assert.That(emote.MenuHandlesInput, Is.True);
        }
    }
}
