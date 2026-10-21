using System;
using Newtonsoft.Json.Linq;

namespace EmbodiedNPC.Settings
{
    [Serializable] public sealed class ModelPresetView
    {
        public string preset_id, name, source, protocol, base_url, discovery, schema_profile, thinking_parameter, token_parameter, health_strategy;
        public bool send_temperature;
        public int default_context_length = 32768;
    }
    [Serializable] public sealed class MainProfileView
    {
        public string profile_id, preset_id;
        public MainModelConfig config;
    }
    [Serializable] public sealed class EmbeddingProfileView
    {
        public string profile_id, preset_id;
        public EmbeddingConfig config;
    }
    [Serializable] public sealed class ModelActivationView
    {
        public string main_profile_id, embedding_profile_id;
        public MainModelConfig main_model;
        public EmbeddingConfig embedding;
    }
    [Serializable] public sealed class ModelProfilesView
    {
        public int schema_version, revision;
        public bool configured, migration_required, restart_required;
        public MainProfileView[] main_profiles;
        public EmbeddingProfileView[] embedding_profiles;
        public ModelPresetView[] main_presets, embedding_presets;
        public ModelActivationView selected_to_activate, active_runtime;
        public string default_main_preset_id, default_embedding_preset_id;
        public string[] migration_notes;
        public EmbeddingConfig builtin_embedding_defaults;
        public string builtin_resource_status;
    }
    [Serializable] public sealed class EmbeddingIndexView
    {
        public string status, model, model_key, error_code, provider_status, provider_error_code, index_error_code;
        public int total, completed, pending, in_flight;
        public float progress;
        public bool applied, cloud, consented;
        public bool? control_allowed, counts_known;
    }
    [Serializable] public sealed class ModelContractResult
    {
        public string @case, status, error_code;
        public int latency_ms;
    }
    [Serializable] public sealed class ModelProbeView
    {
        public bool success;
        public string error_code;
        public string[] models;
        public ModelContractResult[] results;
    }

    public static class ModelProfileJson
    {
        public static JObject Main(MainModelConfig c) => new JObject
        {
            ["display_name"] = c.display_name, ["provider_type"] = c.provider_type,
            ["base_url"] = c.base_url, ["model"] = c.model, ["temperature"] = c.temperature,
            ["thinking"] = c.thinking, ["max_output_tokens"] = c.max_output_tokens,
            ["context_length"] = c.context_length > 0 ? JToken.FromObject(c.context_length) : JValue.CreateNull(),
            ["timeout_seconds"] = c.timeout_seconds, ["schema_profile"] = c.schema_profile,
            ["token_parameter"] = c.token_parameter, ["thinking_parameter"] = c.thinking_parameter,
            ["send_temperature"] = c.send_temperature,
            ["top_p"] = c.top_p >= 0 ? JToken.FromObject(c.top_p) : JValue.CreateNull(),
            ["reasoning_effort"] = string.IsNullOrEmpty(c.reasoning_effort) ? JValue.CreateNull() : JToken.FromObject(c.reasoning_effort),
            ["thinking_budget"] = c.thinking_budget > 0 ? JToken.FromObject(c.thinking_budget) : JValue.CreateNull(),
            ["health_strategy"] = c.health_strategy
        };
        public static JObject Embedding(EmbeddingConfig c)
        {
            if (c.provider_type == "builtin") return new JObject
            {
                ["enabled"] = true, ["provider_type"] = c.provider_type, ["reuse_main_api"] = false,
                ["model"] = c.model, ["dimensions"] = c.dimensions > 0 ? JToken.FromObject(c.dimensions) : JValue.CreateNull(),
                ["model_version"] = c.model_version, ["resource_directory"] = c.resource_directory
            };
            return new JObject {
            ["enabled"] = true, ["provider_type"] = c.provider_type, ["reuse_main_api"] = false,
            ["base_url"] = c.base_url, ["model"] = c.model, ["timeout_seconds"] = c.timeout_seconds,
            ["dimensions"] = c.dimensions > 0 ? JToken.FromObject(c.dimensions) : JValue.CreateNull(),
            ["model_version"] = string.IsNullOrEmpty(c.model_version) ? JValue.CreateNull() : JToken.FromObject(c.model_version)
            };
        }
        public static JObject Save(int revision, bool embedding, string id, string preset, JObject config, string key, bool clear)
        {
            bool replace = !clear && !string.IsNullOrWhiteSpace(key);
            if (replace) config["api_key"] = key.Trim();
            return new JObject { ["expected_revision"] = revision, ["kind"] = embedding ? "embedding" : "main",
                ["profile_id"] = id, ["preset_id"] = preset, ["key_action"] = clear ? "clear" : replace ? "replace" : "retain",
                ["config"] = config };
        }
    }
}
