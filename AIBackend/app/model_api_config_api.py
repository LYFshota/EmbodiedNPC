"""Local Demo model configuration, discovery, and connection probes."""

from __future__ import annotations

import asyncio
import json
import re
from datetime import datetime
from typing import Literal

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .canonical_embeddings import OllamaEmbeddingProvider, OpenAICompatibleEmbeddingProvider
from .deepseek_provider import DeepSeekOfficialResponsesProvider
from .model_api_config import (
    EmbeddingConfig, MainModelConfig, ModelApiConfig, ModelApiConfigDocument, valid_base_url,
)
from .model_api_config_store import ModelApiConfigStore, ModelConfigStoreError
from .openai_compatible_provider import OpenAICompatibleProvider
from .providers import InferenceOptions, ModelMessage, ModelRequest, OllamaProvider, ProviderError
from .model_profile_config import has_activation
from .model_config_runtime import build_main_provider, build_embedding_provider
from .portable_resources import resolve_embedding_resources


_MAX_REQUEST_BYTES = 256 * 1024
_CODE = re.compile(r"^[A-Z][A-Z0-9_]{2,63}$")
_TEST_SCHEMA = {"type": "object", "properties": {"ok": {"type": "boolean"}},
                "required": ["ok"], "additionalProperties": False}


class ModelConfigView(BaseModel):
    schema_version: Literal[1] = 1
    configured: bool
    revision: int
    updated_at: datetime | None
    main_model: MainModelConfig | None
    embedding: EmbeddingConfig | None


class ReplaceModelConfig(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    config: ModelApiConfig


class DiscoverModelsRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    provider_type: str
    base_url: str
    api_key: str | None = None
    timeout_seconds: float = Field(default=15.0, gt=0, le=120)


class TestConnectionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    kind: Literal["main", "embedding"]
    main_model: MainModelConfig
    embedding: EmbeddingConfig | None = None


def _view(document: ModelApiConfigDocument | None) -> ModelConfigView:
    if document is None:
        return ModelConfigView(configured=False, revision=0, updated_at=None,
                               main_model=None, embedding=None)
    return ModelConfigView(configured=has_activation(document), revision=document.revision, updated_at=document.updated_at,
                           main_model=document.main_model, embedding=resolve_embedding_resources(document.embedding))


def _error(status: int, code: str) -> JSONResponse:
    return JSONResponse(status_code=status, content={"error": {"code": code}})


def _store_error(exc: ModelConfigStoreError) -> JSONResponse:
    if exc.code in ("MODEL_CONFIG_REVISION_CONFLICT", "MODEL_CONFIG_V2_REQUIRED"):
        return _error(409, exc.code)
    if exc.code in ("MODEL_CONFIG_VALUE_INVALID", "MODEL_CONFIG_REVISION_INVALID"):
        return _error(422, exc.code)
    if exc.code in ("MODEL_CONFIG_READ_FAILED", "MODEL_CONFIG_WRITE_FAILED"):
        return _error(503, exc.code)
    return _error(500, exc.code)


def _reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("DUPLICATE_JSON_KEY")
        result[key] = value
    return result


async def _parse_request(request: Request, model: type[BaseModel]) -> BaseModel | JSONResponse:
    raw = await request.body()
    if len(raw) > _MAX_REQUEST_BYTES:
        return _error(422, "MODEL_CONFIG_REQUEST_TOO_LARGE")
    try:
        json.loads(raw, object_pairs_hook=_reject_duplicates)
        return model.model_validate_json(raw)
    except (ValueError, ValidationError, UnicodeError):
        return _error(422, "MODEL_CONFIG_REQUEST_INVALID")


def _safe_code(value: str, fallback: str) -> str:
    return value if _CODE.fullmatch(value) else fallback


async def _discover(input: DiscoverModelsRequest, transport: httpx.AsyncBaseTransport | None) -> dict[str, object]:
    if input.provider_type not in ("openai_compatible", "ollama", "deepseek_official_responses"):
        return {"success": False, "models": [], "error_code": "MODEL_DISCOVERY_UNSUPPORTED"}
    try:
        base = valid_base_url(input.base_url)
        url = base + ("/api/tags" if input.provider_type == "ollama" else "/models")
        headers = {"Authorization": "Bearer " + input.api_key} if input.api_key else {}
        async with httpx.AsyncClient(timeout=input.timeout_seconds, trust_env=False,
                                     transport=transport) as client:
            response = await client.get(url, headers=headers)
        if response.status_code >= 400:
            return {"success": False, "models": [], "error_code": "MODEL_DISCOVERY_HTTP_FAILED"}
        body = response.json()
        records = body["models"] if input.provider_type == "ollama" else body["data"]
        if not isinstance(records, list):
            raise ValueError
        ids = set()
        for item in records:
            if not isinstance(item, dict):
                raise ValueError
            name = (item.get("name") or item.get("model")) if input.provider_type == "ollama" else item.get("id")
            if not isinstance(name, str) or not name:
                raise ValueError
            ids.add(name)
        return ({"success": True, "models": sorted(ids), "error_code": None} if ids else
                {"success": False, "models": [], "error_code": "MODEL_DISCOVERY_EMPTY"})
    except httpx.TimeoutException:
        code = "MODEL_DISCOVERY_TIMEOUT"
    except httpx.HTTPError:
        code = "MODEL_DISCOVERY_UNAVAILABLE"
    except (KeyError, TypeError, ValueError):
        code = "MODEL_DISCOVERY_PROTOCOL_ERROR"
    return {"success": False, "models": [], "error_code": code}


async def _test_main(main: MainModelConfig, transport: httpx.AsyncBaseTransport | None) -> dict[str, object]:
    if main.provider_type == "mock":
        return {"success": True, "error_code": None}
    try:
        provider = build_main_provider(main, transport)
    except ValueError:
        return {"success": False, "error_code": "MODEL_TEST_CONFIG_INVALID"}
    request = ModelRequest(
        messages=(ModelMessage(role="user", content='Reply with JSON: {"ok":true}'),),
        response_schema=_TEST_SCHEMA,
        inference_options=InferenceOptions(temperature=0, think=False, max_output_tokens=32),
    )
    try:
        result = await provider.generate(request)
        payload = json.loads(result.text)
        if not isinstance(payload, dict) or payload.get("ok") is not True:
            raise ValueError
        return {"success": True, "error_code": None}
    except ProviderError as exc:
        return {"success": False, "error_code": _safe_code(str(exc), "MODEL_TEST_FAILED")}
    except (ValueError, TypeError, json.JSONDecodeError):
        return {"success": False, "error_code": "MODEL_TEST_RESPONSE_INVALID"}


async def _test_embedding(config: ModelApiConfig, transport: httpx.BaseTransport | None) -> dict[str, object]:
    provider = None
    try:
        base_url, key, model = config.effective_embedding_connection()
        embedding = config.embedding
        provider = build_embedding_provider(EmbeddingConfig.model_validate({**embedding.model_dump(),
            "base_url": base_url, "api_key": key, "model": model, "reuse_main_api": False}), transport)
        vector = await asyncio.to_thread(provider.embed, "test")
        if not vector:
            raise ValueError
        return {"success": True, "error_code": None}
    except httpx.TimeoutException:
        code = "EMBEDDING_TIMEOUT"
    except httpx.HTTPError:
        code = "EMBEDDING_UNAVAILABLE"
    except ValueError as exc:
        code = _safe_code(str(exc), "EMBEDDING_TEST_FAILED")
    finally:
        if provider is not None and callable(getattr(provider, "close", None)):
            await asyncio.to_thread(provider.close)
    return {"success": False, "error_code": code}


def create_model_api_config_router(
    store: ModelApiConfigStore, *,
    async_transport: httpx.AsyncBaseTransport | None = None,
    sync_transport: httpx.BaseTransport | None = None,
) -> APIRouter:
    router = APIRouter()

    @router.get("/v1/model-config", response_model=ModelConfigView)
    async def get_config() -> ModelConfigView | JSONResponse:
        try:
            document = store.load()
            if document is not None and document.schema_version == 2:
                return _error(409, "MODEL_CONFIG_V2_REQUIRED")
            return _view(document)
        except ModelConfigStoreError as exc:
            return _store_error(exc)

    @router.put("/v1/model-config", response_model=ModelConfigView)
    async def put_config(request: Request) -> ModelConfigView | JSONResponse:
        parsed = await _parse_request(request, ReplaceModelConfig)
        if isinstance(parsed, JSONResponse):
            return parsed
        assert isinstance(parsed, ReplaceModelConfig)
        try:
            if "context_length" not in parsed.config.main_model.model_fields_set:
                previous = store.load()
                capacity = previous.main_model.context_length if previous is not None else 32768
                main = parsed.config.main_model.model_copy(update={"context_length": capacity})
                parsed = parsed.model_copy(update={"config": parsed.config.model_copy(update={"main_model": main})})
            return _view(store.save(parsed.config, expected_revision=parsed.expected_revision))
        except ModelConfigStoreError as exc:
            return _store_error(exc)

    @router.post("/v1/model-config/discover-models", response_model=None)
    async def discover_models(request: Request) -> dict[str, object] | JSONResponse:
        parsed = await _parse_request(request, DiscoverModelsRequest)
        if isinstance(parsed, JSONResponse):
            return parsed
        assert isinstance(parsed, DiscoverModelsRequest)
        return await _discover(parsed, async_transport)

    @router.post("/v1/model-config/test", response_model=None)
    async def test_connection(request: Request) -> dict[str, object] | JSONResponse:
        parsed = await _parse_request(request, TestConnectionRequest)
        if isinstance(parsed, JSONResponse):
            return parsed
        assert isinstance(parsed, TestConnectionRequest)
        if parsed.kind == "main":
            return await _test_main(parsed.main_model, async_transport)
        if parsed.embedding is None:
            return _error(422, "EMBEDDING_CONFIG_REQUIRED")
        try:
            config = ModelApiConfig(main_model=parsed.main_model, embedding=parsed.embedding)
        except ValidationError:
            return _error(422, "MODEL_CONFIG_REQUEST_INVALID")
        return await _test_embedding(config, sync_transport)

    return router
