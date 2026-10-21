"""Model protocol adapters used by the backend business layer."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Literal, Protocol

import httpx
from pydantic import BaseModel, ConfigDict, Field


class ProviderError(RuntimeError):
    """Raised when a provider cannot produce a usable model result."""


class ProviderHealth(BaseModel):
    provider: str
    model: str
    available: bool
    detail: str = ""


class ModelMessage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    role: Literal["system", "user", "assistant"]
    content: str = Field(min_length=1, max_length=100_000)


class InferenceOptions(BaseModel):
    model_config = ConfigDict(extra="forbid")

    temperature: float = Field(default=0, ge=0, le=2)
    think: bool = False
    max_output_tokens: int | None = Field(default=None, ge=1, le=32_768)
    # Internal capacity reservation; adapters never send this field as an API parameter.
    output_token_reserve: int | None = Field(default=None, ge=1, le=1_000_000)


class ModelRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    messages: tuple[ModelMessage, ...] = Field(min_length=1, max_length=32)
    response_schema: dict[str, Any] | None = None
    inference_options: InferenceOptions = Field(default_factory=InferenceOptions)


class ModelUsage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    prompt_tokens: int | None = Field(default=None, ge=0)
    completion_tokens: int | None = Field(default=None, ge=0)
    reasoning_tokens: int | None = Field(default=None, ge=0)


class ModelResult(BaseModel):
    model_config = ConfigDict(extra="forbid")

    text: str = Field(min_length=1)
    finish_reason: str | None = None
    usage: ModelUsage = Field(default_factory=ModelUsage)


class ModelProvider(Protocol):
    name: str
    model: str

    async def generate(self, request: ModelRequest) -> ModelResult:
        """Execute one non-streaming model request."""

    async def health(self) -> ProviderHealth:
        """Return provider availability without exposing credentials."""


ModelResponder = Callable[[ModelRequest], ModelResult]


@dataclass(frozen=True)
class MockProvider:
    """In-process adapter whose deterministic responder is supplied by the business layer."""

    responder: ModelResponder | None = None
    name: str = "mock"
    model: str = "fixed-greeting-v1"

    async def generate(self, request: ModelRequest) -> ModelResult:
        if self.responder is not None:
            return self.responder(request)
        from .model_requests import MockModelResponder

        return MockModelResponder()(request)

    async def health(self) -> ProviderHealth:
        return ProviderHealth(provider=self.name, model=self.model, available=True)


@dataclass(frozen=True)
class OllamaProvider:
    base_url: str
    model: str
    timeout_seconds: float
    context_length: int = 8192
    name: str = "ollama"
    transport: httpx.AsyncBaseTransport | None = field(default=None, repr=False, compare=False)

    def budget_payload(self, request: ModelRequest) -> dict:
        payload: dict[str, Any] = {
            "model": self.model,
            "stream": False,
            "think": request.inference_options.think,
            "options": {
                "temperature": request.inference_options.temperature,
                "num_ctx": self.context_length,
            },
            "messages": [message.model_dump() for message in request.messages],
        }
        if request.response_schema is not None:
            payload["format"] = request.response_schema
        if request.inference_options.max_output_tokens is not None:
            payload["options"]["num_predict"] = request.inference_options.max_output_tokens
        return payload

    async def generate(self, request: ModelRequest) -> ModelResult:
        payload = self.budget_payload(request)
        try:
            client_kwargs = {"transport": self.transport} if self.transport is not None else {}
            async with httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                         **client_kwargs) as client:
                response = await client.post(f"{self.base_url}/api/chat", json=payload)
                response.raise_for_status()
        except httpx.TimeoutException as exc:
            raise ProviderError("OLLAMA_TIMEOUT") from exc
        except httpx.HTTPError as exc:
            raise ProviderError("OLLAMA_UNAVAILABLE") from exc

        try:
            body = response.json()
            content = body["message"]["content"]
            finish_reason = body.get("done_reason")
            prompt_tokens = body.get("prompt_eval_count")
            completion_tokens = body.get("eval_count")
        except (KeyError, TypeError, ValueError) as exc:
            raise ProviderError("OLLAMA_PROTOCOL_ERROR") from exc
        if not isinstance(content, str) or not content.strip():
            raise ProviderError("OLLAMA_EMPTY_RESPONSE")
        try:
            return ModelResult(
                text=content,
                finish_reason=finish_reason if isinstance(finish_reason, str) else None,
                usage=ModelUsage(
                    prompt_tokens=prompt_tokens if isinstance(prompt_tokens, int) else None,
                    completion_tokens=completion_tokens if isinstance(completion_tokens, int) else None,
                ),
            )
        except ValueError as exc:
            raise ProviderError("OLLAMA_PROTOCOL_ERROR") from exc

    async def health(self) -> ProviderHealth:
        try:
            client_kwargs = {"transport": self.transport} if self.transport is not None else {}
            async with httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                         **client_kwargs) as client:
                response = await client.get(f"{self.base_url}/api/tags")
                response.raise_for_status()
            models = response.json()["models"]
            if not isinstance(models, list) or any(not isinstance(item, dict) for item in models):
                raise ValueError("invalid model list")

            def tagged(name: str) -> str:
                return name if ":" in name.rsplit("/", 1)[-1] else name + ":latest"

            names = {
                tagged(item[key])
                for item in models
                for key in ("name", "model")
                if isinstance(item.get(key), str)
            }
            available = tagged(self.model) in names
            return ProviderHealth(
                provider=self.name,
                model=self.model,
                available=available,
                detail="" if available else "OLLAMA_MODEL_NOT_FOUND",
            )
        except (KeyError, TypeError, ValueError):
            return ProviderHealth(
                provider=self.name, model=self.model, available=False, detail="OLLAMA_PROTOCOL_ERROR"
            )
        except httpx.HTTPError:
            return ProviderHealth(
                provider=self.name, model=self.model, available=False, detail="OLLAMA_UNAVAILABLE"
            )
