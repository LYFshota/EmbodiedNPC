"""Small Chat Completions adapter for ordinary OpenAI-compatible gateways."""

from __future__ import annotations

import httpx
import asyncio
import json
import logging

from .model_compatibility import (CompatibilityPolicy, CompatibilityRejection, policy_for, suggested_change, remember, config_key)
from .model_list_transport import get_model_list, transport_error_suffix
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
                 health_strategy: str = "models", compatibility_config=None, probe_budget=None) -> None:
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
        self._compatibility_config = compatibility_config
        self._probe_budget = probe_budget
        self._probe_policy = policy_for(compatibility_config) if probe_budget is not None and compatibility_config is not None else None
        self._transport_key = config_key(compatibility_config) if compatibility_config is not None else (self.name, id(self))

    def _policy(self):
        if self._probe_policy is not None:
            return self._probe_policy
        if self._compatibility_config is not None:
            return policy_for(self._compatibility_config)
        return CompatibilityPolicy(schema_profile=self.schema_profile,
            response_format="json_object" if self.schema_profile == "json_object" else "json_schema",
            token_parameter=self.token_parameter, thinking_parameter=self.thinking_parameter,
            send_temperature=self.send_temperature)

    def _thinking_style(self):
        return self._policy().thinking_parameter

    def cognitive_options(self, options: InferenceOptions, context_length: int) -> InferenceOptions:
        maximum = options.max_output_tokens or 1024
        if options.think and self._policy().token_parameter != "max_completion_tokens":
            # Chat max_tokens is not a verified total reasoning + output bound.
            # An arbitrary model name cannot establish a different protocol.
            raise ProviderError("OPENAI_COMPATIBLE_TOTAL_GENERATION_BOUND_UNVERIFIED")
        elif not options.think and self._observed_reasoning and self._thinking_style() == "none":
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

    def _payload_parts(self, request: ModelRequest, policy=None):
        policy = policy or self._policy()
        payload: dict[str, object] = {
            "model": self.model,
            "messages": [message.model_dump() for message in request.messages],
            "stream": False,
        }
        if policy.send_temperature:
            payload["temperature"] = request.inference_options.temperature
        if self.top_p is not None:
            payload["top_p"] = self.top_p
        if request.inference_options.max_output_tokens is not None:
            payload[policy.token_parameter] = request.inference_options.max_output_tokens
        style = policy.thinking_parameter
        if style == "enable_thinking":
            payload["enable_thinking"] = request.inference_options.think
        elif style == "thinking":
            payload["thinking"] = {"type": "enabled" if request.inference_options.think else "disabled"}
        elif style == "reasoning_effort":
            payload["reasoning_effort"] = (self.reasoning_effort or "low") if request.inference_options.think else "none"
        # Generic Chat Completions has no portable boolean thinking field.
        schema, wrapped = project_schema(request.response_schema, policy.schema_profile)
        if request.response_schema is not None:
            payload["response_format"] = {"type": "json_schema", "json_schema": {
                "name": "embodiednpc_result", "schema": schema,
            }}
            if policy.schema_profile == "openai_strict":
                payload["response_format"]["json_schema"]["strict"] = True
        if policy.response_format == "json_object" and request.response_schema is not None:
            payload["response_format"] = {"type": "json_object"}
        # Keep the same schema instructions in both automatic transports. Final
        # payload budgeting therefore includes JSON Object instructions up front.
        if request.response_schema is not None and (self.schema_profile in ("auto", "json_object") or policy.response_format == "json_object"):
            # The schema below describes the INTERNAL result, not a competing
            # wire format. Strict projections may wrap/encode that result.
            # Wording stays identical during a confirmed format fallback, so
            # its schema instruction was already included in payload budgeting.
            instruction = ("Return only JSON. When response_format provides a schema, follow that wire schema "
                "(including any result envelope or key/value arrays). Otherwise return the internal result directly. "
                "The internal result must conform exactly to this JSON Schema: "
                + json.dumps(request.response_schema, ensure_ascii=False, separators=(",", ":")))
            payload["messages"] = [{"role": "system", "content": instruction}, *payload["messages"]]
        return payload, wrapped

    def budget_payload(self, request: ModelRequest) -> dict:
        policy = self._policy()
        if request.inference_options.think and policy.token_parameter != "max_completion_tokens":
            raise ProviderError("OPENAI_COMPATIBLE_TOTAL_GENERATION_BOUND_UNVERIFIED")
        request._transport_state[self._transport_key] = policy
        return self._payload_parts(request, policy)[0]

    async def generate(self, request: ModelRequest) -> ModelResult:
        while True:
            try:
                result = await self._generate_once(request)
                if self._probe_budget is not None and self._compatibility_config is not None:
                    remember(self._compatibility_config, self._policy())
                return result
            except CompatibilityRejection as error:
                if self._probe_budget is None or self._probe_budget.remaining_retries <= 0:
                    raise ProviderError(str(error)) from None
                self._probe_budget.remaining_retries -= 1
                self._probe_policy = error.candidate

    async def _generate_once(self, request: ModelRequest) -> ModelResult:
        policy = self._probe_policy or request._transport_state.get(self._transport_key) or self._policy()
        if request.inference_options.think and policy.token_parameter != "max_completion_tokens":
            raise ProviderError("OPENAI_COMPATIBLE_TOTAL_GENERATION_BOUND_UNVERIFIED")
        payload, wrapped = self._payload_parts(request, policy)
        try:
            async with httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                         transport=self._transport) as client:
                if self._probe_budget is not None:
                    self._probe_budget.calls += 1
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
            if self._compatibility_config is not None:
                candidate = suggested_change(response, self._compatibility_config, policy)
                if candidate is not None:
                    raise CompatibilityRejection(code, candidate)
            raise ProviderError(code)
        try:
            body = response.json()
            choice = body["choices"][0]
            content = choice["message"].get("content")
            reason = choice.get("finish_reason")
            usage = body.get("usage") or {}
            if not isinstance(usage, dict):
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
            if not request.inference_options.think and has_reasoning:
                code = "THINKING_CONTROL_UNVERIFIED" if policy.thinking_parameter == "none" else "THINKING_DISABLE_NOT_HONORED"
                raise ProviderError("OPENAI_COMPATIBLE_" + code)
            reserve = request.inference_options.output_token_reserve
            if reserve is not None and completion_tokens is not None and completion_tokens > reserve:
                raise ProviderError("OPENAI_COMPATIBLE_OUTPUT_RESERVE_VIOLATED")
            if reason == "length":
                raise ProviderError("OPENAI_COMPATIBLE_OUTPUT_TRUNCATED")
            if not isinstance(content, str) or not content.strip():
                raise ValueError
            return ModelResult(
                text=unwrap_text(content, wrapped, request.response_schema,
                    "json_object" if policy.schema_profile == "raw" and self.schema_profile in ("auto", "json_object") else policy.schema_profile,
                    allow_json_envelope=self.schema_profile in ("auto", "json_object") and policy.schema_profile == "raw"),
                finish_reason=reason if isinstance(reason, str) else None,
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
                response = await get_model_list(client, self.base_url + "/models", headers=self._headers(),
                                                timeout_seconds=min(self.timeout_seconds, 5.0))
        except httpx.HTTPError as error:
            detail = "OPENAI_COMPATIBLE_" + transport_error_suffix(error)
        else:
            if response.status_code >= 400:
                detail = "OPENAI_COMPATIBLE_" + {
                    400: "BAD_REQUEST", 401: "AUTH_FAILED", 403: "AUTH_FAILED",
                    404: "ENDPOINT_NOT_FOUND", 429: "RATE_LIMITED",
                }.get(response.status_code, "UNAVAILABLE")
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
