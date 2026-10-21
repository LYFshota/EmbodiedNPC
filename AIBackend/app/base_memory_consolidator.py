"""One bounded, preemptible background proposal call for a resolved C1 context."""

from __future__ import annotations

import logging

from pydantic import ValidationError

from .context_budget_allocator import ContextBudgetPolicy
from .context_token_estimator import ContextTokenEstimator
from .local_model_scheduler import PrioritizedModelExecutor
from .memory_consolidation_context import MemoryConsolidationContext
from .memory_consolidation_models import MemoryConsolidationResponse
from .providers import ModelProvider, ProviderError
from .providers import InferenceOptions
from .consolidation_model_request import (
    consolidation_request, consolidation_system_prompt, serialize_consolidation_request,
)
from .consolidation_evidence_projection import ConsolidationEvidenceProjector, ConsolidationEvidenceProjection
from .task_capacity import task_policy, task_options, payload_text, request_fits, check_result
from .structured_output import StructuredOutputError, extract_json_object


class MemoryConsolidationError(RuntimeError):
    pass


class BaseMemoryConsolidator:
    def __init__(self, provider: ModelProvider, executor: PrioritizedModelExecutor, *,
                 budget_policy: ContextBudgetPolicy | None = None,
                 estimator: ContextTokenEstimator | None = None, context_length: int | None = None) -> None:
        self.provider = provider
        self.executor = executor
        self.budget_policy = budget_policy or task_policy(provider, 'consolidation', context_length)
        self.estimator = estimator or ContextTokenEstimator()
        self.inference_options = (InferenceOptions(temperature=0, think=False, max_output_tokens=1200)
            if self.budget_policy.name == 'memory-consolidation-v2'
            else task_options(provider, self.budget_policy))
        self.projector = ConsolidationEvidenceProjector(self.budget_policy, self.estimator,
            inference_options=self.inference_options, payload_builder=lambda r: payload_text(provider, r))

    def project(self, context: MemoryConsolidationContext) -> ConsolidationEvidenceProjection:
        return self.projector.project(context)

    def fits_events(self, resolver, events, policy_version='episode-boundary/1.0'):
        from .memory_consolidation_context import MemoryConsolidationContextError
        try:
            self.project(resolver.preview(events, policy_version))
            return True
        except MemoryConsolidationContextError as error:
            if str(error) != 'CONSOLIDATION_CONTEXT_BUDGET_EXCEEDED':
                raise
            return False

    async def consolidate(self, context: MemoryConsolidationContext, *,
                          projection: ConsolidationEvidenceProjection | None = None) -> MemoryConsolidationResponse:
        projection = projection if projection is not None else self.project(context)
        request = consolidation_request(context, projection.visible_events, inference_options=self.inference_options,
            reference_events=tuple(e for e in context.reference_events if e.event_id in projection.visible_event_ids))
        logging.getLogger("embodiednpc.backend.memory_consolidation").info(
            "Consolidation projection episode_id=%s full=%s visible=%s dropped=%s tokens=%s",
            context.episode_id, len(context.evidence_events), len(projection.visible_event_ids),
            len(projection.budget_allocation.dropped_components), projection.budget_allocation.estimated_total,
        )
        # Defense in depth: measure exactly the final serialized request again.
        if not request_fits(self.provider, request, self.budget_policy, self.estimator):
            raise MemoryConsolidationError("CONSOLIDATION_REQUEST_BUDGET_EXCEEDED")
        try:
            result = await self.executor.execute_preemptible_background(self.provider, request)
            check_result(result, request, self.budget_policy)
        except ProviderError as exc:
            code = {"OLLAMA_TIMEOUT": "PROVIDER_TIMEOUT", "OLLAMA_UNAVAILABLE": "PROVIDER_UNAVAILABLE"}
            raise MemoryConsolidationError(code.get(str(exc), "PROVIDER_PROTOCOL_ERROR")) from exc
        try:
            if result.finish_reason and ('max_output_tokens' in result.finish_reason or result.finish_reason == 'length'):
                raise StructuredOutputError('MODEL_OUTPUT_TRUNCATED')
            return MemoryConsolidationResponse.model_validate(extract_json_object(result.text, max_chars=32_000))
        except StructuredOutputError as exc:
            raise MemoryConsolidationError("CONSOLIDATION_OUTPUT_MALFORMED") from exc
        except ValidationError as exc:
            raise MemoryConsolidationError("CONSOLIDATION_SCHEMA_INVALID") from exc

    @staticmethod
    def _system_prompt() -> str:
        return consolidation_system_prompt()
