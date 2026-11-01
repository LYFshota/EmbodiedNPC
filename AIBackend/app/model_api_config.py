"""Two machine-local model connection slots for the Demo setup UI."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Literal
from urllib.parse import urlsplit

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


MainProviderType = Literal["mock", "ollama", "deepseek_official_responses", "openai_compatible", "gemini_native", "anthropic_native"]
EmbeddingProviderType = Literal["ollama", "openai_compatible", "gemini_native", "builtin"]


def valid_base_url(value: str) -> str:
    """Check URL shape only; this local Demo does not maintain an endpoint allowlist."""
    try:
        parts = urlsplit(value)
        port = parts.port
    except ValueError:
        raise ValueError("MODEL_CONFIG_URL_INVALID") from None
    if (parts.scheme not in ("http", "https") or not parts.hostname or
            parts.username or parts.password or parts.query or parts.fragment or
            port == 0 or any(ord(char) < 33 for char in value)):
        raise ValueError("MODEL_CONFIG_URL_INVALID")
    return value.rstrip("/")


class MainModelConfig(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    display_name: str = Field(min_length=1, max_length=80)
    provider_type: MainProviderType
    base_url: str | None = None
    api_key: str | None = Field(default=None, max_length=4096, repr=False)
    model: str = Field(min_length=1, max_length=128)
    temperature: float = Field(default=0.0, ge=0, le=2)
    thinking: bool = False
    max_output_tokens: int = Field(default=1024, ge=1, le=32768)
    context_length: int | None = Field(default=None, ge=1, le=1_000_000)
    timeout_seconds: float = Field(default=60.0, gt=0, le=600)
    model_total_concurrency: int | None = Field(default=None, ge=1, le=32)
    model_foreground_concurrency: int | None = Field(default=None, ge=1, le=32)
    model_background_concurrency: int | None = Field(default=None, ge=1, le=32)

    def concurrency_limits(self) -> tuple[int, int, int]:
        local = self.provider_type in ("mock", "ollama")
        total = self.model_total_concurrency or (1 if local else 6)
        foreground = self.model_foreground_concurrency or total
        background = self.model_background_concurrency or min(total, 1 if local else 2)
        return total, foreground, background

    schema_profile: Literal["raw", "portable_union", "openai_strict", "anthropic"] = "raw"
    token_parameter: Literal["max_tokens", "max_completion_tokens"] = "max_tokens"
    thinking_parameter: Literal["none", "enable_thinking", "thinking", "reasoning_effort"] = "none"
    send_temperature: bool = True
    top_p: float | None = Field(default=None, ge=0, le=1)
    reasoning_effort: Literal["none", "minimal", "low", "medium", "high", "xhigh"] | None = None
    thinking_budget: int | None = Field(default=None, ge=1024, le=32768)
    health_strategy: Literal["models", "generation_once"] = "models"

    @field_validator("base_url")
    @classmethod
    def check_base_url(cls, value: str | None) -> str | None:
        return valid_base_url(value) if value is not None else None

    @field_validator("display_name", "model")
    @classmethod
    def trimmed_text(cls, value: str) -> str:
        if value != value.strip() or any(ord(char) < 32 for char in value):
            raise ValueError("MODEL_CONFIG_TEXT_INVALID")
        return value

    @model_validator(mode="after")
    def connection_complete(self) -> "MainModelConfig":
        total, foreground, background = self.concurrency_limits()
        if max(foreground, background) > total:
            raise ValueError("MODEL_CONCURRENCY_CLASS_EXCEEDS_TOTAL")
        if self.provider_type != "mock" and self.base_url is None:
            raise ValueError("MODEL_CONFIG_URL_REQUIRED")
        if self.provider_type == "deepseek_official_responses" and not self.api_key:
            raise ValueError("MODEL_CONFIG_KEY_REQUIRED")
        return self


class EmbeddingConfig(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    enabled: bool = False
    provider_type: EmbeddingProviderType | None = None
    reuse_main_api: bool = False
    base_url: str | None = None
    api_key: str | None = Field(default=None, max_length=4096, repr=False)
    model: str | None = Field(default=None, min_length=1, max_length=128)
    timeout_seconds: float = Field(default=30.0, gt=0, le=600)
    dimensions: int | None = Field(default=None, ge=1, le=65536)
    model_version: str | None = Field(default=None, min_length=1, max_length=128)
    resource_directory: str | None = Field(default=None, min_length=1, max_length=4096)

    @field_validator("base_url")
    @classmethod
    def check_base_url(cls, value: str | None) -> str | None:
        return valid_base_url(value) if value is not None else None

    @field_validator("model")
    @classmethod
    def trimmed_model(cls, value: str | None) -> str | None:
        if value is not None and (value != value.strip() or any(ord(char) < 32 for char in value)):
            raise ValueError("MODEL_CONFIG_TEXT_INVALID")
        return value

    @model_validator(mode="after")
    def connection_complete(self) -> "EmbeddingConfig":
        if self.provider_type == "builtin":
            from pathlib import Path
            from .builtin_embedding_provider import MODEL, REVISION
            if self.reuse_main_api or self.base_url is not None or self.api_key is not None:
                raise ValueError("BUILTIN_EMBEDDING_CONNECTION_INVALID")
            if self.model != MODEL or self.model_version not in (None, REVISION) or self.dimensions not in (None, 768):
                raise ValueError("BUILTIN_EMBEDDING_VERSION_INVALID")
            if (not self.resource_directory or not Path(self.resource_directory).is_absolute() or
                    any(ord(c) < 32 for c in self.resource_directory)):
                raise ValueError("BUILTIN_EMBEDDING_DIRECTORY_INVALID")
        elif self.resource_directory is not None:
            raise ValueError("EMBEDDING_RESOURCE_DIRECTORY_UNSUPPORTED")
        if self.enabled:
            if self.provider_type is None or self.model is None:
                raise ValueError("EMBEDDING_CONFIG_INCOMPLETE")
            if self.provider_type != "builtin" and not self.reuse_main_api and self.base_url is None:
                raise ValueError("EMBEDDING_CONFIG_URL_REQUIRED")
        return self


class ModelApiConfig(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    main_model: MainModelConfig
    embedding: EmbeddingConfig

    @model_validator(mode="after")
    def compatible_reuse(self) -> "ModelApiConfig":
        if self.embedding.enabled and self.embedding.reuse_main_api:
            if self.embedding.provider_type != self.main_model.provider_type or self.embedding.provider_type not in (
                    "ollama", "openai_compatible"):
                raise ValueError("EMBEDDING_REUSE_INCOMPATIBLE")
        return self

    def effective_embedding_connection(self) -> tuple[str | None, str | None, str]:
        """Resolve a configured embedding slot without changing its model ID."""
        embedding = self.embedding
        if not embedding.enabled or embedding.provider_type is None or embedding.model is None:
            raise ValueError("EMBEDDING_NOT_CONFIGURED")
        if embedding.provider_type == "builtin":
            return None, None, embedding.model
        base_url = self.main_model.base_url if embedding.reuse_main_api else embedding.base_url
        api_key = self.main_model.api_key if embedding.reuse_main_api else embedding.api_key
        if base_url is None:
            raise ValueError("EMBEDDING_CONFIG_URL_REQUIRED")
        return base_url, api_key, embedding.model


class ModelApiConfigDocument(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    schema_version: Literal[1] = 1
    revision: int = Field(ge=1)
    updated_at: datetime
    main_model: MainModelConfig
    embedding: EmbeddingConfig

    @field_validator("updated_at")
    @classmethod
    def utc_timestamp(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() != timezone.utc.utcoffset(value):
            raise ValueError("MODEL_CONFIG_TIMESTAMP_INVALID")
        return value

    @model_validator(mode="after")
    def valid_slots(self) -> "ModelApiConfigDocument":
        ModelApiConfig(main_model=self.main_model, embedding=self.embedding)
        return self
