"""Deterministic atomic-group allocation for cognitive prompt context."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Literal, Mapping

from pydantic import BaseModel, ConfigDict, Field

from .context_token_estimator import ContextTokenEstimator


ContextComponentCategory = Literal["recent", "summary", "evidence", "other"]


@dataclass(frozen=True)
class ContextBudgetPolicy:
    name: str
    max_context_tokens: int = 8192
    input_hard_cap: int = 6200
    response_reserve: int = 1024
    estimation_reserve: int = 968
    recent_target_ceiling: int = 1600
    summary_ceiling: int = 800
    evidence_ceiling: int = 0
    recent_hard_minimum_turns: int = 2

    def __post_init__(self) -> None:
        if min(
            self.max_context_tokens, self.input_hard_cap, self.response_reserve,
            self.estimation_reserve, self.recent_target_ceiling,
            self.summary_ceiling, self.recent_hard_minimum_turns,
        ) < 0:
            raise ValueError("CONTEXT_BUDGET_POLICY_INVALID")
        if self.input_hard_cap + self.response_reserve + self.estimation_reserve > self.max_context_tokens:
            raise ValueError("CONTEXT_BUDGET_RESERVES_EXCEED_MODEL_CONTEXT")

    @classmethod
    def pass1(cls) -> "ContextBudgetPolicy":
        return cls(name="pass1")

    @classmethod
    def pass2(cls) -> "ContextBudgetPolicy":
        return cls(
            name="pass2", input_hard_cap=5800, estimation_reserve=1368,
            recent_target_ceiling=1200, summary_ceiling=600, evidence_ceiling=1800,
        )

    @classmethod
    def cognition(cls, max_context_tokens: int, response_reserve: int, *,
                  estimation_reserve: int | None = None) -> "ContextBudgetPolicy":
        """Online engineering margin, pending provider tokenizer/usage calibration.

        Reserve 3/32 of the effective window (rounded up), with a 768-token
        floor for small windows. This is additional to the estimator's 1.15
        multiplier and the provider's output/thinking reserve, not a certified
        upper bound on tokenizer error. The override is an internal calibration
        seam; no user settings or background-task policies change here.
        """
        margin = (max(768, (max_context_tokens * 3 + 31) // 32)
                  if estimation_reserve is None else estimation_reserve)
        input_cap = max(0, max_context_tokens - response_reserve - margin)
        return cls(name="cognition", max_context_tokens=max_context_tokens,
            input_hard_cap=input_cap, response_reserve=response_reserve,
            estimation_reserve=margin, recent_target_ceiling=input_cap,
            summary_ceiling=input_cap, evidence_ceiling=input_cap)

    @classmethod
    def consolidation(cls) -> "ContextBudgetPolicy":
        """C1 exact-evidence budget with a reserved future consolidation response."""
        return cls(
            name="memory-consolidation-v2", input_hard_cap=6000,
            response_reserve=1400, estimation_reserve=792,
            recent_target_ceiling=0, summary_ceiling=0, evidence_ceiling=0,
            recent_hard_minimum_turns=0,
        )


@dataclass(frozen=True)
class AtomicContextGroup:
    group_id: str
    category: ContextComponentCategory
    serialized_content: str
    priority: int
    sequence: int
    fallback_for: str | None = None


class BudgetAllocation(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    policy_name: str
    input_hard_cap: int
    reserved_output_tokens: int
    reserved_estimation_tokens: int
    component_costs: dict[str, int]
    included_components: tuple[str, ...]
    dropped_components: tuple[str, ...]
    drop_reasons: dict[str, str] = Field(default_factory=dict)
    trim_reason: str
    summary_budget: int
    recent_budget: int
    evidence_budget: int
    estimated_total: int
    estimated_chars: int
    actual_prompt_chars: int = Field(default=0, ge=0)
    budget_exhausted: bool
    safe_failure: bool
    actual_prompt_tokens: int | None = Field(default=None, ge=0)


class ContextBudgetExceeded(ValueError):
    def __init__(self, allocation: BudgetAllocation) -> None:
        super().__init__("COGNITIVE_PROMPT_BUDGET_EXCEEDED")
        self.allocation = allocation


class ContextBudgetAllocator:
    """Select complete optional groups while preserving all mandatory prompt data."""

    def __init__(self, policy: ContextBudgetPolicy,
                 estimator: ContextTokenEstimator | None = None) -> None:
        self.policy = policy
        self.estimator = estimator or ContextTokenEstimator()

    def allocate(
        self,
        mandatory_components: Mapping[str, str],
        optional_groups: tuple[AtomicContextGroup, ...],
        render: Callable[[tuple[str, ...]], str],
    ) -> BudgetAllocation:
        mandatory_rendered = render(())
        mandatory_tokens = self.estimator.estimate_text(mandatory_rendered)
        self._mandatory_tokens = mandatory_tokens
        self._groups = optional_groups
        component_costs = {
            name: self.estimator.estimate_text(value)
            for name, value in mandatory_components.items()
        }
        group_costs = {
            group.group_id: self.estimator.estimate_text(group.serialized_content)
            for group in optional_groups
        }
        component_costs.update(group_costs)
        if mandatory_tokens > self.policy.input_hard_cap:
            return self._result(
                component_costs, (), tuple(group_costs), mandatory_rendered,
                "mandatory_context_exceeds_budget", True, True,
                {name: "mandatory_context_exceeds_budget" for name in group_costs},
            )

        included: list[str] = []
        dropped: list[str] = []
        drop_reasons: dict[str, str] = {}
        category_used = {"recent": 0, "summary": 0, "evidence": 0, "other": 0}
        category_caps = {
            "recent": self.policy.recent_target_ceiling,
            "summary": self.policy.summary_ceiling,
            "evidence": self.policy.evidence_ceiling,
            "other": self.policy.input_hard_cap,
        }
        ordered = sorted(optional_groups, key=lambda value: (value.priority, value.sequence, value.group_id))
        current_rendered = mandatory_rendered
        for group in ordered:
            if group.fallback_for in included:
                continue  # Its complete local block already supplies this speech.
            cost = group_costs[group.group_id]
            if category_used[group.category] + cost > category_caps[group.category]:
                dropped.append(group.group_id)
                drop_reasons[group.group_id] = "category_cap"
                continue
            trial_ids = (*included, group.group_id)
            trial_rendered = render(trial_ids)
            if self.estimator.estimate_text(trial_rendered) > self.policy.input_hard_cap:
                dropped.append(group.group_id)
                drop_reasons[group.group_id] = "global_input_cap"
                continue
            included.append(group.group_id)
            category_used[group.category] += cost
            current_rendered = trial_rendered
        return self._result(
            component_costs, tuple(included), tuple(dropped), current_rendered,
            "optional_groups_trimmed" if dropped else "within_budget", bool(dropped), False,
            drop_reasons,
        )

    def require(self, allocation: BudgetAllocation) -> BudgetAllocation:
        if allocation.safe_failure:
            raise ContextBudgetExceeded(allocation)
        return allocation

    def _result(self, component_costs: dict[str, int], included: tuple[str, ...],
                dropped: tuple[str, ...], rendered: str, reason: str,
                exhausted: bool, safe_failure: bool,
                drop_reasons: dict[str, str] | None = None) -> BudgetAllocation:
        result = BudgetAllocation(
            policy_name=self.policy.name,
            input_hard_cap=self.policy.input_hard_cap,
            reserved_output_tokens=self.policy.response_reserve,
            reserved_estimation_tokens=self.policy.estimation_reserve,
            component_costs=component_costs,
            included_components=included,
            dropped_components=dropped,
            drop_reasons=drop_reasons or {},
            trim_reason=reason,
            summary_budget=self.policy.summary_ceiling,
            recent_budget=self.policy.recent_target_ceiling,
            evidence_budget=self.policy.evidence_ceiling,
            estimated_total=self.estimator.estimate_text(rendered),
            estimated_chars=len(rendered),
            budget_exhausted=exhausted,
            safe_failure=safe_failure,
        )
        from .context_capacity import record_allocation
        record_allocation(result, self._mandatory_tokens, self._groups)
        return result
