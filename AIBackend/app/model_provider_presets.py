"""Endpoint presets and developer-selected new-profile budgets, not capacity guarantees."""

from dataclasses import dataclass


@dataclass(frozen=True)
class ProviderPreset:
    preset_id: str
    name: str
    source: str
    protocol: str
    base_url: str
    discovery: str = "models"
    schema_profile: str = "raw"
    thinking_parameter: str = "none"
    send_temperature: bool = True
    token_parameter: str = "max_tokens"
    health_strategy: str = "models"
    default_context_length: int = 0

    def __post_init__(self):
        if self.default_context_length == 0:
            object.__setattr__(self, "default_context_length", 32768)


MAIN_PRESETS = (
    ProviderPreset("deepseek", "DeepSeek", "cloud", "deepseek_official_responses", "https://api.deepseek.com", default_context_length=32768),
    ProviderPreset("qwen", "Qwen / Alibaba Cloud", "cloud", "openai_compatible", "https://dashscope.aliyuncs.com/compatible-mode/v1", thinking_parameter="enable_thinking"),
    ProviderPreset("glm", "Zhipu GLM", "cloud", "openai_compatible", "https://open.bigmodel.cn/api/paas/v4", discovery="manual", thinking_parameter="thinking", health_strategy="generation_once"),
    ProviderPreset("kimi", "Kimi / Moonshot", "cloud", "openai_compatible", "https://api.moonshot.cn/v1", thinking_parameter="thinking"),
    ProviderPreset("openai", "OpenAI", "cloud", "openai_compatible", "https://api.openai.com/v1", schema_profile="openai_strict", token_parameter="max_completion_tokens"),
    ProviderPreset("gemini", "Google Gemini", "cloud", "gemini_native", "https://generativelanguage.googleapis.com/v1beta", discovery="gemini", schema_profile="portable_union"),
    ProviderPreset("claude", "Anthropic Claude", "cloud", "anthropic_native", "https://api.anthropic.com/v1", schema_profile="anthropic", send_temperature=False),
    ProviderPreset("openrouter", "OpenRouter", "cloud", "openai_compatible", "https://openrouter.ai/api/v1"),
    ProviderPreset("siliconflow", "SiliconFlow", "cloud", "openai_compatible", "https://api.siliconflow.cn/v1", thinking_parameter="enable_thinking"),
    ProviderPreset("ollama", "Ollama", "local", "ollama", "http://127.0.0.1:11434", discovery="ollama"),
    ProviderPreset("custom", "Custom OpenAI-compatible", "cloud", "openai_compatible", "", health_strategy="generation_once"),
)

EMBEDDING_PRESETS = (
    ProviderPreset("aliyun", "Alibaba Cloud", "cloud", "openai_compatible", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
    ProviderPreset("openai", "OpenAI", "cloud", "openai_compatible", "https://api.openai.com/v1"),
    ProviderPreset("gemini", "Google Gemini", "cloud", "gemini_native", "https://generativelanguage.googleapis.com/v1beta", discovery="gemini"),
    ProviderPreset("zhipu", "Zhipu AI", "cloud", "openai_compatible", "https://open.bigmodel.cn/api/paas/v4", discovery="manual"),
    ProviderPreset("ollama", "Ollama", "local", "ollama", "http://127.0.0.1:11434", discovery="ollama"),
    ProviderPreset("custom", "Custom Embedding API", "cloud", "openai_compatible", ""),
    ProviderPreset("builtin_gemma", "Builtin EmbeddingGemma Q4", "local", "builtin", "", discovery="manual"),
)


def preset_for(kind, preset_id):
    return next((p for p in (MAIN_PRESETS if kind == "main" else EMBEDDING_PRESETS)
                 if p.preset_id == preset_id), None)


def new_main_context_length(values):
    """Only an absent capacity on a new profile receives this default.

    Saved/user-specified capacities are never inferred or migrated. This preset
    default is the developer's requested budget, not a provider capability claim.
    """
    preset = next((p for p in MAIN_PRESETS if p.protocol == values.get("provider_type")), None)
    return preset.default_context_length if preset is not None else 32768


def validate_preset(kind, preset_id, config):
    preset = preset_for(kind, preset_id)
    if preset is None or (config.provider_type != "mock" and config.provider_type != preset.protocol):
        raise ValueError("MODEL_PROFILE_PROTOCOL_MISMATCH")
    if preset.source == "cloud" and preset_id != "custom" and not config.api_key:
        raise ValueError("MODEL_PROFILE_KEY_REQUIRED")
    if kind == "main" and config.thinking and config.provider_type == "openai_compatible" and config.thinking_parameter == "none":
        raise ValueError("MODEL_THINKING_MAPPING_REQUIRED")
