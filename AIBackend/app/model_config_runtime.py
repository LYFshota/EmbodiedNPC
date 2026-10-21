"""Startup construction and effective comparison for the local model config."""

from __future__ import annotations

from .canonical_embeddings import OllamaEmbeddingProvider, OpenAICompatibleEmbeddingProvider, GeminiEmbeddingProvider
from .deepseek_provider import DeepSeekOfficialResponsesProvider
from .model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig, ModelApiConfigDocument
from .model_requests import create_mock_provider
from .openai_compatible_provider import OpenAICompatibleProvider
from .providers import InferenceOptions, ModelProvider, OllamaProvider
from .model_profile_config import has_activation
from .native_model_providers import NativeTextProvider
from .portable_resources import resolve_embedding_resources


def build_provider_from_model_config(document: ModelApiConfigDocument) -> ModelProvider:
    return build_main_provider(document.main_model)


def build_main_provider(main: MainModelConfig, transport=None) -> ModelProvider:
    if main.provider_type == "mock":
        return create_mock_provider()
    if main.provider_type == "ollama":
        return OllamaProvider(base_url=main.base_url or "", model=main.model,
                              timeout_seconds=main.timeout_seconds,
                              context_length=main.context_length or 8192, transport=transport)
    if main.provider_type == "deepseek_official_responses":
        return DeepSeekOfficialResponsesProvider(
            base_url=main.base_url or "", api_key=main.api_key or "",
            model=main.model, timeout_seconds=main.timeout_seconds, transport=transport)
    if main.provider_type in ("gemini_native", "anthropic_native"):
        return NativeTextProvider(config=main, transport=transport)
    return OpenAICompatibleProvider(
        base_url=main.base_url or "", api_key=main.api_key, model=main.model,
        timeout_seconds=main.timeout_seconds, schema_profile=main.schema_profile,
        token_parameter=main.token_parameter, thinking_parameter=main.thinking_parameter,
        send_temperature=main.send_temperature, top_p=main.top_p, reasoning_effort=main.reasoning_effort,
        health_strategy=main.health_strategy, transport=transport)


def build_embedding_from_model_config(document: ModelApiConfigDocument):
    config = ModelApiConfig(main_model=document.main_model, embedding=document.embedding)
    if not config.embedding.enabled:
        return None
    base_url, api_key, model = config.effective_embedding_connection()
    resolved = EmbeddingConfig.model_validate({**config.embedding.model_dump(), "base_url": base_url,
        "api_key": api_key, "model": model, "reuse_main_api": False})
    return build_embedding_provider(resolved, identity_v2=getattr(document, "embedding_identity_version", 1) == 2)


def build_embedding_provider(embedding, transport=None, *, identity_v2=True):
    embedding = resolve_embedding_resources(embedding)
    if embedding.provider_type == "builtin":
        from .builtin_embedding_provider import BuiltinEmbeddingProvider
        return BuiltinEmbeddingProvider(embedding.resource_directory, model=embedding.model,
            model_version=embedding.model_version, dimensions=embedding.dimensions)
    cls = {"ollama": OllamaEmbeddingProvider, "openai_compatible": OpenAICompatibleEmbeddingProvider,
           "gemini_native": GeminiEmbeddingProvider}[embedding.provider_type]
    values = dict(base_url=embedding.base_url, model=embedding.model, timeout_seconds=embedding.timeout_seconds,
                  transport=transport, dimensions=embedding.dimensions, model_version=embedding.model_version,
                  identity_v2=identity_v2)
    if embedding.provider_type != "ollama":
        values["api_key"] = embedding.api_key
    return cls(**values)


def cognition_options(document: ModelApiConfigDocument | None) -> InferenceOptions | None:
    if not has_activation(document):
        return None
    main = document.main_model
    return InferenceOptions(temperature=main.temperature, think=main.thinking,
                            max_output_tokens=main.max_output_tokens)


def effective_runtime_config(document: ModelApiConfigDocument) -> tuple:
    """Compare invocation-affecting values, without exposing keys in status output."""
    if not has_activation(document):
        return ()
    main = document.main_model
    generation = (main.temperature, main.thinking, main.max_output_tokens, main.schema_profile,
                  main.token_parameter, main.thinking_parameter, main.send_temperature,
                  main.top_p, main.reasoning_effort, main.thinking_budget, main.health_strategy)
    if main.provider_type == "mock":
        # Mock still uses this startup snapshot for cognition budgeting.
        main_effective = ("mock", main.context_length or 8192)
    elif main.provider_type == "ollama":
        main_effective = ("ollama", main.base_url, main.model, main.timeout_seconds,
                          main.context_length or 8192)
    else:
        main_effective = (main.provider_type, main.base_url, main.api_key,
                          main.model, main.timeout_seconds, main.context_length or 8192)

    config = ModelApiConfig(main_model=main, embedding=document.embedding)
    if not config.embedding.enabled:
        embedding_effective = (False,)
    else:
        base_url, api_key, model = config.effective_embedding_connection()
        embedding_effective = (True, config.embedding.provider_type, base_url,
                               api_key if config.embedding.provider_type != "ollama" else None,
                               model, config.embedding.timeout_seconds, config.embedding.dimensions,
                               config.embedding.model_version, getattr(document, "embedding_identity_version", 1))
        if config.embedding.provider_type == "builtin":
            embedding_effective += (resolve_embedding_resources(config.embedding).resource_directory,)
    return main_effective, generation, embedding_effective


def restart_required(active: ModelApiConfigDocument | None,
                     saved: ModelApiConfigDocument | None) -> bool:
    if not has_activation(active) or not has_activation(saved):
        return has_activation(active) != has_activation(saved)
    return effective_runtime_config(active) != effective_runtime_config(saved)
