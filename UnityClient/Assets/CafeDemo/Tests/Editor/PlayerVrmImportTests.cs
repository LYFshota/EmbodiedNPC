using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerVrmImportTests
    {
        string root => SessionState.GetString("PlayerVrmImport.Root", "");
        string previousRoot => SessionState.GetString("PlayerVrmImport.PreviousRoot", "");
        PlayerVrmAppearance appearance;
        WorldEntity player;
        const string Scene = "Assets/Scenes/CafeDemoV2.unity";
        [UnitySetUp] public IEnumerator Load()
        {
            SessionState.SetString("PlayerVrmImport.Root", Path.Combine(Path.GetTempPath(), "EmbodiedNPC-AvatarTests-" + Guid.NewGuid().ToString("N")));
            SessionState.SetString("PlayerVrmImport.PreviousRoot", Environment.GetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT") ?? "");
            Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT", root);
            OpenOffline(); yield return new EnterPlayMode(); Ready(); yield return Frames(4);
            appearance = player.GetComponent<PlayerVrmAppearance>();
            Assert.That(appearance, Is.Not.Null);
            Assert.That(appearance.CurrentAnimator, Is.Not.Null, appearance.Diagnostic);
            Assert.That(appearance.Diagnostic, Is.Null);
        }
        static void OpenOffline()
        {
            EditorSceneManager.OpenScene(Scene); AnimationSceneTestIsolation.Prepare();
        }
        void Ready()
        {
            AnimationSceneTestIsolation.Ready(); Time.captureDeltaTime = 1f / 60;
            player = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "player_01");
            appearance = player.GetComponent<PlayerVrmAppearance>();
            foreach (var driver in Object.FindObjectsByType<EmbodiedNPC.Cognition.CognitiveRunDriver>(FindObjectsSortMode.None)) driver.enabled = false;
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, (Func<bool>)(() => true));
            typeof(CafeDemoDeveloperPanel).GetField("startupGetConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(settings, (Func<Action<ModelConfigView, string>, int, IEnumerator>)((callback, generation) => Configured(callback)));
            settings.enabled = true; // Initialize the real settings view through its existing offline test seam.
            settings.ApplyStartupConfig(new ModelConfigView { configured = true }); settings.Close();
            player.GetComponent<CafeDemoPlayerController>().SetDialogueInputSuspended(false);
        }
        static IEnumerator Configured(Action<ModelConfigView, string> callback)
        { callback(new ModelConfigView { configured = true }, null); yield break; }
        [UnityTearDown] public IEnumerator Close()
        {
            Time.captureDeltaTime = 0;
            if (Application.isPlaying) yield return new ExitPlayMode();
            Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT", string.IsNullOrEmpty(previousRoot) ? null : previousRoot);
            // Only this fixture's freshly generated directory, never Player data.
            if (root != null && root.StartsWith(Path.Combine(Path.GetTempPath(), "EmbodiedNPC-AvatarTests-"), StringComparison.Ordinal) && Directory.Exists(root))
                Directory.Delete(root, true);
        }
        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }
        static IEnumerator Complete(Task<bool> task, bool expected, PlayerVrmAppearance service)
        {
            float deadline = Time.realtimeSinceStartup + 60;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Import exceeded 60 seconds");
            Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
            Assert.That(task.Result, Is.EqualTo(expected), service.Diagnostic);
            yield return Frames(3);
        }
        void Capture(string name)
        {
            var go = new GameObject("VRM evidence camera"); var camera = go.AddComponent<Camera>();
            camera.transform.position = player.transform.position + player.transform.forward * 3.5f + Vector3.up;
            camera.transform.LookAt(player.transform.position + Vector3.up * .95f);
            camera.fieldOfView = 35; camera.backgroundColor = new Color(.16f, .18f, .22f); camera.clearFlags = CameraClearFlags.SolidColor;
            var parts = appearance.CurrentAnimator.GetComponentsInChildren<Transform>(true);
            var layers = parts.Select(x => x.gameObject.layer).ToArray();
            for (int i = 0; i < parts.Length; i++) parts[i].gameObject.layer = 31;
            camera.cullingMask = 1 << 31;
            var rt = RenderTexture.GetTemporary(480,640,24); var old = RenderTexture.active; var image = new Texture2D(480,640,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0,0,480,640),0,0); image.Apply();
                string directory = Path.GetFullPath("../PlayerVrmEvidence"); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory,name + ".png"),image.EncodeToPNG());
            }
            finally { for (int i = 0; i < parts.Length; i++) parts[i].gameObject.layer=layers[i]; RenderTexture.active=old;camera.targetTexture=null;RenderTexture.ReleaseTemporary(rt);Object.Destroy(image);Object.Destroy(go); }
        }
        string Copy(string asset, string name)
        {
            appearance.Store.EnsureDirectory(); string destination = Path.Combine(appearance.Store.DirectoryPath, name);
            File.Copy(asset, destination, true); return name;
        }
        string GuardModel() => Copy(Environment.GetEnvironmentVariable("EMBODIEDNPC_VRM_TEST_FILE") ??
            "Assets/CafeDemo/V2/Characters/薇薇.vrm", "GuardModel.vrm");

        [UnityTest] public IEnumerator PlayerSettingsPage_GroupsControlsWithoutOversizedTextRows()
        {
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            bool wasEnglish = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(false);
            try
            {
                settings.Open(); settings.PlayerNavigation.ShowAppearance(); yield return Frames(4);
                var content = Resources.FindObjectsOfTypeAll<RectTransform>().Single(x => x.name == "AppearanceContent");
                var general = settings.PlayerNavigation.GeneralPage;
                Assert.That(general.GetComponentsInChildren<TMPro.TMP_InputField>(true).Any(x => x.name == "PlayerNameInput"), Is.False);
                var name = content.GetComponentsInChildren<TMPro.TMP_InputField>().Single(x => x.name == "PlayerNameInput");
                var description = content.GetComponentsInChildren<TMPro.TMP_InputField>().Single(x => x.name == "AppearanceDescriptionInput");
                var choice = content.GetComponentsInChildren<TMPro.TMP_Dropdown>().Single(x => x.name == "PlayerAvatarChoice");
                Assert.That(name.transform.GetSiblingIndex(), Is.LessThan(description.transform.GetSiblingIndex()));
                Assert.That(description.transform.GetSiblingIndex(), Is.LessThan(choice.transform.GetSiblingIndex()));
                Assert.That(content.GetChild(0).GetComponent<TMPro.TMP_Text>().text, Is.EqualTo("玩家设定"));
                Assert.That(description.textComponent.lineSpacing, Is.Zero);
                Assert.That(description.textComponent.paragraphSpacing, Is.Zero);
                Assert.That(description.textComponent.characterSpacing, Is.Zero);
                Assert.That(((RectTransform)description.transform).rect.height, Is.EqualTo(120).Within(1));
                var actions = (RectTransform)content.Find("AppearanceDescriptionActions");
                foreach (var button in actions.GetComponentsInChildren<UnityEngine.UI.Button>())
                    Assert.That(((RectTransform)button.transform).rect.height, Is.LessThanOrEqualTo(actions.rect.height + 1));
                var footer = (RectTransform)content.parent.parent.Find("AvatarFooter");
                Assert.That(footer.GetComponentsInChildren<UnityEngine.UI.Button>().Length, Is.EqualTo(4));
                var children = content.Cast<Transform>().Where(x => x.gameObject.activeSelf).Select(x => (RectTransform)x).ToArray();
                for (int i = 1; i < children.Length; i++)
                {
                    float previousBottom = children[i-1].anchoredPosition.y - children[i-1].rect.height * children[i-1].pivot.y;
                    float nextTop = children[i].anchoredPosition.y + children[i].rect.height * (1-children[i].pivot.y);
                    Assert.That(nextTop, Is.LessThanOrEqualTo(previousBottom + 1), children[i].name + " overlaps previous row");
                }
                CaptureSettings("player-settings", 1920, 1080);
                description.SetTextWithoutNotify("银白色长发，自然垂在肩后。\n穿着浅蓝色外套和黑色短裙。\n整体风格简洁、轻便。");
                CaptureSettings("player-settings-description-preview", 1920, 1080);
                description.SetTextWithoutNotify("");
                settings.PlayerNavigation.ShowGeneral(); yield return Frames(2);
                Assert.That(name.gameObject.activeInHierarchy, Is.False);
                CaptureSettings("general-settings", 1920, 1080);
                settings.PlayerNavigation.ShowAppearance(); yield return Frames(2);
                Assert.That(name.gameObject.activeInHierarchy, Is.True);
            }
            finally { settings.CloseWindow(); SettingsUiLanguage.SetEnglish(wasEnglish); }
        }
        static void CaptureSettings(string name, int width, int height)
        {
            string evidence = Environment.GetEnvironmentVariable("EMBODIEDNPC_UI_LAYOUT_EVIDENCE");
            if (string.IsNullOrEmpty(evidence)) return;
            Directory.CreateDirectory(evidence);
            var camera = Camera.main;
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(x => x.isActiveAndEnabled && x.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            var modes = canvases.Select(x => (x.worldCamera, x.planeDistance)).ToArray();
            var rt = RenderTexture.GetTemporary(width,height,24); var previous = RenderTexture.active; var target = camera.targetTexture;
            var image = new Texture2D(width,height,TextureFormat.RGB24,false);
            // Overlay UI normally bypasses scene grading/AA; retain that appearance when rendering evidence to a texture.
            var cameraData = camera.GetComponents<Component>().FirstOrDefault(x => x.GetType().Name == "UniversalAdditionalCameraData");
            var postProperty = cameraData?.GetType().GetProperty("renderPostProcessing");
            var aaProperty = cameraData?.GetType().GetProperty("antialiasing");
            var post = postProperty?.GetValue(cameraData); var aa = aaProperty?.GetValue(cameraData);
            try
            {
                postProperty?.SetValue(cameraData,false);
                if (aaProperty != null) aaProperty.SetValue(cameraData,Enum.ToObject(aaProperty.PropertyType,0));
                camera.targetTexture = rt;
                foreach (var canvas in canvases) { canvas.renderMode=RenderMode.ScreenSpaceCamera; canvas.worldCamera=camera; canvas.planeDistance=1; }
                Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active=rt;
                image.ReadPixels(new Rect(0,0,width,height),0,0); image.Apply();
                File.WriteAllBytes(Path.Combine(evidence,name+".png"),image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=target; RenderTexture.active=previous;
                postProperty?.SetValue(cameraData,post); aaProperty?.SetValue(cameraData,aa);
                for(int i=0;i<canvases.Length;i++) { canvases[i].renderMode=RenderMode.ScreenSpaceOverlay; canvases[i].worldCamera=modes[i].worldCamera; canvases[i].planeDistance=modes[i].planeDistance; }
                RenderTexture.ReleaseTemporary(rt); Object.Destroy(image);
            }
        }

        [UnityTest] public IEnumerator ExternalSharedBufferModel_LoadsAndPreservesVisualTransaction()
        {
            string path = Environment.GetEnvironmentVariable("EMBODIEDNPC_VRM_TEST_FILE");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Optional external VRM fixture was not supplied.");
            var original = appearance.CurrentAnimator;
            string file = Copy(path, "External.vrm");
            Assert.That(appearance.Store.Scan(), Does.Contain(file));
            Assert.That(appearance.IsBusy, Is.False);
            yield return Complete(appearance.ImportScannedAsync(file), true, appearance);
            Assert.That(appearance.CurrentAnimator, Is.Not.SameAs(original));
            Assert.That(appearance.IsCustom, Is.True); Assert.That(appearance.Store.Selected, Is.Not.Null);
            var resources = appearance.CurrentAnimator.GetComponent<UniGLTF.RuntimeGltfInstance>();
            Debug.Log("[PlayerVRMTest] loaded_vertices=" + resources.Meshes.Sum(x => x.vertexCount));
            Assert.That(appearance.RestoreDefault(), Is.True, appearance.Diagnostic);
        }

        [UnityTest] public IEnumerator ClosedDialogueSettings_ImportsWithoutEndingRetainedConversation()
        {
            var movement = player.GetComponent<CafeDemoPlayerController>();
            var controller = player.GetComponent<CharacterController>();
            controller.enabled = false; player.transform.position = new Vector3(-.8f, .02f, 1.25f); controller.enabled = true;
            Physics.SyncTransforms(); yield return Frames(3);
            var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            string conversation = dialogue.CurrentConversationId;
            string model = GuardModel();
            Assert.That(appearance.IsBusy, Is.True, "Visible dialogue must still block replacement");
            yield return Complete(appearance.ImportScannedAsync(model), false, appearance);
            dialogue.HideDialoguePresentation("close_button");
            yield return Frames(4);
            Assert.That(dialogue.HasActiveSession, Is.True, "Closing input intentionally retains the conversation");
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            Assert.That(movement.IsInputSuspended, Is.False);
            Assert.That(appearance.IsBusy, Is.False, "Retained conversation is not an active player interaction");
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.Open(); settings.PlayerNavigation.ShowAppearance(); yield return Frames(4);
            Assert.That(movement.IsInputSuspended, Is.True, "Settings should suspend movement without becoming a busy action");
            Assert.That(appearance.IsBusy, Is.False);
            var dropdown = Resources.FindObjectsOfTypeAll<TMPro.TMP_Dropdown>().Single(x => x.name == "PlayerAvatarChoice");
            dropdown.value = dropdown.options.FindIndex(x => x.text == model);
            Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>().Single(x => x.name == "AvatarImport").onClick.Invoke();
            float deadline = Time.realtimeSinceStartup + 60;
            while ((!appearance.IsCustom || appearance.IsLoading) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(appearance.IsCustom, Is.True, appearance.Diagnostic);
            Assert.That(appearance.IsLoading, Is.False);
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            Assert.That(appearance.Store.Selected, Is.Not.Null);
            Assert.That(appearance.RestoreDefault(), Is.True, appearance.Diagnostic);
            settings.CloseWindow();
        }

        [UnityTest] public IEnumerator IdleSettingsMovementStopAndNaturalWaveCompletion_AllowImport()
        {
            string model = GuardModel();
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.Open(); settings.PlayerNavigation.ShowAppearance(); yield return Frames(4);
            Assert.That(appearance.IsBusy, Is.False);
            yield return Complete(appearance.ImportScannedAsync(model), true, appearance);
            settings.CloseWindow(); yield return Frames(4);
            var controller = player.GetComponent<CharacterController>();
            controller.enabled = false; player.transform.position = new Vector3(1.9f, .03f, 4.5f); controller.enabled = true;
            Physics.SyncTransforms();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            bool background = Application.runInBackground; var inputBackground = InputSystem.settings.backgroundBehavior;
            var editorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            Application.runInBackground = true; InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.EnableDevice(keyboard);
            try
            {
                for (int i = 0; i < 20; i++)
                { InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); InputSystem.Update(); yield return null; }
                Assert.That(appearance.IsBusy, Is.True, "Actual moving player must be blocked");
                yield return Complete(appearance.ImportScannedAsync(model), false, appearance);
            }
            finally
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update(); InputSystem.RemoveDevice(keyboard);
                Application.runInBackground = background; InputSystem.settings.backgroundBehavior = inputBackground;
                InputSystem.settings.editorInputBehaviorInPlayMode = editorInput;
            }
            yield return Frames(45);
            Assert.That(appearance.IsBusy, Is.False, "Movement guard releases after deceleration");
            yield return Complete(appearance.ImportScannedAsync(model), true, appearance);
            var emotes = player.GetComponent<PlayerEmoteController>();
            Assert.That(emotes.TryWave(), Is.True); yield return Frames(10);
            Assert.That(appearance.IsBusy, Is.True);
            yield return Complete(appearance.ImportScannedAsync(model), false, appearance);
            float end = Time.realtimeSinceStartup + 10;
            while (emotes.IsActive && Time.realtimeSinceStartup < end) yield return null;
            Assert.That(emotes.IsActive, Is.False, "Wave must release naturally without Cancel");
            Assert.That(appearance.IsBusy, Is.False);
            yield return Complete(appearance.ImportScannedAsync(model), true, appearance);
            Assert.That(appearance.RestoreDefault(), Is.True, appearance.Diagnostic);
        }
        [UnityTest] public IEnumerator RealFiles_TransactionPlayerIdentityWalkWaveCameraAndRepeatedCleanup()
        {
            var original = appearance.CurrentAnimator; string id = player.EntityId;
            Assert.That(player.TryGetPersistentIdentityKey(out var identity), Is.True);
            var controller = player.GetComponent<CharacterController>(); var camera = Object.FindAnyObjectByType<CafeDemoOrbitCamera>(); var target = camera.Target;
            var movement = player.GetComponent<CafeDemoPlayerController>(); var emotes = player.GetComponent<PlayerEmoteController>();
            var hold = player.GetComponentInChildren<CarryAnchorFollower>(true);
            string vivi = Copy("Assets/CafeDemo/V2/Characters/薇薇.vrm", "Vivi.vrm");
            yield return Complete(appearance.ImportScannedAsync(vivi), true, appearance);
            Assert.That(appearance.IsCustom, Is.True); Assert.That(original.isActiveAndEnabled, Is.False);
            var loaded = appearance.CurrentAnimator; var resources = loaded.GetComponent<UniGLTF.RuntimeGltfInstance>();
            Assert.That(loaded.GetComponent<UniVRM10.Vrm10Instance>().Runtime.SpringBone, Is.TypeOf<UniVRM10.Vrm10FastSpringboneRuntimeStandalone>());
            var allocated = resources.RuntimeResources.Select(x => x.Item2).ToArray();
            Assert.That(player.EntityId, Is.EqualTo(id)); Assert.That(player.TryGetPersistentIdentityKey(out var after), Is.True); Assert.That(after, Is.EqualTo(identity));
            Assert.That(player.GetComponent<CharacterController>(), Is.SameAs(controller)); Assert.That(camera.Target, Is.SameAs(target));
            Assert.That(player.GetComponent<PlayerLocomotionPresenter>().Animator, Is.SameAs(loaded));
            Assert.That(emotes.Animator, Is.SameAs(loaded));
            if (hold != null) Assert.That(hold.RightHand, Is.SameAs(loaded.GetBoneTransform(HumanBodyBones.RightHand)));
            Assert.That(loaded.GetCurrentAnimatorClipInfo(0).Any(x => x.clip.name.ToLowerInvariant().Contains("idle")), Is.True);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            bool background = Application.runInBackground; var inputBackground = InputSystem.settings.backgroundBehavior;
            var editorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            Application.runInBackground = true; InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.EnableDevice(keyboard);
            controller.enabled = false; player.transform.position = new Vector3(1.9f, .03f, 4.5f); controller.enabled = true;
            Physics.SyncTransforms(); yield return Frames(20);
            Vector3 start = player.transform.position;
            try
            {
                for (int i = 0; i < 25; i++)
                { InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); InputSystem.Update(); yield return null; }
                Assert.That(Vector3.ProjectOnPlane(player.transform.position - start, Vector3.up).magnitude, Is.GreaterThan(.1f));
                Assert.That(loaded.GetFloat("Speed"), Is.GreaterThan(.1f));
                Assert.That(loaded.GetCurrentAnimatorClipInfo(0).Any(x => x.clip.name.ToLowerInvariant().Contains("walk")), Is.True);
            }
            finally { InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update(); InputSystem.RemoveDevice(keyboard);
                Application.runInBackground = background; InputSystem.settings.backgroundBehavior = inputBackground;
                InputSystem.settings.editorInputBehaviorInPlayMode = editorInput; }
            yield return Frames(35);
            controller.enabled = false; player.transform.position = new Vector3(-.8f, .02f, 1.25f); controller.enabled = true;
            Physics.SyncTransforms(); yield return Frames(3);
            var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
            Assert.That(interactor.TryBeginDialogue(), Is.True, "Imported player must retain NPC interaction");
            Assert.That(Object.FindAnyObjectByType<DialogueController>().HasActiveSession, Is.True);
            yield return Complete(appearance.ImportScannedAsync(vivi), false, appearance);
            Object.FindAnyObjectByType<DialogueController>().ClearSession(); movement.SetDialogueInputSuspended(false);
            yield return Frames(3);
            Capture("custom-idle");
            Assert.That(emotes.TryWave(), Is.True); yield return Frames(20);
            int layer = loaded.GetLayerIndex(PlayerEmoteController.WaveLayerName);
            Assert.That(loaded.GetCurrentAnimatorStateInfo(layer).IsName(PlayerEmoteController.WaveLayerName + ".Wave"), Is.True);
            Assert.That(loaded.GetLayerWeight(layer), Is.GreaterThan(.5f)); Capture("custom-wave");
            yield return Complete(appearance.ImportScannedAsync(vivi), false, appearance); // Busy retains the live emote.
            Assert.That(emotes.IsActive, Is.True); Assert.That(appearance.CurrentAnimator, Is.SameAs(loaded));
            emotes.Cancel(); yield return Frames(3);
            string xingmang = Copy("Assets/VRMCompatibilityTest/Xingmang.vrm", "Xingmang.vrm");
            yield return Complete(appearance.ImportScannedAsync(xingmang), true, appearance); // VRM 0.x migration.
            Assert.That(loaded == null, Is.True);
            Assert.That(allocated.All(x => x == null), Is.True, "Retired RuntimeGltf resources leaked");
            Assert.That(player.GetComponentsInChildren<Animator>().Length, Is.EqualTo(1));
            Assert.That(Object.FindObjectsByType<UniGLTF.RuntimeGltfInstance>(FindObjectsSortMode.None).Count(x => x.transform.IsChildOf(player.transform)), Is.EqualTo(1));
            Assert.That(appearance.RestoreDefault(), Is.True, appearance.Diagnostic); yield return Frames(3);
            Assert.That(appearance.CurrentAnimator, Is.SameAs(original)); Assert.That(appearance.IsCustom, Is.False);
            Assert.That(appearance.Store.Selected, Is.Null); Assert.That(emotes.TryWave(), Is.True); emotes.Cancel();
            Assert.That(camera.Target, Is.SameAs(target)); Assert.That(player.EntityId, Is.EqualTo(id));
        }
        [UnityTest] public IEnumerator InvalidCorruptOversizeReferencesAndSaveFailure_KeepOriginalVisual()
        {
            var original = appearance.CurrentAnimator;
            appearance.Store.EnsureDirectory();
            File.WriteAllBytes(Path.Combine(appearance.Store.DirectoryPath, "broken.vrm"), new byte[128]);
            yield return Complete(appearance.ImportScannedAsync("broken.vrm"), false, appearance);
            yield return Complete(appearance.ImportScannedAsync("missing.vrm"), false, appearance);
            yield return Complete(appearance.ImportScannedAsync("../escape.vrm"), false, appearance);
            string big = Path.Combine(appearance.Store.DirectoryPath, "big.vrm");
            using (var stream = File.Create(big)) stream.SetLength(PlayerAvatarStore.MaxFileBytes + 1);
            yield return Complete(appearance.ImportScannedAsync("big.vrm"), false, appearance);
            Assert.That(appearance.CurrentAnimator, Is.SameAs(original)); Assert.That(original.isActiveAndEnabled, Is.True);
            Assert.That(appearance.Store.Selected, Is.Null);
            Assert.Throws<IOException>(() => appearance.Store.ResolveSelection("../../outside.vrm"));
            string valid = Copy("Assets/CafeDemo/V2/Characters/薇薇.vrm", "Vivi.vrm");
            // File.Replace must fail: this tests rollback after a successful motion rebind.
            File.Delete(Path.Combine(root, "settings.json")); Directory.CreateDirectory(Path.Combine(root, "settings.json"));
            yield return Complete(appearance.ImportScannedAsync(valid), false, appearance);
            Assert.That(appearance.CurrentAnimator, Is.SameAs(original)); Assert.That(appearance.IsCustom, Is.False);
            Assert.That(player.GetComponent<PlayerLocomotionPresenter>().Animator, Is.SameAs(original));
            Assert.That(player.GetComponent<PlayerEmoteController>().Animator, Is.SameAs(original));
            Assert.That(player.GetComponentsInChildren<Animator>().Length, Is.EqualTo(1));
            Assert.That(player.GetComponent<PlayerEmoteController>().TryWave(), Is.True); player.GetComponent<PlayerEmoteController>().Cancel();
        }
        [UnityTest] public IEnumerator SavedAvatar_RestoresAcrossSceneReload_AndMissingFileFallsBack()
        {
            string valid = Copy("Assets/CafeDemo/V2/Characters/薇薇.vrm", "Vivi.vrm");
            yield return Complete(appearance.ImportScannedAsync(valid), true, appearance);
            string selected = appearance.Store.Selected; SessionState.SetString("PlayerVrmImport.Selected", selected);
            Assert.That(Path.IsPathRooted(selected), Is.False); string preserved = appearance.Store.ResolveSelection(selected);
            Assert.That(File.Exists(preserved), Is.True); SessionState.SetString("PlayerVrmImport.Preserved", preserved);
            yield return new ExitPlayMode(); OpenOffline(); yield return new EnterPlayMode(); Ready();
            float deadline = Time.realtimeSinceStartup + 60;
            while ((!appearance.IsCustom || appearance.IsLoading) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(appearance.IsCustom, Is.True, appearance.Diagnostic); Assert.That(appearance.Store.Selected, Is.EqualTo(SessionState.GetString("PlayerVrmImport.Selected", "")));
            yield return new ExitPlayMode(); File.Delete(SessionState.GetString("PlayerVrmImport.Preserved", "")); OpenOffline(); yield return new EnterPlayMode(); Ready(); yield return Frames(20);
            while (!appearance.RestorationComplete || appearance.IsLoading) yield return null;
            Assert.That(appearance.IsCustom, Is.False); Assert.That(appearance.CurrentAnimator.isActiveAndEnabled, Is.True);
            Assert.That(appearance.Store.Selected, Is.Null); Debug.Log("[AvatarMissing] directory=" + appearance.Store.DirectoryPath + "; selected=" + appearance.Store.Selected + "; diagnostic=" + appearance.Diagnostic);
            Assert.That(appearance.Diagnostic, Is.Not.Null.And.Not.Empty);
        }
    }

    public sealed class PlayerVrmCapacityTests
    {
        [TestCase(400000, true)]
        [TestCase(400001, false)]
        public void MaterialSplitCapacity_RemainsBounded(int verticesPerPrimitive, bool accepted)
        {
            var gltf = new UniGLTF.glTF();
            gltf.accessors.Add(new UniGLTF.glTFAccessor { count = verticesPerPrimitive });
            var mesh = new UniGLTF.glTFMesh(); gltf.meshes.Add(mesh);
            // Reusing POSITION does not remove UniGLTF's temporary primitive capacity allocation.
            for (int i = 0; i < 3; i++) mesh.primitives.Add(new UniGLTF.glTFPrimitives
                { attributes = new UniGLTF.glTFAttributes { POSITION = 0 } });
            using var data = new UniGLTF.GltfData("", "{}", gltf, null, null, new UniGLTF.MigrationFlags());
            if (accepted) Assert.DoesNotThrow(() => PlayerVrmBudget.Validate(data));
            else Assert.Throws<InvalidDataException>(() => PlayerVrmBudget.Validate(data));
        }
    }
}
