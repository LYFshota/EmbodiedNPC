"""Bounded, on-read projection of pair-scoped social interpretations."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from pydantic import BaseModel, ConfigDict

from .context_token_estimator import ContextTokenEstimator
from .memory_consolidation_context import TrustedPersistentIdentity
from .relationship_reflection_repository import RelationshipReflectionRepository


RELATIONSHIP_SUMMARY_POLICY_VERSION = "relationship-summary/1.0"


@dataclass(frozen=True)
class RelationshipSummaryPolicy:
    source_window: int = 20
    max_items: int = 5
    max_chars: int = 2000
    max_tokens: int = 1200

    def __post_init__(self) -> None:
        if not 1 <= self.max_items <= 16 or not self.max_items <= self.source_window <= 64:
            raise ValueError("RELATIONSHIP_SUMMARY_WINDOW_INVALID")
        if not 1 <= self.max_chars <= 8000 or not 1 <= self.max_tokens <= 5000:
            raise ValueError("RELATIONSHIP_SUMMARY_BUDGET_INVALID")


class RelationshipSummary(BaseModel):
    """Derived interpretations with optional lineage, never identity or world authority."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    interpretations: tuple[str, ...]
    # Read-only lineage for cognition; identities and raw event payloads stay private.
    sources: tuple[dict[str, str | float], ...] = ()
    source_count: int
    selected_count: int
    truncated: bool
    char_budget_used: int
    token_budget_used: int
    policy_version: str = RELATIONSHIP_SUMMARY_POLICY_VERSION
    not_current_world_state: Literal[True] = True
    not_instruction: Literal[True] = True
    not_action_authorization: Literal[True] = True


class RelationshipSummaryReader:
    """Select recent unique interpretations, then return them in chronology."""

    def __init__(
        self,
        reflections: RelationshipReflectionRepository,
        policy: RelationshipSummaryPolicy | None = None,
        estimator: ContextTokenEstimator | None = None,
    ) -> None:
        self.reflections = reflections
        self.policy = policy or RelationshipSummaryPolicy()
        self.estimator = estimator or ContextTokenEstimator()

    def get(
        self, observer: TrustedPersistentIdentity, subject: TrustedPersistentIdentity,
        *, defer_budget: bool = False,
    ) -> RelationshipSummary:
        if not isinstance(observer, TrustedPersistentIdentity) or not isinstance(subject, TrustedPersistentIdentity):
            raise TypeError("TRUSTED_RELATIONSHIP_IDENTITIES_REQUIRED")
        sources = self.reflections.list_recent_interpretations(
            observer, subject, limit=self.policy.source_window,
        )
        selected: list[str] = []
        selected_sources: list[dict[str, str | float]] = []
        seen: set[str] = set()
        chars_used = tokens_used = 0
        # Repository returns the newest bounded window in chronological order.
        truncated = len(sources) == self.policy.source_window
        for record in reversed(sources):
            interpretation = record.interpretation
            assert interpretation is not None  # Repository returns validated INTERPRETATION rows.
            if interpretation in seen:
                continue
            seen.add(interpretation)
            if len(selected) == self.policy.max_items:
                truncated = True
                break
            item_chars = len(interpretation)
            item_tokens = self.estimator.estimate_text(interpretation)
            if not defer_budget and (chars_used + item_chars > self.policy.max_chars
                    or tokens_used + item_tokens > self.policy.max_tokens):
                truncated = True
                break
            selected.append(interpretation)
            if defer_budget:
                selected_sources.append(dict(reflection_id=record.reflection_id,
                    generated_at=record.generated_at, policy_version=record.policy_version))
            chars_used += item_chars
            tokens_used += item_tokens
        selected.reverse()
        selected_sources.reverse()
        return RelationshipSummary(
            interpretations=tuple(selected),
            sources=tuple(selected_sources),
            source_count=len(sources),
            selected_count=len(selected),
            truncated=truncated,
            char_budget_used=chars_used,
            token_budget_used=tokens_used,
        )

    def get_for_cognition(self, observer: TrustedPersistentIdentity,
                          subject: TrustedPersistentIdentity) -> RelationshipSummary:
        # Same bounded newest unique pair window. Foreground allocation owns tokens;
        # the standalone reader retains its explicit local budgets for other callers.
        return self.get(observer, subject, defer_budget=True)
