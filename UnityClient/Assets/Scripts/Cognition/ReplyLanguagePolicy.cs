using System;
using System.Text.RegularExpressions;

namespace EmbodiedNPC.Cognition
{
    /// <summary>Presentation only. One instance per NPC composer, no process-global last language.</summary>
    public sealed class ReplyLanguageSession
    {
        private string sessionKey, sessionLanguage;
        // Only standalone directives are recognized. Quoted examples, negations and arbitrary
        // prose are deliberately not classified. No inferred preference is ever persisted.
        private static readonly Regex Chinese = new(
            @"^(?<session>接下来|以后|从现在开始)?(?:请)?(?:这句话|这一轮|本轮)?(?:请)?用(?<language>中文|英文|英语)(?:回答(?:我)?|回复(?:我)?|交流|聊天)[。！!\.\s]*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex English = new(
            @"^(?:(?<session>from now on|for this conversation)[, ]+)?(?:please )?(?:answer|reply|respond|speak|talk)(?: to me)? (?:in )?(?<language>Chinese|English)(?: (?:for this turn|this time|only))?[.!\s]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Resolve(string key, string playerText, string defaultLanguage)
        {
            if (string.IsNullOrEmpty(key)) return defaultLanguage;
            if (!string.Equals(sessionKey, key, StringComparison.Ordinal))
            { sessionKey = key; sessionLanguage = null; }
            if (playerText != null)
            {
                string text = playerText.Trim();
                Match match = Chinese.Match(text);
                if (!match.Success) match = English.Match(text);
                if (match.Success)
                {
                    string label = match.Groups["language"].Value;
                    string language = label == "中文" || label.Equals("Chinese", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
                    if (match.Groups["session"].Success) sessionLanguage = language;
                    return language;
                }
            }
            return sessionLanguage ?? defaultLanguage;
        }
    }

    public static class ReplyUtteranceContract
    {
        public static int HardLimit(string language) => language == "en-US" ? 250 : 150;
        public static bool IsLanguage(string value) => value == "zh-CN" || value == "en-US";
        // Unicode scalar values, matching Python len() on valid Unicode, not UTF-16 units
        // or visual grapheme clusters. Isolated surrogates are invalid at both ends.
        public static int ScalarCount(string value)
        {
            if (value == null) return 0;
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
            {
                if (char.IsHighSurrogate(value[i]))
                { if (i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])) return -1; }
                else if (char.IsLowSurrogate(value[i])) return -1;
            }
            return count;
        }
        public static bool Fits(string value, string language)
        { int count = ScalarCount(value); return count >= 0 && count <= HardLimit(language); }
    }
}
