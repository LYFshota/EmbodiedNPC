using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace EmbodiedNPC.Settings
{
    [Serializable] public sealed class KnowledgeAccessView
    { public string[] character_ids = Array.Empty<string>(), deployment_ids = Array.Empty<string>(), scene_ids = Array.Empty<string>(); }
    [Serializable] public sealed class KnowledgeEntryView
    {
        public string knowledge_id, name, content, knowledge_type, source, updated_at;
        public int version;
        public Dictionary<string, JToken> attributes = new();
        public string[] related_ids = Array.Empty<string>();
        public KnowledgeAccessView access = new();
    }
    [Serializable] public sealed class KnowledgeReferenceView { public string knowledge_id, name; }
    [Serializable] public sealed class KnowledgeItemView
    {
        public KnowledgeEntryView entry;
        public bool has_default, hidden;
        public string origin;
        public KnowledgeReferenceView[] referenced_by = Array.Empty<KnowledgeReferenceView>();
    }
    [Serializable] public sealed class KnowledgeScopesView
    { public string deployment_id; public string[] character_ids = Array.Empty<string>(), scene_ids = Array.Empty<string>(); }
    [Serializable] public sealed class KnowledgeLimitsView
    { public int name, content, knowledge_type, attribute_keys, attribute_key_length, attributes_chars, related_ids; }
    [Serializable] public sealed class KnowledgeIndexView { public string mode; public int ready, total; }
    [Serializable] public sealed class KnowledgeCatalogView
    {
        public int revision;
        public string selected_id;
        public KnowledgeItemView[] items = Array.Empty<KnowledgeItemView>();
        public KnowledgeScopesView scopes;
        public KnowledgeLimitsView limits;
        public KnowledgeIndexView index;
    }
    [Serializable] public sealed class KnowledgeStatusView { public int revision; public KnowledgeIndexView index; }
    [Serializable] public sealed class KnowledgeEditView
    {
        public int expected_revision;
        public string name, content, knowledge_type = "reference", access_mode = "default";
        public Dictionary<string, JToken> attributes = new();
        public string[] related_ids = Array.Empty<string>(), character_ids = Array.Empty<string>(), scene_ids = Array.Empty<string>();
    }

    /// <summary>Native local developer capability. Credentials are never serialized or logged.</summary>
    public sealed class KnowledgeManagementClient
    {
        private readonly Func<string> baseUrl;
        private readonly string sessionPath;
        public KnowledgeManagementClient(Func<string> baseUrl, string sessionPath = null)
        { this.baseUrl = baseUrl; this.sessionPath = sessionPath; }

        public IEnumerator Catalog(Action<KnowledgeCatalogView, string> done) => Send("GET", "", null, done);
        public IEnumerator Status(Action<KnowledgeStatusView, string> done) => Send("GET", "/status", null, done);
        public IEnumerator Save(string id, KnowledgeEditView edit, Action<KnowledgeCatalogView, string> done) =>
            Send(id == null ? "POST" : "PUT", id == null ? "" : "/" + Uri.EscapeDataString(id), edit, done);
        public IEnumerator Delete(string id, int revision, Action<KnowledgeCatalogView, string> done) =>
            Send("DELETE", "/" + Uri.EscapeDataString(id), new { expected_revision = revision }, done);
        public IEnumerator Restore(string id, int revision, Action<KnowledgeCatalogView, string> done) =>
            Send("POST", "/" + Uri.EscapeDataString(id) + "/restore", new { expected_revision = revision }, done);

        private IEnumerator Send<T>(string method, string suffix, object body, Action<T, string> done) where T : class
        {
            string url = baseUrl()?.TrimEnd('/');
            using (var request = new UnityWebRequest(url + "/v1/developer/knowledge" + suffix, method))
            {
                request.redirectLimit = 0; // Never forward the local capability to another endpoint.
                request.timeout = 10;
                request.downloadHandler = new DownloadHandlerBuffer();
                if (!LocalBackendAccess.TryAuthorize(request, out var sessionError, sessionPath))
                { done(null, sessionError); yield break; }
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string code = request.responseCode == 0 ? "BACKEND_UNAVAILABLE" : "KNOWLEDGE_HTTP_ERROR";
                    try { code = (string)JObject.Parse(request.downloadHandler.text)["detail"] ?? code; }
                    catch (JsonException) { }
                    done(null, code); yield break;
                }
                T result = null;
                try { result = JsonConvert.DeserializeObject<T>(request.downloadHandler.text); }
                catch (JsonException) { }
                done(result, result == null ? "KNOWLEDGE_RESPONSE_INVALID" : null);
            }
        }
    }
}
