"""Saved Runtime Settings schema. P1 does not apply these values to the runtime."""

from __future__ import annotations

import re
from datetime import datetime, timezone
from typing import Literal
from urllib.parse import urlsplit

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


_ID = re.compile(r"^[a-z][a-z0-9_-]{0,63}$")
_PROVIDER_TYPE = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
_ENV_NAME = re.compile(r"^[A-Z][A-Z0-9_]{0,127}$")
_SUSPICIOUS_VALUE = re.compile(r"(?i)(bearer\s+|sk-[a-z0-9]{8,}|api[_-]?key\s*[=:]|token\s*[=:])")


class GenerationSettings(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    temperature: float = Field(default=0.0, ge=0, le=2)
    thinking: bool = False
    max_output_tokens: int = Field(default=512, ge=1, le=32768)


class ProviderPreset(BaseModel):
    """Saved configuration, not a production provider registry entry."""

    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    preset_id: str = Field(min_length=1, max_length=64)
    display_name: str = Field(min_length=1, max_length=80)
    provider_type: str = Field(min_length=1, max_length=64)
    model: str = Field(min_length=1, max_length=128)
    base_url: str | None = None
    credential_reference: str | None = None
    generation: GenerationSettings = Field(default_factory=GenerationSettings)
    context_length: int | None = Field(default=None, ge=1, le=1_000_000)
    timeout_seconds: float | None = Field(default=None, gt=0, le=600)

    @field_validator("preset_id")
    @classmethod
    def valid_id(cls, value: str) -> str:
        if not _ID.fullmatch(value):
            raise ValueError("INVALID_PRESET_ID")
        return value

    @field_validator("provider_type")
    @classmethod
    def valid_provider_type(cls, value: str) -> str:
        if not _PROVIDER_TYPE.fullmatch(value):
            raise ValueError("INVALID_PROVIDER_TYPE")
        return value

    @field_validator("display_name", "model")
    @classmethod
    def no_secret_or_controls(cls, value: str) -> str:
        if value != value.strip() or any(ord(char) < 32 for char in value) or _SUSPICIOUS_VALUE.search(value):
            raise ValueError("UNSAFE_PRESET_TEXT")
        return value

    @field_validator("credential_reference")
    @classmethod
    def valid_credential_reference(cls, value: str | None) -> str | None:
        if value is not None and not _ENV_NAME.fullmatch(value):
            raise ValueError("INVALID_CREDENTIAL_REFERENCE")
        return value

    @field_validator("base_url")
    @classmethod
    def safe_base_url(cls, value: str | None) -> str | None:
        if value is None:
            return None
        parts = urlsplit(value)
        if (parts.scheme not in ("http", "https") or not parts.hostname or
                parts.username or parts.password or parts.query or parts.fragment or
                _SUSPICIOUS_VALUE.search(value)):
            raise ValueError("UNSAFE_PROVIDER_URL")
        return value


class RuntimeSettings(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    provider_presets: tuple[ProviderPreset, ...]
    selected_preset_id: str

    @model_validator(mode="after")
    def valid_selection(self) -> "RuntimeSettings":
        ids = [preset.preset_id for preset in self.provider_presets]
        if not ids or len(ids) != len(set(ids)):
            raise ValueError("DUPLICATE_OR_EMPTY_PRESETS")
        if self.selected_preset_id not in ids:
            raise ValueError("UNKNOWN_SELECTED_PRESET")
        return self

    def diagnostic(self) -> dict[str, object]:
        """Explicit allowlist. Never read a credential or return its reference name."""
        selected = next(p for p in self.provider_presets if p.preset_id == self.selected_preset_id)
        return {
            "selected_preset_id": selected.preset_id,
            "provider_type": selected.provider_type,
            "model": selected.model,
            "generation": selected.generation.model_dump(),
            "context_length": selected.context_length,
            "timeout_seconds": selected.timeout_seconds,
            "credential_source": "environment" if selected.credential_reference else "none",
        }


class RuntimeSettingsDocument(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)

    schema_version: Literal[1] = 1
    revision: int = Field(ge=0)
    updated_at: datetime
    settings: RuntimeSettings

    @field_validator("updated_at")
    @classmethod
    def utc_timestamp(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() != timezone.utc.utcoffset(value):
            raise ValueError("UPDATED_AT_MUST_BE_UTC")
        return value

    def diagnostic(self) -> dict[str, object]:
        return {"schema_version": self.schema_version, "settings_revision": self.revision,
                **self.settings.diagnostic()}


def code_defaults() -> RuntimeSettings:
    return RuntimeSettings(
        provider_presets=(
            ProviderPreset(preset_id="mock", display_name="Mock", provider_type="mock",
                           model="fixed-greeting-v1"),
            ProviderPreset(preset_id="local-qwen", display_name="Local Qwen",
                           provider_type="ollama", model="qwen3.5:4b",
                           base_url="http://127.0.0.1:11434", context_length=8192,
                           timeout_seconds=30),
        ),
        selected_preset_id="mock",
    )


def default_document() -> RuntimeSettingsDocument:
    return RuntimeSettingsDocument(revision=0, updated_at=datetime.now(timezone.utc),
                                   settings=code_defaults())
