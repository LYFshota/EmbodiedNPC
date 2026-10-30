using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using EmbodiedNPC.Settings;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Local avatar files only. Never owns identity or any Backend data.</summary>
    public sealed class PlayerAvatarStore
    {
        public const long MaxFileBytes = 64L * 1024 * 1024;
        private readonly PortablePathSet paths;
        private readonly PortablePreferenceFile preferences;
        public string DirectoryPath => Path.Combine(paths.Root, "Avatars");
        public string Selected => preferences.PlayerAvatar;
        public string AppearanceDescription => preferences.PlayerAppearanceDescription;
        public bool SetAppearanceDescription(string value) => preferences.SetPlayerAppearanceDescription(value);

        public PlayerAvatarStore(PortablePathSet paths, PortablePreferenceFile preferences)
        { this.paths = paths; this.preferences = preferences; }

        public static PlayerAvatarStore OpenDefault()
        {
            if (PortableDataPaths.IsPortable)
                return new PlayerAvatarStore(PortableDataPaths.Current, PortablePreferences.Current);
            // Editor use is explicitly isolated from Player UserData and PlayerPrefs.
            var isolated = PortablePathSet.Open(Environment.GetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT") ?? Path.Combine(Path.GetTempPath(), "EmbodiedNPC-AvatarEditor"), Application.dataPath);
            return new PlayerAvatarStore(isolated, new PortablePreferenceFile(isolated));
        }
        private void Check(string path)
        {
            if (paths.Error != null || preferences.Error != null || !paths.SafeFile(path))
                throw new IOException("UserData / Avatars is unavailable or unsafe.");
        }
        public void EnsureDirectory()
        { Check(DirectoryPath); Directory.CreateDirectory(DirectoryPath); }
        public string[] Scan()
        {
            EnsureDirectory();
            return Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), ".vrm", StringComparison.OrdinalIgnoreCase) && paths.SafeFile(p))
                .Select(Path.GetFileName).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        }
        public string ScannedPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                !string.Equals(Path.GetExtension(name), ".vrm", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a VRM from the avatar directory.");
            string path = Path.Combine(DirectoryPath, name); Check(path); return path;
        }
        public string ResolveSelection(string relative)
        {
            const string prefix = "Avatars/Imported/";
            if (relative == null || !relative.StartsWith(prefix, StringComparison.Ordinal) ||
                relative.Length != prefix.Length + 64 + 4 || !relative.EndsWith(".vrm", StringComparison.Ordinal) ||
                relative.Substring(prefix.Length, 64).Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                throw new IOException("The saved avatar reference is invalid.");
            string path = Path.Combine(paths.Root, relative.Replace('/', Path.DirectorySeparatorChar)); Check(path); return path;
        }
        public static byte[] ReadBounded(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Only .vrm files are supported.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 20 || stream.Length > MaxFileBytes) throw new IOException("VRM file must be between 20 bytes and 64 MiB.");
            byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
            while (offset < bytes.Length)
            { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return bytes;
        }
        public static string ContentReference(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return "Avatars/Imported/" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() + ".vrm";
        }
        public string Preserve(byte[] bytes)
        {
            using var sha = SHA256.Create();
            string relative = ContentReference(bytes);
            string destination = ResolveSelection(relative); Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination))
            {
                if (!sha.ComputeHash(ReadBounded(destination)).SequenceEqual(sha.ComputeHash(bytes)))
                    throw new IOException("The stored avatar is damaged. Remove it from Avatars/Imported and import again.");
                return relative;
            }
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return relative;
        }
        public bool Select(string relative)
        {
            if (relative != null) ResolveSelection(relative);
            return preferences.SetPlayerAvatar(relative);
        }
        // Confirmed files are retained for reuse. No user model is ever deleted by switching/resetting.
    }
}
