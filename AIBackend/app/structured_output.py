"""Strict JSON-envelope parsing shared by structured model consumers."""

from __future__ import annotations

import json
import re


MAX_RAW_OUTPUT_CHARS = 4_000


class StructuredOutputError(ValueError):
    pass


def extract_json_object(raw_text: str, *, max_chars: int | None = MAX_RAW_OUTPUT_CHARS,
                        require_single_result: bool = False) -> dict:
    if max_chars is not None and len(raw_text) > max_chars:
        raise StructuredOutputError("MODEL_OUTPUT_TOO_LONG")
    fenced = re.fullmatch(r"\s*```(?:json)?\s*(.*?)\s*```\s*", raw_text, re.DOTALL | re.IGNORECASE)
    candidate = fenced.group(1) if fenced else raw_text.strip()
    offset = candidate.find("{")
    if offset < 0:
        raise StructuredOutputError("MODEL_JSON_MISSING")
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise StructuredOutputError("MODEL_JSON_DUPLICATE_KEYS")
            result[key] = value
        return result

    def invalid_constant(_):
        raise StructuredOutputError("MODEL_JSON_INVALID")

    try:
        decoder = (json.JSONDecoder(object_pairs_hook=unique_object, parse_constant=invalid_constant)
                   if require_single_result else json.JSONDecoder())
        parsed, end = decoder.raw_decode(candidate[offset:])
    except json.JSONDecodeError as exc:
        raise StructuredOutputError("MODEL_JSON_INVALID") from exc
    if not isinstance(parsed, dict):
        raise StructuredOutputError("MODEL_JSON_NOT_OBJECT")
    if require_single_result and candidate[offset + end:].strip():
        raise StructuredOutputError("MODEL_JSON_MULTIPLE_RESULTS_OR_TRAILING_TEXT")
    return parsed
