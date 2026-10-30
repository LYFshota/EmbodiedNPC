using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbodiedNPC.Settings
{
    /// <summary>Offline, bounded technical projection of existing logs. Never exports raw log lines.</summary>
    public static partial class DiagnosticsExport
    {
        public const int MaxSourceBytes = 256 * 1024, MaxSources = 12;
        private static readonly HashSet<string> Codes = new((
            "BACKEND_UNAVAILABLE TIMEOUT INVALID_RESPONSE CONNECTION_FAILED CONNECTED DISCONNECTED OFFLINE " +
            "COGNITIVE_REQUEST_FAILED COGNITIVE_PROMPT_BUDGET_EXCEEDED SCHEMA_ERROR INVALID_DECISION " +
            "COGNITIVE_ADMISSION_REJECTED COGNITIVE_INPUT_COMMIT_FAILED RECALL_UNAVAILABLE NO_MEMORY_FOUND " +
            "EXPERIENCE_ACK_TIMEOUT EXPERIENCE_APPEND_FAILED EMBEDDING_REQUEST_FAILED EMBEDDING_INDEX_FAILED " +
            "EMBEDDING_PAUSED EMBEDDING_CLOUD_CONFIRMATION_REQUIRED EMBEDDING_PREVIOUS_REQUEST_FAILED " +
            "EMBEDDING_CONTROL_STATE_INVALID EMBEDDING_CONTROL_WRITE_FAILED BUILTIN_EMBEDDING_FILE_MISSING " +
            "BUILTIN_EMBEDDING_LOAD_FAILED BUILTIN_EMBEDDING_RESOURCE_INVALID MODEL_NOT_CONFIGURED " +
            "PROVIDER_UNAVAILABLE PROVIDER_REQUEST_FAILED MODEL_CONFIG_CONFLICT REVISION_CONFLICT " +
            "KNOWLEDGE_ADMIN_FILE_MISSING KNOWLEDGE_ADMIN_SESSION_INVALID KNOWLEDGE_ACCESS_DENIED " +
            "UNIFIED_START_FAILED UNIFIED_DIAGNOSTIC UNIFIED_ALREADY_RUNNING UNIFIED_RECORD_DEFERRED " +
            "UNIFIED_BACKEND_EXITED UNIFIED_BACKEND_START_FAILED UNIFIED_STOP_INCOMPLETE UNIFIED_STALE_RECORD " +
            "UNIFIED_PLAYER_START_FAILED UNIFIED_PLAYER_IDENTITY_UNCONFIRMED UNIFIED_PROCESS_IDENTITY_UNAVAILABLE " +
            "PORTABLE_ALREADY_RUNNING PORTABLE_PORT_UNAVAILABLE PORTABLE_STOP_TIMEOUT PORTABLE_INSTANCE_CHANGED " +
            "PORTABLE_BACKEND_START_FAILED PORTABLE_ROOT_UNWRITABLE " +
            "IOException UnauthorizedAccessException NullReferenceException InvalidOperationException " +
            "ArgumentException TimeoutException HttpRequestException WebSocketException JsonException " +
            "ValueError TypeError RuntimeError OSError PermissionError ConnectionError TimeoutError " +
            "ProviderError LaunchError PortableError ValidationError ReadTimeout ConnectTimeout HTTPStatusError"
        ).Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> Stages = new((
            "starting listening launching_player running stopped failed player_identity_pending " +
            "cognitive_request_created cognitive_response cognitive_run_started cognitive_run_terminal " +
            "connection_status experience_append_settled transport_failure " +
            "publish_player read_previous_record cleanup_player startup Ready Unavailable Checking " +
            "Unconfigured PendingApplication Connecting Loading"
        ).Split(' '), StringComparer.Ordinal);
        private static string captureFile, currentScene, lastFolder;
        private static JObject modelStatus;
        private static readonly object Sync = new();
        public static string LastFolder => lastFolder;
        // Projection of an already received public response. No additional HTTP or credential read.
        public static void ObserveModelStatus(ModelConfigView view)
        {
            string Name(string value) => value != null && Regex.IsMatch(value,@"^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,127}$") ? value : null;
            modelStatus = view == null ? null : new JObject {
                ["revision"]=view.revision, ["saved_configured"]=view.saved_configured,
                ["observed_at_utc"]=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),
                ["startup_ready"]=view.configured, ["restart_required"]=view.profiles?.restart_required,
                ["saved_provider"]=Name(view.main_model?.provider_type), ["saved_model"]=Name(view.main_model?.model),
                ["active_provider"]=Name(view.profiles?.active_runtime?.main_model?.provider_type),
                ["active_model"]=Name(view.profiles?.active_runtime?.main_model?.model),
                ["embedding_enabled"]=view.profiles?.active_runtime?.embedding?.enabled,
                ["embedding_provider"]=Name(view.profiles?.active_runtime?.embedding?.provider_type)
            };
        }

        public static string DataDirectory => PortableDataPaths.IsPortable
            ? PortableDataPaths.Current.Error == null ? PortableDataPaths.Current.Root : throw new IOException("PORTABLE_ROOT_UNAVAILABLE")
            : Path.Combine(Application.persistentDataPath, "UserData");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            Application.logMessageReceivedThreaded -= Capture;
            SceneManager.activeSceneChanged -= SceneChanged;
            captureFile = null; modelStatus=null; currentScene = SceneManager.GetActiveScene().name;
            if (Application.isEditor) return; // Editor tests export explicit private fixtures, never capture personal Editor logs.
            try
            {
                var logs = Path.Combine(DataDirectory, "logs"); Directory.CreateDirectory(logs);
                captureFile = Path.Combine(logs, "diagnostics-current.jsonl");
            }
            catch (Exception) { /* Export still works when an existing log can be read. */ }
            Application.logMessageReceivedThreaded += Capture;
            SceneManager.activeSceneChanged += SceneChanged;
        }
        private static void SceneChanged(Scene previous, Scene next) => currentScene = next.name;
        private static void Capture(string message, string stack, LogType type)
        {
            if (captureFile == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Warning)) return;
            var projected = ProjectLine(message + "\n" + stack);
            if (projected == null) return;
            projected["utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            projected["pid"] = Process.GetCurrentProcess().Id;
            projected["scene"] = currentScene is "TitleScene" or "CafeDemoV2" ? currentScene : "other";
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(captureFile) || new FileInfo(captureFile).Length < 1024 * 1024)
                        File.AppendAllText(captureFile, projected.ToString(Formatting.None) + "\n", Encoding.UTF8);
                }
                catch (Exception) { /* Diagnostics must never interrupt gameplay or recurse into logging. */ }
            }
        }

        // Only known code vocabulary and structural metadata survive. Unknown text is excluded,
        // including exception messages, query strings, source lines, model payloads and chat.
        public static JObject ProjectLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 16384) return null;
            if (Regex.IsMatch(line, @"(?i)bearer\s|authorization|api[ _-]?key|access[ _-]?token|secret|password|[?&][^\s=]+=")) return null;
            var found = Regex.Matches(line, @"\b[A-Za-z][A-Za-z0-9_]+\b").Cast<Match>()
                .Select(m => m.Value).Where(Codes.Contains).Distinct().Take(20).ToArray();
            var output = new JObject();
            // Task diagnostics use the same strict projection on capture AND export.
            JObject taskSource = null;
            int taskMarker = line.IndexOf("TASK_DIAGNOSTIC ", StringComparison.Ordinal);
            try
            {
                if(taskMarker>=0) taskSource=JObject.Parse(line.Substring(taskMarker+"TASK_DIAGNOSTIC ".Length));
                else if(line.TrimStart().StartsWith("{",StringComparison.Ordinal))
                    taskSource=JObject.Parse(line)["task_diagnostic"] as JObject;
            }
            catch(JsonException) { }
            var taskProjection=ProjectTask(taskSource);
            if(taskProjection!=null) output["task_diagnostic"]=taskProjection;
            if (found.Length > 0) output["codes"] = new JArray(found);
            // These are labelled correlation IDs, never arbitrary hexadecimal values.
            foreach (var key in new[] { "trace_id", "request_id", "conversation_id", "run_id" })
            {
                var id = Regex.Match(line, "[\"']?" + key + "[\"']?\\s*[:=]\\s*[\"']?((?:cognitive-(?:request|trace)-|experience-(?:request|trace)-|failure-trace-|counter-trace-)?(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}))(?=[\"'\\s,}]|$)");
                if (id.Success) output[key] = id.Groups[1].Value;
            }
            var status = Regex.Match(line, "(?i)\\b(?:http_status|HTTP(?:/1\\.[01])?|status(?:_code)?)[\"']?\\s*[:=]?\\s*([45]\\d\\d)\\b");
            if (status.Success) output["http_status"] = int.Parse(status.Groups[1].Value);
            var stamp = Regex.Match(line, @"\b\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)\b");
            if (stamp.Success && DateTimeOffset.TryParse(stamp.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                output["utc"] = time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            // Export only project stack symbols, without arguments, paths or source code.
            var frames = Regex.Matches(line, "\\b(?:EmbodiedNPC|CafeDemo)\\.[A-Za-z0-9_.+<>`]+(?=\\s*[:(\"'])")
                .Cast<Match>().Select(m => m.Value).Distinct().Take(12).ToArray();
            if (frames.Length > 0) output["frames"] = new JArray(frames);
            var stage = Regex.Match(line, "[\"']?(?:stage|kind|state)[\"']?\\s*[:=]\\s*[\"']?([A-Za-z_]+)");
            if (stage.Success && Stages.Contains(stage.Groups[1].Value)) output["stage"] = stage.Groups[1].Value;
            var scene=Regex.Match(line,"[\"']?scene[\"']?\\s*[:=]\\s*[\"']?(TitleScene|CafeDemoV2)(?=[\"'\\s,}]|$)");
            if(scene.Success)output["scene"]=scene.Groups[1].Value;
            var conversation=Regex.Match(line,"[\"']?conversation(?:_id)?[\"']?\\s*[:=]\\s*[\"']?(player_01:npc_cafe_(?:01|regular_01|companion_01|quiet_01):[a-f0-9]{32})(?=[\"'\\s,;}]|$)");
            if(conversation.Success)output["conversation_id"]=conversation.Groups[1].Value;
            int diagnostic=line.IndexOf("UNIFIED_DIAGNOSTIC ",StringComparison.Ordinal);
            if(diagnostic>=0)
            {
                try
                {
                    var data=JObject.Parse(line.Substring(diagnostic+"UNIFIED_DIAGNOSTIC ".Length));
                    var framesOut=new JArray();
                    foreach(var frame in (data["chain"] as JArray??new JArray()).OfType<JObject>()
                        .SelectMany(c=>(c["frames"] as JArray??new JArray()).OfType<JObject>()).Take(20))
                        if(new[]{"unified.py","launcher.py"}.Contains((string)frame["file"]) &&
                            new[]{"start","stop","main","serve","publish","retry_metadata","owned_identity","identity","handle_identity","controlled_stop","recover","cleanup"}.Contains((string)frame["function"]) && frame["line"]?.Type==JTokenType.Integer)
                            framesOut.Add(new JObject{["file"]=(string)frame["file"],["line"]=(int)frame["line"],["function"]=(string)frame["function"]});
                    if(framesOut.Count>0)output["launcher_frames"]=framesOut;
                }
                catch(JsonException) { }
            }
            var pid = Regex.Match(line, @"\bpid[\""']?\s*[:=]\s*(\d{1,9})\b");
            if (pid.Success) output["pid"] = int.Parse(pid.Groups[1].Value);
            return output.Count > (output["utc"] != null ? 1 : 0) ? output : null;
        }

        public static string Export(string root = null)
        {
            root ??= DataDirectory;
            var logs = Path.Combine(root, "logs"); var folder = Path.Combine(root, "Exports");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "EmbodiedNPC_Diagnostics_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".zip");
            var manifest = new JObject {
                ["exported_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["local_time"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                ["unity_version"] = Application.unityVersion, ["version"] = Application.version,
                ["platform"] = Application.platform.ToString(), ["pid"] = Process.GetCurrentProcess().Id,
                ["last_observed_model_status"]=modelStatus?.DeepClone(),
                ["policy"] = "Technical projection only. Raw text, unknown fields, credentials, chat, model payloads, databases and config files excluded. No network requests.",
                ["time_policy"] = "UTC is preserved only when explicitly present. Missing timestamps remain unknown; file order is not cross-process chronology. Relative runtime clocks are not wall time.",
                ["limits"] = "12 newest eligible files; tail 256 KiB each; at most 2000 projected lines each; capture stops at 1 MiB.",
                ["excluded_sources"] = "All files outside logs, raw Runtime Trace/chat, reparse points, unknown log names and non-log assets. No credential/config files are read; only the last already-received public status projection is included."
            };
            var sources = new JArray(); manifest["sources"] = sources;
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var files = Directory.Exists(logs) && (File.GetAttributes(logs) & FileAttributes.ReparsePoint) == 0
                    ? Directory.GetFiles(logs).Where(p => Regex.IsMatch(Path.GetFileName(p), @"^(?:Player(?:-restarted)?\.log|backend(?:-start)?_\d+(?:_\d+)?\.log|diagnostics-current\.jsonl|portable\.log)$"))
                        .OrderByDescending(File.GetLastWriteTimeUtc).Take(MaxSources).ToArray() : Array.Empty<string>();
                int index = 0;
                foreach (string source in files)
                {
                    var info = new JObject { ["source"] = Path.GetFileName(source) }; sources.Add(info);
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { info["excluded"] = "reparse point"; continue; }
                    try
                    {
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        bool truncated = input.Length > MaxSourceBytes;
                        input.Position = Math.Max(0, input.Length - MaxSourceBytes);
                        var bytes=new byte[(int)Math.Min(input.Length,MaxSourceBytes)];
                        int read=0, count; while(read<bytes.Length && (count=input.Read(bytes,read,bytes.Length-read))>0) read+=count;
                        var lines=Encoding.UTF8.GetString(bytes,0,read).Split('\n').Skip(truncated?1:0).Take(4000);
                        var safe = new StringBuilder(); int accepted = 0, omitted = 0;
                        foreach (string line in lines)
                        {
                            if(accepted>=2000)break;
                            var value = ProjectLine(line);
                            if (value == null) { omitted++; continue; }
                            // Capture is already projected, but still passes through the same policy.
                            safe.AppendLine(value.ToString(Formatting.None)); accepted++;
                        }
                        info["projected_lines"] = accepted; info["omitted_lines"] = omitted; info["tail_only"] = truncated;
                        using var writer = new StreamWriter(zip.CreateEntry("technical-" + index++ + ".jsonl").Open(), Encoding.UTF8);
                        writer.Write(safe);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { info["excluded"] = e.GetType().Name; }
                }
                using var summary = new StreamWriter(zip.CreateEntry("manifest.json").Open(), Encoding.UTF8);
                summary.Write(manifest.ToString(Formatting.Indented));
            }
            lastFolder = folder; return path;
        }
        public static string TryExport()
        {
            try { return SettingsUiLanguage.T("已导出：", "Exported: ") + Export(); }
            catch (Exception error) { return SettingsUiLanguage.T("导出失败：", "Export failed: ") + error.GetType().Name; }
        }
        public static string EnsureExportDirectory(string dataRoot)
        {
            string folder=Path.Combine(dataRoot,"Exports");
            Directory.CreateDirectory(folder); return folder;
        }
        public static string TryOpenFolder()
        {
            try
            {
                string folder=EnsureExportDirectory(DataDirectory);
                Process.Start(new ProcessStartInfo { FileName=folder, UseShellExecute=true });
                return SettingsUiLanguage.T("已打开导出目录。", "Export folder opened.");
            }
            catch(Exception failure)
            { return SettingsUiLanguage.T("无法打开导出目录：", "Cannot open export folder: ")+failure.GetType().Name; }
        }
        public static void OpenFolder() => TryOpenFolder();
    }
}
