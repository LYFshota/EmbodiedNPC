"""Production DeepSeek Official Responses adapter for the ModelProvider contract."""

from __future__ import annotations

import math
import re
from urllib.parse import urlsplit

import httpx

from .providers import ModelRequest, ModelResult, ModelUsage, ProviderError, ProviderHealth


_SECRET_LIKE = re.compile(r"(?i)(bearer\s+|sk-[a-z0-9-]{8,}|api[_-]?key\s*[=:]|token\s*[=:])")


class DeepSeekOfficialResponsesProvider:
    name = "deepseek_official_responses"
    schema_name = "embodiednpc_cognitive_result"

    def __init__(self, *, base_url: str, api_key: str, model: str,
                 timeout_seconds: float = 60,
                 transport: httpx.AsyncBaseTransport | None = None) -> None:
        if not isinstance(base_url, str):
            raise ValueError("DEEPSEEK_CONFIG_INVALID")
        try:
            parts = urlsplit(base_url)
            port = parts.port
        except ValueError:
            raise ValueError("DEEPSEEK_CONFIG_INVALID") from None
        if (parts.scheme != "https" or not parts.hostname or parts.username or parts.password
                or parts.query or parts.fragment or parts.path not in ("", "/")
                or (port is not None and port == 0)
                or any(ord(char) < 33 for char in base_url)):
            raise ValueError("DEEPSEEK_CONFIG_INVALID")
        if (not isinstance(api_key, str) or not api_key.strip()
                or api_key != api_key.strip() or any(ord(char) < 33 for char in api_key)
                or not isinstance(model, str) or not model.strip()
                or model != model.strip() or any(ord(char) < 32 for char in model)
                or _SECRET_LIKE.search(model)
                or isinstance(timeout_seconds, bool)
                or not isinstance(timeout_seconds, (int, float))
                or not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 600):
            raise ValueError("DEEPSEEK_CONFIG_INVALID")
        self.base_url = base_url.rstrip("/")
        self._api_key = api_key
        self.model = model
        self.timeout_seconds = float(timeout_seconds)
        self._transport = transport

    def __repr__(self) -> str:
        return "DeepSeekOfficialResponsesProvider()"

    def _client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(timeout=self.timeout_seconds, trust_env=False,
                                 transport=self._transport)

    def _headers(self) -> dict[str, str]:
        return {"Authorization": "Bearer " + self._api_key}

    def _payload(self, request: ModelRequest) -> dict[str, object]:
        payload: dict[str, object] = {
            "model": self.model,
            "input": [{"role": message.role, "content": message.content}
                      for message in request.messages],
            "stream": False,
            "temperature": request.inference_options.temperature,
        }
        if request.inference_options.max_output_tokens is not None:
            payload["max_output_tokens"] = request.inference_options.max_output_tokens
        if not request.inference_options.think:
            payload["reasoning"] = {"effort": "none"}
        if request.response_schema is not None:
            payload["text"] = {"format": {
                "type": "json_schema", "name": self.schema_name,
                "schema": request.response_schema,
            }}
        return payload

    @staticmethod
    def _schema_rejected(response: httpx.Response, schema: dict | None) -> bool:
        """Recognize only a schema path confirmed against our own input schema."""
        if schema is None or response.status_code not in (400, 422):
            return False
        try:
            parameter = response.json()["error"]["param"]
        except (ValueError, TypeError, KeyError):
            return False
        prefix = "text.format.schema."
        if not isinstance(parameter, str) or not parameter.startswith(prefix):
            return False
        current: object = schema
        for part in parameter[len(prefix):].split("."):
            if not part or not isinstance(current, dict) or part not in current:
                return False
            current = current[part]
        return True

    def budget_payload(self, request: ModelRequest) -> dict:
        # Identical to the HTTP body; authorization headers are never included.
        return self._payload(request)

    @classmethod
    def _http_error(cls, response: httpx.Response, schema: dict | None = None) -> str:
        status = response.status_code
        if status in (401, 403):
            return "DEEPSEEK_AUTH_FAILED"
        if status == 404:
            return "DEEPSEEK_MODEL_NOT_FOUND"
        if status == 429:
            return "DEEPSEEK_RATE_LIMITED"
        if cls._schema_rejected(response, schema):
            return "DEEPSEEK_SCHEMA_UNSUPPORTED"
        if status in (400, 422):
            from .model_schema_projection import rejection_reason
            if rejection_reason(response) == "CONTEXT_LENGTH_EXCEEDED":
                return "DEEPSEEK_CONTEXT_LENGTH_EXCEEDED"
        if status in (400, 422):
            return "DEEPSEEK_BAD_REQUEST"
        return "DEEPSEEK_UNAVAILABLE"

    @staticmethod
    def _parse_response(response: httpx.Response) -> ModelResult:
        try:
            body = response.json()
            if not isinstance(body, dict):
                raise ValueError
            status = body["status"]
            if status == "failed":
                raise ProviderError("DEEPSEEK_RESPONSE_FAILED")
            if status not in ("completed", "incomplete"):
                raise ValueError
            output = body["output"]
            if not isinstance(output, list):
                raise ValueError
            parts: list[str] = []
            for item in output:
                if not isinstance(item, dict):
                    raise ValueError
                if item.get("type") != "message":
                    continue
                if item.get("role") != "assistant" or not isinstance(item.get("content"), list):
                    raise ValueError
                for content in item["content"]:
                    if not isinstance(content, dict):
                        raise ValueError
                    if content.get("type") == "output_text":
                        if not isinstance(content.get("text"), str):
                            raise ValueError
                        parts.append(content["text"])
            text = "".join(parts)
            if not text.strip():
                raise ProviderError("DEEPSEEK_EMPTY_RESPONSE")
            usage = body.get("usage")
            if usage is not None and not isinstance(usage, dict):
                raise ValueError
            usage = usage or {}
            prompt_tokens = usage.get("input_tokens")
            completion_tokens = usage.get("output_tokens")
            if any(value is not None and (isinstance(value, bool) or not isinstance(value, int) or value < 0)
                   for value in (prompt_tokens, completion_tokens)):
                raise ValueError
            finish_reason = status
            if status == "incomplete":
                details = body.get("incomplete_details")
                if isinstance(details, dict) and details.get("reason") in ("max_output_tokens", "content_filter"):
                    finish_reason += ":" + details["reason"]
            return ModelResult(
                text=text, finish_reason=finish_reason,
                usage=ModelUsage(prompt_tokens=prompt_tokens, completion_tokens=completion_tokens),
            )
        except ProviderError:
            raise
        except (ValueError, TypeError, KeyError):
            raise ProviderError("DEEPSEEK_PROTOCOL_ERROR") from None

    async def generate(self, request: ModelRequest) -> ModelResult:
        try:
            async with self._client() as client:
                response = await client.post(self.base_url + "/responses", headers=self._headers(),
                                             json=self._payload(request))
        except httpx.TimeoutException:
            raise ProviderError("DEEPSEEK_TIMEOUT") from None
        except httpx.HTTPError:
            raise ProviderError("DEEPSEEK_UNAVAILABLE") from None
        if response.status_code >= 400:
            raise ProviderError(self._http_error(response, request.response_schema))
        return self._parse_response(response)

    async def health(self) -> ProviderHealth:
        try:
            async with self._client() as client:
                response = await client.get(self.base_url + "/models", headers=self._headers())
        except httpx.TimeoutException:
            detail = "DEEPSEEK_UNAVAILABLE"
        except httpx.HTTPError:
            detail = "DEEPSEEK_UNAVAILABLE"
        else:
            if response.status_code >= 400:
                detail = self._http_error(response)
            else:
                try:
                    body = response.json()
                    if not isinstance(body, dict) or body.get("object") != "list" or not isinstance(body.get("data"), list):
                        raise ValueError
                    ids = set()
                    for item in body["data"]:
                        if not isinstance(item, dict) or not isinstance(item.get("id"), str):
                            raise ValueError
                        ids.add(item["id"])
                    detail = "" if self.model in ids else "DEEPSEEK_MODEL_NOT_FOUND"
                except (ValueError, TypeError):
                    detail = "DEEPSEEK_PROTOCOL_ERROR"
        return ProviderHealth(provider=self.name, model=self.model,
                              available=not detail, detail=detail)
