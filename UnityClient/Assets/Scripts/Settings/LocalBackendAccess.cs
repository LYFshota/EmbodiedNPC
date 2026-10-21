using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace EmbodiedNPC.Settings
{
    /// <summary>Reads the Backend's private process capability without caching or owning its lifecycle.
    /// It authorizes native transport only; it is neither a model API Key nor a GameVisit identity.</summary>
    public static class LocalBackendAccess
    {
        private static readonly Regex LocalAuthority = new(
            @"\A(https?|wss?)://(127\.0\.0\.1|localhost|\[::1\])(?::([1-9][0-9]{0,4}))?(?=/|\z)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TokenFormat = new(@"\A[A-Za-z0-9_-]{32,256}\z", RegexOptions.CultureInvariant);

        private static bool Endpoint(string value, bool baseOnly, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrEmpty(value) || !LocalAuthority.IsMatch(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Port <= 0 || uri.UserInfo.Length != 0 ||
                uri.Fragment.Length != 0 || (baseOnly && (uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
                    (uri.Scheme != "http" && uri.Scheme != "https")))) return false;
            if ((uri.Scheme == "ws" || uri.Scheme == "wss") && (uri.AbsolutePath != "/ws" || uri.Query.Length != 0)) return false;
            return true;
        }

        private static bool SameEndpoint(Uri target, Uri source)
        {
            string scheme = target.Scheme == "ws" ? "http" : target.Scheme == "wss" ? "https" : target.Scheme;
            return scheme == source.Scheme && string.Equals(target.Host, source.Host, StringComparison.OrdinalIgnoreCase) &&
                target.Port == source.Port;
        }

        /// <summary>Read again on every request/connection attempt so Backend restart needs no token UI.
        /// A transient replacement/read error fails closed; writes and paid probes are never replayed here.</summary>
        public static bool TryHeaders(string target, out Dictionary<string, string> headers, out string error,
            string sessionPath = null)
        {
            headers = null;
            error = "LOCAL_DEVELOPER_SESSION_INVALID";
            try
            {
                if (!Endpoint(target, false, out var endpoint))
                { error = "LOCAL_DEVELOPER_SESSION_URL_INVALID"; return false; }
                string file = sessionPath ?? Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE");
                if (PortableDataPaths.IsPortable)
                {
                    if (PortableDataPaths.StartupError != null) { error = PortableDataPaths.StartupError; return false; }
                    if (!PortableDataPaths.Current.TrySessionPath(file, out file))
                    { error = "LOCAL_DEVELOPER_SESSION_PATH_INVALID"; return false; }
                }
                else file ??= Path.GetFullPath(Path.Combine(Application.dataPath, "../../AIBackend/data/knowledge_admin_session.json"));
                JObject session;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 65536) return false;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        // Bound the actual read too, even if another writer grows a damaged file.
                        var buffer = new char[65537];
                        int count = reader.ReadBlock(buffer, 0, buffer.Length);
                        if (count > 65536) return false;
                        // Keep the ISO timestamp a JSON string; Json.NET otherwise
                        // converts it to a Date token before our field validation.
                        using (var json = new JsonTextReader(new StringReader(new string(buffer, 0, count)))
                            { DateParseHandling = DateParseHandling.None })
                        {
                            session = JObject.Load(json, new JsonLoadSettings
                            { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                            if (json.Read()) return false;
                        }
                    }
                }
                if (session["schema_version"]?.Type != JTokenType.Integer || (int?)session["schema_version"] != 1 ||
                    session["base_url"]?.Type != JTokenType.String || session["token"]?.Type != JTokenType.String ||
                    session["pid"]?.Type != JTokenType.Integer || (long?)session["pid"] <= 0 ||
                    session["created_at"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)session["created_at"])) return false;
                if (!Endpoint((string)session["base_url"], true, out var source) || !SameEndpoint(endpoint, source))
                { error = "LOCAL_DEVELOPER_SESSION_MISMATCH"; return false; }
                string configured = Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL");
                if (configured != null && (!Endpoint(configured, true, out var expected) || !SameEndpoint(endpoint, expected)))
                { error = "LOCAL_DEVELOPER_SESSION_MISMATCH"; return false; }
                string token = (string)session["token"];
                if (token == null || !TokenFormat.IsMatch(token)) return false;
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
                error = null;
                return true;
            }
            catch (UnauthorizedAccessException) { error = "LOCAL_DEVELOPER_SESSION_ACCESS_DENIED"; return false; }
            catch (FileNotFoundException) { error = "LOCAL_DEVELOPER_SESSION_MISSING"; return false; }
            catch (DirectoryNotFoundException) { error = "LOCAL_DEVELOPER_SESSION_MISSING"; return false; }
            catch (IOException) { error = "LOCAL_DEVELOPER_SESSION_UNREADABLE"; return false; }
            catch (Exception failure) when (failure is JsonException || failure is ArgumentException || failure is InvalidCastException ||
                failure is FormatException || failure is OverflowException || failure is NotSupportedException)
            { return false; }
        }

        public static bool TryAuthorize(UnityWebRequest request, out string error, string sessionPath = null)
        {
            request.redirectLimit = 0; // Never forward this capability to a redirected endpoint.
            if (!TryHeaders(request.url, out var headers, out error, sessionPath)) return false;
            request.SetRequestHeader("Authorization", headers["Authorization"]);
            return true;
        }
    }
}
