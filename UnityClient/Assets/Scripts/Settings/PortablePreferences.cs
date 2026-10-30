using System;
using System.IO;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Settings
{
    /// <summary>Unity preferences only; Backend settings retain their existing owner.</summary>
    public static class PortablePreferences
    {
        private static PortablePreferenceFile current;
        public static PortablePreferenceFile Current => current ??= new PortablePreferenceFile(PortableDataPaths.Current);
        internal static void Reset() => current = null;
    }

    public sealed class PortablePreferenceFile
    {
        private readonly PortablePathSet paths;
        private bool dirty;
        public int Language { get; private set; }
        public int NpcReplyLanguage { get; private set; }
        public float BgmVolume { get; private set; } = .5f;
        public string DisplayResolution { get; private set; }
        public bool FreeCameraEnabled { get; private set; }
        public bool ContextWarningDismissed { get; private set; }
        public string PlayerAvatar { get; private set; }
        public PlayerNamePreference PlayerProfile { get; private set; }
        public string PlayerAppearanceDescription { get; private set; }
        public string Error { get; private set; }

        public PortablePreferenceFile(PortablePathSet paths)
        {
            this.paths = paths;
            if (paths.Error != null) { Error = paths.Error; return; }
            if (!paths.SafeFile(paths.SettingsFile)) { Error = "PORTABLE_ROOT_UNSAFE"; return; }
            try
            {
                if (!File.Exists(paths.SettingsFile)) { dirty = true; Flush(); return; }
                JObject document;
                using (var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > 65536) throw new FormatException();
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        document = JObject.Parse(reader.ReadToEnd(), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                }
                if (document["schema_version"]?.Type != JTokenType.Integer || (int)document["schema_version"] != 1 ||
                    document["language"]?.Type != JTokenType.Integer || ((int)document["language"] != 0 && (int)document["language"] != 1) ||
                    (document["bgm_volume"]?.Type != JTokenType.Float && document["bgm_volume"]?.Type != JTokenType.Integer)) throw new FormatException();
                float volume = (float)document["bgm_volume"];
                if (float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0 || volume > 1) throw new FormatException();
                // Avatar references are optional. Invalid references fall back independently of other settings.
                if (document["player_avatar"]?.Type == JTokenType.String)
                    PlayerAvatar = (string)document["player_avatar"];
                // Optional manual description belongs only to the selected model; invalid metadata is unknown.
                if (document["player_appearance"] is JObject appearance &&
                    appearance["avatar"]?.Type == JTokenType.String &&
                    (string)appearance["avatar"] == (PlayerAvatar ?? "builtin:default") &&
                    appearance["description"]?.Type == JTokenType.String &&
                    TryValidateAppearanceDescription((string)appearance["description"], out string description))
                    PlayerAppearanceDescription = description;
                if (document["player_profile"] != null)
                {
                    PlayerProfile = document["player_profile"].ToObject<PlayerNamePreference>();
                    if (PlayerProfile?.IsValid != true) throw new FormatException();
                }
                Language = (int)document["language"]; BgmVolume = volume;
                if (document["context_warning_dismissed"] != null)
                {
                    if (document["context_warning_dismissed"].Type != JTokenType.Boolean) throw new FormatException();
                    ContextWarningDismissed = (bool)document["context_warning_dismissed"];
                }
                if (document["npc_reply_language"] != null)
                {
                    if (document["npc_reply_language"].Type != JTokenType.Integer || (int)document["npc_reply_language"] < 0 || (int)document["npc_reply_language"] > 2) throw new FormatException();
                    NpcReplyLanguage = (int)document["npc_reply_language"];
                }
                if (document["free_camera_enabled"] != null)
                {
                    if (document["free_camera_enabled"].Type != JTokenType.Boolean) throw new FormatException();
                    FreeCameraEnabled = (bool)document["free_camera_enabled"];
                }
                if (document["display_resolution"] != null)
                {
                    if(document["display_resolution"].Type!=JTokenType.String || !DisplaySettings.IsPreset((string)document["display_resolution"])) throw new FormatException();
                    DisplayResolution=(string)document["display_resolution"];
                }
            }
            catch (Exception failure) when (failure is JsonException || failure is FormatException || failure is OverflowException || failure is ArgumentException)
            { Error = "PORTABLE_SETTINGS_INVALID"; }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is System.Security.SecurityException)
            { Error = "PORTABLE_SETTINGS_UNREADABLE"; }
        }

        public bool SetPlayerProfile(PlayerNamePreference value)
        {
            if (Error != null || value?.IsValid != true) return false;
            var previous = PlayerProfile; PlayerProfile = value; dirty = true;
            if (Flush()) return true;
            PlayerProfile = previous; return false;
        }
        public bool SetPlayerAvatar(string relativePath)
        {
            if (Error != null) return false;
            string previous = PlayerAvatar, description = PlayerAppearanceDescription;
            if (PlayerAvatar != relativePath) PlayerAppearanceDescription = null;
            PlayerAvatar = relativePath; dirty = true;
            if (Flush()) return true;
            PlayerAvatar = previous; PlayerAppearanceDescription = description; return false;
        }
        public static bool TryValidateAppearanceDescription(string value, out string description)
        {
            description = (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim(); int count = 0;
            for (int i = 0; i < description.Length; i++)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(description, i);
                if ((category == UnicodeCategory.Control && description[i] != '\n') || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) return false;
                if (char.IsSurrogate(description[i]))
                { if (!char.IsHighSurrogate(description[i]) || i + 1 >= description.Length || !char.IsLowSurrogate(description[i + 1])) return false; i++; }
                if (++count > 160) return false;
            }
            if (description.Length == 0) description = null;
            return true;
        }
        public bool SetPlayerAppearanceDescription(string value)
        {
            if (Error != null || !TryValidateAppearanceDescription(value, out string description)) return false;
            string previous = PlayerAppearanceDescription; PlayerAppearanceDescription = description; dirty = true;
            if (Flush()) return true;
            PlayerAppearanceDescription = previous; return false;
        }
        public void SetLanguage(bool english)
        { if (Error != null) return; Language = english ? 1 : 0; dirty = true; Flush(); }
        public bool SetNpcReplyLanguage(int value)
        {
            if (Error != null || value < 0 || value > 2) return false;
            int previous = NpcReplyLanguage; NpcReplyLanguage = value; dirty = true;
            if (Flush()) return true;
            NpcReplyLanguage = previous; return false;
        }
        public void SetBgmVolume(float volume)
        { if (Error != null) return; BgmVolume = volume; dirty = true; }
        public bool SetDisplayResolution(string value)
        {
            if(Error!=null || !DisplaySettings.IsPreset(value))return false;
            DisplayResolution=value;dirty=true;return Flush();
        }
        public bool SetFreeCameraEnabled(bool enabled)
        {
            if (Error != null) return false;
            bool previous = FreeCameraEnabled;
            FreeCameraEnabled = enabled; dirty = true;
            if (Flush()) return true;
            FreeCameraEnabled = previous; return false;
        }
        public bool DismissContextWarning()
        {
            if (Error != null) return false;
            ContextWarningDismissed = true; dirty = true;
            if (Flush()) return true;
            ContextWarningDismissed = false; return false;
        }
        public bool Flush()
        {
            if (Error != null) return false;
            if (!dirty) return true;
            if (!paths.SafeFile(paths.SettingsFile)) { Error = "PORTABLE_ROOT_UNSAFE"; return false; }
            string temporary = paths.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var document = new JObject { ["schema_version"] = 1, ["language"] = Language, ["bgm_volume"] = BgmVolume };
                if (PlayerAvatar != null) document["player_avatar"] = PlayerAvatar;
                if (PlayerProfile != null) document["player_profile"] = JObject.FromObject(PlayerProfile);
                if (PlayerAppearanceDescription != null) document["player_appearance"] = new JObject {
                    ["avatar"] = PlayerAvatar ?? "builtin:default", ["description"] = PlayerAppearanceDescription };
                document["free_camera_enabled"] = FreeCameraEnabled;
                document["npc_reply_language"] = NpcReplyLanguage;
                document["context_warning_dismissed"] = ContextWarningDismissed;
                if(DisplayResolution!=null)document["display_resolution"]=DisplayResolution;
                byte[] data = new UTF8Encoding(false).GetBytes(document.ToString(Formatting.Indented));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(data, 0, data.Length); stream.Flush(true); }
                if (File.Exists(paths.SettingsFile)) File.Replace(temporary, paths.SettingsFile, null);
                else File.Move(temporary, paths.SettingsFile);
                dirty = false; return true;
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is System.Security.SecurityException)
            { Error = "PORTABLE_SETTINGS_SAVE_FAILED"; return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
                { Error = "PORTABLE_SETTINGS_SAVE_FAILED"; }
            }
        }
    }
}
