using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EmbodiedNPC.Settings;

namespace CafeDemo
{
    /// <summary>Bounded UTF-8 transfer files for the existing Knowledge page.</summary>
    public static class CafeKnowledgeFiles
    {
        public const int MaxBytes = 16 * 1024 * 1024;
        public static string Choose(bool save)
        {
            string title = SettingsUiLanguage.T(save ? "导出世界知识" : "导入世界知识", save ? "Export world knowledge" : "Import world knowledge");
#if UNITY_EDITOR
            return save ? UnityEditor.EditorUtility.SaveFilePanel(title, "", "world_knowledge", "json")
                        : UnityEditor.EditorUtility.OpenFilePanel(title, "", "json");
#elif UNITY_STANDALONE_WIN
            const int capacity = 32768;
            IntPtr buffer = Marshal.AllocHGlobal(capacity * 2);
            try
            {
                Marshal.Copy(new byte[capacity * 2], 0, buffer, capacity * 2);
                if (save)
                {
                    char[] initial = "world_knowledge.json\0".ToCharArray();
                    Marshal.Copy(initial, 0, buffer, initial.Length);
                }
                var value = new OpenFileName { file = buffer, maxFile = capacity,
                    filter = "JSON (*.json)\0*.json\0\0", title = title, defaultExtension = "json",
                    flags = 0x80000 | 0x800 | 0x8 | (save ? 0x2 : 0x1000) };
                value.size = Marshal.SizeOf(typeof(OpenFileName));
                return (save ? GetSaveFileName(ref value) : GetOpenFileName(ref value)) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
#else
            throw new PlatformNotSupportedException("Knowledge file dialogs require the Editor or Windows Player.");
#endif
        }

        public static string Read(string path)
        {
            var buffer = new byte[MaxBytes + 1];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int count = 0, read;
            while (count < buffer.Length && (read = file.Read(buffer, count, buffer.Length - count)) > 0) count += read;
            if (count > MaxBytes) throw new IOException("JSON exceeds 16 MiB.");
            // Preserve raw JSON, including duplicate keys, for authoritative Backend validation.
            string result = new UTF8Encoding(false, true).GetString(buffer, 0, count);
            return result.Length > 0 && result[0] == '\uFEFF' ? result.Substring(1) : result;
        }

        public static void Write(string path, string json)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > MaxBytes) throw new IOException("JSON exceeds 16 MiB.");
            string target = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".knowledge-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int size; public IntPtr owner, instance;
            public string filter, customFilter; public int maxCustomFilter, filterIndex;
            public IntPtr file; public int maxFile;
            public string fileTitle; public int maxFileTitle;
            public string initialDirectory, title; public int flags;
            public short fileOffset, fileExtension; public string defaultExtension;
            public IntPtr customData, hook; public string template;
            public IntPtr reserved; public int reservedSize, flagsEx;
        }
        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(ref OpenFileName value);
        [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSaveFileName(ref OpenFileName value);
#endif
    }
}
