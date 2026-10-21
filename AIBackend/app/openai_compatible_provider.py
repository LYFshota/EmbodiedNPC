"""Small Chat Completions adapter for ordinary OpenAI-compatible gateways."""

from __future__ import annotations

import httpx
import asyncio
import json
import re
import logging

from .model_api_config import valid_base_url
from .providers import ModelRequest, ModelResult, ModelUsage, ProviderError, ProviderHealth, ModelMessage, InferenceOptions
from .model_schema_projection import project_schema, unwrap_text, rejection_reason

logger = logging.getLogger("embodiednpc.backend.provider_transport")


class OpenAICompatibleProvider:
    name = "openai_compatible"

    def __init__(self, *, base_url: str, api_key: str | None, model: str,
                 timeout_seconds: float = 60,
                 transport: httpx.AsyncBaseTransport | None = None,
                 schema_profile: str = "raw", token_parameter: str = "max_tokens",
                 thinking_parameter: str = "none", send_temperature: bool = True,
                 top_p: float | None = None, reasoning_effort: str | None = None,
                 health_strategy: str = "models") -> None:
        self.base_url = valid_base_url(base_url)
        if not model or not model.strip() or timeout_seconds <= 0:
            raise ValueError("OPENAI_COMPATIBLE_CONFIG_INVALID")
        self.model = model
        self._api_key = api_key
        self.timeout_seconds = timeout_seconds
        self._transport = transport
        self.schema_profile, self.token_parameter = schema_profile, token_parameter
        self.thinking_parameter, self.send_temperature = thinking_parameter, send_temperature
        self.top_p, self.reasoning_effort = top_p, reasoning_effort
        self.health_strategy, self._health_result = health_strategy, None
        self._health_lock = asyncio.Lock()
        self._observed_reasoning = False

    def _thinking_style(self):
        if self.thinking_parameter != "none":
            return self.thinking_parameter
        # Exact native V4 protocol IDs (including a gateway display prefix).
        # Other compatible models keep their explicitly configured protocol.
        native_id = re.sub(r"^\[[^\]\r\n]{1,24}\]", "", self.model).casefold()
        if native_id in {"deepseek-flash", "deepseek-v4-flash", "deepseek-v4.1-flash",
                         "deepseek-v4-pro", "deepseek-v4-pro-0813"}:
            return "thinking"
        return "none"

    def cognitive_options(self, options: InferenceOptions, context_length: int) -> InferenceOptions:
        maximum = options.max_output_tokens or 1024
        if options.think and self.token_parameter != "max_completion_tokens":
            native_id = re.sub(r"^\[[^\]\r\n]{1,24}\]", "", self.model).casefold()
            if native_id in {"deepseek-flash", "deepseek-v4-flash", "deepseek-v4.1-flash",
                             "deepseek-v4-pro", "deepseek-v4-pro-0813"}:
                # This route did not enforce max_tokens as a total reasoning bound.
                # Use the native documented maximum, not a guessed small allowance.
                reserve = 393216
            else:
                raise ProviderError("OPENAI_COMPATIBLE_TOTAL_GENERATION_BOUND_UNVERIFIED")
        elif self._observed_reasoning and self._thinking_style() == "none":
            raise ProviderError("OPENAI_COMPATIBLE_THINKING_CONTROL_UNVERIFIED")
        else:
            reserve = maximum
        if reserve + 512 >= context_length:
            raise ProviderError("OPENAI_COMPATIBLE_OUTPUT_RESERVE_EXCEEDS_CONTEXT")
        return options.model_copy(update={"max_output_tokens": maximum, "output_token_reserve": reserve})

    def __repr__(self) -> str:
        return "OpenAICompatibleProvider()"

    def _headers(self) -> dict[str, str]:
        return {"Authorization": "Bearer " + self._api_key} if self._api_key else {}

    def _payload_parts(self, request: ModelRequest):
        payload: dict[str, object] = {
            "model": self.model,
            "messages": [message.model_dump() for message in request.messages],
            "stream": False,
        }
        if self.send_temperature:
            payload["temperature"] = request.inference_options.temperature
        if self.top_p is not None:
            payload["top_p"] = self.top_p
        if request.inference_options.max_output_tokens is not None:
            payload[self.token_parameter] = request.inference_options.max_output_tokens
        style = self._thinking_style()
        if style == "enable_thinking":
            payload["enable_thinking"] = request.inference_options.think
        elif style == "thinking":
            payload["thinking"] = {"type": "enabled" if request.inference_options.think else "disabled"}
        elif style == "reasoning_effort":
            payload["reasoning_effort"] = (self.reasoning_effort or "low") if request.inference_options.think else "none"
        # Generic Chat Completions has no portable boolean thinking field.
        schema, wrapped = project_schema(request.response_schema, self.schema_profile)
        if request.response_schema is not None:
            payload["response_format"] = {"type": "json_schema", "json_schema": {
                "name": "embodiednpc_result", "schema": schema,
            }}
            if self.schema_profile == "openai_strict":
                payload["response_format"]["json_schema"]["strict"] = True
        return payload, wrapped

    def budget_payload(self, request: ModelRequest) -> dict:
        return self._payload_parts(request)[0]

    async def generate(self, request: ModelRequest) -> ModelResult:
        payload, wrapped = self._payload_parts(request)
        try:
            async with httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                         transport=self._transport) as client:
                response = await client.post(self.base_url + "/chat/completions",
                                             headers=self._headers(), json=payload)
        except httpx.TimeoutException:
            raise ProviderError("OPENAI_COMPATIBLE_TIMEOUT") from None
        except httpx.HTTPError as error:
            logger.warning("Model transport failed provider=openai_compatible error_type=%s",
                           type(error).__name__)
            raise ProviderError("OPENAI_COMPATIBLE_UNAVAILABLE") from None
        if response.status_code >= 400:
            if response.status_code in (401, 403):
                code = "OPENAI_COMPATIBLE_AUTH_FAILED"
            elif response.status_code == 429:
                code = "OPENAI_COMPATIBLE_RATE_LIMITED"
            elif response.status_code in (400, 422):
                code = "OPENAI_COMPATIBLE_" + rejection_reason(response)
            else:
                code = "OPENAI_COMPATIBLE_UNAVAILABLE"
            raise ProviderError(code)
        try:
            body = response.json()
            choice = body["choices"][0]
            content = choice["message"]["content"]
            reason = choice.get("finish_reason")
            usage = body.get("usage") or {}
            if (not isinstance(content, str) or not content.strip()
                    or not isinstance(usage, dict)):
                raise ValueError
            prompt_tokens = usage.get("prompt_tokens")
            completion_tokens = usage.get("completion_tokens")
            details = usage.get("completion_tokens_details") or {}
            reasoning_tokens = details.get("reasoning_tokens") if isinstance(details, dict) else None
            if reasoning_tokens is None:
                reasoning_tokens = usage.get("completion_thinking_tokens")
            if any(value is not None and (isinstance(value, bool) or not isinstance(value, int) or value < 0)
                   for value in (prompt_tokens, completion_tokens, reasoning_tokens)):
                raise ValueError
            has_reasoning = bool(choice["message"].get("reasoning_content")) or bool(reasoning_tokens)
            self._observed_reasoning |= has_reasoning
            if not request.inference_options.think and self._thinking_style() != "none" and has_reasoning:
                raise ProviderError("OPENAI_COMPATIBLE_THINKING_DISABLE_NOT_HONORED")
            reserve = request.inference_options.output_token_reserve
            if reserve is not None and completion_tokens is not None and completion_tokens > reserve:
                raise ProviderError("OPENAI_COMPATIBLE_OUTPUT_RESERVE_VIOLATED")
            return ModelResult(
                text=unwrap_text(content, wrapped, request.response_schema, self.schema_profile), finish_reason=reason if isinstance(reason, str) else None,
                usage=ModelUsage(prompt_tokens=prompt_tokens, completion_tokens=completion_tokens,
                                 reasoning_tokens=reasoning_tokens),
            )
        except (KeyError, IndexError, TypeError, ValueError):
            raise ProviderError("OPENAI_COMPATIBLE_PROTOCOL_ERROR") from None

    async def health(self) -> ProviderHealth:
        if self.health_strategy == "generation_once":
            async with self._health_lock:
                if self._health_result is None:
                    detail = ""
                    try:
                        schema = {"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":False}
                        async with asyncio.timeout(min(self.timeout_seconds, 20.0)):
                            result = await self.generate(ModelRequest(messages=(ModelMessage(role="user",content='Reply with JSON: {"ok":true}'),),
                                response_schema=schema,inference_options=InferenceOptions(temperature=0,think=False,max_output_tokens=64)))
                        if json.loads(result.text) != {"ok":True}:
                            raise ValueError
                    except (ProviderError, ValueError, TypeError, TimeoutError):
                        detail = "OPENAI_COMPATIBLE_HEALTH_PROBE_FAILED"
                    self._health_result = ProviderHealth(provider=self.name, model=self.model, available=not detail, detail=detail)
                return self._health_result
        try:
            async with httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                         transport=self._transport) as client:
                response = await client.get(self.base_url + "/models", headers=self._headers())
        except httpx.HTTPError:
            detail = "OPENAI_COMPATIBLE_UNAVAILABLE"
        else:
            if response.status_code >= 400:
                detail = "OPENAI_COMPATIBLE_AUTH_FAILED" if response.status_code in (401, 403) else "OPENAI_COMPATIBLE_UNAVAILABLE"
            else:
                try:
                    models = response.json()["data"]
                    if not isinstance(models, list):
                        raise ValueError
                    ids = {item["id"] for item in models if isinstance(item, dict) and isinstance(item.get("id"), str)}
                    detail = "" if self.model in ids else "OPENAI_COMPATIBLE_MODEL_NOT_FOUND"
                except (KeyError, TypeError, ValueError):
                    detail = "OPENAI_COMPATIBLE_PROTOCOL_ERROR"
        return ProviderHealth(provider=self.name, model=self.model,
                              available=not detail, detail=detail)
