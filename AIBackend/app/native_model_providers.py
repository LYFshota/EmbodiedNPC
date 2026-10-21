"""Necessary text-only Gemini and Claude adapters, without agent/tool execution."""

from urllib.parse import quote

import httpx

from .model_api_config import valid_base_url
from .model_schema_projection import project_schema, unwrap_text, rejection_reason
from .providers import ModelResult, ModelUsage, ProviderError, ProviderHealth


def model_resource(model):
    # A resource prefix is protocol syntax, not a guess about the model's vendor.
    return "models/" + quote(model[7:] if model.startswith("models/") else model, safe="")


async def native_http(client, method, url, headers, prefix, payload=None):
    try:
        response = await client.request(method, url, headers=headers, json=payload)
    except httpx.TimeoutException:
        raise ProviderError(prefix + "_TIMEOUT") from None
    except httpx.HTTPError:
        raise ProviderError(prefix + "_UNAVAILABLE") from None
    if response.status_code >= 400:
        suffix = "AUTH_FAILED" if response.status_code in (401, 403) else (
            "RATE_LIMITED" if response.status_code == 429 else
            rejection_reason(response) if response.status_code in (400, 422) else "UNAVAILABLE")
        raise ProviderError(prefix + "_" + suffix)
    try:
        value = response.json()
        if not isinstance(value, dict):
            raise ValueError
        return value
    except (ValueError, TypeError):
        raise ProviderError(prefix + "_PROTOCOL_ERROR") from None


class NativeTextProvider:
    def __init__(self, *, config, transport=None):
        self.config = config
        self.base_url = valid_base_url(config.base_url)
        self.model = config.model
        self.name = config.provider_type
        self.transport = transport

    def __repr__(self):
        return "NativeTextProvider()"

    @property
    def prefix(self):
        return "GEMINI" if self.name == "gemini_native" else "ANTHROPIC"

    def headers(self):
        if self.name == "gemini_native":
            return {"x-goog-api-key": self.config.api_key or ""}
        return {"x-api-key": self.config.api_key or "", "anthropic-version": "2023-06-01"}

    def budget_payload(self, request):
        schema, wrapped = project_schema(request.response_schema, self.config.schema_profile)
        systems = [message.content for message in request.messages if message.role == "system"]
        messages = [message for message in request.messages if message.role != "system"]
        if not messages:
            raise ProviderError(self.prefix + "_REQUEST_INVALID")
        if self.name == "gemini_native":
            generation = {"maxOutputTokens": request.inference_options.max_output_tokens or 512}
            if self.config.send_temperature:
                generation["temperature"] = request.inference_options.temperature
            if self.config.top_p is not None:
                generation["topP"] = self.config.top_p
            if request.inference_options.think:
                if self.config.thinking_budget is None:
                    raise ProviderError("MODEL_THINKING_MAPPING_REQUIRED")
                generation["thinkingConfig"] = {"thinkingBudget": self.config.thinking_budget}
            if schema is not None:
                generation.update(responseMimeType="application/json", responseJsonSchema=schema)
            payload = {"contents": [{"role": "model" if m.role == "assistant" else "user",
                                     "parts": [{"text": m.content}]} for m in messages],
                       "generationConfig": generation}
            if systems:
                payload["systemInstruction"] = {"parts": [{"text": text} for text in systems]}
            url = self.base_url + "/" + model_resource(self.model) + ":generateContent"
        else:
            payload = {"model": self.model, "messages": [m.model_dump() for m in messages],
                       "max_tokens": request.inference_options.max_output_tokens or 512, "stream": False}
            if systems:
                payload["system"] = [{"type": "text", "text": text} for text in systems]
            if self.config.send_temperature:
                payload["temperature"] = request.inference_options.temperature
            if self.config.top_p is not None:
                payload["top_p"] = self.config.top_p
            if request.inference_options.think:
                budget = self.config.thinking_budget
                if budget is None or budget >= payload["max_tokens"]:
                    raise ProviderError("MODEL_THINKING_MAPPING_REQUIRED")
                payload["thinking"] = {"type": "enabled", "budget_tokens": budget}
            if schema is not None:
                payload["output_config"] = {"format": {"type": "json_schema", "schema": schema}}
            url = self.base_url + "/messages"
        return payload

    async def generate(self, request):
        _, wrapped = project_schema(request.response_schema, self.config.schema_profile)
        payload = self.budget_payload(request)
        url = (self.base_url + '/' + model_resource(self.model) + ':generateContent'
               if self.name == 'gemini_native' else self.base_url + '/messages')
        async with httpx.AsyncClient(timeout=self.config.timeout_seconds, trust_env=False,
                                     transport=self.transport) as client:
            body = await native_http(client, "POST", url, self.headers(), self.prefix, payload)
        try:
            if self.name == "gemini_native":
                candidate = body["candidates"][0]
                text = "".join(p["text"] for p in candidate["content"]["parts"]
                               if "text" in p and not p.get("thought", False))
                usage = body.get("usageMetadata") or {}
                prompt, completion = usage.get("promptTokenCount"), usage.get("candidatesTokenCount")
                reason = candidate.get("finishReason")
            else:
                text = "".join(p["text"] for p in body["content"] if p.get("type") == "text")
                usage = body.get("usage") or {}
                prompt, completion = usage.get("input_tokens"), usage.get("output_tokens")
                reason = body.get("stop_reason")
            if not text.strip() or any(n is not None and (isinstance(n, bool) or not isinstance(n, int) or n < 0)
                                       for n in (prompt, completion)):
                raise ValueError
            return ModelResult(text=unwrap_text(text, wrapped, request.response_schema, self.config.schema_profile), finish_reason=reason,
                               usage=ModelUsage(prompt_tokens=prompt, completion_tokens=completion))
        except (KeyError, IndexError, TypeError, ValueError, AttributeError):
            raise ProviderError(self.prefix + "_PROTOCOL_ERROR") from None

    async def health(self):
        try:
            async with httpx.AsyncClient(timeout=self.config.timeout_seconds, trust_env=False,
                                         transport=self.transport) as client:
                suffix = "/" + model_resource(self.model) if self.name == "gemini_native" else "/models"
                body = await native_http(client, "GET", self.base_url + suffix, self.headers(), self.prefix)
            if self.name == "gemini_native":
                found = bool(body["name"]) and "generateContent" in body["supportedGenerationMethods"]
            else:
                found = any(item["id"] == self.model for item in body["data"])
            detail = "" if found else self.prefix + "_MODEL_NOT_FOUND"
        except ProviderError as error:
            detail = str(error)
        except (KeyError, TypeError, ValueError, AttributeError):
            detail = self.prefix + "_PROTOCOL_ERROR"
        return ProviderHealth(provider=self.name, model=self.model, available=not detail, detail=detail)
