"""Small profile editing API. Credentials are write-only on this v2 surface."""

from __future__ import annotations

from typing import Literal

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .model_api_config import EmbeddingConfig, MainModelConfig, ModelApiConfig
from .model_api_config_api import _parse_request, _store_error, _error, _test_main, _test_embedding
from .model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from .model_profile_config import EmbeddingProfile, MainProfile, ModelConfigV2Document, as_v2, has_activation
from .model_provider_presets import MAIN_PRESETS, EMBEDDING_PRESETS, validate_preset, new_main_context_length
from dataclasses import asdict
from .model_profile_probes import contract_probe, discover_profile
from .model_config_runtime import restart_required
from .portable_resources import resolve_embedding_resources


class SaveProfileCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    kind: Literal["main", "embedding"]
    profile_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    preset_id: str = Field(pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    key_action: Literal["retain", "replace", "clear"] = "retain"
    config: dict


class ActivateProfileCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    kind: Literal["main", "embedding"]
    profile_id: str | None = None
    embedding_enabled: bool = False


class ResetMainCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    profile_id: str | None = Field(default=None, pattern=r"^[a-z][a-z0-9_-]{0,63}$")
    key_action: Literal["clear"]


class ProbeProfileCommand(SaveProfileCommand):
    operation: Literal["discover", "connection", "contracts"]
    confirm_paid_requests: bool = False


class SaveAndActivateCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    main: SaveProfileCommand
    embedding: SaveProfileCommand | None = None
    embedding_enabled: bool


def save_and_activate(document, command):
    if (command.main.kind != "main" or
            command.main.expected_revision != command.expected_revision or
            (command.embedding is not None and (command.embedding.kind != "embedding" or
             command.embedding.expected_revision != command.expected_revision)) or
            (command.embedding_enabled and command.embedding is None)):
        raise ValueError("MODEL_CONFIG_VALUE_INVALID")
    # Validate both drafts before the existing store performs its single atomic write.
    changed = save_profile(document, command.main)
    if command.embedding is not None:
        changed = save_profile(changed, command.embedding)
    changed = activate_profile(changed, ActivateProfileCommand(
        expected_revision=command.expected_revision, kind="main", profile_id=command.main.profile_id))
    return activate_profile(changed, ActivateProfileCommand(
        expected_revision=command.expected_revision, kind="embedding",
        profile_id=command.embedding.profile_id if command.embedding else None,
        embedding_enabled=command.embedding_enabled))


def public_connection(config):
    config = resolve_embedding_resources(config)
    view = {**config.model_dump(mode="json", exclude={"api_key"}, exclude_none=True), "key_present": bool(config.api_key)}
    if config.provider_type == "builtin":
        from pathlib import Path
        from .builtin_embedding_provider import RESOURCE_SPEC
        view["resource_status"] = "unverified" if all(
            (Path(config.resource_directory) / name).is_file() for name in RESOURCE_SPEC["files"]) else "missing"
    return view


def public_profile_view(document, active=None) -> dict:
    # Deployment owns the local resource location; first-run users never type it.
    from pathlib import Path
    import os
    from .builtin_embedding_provider import MODEL, REVISION, RESOURCE_SPEC
    resource_directory = Path(os.environ.get("EMBODIEDNPC_BUILTIN_EMBEDDING_DIR",
        str(Path(__file__).resolve().parents[1] / "models" / MODEL))).resolve()
    builtin = EmbeddingConfig(enabled=True, provider_type="builtin", model=MODEL,
        dimensions=768, model_version=REVISION, resource_directory=str(resource_directory))
    resources_present = all((resource_directory / name).is_file() for name in RESOURCE_SPEC["files"])
    value = as_v2(document)
    def profiles(items):
        return [{"profile_id": p.profile_id, "preset_id": p.preset_id,
                 "config": public_connection(p.config)} for p in items]
    def snapshot(doc):
        if not has_activation(doc):
            return None
        v2 = as_v2(doc)
        return {"main_profile_id": v2.selected_main_profile_id,
                "embedding_profile_id": v2.selected_embedding_profile_id,
                "main_model": public_connection(v2.main_model),
                "embedding": public_connection(v2.embedding)}
    return {"schema_version": 2, "revision": document.revision if document else 0,
            "configured": has_activation(document),
            "main_profiles": profiles(value.main_profiles),
            "embedding_profiles": profiles(value.embedding_profiles),
            "selected_to_activate": snapshot(document), "active_runtime": snapshot(active),
            "migration_required": document is not None and document.schema_version == 1,
            "migration_notes": value.migration_notes,
            "main_presets": [asdict(p) for p in MAIN_PRESETS],
            "embedding_presets": [asdict(p) for p in EMBEDDING_PRESETS],
            "default_main_preset_id": "deepseek",
            "default_embedding_preset_id": "builtin_gemma" if document is None else "aliyun",
            "builtin_embedding_defaults": public_connection(builtin),
            # Presence is not integrity verification, model loading, or activation.
            "builtin_resource_status": "unverified" if resources_present else "missing",
            "restart_required": restart_required(active, document)}


def profile_connection(command: SaveProfileCommand, document: ModelConfigV2Document):
    items = document.main_profiles if command.kind == "main" else document.embedding_profiles
    previous = next((p for p in items if p.profile_id == command.profile_id), None)
    values = dict(command.config)
    if command.key_action == "replace":
        if not isinstance(values.get("api_key"), str) or not values["api_key"].strip():
            raise ValueError("MODEL_PROFILE_KEY_REQUIRED")
    else:
        if "api_key" in values:
            raise ValueError("MODEL_PROFILE_KEY_ACTION_INVALID")
        values["api_key"] = previous.config.api_key if previous and command.key_action == "retain" else None
    if previous and previous.preset_id != command.preset_id:
        raise ValueError("MODEL_PROFILE_PRESET_IMMUTABLE")
    if command.kind == "main":
        if previous is None and values.get("context_length") is None:
            values["context_length"] = new_main_context_length(values)
        elif previous is not None and "context_length" not in values:
            values["context_length"] = previous.config.context_length
        if previous is not None and "max_output_tokens" not in values:
            values["max_output_tokens"] = previous.config.max_output_tokens
    domain = MainModelConfig if command.kind == "main" else EmbeddingConfig
    return domain.model_validate(values)


def save_profile(document: ModelConfigV2Document, command: SaveProfileCommand):
    config = profile_connection(command, document)
    validate_preset(command.kind, command.preset_id, config)
    domain = MainProfile if command.kind == "main" else EmbeddingProfile
    profile = domain(profile_id=command.profile_id, preset_id=command.preset_id, config=config)
    field = "main_profiles" if command.kind == "main" else "embedding_profiles"
    items = tuple(p for p in getattr(document, field) if p.profile_id != command.profile_id) + (profile,)
    return document.model_copy(update={field: items})


def activate_profile(document: ModelConfigV2Document, command: ActivateProfileCommand):
    if command.kind == "main":
        profile = next((p for p in document.main_profiles if p.profile_id == command.profile_id), None)
        if profile is None:
            raise ValueError("MODEL_PROFILE_NOT_FOUND")
        activation = ModelApiConfig(main_model=profile.config, embedding=document.embedding)
        return document.model_copy(update={"activation": activation, "selected_main_profile_id": profile.profile_id})
    if document.activation is None:
        raise ValueError("MODEL_MAIN_ACTIVATION_REQUIRED")
    if not command.embedding_enabled:
        return document.model_copy(update={"activation": ModelApiConfig(
            main_model=document.main_model, embedding=EmbeddingConfig()), "selected_embedding_profile_id": None})
    profile = next((p for p in document.embedding_profiles if p.profile_id == command.profile_id), None)
    if profile is None:
        raise ValueError("MODEL_PROFILE_NOT_FOUND")
    return document.model_copy(update={"activation": ModelApiConfig(
        main_model=document.main_model, embedding=profile.config), "selected_embedding_profile_id": profile.profile_id,
        "embedding_identity_version": 2})


def reset_main(document: ModelConfigV2Document, command: ResetMainCommand):
    # Reuse v2's existing unconfigured representation. Remove both the profile
    # credential and its activation snapshot in the store's one atomic write.
    if (document.selected_main_profile_id is not None and
            document.selected_main_profile_id != command.profile_id):
        raise ValueError("MODEL_CONFIG_VALUE_INVALID")
    if command.profile_id is not None and not any(p.profile_id == command.profile_id for p in document.main_profiles):
        raise ValueError("MODEL_CONFIG_VALUE_INVALID")
    return document.model_copy(update={
        "main_profiles": tuple(p for p in document.main_profiles if p.profile_id != command.profile_id),
        "activation": None, "selected_main_profile_id": None, "selected_embedding_profile_id": None})


def create_model_profile_router(store: ModelApiConfigStore, *, active_document=None, async_transport=None, sync_transport=None):
    router = APIRouter()

    @router.get("/v2/model-config")
    async def get_profiles():
        try:
            return public_profile_view(store.load(), active_document)
        except ModelConfigStoreError as error:
            return _store_error(error)

    async def change(request, domain, transform):
        command = await _parse_request(request, domain)
        if isinstance(command, JSONResponse):
            return command
        try:
            value = store.update_profiles(expected_revision=command.expected_revision,
                                          transform=lambda doc: transform(doc, command))
            return public_profile_view(value, active_document)
        except ModelConfigStoreError as error:
            return _store_error(error)

    @router.post("/v2/model-config/profile")
    async def put_profile(request: Request):
        return await change(request, SaveProfileCommand, save_profile)

    @router.post("/v2/model-config/activate")
    async def activate(request: Request):
        return await change(request, ActivateProfileCommand, activate_profile)

    @router.post("/v2/model-config/save-and-activate")
    async def save_and_activate_profiles(request: Request):
        return await change(request, SaveAndActivateCommand, save_and_activate)

    @router.post("/v2/model-config/reset-main")
    async def reset_main_profile(request: Request):
        return await change(request, ResetMainCommand, reset_main)

    @router.post("/v2/model-config/probe")
    async def probe(request: Request):
        command = await _parse_request(request, ProbeProfileCommand)
        if isinstance(command, JSONResponse):
            return command
        try:
            saved = store.load()
            if (saved.revision if saved else 0) != command.expected_revision:
                return _error(409, "MODEL_CONFIG_REVISION_CONFLICT")
            config = profile_connection(command, as_v2(saved))
            validate_preset(command.kind, command.preset_id, config)
            preset = next(p for p in (MAIN_PRESETS if command.kind == "main" else EMBEDDING_PRESETS)
                          if p.preset_id == command.preset_id)
            if command.operation == "discover":
                return await discover_profile(config, command.kind, preset, async_transport)
            if not command.confirm_paid_requests:
                return _error(422, "MODEL_PROBE_CONFIRMATION_REQUIRED")
            if command.operation == "contracts":
                if command.kind != "main":
                    return _error(422, "MODEL_CONTRACT_MAIN_REQUIRED")
                return await contract_probe(config, async_transport)
            if command.kind == "main":
                return await _test_main(config.model_copy(update={"timeout_seconds": min(config.timeout_seconds, 20.0)}), async_transport)
            return await _test_embedding(ModelApiConfig(main_model=MainModelConfig(display_name="Probe",
                provider_type="mock", model="probe"), embedding=config.model_copy(update={"timeout_seconds": min(config.timeout_seconds, 20.0)})), sync_transport)
        except ModelConfigStoreError as error:
            return _store_error(error)
        except ValidationError as error:
            fields = {str(part) for item in error.errors(include_input=False, include_url=False) for part in item["loc"]}
            code = ("MODEL_PROFILE_MODEL_REQUIRED" if "model" in fields else
                    "MODEL_PROFILE_URL_INVALID" if "base_url" in fields else "MODEL_PROFILE_DRAFT_INVALID")
            return _error(422, code)
        except (ValueError, StopIteration):
            return _error(422, "MODEL_PROFILE_PROBE_INVALID")

    return router
