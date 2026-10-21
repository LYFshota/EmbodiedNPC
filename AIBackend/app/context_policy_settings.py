"""Small machine-local Context Policy store and hot-applied immutable snapshot."""

from __future__ import annotations

import os
import json
import tempfile
import threading
from pathlib import Path
from typing import Literal

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, model_validator

from .recent_dialogue_context import RecentDialoguePolicy
from .runtime_settings_store import _reject_duplicate_keys


class ContextPolicyConfig(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, frozen=True)
    mode: Literal["auto", "manual"] = "auto"
    target_messages: int | None = Field(default=None, ge=1, le=64)

    @model_validator(mode="after")
    def check_mode(self):
        if (self.mode == "auto") != (self.target_messages is None):
            raise ValueError("CONTEXT_POLICY_VALUE_INVALID")
        return self

    def policy(self) -> RecentDialoguePolicy:
        return RecentDialoguePolicy(self.target_messages)


class ContextPolicyDocument(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, frozen=True)
    schema_version: Literal[1]
    revision: int = Field(ge=0)
    config: ContextPolicyConfig


class ReplaceContextPolicy(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: int = Field(ge=0)
    config: ContextPolicyConfig


class ContextPolicyError(RuntimeError):
    pass


class ContextPolicySettings:
    """Persist before publishing; provider, memory and indexes are never reinitialized."""

    def __init__(self, path: Path | None = None):
        self.path = Path(path) if path is not None else Path(os.getenv(
            "EMBODIEDNPC_CONTEXT_POLICY_FILE",
            str(Path(__file__).resolve().parents[1] / "data" / "context_policy.json")))
        self._lock = threading.RLock()
        self._applied = ContextPolicyDocument(schema_version=1, revision=0, config=ContextPolicyConfig())
        self._policy = self._applied.config.policy()
        # A broken saved file is visible in GET, but cannot block safe default cognition.
        try:
            self._applied, _ = self._load()
            self._policy = self._applied.config.policy()
        except ContextPolicyError:
            pass

    def _load(self):
        try:
            raw = self.path.read_bytes()
        except FileNotFoundError:
            return ContextPolicyDocument(schema_version=1, revision=0, config=ContextPolicyConfig()), "defaults"
        except OSError:
            raise ContextPolicyError("CONTEXT_POLICY_READ_FAILED") from None
        try:
            if len(raw) > 4096:
                raise ValueError()
            json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
            return ContextPolicyDocument.model_validate_json(raw), "persisted"
        except (ValueError, UnicodeError):
            raise ContextPolicyError("CONTEXT_POLICY_DOCUMENT_INVALID") from None

    def snapshot(self) -> RecentDialoguePolicy:
        with self._lock:
            return self._policy

    def view(self):
        with self._lock:
            error = None
            try:
                saved, source = self._load()
            except ContextPolicyError as exc:
                saved, source, error = None, "invalid", str(exc)
            return dict(saved=saved.model_dump() if saved else None,
                        applied=self._applied.model_dump(), source=source, error=error,
                        hot_apply=True, automatic_rule="effective_capacity_v1_8k_8_16k_plus_16",
                        saved_matches_applied=saved == self._applied)

    def save_and_apply(self, replacement: ReplaceContextPolicy):
        # Revalidate even objects created with model_construct/model_copy.
        replacement = ReplaceContextPolicy.model_validate_json(replacement.model_dump_json())
        with self._lock:
            current, _ = self._load()
            if current.revision != replacement.expected_revision:
                raise ContextPolicyError("CONTEXT_POLICY_REVISION_CONFLICT")
            document = ContextPolicyDocument(schema_version=1, revision=current.revision + 1, config=replacement.config)
            policy = document.config.policy()
            temporary = None
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with tempfile.NamedTemporaryFile(mode="wb", dir=self.path.parent,
                        prefix=".context_policy-", suffix=".tmp", delete=False) as stream:
                    temporary = Path(stream.name)
                    stream.write(document.model_dump_json(indent=2).encode("utf-8") + b"\n")
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, self.path)
            except OSError:
                raise ContextPolicyError("CONTEXT_POLICY_WRITE_FAILED") from None
            finally:
                if temporary is not None:
                    try:
                        temporary.unlink(missing_ok=True)
                    except OSError:
                        pass
            self._applied, self._policy = document, policy
            return self.view()


def create_context_policy_router(settings: ContextPolicySettings) -> APIRouter:
    router = APIRouter()

    @router.get("/v1/context-policy")
    async def read_policy():
        return settings.view()

    @router.put("/v1/context-policy")
    async def replace_policy(request: Request):
        raw = await request.body()
        try:
            if len(raw) > 4096:
                raise ValueError()
            json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
            replacement = ReplaceContextPolicy.model_validate_json(raw)
        except (ValueError, UnicodeError):
            return JSONResponse(status_code=422, content={"error": {"code": "CONTEXT_POLICY_VALUE_INVALID"}})
        try:
            return settings.save_and_apply(replacement)
        except ContextPolicyError as exc:
            status = 409 if str(exc).endswith("REVISION_CONFLICT") else 503
            return JSONResponse(status_code=status, content={"error": {"code": str(exc)}})

    return router
