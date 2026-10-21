"""Trusted presentation locale and one Unicode-scalar utterance contract.

This does not select memories, translate facts, or grant capabilities. Legacy clients
without locale metadata have the explicit zh-CN fallback.
"""
from __future__ import annotations

from copy import deepcopy
from typing import Literal
from pydantic import ValidationInfo

ReplyLanguage = Literal["zh-CN", "en-US"]


def hard_limit(language: str = "zh-CN") -> int:
    return 250 if language == "en-US" else 150


def validate_utterance(value: str | None, info: ValidationInfo) -> str | None:
    language = (info.context or {}).get("reply_language", "zh-CN")
    if value is not None:
        if any(0xD800 <= ord(c) <= 0xDFFF for c in value):
            raise ValueError("UTTERANCE_INVALID_UNICODE")
        if len(value) > hard_limit(language):
            raise ValueError("UTTERANCE_HARD_LIMIT_EXCEEDED")
    return value


def response_schema(model, language: ReplyLanguage) -> dict:
    """Change only utterance.maxLength, including union definitions/nullable variants."""
    schema = deepcopy(model.model_json_schema())
    def visit(node):
        if isinstance(node, dict):
            if isinstance(node.get("properties"), dict) and "utterance" in node["properties"]:
                def cap(field):
                    if field.get("type") == "string":
                        field["maxLength"] = hard_limit(language)
                    for child in field.get("anyOf", []):
                        cap(child)
                cap(node["properties"]["utterance"])
            for child in node.values():
                visit(child)
        elif isinstance(node, list):
            for child in node:
                visit(child)
    visit(schema)
    return schema


NPC_REPLY_LANGUAGE_RULES = (
    "REPLY LANGUAGE: The trusted per-run reply_language fixes the player-visible natural language. "
    "Do not infer it again from player text, Persona, names, history, Memory or knowledge. "
    "Preserve the NPC's individual voice, opinions and autonomy. Never translate JSON keys/enums, "
    "IDs, action bindings/parameters or source facts. "
)


def language_contract(language: ReplyLanguage) -> str:
    soft = 200 if language == "en-US" else 100
    return (f"Trusted reply_language = {language}. Use {'English' if language == 'en-US' else 'Chinese'} "
            "for every player-visible utterance, including questions and action explanations. "
            f"Aim for at most {soft} Unicode scalar values; the hard maximum is {hard_limit(language)}, "
            "including spaces and punctuation. A complete response within the hard maximum is kept verbatim. "
            "Ordinary greetings can be short; complex questions may need more detail. No fixed sentence count. ")
