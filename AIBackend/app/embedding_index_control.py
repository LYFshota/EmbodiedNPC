"""One durable gate for both indexing and query embeddings; no Memory writes."""

import asyncio
import json
import threading
import sqlite3
from pathlib import Path

from fastapi import APIRouter, Request
from pydantic import BaseModel, ConfigDict
from typing import Literal

from .canonical_embeddings import CanonicalEmbeddingIndexer, embedding_text
from .model_api_config_store import atomic_json_write, ModelConfigStoreError
from .model_api_config_api import _parse_request, _error
from .model_config_runtime import effective_runtime_config
from .model_profile_config import has_activation


class EmbeddingControl:
    def __init__(self, provider, state_path, *, cloud, auto_start_builtin=False):
        self.provider, self.path, self.cloud = provider, Path(state_path), cloud
        self._lock = threading.RLock()
        self.mode, self.consented, self.error_code, self.in_flight = "waiting", False, None, 0
        self._states = {}
        try:
            if self.path.exists():
                raw = self.path.read_bytes()
                if len(raw) > 128 * 1024:
                    raise ValueError
                value = json.loads(raw)
                if set(value) != {"schema_version", "spaces"} or value["schema_version"] != 1 or not isinstance(value["spaces"], dict):
                    raise ValueError
                for state in value["spaces"].values():
                    if (not isinstance(state, dict) or set(state) != {"mode", "consented"} or
                        state["mode"] not in ("running", "paused", "waiting", "failed") or type(state["consented"]) is not bool):
                        raise ValueError
                self._states = value["spaces"]
                state = self._states.get(provider.model_key)
                if state:
                    self.mode, self.consented = state["mode"], state["consented"]
                    if self.mode == "failed":
                        self.error_code = "EMBEDDING_PREVIOUS_REQUEST_FAILED"
                    if cloud and self.mode == "running" and not self.consented:
                        raise ValueError
        except (OSError, ValueError, TypeError):
            self.mode, self.error_code = "failed", "EMBEDDING_CONTROL_STATE_INVALID"
        # Only the startup-selected builtin is opted in. Existing pause/failure decisions
        # and cloud consent remain authoritative; workers still process pending rows only.
        if auto_start_builtin and not cloud and self.error_code is None and self.mode == "waiting":
            from .builtin_embedding_provider import BuiltinEmbeddingProvider
            if isinstance(provider, BuiltinEmbeddingProvider):
                self.mode = "running"

    @property
    def model_key(self):
        return self.provider.model_key

    @property
    def dimensions(self):
        return getattr(self.provider, "dimensions", None)

    def _persist(self, mode, consented):
        if self.error_code == "EMBEDDING_CONTROL_STATE_INVALID":
            raise ValueError(self.error_code)
        states = {**self._states, self.model_key: dict(mode=mode, consented=consented)}
        atomic_json_write(self.path, json.dumps(dict(schema_version=1, spaces=states)).encode())
        self._states, self.mode, self.consented = states, mode, consented

    def command(self, action, confirm=False):
        with self._lock:
            if action == "pause":
                self._persist("paused", self.consented)
            else:
                if self.cloud and not (confirm or self.consented):
                    raise ValueError("EMBEDDING_CLOUD_CONFIRMATION_REQUIRED")
                self._persist("running", self.consented or confirm)
                self.error_code = None

    def embed(self, text):
        return self._embed(text, query=False)

    def embed_query(self, text):
        return self._embed(text, query=True)

    def close(self):
        close = getattr(self.provider, "close", None)
        if callable(close):
            close()

    def _embed(self, text, *, query):
        with self._lock:
            if self.mode != "running":
                raise ValueError("EMBEDDING_PAUSED")
            self.in_flight += 1
        try:
            encode = getattr(self.provider, "embed_query", self.provider.embed) if query else self.provider.embed
            return encode(text)
        except Exception as error:
            from .builtin_embedding_provider import BuiltinEmbeddingError
            code = error.code if isinstance(error, BuiltinEmbeddingError) else "EMBEDDING_REQUEST_FAILED"
            with self._lock:
                if self.mode == "running":
                    try:
                        self._persist("failed", self.consented)
                        self.error_code = code
                    except (OSError, ValueError, ModelConfigStoreError):
                        self.mode, self.error_code = "failed", "EMBEDDING_CONTROL_WRITE_FAILED"
            raise ValueError(code) from None
        finally:
            with self._lock:
                self.in_flight -= 1


class ManagedEmbeddingIndexer(CanonicalEmbeddingIndexer):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._wake = asyncio.Event()
        self._stopping = False

    async def index_missing_or_stale(self, limit=8):
        if not 1 <= limit <= 16:
            raise ValueError("INVALID_EMBEDDING_BATCH")
        pending = await asyncio.to_thread(self.index.pending, self.provider.model_key, limit, self.provider.dimensions)
        count = 0
        for row in pending:
            if self._stopping or self.provider.mode != "running":
                break
            try:
                vector = await asyncio.to_thread(self.provider.embed, embedding_text(row))
                count += await asyncio.to_thread(self.index.store_if_current, row, self.provider.model_key, vector)
            except Exception:
                if self.provider.mode == "running":
                    self.provider.command("pause")
                    self.provider.error_code = "EMBEDDING_INDEX_FAILED"
                break
        return count

    def notify(self):
        self._wake.set()

    def start(self):
        self._stopping = False
        super().start()

    async def stop(self):
        self._stopping = True
        self.notify()
        if self._task is not None:
            await self._task
            self._task = None

    async def _run(self):
        while not self._stopping:
            self._wake.clear()
            if self.provider.mode == "running":
                try:
                    await self.index_missing_or_stale(self.batch_size)
                except Exception:
                    try:
                        self.provider.command("pause")
                    except (ValueError, ModelConfigStoreError):
                        self.provider.mode = "failed"
                    self.provider.error_code = "EMBEDDING_INDEX_FAILED"
            if self._stopping:
                break
            try:
                await asyncio.wait_for(self._wake.wait(), self.interval_seconds)
            except TimeoutError:
                pass


class IndexCommand(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    action: Literal["start", "pause", "resume"]
    model_key: str
    confirm_cloud_transfer: bool = False


def create_embedding_index_router(store, active_document, indexer):
    router = APIRouter()
    def applied():
        saved = store.load()
        if not has_activation(saved) or not has_activation(active_document):
            return False
        return effective_runtime_config(saved)[2] == effective_runtime_config(active_document)[2]

    @router.get("/v1/embedding-index/status")
    async def status():
        if indexer is None:
            try:
                is_applied = applied()
                saved = store.load()
                pending_enabled = has_activation(saved) and saved.embedding.enabled
                return dict(status="restart_required" if pending_enabled and not is_applied else "disabled",
                            applied=is_applied, control_allowed=False, counts_known=False, error_code=None)
            except (ModelConfigStoreError, ValueError):
                return _error(503, "EMBEDDING_INDEX_STATUS_UNAVAILABLE")
        provider = indexer.provider
        try:
            is_applied = applied()
            counts = await asyncio.to_thread(indexer.index.statistics, provider.model_key, getattr(provider, "dimensions", None))
            mode = getattr(provider, "mode", "legacy_auto")
            state = "completed" if mode == "running" and counts["pending"] == 0 else mode
            adapter = getattr(provider, "provider", provider)
            availability = {}
            if hasattr(adapter, "provider_status"):
                availability = dict(provider_status=adapter.provider_status,
                    provider_error_code=adapter.error_code,
                    index_error_code="EMBEDDING_INDEX_INCOMPLETE" if counts["pending"] else None)
            return dict(**counts, status=state if is_applied or mode == "legacy_auto" else "restart_required", applied=is_applied,
                **availability,
                model_key=provider.model_key, model=getattr(getattr(provider, "provider", provider), "model", ""),
                cloud=getattr(provider, "cloud", False), consented=getattr(provider, "consented", False),
                in_flight=getattr(provider, "in_flight", 0), error_code=getattr(provider, "error_code", None),
                counts_known=True, control_allowed=is_applied and isinstance(provider, EmbeddingControl) and
                    provider.error_code != "EMBEDDING_CONTROL_STATE_INVALID")
        except (ModelConfigStoreError, ValueError, sqlite3.Error):
            return _error(503, "EMBEDDING_INDEX_STATUS_UNAVAILABLE")

    @router.post("/v1/embedding-index/control")
    async def control(request: Request):
        command = await _parse_request(request, IndexCommand)
        if not isinstance(command, IndexCommand):
            return command
        if indexer is None or not isinstance(indexer.provider, EmbeddingControl):
            return _error(409, "EMBEDDING_MANAGED_PROFILE_REQUIRED")
        try:
            if not applied() or command.model_key != indexer.provider.model_key:
                return _error(409, "EMBEDDING_PROFILE_NOT_APPLIED")
            indexer.provider.command(command.action, command.confirm_cloud_transfer)
            indexer.notify()
            return await status()
        except (ValueError, ModelConfigStoreError) as error:
            safe_codes = {"EMBEDDING_CONTROL_STATE_INVALID", "EMBEDDING_CLOUD_CONFIRMATION_REQUIRED"}
            code = str(error) if isinstance(error, ValueError) and str(error) in safe_codes else "EMBEDDING_CONTROL_REJECTED"
            if isinstance(error, ModelConfigStoreError) and error.code == "MODEL_CONFIG_WRITE_FAILED":
                code = "EMBEDDING_CONTROL_WRITE_FAILED"
            return _error(422, code)
    return router
