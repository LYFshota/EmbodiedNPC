using System;
using System.Collections;
using System.Globalization;
using System.Text;
using EmbodiedNPC.Communication;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Settings
{
    public enum StartupHttpStage { ReadingProfiles, ProfilesReceived, CheckingRuntime, RuntimeReceived }
    /// <summary>Small HTTP client for the local model setup API. Never logs request bodies or keys.</summary>
    public sealed class ModelConfigClient : MonoBehaviour
    {
        [SerializeField] private BackendWebSocketClient backendClient;

        public BackendWebSocketClient BackendClient => backendClient;
        public event Action<StartupHttpStage> StartupCheckProgress;

        public IEnumerator GetConfig(Action<ModelConfigView, string> done) => GetConfigProjection(done, 20);

        // Only the Cafe startup owner supplies a shorter timeout; manual consumers keep 20s.
        public IEnumerator GetStartupConfig(Action<ModelConfigView, string> done, int timeoutSeconds) =>
            GetStartupProjection(done, timeoutSeconds);

        // Saved/activated configuration and the running Provider must agree.
        // A live process, selected draft or successful save alone is not AI Ready.
        public static bool StartupReady(ModelProfilesView profiles, RuntimeStatusView status) =>
            profiles != null && profiles.configured && profiles.active_runtime != null && !profiles.restart_required &&
            status?.model_config != null && status.model_config.configured && !status.restart_required &&
            status.active_provider != null && status.active_provider.available;

        private IEnumerator GetStartupProjection(Action<ModelConfigView, string> done, int timeoutSeconds)
        {
            StartupCheckProgress?.Invoke(StartupHttpStage.ReadingProfiles);
            ModelProfilesView profiles = null; string error = null;
            yield return GetProfiles((value, code) => { profiles = value; error = code; }, timeoutSeconds);
            if (error != null || profiles == null || profiles.schema_version != 2)
            { done?.Invoke(null, error ?? "UNSUPPORTED_MODEL_CONFIG"); yield break; }
            StartupCheckProgress?.Invoke(StartupHttpStage.ProfilesReceived);
            RuntimeStatusView status = null;
            if (profiles.configured && profiles.active_runtime != null && !profiles.restart_required)
            {
                StartupCheckProgress?.Invoke(StartupHttpStage.CheckingRuntime);
                yield return Send<RuntimeStatusView>("GET", "v1/runtime-status", null, (value, code) => { status = value; error = code; }, timeoutSeconds);
                if(error==null&&status!=null)StartupCheckProgress?.Invoke(StartupHttpStage.RuntimeReceived);
            }
            var view = new ModelConfigView { configured = StartupReady(profiles, status), saved_configured = profiles.configured,
                revision = profiles.revision, main_model = profiles.selected_to_activate?.main_model,
                embedding = profiles.selected_to_activate?.embedding, profiles = profiles };
            DiagnosticsExport.ObserveModelStatus(view);
            done?.Invoke(view, error);
        }

        private IEnumerator GetConfigProjection(Action<ModelConfigView, string> done, int timeoutSeconds)
        {
            yield return GetProfiles((value, error) => done?.Invoke(value == null ? null : new ModelConfigView
            {
                configured = value.configured, saved_configured = value.configured, revision = value.revision,
                main_model = value.selected_to_activate?.main_model, embedding = value.selected_to_activate?.embedding
            }, error), timeoutSeconds);
        }

        public IEnumerator GetProfiles(Action<ModelProfilesView, string> done, int timeoutSeconds = 20) =>
            Send("GET", "v2/model-config", null, done, timeoutSeconds);
        public IEnumerator RecheckContextCapacity(EmbodiedNPC.Cognition.CapacityCheckDto[] checks,
            Action<EmbodiedNPC.Cognition.CapacityRecheckResult, string> done) =>
            Send("POST", "v1/context-budget/recheck", new JObject { ["checks"] = JArray.FromObject(checks) }.ToString(Formatting.None), done);
        public IEnumerator GetContextPolicy(Action<ContextPolicyView, string> done) =>
            Send("GET", "v1/context-policy", null, done);
        public IEnumerator SaveContextPolicy(ContextPolicySaveRequest value, Action<ContextPolicyView, string> done) =>
            Send("PUT", "v1/context-policy", SerializeContextPolicyRequest(value), done);
        public static string SerializeContextPolicyRequest(ContextPolicySaveRequest value) => new JObject
        {
            ["expected_revision"] = value.expected_revision,
            ["config"] = new JObject
            {
                ["mode"] = value.config.mode,
                ["target_messages"] = value.config.target_messages.HasValue ?
                    JToken.FromObject(value.config.target_messages.Value) : JValue.CreateNull()
            }
        }.ToString(Formatting.None);
        public IEnumerator SaveProfile(string json, Action<ModelProfilesView, string> done) =>
            Send("POST", "v2/model-config/profile", json, done);
        public IEnumerator ActivateProfile(string json, Action<ModelProfilesView, string> done) =>
            Send("POST", "v2/model-config/activate", json, done);
        public IEnumerator SaveAndActivateProfiles(string json, Action<ModelProfilesView, string> done) =>
            Send("POST", "v2/model-config/save-and-activate", json, done);
        public IEnumerator ResetMainProfile(string json, Action<ModelProfilesView, string> done) =>
            Send("POST", "v2/model-config/reset-main", json, done);
        public IEnumerator ProbeProfile(string json, Action<ModelProbeView, string> done) =>
            Send("POST", "v2/model-config/probe", json, done, 90);
        public IEnumerator GetEmbeddingIndex(Action<EmbeddingIndexView, string> done) =>
            Send("GET", "v1/embedding-index/status", null, done);
        public IEnumerator ControlEmbeddingIndex(string json, Action<EmbeddingIndexView, string> done) =>
            Send("POST", "v1/embedding-index/control", json, done);

        public IEnumerator SaveConfig(ModelConfigSaveRequest value, Action<ModelConfigView, string> done) =>
            Send("PUT", "v1/model-config", SerializeSaveRequest(value), done);

        public static string SerializeSaveRequest(ModelConfigSaveRequest value)
        {
            // JsonUtility turns null optional fields into empty strings. The Backend rejects
            // those as configured URLs/models, so omit them for disabled and reused slots.
            EmbeddingConfig embedding = value.config.embedding;
            string embeddingJson = !embedding.enabled ? "{\"enabled\":false}" :
                embedding.reuse_main_api ? JsonUtility.ToJson(new ReusedEmbeddingSavePayload
                {
                    provider_type = embedding.provider_type,
                    model = embedding.model,
                    timeout_seconds = embedding.timeout_seconds,
                }) : ModelProfileJson.Embedding(embedding).ToString(Formatting.None);
            JObject main = ModelProfileJson.Main(value.config.main_model);
            main["api_key"] = value.config.main_model.api_key;
            if (embedding.enabled && !embedding.reuse_main_api)
            {
                JObject embedded = JObject.Parse(embeddingJson);
                embedded["api_key"] = embedding.api_key;
                embeddingJson = embedded.ToString(Formatting.None);
            }
            return "{\"expected_revision\":" + value.expected_revision.ToString(CultureInfo.InvariantCulture) +
                ",\"config\":{\"main_model\":" + main.ToString(Formatting.None) +
                ",\"embedding\":" + embeddingJson + "}}";
        }

        public IEnumerator DiscoverModels(DiscoverModelsRequest value,
            Action<DiscoverModelsResponse, string> done) =>
            Send("POST", "v1/model-config/discover-models", JsonUtility.ToJson(value), done,
                RequestTimeout(value.timeout_seconds));

        public IEnumerator TestMain(MainModelConfig value, Action<TestConnectionResponse, string> done) =>
            Send("POST", "v1/model-config/test", MainTestJson(value), done, RequestTimeout(value.timeout_seconds));

        private static string MainTestJson(MainModelConfig value)
        {
            JObject main = ModelProfileJson.Main(value); main["api_key"] = value.api_key;
            return new JObject { ["kind"] = "main", ["main_model"] = main }.ToString(Formatting.None);
        }

        public IEnumerator TestEmbedding(MainModelConfig main, EmbeddingConfig embedding,
            Action<TestConnectionResponse, string> done) =>
            Send("POST", "v1/model-config/test", EmbeddingTestJson(main, embedding), done,
                RequestTimeout(embedding.timeout_seconds));

        private static string EmbeddingTestJson(MainModelConfig main, EmbeddingConfig embedding)
        {
            JObject saved = JObject.Parse(SerializeSaveRequest(new ModelConfigSaveRequest { config = new ModelConfigDocument
                { main_model = main, embedding = embedding } }));
            return new JObject { ["kind"] = "embedding", ["main_model"] = saved["config"]["main_model"],
                ["embedding"] = saved["config"]["embedding"] }.ToString(Formatting.None);
        }

        public IEnumerator GetRuntimeStatus(Action<RuntimeStatusView, string> done) =>
            Send("GET", "v1/runtime-status", null, done);

        public IEnumerator GetRuntimeStatus(Action<RuntimeStatusView, string> done, Action<long> observedHttpStatus) =>
            Send("GET", "v1/runtime-status", null, done, observedHttpStatus: observedHttpStatus);

        private static int RequestTimeout(float seconds) => Mathf.Clamp(Mathf.CeilToInt(seconds) + 5, 5, 610);

        private IEnumerator Send<T>(string method, string path, string body, Action<T, string> done,
            int timeoutSeconds = 20, Action<long> observedHttpStatus = null)
            where T : class
        {
            if (PortableDataPaths.StartupError != null)
            {
                observedHttpStatus?.Invoke(0);
                done?.Invoke(null, PortableDataPaths.StartupError);
                yield break;
            }
            if (backendClient == null)
            {
                observedHttpStatus?.Invoke(0);
                done?.Invoke(null, "BACKEND_UNAVAILABLE");
                yield break;
            }

            using (var request = new UnityWebRequest(backendClient.HttpUrl(path), method))
            {
                if (!LocalBackendAccess.TryAuthorize(request, out var sessionError))
                {
                    observedHttpStatus?.Invoke(0);
                    done?.Invoke(null, sessionError);
                    yield break;
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = timeoutSeconds;
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                yield return request.SendWebRequest();
                observedHttpStatus?.Invoke(request.responseCode);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string code = "BACKEND_UNAVAILABLE";
                    if (request.responseCode > 0)
                    {
                        try
                        {
                            var error = JsonUtility.FromJson<ModelConfigErrorEnvelope>(
                                request.downloadHandler.text);
                            code = string.IsNullOrEmpty(error?.error?.code) ? "HTTP_ERROR" : error.error.code;
                        }
                        catch (ArgumentException) { code = "HTTP_ERROR"; }
                    }
                    else if (!string.IsNullOrEmpty(request.error) &&
                             request.error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                        code = "TIMEOUT";
                    done?.Invoke(null, code);
                    yield break;
                }

                try
                {
                    T value = JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
                    done?.Invoke(value, value == null ? "INVALID_RESPONSE" : null);
                }
                catch (JsonException) { done?.Invoke(null, "INVALID_RESPONSE"); }
            }
        }
    }
}
