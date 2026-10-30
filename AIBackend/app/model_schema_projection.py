"""Explicit transport projections. Internal schemas and validators stay authoritative."""

from copy import deepcopy
import json
from jsonschema import Draft202012Validator

from .providers import ProviderError


def _map_values(schema, profile):
    """Recognize only reversible typed maps, never discard named properties."""
    patterns = schema.get("patternProperties")
    if patterns is not None:
        if (not isinstance(patterns, dict) or len(patterns) != 1
                or schema.get("properties") or schema.get("additionalProperties") not in (None, False)):
            raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
        return next(iter(patterns.values()))
    if profile in ("anthropic", "openai_strict") and isinstance(schema.get("additionalProperties"), dict):
        if schema.get("properties"):
            raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
        return schema["additionalProperties"]
    return None


def project_schema(original, profile):
    if original is None or profile == "raw":
        return original, False
    definitions = original.get("$defs", {})
    dropped = {"minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum",
               "exclusiveMaximum", "multipleOf", "pattern", "format", "minItems", "maxItems"}

    def walk(schema, visiting=()):
        if not isinstance(schema, dict):
            raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
        schema = deepcopy(schema)
        if "$ref" in schema:
            ref = schema.pop("$ref")
            if not isinstance(ref, str) or not ref.startswith("#/$defs/") or ref in visiting:
                raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
            target = definitions.get(ref[len("#/$defs/"):])
            if target is None:
                raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
            return walk({**target, **schema}, (*visiting, ref))
        schema.pop("$defs", None)
        schema.pop("title", None)
        schema.pop("default", None)
        map_values = _map_values(schema, profile)
        if map_values is not None:
            key_constraints = {key: schema[key] for key in ("minProperties", "maxProperties", "propertyNames") if key in schema}
            if "patternProperties" in schema:
                key_constraints["key_patterns"] = list(schema["patternProperties"])
            return {"type": "array", "items": {"type": "object", "properties": {
                "key": {"type": "string"}, "value": walk(map_values, visiting)},
                "required": ["key", "value"], "additionalProperties": False},
                "description": "Object encoded as unique key/value entries; original key/value constraints validated locally. "
                    + schema.get("description", "") + " " + json.dumps(key_constraints, ensure_ascii=True)}
        discriminator = schema.pop("discriminator", None)
        if "oneOf" in schema:
            # The project's decision unions have disjoint literal tags. Refuse other unions.
            if not isinstance(discriminator, dict) or not discriminator.get("propertyName"):
                raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
            branches = [walk(item, visiting) for item in schema.pop("oneOf")]
            field = discriminator["propertyName"]
            tags = [b.get("properties", {}).get(field, {}).get("const",
                    (b.get("properties", {}).get(field, {}).get("enum") or [None])[0]) for b in branches]
            if any(not isinstance(tag, str) for tag in tags) or len(set(tags)) != len(tags):
                raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
            schema["anyOf"] = branches
        elif discriminator is not None:
            raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
        if "properties" in schema:
            schema["properties"] = {key: walk(value, visiting) for key, value in schema["properties"].items()}
            if profile == "openai_strict":
                schema["required"] = list(schema["properties"])
            if profile in ("openai_strict", "anthropic"):
                if schema.get("additionalProperties", False) is not False:
                    raise ProviderError("MODEL_SCHEMA_PROJECTION_UNSUPPORTED")
                schema["additionalProperties"] = False
        if "items" in schema:
            schema["items"] = walk(schema["items"], visiting)
        if isinstance(schema.get("additionalProperties"), dict):
            schema["additionalProperties"] = walk(schema["additionalProperties"], visiting)
        for key in ("anyOf", "allOf"):
            if key in schema:
                schema[key] = [walk(item, visiting) for item in schema[key]]
        if profile in ("anthropic", "openai_strict", "portable_union"):
            # GenerateContent responseJsonSchema has a narrower keyword set than
            # general JSON Schema. Keep supported numeric/array bounds there.
            unsupported = (dropped if profile != "portable_union" else
                {"minLength", "maxLength", "pattern", "multipleOf", "exclusiveMinimum", "exclusiveMaximum", "propertyNames"})
            constraints = {key: schema.pop(key) for key in tuple(schema) if key in unsupported}
            if constraints:
                schema["description"] = (schema.get("description", "") +
                    " Locally validated constraints: " + json.dumps(constraints, ensure_ascii=True)).strip()
        if profile in ("portable_union", "openai_strict") and "const" in schema:
            schema["enum"] = [schema.pop("const")]
        return schema

    projected = walk(original)
    wrapped = profile in ("openai_strict", "anthropic") and "anyOf" in projected
    if wrapped:
        projected = {"type": "object", "properties": {"result": projected},
                     "required": ["result"], "additionalProperties": False}
    return projected, wrapped


def unwrap_text(text, wrapped, original=None, profile="raw"):
    if original is None and not wrapped:
        return text
    if not wrapped and profile not in ("anthropic", "openai_strict", "portable_union"):
        return text
    try:
        value = json.loads(text)
        if wrapped:
            if not isinstance(value, dict) or set(value) != {"result"}:
                raise ValueError
            value = value["result"]
        definitions = (original or {}).get("$defs", {})
        def restore(data, schema):
            if "$ref" in schema:
                schema = {**definitions[schema["$ref"].removeprefix("#/$defs/")],
                          **{k: v for k, v in schema.items() if k != "$ref"}}
            branches = schema.get("oneOf", schema.get("anyOf", []))
            if branches:
                for branch in branches:
                    try:
                        candidate = restore(data, branch)
                        if Draft202012Validator({**branch, "$defs": definitions}).is_valid(candidate):
                            return candidate
                    except (ValueError, TypeError, KeyError):
                        pass
                raise ValueError
            map_values = _map_values(schema, profile)
            if map_values is not None:
                if not isinstance(data, list):
                    raise ValueError
                result = {}
                for entry in data:
                    if not isinstance(entry, dict) or set(entry) != {"key", "value"} or not isinstance(entry["key"], str):
                        raise ValueError
                    if entry["key"] in result:
                        raise ValueError
                    result[entry["key"]] = restore(entry["value"], map_values)
                return result
            if isinstance(data, dict) and "properties" in schema:
                return {k: restore(v, schema["properties"].get(k, {})) for k, v in data.items()}
            if isinstance(data, list) and "items" in schema:
                return [restore(v, schema["items"]) for v in data]
            return data
        if original is not None:
            value = restore(value, original)
            if not Draft202012Validator(original).is_valid(value):
                raise ValueError
        return json.dumps(value, ensure_ascii=False)
    except (ValueError, TypeError, KeyError):
        raise ProviderError("MODEL_SCHEMA_ENVELOPE_INVALID") from None


def rejection_reason(response):
    """Classify diagnostics without returning any upstream message or echoed credential."""
    try:
        error = response.json().get("error", {})
        message = " ".join(str(error.get(k, "")) for k in ("message", "param")).casefold()
        if (error.get("code") in ("context_length_exceeded", "context_window_exceeded")
                or error.get("type") == "context_length_exceeded"
                or any(phrase in message for phrase in ("maximum context length is", "context length exceeded", "prompt is too long"))
                or ("input token count" in message and "exceeds" in message)):
            return "CONTEXT_LENGTH_EXCEEDED"
        if any(word in message for word in ("schema", "discriminator", "response_format", "output_config")):
            return "SCHEMA_REJECTED"
        if any(word in message for word in ("temperature", "thinking", "reasoning_effort", "max_tokens", "max_completion_tokens")):
            return "PARAMETER_REJECTED"
    except (ValueError, TypeError, AttributeError):
        pass
    return "REQUEST_REJECTED"
