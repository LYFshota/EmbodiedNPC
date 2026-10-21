using System;
using System.Reflection;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Tests
{
    public sealed class FirstRunModelSetupTests
    {
        private GameObject root;
        private GameObject panel;
        private FirstRunModelSetup setup;
        private DialogueController dialogue;
        private TMP_Dropdown mainCandidates;
        private TMP_InputField mainModel;
        private TMP_InputField embeddingModel;
        private TMP_InputField embeddingBaseUrl;
        private TMP_InputField embeddingApiKey;
        private TMP_InputField embeddingTimeout;
        private TMP_Text statusText;
        private Toggle embeddingEnabled;
        private Toggle reuseMainApi;
        private GameObject embeddingFields;
        private GameObject embeddingConnectionFields;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("setup-test");
            root.SetActive(false);
            setup = root.AddComponent<FirstRunModelSetup>();
            dialogue = root.AddComponent<DialogueController>();
            panel = new GameObject("panel");
            mainCandidates = New<TMP_Dropdown>("candidates");
            var candidateRow = new GameObject("candidate-row", typeof(RectTransform));
            candidateRow.transform.SetParent(root.transform);
            mainCandidates.transform.SetParent(candidateRow.transform);
            mainModel = New<TMP_InputField>("model");
            embeddingEnabled = New<Toggle>("embedding-enabled");
            reuseMainApi = New<Toggle>("reuse-main");
            embeddingFields = new GameObject("embedding-fields");
            embeddingFields.transform.SetParent(root.transform);
            embeddingConnectionFields = new GameObject("connection-fields");
            embeddingConnectionFields.transform.SetParent(root.transform);
            embeddingModel = New<TMP_InputField>("embedding-model");
            embeddingBaseUrl = New<TMP_InputField>("embedding-url");
            embeddingApiKey = New<TMP_InputField>("embedding-key");
            embeddingTimeout = New<TMP_InputField>("embedding-timeout");
            statusText = New<TextMeshProUGUI>("status");
            Field("dialogueController", dialogue);
            Field("panel", panel);
            Field("mainProvider", New<TMP_Dropdown>("provider"));
            Field("mainCandidates", mainCandidates);
            Field("mainModel", mainModel);
            Field("embeddingEnabled", embeddingEnabled);
            Field("reuseMainApi", reuseMainApi);
            Field("embeddingProvider", New<TMP_Dropdown>("embedding-provider"));
            Field("embeddingFields", embeddingFields);
            Field("embeddingConnectionFields", embeddingConnectionFields);
            Field("embeddingModel", embeddingModel);
            Field("embeddingBaseUrl", embeddingBaseUrl);
            Field("embeddingApiKey", embeddingApiKey);
            Field("embeddingTimeout", embeddingTimeout);
            Field("statusText", statusText);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(panel);
        }

        [Test]
        public void CloudProtocol_UsesOfficialDeepSeekOnlyForItsHost()
        {
            Assert.AreEqual("deepseek_official_responses",
                FirstRunModelSetup.MainProviderType(0, "https://api.deepseek.com"));
            Assert.AreEqual("openai_compatible",
                FirstRunModelSetup.MainProviderType(0, "https://other.example.com/v1"));
            Assert.AreEqual("openai_compatible",
                FirstRunModelSetup.MainProviderType(0, "https://api.deepseek.com/v1"));
            Assert.AreEqual("ollama",
                FirstRunModelSetup.MainProviderType(1, "https://api.deepseek.com"));
        }

        [Test]
        public void JsonContracts_IncludeRevisionAndUnsavedConnectionData()
        {
            var request = new ModelConfigSaveRequest
            {
                expected_revision = 7,
                config = new ModelConfigDocument
                {
                    main_model = new MainModelConfig { base_url = "https://example.com/v1", api_key = "test-key", model = "chat" },
                    embedding = new EmbeddingConfig { model = "embed" },
                },
            };
            string json = JsonUtility.ToJson(request);
            StringAssert.Contains("\"expected_revision\":7", json);
            StringAssert.Contains("\"api_key\":\"test-key\"", json);
            Assert.AreEqual("chat", JsonUtility.FromJson<ModelConfigSaveRequest>(json).config.main_model.model);
            StringAssert.Contains("\"provider_type\":\"ollama\"",
                JsonUtility.ToJson(new DiscoverModelsRequest { provider_type = "ollama", base_url = "http://localhost:11434" }));
            StringAssert.Contains("\"kind\":\"embedding\"",
                JsonUtility.ToJson(new TestConnectionRequest { kind = "embedding", main_model = request.config.main_model,
                    embedding = request.config.embedding }));
            string mainTest = JsonUtility.ToJson(new MainTestConnectionRequest { main_model = request.config.main_model });
            StringAssert.Contains("\"kind\":\"main\"", mainTest);
            Assert.IsFalse(mainTest.Contains("\"embedding\""));
        }

        [Test]
        public void SaveJson_OmitsOptionalEmbeddingFieldsWhenDisabledOrReused()
        {
            var request = new ModelConfigSaveRequest
            {
                expected_revision = 3,
                config = new ModelConfigDocument
                {
                    main_model = new MainModelConfig { provider_type = "openai_compatible",
                        base_url = "https://example.com/v1", model = "chat" },
                    embedding = new EmbeddingConfig { enabled = false },
                },
            };
            string disabled = ModelConfigClient.SerializeSaveRequest(request);
            StringAssert.Contains("\"embedding\":{\"enabled\":false}", disabled);
            Assert.IsFalse(disabled.Contains("\"embedding\":{\"enabled\":false,\"model\""));

            request.config.embedding = new EmbeddingConfig { enabled = true, reuse_main_api = true,
                provider_type = "openai_compatible", model = "embed", timeout_seconds = 30 };
            string reused = ModelConfigClient.SerializeSaveRequest(request);
            StringAssert.Contains("\"reuse_main_api\":true", reused);
            StringAssert.Contains("\"model\":\"embed\"", reused);
            Assert.IsFalse(reused.Contains("\"base_url\":\"\""));
        }

        [Test]
        public void HttpEndpoints_FollowTheWebSocketBackendAddress()
        {
            var backend = root.AddComponent<BackendWebSocketClient>();
            typeof(BackendWebSocketClient).GetField("serverUrl", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(backend, "wss://demo.example:8443/ws");
            Assert.AreEqual("https://demo.example:8443/v1/model-config", backend.HttpUrl("v1/model-config"));
            Assert.AreEqual("https://demo.example:8443/v1/dialogue-history", backend.DialogueHistoryUrl);
            typeof(BackendWebSocketClient).GetField("serverUrl", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(backend, "ws://127.0.0.1:8000/ws");
            Assert.AreEqual("http://127.0.0.1:8000/v1/model-config", backend.HttpUrl("v1/model-config"));
            Assert.AreEqual("http://127.0.0.1:8000/v1/dialogue-history", backend.DialogueHistoryUrl);
        }

        [Test]
        public void Unconfigured_ShowsSetupAndBlocksDialogue()
        {
            setup.ApplyInitialConfig(new ModelConfigView { configured = false, revision = 0 });
            Assert.IsTrue(panel.activeSelf);
            Assert.IsFalse(dialogue.IsSetupReady);
            Assert.IsFalse(dialogue.SelectTarget(null));
        }

        [Test]
        public void Configured_HidesSetupAndReleasesDialogueGate()
        {
            dialogue.SetSetupReady(false);
            setup.ApplyInitialConfig(new ModelConfigView { configured = true, revision = 4 });
            Assert.IsFalse(panel.activeSelf);
            Assert.IsTrue(dialogue.IsSetupReady);
        }

        [Test]
        public void UnconfiguredRuntimeStatus_DoesNotActivateDialogueFromMockProvider()
        {
            Field("developerMode", true);
            int activations = 0;
            setup.ConfigurationActivated += () => activations++;
            setup.ApplyRuntimeStatus(new RuntimeStatusView
            {
                model_config = new ModelConfigStatus { configured = false },
                active_provider = new ActiveProviderStatus { available = true },
            }, false);
            Assert.AreEqual(0, activations);

            setup.ApplyRuntimeStatus(new RuntimeStatusView
            {
                model_config = new ModelConfigStatus { configured = true, revision = 1 },
                active_provider = new ActiveProviderStatus { available = true },
            }, false);
            Assert.AreEqual(1, activations);
        }

        [Test]
        public void DiscoveredModels_KeepManualModelAndPopulateCandidates()
        {
            mainModel.text = "hand-entered";
            FirstRunModelSetup.ApplyDiscoveredModels(mainCandidates, new[] { "model-a", "model-b" });
            Assert.AreEqual(2, mainCandidates.options.Count);
            Assert.AreEqual("model-a", mainCandidates.options[0].text);
            Assert.AreEqual("hand-entered", mainModel.text);
            Invoke("OnMainCandidateChosen", 1);
            Assert.AreEqual("model-b", mainModel.text);
        }

        [Test]
        public void SelectedCandidate_IsUsedForMainTestEvenIfInputWasNotUpdated()
        {
            FirstRunModelSetup.ApplyDiscoveredModels(mainCandidates, new[] { "deepseek-chat" });
            mainCandidates.SetValueWithoutNotify(0);
            mainModel.text = string.Empty;
            var provider = (TMP_Dropdown)typeof(FirstRunModelSetup)
                .GetField("mainProvider", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(setup);
            provider.AddOptions(new System.Collections.Generic.List<string> { "Cloud", "Local" });
            Field("mainBaseUrl", New<TMP_InputField>("main-url"));
            Field("mainApiKey", New<TMP_InputField>("main-key"));
            Field("temperature", New<TMP_InputField>("temperature"));
            Field("thinking", New<Toggle>("thinking"));
            Field("maxOutputTokens", New<TMP_InputField>("max-tokens"));
            Field("contextLength", New<TMP_InputField>("context"));
            Field("mainTimeout", New<TMP_InputField>("main-timeout"));
            GetInput("mainBaseUrl").text = "https://api.example.com/v1";
            GetInput("temperature").text = "0";
            GetInput("maxOutputTokens").text = "512";
            GetInput("contextLength").text = "16384";
            GetInput("mainTimeout").text = "60";
            var arguments = new object[] { null };
            bool valid = (bool)typeof(FirstRunModelSetup)
                .GetMethod("TryBuildMain", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(setup, arguments);
            Assert.IsTrue(valid, statusText.text);
            Assert.AreEqual("deepseek-chat", ((MainModelConfig)arguments[0]).model);
            GetInput("contextLength").text = string.Empty;
            valid = (bool)typeof(FirstRunModelSetup)
                .GetMethod("TryBuildMain", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(setup, arguments);
            Assert.IsFalse(valid);
            StringAssert.Contains("16K", statusText.text);
        }

        [Test]
        public void DiscoveryFailure_PreservesManualModelAndShowsFallback()
        {
            mainModel.text = "manual-model";
            setup.ApplyDiscoveryResult(false, new DiscoverModelsResponse { success = false,
                error_code = "MODEL_DISCOVERY_HTTP_FAILED" }, null);
            Assert.AreEqual("manual-model", mainModel.text);
            StringAssert.Contains("手动输入", statusText.text);
            mainModel.text = "another-manual-model";
            Assert.AreEqual("another-manual-model", mainModel.text);
        }

        [Test]
        public void EmbeddingReuse_HidesIndependentConnectionAndCanRestoreIt()
        {
            embeddingEnabled.isOn = true;
            reuseMainApi.isOn = true;
            Invoke("RefreshEmbeddingFields");
            Assert.IsTrue(embeddingFields.activeSelf);
            Assert.IsFalse(embeddingConnectionFields.activeSelf);
            reuseMainApi.isOn = false;
            Invoke("RefreshEmbeddingFields");
            Assert.IsTrue(embeddingConnectionFields.activeSelf);
        }

        [Test]
        public void EmbeddingReuse_SubmitsCompatibleProviderAndIndependentModel()
        {
            embeddingEnabled.isOn = true;
            reuseMainApi.isOn = true;
            embeddingModel.text = "embed-model";
            embeddingTimeout.text = "30";
            var main = new MainModelConfig { provider_type = "ollama", base_url = "http://localhost:11434", model = "chat" };
            var provider = (TMP_Dropdown)typeof(FirstRunModelSetup)
                .GetField("mainProvider", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(setup);
            provider.AddOptions(new System.Collections.Generic.List<string> { "OpenAI", "Ollama", "DeepSeek" });
            provider.value = 1;
            var embeddingSource = (TMP_Dropdown)typeof(FirstRunModelSetup)
                .GetField("embeddingProvider", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(setup);
            embeddingSource.AddOptions(new System.Collections.Generic.List<string> { "Cloud", "Local" });
            embeddingSource.value = 1;
            Invoke("RefreshEmbeddingFields");
            var arguments = new object[] { main, null };
            bool valid = (bool)typeof(FirstRunModelSetup)
                .GetMethod("TryBuildEmbedding", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(setup, arguments);
            Assert.IsTrue(valid);
            var embed = (EmbeddingConfig)arguments[1];
            Assert.AreEqual("ollama", embed.provider_type);
            Assert.IsTrue(embed.reuse_main_api);
            Assert.IsNull(embed.base_url);
            Assert.AreEqual("embed-model", embed.model);

            reuseMainApi.isOn = false;
            embeddingBaseUrl.text = "http://localhost:11434";
            embeddingApiKey.text = "separate-key";
            arguments = new object[] { main, null };
            valid = (bool)typeof(FirstRunModelSetup)
                .GetMethod("TryBuildEmbedding", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(setup, arguments);
            Assert.IsTrue(valid);
            embed = (EmbeddingConfig)arguments[1];
            Assert.IsFalse(embed.reuse_main_api);
            Assert.AreEqual("http://localhost:11434", embed.base_url);
            Assert.IsNull(embed.api_key); // Local Ollama does not submit an API key.
        }

        [Test]
        public void SavedRuntimeStatus_RequiresRecheckAfterRestart()
        {
            setup.ApplyRuntimeStatus(new RuntimeStatusView { restart_required = true,
                active_provider = new ActiveProviderStatus { available = true } }, true);
            Assert.IsTrue(panel.activeSelf);
            setup.ApplyRuntimeStatus(new RuntimeStatusView { restart_required = false,
                active_provider = new ActiveProviderStatus { available = true } }, true);
            Assert.IsTrue(panel.activeSelf); // A different client's active config must not close this form.
            Field("savedThisSession", true);
            setup.ApplyRuntimeStatus(new RuntimeStatusView { restart_required = false,
                active_provider = new ActiveProviderStatus { available = true } }, true);
            Assert.IsFalse(panel.activeSelf);
        }

        private T New<T>(string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root.transform);
            return go.AddComponent<T>();
        }

        private void Field(string name, object value) =>
            typeof(FirstRunModelSetup).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(setup, value);

        private TMP_InputField GetInput(string name) => (TMP_InputField)typeof(FirstRunModelSetup)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(setup);

        private void Invoke(string name, params object[] arguments) =>
            typeof(FirstRunModelSetup).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(setup, arguments);
    }
}
