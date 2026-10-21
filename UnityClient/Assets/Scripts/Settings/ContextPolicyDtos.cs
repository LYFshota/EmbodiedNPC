using System;
using System.Globalization;

namespace EmbodiedNPC.Settings
{
    [Serializable] public sealed class ContextPolicyConfig
    {
        public string mode = "auto";
        public int? target_messages;
        public bool IsValid => mode == "auto" ? target_messages == null :
            mode == "manual" && target_messages >= 1 && target_messages <= 64;
        public bool Matches(ContextPolicyConfig other) => other != null && mode == other.mode && target_messages == other.target_messages;
        public static bool TryDraft(int mode, string text, out ContextPolicyConfig config)
        {
            config = null;
            if (mode == 0) { config = new ContextPolicyConfig(); return true; }
            if (mode != 1 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 64) return false;
            config = new ContextPolicyConfig { mode = "manual", target_messages = n }; return true;
        }
    }
    [Serializable] public sealed class ContextPolicyDocument
    {
        public int schema_version, revision;
        public ContextPolicyConfig config;
        public bool IsValid => schema_version == 1 && revision >= 0 && config?.IsValid == true;
    }
    [Serializable] public sealed class ContextPolicyView
    {
        public ContextPolicyDocument saved, applied;
        public string source, error, automatic_rule;
        public bool hot_apply, saved_matches_applied;
        public bool IsValid => applied?.IsValid == true && hot_apply;
        public bool Confirms(ContextPolicySaveRequest request) => IsValid && error == null && saved?.IsValid == true &&
            saved_matches_applied && saved.revision == request.expected_revision + 1 && applied.revision == saved.revision &&
            saved.config.Matches(request.config) && applied.config.Matches(request.config);
    }
    [Serializable] public sealed class ContextPolicySaveRequest
    {
        public int expected_revision;
        public ContextPolicyConfig config;
    }
}
