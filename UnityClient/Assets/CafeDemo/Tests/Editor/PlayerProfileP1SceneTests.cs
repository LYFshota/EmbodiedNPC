using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerProfileP1SceneTests
    {
        const string Session = "PlayerProfileP1.Scene.";
        const string EnvKey = "EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY";
        [UnitySetUp] public IEnumerator Load()
        {
            SessionState.SetString(Session + "key", "PlayerProfileP1.Scene." + Guid.NewGuid().ToString("N"));
            SessionState.SetString(Session + "previous", Environment.GetEnvironmentVariable(EnvKey) ?? "");
            Environment.SetEnvironmentVariable(EnvKey, SessionState.GetString(Session + "key", ""));
            // A fresh checkout can import VRMs before the package's scripted importer has compiled.
            foreach (string model in new[] { "Assets/VRMCompatibilityTest/Xingmang.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/CafeDemo/V2/Characters/Ao.vrm", "Assets/CafeDemo/V2/Characters/薇薇.vrm", "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm" })
                if (AssetDatabase.LoadAssetAtPath<GameObject>(model) == null)
                    AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity"); AnimationSceneTestIsolation.Prepare();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None)) driver.enabled = false;
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None)) trace.enabled = false;
            Object.FindAnyObjectByType<LocalTestIdentityProvider>().Configure("player-profile-isolated", "Player", 1, DateTimeOffset.UnixEpoch);
            yield return new EnterPlayMode(); AnimationSceneTestIsolation.Ready(); yield return null;
            var host = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            host.GetType().GetField("startupTransportReady", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host, (Func<bool>)(() => true));
            host.GetType().GetField("startupGetConfig", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(host,
                (Func<Action<ModelConfigView, string>, int, IEnumerator>)((callback, generation) => Configured(callback)));
            host.enabled = true; yield return null;
            host.ApplyStartupConfig(new ModelConfigView { configured = true }); host.Close();
        }
        [UnityTearDown] public IEnumerator Close()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            PlayerPrefs.DeleteKey(SessionState.GetString(Session + "key", "")); PlayerPrefs.Save();
            string previous = SessionState.GetString(Session + "previous", ""); Environment.SetEnvironmentVariable(EnvKey, previous.Length == 0 ? null : previous);
        }
        private static IEnumerator Configured(Action<ModelConfigView, string> callback)
        { callback(new ModelConfigView { configured = true }, null); yield break; }
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        private static CognitiveRequest Capture(string eventId)
        {
            var composer = Object.FindObjectsByType<CognitiveContextComposer>(FindObjectsSortMode.None).First();
            string npcId = composer.GetComponent<WorldEntity>().EntityId;
            var orchestrator = composer.GetComponent<AgentOrchestrator>(); orchestrator.CancelCurrentRun();
            var receiver = composer.GetComponent<AgentEventReceiver>();
            Assert.IsTrue(receiver.Receive(new AgentEvent(eventId, AgentEventTypes.PlayerUtterance, "player_01", npcId, "",
                new Dictionary<string,string> { ["text"] = "你好。", ["conversation_id"] = "profile-test-conversation" }, Time.unscaledTimeAsDouble)));
            Assert.IsNotNull(composer.LastRequest); Assert.AreEqual(eventId, composer.LastRequest.context.current_event.event_id);
            return composer.LastRequest;
        }
        [UnityTest] public IEnumerator SavedNameRestoresAfterPlayModeRestart()
        {
            Assert.IsTrue(PlayerNameSettings.TrySave("小羽", out _));
            SessionState.SetString(Session + "revision", PlayerNameSettings.Current.Revision.ToString());
            yield return new ExitPlayMode();
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity"); AnimationSceneTestIsolation.Prepare();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None)) driver.enabled = false;
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None)) trace.enabled = false;
            Object.FindAnyObjectByType<LocalTestIdentityProvider>().Configure("player-profile-isolated", "Player", 1, DateTimeOffset.UnixEpoch);
            yield return new EnterPlayMode(); AnimationSceneTestIsolation.Ready(); yield return null;
            var binding = Find<LocalTestIdentityProvider>().GetComponent<PlayerIdentityBinding>();
            Assert.AreEqual("小羽", binding.CurrentIdentity.DisplayName);
            Assert.AreEqual("player-profile-isolated", binding.CurrentIdentity.PlayerUid);
            Assert.AreEqual("player_01", binding.GetComponent<WorldEntity>().EntityId);
            Assert.AreEqual(long.Parse(SessionState.GetString(Session + "revision", "0")), binding.CurrentIdentity.ProfileRevision);
        }
        [UnityTest] public IEnumerator SettingsButtonsRefreshIdentityAndFutureRunOnly()
        {
            var provider = Find<LocalTestIdentityProvider>(); var binding = provider.GetComponent<PlayerIdentityBinding>();
            var entity = provider.GetComponent<WorldEntity>(); var appearance = provider.GetComponent<PlayerVrmAppearance>();
            string entityId = entity.EntityId; var oldIdentity = binding.CurrentIdentity;
            Assert.IsTrue(entity.TryGetPersistentIdentityKey(out var oldKey));
            Assert.IsNotNull(appearance); Assert.IsNotNull(appearance.CurrentAnimator);
            var animator = appearance.CurrentAnimator;
            var first = Capture("profile_before"); string frozen = JsonUtility.ToJson(first);
            var host = Find<CafeDemoDeveloperPanel>(); host.Open(); Assert.IsNotNull(host.PlayerNavigation);
            host.PlayerNavigation.ShowGeneral(); yield return null;
            var input = host.PlayerNavigation.GeneralPage.GetComponentsInChildren<TMP_InputField>(true).Single(x => x.name == "PlayerNameInput");
            Button Button(string name) => host.PlayerNavigation.GeneralPage.GetComponentsInChildren<Button>(true).Single(x => x.name == name);
            Assert.AreEqual("Player", input.text);
            Assert.IsTrue(input.isActiveAndEnabled); Assert.IsTrue(input.interactable);
            Canvas.ForceUpdateCanvases(); Assert.Greater(((RectTransform)input.transform).rect.height, 40);
            Assert.Greater(((RectTransform)input.transform).rect.width, 100);
            foreach (var name in new[] { "小羽", "91651", "老婆" })
            {
                input.text = name; Button("SavePlayerName").onClick.Invoke(); yield return null;
                Assert.AreEqual(name, PlayerNameSettings.DisplayName); Assert.AreEqual(name, binding.CurrentIdentity.DisplayName);
                Assert.AreEqual(name, entity.GetComponent<PerceivableEntity>().DisplayName);
                Assert.AreEqual(oldIdentity.PlayerUid, binding.CurrentIdentity.PlayerUid); Assert.AreEqual(entityId, entity.EntityId);
                Assert.IsTrue(entity.TryGetPersistentIdentityKey(out var key)); Assert.AreEqual(oldKey, key);
                Assert.AreSame(animator, appearance.CurrentAnimator); Assert.AreEqual(frozen, JsonUtility.ToJson(first));
                var next = Capture("profile_" + name); Assert.AreEqual(name, next.context.relevant_player.display_name);
                foreach (var perceived in next.context.perception.entities.Where(x => x.entity_id == entityId)) Assert.AreEqual(name, perceived.display_name);
            }
            Assert.Greater(binding.CurrentIdentity.ProfileRevision, oldIdentity.ProfileRevision);
            provider.enabled = false; Assert.IsTrue(PlayerNameSettings.TrySave("小羽", out _));
            provider.enabled = true; Assert.AreEqual("小羽", binding.CurrentIdentity.DisplayName);
            Assert.IsTrue(PlayerNameSettings.TrySave("老婆", out _));
            input.text = new string('x', 25); Button("SavePlayerName").onClick.Invoke();
            Assert.AreEqual("老婆", binding.CurrentIdentity.DisplayName);
            Button("ResetPlayerName").onClick.Invoke(); Assert.AreEqual("Player", binding.CurrentIdentity.DisplayName);
            Assert.AreEqual(frozen, JsonUtility.ToJson(first));
            // Recreating the existing provider/binding simulates the profile part of game startup.
            var fresh = new GameObject("ReloadedProfile"); fresh.SetActive(false);
            try
            {
                var source = fresh.AddComponent<LocalTestIdentityProvider>();
                Assert.IsTrue(source.TryGetCurrentIdentity(out var restored)); Assert.AreEqual("Player", restored.DisplayName);
                Assert.AreEqual(PlayerNameSettings.Current.Revision, restored.ProfileRevision);
            }
            finally { Object.Destroy(fresh); }
            host.Close(); yield return null;
        }
    }
}
