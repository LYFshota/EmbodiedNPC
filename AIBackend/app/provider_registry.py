"""Explicit production Provider registry built only from startup bootstrap values."""

from __future__ import annotations

import os
from collections.abc import Callable, Mapping

from .deepseek_provider import DeepSeekOfficialResponsesProvider
from .model_requests import create_mock_provider
from .providers import ModelProvider, OllamaProvider


def _mock(_: Mapping[str, str]) -> ModelProvider:
    return create_mock_provider()


def _ollama(env: Mapping[str, str]) -> ModelProvider:
    return OllamaProvider(
        base_url=env.get("OLLAMA_BASE_URL", "http://127.0.0.1:11434").rstrip("/"),
        model=env.get("OLLAMA_MODEL", "qwen3.5:4b"),
        timeout_seconds=float(env.get("LLM_TIMEOUT_SECONDS", "30")),
        context_length=int(env.get("OLLAMA_NUM_CTX", "8192")),
    )


def _deepseek(env: Mapping[str, str]) -> ModelProvider:
    key = env.get("DEEPSEEK_API_KEY", "")
    if not key.strip():
        raise ValueError("DEEPSEEK_API_KEY_MISSING")
    try:
        timeout = float(env.get("DEEPSEEK_TIMEOUT_SECONDS", "60"))
        return DeepSeekOfficialResponsesProvider(
            base_url=env.get("DEEPSEEK_BASE_URL", "https://api.deepseek.com"),
            api_key=key,
            model=env.get("DEEPSEEK_MODEL", "deepseek-flash"),
            timeout_seconds=timeout,
        )
    except ValueError:
        raise ValueError("DEEPSEEK_CONFIG_INVALID") from None


PRODUCTION_PROVIDERS: dict[str, Callable[[Mapping[str, str]], ModelProvider]] = {
    "mock": _mock,
    "ollama": _ollama,
    "deepseek_official_responses": _deepseek,
}


def build_model_provider(env: Mapping[str, str] | None = None) -> ModelProvider:
    """Construct one active Provider from environment, never from SavedSettings."""
    bootstrap = os.environ if env is None else env
    provider_id = bootstrap.get("EMBODIEDNPC_PROVIDER", "mock").strip().lower()
    constructor = PRODUCTION_PROVIDERS.get(provider_id)
    if constructor is None:
        raise ValueError("PROVIDER_NOT_SUPPORTED")
    return constructor(bootstrap)
