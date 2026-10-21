using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Opt-in real transport/model test. Requires the isolated external harness.</summary>
    public sealed class NpcReplyLanguageRealTests
    {
        private const string Root = @"D:\EmbodiedNPC-LanguageProduction-20261018";
        private const string State = "NpcReplyLanguageRealTests.";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly List<PresentedCognitiveUtterance> Displayed = new();
        private static readonly JArray Checks = new();
        private BackendWebSocketClient backend;
        private DialogueController dialogue;
        private TextUtteranceInput input;
        private WorldEntity player;

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);
        private static void Save() => File.WriteAllText(Path.Combine(Root, TestContext.CurrentContext.Test.MethodName == "RemainingSessionAndNpcIsolation" ? "unity-remaining-checks.json" : "unity-checks.json"), Checks.ToString());
        private static IEnumerator Wait(Func<bool> condition, string reason, float timeout = 80)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), reason);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            if (!File.Exists(Path.Combine(Root, "unity-backend.enabled"))) Assert.Ignore("Start isolated unity_backend.py first.");
            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "Preserve unsaved scene work");
            SessionState.SetString(State + "scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            foreach (string key in new[] { "EmbodiedNPC.Settings.Language", NpcReplyLanguageSettings.PreferenceKey })
            { SessionState.SetBool(State + key + "has", PlayerPrefs.HasKey(key)); SessionState.SetInt(State + key, PlayerPrefs.GetInt(key, 0)); }
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            Set(Find<BackendWebSocketClient>(), "serverUrl", "ws://127.0.0.1:18618/ws");
            Find<LocalTestIdentityProvider>().Configure("language-production-player", "Language test player", 1, DateTimeOffset.UnixEpoch);
            Find<CafeDemoDeveloperPanel>().enabled = false;
            Find<FirstRunModelSetup>().enabled = false;
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>()) trace.enabled = false;
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            yield return new EnterPlayMode();
            Displayed.Clear(); Checks.Clear();
            backend = Find<BackendWebSocketClient>(); dialogue = Find<DialogueController>(); input = Find<TextUtteranceInput>();
            player = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "player_01");
            player.GetComponent<CafeDemoPlayerController>().enabled = false;
            var panel = Find<CafeDemoDeveloperPanel>(); panel.ApplyStartupConfig(new ModelConfigView { configured = true }); panel.enabled = true; panel.Close();
            dialogue.SetSetupReady(true);
            Find<DialogueContinuationScheduler>().SetFeatureEnabled(false);
            string overrides = Path.Combine(Root, "unity-empty-persona-overrides.json");
            foreach (var composer in Object.FindObjectsByType<CognitiveContextComposer>())
            {
                Set(composer, "characterOverrides", new CharacterOverrideStore(overrides));
                composer.GetComponent<AgentOrchestrator>().CancelCurrentRun();
                var driver = composer.GetComponent<CognitiveRunDriver>();
                Set(driver, "responseTimeoutSeconds", 80f);
                driver.UtterancePresented += value => Displayed.Add(value);
                driver.enabled = true;
            }
            // Private runtime font prevents generated glyphs from changing font asset WIP.
            var source = Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include).First(t => t.font?.sourceFontFile != null).font;
            var font = TMP_FontAsset.CreateFontAsset(source.sourceFontFile); font.isMultiAtlasTexturesEnabled = true;
            foreach (var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include)) text.font = font;
            yield return Wait(() => backend.IsConnected, "isolated formal WebSocket connection");
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (backend != null)
            {
                yield return Wait(() => backend.PendingExperienceCount == 0, "isolated experience ACKs");
                var close = backend.DisconnectAsync(); while (!close.IsCompleted) yield return null;
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
            foreach (string key in new[] { "EmbodiedNPC.Settings.Language", NpcReplyLanguageSettings.PreferenceKey })
            { if (SessionState.GetBool(State + key + "has", false)) PlayerPrefs.SetInt(key, SessionState.GetInt(State + key, 0)); else PlayerPrefs.DeleteKey(key); }
            PlayerPrefs.Save();
            string previous = SessionState.GetString(State + "scene", "");
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous) ? "Assets/Scenes/CafeDemoV2.unity" : previous);
        }

        private void Select(string id)
        {
            var npc = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == id);
            player.transform.position = npc.transform.position + Vector3.forward * 1.5f; Physics.SyncTransforms();
            Assert.IsTrue(dialogue.SelectTarget(npc.GetComponent<DialogueParticipant>()));
            Camera.main.GetComponent<CafeDemoOrbitCamera>().enabled = false;
            Vector3 head = npc.GetComponent<DialogueBubbleAnchor>().HeadPosition;
            Camera.main.transform.position = head + Vector3.forward * 3f + Vector3.up * .6f;
            Camera.main.transform.LookAt(head + Vector3.up * .4f);
        }

        private IEnumerator Say(string text, string language)
        {
            int before = Displayed.Count;
            Assert.IsTrue(input.SubmitText(text), "real input accepted");
            var composer = dialogue.SelectedTarget.GetComponent<CognitiveContextComposer>();
            var request = composer.LastRequest;
            Assert.AreEqual(language, request.reply_language);
            Assert.AreEqual(text, request.context.current_event.payload.Single(p => p.key == "text").value);
            var driver = dialogue.SelectedTarget.GetComponent<CognitiveRunDriver>();
            yield return Wait(() => Displayed.Count == before + 1 || driver.PendingRequest == null, "real request settles for " + text);
            if (Displayed.Count != before + 1)
            {
                Checks.Add(JObject.FromObject(new { text, language, request = request.request_id, npc = request.run.agent_entity_id,
                    conversation = dialogue.CurrentConversationId, language_ok = false, accepted = false, scalars = 0 })); Save();
                yield break; // Preserve failure, continue independent checks; no regeneration.
            }
            yield return Wait(() => backend.PendingExperienceCount == 0, "displayed response committed");
            var response = Displayed.Last();
            Assert.AreEqual(request.request_id, response.RequestId);
            Assert.AreEqual(request.run.agent_entity_id, response.OwnerAgentEntityId);
            Assert.AreEqual(dialogue.CurrentConversationId, response.ConversationId);
            bool english = System.Text.RegularExpressions.Regex.IsMatch(response.Utterance, "[a-zA-Z]") &&
                !System.Text.RegularExpressions.Regex.IsMatch(response.Utterance, "[\\u3400-\\u9fff]");
            bool languageOk = language == "en-US" ? english : !english;
            var bubble = Object.FindObjectsByType<DialogueBubbleView>().Where(v => v.Message.Text == response.Utterance && v.Message.SpeakerEntityId == response.OwnerAgentEntityId).OrderByDescending(v => v.Message.Sequence).First();
            var rendered = bubble.GetComponentInChildren<TMP_Text>(); rendered.ForceMeshUpdate();
            Assert.AreEqual(response.Utterance, rendered.text);
            Assert.IsFalse(rendered.isTextTruncated);
            Checks.Add(JObject.FromObject(new { text, language, request = request.request_id, npc = response.OwnerAgentEntityId,
                conversation = response.ConversationId, utterance = response.Utterance, language_ok = languageOk, scalars = ReplyUtteranceContract.ScalarCount(response.Utterance), truncated = rendered.isTextTruncated })); Save();
            if (ReplyUtteranceContract.ScalarCount(response.Utterance) > 120)
            { ScreenCapture.CaptureScreenshot(Path.Combine(Root, "unity-long-reply.png")); yield return null; yield return null; }
        }

        [UnityTest] public IEnumerator RemainingSessionAndNpcIsolation()
        {
            NpcReplyLanguageSettings.SetPreference(NpcReplyLanguagePreference.FollowInterface); SettingsUiLanguage.SetEnglish(true);
            Select("npc_cafe_regular_01"); yield return Say("接下来用中文交流", "zh-CN");
            Select("npc_cafe_companion_01"); yield return Say("Tell me your name, age, and what you do.", "en-US");
            Select("npc_cafe_regular_01"); yield return Say("good morning", "en-US");
            yield return Say("接下来用中文交流", "zh-CN");
            yield return Say("From now on, speak English.", "en-US");
            yield return Say("好的", "en-US");
            SettingsUiLanguage.SetEnglish(false); dialogue.ClearSession(); Select("npc_cafe_regular_01");
            yield return Say("你好", "zh-CN");
            SettingsUiLanguage.SetEnglish(true);
            foreach (string npc in new[] { "npc_cafe_01", "npc_cafe_quiet_01" })
            { Select(npc); yield return Say("Tell me your name, age, and what you do.", "en-US"); }
            Assert.IsTrue(Checks.All(c => (bool)c["language_ok"]), "See every accepted/error result in unity-remaining-checks.json");
        }

        [UnityTest] public IEnumerator SettingsSessionIsolationAndFullRealReply()
        {
            var panel = Find<CafeDemoDeveloperPanel>(); panel.Open(); panel.PlayerNavigation.ShowGeneral();
            yield return null;
            var dropdown = Object.FindObjectsByType<TMP_Dropdown>().Single(d => d.name == "NpcReplyLanguageDropdown");
            Assert.AreEqual(3, dropdown.options.Count);
            dropdown.value = 0; SettingsUiLanguage.SetEnglish(true);
            Assert.AreEqual("en-US", NpcReplyLanguageSettings.DefaultLanguage);
            ScreenCapture.CaptureScreenshot(Path.Combine(Root, "unity-settings-english.png")); yield return null; yield return null;
            panel.Close(); Select("npc_cafe_regular_01");
            yield return Say("goodmorning", "en-US");
            Assert.IsTrue(NpcReplyLanguageSettings.SetPreference(NpcReplyLanguagePreference.Chinese));
            yield return Say("Hello, how are you?", "zh-CN");
            SettingsUiLanguage.SetEnglish(false);
            yield return Say("good morning", "zh-CN");
            NpcReplyLanguageSettings.SetPreference(NpcReplyLanguagePreference.English);
            yield return Say("早上好", "en-US");
            NpcReplyLanguageSettings.SetPreference(NpcReplyLanguagePreference.FollowInterface); SettingsUiLanguage.SetEnglish(true);
            yield return Say("接下来用中文交流", "zh-CN");
            yield return Say("OK", "zh-CN");
            yield return Say("这句话用英文回答", "en-US");
            yield return Say("sure", "zh-CN");
            // Another NPC receives the current global default, never Shen's temporary override.
            Select("npc_cafe_companion_01"); yield return Say("What is your favorite drink, and why?", "en-US");
            Select("npc_cafe_regular_01"); yield return Say("good morning", "en-US");
            yield return Say("接下来用中文交流", "zh-CN");
            yield return Say("From now on, speak English.", "en-US");
            yield return Say("好的", "en-US");
            SettingsUiLanguage.SetEnglish(false); dialogue.ClearSession(); Select("npc_cafe_regular_01");
            yield return Say("你好", "zh-CN");
            SettingsUiLanguage.SetEnglish(true);
            foreach (string npc in new[] { "npc_cafe_01", "npc_cafe_quiet_01" })
            { Select(npc); yield return Say("Tell me your name, age, and what you do.", "en-US"); }
            Assert.IsTrue(Checks.All(c => (bool)c["language_ok"]), "Real language adherence; see unity-checks.json for every turn");
            Assert.IsTrue(Checks.Any(c => (int)c["scalars"] > 120), "At least one real long reply displayed in full");
        }
    }
}
