"""Explicit bounded diagnostics, using synthetic context and the real request factory."""

import asyncio
import time
import httpx
from pydantic import ValidationError

from .contracts import CognitiveRequest
from .cognitive_step_models import CognitiveStepResult, TerminalCognitiveStep, DialogueContinuationStep, RecallOutcome
from .model_requests import CognitiveModelRequestFactory
from .model_config_runtime import build_main_provider
from .model_api_config_api import DiscoverModelsRequest, _discover, _safe_code
from .providers import InferenceOptions, ProviderError
from .context_budget_allocator import ContextBudgetExceeded


def synthetic_request(continuation=False):
    event_type = "npc.dialogue_continue" if continuation else "player.utterance"
    trigger = dict(trigger_id="probe-trigger", trigger_type="event", agent_entity_id="probe_npc",
                   source_event_id="probe-event", created_at=1.0, reason=event_type)
    return CognitiveRequest.model_validate(dict(message_type="cognitive_request", schema_version="1.0",
        request_id="probe-request", trace_id="probe-trace",
        run=dict(run_id="probe-run", agent_entity_id="probe_npc", trigger_id="probe-trigger",
                 trigger_type="event", started_at=1.0), trigger=trigger,
        context=dict(run_id="probe-run", agent_entity_id="probe_npc", trigger=trigger,
            current_event=dict(event_id="probe-event", event_type=event_type, source_entity_id="probe_player",
                target_entity_id="probe_npc", area_entity_id="probe_area", payload=[dict(key="text", value="Hello")],
                occurred_at=1.0, world_revision=0, has_world_revision=False),
            character=dict(character_id="probe-character", display_name="Probe", role_summary="Synthetic cafe host",
                speaking_style="Brief", backstory="Synthetic diagnostics only", personality="Polite",
                boundaries="Do not invent memory"), behavior_mode=dict(mode_id="working"),
            relevant_player=dict(player_entity_id="probe_player", display_name="Synthetic visitor"),
            perception=dict(observer_entity_id="probe_npc", perception_revision=0, entities=[]))))


async def contract_probe(main, transport=None):
    bounded = main.model_copy(update={"timeout_seconds": min(main.timeout_seconds, 20.0)})
    provider = build_main_provider(bounded, transport)
    options = InferenceOptions(temperature=main.temperature, think=main.thinking,
                              max_output_tokens=min(main.max_output_tokens, 2048))
    cases = [("cognition_step_0", synthetic_request(), None, CognitiveStepResult),
             ("cognition_step_1", synthetic_request(), RecallOutcome(status="NO_MEMORY_FOUND"), TerminalCognitiveStep),
             ("dialogue_continuation", synthetic_request(True), None, DialogueContinuationStep)]
    results = []
    for name, context, outcome, validator in cases:
        start = time.monotonic()
        try:
            # Same saved draft capacity as cognition; the factory's legacy 8K default
            # must not reject a valid 16K draft before any provider call.
            request = CognitiveModelRequestFactory.create_event(context, recall_outcome=outcome,
                inference_options=options, knowledge_context_limit=main.context_length or 8192)
            async with asyncio.timeout(22):
                response = await provider.generate(request)
            validator.model_validate_json(response.text)
            status, code = "PASS", None
        except ProviderError as error:
            status, code = "FAIL", _safe_code(str(error), "MODEL_CONTRACT_FAILED")
        except ContextBudgetExceeded:
            status, code = "FAIL", "COGNITIVE_PROMPT_BUDGET_EXCEEDED"
        except (ValidationError, ValueError, TypeError):
            status, code = "FAIL", "MODEL_CONTRACT_RESPONSE_INVALID"
        except TimeoutError:
            status, code = "FAIL", "MODEL_CONTRACT_TIMEOUT"
        results.append(dict(case=name, status=status, error_code=code, latency_ms=round((time.monotonic()-start)*1000)))
    results.extend(dict(case=name, status="NOT_RUN", error_code=None) for name in
                   ("memory_consolidation", "relationship_reflection", "action_feedback"))
    return {"success": all(r["status"] != "FAIL" for r in results), "results": results,
            "max_calls": 3, "semantic_correctness": "NOT_PROVEN", "world_execution": "NOT_RUN"}


async def discover_profile(config, kind, preset, transport=None):
    if preset.discovery == "manual":
        return dict(success=False, models=[], error_code="MODEL_DISCOVERY_MANUAL", manual_entry_allowed=True)
    if config.provider_type not in ("gemini_native", "anthropic_native"):
        result = await _discover(DiscoverModelsRequest(provider_type=config.provider_type, base_url=config.base_url,
            api_key=config.api_key, timeout_seconds=min(config.timeout_seconds, 15.0)), transport)
        return {**result, "manual_entry_allowed": True}
    try:
        headers = ({"x-goog-api-key": config.api_key or ""} if config.provider_type == "gemini_native" else
                   {"x-api-key": config.api_key or "", "anthropic-version": "2023-06-01"})
        ids, token = set(), None
        async with httpx.AsyncClient(timeout=min(config.timeout_seconds, 15.0), trust_env=False, transport=transport) as client:
            for _ in range(3):
                response = await client.get(config.base_url + "/models", headers=headers,
                    params={"pageToken": token} if token else None)
                if response.status_code >= 400:
                    raise ValueError
                body = response.json()
                records = body["models"] if config.provider_type == "gemini_native" else body["data"]
                for item in records[:1000]:
                    if config.provider_type == "gemini_native":
                        method = "embedContent" if kind == "embedding" else "generateContent"
                        if method not in item.get("supportedGenerationMethods", []):
                            continue
                        ids.add(item["name"])
                    else:
                        ids.add(item["id"])
                token = body.get("nextPageToken") if config.provider_type == "gemini_native" else None
                if not token:
                    break
        if any(not isinstance(i, str) or not i for i in ids):
            raise ValueError
        return dict(success=bool(ids), models=sorted(ids), error_code=None if ids else "MODEL_DISCOVERY_EMPTY",
                    manual_entry_allowed=True, truncated=bool(token))
    except (httpx.HTTPError, ValueError, KeyError, TypeError, AttributeError):
        return dict(success=False, models=[], error_code="MODEL_DISCOVERY_FAILED", manual_entry_allowed=True)
