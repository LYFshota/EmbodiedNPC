using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class SettingsInformationArchitectureP1Tests
    {
        private const string Session = "SettingsP1.Tests.";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string Evidence => Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/SettingsP1"));
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static JArray Writes
        {
            get
            {
                using (var file = new FileStream(Path.Combine(Evidence, "stats.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file)) return (JArray)JObject.Parse(reader.ReadToEnd())["writes"];
            }
        }
        private static Button Button(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(x => x.name == name);
        private static void Patch() => Set(Find<BackendWebSocketClient>(), "serverUrl", "ws://127.0.0.1:18846/ws");

        [UnitySetUp] public IEnumerator Setup()
        {
            if (!File.Exists(Path.Combine(Evidence, "fake-ready.txt"))) Assert.Ignore("Start SettingsP1FakeService.py first.");
            SessionState.SetString(Session + "scene", SceneManager.GetActiveScene().path);
            SessionState.SetBool(Session + "hadVolume", PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetFloat(Session + "volume", PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session + "hadLanguage", PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));
            SessionState.SetInt(Session + "language", PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            File.WriteAllText(Path.Combine(Evidence, "mode.txt"), "ready");
            using (var request = UnityWebRequest.Get("http://127.0.0.1:18846/fixture/reset")) yield return request.SendWebRequest();
            bool cafeTest = NUnit.Framework.TestContext.CurrentContext.Test.MethodName.StartsWith("T3_");
            EditorSceneManager.OpenScene(cafeTest ? "Assets/Scenes/CafeDemoV2.unity" : "Assets/Scenes/TitleScene.unity"); Patch();
            if (cafeTest)
            {
                PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey, .27f);
                foreach (var recorder in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None)) recorder.enabled = false;
            }
            else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            yield return new EnterPlayMode();
            if (SceneManager.GetActiveScene().name == "CafeDemoV2") yield return Wait(() => Find<DialogueController>()?.IsSetupReady == true);
            else yield return Wait(() => Find<TitleScreenController>()?.CanEnter == true);
        }
        private static IEnumerator Disconnect()
        {
            foreach (var client in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsSortMode.None))
            { var task = client.DisconnectAsync(); while (!task.IsCompleted) yield return null; }
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            File.WriteAllText(Path.Combine(Evidence, NUnit.Framework.TestContext.CurrentContext.Test.MethodName + "-writes.json"), Writes.ToString());
            if (Application.isPlaying) { yield return Disconnect(); yield return new ExitPlayMode(); }
            if (SessionState.GetBool(Session + "hadVolume", false)) PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey, SessionState.GetFloat(Session + "volume", .5f));
            else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            SettingsUiLanguage.SetEnglish(SessionState.GetInt(Session + "language", 0) == 1);
            if (!SessionState.GetBool(Session + "hadLanguage", false)) PlayerPrefs.DeleteKey("EmbodiedNPC.Settings.Language");
            PlayerPrefs.Save();
            File.WriteAllText(Path.Combine(Evidence, "mode.txt"), "ready");
            yield return null;
            var previous = SessionState.GetString(Session + "scene", "Assets/Scenes/CafeDemoV2.unity");
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous) ? "Assets/Scenes/CafeDemoV2.unity" : previous);
        }
        private static IEnumerator Wait(Func<bool> condition, float seconds = 12)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < end) yield return null;
            string detail = "";
            var host = Find<TitleSettingsHost>(); var form = Find<FirstRunModelSetup>();
            if (form != null) detail = " busy=" + form.IsSavingSettings + " revision=" + Get(form, "revision") + " status=" + ((TMP_Text)Get(form, "statusText")).text;
            Assert.IsTrue(condition(), "Settings P1 timeout; scene=" + SceneManager.GetActiveScene().name + detail);
        }
        private static IEnumerator Capture(string name)
        { yield return null; ScreenCapture.CaptureScreenshot(Path.Combine(Evidence, name + ".png")); yield return new WaitForSecondsRealtime(.25f); }

        private static void CheckDeveloperEntryHit()
        {
            Canvas.ForceUpdateCanvases();
            var button = Button("DeveloperToolsEntry");
            var pointer = new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(null, button.transform.position) };
            var hits = new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Assert.IsNotEmpty(hits);
            Assert.AreSame(button, hits[0].gameObject.GetComponentInParent<Button>(), "AI footer must not cover the developer entry: " + hits[0].gameObject.name);
        }

        [UnityTest] public IEnumerator T1_TitleNavigation_DeveloperReachability_Preferences()
        {
            int writes = Writes.Count;
            var title = Find<TitleScreenController>(); title.OpenSettings();
            var host = Find<TitleSettingsHost>();
            yield return Wait(() => !host.IsBusy);
            Assert.IsTrue(host.Navigation.GeneralPage.activeInHierarchy);
            Assert.IsFalse(host.transform.Find("SharedSettings/Window/SettingsNavigation").gameObject.activeInHierarchy);
            Button("AudioSettingsNav").onClick.Invoke();
            var slider = host.Navigation.AudioPage.GetComponentInChildren<Slider>();
            Assert.AreEqual(.5f, slider.value, .001f);
            slider.value = .27f;
            Assert.AreEqual(.27f, Find<CafeBackgroundMusic>().Volume, .001f);
            Button("GeneralSettingsNav").onClick.Invoke();
            var language = host.Navigation.GeneralPage.GetComponentInChildren<TMP_Dropdown>();
            language.value = 1; Assert.AreEqual(1, PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            language.value = 0;
            yield return Capture("title-settings-1920");
            Button("AiSettingsNav").onClick.Invoke(); yield return null;
            var form = Find<FirstRunModelSetup>();
            Assert.IsFalse(form.AdvancedSettingsRoot.gameObject.activeSelf);
            Assert.IsFalse(((GameObject)Get(form, "embeddingFields")).activeInHierarchy);
            yield return Capture("title-ai-1920");
            CheckDeveloperEntryHit();
            Button("DeveloperToolsEntry").onClick.Invoke(); Assert.IsTrue(host.Navigation.IsDeveloper);
            Button("ModelMaintenanceNav").onClick.Invoke(); yield return Wait(() => !host.IsBusy);
            Assert.IsTrue(((Button)Get(form, "contractTest")).gameObject.activeInHierarchy);
            ((Button)Get(form, "contractTest")).onClick.Invoke();
            Assert.IsTrue(form.BlocksSettingsNavigation);
            Assert.IsFalse(host.TryClose());
            var modal = (GameObject)Get(form, "profileModal");
            modal.GetComponentsInChildren<Button>().Single(b => b.GetComponentInChildren<TMP_Text>().text is "取消" or "Cancel").onClick.Invoke();
            yield return null;
            Assert.IsFalse(host.TryClose(), "First back exits developer tools only.");
            Assert.IsFalse(host.Navigation.IsDeveloper); Assert.IsTrue(host.IsOpen);
            Assert.IsTrue(host.TryClose());
            Assert.AreEqual(writes, Writes.Count, "Ordinary preferences, navigation and canceled probes cannot save Backend config.");
        }

        [UnityTest] public IEnumerator T3_CafeNavigation_DeveloperReachability_Focus()
        {
            int writes = Writes.Count;
            Assert.AreEqual(.27f, Find<CafeBackgroundMusic>().Volume, .001f, "Shared preference survives scene host changes.");
            var cafe = Find<CafeDemoDeveloperPanel>(); cafe.Open(); yield return null;
            Assert.IsTrue(cafe.PlayerNavigation.GeneralPage.activeInHierarchy);
            Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);
            Assert.IsTrue(Find<CafeDemoOrbitCamera>().IsInputSuspended);
            Button("AudioSettingsNav").onClick.Invoke(); Assert.IsTrue(cafe.PlayerNavigation.AudioPage.activeInHierarchy);
            Button("AiSettingsNav").onClick.Invoke(); yield return Wait(() => !cafe.ModelForm.BlocksSettingsNavigation && cafe.ModelForm.AdvancedSettingsRoot != null);
            CheckDeveloperEntryHit();
            Button("GeneralSettingsNav").onClick.Invoke();
            yield return Capture("cafe-settings-1920");
            Button("DeveloperToolsEntry").onClick.Invoke();
            foreach (int i in new[] { 0, 1, 2, 3, 4, 5 })
            {
                Button("DeveloperTool" + i).onClick.Invoke(); yield return null;
                string field = new[] { "overviewPage", "npcPage", "knowledgePage", "", "debugPage", "tracePage" }[i];
                if (field != "") Assert.IsTrue(((GameObject)Get(cafe, field)).activeInHierarchy, field);
            }
            Button("DeveloperTool2").onClick.Invoke(); yield return null;
            var knowledge = Find<CafeWorldKnowledgePanel>();
            yield return Wait(() => !knowledge.IsBusy);
            Set(knowledge, "dirty", true); // UI-only draft, never submit a Knowledge mutation.
            Button("BackToPlayerSettings").onClick.Invoke();
            Assert.IsTrue(knowledge.ConfirmationVisible); Assert.IsTrue(cafe.PlayerNavigation.IsDeveloper);
            cafe.SendMessage("OnSettingsKey"); Assert.IsTrue(knowledge.ConfirmationVisible); Assert.IsTrue(cafe.IsOpen);
            knowledge.transform.Find("KnowledgeConfirmation/Dialog/ConfirmChoice2").GetComponent<Button>().onClick.Invoke();
            Set(knowledge, "dirty", false);
            Button("DeveloperTool3").onClick.Invoke(); yield return null;
            Assert.IsNotNull(cafe.PlayerNavigation.DailyDebugRoot.Find("DailyBehaviorModeRow"));
            Assert.IsNull(cafe.ModelForm.AdvancedSettingsRoot.Find("DailyBehaviorModeRow"));
            Button("ModelMaintenanceNav").onClick.Invoke(); yield return Wait(() => !cafe.ModelForm.BlocksSettingsNavigation);
            Assert.IsTrue(((Button)Get(cafe.ModelForm, "contractTest")).gameObject.activeInHierarchy);
            Assert.IsTrue(((Button)Get(cafe.ModelForm, "indexAction")).transform.IsChildOf((Transform)Get(cafe.ModelForm, "developerDiagnostics")));
            cafe.SendMessage("OnSettingsKey"); Assert.IsTrue(cafe.IsOpen); Assert.IsFalse(cafe.PlayerNavigation.IsDeveloper);
            cafe.SendMessage("OnSettingsKey"); Assert.IsFalse(cafe.IsOpen);
            var player = Find<CafeDemoPlayerController>();
            var npc = Object.FindObjectsByType<DialogueParticipant>(FindObjectsSortMode.None).First(x => x.Entity.EntityId == "npc_cafe_regular_01");
            var character = player.GetComponent<CharacterController>(); character.enabled = false;
            player.transform.position = npc.transform.position + Vector3.back * 1.2f; character.enabled = true;
            Set(Find<CafeDemoOrbitCamera>(), "yaw", 0f); Set(Find<CafeDemoOrbitCamera>(), "pitch", 12f);
            yield return null; yield return null;
            var interactor = player.GetComponent<PlayerDialogueInteractor>(); Assert.IsNotNull(interactor, "Player interactor binding");
            Assert.IsTrue(interactor.TryBeginDialogue()); yield return null;
            var input = Find<DialogueController>().DialogueInputField; Assert.IsNotNull(input, "Dialogue TMP binding"); input.text = "P1 未发送草稿";
            cafe.Open(); yield return null; cafe.Close(); yield return null;
            Assert.AreEqual("P1 未发送草稿", input.text); Assert.IsTrue(Find<TextUtteranceInput>().IsInputFocused);
            Assert.AreEqual(writes, Writes.Count);
        }

        [UnityTest] public IEnumerator T2_OriginalJointSave_KeyActions_SeparateContextRevision_ActivationRecheck()
        {
            Find<TitleScreenController>().OpenSettings(); var host = Find<TitleSettingsHost>();
            yield return Wait(() => !host.IsBusy);
            Button("AiSettingsNav").onClick.Invoke();
            var form = Find<FirstRunModelSetup>();
            yield return Wait(() => Get(form, "contextPolicyView") != null);
            int before = Writes.Count;
            string oldActive = ((ModelProfilesView)Get(form, "profilesView")).active_runtime.main_model.model;
            ((TMP_InputField)Get(form, "mainModel")).text = oldActive + "-edited";
            ((TMP_Dropdown)Get(form, "recentMode")).value = 1; ((TMP_InputField)Get(form, "recentTarget")).text = "32";
            File.WriteAllText(Path.Combine(Evidence, "mode.txt"), "contextfail");
            ((Button)Get(form, "saveButton")).onClick.Invoke();
            yield return Wait(() => Writes.Count >= before + 2 && !host.IsBusy && (int)Get(form, "revision") > 7);
            yield return null;
            var first = Writes[before]; var second = Writes[before + 1];
            Assert.AreEqual("profiles", (string)first["resource"]); Assert.AreEqual("context", (string)second["resource"]);
            CollectionAssert.AreEquivalent(new[] { "main", "embedding" }, first["slots"].Values<string>().ToArray());
            Assert.AreEqual("retain", (string)first["key_actions"]["main"]);
            var profiles = (ModelProfilesView)Get(form, "profilesView");
            Assert.IsTrue(profiles.restart_required); Assert.AreEqual(oldActive, profiles.active_runtime.main_model.model);
            Assert.IsTrue(host.IsOpen); Assert.AreEqual("32", ((TMP_InputField)Get(form, "recentTarget")).text);
            Assert.IsFalse(((TMP_Text)Get(form, "statusText")).text.Contains("配置已生效"));
            File.WriteAllText(Path.Combine(Evidence, "mode.txt"), "ready");
            yield return Disconnect(); Find<BackendWebSocketClient>().EnsureConnected();
            yield return Wait(() => (bool)Get(form, "contextPolicyConnected"));
            ((Button)Get(form, "saveButton")).onClick.Invoke();
            yield return Wait(() => Writes.Count >= before + 3 && !host.IsBusy);
            Assert.AreEqual("context", (string)Writes[before + 2]["resource"], "Context retry must not stage the profiles again.");
            Assert.AreEqual((int)second["expected_revision"], (int)Writes[before + 2]["expected_revision"]);
            Assert.IsFalse((bool)typeof(FirstRunModelSetup).GetMethod("ContextPolicyDirty", Private).Invoke(form, null), "Context retry: " + Newtonsoft.Json.JsonConvert.SerializeObject(Get(form, "contextPolicyView")) + " UI=" + ((TMP_Text)Get(form, "recentState")).text);
            foreach (string action in new[] { "replace", "clear", "retain" })
            {
                ((TMP_InputField)Get(form, "mainApiKey")).text = action == "replace" ? "synthetic-only" : "";
                Set(form, "mainClearKey", action == "clear");
                int count = Writes.Count; ((Button)Get(form, "saveButton")).onClick.Invoke();
                yield return Wait(() => Writes.Count > count && !host.IsBusy);
                Assert.AreEqual(action, (string)Writes[count]["key_actions"]["main"]);
            }
            using (var activate = UnityWebRequest.Get("http://127.0.0.1:18846/fixture/activate")) yield return activate.SendWebRequest();
            // Use the original public recheck path; successful activation still returns through Title's readiness check.
            form.ApplyRuntimeStatus(new RuntimeStatusView { model_config = new ModelConfigStatus { configured = true }, active_provider = new ActiveProviderStatus { available = true } }, false);
            yield return Wait(() => !host.IsOpen && Find<TitleScreenController>().CanEnter);
            File.WriteAllText(Path.Combine(Evidence, "T2-writes.json"), Writes.ToString());
        }
    }
}
