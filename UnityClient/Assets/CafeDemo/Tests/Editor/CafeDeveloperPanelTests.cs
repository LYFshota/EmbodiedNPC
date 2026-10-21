using System.Linq;
using CafeDemo;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeDeveloperPanelTests
    {
        private const string ScenePath = "Assets/Scenes/CafeDemoV2.unity";
        private bool previousEnglish;

        [SetUp]
        public void SetUp()
        {
            previousEnglish = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(false);
        }

        [TearDown]
        public void TearDown() => SettingsUiLanguage.SetEnglish(previousEnglish);

        [Test]
        public void Scene_WiresTwoPagesAndReusesTheModelForm()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            Assert.That(panel, Is.Not.Null);
            var root = GameObject.Find("CognitiveDialogueUI").transform.Find("DeveloperPanelRoot")?.gameObject;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.transform.Find("Window/OverviewPage"), Is.Not.Null);
            Assert.That(root.transform.Find("Window/ModelPage/ModelConfigForm"), Is.Not.Null);
            var forms = Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Assert.That(forms.Length, Is.EqualTo(1), "Cafe startup has one reusable Model/API form.");
            var developerForm = forms.Single(x => new SerializedObject(x).FindProperty("developerMode").boolValue);
            var serialized = new SerializedObject(developerForm);
            foreach (string field in new[] { "client", "panel", "mainProvider", "mainBaseUrl", "mainApiKey",
                "mainModel", "mainCandidates", "temperature", "thinking", "maxOutputTokens",
                "contextLength", "mainTimeout", "embeddingEnabled", "reuseMainApi", "embeddingProvider",
                "embeddingFields", "embeddingConnectionFields", "embeddingBaseUrl", "embeddingApiKey",
                "embeddingModel", "embeddingCandidates", "embeddingTimeout", "statusText", "saveButton",
                "restartBackendButton", "recheckButton", "discoverMainButton", "testMainButton",
                "discoverEmbeddingButton", "testEmbeddingButton" })
                Assert.That(serialized.FindProperty(field)?.objectReferenceValue, Is.Not.Null, field);
        }

        [Test]
        public void Overview_MapsExistingRuntimeStatus()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            panel.ApplyRuntimeStatus(new RuntimeStatusView
            {
                active_main_model = new ActiveMainModelStatus { provider = "ollama", model = "qwen3.5:4b" },
                active_embedding = new ActiveEmbeddingStatus
                    { enabled = true, provider = "openai_compatible", model = "embed-small" },
                active_provider = new ActiveProviderStatus { available = false },
                restart_required = true,
            });
            var labels = GameObject.Find("CognitiveDialogueUI").transform.Find("DeveloperPanelRoot")
                .GetComponentsInChildren<TMP_Text>(true).Select(x => x.text).ToArray();
            Assert.That(labels, Does.Contain("后端：已连接"));
            Assert.That(labels, Does.Contain("主模型：ollama / qwen3.5:4b"));
            Assert.That(labels, Does.Contain("Embedding：已启用 · openai_compatible / embed-small"));
            Assert.That(labels, Does.Contain("服务状态：不可用"));
            Assert.That(labels, Does.Contain("需要重启：是"));
        }

        [Test]
        public void ModelForm_LoadsExistingConfig_AndDisplaysRestartRequirement()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var form = Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include,
                FindObjectsSortMode.None).Single(x => new SerializedObject(x).FindProperty("developerMode").boolValue);
            form.ApplyInitialConfig(new ModelConfigView
            {
                configured = true, revision = 3,
                main_model = new MainModelConfig { provider_type = "ollama", base_url = "http://localhost:11434",
                    api_key = "local-test-only", model = "qwen3.5:4b", temperature = .4f,
                    max_output_tokens = 600, context_length = 8192, timeout_seconds = 45 },
                embedding = new EmbeddingConfig { enabled = true, reuse_main_api = true,
                    provider_type = "ollama", model = "nomic-embed-text", timeout_seconds = 20 },
            });
            var serialized = new SerializedObject(form);
            var model = (TMP_InputField)serialized.FindProperty("mainModel").objectReferenceValue;
            var key = (TMP_InputField)serialized.FindProperty("mainApiKey").objectReferenceValue;
            Assert.That(model.text, Is.EqualTo("qwen3.5:4b"));
            Assert.That(key.text, Is.EqualTo("local-test-only"));
            Assert.That(key.contentType, Is.EqualTo(TMP_InputField.ContentType.Password));
            form.ApplyRuntimeStatus(new RuntimeStatusView { restart_required = true }, false);
            var status = (TMP_Text)serialized.FindProperty("statusText").objectReferenceValue;
            Assert.That(status.text, Does.Contain("需要重启 AI Backend"));
        }
    }

}
