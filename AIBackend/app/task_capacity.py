"""Startup capacity and complete wire budgets for the existing background tasks."""
import json
from .context_budget_allocator import ContextBudgetPolicy
from .context_token_estimator import ContextTokenEstimator
from .providers import InferenceOptions, ProviderError


def effective_capacity(provider, configured=None):
    limits = [v for v in (configured, getattr(provider, 'context_length', None))
              if type(v) is int and v > 0]
    return min(limits) if limits else 8192


def task_policy(provider, task, configured=None):
    capacity = effective_capacity(provider, configured)
    known = any(type(v) is int and v > 0 for v in
                (configured, getattr(provider, 'context_length', None)))
    if task == 'consolidation' and (not known or capacity == 8192):
        return ContextBudgetPolicy.consolidation()
    target = {'consolidation': 4096, 'maintenance': 1600, 'relationship': 256}[task]
    output = min(target, max(1, capacity // 4))
    margin = min(512, max(1, capacity // 8))
    return ContextBudgetPolicy(name=task, max_context_tokens=capacity,
        input_hard_cap=max(0, capacity-output-margin), response_reserve=output,
        estimation_reserve=margin, recent_target_ceiling=0, summary_ceiling=0,
        evidence_ceiling=0, recent_hard_minimum_turns=0)


def task_options(provider, policy, *, output=None):
    maximum = output or policy.response_reserve
    options = InferenceOptions(temperature=0, think=False, max_output_tokens=maximum,
                               output_token_reserve=policy.response_reserve)
    prepare = getattr(provider, 'cognitive_options', None)
    if callable(prepare):
        options = prepare(options, policy.max_context_tokens)
    # No unverified reasoning allowance may silently exceed the shared policy.
    if (options.output_token_reserve or maximum) > policy.response_reserve:
        raise ProviderError('MODEL_OUTPUT_RESERVE_EXCEEDS_CONTEXT')
    return options


def payload_text(provider, request):
    builder = getattr(provider, 'budget_payload', None)
    payload = builder(request) if callable(builder) else request.model_dump(mode='json')
    return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def request_fits(provider, request, policy, estimator=None):
    tokens = (estimator or ContextTokenEstimator()).estimate_text(payload_text(provider, request))
    reserve = request.inference_options.output_token_reserve or request.inference_options.max_output_tokens
    maximum = request.inference_options.max_output_tokens
    limit = effective_capacity(provider, policy.max_context_tokens)
    return bool(reserve and maximum and maximum <= reserve <= policy.response_reserve
                and tokens <= policy.input_hard_cap and
                tokens + reserve + policy.estimation_reserve <= limit)


def check_result(result, request, policy=None):
    reserve = request.inference_options.output_token_reserve or request.inference_options.max_output_tokens
    if result.usage and result.usage.completion_tokens is not None and result.usage.completion_tokens > reserve:
        raise ProviderError('MODEL_OUTPUT_RESERVE_EXCEEDED')
    if not request.inference_options.think and result.usage and result.usage.reasoning_tokens:
        raise ProviderError('MODEL_THINKING_CONTROL_UNVERIFIED')
    if policy and result.usage and result.usage.prompt_tokens is not None:
        actual = result.usage.prompt_tokens
        if (actual > policy.input_hard_cap or
                actual + policy.response_reserve + policy.estimation_reserve > policy.max_context_tokens):
            raise ProviderError('MODEL_INPUT_BUDGET_EXCEEDED')
