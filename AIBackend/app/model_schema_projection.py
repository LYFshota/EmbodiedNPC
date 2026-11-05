"""Explicit transport projections. Internal schemas and validators stay authoritative."""

from copy import deepcopy
import json
import logging
import re
from jsonschema import Draft202012Validator
from jsonschema.exceptions import best_match

from .providers import ProviderError
from .structured_output import extract_json_object


class ModelSchemaOutputError(ProviderError):
    """Keep the existing adapter code; expose only bounded, value-free details."""

    def __init__(self, category, *, field_path="$", keyword="envelope"):
        super().__init__("MODEL_SCHEMA_ENVELOPE_INVALID")
        self.diagnostic_category = category
        self.field_path = field_path
        self.validation_keyword = keyword
        logging.getLogger("embodiednpc.backend.model_output").warning(
            "Model output rejected category=%s field=%s validator=%s", category, field_path, keyword)


def _schema_error(error, schema, value):
    # A union's generic error hides useful details such as a missing commitment
    # flag. Pick only the schema-declared branch for the returned discriminator;
    # this affects diagnostics, never the full-schema acceptance decision.
    discriminator = schema.get("discriminator", {})
    if error.validator in ("oneOf", "anyOf") and isinstance(value, dict):
        tag = value.get(discriminator.get("propertyName"))
        reference = discriminator.get("mapping", {}).get(tag) if isinstance(tag, str) else None
        definitions = schema.get("$defs", {})
        if isinstance(reference, str) and reference.startswith("#/$defs/"):
            branch = definitions.get(reference.removeprefix("#/$defs/"))
            if branch is not None:
                detail = best_match(Draft202012Validator({**branch, "$defs": definitions}).iter_errors(value))
                if detail is not None:
                    error = detail
    # Dynamic map keys, rejected values and jsonschema's human-readable message
    # can contain private model output. Only developer-declared field names pass.
    names = set()
    def declared(node):
        if isinstance(node, dict):
            names.update(k for k in node.get("properties", {})
                         if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", k))
            for child in node.values():
                declared(child)
        elif isinstance(node, list):
            for child in node:
                declared(child)
    declared(schema)
    parts = list(error.absolute_path)
    if error.validator == "required" and isinstance(error.instance, dict):
        missing = next((k for k in error.validator_value if k not in error.instance and k in names), None)
        if missing is not None:
            parts.append(missing)
    path = "$" + "".join(f"[{part}]" if isinstance(part, int) else "." + (part if part in names else "*")
                          for part in parts[:8])
    keyword = error.validator if error.validator in {
        "required", "type", "enum", "const", "maxLength", "minLength", "additionalProperties",
        "oneOf", "anyOf", "maxItems", "minItems", "pattern", "minimum", "maximum"} else "schema"
    category = "RESPONSE_TOO_LONG" if keyword == "maxLength" else "RESPONSE_FIELDS_INVALID"
    return ModelSchemaOutputError(category, field_path=path, keyword=keyword)


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
    if original is None or profile in ("raw", "json_object"):
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


def unwrap_text(text, wrapped, original=None, profile="raw", *, allow_json_envelope=False):
    if original is None and not wrapped:
        return text
    if not wrapped and profile not in ("anthropic", "openai_strict", "portable_union", "json_object"):
        return text
    try:
        try:
            # Consumer-specific limits (cognition 4K, memory 32K) still apply
            # downstream, as before. This transport layer never owned that cap.
            value = (extract_json_object(text, max_chars=None, require_single_result=True)
                     if allow_json_envelope else json.loads(text))
        except ValueError:
            raise ModelSchemaOutputError("RESPONSE_JSON_INVALID", keyword="json") from None
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
            # JSON Object carries the original shape. Do not decode transport
            # key/value arrays that were never projected (e.g. pattern maps).
            if profile != "json_object":
                value = restore(value, original)
            error = best_match(Draft202012Validator(original).iter_errors(value))
            if error is not None:
                raise _schema_error(error, original, value)
        return json.dumps(value, ensure_ascii=False)
    except (ValueError, TypeError, KeyError):
        raise ModelSchemaOutputError("RESPONSE_SCHEMA_INVALID") from None


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
