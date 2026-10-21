"""Strict JSON-envelope parsing shared by structured model consumers."""

from __future__ import annotations

import json
import re


MAX_RAW_OUTPUT_CHARS = 4_000


class StructuredOutputError(ValueError):
    pass


def extract_json_object(raw_text: str, *, max_chars: int = MAX_RAW_OUTPUT_CHARS) -> dict:
    if len(raw_text) > max_chars:
        raise StructuredOutputError("MODEL_OUTPUT_TOO_LONG")
    fenced = re.fullmatch(r"\s*```(?:json)?\s*(.*?)\s*```\s*", raw_text, re.DOTALL | re.IGNORECASE)
    candidate = fenced.group(1) if fenced else raw_text.strip()
    offset = candidate.find("{")
    if offset < 0:
        raise StructuredOutputError("MODEL_JSON_MISSING")
    try:
        parsed, _ = json.JSONDecoder().raw_decode(candidate[offset:])
    except json.JSONDecodeError as exc:
        raise StructuredOutputError("MODEL_JSON_INVALID") from exc
    if not isinstance(parsed, dict):
        raise StructuredOutputError("MODEL_JSON_NOT_OBJECT")
    return parsed
