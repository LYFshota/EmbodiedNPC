using System;
using UnityEngine;

namespace EmbodiedNPC.Settings
{
    public enum NpcReplyLanguagePreference { FollowInterface = 0, Chinese = 1, English = 2 }

    /// <summary>Presentation preference; independent of Provider configuration and session overrides.</summary>
    public static class NpcReplyLanguageSettings
    {
        public const string PreferenceKey = "EmbodiedNPC.Settings.NpcReplyLanguage";
        public static event Action Changed;
        public static NpcReplyLanguagePreference Preference => (NpcReplyLanguagePreference)Mathf.Clamp(
            PortableDataPaths.IsPortable ? PortablePreferences.Current.NpcReplyLanguage : PlayerPrefs.GetInt(PreferenceKey, 0), 0, 2);
        public static string DefaultLanguage => Resolve(Preference, SettingsUiLanguage.IsEnglish);
        public static string Resolve(NpcReplyLanguagePreference preference, bool englishUi) =>
            preference == NpcReplyLanguagePreference.English ||
            preference == NpcReplyLanguagePreference.FollowInterface && englishUi ? "en-US" : "zh-CN";

        public static bool SetPreference(NpcReplyLanguagePreference value)
        {
            if ((int)value < 0 || (int)value > 2) return false;
            if (Preference == value) return true;
            if (PortableDataPaths.IsPortable)
            { if (!PortablePreferences.Current.SetNpcReplyLanguage((int)value)) return false; }
            else { PlayerPrefs.SetInt(PreferenceKey, (int)value); PlayerPrefs.Save(); }
            Changed?.Invoke(); return true;
        }
    }
}
