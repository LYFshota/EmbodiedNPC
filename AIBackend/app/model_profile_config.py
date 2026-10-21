"""Machine-local profiles; saved edits and restart activation are separate snapshots."""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

from .model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig, ModelApiConfigDocument


class MainProfile(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    profile_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    preset_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    config: MainModelConfig


class EmbeddingProfile(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    profile_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    preset_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    config: EmbeddingConfig

    @model_validator(mode="after")
    def independent_connection(self):
        if self.profile_id != "legacy-embedding" and (not self.config.enabled or self.config.reuse_main_api):
            raise ValueError("PROFILE_EMBEDDING_MUST_BE_INDEPENDENT")
        return self


class ModelConfigV2Document(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    schema_version: Literal[2] = 2
    revision: int = Field(ge=1)
    updated_at: datetime
    main_profiles: tuple[MainProfile, ...] = Field(default=(), max_length=32)
    embedding_profiles: tuple[EmbeddingProfile, ...] = Field(default=(), max_length=32)
    selected_main_profile_id: str | None = None
    selected_embedding_profile_id: str | None = None
    activation: ModelApiConfig | None = None
    embedding_identity_version: Literal[1, 2] = 2
    migration_notes: tuple[Literal["V1_CONNECTIONS_FROZEN", "LEGACY_INDEX_IDENTITY_UNVERIFIED"], ...] = ()

    @model_validator(mode="after")
    def valid_document(self):
        if self.updated_at.tzinfo is None or self.updated_at.utcoffset() != timezone.utc.utcoffset(self.updated_at):
            raise ValueError("MODEL_CONFIG_TIMESTAMP_INVALID")
        for profiles in (self.main_profiles, self.embedding_profiles):
            ids = [profile.profile_id for profile in profiles]
            if len(ids) != len(set(ids)):
                raise ValueError("MODEL_CONFIG_DUPLICATE_PROFILE")
        if self.activation is None:
            if self.selected_main_profile_id is not None or self.selected_embedding_profile_id is not None:
                raise ValueError("MODEL_CONFIG_ACTIVATION_INVALID")
        else:
            if self.selected_main_profile_id not in {p.profile_id for p in self.main_profiles}:
                raise ValueError("MODEL_CONFIG_MAIN_PROFILE_NOT_FOUND")
            if self.activation.embedding.enabled and self.selected_embedding_profile_id not in {
                    p.profile_id for p in self.embedding_profiles}:
                raise ValueError("MODEL_CONFIG_EMBEDDING_PROFILE_NOT_FOUND")
            if self.activation.embedding.enabled and self.activation.embedding.reuse_main_api:
                raise ValueError("MODEL_CONFIG_ACTIVATION_NOT_INDEPENDENT")
        return self

    @property
    def main_model(self) -> MainModelConfig:
        return self.activation.main_model if self.activation else MainModelConfig(
            display_name="Unconfigured", provider_type="mock", model="fixed-greeting-v1")

    @property
    def embedding(self) -> EmbeddingConfig:
        return self.activation.embedding if self.activation else EmbeddingConfig()


ModelDocument = ModelApiConfigDocument | ModelConfigV2Document


def has_activation(document: ModelDocument | None) -> bool:
    return document is not None and (isinstance(document, ModelApiConfigDocument) or document.activation is not None)


def as_v2(document: ModelDocument | None) -> ModelConfigV2Document:
    if isinstance(document, ModelConfigV2Document):
        return document
    if document is None:
        return ModelConfigV2Document(revision=1, updated_at=datetime.now(timezone.utc))
    main = document.main_model
    # Resolve v1 reuse once, so later main selection cannot change embedding silently.
    config = ModelApiConfig(main_model=main, embedding=document.embedding)
    embedding = document.embedding
    if embedding.enabled:
        base, key, model = config.effective_embedding_connection()
        embedding = EmbeddingConfig.model_validate({**embedding.model_dump(),
            "base_url": base, "api_key": key, "model": model, "reuse_main_api": False})
    main_preset = {"ollama": "ollama", "deepseek_official_responses": "deepseek"}.get(main.provider_type, "custom")
    embed_preset = {"ollama": "ollama", "builtin": "builtin_gemma"}.get(embedding.provider_type, "custom")
    return ModelConfigV2Document(
        revision=document.revision, updated_at=document.updated_at,
        main_profiles=(MainProfile(profile_id="legacy-main", preset_id=main_preset, config=main),),
        embedding_profiles=(EmbeddingProfile(profile_id="legacy-embedding", preset_id=embed_preset,
                                             config=embedding),),
        selected_main_profile_id="legacy-main",
        selected_embedding_profile_id="legacy-embedding" if embedding.enabled else None,
        activation=ModelApiConfig(main_model=main, embedding=embedding),
        embedding_identity_version=1,
        migration_notes=("V1_CONNECTIONS_FROZEN", "LEGACY_INDEX_IDENTITY_UNVERIFIED"),
    )
