using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace CafeDemo.Tests
{
    public sealed class P0SettingsFeedbackTests
    {
        private GameObject root;
        private bool originalLanguage;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
        private T Add<T>(string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(root.transform);
            return go.AddComponent<T>();
        }
        [SetUp] public void Setup()
        {
            root = new GameObject("IsolatedFeedback", typeof(RectTransform)); root.SetActive(false);
            originalLanguage = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(true);
        }
        [TearDown] public void Cleanup()
        {
            UnityEngine.Object.DestroyImmediate(root);
            SettingsUiLanguage.SetEnglish(originalLanguage);
        }

        [TestCase(0)] [TestCase(404)] [TestCase(503)]
        public void HttpFailure_DoesNotInventWebSocketLoss_AndRecoveryClearsError(long httpCode)
        {
            var panel = root.AddComponent<CafeDemoDeveloperPanel>();
            var backend = Add<BackendWebSocketClient>("NoSocket");
            typeof(BackendWebSocketClient).GetProperty("ConnectionStatus").SetValue(backend, "CONNECTED");
            Set(panel, "startupBackend", backend);
            var labels = new Dictionary<string, TMP_Text>();
            foreach (string name in new[] { "backendText", "mainText", "embeddingText", "healthText", "restartText" })
            { labels[name] = Add<TextMeshProUGUI>(name); Set(panel, name, labels[name]); }
            var healthy = new RuntimeStatusView { active_provider = new ActiveProviderStatus { available = true } };
            panel.ApplyRuntimeStatus(healthy);
            panel.ApplyRuntimeFailure("BACKEND_UNAVAILABLE", httpCode);
            Assert.That(labels["backendText"].text, Does.Contain("WS: Connected"));
            Assert.AreEqual("CONNECTED", backend.ConnectionStatus);
            Assert.That(labels["mainText"].text, Does.Contain("unknown"));
            if (httpCode > 0) Assert.That(labels["backendText"].text, Does.Contain("Responded"));
            string error = labels["healthText"].text;
            panel.ApplyRuntimeFailure("BACKEND_UNAVAILABLE", httpCode);
            Assert.AreEqual(error, labels["healthText"].text);
            SettingsUiLanguage.SetEnglish(false); Call(panel, "RefreshLanguage");
            Assert.That(labels["mainText"].text, Does.Contain("未知"));
            Assert.That(labels["backendText"].text, Does.Contain("WS: 已连接"));
            panel.ApplyRuntimeStatus(healthy);
            Assert.That(labels["healthText"].text, Does.Contain("可用"));
            typeof(BackendWebSocketClient).GetProperty("ConnectionStatus").SetValue(backend, "OFFLINE");
            Call(panel, "RefreshBackendLine");
            Assert.That(labels["backendText"].text, Does.Contain("WS: 未连接"));
        }

        [UnityTest]
        public IEnumerator RuntimeHttpClient_503DoesNotChangeConnectedWebSocketProjection()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serve = ServeJson(listener, 503, "{\"error\":{\"code\":\"MODEL_CONFIG_DOCUMENT_INVALID\"}}");
            try
            {
                var backend = Add<BackendWebSocketClient>("IsolatedTransport");
                Set(backend, "serverUrl", "ws://127.0.0.1:" + port + "/ws");
                typeof(BackendWebSocketClient).GetProperty("ConnectionStatus").SetValue(backend, "CONNECTED");
                var client = Add<ModelConfigClient>("IsolatedHttp"); Set(client, "backendClient", backend);
                string error = null; long httpCode = 0;
                yield return DriveHttp(client.GetRuntimeStatus((value, code) => { Assert.IsNull(value); error = code; }, code => httpCode = code));
                Assert.AreEqual(503, httpCode, "error=" + error + "; isolated server=" + serve.Status); Assert.AreEqual("MODEL_CONFIG_DOCUMENT_INVALID", error);
                Assert.AreEqual("CONNECTED", backend.ConnectionStatus);
                while (!serve.IsCompleted) yield return null;
                Assert.IsFalse(serve.IsFaulted);
            }
            finally { listener.Stop(); }
        }
        private static IEnumerator DriveHttp(IEnumerator request)
        {
            // EditMode does not run MonoBehaviour coroutines; wait for nested HTTP operations explicitly.
            while (request.MoveNext())
            {
                if (request.Current is IEnumerator nested) yield return DriveHttp(nested);
                else if (request.Current is AsyncOperation operation)
                    while (!operation.isDone) yield return null;
                else yield return request.Current;
            }
        }
        private static async Task ServeJson(TcpListener listener, int status, string body)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = connection.GetStream();
            var buffer = new byte[8192]; await stream.ReadAsync(buffer, 0, buffer.Length);
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
            await stream.WriteAsync(response, 0, response.Length);
        }
        [UnityTest]
        public IEnumerator StartupProjection_CarriesV2ProfilesFromIsolatedHttpWithoutLegacyWrite()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serve = ServeJson(listener, 200, "{\"schema_version\":2,\"revision\":7,\"configured\":false}");
            try
            {
                var backend = Add<BackendWebSocketClient>("StartupTransport"); Set(backend, "serverUrl", "ws://127.0.0.1:" + port + "/ws");
                var client = Add<ModelConfigClient>("StartupHttp"); Set(client, "backendClient", backend);
                ModelConfigView projection = null; string error = null;
                yield return DriveHttp(client.GetStartupConfig((value, code) => { projection = value; error = code; }, 2));
                Assert.IsNull(error); Assert.IsNotNull(projection?.profiles);
                Assert.AreEqual(2, projection.profiles.schema_version); Assert.AreEqual(7, projection.revision);
                Assert.IsFalse(projection.configured);
                while (!serve.IsCompleted) yield return null;
                Assert.IsFalse(serve.IsFaulted);
            }
            finally { listener.Stop(); }
        }
        [UnityTest]
        public IEnumerator RuntimeHttpClient_MissingBackendIsUnknownAndOffersStartGuidance()
        {
            var client = Add<ModelConfigClient>("NoBackend");
            string error = null; long httpCode = -1;
            yield return client.GetRuntimeStatus((value, code) => error = code, code => httpCode = code);
            Assert.AreEqual(0, httpCode); Assert.AreEqual("BACKEND_UNAVAILABLE", error);
            Assert.That(P0FeedbackText.BackendError(error, httpCode).English, Does.Contain("Backend is running"));
        }

        [Test]
        public void ActualStartupOwner_InitializesV2InIsolatedSettingsPrefabWithoutTabClick()
        {
            GameObject prefab = PrefabUtility.LoadPrefabContents("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab");
            try
            {
                prefab.SetActive(false);
                foreach (Transform child in prefab.GetComponentsInChildren<Transform>(true))
                {
                    GameObject instance = PrefabUtility.GetOutermostPrefabInstanceRoot(child.gameObject);
                    if (instance != null) PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
                var panel = root.AddComponent<CafeDemoDeveloperPanel>();
                var form = prefab.GetComponentInChildren<FirstRunModelSetup>(true);
                Assert.IsNotNull(panel); Assert.IsNotNull(form);
                Call(form, "EnsureControls");
                Set(panel, "modelForm", form);
                Set(panel, "panelRoot", Add<TextMeshProUGUI>("InactiveModalPreview").gameObject);
                Set(panel, "overviewPage", Add<TextMeshProUGUI>("Overview").gameObject);
                Set(panel, "npcPage", Add<TextMeshProUGUI>("Persona").gameObject);
                Set(panel, "modelPage", typeof(FirstRunModelSetup).GetField("panel", Private).GetValue(form));
                Set(panel, "entryButton", Add<Button>("SettingsEntry"));
                var profiles = new ModelProfilesView { schema_version = 2,
                    main_profiles = Array.Empty<MainProfileView>(), embedding_profiles = Array.Empty<EmbeddingProfileView>(),
                    default_main_preset_id = "synthetic", default_embedding_preset_id = "synthetic",
                    main_presets = new[] { new ModelPresetView { preset_id = "synthetic", name = "Synthetic", source = "cloud", protocol = "openai_compatible", base_url = "https://fixture.invalid/v1" } },
                    embedding_presets = new[] { new ModelPresetView { preset_id = "synthetic", name = "Synthetic", source = "cloud", protocol = "openai_compatible", base_url = "https://embedding.invalid/v1" } } };
                panel.ApplyStartupConfig(new ModelConfigView { configured = false, profiles = profiles });
                Assert.AreSame(profiles, typeof(FirstRunModelSetup).GetField("profilesView", Private).GetValue(form));
                Assert.IsNotNull(typeof(FirstRunModelSetup).GetField("contractTest", Private).GetValue(form));
                Assert.IsNotNull(typeof(FirstRunModelSetup).GetField("indexAction", Private).GetValue(form));
                Assert.IsTrue((bool)Call(form, "RequireDeveloperProfiles"));
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
        }

        private DialogueController Dialogue(out TMP_Text label)
        {
            var controller = root.AddComponent<DialogueController>();
            var participant = Add<DialogueParticipant>("SyntheticNpc");
            participant.GetComponent<WorldEntity>().Configure("npc_fixture", "fixture", EntityKind.Agent, AgentRole.Npc);
            Call(participant, "Awake");
            Set(controller, "selectedTarget", participant);
            Set(controller, "activeContext", new DialogueContext { conversationId = "conversation_fixture", speakerEntityId = "player_fixture", targetAgentId = "npc_fixture" });
            label = Add<TextMeshProUGUI>("ShortFeedback"); Set(controller, "statusText", label);
            Call(controller, "TrackAcceptedFeedback", new AcceptedDialogueTurn(false, "request_fixture", "trace_fixture", "conversation_fixture", "player_fixture", "npc_fixture", "synthetic", 1, 1, 1));
            Call(controller, "RefreshRequestFeedback");
            return controller;
        }
        private static CognitiveRequestFeedback Feedback(string status, string reason, string requestId = "request_fixture", string conversation = "conversation_fixture") =>
            new(new CognitiveRequest { request_id = requestId, trace_id = "trace_fixture", run = new CognitiveRunDto { agent_entity_id = "npc_fixture" },
                context = new CognitiveContextSnapshot { current_event = new CognitiveEventDto { event_type = AgentEventTypes.PlayerUtterance,
                    payload = new[] { new CognitiveKeyValueDto { key = AgentEventPayloadKeys.ConversationId, value = conversation } } } } }, status, reason);
        private static ExperienceDraftDto Draft(string type = "dialogue_input", string source = "source_fixture") =>
            new() { request_id = "request_fixture", trace_id = "trace_fixture", observer_id = "npc_fixture", conversation_id = "conversation_fixture",
                source_type = "dialogue", event_type = type, source_event_id = source };
        private static ValidatedExperienceAppendResult Ack()
        {
            Assert.IsTrue(ExperienceAppendResultValidator.TryParse("{\"message_type\":\"experience_append_result\",\"schema_version\":\"1.0\",\"request_id\":\"request_fixture\",\"trace_id\":\"trace_fixture\",\"event_id\":\"event_fixture\",\"observer_id\":\"npc_fixture\",\"experience_seq\":1,\"status\":\"inserted\",\"reason\":\"\"}", out var value, out var reason), reason);
            return value;
        }

        [TestCase("AUTH_FAILED", "key", "密钥")]
        [TestCase("TIMEOUT", "timed out", "超时")]
        [TestCase("CLIENT_TIMEOUT", "still be processing", "仍在处理")]
        [TestCase("SEND_FAILED", "unconfirmed", "未确认")]
        [TestCase("cancelled", "cancelled", "取消")]
        public void OwnedFailure_IsVisibleBilingualAndIgnoresLateOtherRequest(string reason, string english, string chinese)
        {
            var controller = Dialogue(out var label);
            Assert.That(label.text, Does.Contain("Waiting"));
            Call(controller, "HandleRequestFeedback", Feedback("failed", reason));
            Assert.That(label.text, Does.Contain(english));
            Assert.That(label.text, Does.Contain("Storage unconfirmed"));
            string text = label.text;
            Call(controller, "HandleRequestFeedback", Feedback("failed", "AUTH_FAILED", "old_request"));
            Call(controller, "HandleRequestFeedback", Feedback("failed", "AUTH_FAILED", conversation: "old_conversation"));
            Assert.AreEqual(text, label.text);
            SettingsUiLanguage.SetEnglish(false); Call(controller, "RefreshRequestFeedback");
            Assert.That(label.text, Does.Contain(chinese));
        }

        [TestCase("queue_full")] [TestCase("retry_exhausted")]
        public void SubmittedAndCompleted_AreNotPersistence_OnlyMatchingAckClearsWarning(string settlement)
        {
            var controller = Dialogue(out var label);
            Call(controller, "HandleRequestFeedback", Feedback("response_received", null));
            Assert.That(label.text, Does.Contain("pending confirmation"));
            Call(controller, "HandleExperienceSettled", Draft(), null, settlement);
            Assert.That(label.text, Does.Contain("unconfirmed"));
            string warning = label.text;
            Call(controller, "HandleExperienceSettled", Draft(), null, settlement);
            Assert.AreEqual(warning, label.text);
            var unrelated = Draft(); unrelated.conversation_id = "another_conversation";
            Call(controller, "HandleExperienceSettled", unrelated, Ack(), "inserted");
            Assert.AreEqual(warning, label.text);
            Call(controller, "HandleExperienceSettled", Draft(), Ack(), "inserted");
            Assert.AreEqual("", label.text);
            Call(controller, "HandleFeedbackPresentation", new PresentedCognitiveUtterance("request_fixture", "trace_fixture", "run_fixture", "npc_fixture", "player_fixture", "synthetic", 1, 1, "source_response", "conversation_fixture", null));
            Assert.That(label.text, Does.Contain("pending confirmation"));
            Call(controller, "HandleExperienceSettled", Draft("dialogue_response", "source_response"), Ack(), "inserted");
            Assert.AreEqual("", label.text);
        }

        [Test]
        public void DisconnectAndRecovery_ReplaceStaleErrorWithoutChangingRequestAuthority()
        {
            var controller = Dialogue(out var label);
            Set(controller, "hasEverConnected", true);
            Call(controller, "HandleConnectionStatusChanged", "OFFLINE");
            Assert.That(label.text, Does.Contain("unconfirmed"));
            Call(controller, "HandleConnectionStatusChanged", "CONNECTED");
            Assert.That(label.text, Does.Contain("restored"));
            Assert.That(label.text, Does.Not.Contain("unconfirmed"));
            Assert.That(label.text, Does.Not.Contain("model is available"));
        }

        [TestCase(false)] [TestCase(true)]
        public void PersonaRestore_ReportsRealStoreOutcomeAndPreservesRejectedFile(bool corrupt)
        {
            string directory = Path.Combine(Path.GetTempPath(), "p0-persona-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "fixture.json");
            var definition = ScriptableObject.CreateInstance<CharacterDefinition>();
            try
            {
                Directory.CreateDirectory(directory);
                definition.Configure("fixture", "Fixture", "Role", "History", "Traits", "Style", "Limits");
                if (corrupt) { File.WriteAllText(path, "{bad fixture"); LogAssert.Expect(LogType.Warning, new Regex("NPC character overrides could not be loaded")); }
                var store = new CharacterOverrideStore(path);
                if (!corrupt) store.Save("fixture", "Override", "Role", "History", "Traits", "Style", "Limits");
                var page = root.AddComponent<CafeNpcCharacterSettings>();
                var composer = Add<CognitiveContextComposer>("SyntheticComposer"); Set(composer, "characterDefinition", definition);
                var candidates = (List<CognitiveContextComposer>)typeof(CafeNpcCharacterSettings).GetField("candidates", Private).GetValue(page);
                candidates.Add(composer); Set(page, "store", store);
                var picker = Add<TMP_Dropdown>("Picker"); picker.options.Add(new TMP_Dropdown.OptionData("Fixture")); Set(page, "npcDropdown", picker);
                var label = Add<TextMeshProUGUI>("PersonaStatus"); Set(page, "statusText", label); Set(page, "characterIdText", Add<TextMeshProUGUI>("Id"));
                foreach (string name in new[] { "displayName", "roleSummary", "backstory", "personality", "speakingStyle", "boundaries" }) Set(page, name, Add<TMP_InputField>(name));
                if (corrupt) LogAssert.Expect(LogType.Warning, "NPC character override remove failed: IOException");
                Call(page, "RestoreDefault");
                Assert.That(label.text, Does.Contain(corrupt ? "Restore failed" : "defaults restored"));
                if (corrupt) Assert.AreEqual("{bad fixture", File.ReadAllText(path));
                else Assert.IsFalse(store.TryGet("fixture", out _));
                SettingsUiLanguage.SetEnglish(false); Call(page, "RefreshLanguage");
                Assert.That(label.text, Does.Contain(corrupt ? "恢复失败" : "已恢复默认"));
            }
            finally { UnityEngine.Object.DestroyImmediate(definition); Directory.Delete(directory, true); }
        }

        [Test]
        public void PublicFeedback_UnknownCodesNeverEchoPrivateBody()
        {
            const string privateBody = "synthetic-private-upstream-body";
            var messages = new[] { P0FeedbackText.BackendError(privateBody, 503), P0FeedbackText.Dialogue(privateBody), P0FeedbackText.IndexError(privateBody) };
            foreach (var message in messages)
            {
                Assert.IsNotEmpty(message.Chinese); Assert.IsNotEmpty(message.English);
                Assert.That(message.Chinese + message.English, Does.Not.Contain(privateBody));
            }
        }

        [TestCase("queue_full")] [TestCase("retry_exhausted")]
        public void Trace_RecordsLocalAppendSettlementWithoutDialogueText(string status)
        {
            string path = Path.Combine(Path.GetTempPath(), "p0-trace-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var recorder = root.AddComponent<RuntimeTraceRecorder>();
            var backend = Add<BackendWebSocketClient>("IsolatedTraceTransport");
            try
            {
                Set(recorder, "writer", new RuntimeTraceJsonlWriter(path, "fixture", "isolated"));
                Call(recorder, "SubscribeBackend", backend);
                var settlement = (Action<ExperienceDraftDto, ValidatedExperienceAppendResult, string>)typeof(BackendWebSocketClient)
                    .GetField("ExperienceAppendSettled", Private).GetValue(backend);
                var draft = Draft(); draft.natural_language_payload = "synthetic-private-dialogue";
                settlement(draft, null, status);
                Call(recorder, "UnsubscribeAll"); Call(recorder, "StopRecording");
                string text = File.ReadAllText(path);
                Assert.That(text, Does.Contain("experience_append_settled"));
                Assert.That(text, Does.Contain(status)); Assert.That(text, Does.Contain("persisted_confirmed"));
                Assert.That(text, Does.Not.Contain(draft.natural_language_payload));
                Assert.IsNull(typeof(BackendWebSocketClient).GetField("ExperienceAppendSettled", Private).GetValue(backend));
            }
            finally { Call(recorder, "StopRecording"); if (File.Exists(path)) File.Delete(path); }
        }
    }
}
