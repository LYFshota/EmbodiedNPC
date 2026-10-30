using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CafeDemo
{
    /// <summary>Read-only local export orchestration; no uploads, model calls or save mutations.</summary>
    public sealed class ExperienceDataExport : MonoBehaviour
    {
        public bool IsBusy { get; private set; }
        public string LastPath { get; private set; }
        public string LastResult { get; private set; }
        private UnityWebRequest download;
        private CancellationTokenSource cancellation;
        private string temporaryBackend, temporaryDiagnostics;
        private Task packingTask;
        public const long MaxDownloadBytes = 128L * 1024 * 1024;
        public const long MaxExpandedBytes = 256L * 1024 * 1024;
        private const int MaxArchiveEntries = 32, MaxManifestBytes = 1024 * 1024;
        public string RootOverride { get; set; } // Isolated fixtures only; normal callers use the existing UserData root.

        public void Begin(Action<string> status)
        {
            if (!IsBusy) StartCoroutine(Export(status));
        }

        public static JObject Personas(CharacterDefinition[] definitions, CharacterOverrideStore store)
        {
            var items = new JArray();
            foreach (var d in definitions.Where(d => d != null).GroupBy(d => d.CharacterId).Select(g => g.First()).OrderBy(d => d.CharacterId))
            {
                store.TryGet(d.CharacterId, out var edit);
                items.Add(new JObject { ["character_id"] = d.CharacterId,
                    ["default"] = JToken.FromObject(CognitiveCharacterDto.From(d)),
                    ["override"] = edit == null ? null : JToken.FromObject(edit),
                    ["effective"] = JToken.FromObject(CharacterProfileResolver.Resolve(d, store)) });
            }
            return new JObject { ["characters"] = items, ["all_saved_overrides"] = JArray.FromObject(store.Snapshot()),
                ["overrides_readable"] = !store.LoadRejected,
                ["note"] = "Defaults are loaded CharacterDefinitions; effective Persona is resolved from those defaults and saved overrides, not unsaved UI drafts. This is the source Persona before Backend language projection; persona_language_projections.json contains the existing English views and exact hash-match conditions. Custom overrides keep their own text." };
        }

        private static JObject PlayerProfile()
        {
            var identity = FindAnyObjectByType<PlayerIdentityBinding>()?.CurrentIdentity;
            var avatar = FindAnyObjectByType<PlayerVrmAppearance>();
            var reference = avatar?.Store?.Selected;
            bool safe = reference == null || System.Text.RegularExpressions.Regex.IsMatch(reference, @"^Avatars/Imported/[a-f0-9]{64}\.vrm$");
            return new JObject {
                ["identity"] = identity == null ? null : JObject.FromObject(identity),
                ["saved_name"] = JObject.FromObject(PlayerNameSettings.Current), ["name_read_error"] = PlayerNameSettings.Error,
                ["appearance_description"] = avatar?.AppearanceDescription,
                ["avatar_reference"] = safe ? reference ?? "builtin:default" : "[invalid reference]",
                ["avatar_metadata_available"] = avatar?.Store != null,
                ["avatar_is_custom"] = avatar?.IsCustom ?? false,
                ["avatar_binary_included"] = false,
                ["ui_language"] = SettingsUiLanguage.IsEnglish ? "en" : "zh",
                ["npc_reply_language"] = NpcReplyLanguageSettings.Preference.ToString()
            };
        }

        private IEnumerator Export(Action<string> status)
        {
            IsBusy = true; LastPath = LastResult = null; cancellation = new CancellationTokenSource();
            string folder = null;
            JObject local = null;
            string backendError = "BACKEND_UNAVAILABLE", diagnosticsError = null;
            try
            {
                status?.Invoke(SettingsUiLanguage.T("正在只读收集体验数据…", "Collecting saved experience data (read-only)…"));
                string dataRoot = RootOverride ?? DiagnosticsExport.DataDirectory;
                ValidatePath(dataRoot);
                folder = DiagnosticsExport.EnsureExportDirectory(dataRoot);
                ValidatePath(folder);
                temporaryBackend = Path.Combine(folder, ".experience-backend-" + Guid.NewGuid().ToString("N") + ".tmp");
                var definitions = FindObjectsByType<CognitiveContextComposer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Select(c => c.CharacterDefinition).Where(d => d != null).ToArray();
                if (definitions.Length == 0) definitions = Resources.FindObjectsOfTypeAll<CharacterDefinition>();
                local = new JObject { ["npc_personas"] = Personas(definitions, CharacterOverrideStore.Shared), ["player_profile"] = PlayerProfile(),
                    ["runtime"] = new JObject { ["version"] = Application.version, ["unity_version"] = Application.unityVersion,
                        ["platform"] = Application.platform.ToString(), ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name } };
                try { temporaryDiagnostics = DiagnosticsExport.Export(RootOverride ?? DiagnosticsExport.DataDirectory); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { diagnosticsError = e.GetType().Name; }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            { LastResult = SettingsUiLanguage.T("无法创建体验导出：", "Cannot create experience export: ") + e.GetType().Name; }
            if (local == null) { Finish(status); yield break; }
            if (local["player_profile"]?["identity"]?.Type != JTokenType.Object)
            {
                LastResult = SettingsUiLanguage.T("请先进入咖啡馆，再导出全部体验数据。", "Enter the cafe before exporting all experience data.");
                Finish(status); yield break;
            }
            string backendPath = null;
            var backend = FindAnyObjectByType<BackendWebSocketClient>();
            if (backend != null)
            {
                download = UnityWebRequest.Get(backend.HttpUrl("v1/experience-export"));
                download.downloadHandler = new DownloadHandlerFile(temporaryBackend) { removeFileOnAbort = true };
                download.timeout = 120;
                if (LocalBackendAccess.TryAuthorize(download, out var authorizationError))
                {
                    var operation = download.SendWebRequest();
                    bool exceeded = false;
                    while (!operation.isDone)
                    {
                        if (download.downloadedBytes > (ulong)MaxDownloadBytes) { exceeded = true; download.Abort(); break; }
                        yield return null;
                    }
                    if (exceeded || download.downloadedBytes > (ulong)MaxDownloadBytes) backendError = "BACKEND_PACKAGE_TOO_LARGE";
                    else if (download.result == UnityWebRequest.Result.Success) { backendPath = temporaryBackend; backendError = null; }
                    else backendError = "BACKEND_HTTP_" + download.responseCode;
                }
                else backendError = authorizationError;
                download.Dispose(); download = null;
            }
            var token = cancellation.Token;
            string diagnostics = temporaryDiagnostics;
            var packing = Task.Run(() => BuildArchive(folder, backendPath, local, diagnostics, backendError, diagnosticsError, token), token);
            packingTask = packing;
            while (!packing.IsCompleted) yield return null;
            if (packing.IsCompletedSuccessfully)
            {
                LastPath = packing.Result.path;
                string missing = string.Join(", ", packing.Result.incomplete.Select(SectionCaption));
                LastResult = packing.Result.incomplete.Length == 0
                    ? SettingsUiLanguage.T("全部体验数据已导出：", "All experience data exported: ") + LastPath
                    : SettingsUiLanguage.T("已生成不完整体验包。未完整导出的类别：", "Partial experience package created. Incomplete sections: ") + missing + "\n" + LastPath;
            }
            else LastResult = SettingsUiLanguage.T("体验 ZIP 生成失败：", "Experience ZIP failed: ") +
                (packing.Exception?.GetBaseException().GetType().Name ?? "CANCELLED");
            Finish(status);
        }

        private static string SectionCaption(string key) => key switch {
            "npc_personas" => SettingsUiLanguage.T("NPC 人设", "NPC Persona"),
            "persona_language_projections" => SettingsUiLanguage.T("人设语言投影", "Persona language projections"),
            "player_profile" => SettingsUiLanguage.T("玩家设定", "Player profile"),
            "world_knowledge" => SettingsUiLanguage.T("世界知识", "World knowledge"),
            "experiences" => SettingsUiLanguage.T("原始经历", "Experience Ledger"),
            "conversations" => SettingsUiLanguage.T("聊天记录", "Conversations"),
            "episodes" => SettingsUiLanguage.T("经历片段", "Episodes"),
            "memories" => SettingsUiLanguage.T("长期记忆", "Memories"),
            "relationships" => SettingsUiLanguage.T("关系记录", "Relationships"),
            "tasks" => SettingsUiLanguage.T("持久任务", "Tasks"),
            "actions" => SettingsUiLanguage.T("行动结果", "Actions"),
            "diagnostics" => SettingsUiLanguage.T("脱敏诊断", "Diagnostics"),
            "legacy_unscoped" => SettingsUiLanguage.T("无法归属的旧数据", "Unattributed legacy data"),
            _ => key };

        private void Finish(Action<string> status)
        {
            Cleanup(); IsBusy = false;
            status?.Invoke(LastResult);
        }
        private void Cleanup()
        {
            download?.Abort(); download?.Dispose(); download = null;
            foreach (string p in new[] { temporaryBackend, temporaryDiagnostics })
                if (p != null) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            temporaryBackend = temporaryDiagnostics = null;
            cancellation?.Dispose(); cancellation = null;
        }
        private void OnDisable()
        {
            cancellation?.Cancel(); StopAllCoroutines();
            // Packing checks its token and only publishes a complete ZIP. Deleting open read inputs
            // may wait until the worker releases them; its finally owns that cleanup as well.
            var inputs = new[] { temporaryBackend, temporaryDiagnostics };
            if (packingTask != null && !packingTask.IsCompleted)
                _ = packingTask.ContinueWith(_ => {
                    foreach (string p in inputs) if (p != null) { try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }, TaskScheduler.Default);
            Cleanup(); IsBusy = false;
        }

        private static readonly string[] BackendEntries = { "world_knowledge.json", "experiences.jsonl", "conversations.jsonl", "conversations.md",
            "episodes.json", "memories.json", "relationships.json", "tasks.json", "actions.json", "legacy_unscoped.json", "persona_language_projections.json", "backend_manifest.json" };

        private static void ValidatePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("EXPORT_LINK_NOT_ALLOWED");
            for (var directory = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("EXPORT_LINK_NOT_ALLOWED");
        }

        private static void ValidateArchive(ZipArchive input)
        {
            if (input.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("EXPORT_ENTRY_LIMIT");
            long expanded = 0;
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in input.Entries)
            {
                if (!names.Add(entry.FullName)) throw new InvalidDataException("EXPORT_DUPLICATE_ENTRY");
                // No extraction is used. Unknown names are ignored, never copied or interpreted.
                expanded = checked(expanded + entry.Length);
                if (expanded > MaxExpandedBytes) throw new InvalidDataException("EXPORT_SIZE_LIMIT");
                if (entry.FullName.EndsWith("manifest.json", StringComparison.Ordinal) && entry.Length > MaxManifestBytes)
                    throw new InvalidDataException("EXPORT_MANIFEST_LIMIT");
            }
        }

        public static (string path, string[] incomplete) BuildArchive(string folder, string backendZip, JObject local,
            string diagnosticsZip, string backendError = null, string diagnosticsError = null, CancellationToken token = default)
        {
            ValidatePath(folder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "EmbodiedNPC_Experience_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
            string temporary = path + ".partial";
            var sections = new JObject();
            var manifest = new JObject { ["schema_version"] = 1, ["exported_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["local_time"] = DateTimeOffset.Now.ToString("o"), ["runtime"] = local["runtime"]?.DeepClone(), ["sections"] = sections,
                ["privacy_notice"] = "Authored dialogue, Persona, knowledge and memory text is preserved and may contain information the player typed. Local export only; nothing is uploaded.",
                ["consistency"] = "Backend SQLite uses one read-only snapshot; knowledge and Unity settings are separately timestamped snapshots. Missing/deleted data is not reconstructed.",
                ["excluded"] = "Credentials/auth files, model endpoints, machine paths in structural metadata, raw logs, model weights, vectors, Unity caches and avatar binaries. Player-authored prose is preserved.",
                ["limits"] = new JObject { ["download_bytes"] = MaxDownloadBytes, ["expanded_bytes"] = MaxExpandedBytes,
                    ["entries_per_input"] = MaxArchiveEntries, ["policy"] = "Fail or report an unavailable category, never silently truncate records." } };
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    long copied = 0;
                    void Write(string name, JToken value) { token.ThrowIfCancellationRequested(); using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented }; value.WriteTo(json); }
                    void Copy(ZipArchiveEntry source, string name)
                    {
                        token.ThrowIfCancellationRequested();
                        using var input = source.Open(); using var output = zip.CreateEntry(name).Open();
                        byte[] buffer = new byte[65536]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                            token.ThrowIfCancellationRequested(); copied += read;
                            if (copied > MaxExpandedBytes) throw new InvalidDataException("EXPORT_SIZE_LIMIT");
                            output.Write(buffer, 0, read);
                        }
                    }
                    var personas = local["npc_personas"];
                    var profile = local["player_profile"];
                    Write("npc_personas.json", personas); Write("player_profile.json", profile);
                    int personaCount = (personas["characters"] as JArray)?.Count ?? 0;
                    sections["npc_personas"] = new JObject { ["status"] = personaCount > 0 && (bool?)personas["overrides_readable"] == true ? "ok" : "partial", ["count"] = personaCount, ["source"] = "CharacterDefinition + CharacterOverrideStore" };
                    sections["player_profile"] = new JObject { ["status"] = profile["identity"]?.Type == JTokenType.Object && string.IsNullOrEmpty((string)profile["name_read_error"]) && (bool?)profile["avatar_metadata_available"] == true ? "ok" : "partial", ["count"] = 1, ["source"] = "Current identity and saved player preferences" };
                    bool backendRead = false;
                    bool backendCopyStarted = false;
                    if (backendZip != null)
                    {
                        try
                        {
                            ValidatePath(backendZip);
                            if (new FileInfo(backendZip).Length > MaxDownloadBytes) throw new InvalidDataException("EXPORT_DOWNLOAD_LIMIT");
                            using var input = ZipFile.OpenRead(backendZip);
                            ValidateArchive(input);
                            using var reader = new StreamReader(input.GetEntry("backend_manifest.json")?.Open() ?? throw new InvalidDataException());
                            var backend = JObject.Parse(reader.ReadToEnd());
                            var sourceSections = (JObject)backend["sections"] ?? throw new InvalidDataException();
                            string currentUid = (string)(profile["identity"] as JObject)?["PlayerUid"];
                            if (currentUid == null || currentUid != (string)(backend["player_identity"] as JObject)?["persistent_id"]) throw new InvalidDataException("PLAYER_SCOPE_MISMATCH");
                            foreach (var name in new[] { "world_knowledge", "experiences", "conversations", "episodes", "memories", "relationships", "tasks", "actions" })
                            {
                                var item = sourceSections[name] as JObject ?? throw new InvalidDataException();
                                string fileName = name + (name is "experiences" or "conversations" ? ".jsonl" : ".json");
                                if ((string)item["status"] == "ok" && input.GetEntry(fileName) == null) throw new InvalidDataException();
                            }
                            foreach (var property in sourceSections.Properties())
                                if (BackendEntries.Contains(property.Name + ".json") || property.Name is "experiences" or "conversations")
                                    sections[property.Name] = property.Value.DeepClone();
                            if (sections["persona_language_projections"] == null)
                                sections["persona_language_projections"] = new JObject { ["status"] = "unavailable", ["count"] = 0, ["error"] = "PERSONA_PROJECTION_UNAVAILABLE" };
                            backendCopyStarted = true;
                            foreach (string name in BackendEntries) if (input.GetEntry(name) is { } entry) Copy(entry, name);
                            backendRead = true;
                        }
                        catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException) {
                            // A failed copy may already have written private entries. Never publish that archive.
                            if (backendCopyStarted) throw;
                            backendError = "BACKEND_PACKAGE_INVALID";
                        }
                    }
                    if (!backendRead)
                        foreach (string name in new[] { "world_knowledge", "experiences", "conversations", "episodes", "memories", "relationships", "tasks", "actions", "persona_language_projections" })
                            sections[name] = new JObject { ["status"] = "unavailable", ["count"] = 0, ["error"] = backendError ?? "BACKEND_UNAVAILABLE" };
                    int diagnosticsCount = 0;
                    if (diagnosticsZip != null)
                    {
                        try
                        {
                            ValidatePath(diagnosticsZip);
                            using var input = ZipFile.OpenRead(diagnosticsZip);
                            ValidateArchive(input);
                            foreach (var entry in input.Entries)
                                if (entry.FullName == "manifest.json" || System.Text.RegularExpressions.Regex.IsMatch(entry.FullName, @"^technical-\d+\.jsonl$"))
                                { Copy(entry, "diagnostics/" + entry.FullName); diagnosticsCount++; }
                        }
                        catch (IOException e) { diagnosticsError = e.GetType().Name; }
                    }
                    sections["diagnostics"] = new JObject { ["status"] = diagnosticsError == null && diagnosticsCount > 0 ? "ok" : "unavailable", ["count"] = diagnosticsCount,
                        ["source"] = "Existing DiagnosticsExport sanitized technical projection (existing limits apply)", ["error"] = diagnosticsError };
                    var missing = sections.Properties().Where(p => (string)p.Value["status"] != "ok").Select(p => p.Name).ToArray();
                    manifest["complete"] = missing.Length == 0;
                    manifest["incomplete_sections"] = JArray.FromObject(missing);
                    Write("manifest.json", manifest);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path);
                return (path, ((JArray)manifest["incomplete_sections"]).Values<string>().ToArray());
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                // Only private temporary inputs owned by the runner are removed there, never caller fixtures.
            }
        }
    }
}
