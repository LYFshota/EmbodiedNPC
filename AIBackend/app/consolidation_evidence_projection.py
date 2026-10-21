"""Deterministic Memory model presentation; authoritative evidence stays intact."""
from __future__ import annotations

from dataclasses import dataclass, replace
import json

from .context_budget_allocator import (
    AtomicContextGroup, BudgetAllocation, ContextBudgetAllocator, ContextBudgetPolicy,
)
from .context_token_estimator import ContextTokenEstimator
from .consolidation_model_request import consolidation_request, serialize_consolidation_request
from .memory_consolidation_context import (
    ConsolidationEvidenceEvent, MemoryConsolidationContext, MemoryConsolidationContextError,
)


OPTIONAL_EVENT_TYPES = frozenset({"observation", "reported_observation", "social_response"})


@dataclass(frozen=True)
class ConsolidationEvidenceProjection:
    visible_events: tuple[ConsolidationEvidenceEvent, ...]
    visible_event_ids: tuple[str, ...]
    budget_allocation: BudgetAllocation


class ConsolidationEvidenceProjector:
    def __init__(self, policy: ContextBudgetPolicy | None = None,
                 estimator: ContextTokenEstimator | None = None, *, inference_options=None,
                 payload_builder=None) -> None:
        policy = policy or ContextBudgetPolicy.consolidation()
        # The full final-request cap remains the limiting budget for observations.
        self.allocator = ContextBudgetAllocator(
            replace(policy, evidence_ceiling=policy.input_hard_cap), estimator)
        self.inference_options = inference_options
        self.payload_builder = payload_builder or serialize_consolidation_request

    def project(self, context: MemoryConsolidationContext) -> ConsolidationEvidenceProjection:
        core = tuple(e for e in context.evidence_events if e.event_type not in OPTIONAL_EVENT_TYPES)
        optional_events = [e for e in context.evidence_events if e.event_type in OPTIONAL_EVENT_TYPES]
        grouped, consumed = {}, set()
        for event in optional_events:
            if event.event_id in consumed:
                continue
            group = [event]
            if event.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote':
                group += [reply for reply in optional_events if reply.event_type == 'social_response'
                          and reply.structured_payload.get('parent_source_event_id') == event.structured_payload.get('source_event_id')
                          and reply.structured_payload.get('trace_id') == event.structured_payload.get('trace_id')]
            grouped[event.event_id] = tuple(group)
            consumed.update(e.event_id for e in group)
        optional = tuple(AtomicContextGroup(
            group_id=key, category="evidence", priority=0, sequence=group[0].experience_seq,
            serialized_content=json.dumps(group[0].model_dump(mode="json") if len(group) == 1 else
                                          [e.model_dump(mode="json") for e in group], ensure_ascii=False,
                                          sort_keys=True, separators=(",", ":")),
        ) for key, group in grouped.items())
        core_ids = {e.event_id for e in core}
        references = context.reference_events
        if references:
            optional = (*optional, AtomicContextGroup(group_id='previous_interaction',
                category='evidence', priority=1, sequence=-1,
                serialized_content=json.dumps([e.model_dump(mode='json') for e in references], ensure_ascii=False)))

        def selected(ids: tuple[str, ...]) -> tuple[ConsolidationEvidenceEvent, ...]:
            visible = core_ids | {e.event_id for key in ids for e in grouped.get(key, ())}
            return tuple(e for e in context.evidence_events if e.event_id in visible)

        def render(ids: tuple[str, ...]) -> str:
            return self.payload_builder(consolidation_request(context, selected(ids),
                inference_options=self.inference_options,
                reference_events=references if 'previous_interaction' in ids else ()))

        allocation = self.allocator.allocate({"core_request": render(())}, optional, render)
        if allocation.safe_failure:
            raise MemoryConsolidationContextError("CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED")
        events = selected(allocation.included_components)
        visible_references = references if 'previous_interaction' in allocation.included_components else ()
        return ConsolidationEvidenceProjection(events,
            tuple(e.event_id for e in (*visible_references, *events)), allocation)
