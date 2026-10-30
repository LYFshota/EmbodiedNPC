using System;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace EmbodiedNPC.Settings
{
    // One optional preference record keeps name/revision/time in the same atomic write.
    public sealed class PlayerNamePreference
    {
        [JsonProperty("display_name")] public string DisplayName { get; }
        [JsonProperty("revision")] public long Revision { get; }
        [JsonProperty("updated_at_ms")] public long UpdatedAtMs { get; }
        [JsonConstructor]
        public PlayerNamePreference(string display_name, long revision, long updated_at_ms)
        { DisplayName = display_name; Revision = revision; UpdatedAtMs = updated_at_ms; }
        [JsonIgnore] public bool IsValid => PlayerNameSettings.TryValidate(DisplayName, out var normalized) &&
            normalized == DisplayName && Revision >= 1 && UpdatedAtMs >= 0 && UpdatedAtMs <= 253402300799999L;
    }

    /// <summary>Local display profile only. Never selects a player UID or writes social history.</summary>
    public static class PlayerNameSettings
    {
        public const string DefaultName = "Player";
        public const string PreferenceKey = "EmbodiedNPC.Settings.PlayerName";
        public const int MaxCharacters = 24;
        public static event Action Changed;
        public static PlayerNamePreference Current => Read(out _);
        public static string DisplayName => Current.DisplayName;
        public static string Error { get { Read(out var error); return error; } }

        private static string EditorKey => Environment.GetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY") ?? PreferenceKey;

        private static PlayerNamePreference Read(out string error)
        {
            error = null;
            if (PortableDataPaths.IsPortable)
            { error = PortablePreferences.Current.Error; return error == null ? PortablePreferences.Current.PlayerProfile ?? Default : Default; }
            try
            {
                if (!PlayerPrefs.HasKey(EditorKey)) return Default;
                var value = JsonConvert.DeserializeObject<PlayerNamePreference>(PlayerPrefs.GetString(EditorKey));
                if (value?.IsValid == true) return value;
                error = "PLAYER_NAME_SETTINGS_INVALID";
            }
            catch (Exception failure) when (failure is JsonException || failure is PlayerPrefsException)
            { error = "PLAYER_NAME_SETTINGS_UNREADABLE"; }
            return Default;
        }
        private static PlayerNamePreference Default => new(DefaultName, 1, 0);

        // Count Unicode scalar values, rather than UTF-16 halves. Reject invisible control/format data.
        public static bool TryValidate(string input, out string normalized)
        {
            normalized = null;
            if (input == null || input.Length > MaxCharacters * 2) return false;
            int count = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (char.IsLowSurrogate(input[i]) || (char.IsHighSurrogate(input[i]) &&
                    (i + 1 >= input.Length || !char.IsLowSurrogate(input[i + 1])))) return false;
                var category = CharUnicodeInfo.GetUnicodeCategory(input, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) return false;
                if (char.IsHighSurrogate(input[i])) i++;
                count++;
            }
            if (count < 1 || count > MaxCharacters || string.IsNullOrWhiteSpace(input)) return false;
            normalized = input.Trim(); return normalized.Length > 0;
        }
        public static bool TrySave(string input, out string error)
        {
            error = null;
            if (!TryValidate(input, out string name)) { error = "PLAYER_NAME_INVALID"; return false; }
            var old = Read(out error);
            if (error != null) return false;
            if (old.DisplayName == name) return true;
            if (old.Revision == long.MaxValue) { error = "PLAYER_PROFILE_REVISION_LIMIT"; return false; }
            var next = new PlayerNamePreference(name, old.Revision + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (PortableDataPaths.IsPortable)
            {
                if (!PortablePreferences.Current.SetPlayerProfile(next))
                { error = PortablePreferences.Current.Error ?? "PLAYER_NAME_SAVE_FAILED"; return false; }
            }
            else
            {
                bool had = PlayerPrefs.HasKey(EditorKey); string previous = PlayerPrefs.GetString(EditorKey);
                try { PlayerPrefs.SetString(EditorKey, JsonConvert.SerializeObject(next)); PlayerPrefs.Save(); }
                catch (Exception failure) when (failure is PlayerPrefsException || failure is System.IO.IOException || failure is UnauthorizedAccessException)
                {
                    if (had) PlayerPrefs.SetString(EditorKey, previous); else PlayerPrefs.DeleteKey(EditorKey);
                    error = "PLAYER_NAME_SAVE_FAILED"; return false;
                }
            }
            Changed?.Invoke(); return true;
        }
    }
}
