"""Deterministic conservative token estimates for bounded local-model prompts."""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Mapping


@dataclass(frozen=True)
class TokenEstimate:
    estimated_tokens: int
    estimated_chars: int
    component_breakdown: dict[str, int]


class ContextTokenEstimator:
    """Estimate Qwen prompt cost without adding a model-specific tokenizer dependency.

    CJK and other non-ASCII characters are charged one token each. ASCII word
    characters are charged at four characters per token, while JSON punctuation
    is charged at two characters per token. A safety multiplier absorbs boundary
    and tokenizer differences. Actual Ollama ``prompt_eval_count`` can later be
    compared with this estimate without changing the allocation interface.
    """

    def __init__(self, safety_factor: float = 1.15) -> None:
        if safety_factor < 1:
            raise ValueError("TOKEN_ESTIMATOR_SAFETY_FACTOR_INVALID")
        self.safety_factor = safety_factor

    def estimate_text(self, text: str) -> int:
        ascii_word = ascii_punctuation = non_ascii = 0
        for char in text:
            if ord(char) > 127:
                non_ascii += 1
            elif char.isalnum() or char.isspace() or char == "_":
                ascii_word += 1
            else:
                ascii_punctuation += 1
        raw = non_ascii + math.ceil(ascii_word / 4) + math.ceil(ascii_punctuation / 2)
        return max(1, math.ceil(raw * self.safety_factor)) if text else 0

    def estimate_components(self, components: Mapping[str, str]) -> TokenEstimate:
        breakdown = {name: self.estimate_text(value) for name, value in components.items()}
        return TokenEstimate(
            estimated_tokens=sum(breakdown.values()),
            estimated_chars=sum(len(value) for value in components.values()),
            component_breakdown=breakdown,
        )
