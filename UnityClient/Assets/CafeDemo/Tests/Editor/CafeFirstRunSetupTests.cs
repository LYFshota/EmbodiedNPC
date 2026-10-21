using System.Collections;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeFirstRunSetupTests
    {
        [UnityTest]
        public IEnumerator CafeScene_GatesDialogueUntilConfigured()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            string endpoint = System.Environment.GetEnvironmentVariable("EMBODIEDNPC_TEST_BACKEND_URL");
            if (!string.IsNullOrWhiteSpace(endpoint))
                typeof(EmbodiedNPC.Communication.BackendWebSocketClient).GetField("serverUrl",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(
                    Object.FindAnyObjectByType<EmbodiedNPC.Communication.BackendWebSocketClient>(), endpoint);
            var sceneSetup = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            var scenePanel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            Assert.That(sceneSetup, Is.Not.Null);
            Assert.That(scenePanel, Is.Not.Null);
            Assert.That(GameObject.Find("FirstRunSetupRoot"), Is.Null);
            Assert.That(GameObject.Find("ModelConfigService")?.GetComponent<ModelConfigClient>(), Is.Not.Null);
            var serialized = new SerializedObject(sceneSetup);
            foreach (string field in new[] { "client", "panel", "mainProvider",
                "mainBaseUrl", "mainApiKey", "mainModel", "mainCandidates", "temperature", "thinking",
                "maxOutputTokens", "contextLength", "mainTimeout", "embeddingEnabled", "reuseMainApi",
                "embeddingProvider", "embeddingFields", "embeddingConnectionFields", "embeddingBaseUrl",
                "embeddingApiKey", "embeddingModel", "embeddingCandidates", "embeddingTimeout", "statusText",
                "saveButton", "recheckButton", "restartBackendButton", "discoverMainButton",
                "testMainButton", "discoverEmbeddingButton", "testEmbeddingButton" })
                Assert.That(serialized.FindProperty(field)?.objectReferenceValue, Is.Not.Null, field);
            yield return new EnterPlayMode();
            var setup = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            var settings = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            Assert.That(setup, Is.Not.Null);
            Assert.That(dialogue, Is.Not.Null);
            Assert.That(settings.IsOpen, Is.True, "The existing model settings page opens on Play startup.");
            settings.ApplyStartupConfig(new ModelConfigView { configured = false });
            Assert.That(settings.IsOpen, Is.True);
            Assert.That(settings.transform.parent.Find("DeveloperPanelRoot/Window/ModelPage").gameObject.activeSelf, Is.True);
            var context = settings.transform.parent.Find(
                "DeveloperPanelRoot/Window/ModelPage/ModelConfigForm/ScrollView/Viewport/Content/Context LengthRow")
                ?.GetComponentInChildren<TMPro.TMP_InputField>();
            Assert.That(context?.text, Is.EqualTo("16384"));
            Assert.That(dialogue.IsSetupReady, Is.False);

            settings.ApplyStartupConfig(new ModelConfigView { configured = true, revision = 1 });
            Assert.That(settings.IsOpen, Is.True);
            Assert.That(dialogue.IsSetupReady, Is.True);
            settings.Close();
            var player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            player.Controller.enabled = false;
            player.transform.position = new Vector3(-.8f, .02f, 1.25f);
            player.Controller.enabled = true;
            Physics.SyncTransforms();
            yield return null;
            Assert.That(player.GetComponent<PlayerDialogueInteractor>().TryBeginDialogue(), Is.True);
            yield return new ExitPlayMode();
        }
    }
}
