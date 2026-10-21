"""Legacy Settings API and safe status of the startup model configuration."""

from __future__ import annotations

import json
from collections.abc import Callable
from datetime import datetime
from typing import Literal

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .canonical_embeddings import OllamaEmbeddingProvider, OpenAICompatibleEmbeddingProvider, GeminiEmbeddingProvider
from .providers import ModelProvider
from .model_api_config import ModelApiConfigDocument
from .model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from .model_config_runtime import restart_required as model_restart_required
from .model_profile_config import has_activation
from .runtime_settings import GenerationSettings, ProviderPreset, RuntimeSettings, RuntimeSettingsDocument
from .runtime_settings_store import (
    RuntimeSettingsStore, SettingsInvalidDocument, SettingsRevisionConflict,
    SettingsStorageError,
)


_MAX_REQUEST_BYTES = 256 * 1024
_SAFE_HEALTH_DETAILS = frozenset({
    "", "OLLAMA_MODEL_NOT_FOUND", "OLLAMA_PROTOCOL_ERROR", "OLLAMA_UNAVAILABLE",
    "DEEPSEEK_MODEL_NOT_FOUND", "DEEPSEEK_AUTH_FAILED", "DEEPSEEK_RATE_LIMITED",
    "DEEPSEEK_UNAVAILABLE", "DEEPSEEK_PROTOCOL_ERROR", "DEEPSEEK_BAD_REQUEST",
    "OPENAI_COMPATIBLE_UNAVAILABLE", "OPENAI_COMPATIBLE_AUTH_FAILED",
    "OPENAI_COMPATIBLE_MODEL_NOT_FOUND", "OPENAI_COMPATIBLE_PROTOCOL_ERROR",
    "OPENAI_COMPATIBLE_HEALTH_PROBE_FAILED", "GEMINI_MODEL_NOT_FOUND", "GEMINI_AUTH_FAILED",
    "GEMINI_RATE_LIMITED", "GEMINI_UNAVAILABLE", "GEMINI_PROTOCOL_ERROR",
    "ANTHROPIC_MODEL_NOT_FOUND", "ANTHROPIC_AUTH_FAILED", "ANTHROPIC_RATE_LIMITED",
    "ANTHROPIC_UNAVAILABLE", "ANTHROPIC_PROTOCOL_ERROR",
})


class PublicGeneration(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    temperature: float
    thinking: bool
    max_output_tokens: int


class PublicPreset(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    preset_id: str
    display_name: str
    provider_type: str
    model: str
    base_url: str | None
    credential_source: Literal["environment", "none"]
    generation: PublicGeneration
    context_length: int | None
    timeout_seconds: float | None


class PublicSettings(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    provider_presets: tuple[PublicPreset, ...]
    selected_preset_id: str


class SettingsView(BaseModel):
    schema_version: Literal[1]
    revision: int
    source: Literal["defaults", "persisted"]
    updated_at: datetime | None
    settings: PublicSettings


class ReplaceSettingsRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    settings: PublicSettings


class RuntimeSettingsSummary(BaseModel):
    source: Literal["defaults", "persisted"]
    revision: int
    selected_preset_id: str


class ActiveProviderStatus(BaseModel):
    provider: str
    model: str
    available: bool
    detail: str


class ActiveMainModelStatus(BaseModel):
    source: Literal["bootstrap", "model_config"]
    provider: str
    model: str


class ActiveEmbeddingStatus(BaseModel):
    enabled: bool
    provider: str | None
    model: str | None


class ModelConfigStatus(BaseModel):
    configured: bool
    revision: int


class EpisodeWorkerStatus(BaseModel):
    state: Literal["STOPPED", "RUNNING", "RETRYING", "FAULTED"]
    stopping: bool
    phase: str | None = None
    attempt: int = 0
    next_retry_at: float | None = None
    last_success_at: float | None = None
    last_failure_at: float | None = None
    error_code: str | None = None
    sqlite_code: int | None = None


class RuntimeStatusView(BaseModel):
    model_config = ConfigDict(populate_by_name=True)
    status: Literal["ok", "degraded"]
    service: Literal["embodiednpc-backend"]
    settings: RuntimeSettingsSummary
    active_provider: ActiveProviderStatus
    provider_settings_activation: Literal["not_supported"]
    active_main_model: ActiveMainModelStatus | None = None
    active_embedding: ActiveEmbeddingStatus | None = None
    model_config_status: ModelConfigStatus | None = Field(default=None, alias="model_config")
    restart_required: bool | None = None
    episode_worker: EpisodeWorkerStatus | None = None


def _public_view(document: RuntimeSettingsDocument, source: Literal["defaults", "persisted"]) -> SettingsView:
    presets = tuple(
        PublicPreset(
            preset_id=p.preset_id, display_name=p.display_name, provider_type=p.provider_type,
            model=p.model, base_url=p.base_url,
            credential_source="environment" if p.credential_reference else "none",
            generation=PublicGeneration(
                temperature=p.generation.temperature, thinking=p.generation.thinking,
                max_output_tokens=p.generation.max_output_tokens,
            ),
            context_length=p.context_length, timeout_seconds=p.timeout_seconds,
        ) for p in document.settings.provider_presets
    )
    return SettingsView(
        schema_version=document.schema_version, revision=document.revision, source=source,
        updated_at=document.updated_at if source == "persisted" else None,
        settings=PublicSettings(provider_presets=presets, selected_preset_id=document.settings.selected_preset_id),
    )


def _validated_settings(public: PublicSettings, current: RuntimeSettingsDocument) -> RuntimeSettings:
    previous = {p.preset_id: p for p in current.settings.provider_presets}
    presets = []
    for p in public.provider_presets:
        # The public API can retain an existing environment binding, or clear it.
        # It cannot create a new credential reference or disclose its name.
        reference = None
        if p.credential_source == "environment":
            reference = previous.get(p.preset_id).credential_reference if p.preset_id in previous else None
            if reference is None:
                raise ValueError("SETTINGS_CREDENTIAL_BINDING_UNAVAILABLE")
        presets.append(ProviderPreset(
            preset_id=p.preset_id, display_name=p.display_name, provider_type=p.provider_type,
            model=p.model, base_url=p.base_url, credential_reference=reference,
            generation=GenerationSettings(
                temperature=p.generation.temperature, thinking=p.generation.thinking,
                max_output_tokens=p.generation.max_output_tokens,
            ), context_length=p.context_length, timeout_seconds=p.timeout_seconds,
        ))
    return RuntimeSettings(provider_presets=tuple(presets), selected_preset_id=public.selected_preset_id)


def _error(status: int, code: str) -> JSONResponse:
    return JSONResponse(status_code=status, content={"error": {"code": code}})


def _store_error(exc: Exception) -> JSONResponse:
    if isinstance(exc, SettingsRevisionConflict):
        return _error(409, exc.code)
    if isinstance(exc, SettingsInvalidDocument):
        return _error(500, exc.code)
    if isinstance(exc, SettingsStorageError):
        return _error(503, exc.code)
    return _error(500, "SETTINGS_INTERNAL_ERROR")


def _reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("SETTINGS_REQUEST_INVALID")
        result[key] = value
    return result


def create_runtime_settings_router(
    store: RuntimeSettingsStore, provider: ModelProvider, *,
    model_config_store: ModelApiConfigStore | None = None,
    startup_model_config_document: ModelApiConfigDocument | None = None,
    active_embedding_provider: object | None = None,
    episode_worker_status: Callable[[], dict[str, object]] | None = None,
) -> APIRouter:
    router = APIRouter()

    @router.get("/v1/settings", response_model=SettingsView)
    async def get_settings() -> SettingsView | JSONResponse:
        try:
            document, source = store.load_with_source()
        except (SettingsInvalidDocument, SettingsStorageError) as exc:
            return _store_error(exc)
        return _public_view(document, source)

    @router.put("/v1/settings", response_model=SettingsView)
    async def replace_settings(request: Request) -> SettingsView | JSONResponse:
        raw = await request.body()
        if len(raw) > _MAX_REQUEST_BYTES:
            return _error(422, "SETTINGS_REQUEST_TOO_LARGE")
        try:
            json.loads(raw, object_pairs_hook=_reject_duplicates)
            replacement = ReplaceSettingsRequest.model_validate_json(raw)
        except (ValueError, ValidationError, UnicodeError):
            return _error(422, "SETTINGS_REQUEST_INVALID")
        try:
            current, _ = store.load_with_source()
            if current.revision != replacement.expected_revision:
                return _error(409, "SETTINGS_REVISION_CONFLICT")
            settings = _validated_settings(replacement.settings, current)
            saved = store.save(settings, expected_revision=replacement.expected_revision)
        except (ValueError, ValidationError):
            return _error(422, "SETTINGS_VALUE_INVALID")
        except (SettingsInvalidDocument, SettingsRevisionConflict, SettingsStorageError) as exc:
            return _store_error(exc)
        return _public_view(saved, "persisted")

    @router.get("/v1/runtime-status", response_model=RuntimeStatusView,
                response_model_exclude_none=True)
    async def get_runtime_status() -> RuntimeStatusView | JSONResponse:
        try:
            document, source = store.load_with_source()
        except (SettingsInvalidDocument, SettingsStorageError) as exc:
            return _store_error(exc)
        saved_model_config = None
        if model_config_store is not None:
            try:
                saved_model_config = model_config_store.load()
            except ModelConfigStoreError as exc:
                return _error(503 if exc.code == "MODEL_CONFIG_READ_FAILED" else 500, exc.code)
        try:
            health = await provider.health()
            available = health.available
            detail = health.detail if health.detail in _SAFE_HEALTH_DETAILS else "PROVIDER_HEALTH_UNAVAILABLE"
        except Exception:
            available, detail = False, "PROVIDER_HEALTH_UNAVAILABLE"
        return RuntimeStatusView(
            episode_worker=(EpisodeWorkerStatus(**episode_worker_status())
                            if episode_worker_status is not None else None),
            status="ok" if available else "degraded", service="embodiednpc-backend",
            settings=RuntimeSettingsSummary(
                source=source, revision=document.revision,
                selected_preset_id=document.settings.selected_preset_id,
            ),
            active_provider=ActiveProviderStatus(
                provider=provider.name, model=provider.model, available=available, detail=detail,
            ),
            provider_settings_activation="not_supported",
            active_main_model=(ActiveMainModelStatus(
                source="model_config" if has_activation(startup_model_config_document) else "bootstrap",
                provider=provider.name, model=provider.model) if model_config_store is not None else None),
            active_embedding=(ActiveEmbeddingStatus(
                enabled=active_embedding_provider is not None,
                provider=(startup_model_config_document.embedding.provider_type if has_activation(startup_model_config_document)
                          else "ollama" if isinstance(active_embedding_provider, OllamaEmbeddingProvider) else None),
                model=getattr(getattr(active_embedding_provider, "provider", active_embedding_provider), "model", None))
                if model_config_store is not None else None),
            model_config_status=(ModelConfigStatus(
                configured=has_activation(saved_model_config),
                revision=saved_model_config.revision if saved_model_config is not None else 0)
                if model_config_store is not None else None),
            restart_required=(model_restart_required(startup_model_config_document, saved_model_config)
                              if model_config_store is not None else None),
        )

    return router
