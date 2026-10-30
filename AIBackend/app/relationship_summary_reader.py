"""Bounded, on-read projection of pair-scoped social interpretations."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

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


class RelationshipStateItemContext(BaseModel):
    """Model-visible current interpretation; raw anchors and superseded text stay private."""

    model_config = ConfigDict(extra="forbid", frozen=True)
    kind: Literal["EXPLICIT", "BOUNDARY", "TENTATIVE", "CONFLICT"]
    text: str = Field(min_length=1, max_length=160)
    # Only unresolved conflicts need the previous reading alongside the new one.
    alternative: str | None = Field(default=None, max_length=160)
    # Read-time provenance only; never persist it or expose raw session tokens to the model.
    source_sessions: tuple[str, ...] = Field(default=(), exclude=True)
    session_scope: Literal["current_session", "earlier_session", "mixed_sessions", "unknown"] | None = None


class RelationshipCurrentStateContext(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    items: tuple[RelationshipStateItemContext, ...] = Field(max_length=4)
    has_direct_interaction: bool
    coverage: Literal["SINCE_B1", "LEGACY_PARTIAL", "SOURCE_UNAVAILABLE"]
    late_episode_skipped: bool


class RelationshipSummary(BaseModel):
    """Derived interpretations with optional lineage, never identity or world authority."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    interpretations: tuple[str, ...]
    current_state: RelationshipCurrentStateContext | None = None
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
    """Prefer validated B1 current state; otherwise retain the legacy bounded window."""

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
        state = self.reflections.read_current_pair_state(observer, subject)
        if state.revision > 0:
            # A committed but now empty/invalid state must never resurrect old impressions.
            # Corrected/retracted predecessors are audit data, not simultaneous facts.
            current = RelationshipCurrentStateContext(
                items=tuple(RelationshipStateItemContext(kind=item.kind, text=item.text,
                    alternative=item.predecessor_text if item.kind == "CONFLICT" else None,
                    source_sessions=self.reflections.source_sessions(item.evidence))
                    for item in state.items),
                has_direct_interaction=state.interaction_evidence is not None or bool(state.items),
                coverage=state.coverage, late_episode_skipped=state.late_episode_skipped,
            )
            serialized = current.model_dump_json(exclude_none=True)
            chars, tokens = len(serialized), self.estimator.estimate_text(serialized)
            fits = defer_budget or (chars <= self.policy.max_chars and tokens <= self.policy.max_tokens)
            return RelationshipSummary(
                interpretations=(), current_state=current if fits else None,
                source_count=len(state.items) + int(state.interaction_evidence is not None),
                # The whole snapshot (including limited-coverage metadata) is one atomic selection.
                selected_count=1 if fits else 0, truncated=not fits,
                char_budget_used=chars if fits else 0, token_budget_used=tokens if fits else 0,
                policy_version="relationship-summary/2.0",
            )
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
        # B1 state or the bounded legacy pair window. Foreground allocation owns tokens;
        # the standalone reader retains its explicit local budgets for other callers.
        return self.get(observer, subject, defer_budget=True)
