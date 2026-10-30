#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CafeDemo;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo.Tests
{
    /// <summary>Only installed into the generated offline acceptance scene, never a production scene.</summary>
    public sealed class PlayerVrmStandaloneProbe : MonoBehaviour
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static async Task Frames(int n) { for (int i = 0; i < n; i++) await Task.Yield(); }
        private static void Capture(string path)
        {
            var camera = Camera.main;
            var canvases = FindObjectsByType<Canvas>().Where(x => x.isActiveAndEnabled && x.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            var modes = canvases.Select(x => (x.renderMode, x.worldCamera, x.planeDistance)).ToArray();
            var rt = RenderTexture.GetTemporary(1280,720,24); var previous = RenderTexture.active; var previousTarget = camera.targetTexture;
            var image = new Texture2D(1280,720,TextureFormat.RGB24,false);
            try
            {
                foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1; }
                Canvas.ForceUpdateCanvases(); camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                image.ReadPixels(new Rect(0,0,1280,720),0,0); image.Apply(); File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); Destroy(image);
                for (int i = 0; i < canvases.Length; i++) { canvases[i].renderMode=modes[i].Item1;canvases[i].worldCamera=modes[i].Item2;canvases[i].planeDistance=modes[i].Item3; }
            }
        }
        private async void Start()
        {
            string phase = Environment.GetEnvironmentVariable("EMBODIEDNPC_AVATAR_PROBE_PHASE");
            if (string.IsNullOrEmpty(phase)) return;
            string root = Environment.GetEnvironmentVariable(PortableDataPaths.EnvironmentKey);
            try
            {
                DisplaySettings.Select(3);
                await Frames(30);
                var appearance = FindAnyObjectByType<PlayerVrmAppearance>();
                Check(appearance != null && appearance.CurrentAnimator != null, "Player appearance bootstrap failed");
                float deadline = Time.realtimeSinceStartup + 60;
                while ((!appearance.RestorationComplete || appearance.IsLoading) && Time.realtimeSinceStartup < deadline) await Task.Yield();
                Check(!appearance.IsLoading, "Restore timed out");
                var player = appearance.GetComponent<WorldEntity>();
                string id = player.EntityId; Check(player.TryGetPersistentIdentityKey(out var identity), "Missing persistent identity");
                var movement = player.GetComponent<CafeDemoPlayerController>();
                var settings = FindAnyObjectByType<CafeDemoDeveloperPanel>();
                settings.enabled = false; // Stop network startup in this offline fixture; keep the actual settings view.
                typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(settings, (Func<bool>)(() => true));
                settings.ApplyStartupConfig(new ModelConfigView { configured = true }); settings.Close();
                FindAnyObjectByType<DialogueController>().SetSetupReady(true);
                movement.SetDialogueInputSuspended(false);
                var orbit = FindAnyObjectByType<CafeDemoOrbitCamera>(); var target = orbit.Target;
                if (phase == "idle_guard")
                {
                    var controller = player.GetComponent<CharacterController>();
                    controller.enabled = false; player.transform.position = new Vector3(-.8f, .02f, 1.25f); controller.enabled = true;
                    Physics.SyncTransforms(); await Frames(5);
                    var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
                    Check(interactor.TryBeginDialogue(), "Could not start the ordinary NPC interaction");
                    var dialogue = FindAnyObjectByType<DialogueController>();
                    string conversation = dialogue.CurrentConversationId;
                    Check(!await appearance.ImportScannedAsync("GuardModel.vrm"), "Visible dialogue must reject import");
                    dialogue.HideDialoguePresentation("close_button"); await Frames(5);
                    Check(dialogue.HasActiveSession && !dialogue.IsPresentationVisible, "Closing input must retain the conversation");
                    Check(!appearance.IsBusy, "Retained conversation still blocks idle player");
                    settings.Open(); settings.PlayerNavigation.ShowAppearance(); await Frames(5);
                    Check(movement.IsInputSuspended && !appearance.IsBusy, "Settings suspension must allow replacement");
                    var dropdown = Resources.FindObjectsOfTypeAll<TMP_Dropdown>().Single(x => x.name == "PlayerAvatarChoice");
                    int choice = dropdown.options.FindIndex(x => x.text == "GuardModel.vrm");
                    Check(choice >= 0, "Real VRM was not scanned"); dropdown.value = choice;
                    Resources.FindObjectsOfTypeAll<Button>().Single(x => x.name == "AvatarImport").onClick.Invoke();
                    deadline = Time.realtimeSinceStartup + 60;
                    while ((!appearance.IsCustom || appearance.IsLoading) && Time.realtimeSinceStartup < deadline) await Task.Yield();
                    Check(appearance.IsCustom && !appearance.IsLoading, "Settings import failed: " + appearance.Diagnostic);
                    Check(dialogue.CurrentConversationId == conversation, "Visual swap changed retained conversation");
                    Check(File.Exists(appearance.Store.ResolveSelection(appearance.Store.Selected)), "Confirmed avatar was not persisted");
                    settings.CloseWindow();
                }
                else if (phase == "import")
                {
                    // Exercise the actual settings controls; no Editor file selection or loading APIs.
                    settings.Open(); settings.PlayerNavigation.ShowAppearance();
                    await Frames(4); Capture(Path.Combine(root, "appearance-ui.png")); await Frames(4);
                    float? footerY = null;
                    foreach (string buttonName in new[] { "AvatarOpenFolder", "AvatarScan", "AvatarImport", "AvatarDefault" })
                    {
                        var button = Resources.FindObjectsOfTypeAll<Button>().Single(x => x.name == buttonName);
                        float y = button.transform.position.y;
                        if (footerY.HasValue) Check(Mathf.Abs(y - footerY.Value) < 1, "Avatar operations must share one footer row");
                        footerY = y;
                        Check(((RectTransform)button.transform).rect.height <= 60, "Avatar button stretched: " + buttonName + " height=" + ((RectTransform)button.transform).rect.height);
                    }
                    Capture(Path.Combine(root, "appearance-ui.png")); await Frames(4);
                    var dropdown = Resources.FindObjectsOfTypeAll<TMP_Dropdown>().Single(x => x.name == "PlayerAvatarChoice");
                    Check(dropdown.options.Any(x => x.text == "Vivi.vrm"), "VRM scan did not populate settings");
                    dropdown.value = dropdown.options.FindIndex(x => x.text == "Vivi.vrm");
                    Resources.FindObjectsOfTypeAll<Button>().Single(x => x.name == "AvatarImport").onClick.Invoke();
                    deadline = Time.realtimeSinceStartup + 60;
                    while ((!appearance.IsCustom || appearance.IsLoading) && Time.realtimeSinceStartup < deadline) await Task.Yield();
                    Check(appearance.IsCustom && !appearance.IsLoading, "Settings import failed: " + appearance.Diagnostic);
                    settings.Close(); movement.SetDialogueInputSuspended(false);
                    Check(player.GetComponent<PlayerEmoteController>().TryWave(), "Imported player Wave rejected");
                    await Frames(25);
                    Check(player.GetComponent<PlayerEmoteController>().IsActive, "Wave did not remain active");
                    player.GetComponent<PlayerEmoteController>().Cancel();
                    Check(File.Exists(appearance.Store.ResolveSelection(appearance.Store.Selected)), "Confirmed avatar was not copied");
                }
                else if (phase == "restore")
                {
                    Check(appearance.IsCustom, "Saved avatar was not restored: " + appearance.Diagnostic);
                    Check(appearance.RestoreDefault(), "Restore default failed: " + appearance.Diagnostic);
                    await Frames(3); Check(!appearance.IsCustom && appearance.Store.Selected == null, "Default selection not persisted");
                }
                else if (phase == "missing") Check(!appearance.IsCustom && appearance.Store.Selected == null, "Missing file did not fall back");
                else throw new Exception("Unknown probe phase");
                Check(player.EntityId == id && player.TryGetPersistentIdentityKey(out var after) && after.Equals(identity), "Player identity changed");
                Check(orbit.Target == target, "Orbit target changed");
                Check(player.GetComponentsInChildren<Animator>().Length == 1, "Duplicate active Animator");
                Capture(Path.Combine(root, phase + ".png")); await Frames(4);
                File.WriteAllText(Path.Combine(root, phase + "-result.txt"), "PASS: " + phase + "; identity=" + id + "; custom=" + appearance.IsCustom + "; screen=" + Screen.width + "x" + Screen.height);
                Application.Quit(0);
            }
            catch (Exception failure)
            {
                File.WriteAllText(Path.Combine(root, phase + "-result.txt"), "FAIL: " + failure);
                Debug.LogException(failure); Application.Quit(1);
            }
        }
    }
}
#endif
