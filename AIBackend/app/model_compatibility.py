"""Bounded, evidence-based transport choices for the custom Chat API only.

No requests are made here. Learned choices are process-local, keyed by the entire
connection and generation config (including a credential digest), and never logged.
"""
from collections import OrderedDict
from dataclasses import dataclass, replace
import hashlib
import json
import re
from urllib.parse import urlsplit

from .providers import ProviderError


@dataclass(frozen=True)
class CompatibilityPolicy:
    schema_profile: str = "raw"
    response_format: str = "json_schema"
    token_parameter: str = "max_tokens"
    thinking_parameter: str = "none"
    send_temperature: bool = True


# These rules describe exact service endpoints, never model-name guesses. Other
# hosts (even when they expose the same model ID) do not inherit these controls.
def known_policy(base_url):
    u = urlsplit(base_url)
    # Explicit HTTPS :443 is the same official endpoint. Other ports and paths
    # remain unknown; never match a substring in an arbitrary gateway hostname.
    if u.scheme != "https" or u.port not in (None, 443):
        return CompatibilityPolicy()
    host, path = u.hostname or "", u.path.rstrip("/")
    if host == "api.deepseek.com" and path in ("", "/v1"):
        return CompatibilityPolicy(response_format="json_object", thinking_parameter="thinking")
    # Official pay-as-you-go/trial Model Studio endpoints. Schema mode is only
    # supported by a subset of its models; JSON Object is the common baseline.
    qwen_hosts = {"dashscope.aliyuncs.com", "dashscope-intl.aliyuncs.com",
                  "dashscope-us.aliyuncs.com", "cn-hongkong.dashscope.aliyuncs.com"}
    workspace = re.fullmatch(r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.(?:cn-beijing|ap-southeast-1|ap-northeast-1|eu-central-1|us-east-1|cn-hongkong)\.maas\.aliyuncs\.com", host)
    if path == "/compatible-mode/v1" and (host in qwen_hosts or workspace):
        return CompatibilityPolicy(response_format="json_object", thinking_parameter="enable_thinking")
    if host in ("open.bigmodel.cn", "api.z.ai") and path == "/api/paas/v4":
        # The official text Chat API documents text/json_object, not json_schema.
        return CompatibilityPolicy(response_format="json_object", thinking_parameter="thinking")
    rules = {
        ("api.moonshot.cn", "/v1"): ("raw", "thinking", "max_tokens"),
        ("api.siliconflow.cn", "/v1"): ("raw", "enable_thinking", "max_tokens"),
        ("api.openai.com", "/v1"): ("openai_strict", "none", "max_completion_tokens"),
    }
    if (host, path) in rules:
        schema, thinking, token = rules[(host, path)]
        return CompatibilityPolicy(schema_profile=schema, thinking_parameter=thinking, token_parameter=token)
    return CompatibilityPolicy()


_CACHE = OrderedDict()


def config_key(config):
    # Display name/timeout do not change payloads; a bounded probe may shorten its
    # deadline without separating its learned result from the saved connection.
    values = config.model_dump(mode="json", exclude={"display_name", "timeout_seconds"})
    return hashlib.sha256(json.dumps(values, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def initial_policy(config):
    known = known_policy(config.base_url or "")
    return CompatibilityPolicy(
        schema_profile=known.schema_profile if config.schema_profile == "auto" else ("raw" if config.schema_profile == "json_object" else config.schema_profile),
        response_format=known.response_format if config.schema_profile == "auto" else ("json_object" if config.schema_profile == "json_object" else "json_schema"),
        token_parameter=known.token_parameter if config.token_parameter == "auto" else config.token_parameter,
        thinking_parameter=known.thinking_parameter if config.thinking_parameter == "auto" else config.thinking_parameter,
        send_temperature=known.send_temperature if config.send_temperature is None else config.send_temperature)


def policy_for(config):
    return _CACHE.get(config_key(config), initial_policy(config))


def remember(config, policy):
    key = config_key(config)
    _CACHE[key] = policy
    _CACHE.move_to_end(key)
    while len(_CACHE) > 128:
        _CACHE.popitem(last=False)


def suggested_change(response, config, policy):
    """Only a specific unsupported-field response permits a paid probe retry."""
    if response.status_code not in (400, 422):
        return None
    from .model_schema_projection import rejection_reason
    if rejection_reason(response) == "CONTEXT_LENGTH_EXCEEDED":
        return None
    try:
        error = response.json()["error"]
        param = error.get("param") or ""
        message = str(error.get("message", "")).casefold()
        code = str(error.get("code", "")).casefold()
        if not isinstance(param, str):
            return None
        if code in ("invalid_api_key", "authentication_error", "rate_limit_exceeded"):
            return None
        # A rejected structured format (including a schema keyword unsupported by
        # that API) is evidence that this schema transport cannot carry our request.
        # Try JSON Object once, only inside a confirmed probe; the SAME original
        # schema is still enforced locally. A second rejection never loops.
        if config.schema_profile == "auto" and policy.response_format == "json_schema" and rejection_reason(response) == "SCHEMA_REJECTED":
            return replace(policy, response_format="json_object", schema_profile="raw")
        rejected = any(s in message for s in ("not supported", "unsupported", "not support", "not allowed", "must be", "only support", "invalid value")) or code in ("unsupported_parameter", "unsupported_value")
        if not rejected:
            return None
        if (config.token_parameter == "auto" and policy.token_parameter == "max_tokens"
                and (param == "max_tokens" or "max_tokens" in message) and "max_completion_tokens" in message):
            return replace(policy, token_parameter="max_completion_tokens")
        if config.send_temperature is None and policy.send_temperature and (param == "temperature" or "temperature" in message):
            return replace(policy, send_temperature=False)
    except (ValueError, TypeError, KeyError, AttributeError):
        pass
    return None


class CompatibilityRejection(ProviderError):
    def __init__(self, code, candidate):
        super().__init__(code)
        self.candidate = candidate


@dataclass
class ProbeBudget:
    # Shared across the contract cases. Retries only happen in an explicitly
    # confirmed probe; normal generate() never retries a rejected model request.
    remaining_retries: int = 3
    calls: int = 0


def report(config, policy, calls):
    return dict(scope="process", response_format=policy.response_format,
        schema_profile=policy.schema_profile, token_parameter=policy.token_parameter,
        thinking_parameter=policy.thinking_parameter, send_temperature=policy.send_temperature,
        api_calls=calls, strict_validation=True,
        unverified=["model_semantics", "world_execution", "long_term_memory"])
