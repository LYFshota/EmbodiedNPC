using System;

namespace EmbodiedNPC.Settings
{
    [Serializable]
    public sealed class MainModelConfig
    {
        public string display_name = "Main LLM";
        public string provider_type = "openai_compatible";
        public string base_url = "";
        public string api_key = "";
        public string model = "";
        public float temperature;
        public bool thinking;
        public int max_output_tokens = 1024;
        public int context_length = 32768;
        public float timeout_seconds = 60;
        public int model_total_concurrency;
        public int model_foreground_concurrency;
        public int model_background_concurrency;
        public string schema_profile = "raw";
        public string token_parameter = "max_tokens";
        public string thinking_parameter = "none";
        public bool send_temperature = true;
        public float top_p = -1;
        public string reasoning_effort;
        public int thinking_budget;
        public bool key_present;
        public string health_strategy = "models";
    }

    [Serializable]
    public sealed class EmbeddingConfig
    {
        public bool enabled = true;
        public string provider_type = "openai_compatible";
        public bool reuse_main_api = true;
        public string base_url;
        public string api_key;
        public string model = "";
        public float timeout_seconds = 30;
        public int dimensions;
        public string model_version;
        public string resource_directory;
        public string resource_status;
        public bool key_present;
    }

    [Serializable]
    public sealed class ModelConfigDocument
    {
        public MainModelConfig main_model;
        public EmbeddingConfig embedding;
    }

    [Serializable]
    public sealed class ModelConfigView
    {
        public bool configured;
        public bool saved_configured;
        public int revision;
        public MainModelConfig main_model;
        public EmbeddingConfig embedding;
        public ModelProfilesView profiles;
    }

    [Serializable]
    public sealed class ModelConfigSaveRequest
    {
        public int expected_revision;
        public ModelConfigDocument config;
    }

    [Serializable]
    public sealed class ReusedEmbeddingSavePayload
    {
        public bool enabled = true;
        public string provider_type;
        public bool reuse_main_api = true;
        public string model;
        public float timeout_seconds;
    }

    [Serializable]
    public sealed class DiscoverModelsRequest
    {
        public string provider_type;
        public string base_url;
        public string api_key;
        public float timeout_seconds = 15;
    }

    [Serializable]
    public sealed class DiscoverModelsResponse
    {
        public bool success;
        public string[] models;
        public string error_code;
    }

    [Serializable]
    public sealed class MainTestConnectionRequest
    {
        public string kind = "main";
        public MainModelConfig main_model;
    }

    [Serializable]
    public sealed class TestConnectionRequest
    {
        public string kind;
        public MainModelConfig main_model;
        public EmbeddingConfig embedding;
    }

    [Serializable]
    public sealed class TestConnectionResponse
    {
        public bool success;
        public string error_code;
    }

    [Serializable]
    public sealed class ActiveProviderStatus
    {
        public string provider;
        public string model;
        public bool available;
    }

    [Serializable]
    public sealed class ActiveMainModelStatus
    {
        public string provider;
        public string model;
    }

    [Serializable]
    public sealed class ActiveEmbeddingStatus
    {
        public bool enabled;
        public string provider;
        public string model;
    }

    [Serializable]
    public sealed class RuntimeStatusView
    {
        public string status;
        public bool restart_required;
        public ModelConfigStatus model_config;
        public ActiveProviderStatus active_provider;
        public ActiveMainModelStatus active_main_model;
        public ActiveEmbeddingStatus active_embedding;
    }

    [Serializable]
    public sealed class ModelConfigStatus
    {
        public bool configured;
        public int revision;
    }

    [Serializable]
    public sealed class ModelConfigError
    {
        public string code;
    }

    [Serializable]
    public sealed class ModelConfigErrorEnvelope
    {
        public ModelConfigError error;
    }
}
