using System.Collections;
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

namespace CafeDemo.Tests
{
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
