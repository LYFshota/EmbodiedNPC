using System;
using System.IO;
using UnityEngine;

namespace EmbodiedNPC.Settings
{
    /// <summary>Player-only path contract; no migration, fallback or Backend configuration ownership.</summary>
    public static class PortableDataPaths
    {
        public const string EnvironmentKey = "EMBODIEDNPC_DATA_ROOT";
        private static PortablePathSet paths;
        public static bool IsPortable => !Application.isEditor;
        public static PortablePathSet Current => paths ??= PortablePathSet.Open(
            Environment.GetEnvironmentVariable(EnvironmentKey), Application.dataPath);
        public static string StartupError => !IsPortable ? null : Current.Error ?? PortablePreferences.Current.Error;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { paths = null; PortablePreferences.Reset(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if (IsPortable && StartupError != null) Debug.LogError("[Portable] " + StartupError);
        }

        public static string ErrorDescription(string code) => code switch
        {
            "PORTABLE_ROOT_MISSING" => "未提供 UserData 路径。请通过正确的便携启动入口运行。",
            "PORTABLE_ROOT_RELATIVE" => "UserData 必须使用绝对路径。请检查便携启动入口。",
            "PORTABLE_ROOT_UNSAFE" => "UserData 路径不符合便携约束，不能使用程序、开发或链接目录。",
            "PORTABLE_ROOT_UNWRITABLE" => "无法创建或写入 UserData。请选择有写入权限的游戏目录，无需管理员权限。",
            "PORTABLE_SETTINGS_INVALID" => "settings.json 损坏或版本不支持；已保留原文件。请关闭程序后修复文件，再重新启动。",
            "PORTABLE_SETTINGS_UNREADABLE" => "无法读取 settings.json；已保留原文件。请检查文件权限后重新启动。",
            "PORTABLE_SETTINGS_SAVE_FAILED" => "无法保存 settings.json；原设置文件未被替换。请检查 UserData 写入权限。",
            _ => "便携用户数据环境不可用。请检查启动入口与 UserData 路径。"
        };
    }

    /// <summary>One resolved root, also usable by isolated path tests without changing Editor state.</summary>
    public sealed class PortablePathSet
    {
        public string Root { get; private set; }
        public string Error { get; private set; }
        public string SettingsFile => Child("settings.json");
        public string CharacterFile => Child("npc_character_overrides.json");
        public string KnowledgeSessionFile => Child("runtime", "knowledge_admin_session.json");
        public string TraceDirectory => Child("logs", "DebugTraces");
        private string Child(params string[] parts)
        {
            if (Error != null) throw new InvalidOperationException(Error);
            string result = Root;
            foreach (string part in parts) result = Path.Combine(result, part);
            return result;
        }

        public static PortablePathSet Open(string root, string applicationDataPath)
        {
            var result = new PortablePathSet();
            if (string.IsNullOrWhiteSpace(root)) { result.Error = "PORTABLE_ROOT_MISSING"; return result; }
            try
            {
                if (!FullyQualified(root)) { result.Error = "PORTABLE_ROOT_RELATIVE"; return result; }
                result.Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string data = Path.GetFullPath(applicationDataPath);
                string player = Path.GetDirectoryName(data);
                // Recognize the supported UnityPlayer/ package layout without inspecting any player data.
                string package = string.Equals(Path.GetFileName(player), "UnityPlayer", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(player) : player;
                if (result.Root.Length <= Path.GetPathRoot(result.Root).Length ||
                    Inside(result.Root, data) || Inside(result.Root, Path.Combine(package, "Backend")) ||
                    string.Equals(result.Root, player, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Root, package, StringComparison.OrdinalIgnoreCase) ||
                    UnsafeAncestors(result.Root))
                { result.Error = "PORTABLE_ROOT_UNSAFE"; return result; }
                Directory.CreateDirectory(result.Root);
                foreach (string directory in new[] { "runtime", "logs" })
                {
                    string child = Path.Combine(result.Root, directory);
                    if (UnsafeAncestors(child)) { result.Error = "PORTABLE_ROOT_UNSAFE"; return result; }
                    Directory.CreateDirectory(child);
                }
                // A unique probe never opens or overwrites an existing player file.
                string probe = Path.Combine(result.Root, ".unity-write-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException ||
                failure is ArgumentException || failure is NotSupportedException || failure is System.Security.SecurityException)
            { result.Error = "PORTABLE_ROOT_UNWRITABLE"; }
            return result;
        }

        public static bool FullyQualified(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            // Mono's IsPathRooted also accepts C:relative. Require a drive separator or a UNC share.
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':') return path[2] == '\\' || path[2] == '/';
            if (!path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("\\\\?", StringComparison.Ordinal) || path.StartsWith("\\\\.", StringComparison.Ordinal)) return false;
            return path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        }

        private static bool Inside(string path, string parent) =>
            string.Equals(path, parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static bool UnsafeAncestors(string path)
        {
            for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                // Reject source trees and their data subdirectories, not just the old SQLite filename.
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets")) && Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings"))) return true;
                if (Directory.Exists(Path.Combine(directory.FullName, "AIBackend", "app"))) return true;
            }
            return false;
        }

        public bool TrySessionPath(string explicitPath, out string file)
        {
            file = null;
            if (Error != null) return false;
            if (explicitPath == null) { file = KnowledgeSessionFile; return true; }
            try
            {
                if (!FullyQualified(explicitPath)) return false;
                string full = Path.GetFullPath(explicitPath);
                if (!Inside(full, Root) || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase) || UnsafeAncestors(full) ||
                    (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)) return false;
                file = full; return true;
            }
            catch (Exception failure) when (failure is ArgumentException || failure is NotSupportedException || failure is IOException || failure is UnauthorizedAccessException)
            { return false; }
        }

        public bool SafeFile(string file) => TrySessionPath(file, out _);
    }
}
