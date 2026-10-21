using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    [Serializable] public sealed class CapacityCountsDto { public int offered, retained, dropped; }
    [Serializable] public sealed class CapacityCheckDto
    {
        public string condition_id, configuration_key, state, cause, proof;
        public int window, input_cap, required_estimate, input_estimate, output_reserve, estimation_reserve;
        public Dictionary<string, CapacityCountsDto> counts;
        public bool provider_verified;
        private static bool Hash(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        public bool IsValid => Hash(condition_id) && Hash(configuration_key) &&
            (state == "ok" || state == "warning" || state == "error") &&
            (cause == "budget" || cause == "provider" || cause == "output_reserve") &&
            window > 0 && window <= 1000000 && input_cap >= 0 && input_cap <= window &&
            required_estimate >= 0 && required_estimate <= 10000000 && input_estimate >= 0 && input_estimate <= 10000000 &&
            output_reserve > 0 && output_reserve <= 1000000 && estimation_reserve >= 0 && estimation_reserve <= 1000000 &&
            (string.IsNullOrEmpty(proof) || Hash(proof)) && counts != null && counts.Count <= 5 && counts.All(p =>
                new[] { "memory", "recent", "knowledge", "task", "relationship" }.Contains(p.Key) && p.Value != null &&
                p.Value.offered >= 0 && p.Value.offered <= 1024 && p.Value.retained >= 0 && p.Value.dropped >= 0 &&
                p.Value.retained + p.Value.dropped <= p.Value.offered);
    }
    [Serializable] public sealed class CapacityStatusDto
    {
        public int version;
        public CapacityCheckDto[] checks;
        public bool IsValid => version == 1 && checks != null && checks.Length <= 2 && checks.All(c => c?.IsValid == true);
        public static bool StrictShape(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return true;
            if (value is not JObject root || root["version"]?.Type != JTokenType.Integer || root["checks"] is not JArray checks) return false;
            foreach (var c in checks.OfType<JObject>())
            {
                foreach (string k in new[] { "window", "input_cap", "required_estimate", "input_estimate", "output_reserve", "estimation_reserve" })
                    if (c[k]?.Type != JTokenType.Integer) return false;
                foreach (string k in new[] { "condition_id", "configuration_key", "state", "cause", "proof" })
                    if (c[k]?.Type != JTokenType.String) return false;
                if (c["provider_verified"]?.Type != JTokenType.Boolean || c["counts"] is not JObject counts) return false;
                foreach (var p in counts.Properties())
                    if (p.Value is not JObject numbers || numbers.Properties().Any(x => x.Value.Type != JTokenType.Integer)) return false;
            }
            return checks.All(c => c is JObject);
        }
    }
    [Serializable] public sealed class CapacityRecheckDto { public string condition_id, proof; public bool resolved; }
    [Serializable] public sealed class CapacityRecheckResult { public int version; public CapacityRecheckDto[] checks; }

    /// <summary>Process lifetime UI state; independent of NPC/dialogue/connection lifetime.</summary>
    public sealed class ContextCapacityState
    {
        private readonly Dictionary<string, CapacityCheckDto> errors = new();
        public IReadOnlyCollection<CapacityCheckDto> Errors => errors.Values;
        public bool WarningPending { get; private set; }
        public int Revision { get; private set; }
        public void Observe(CapacityStatusDto status)
        {
            if (status?.IsValid != true) return;
            foreach (var check in status.checks)
            {
                if (check.state == "error") errors[check.condition_id] = check;
                else if (errors.TryGetValue(check.condition_id, out var old) &&
                         (old.cause != "provider" || check.provider_verified)) errors.Remove(check.condition_id);
                if (check.state == "warning") WarningPending = true;
            }
            Revision++;
        }
        public void Rechecked(CapacityRecheckResult result)
        {
            if (result?.version != 1 || result.checks == null) return;
            foreach (var answer in result.checks)
                if (answer != null && answer.resolved && errors.TryGetValue(answer.condition_id ?? "", out var old) &&
                    !string.IsNullOrEmpty(old.proof) && old.proof == answer.proof) errors.Remove(answer.condition_id);
            Revision++;
        }
    }

    public static class ContextCapacityFeedback
    {
        public static ContextCapacityState Current { get; } = new();
        private const string DismissedKey = "EmbodiedNPC.ContextWarningDismissed";
        public static bool WarningDismissed => PortableDataPaths.IsPortable ? PortablePreferences.Current.ContextWarningDismissed : PlayerPrefs.GetInt(DismissedKey, 0) == 1;
        public static bool DismissWarning()
        {
            if (PortableDataPaths.IsPortable) return PortablePreferences.Current.DismissContextWarning();
            PlayerPrefs.SetInt(DismissedKey, 1); PlayerPrefs.Save(); return true;
        }
        public static string ErrorText => SettingsUiLanguage.T(ErrorMessage.Chinese, ErrorMessage.English);
        public static string WarningText => SettingsUiLanguage.T(WarningMessage.Chinese, WarningMessage.English);
        public static (string Chinese, string English) ErrorMessage => (
            "NPC 上下文容量不足\n本次对话需要的信息超过了当前模型的上下文容量，NPC 暂时无法正常回应。\n请在「设置 → AI 配置 → 高级设置」中适当提高上下文长度，或更换支持更大上下文的模型。",
            "NPC context capacity is insufficient\nThe information needed for this conversation exceeds the model's current context capacity. The NPC cannot respond normally yet.\nIncrease the context length in Settings → AI configuration → Advanced settings, or choose a model with a larger context window.");
        public static (string Chinese, string English) WarningMessage => (
            "NPC 本次获取的信息不完整\n由于当前上下文空间不足，部分对话历史、记忆或世界知识未能提供给 NPC，本次回应可能缺少相关信息。\n如果想获得最佳体验，请在「设置 → AI 配置 → 高级设置」中适当提高上下文长度，或更换支持更大上下文的模型。",
            "The NPC received incomplete information this time\nLimited context space prevented some dialogue history, memories or world knowledge from reaching the NPC. This response may lack relevant information.\nFor the best experience, increase the context length in Settings → AI configuration → Advanced settings, or choose a model with a larger context window.");
    }
}
