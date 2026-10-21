"""Bounded, content-free cognition capacity feedback. Never grants action authority."""

import hashlib
import hmac
import json
import os
from contextvars import ContextVar
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class CapacityCounts(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    offered: int = Field(ge=0, le=1024)
    retained: int = Field(ge=0, le=1024)
    dropped: int = Field(ge=0, le=1024)


class CapacityCheck(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    condition_id: str = Field(pattern=r"^[a-f0-9]{64}$")
    configuration_key: str = Field(pattern=r"^[a-f0-9]{64}$")
    state: Literal["ok", "warning", "error"]
    cause: Literal["budget", "provider", "output_reserve"] = "budget"
    window: int = Field(ge=1, le=1_000_000)
    input_cap: int = Field(ge=0, le=1_000_000)
    required_estimate: int = Field(ge=0, le=10_000_000)
    input_estimate: int = Field(ge=0, le=10_000_000)
    output_reserve: int = Field(ge=1, le=1_000_000)
    estimation_reserve: int = Field(ge=0, le=1_000_000)
    counts: dict[str, CapacityCounts] = Field(default_factory=dict, max_length=5)
    provider_verified: bool = False
    proof: str = ""


class CapacityStatus(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True)
    version: Literal[1] = 1
    checks: tuple[CapacityCheck, ...] = Field(max_length=2)


def _digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                    separators=(",", ":")).encode()).hexdigest()


def configuration_key(provider, options):
    # Neither credentials nor the URL itself leave the Backend. Generation bounds
    # are re-evaluated from the active runtime, never from a saved draft.
    return _digest(["cognition-budget-v1", provider.name, provider.model,
        getattr(provider, "base_url", ""), getattr(provider, "schema_profile", ""),
        getattr(provider, "token_parameter", ""), options.think])


def context_error(error):
    from .context_budget_allocator import ContextBudgetExceeded
    from .providers import ProviderError
    if isinstance(error, ContextBudgetExceeded): return "budget"
    if isinstance(error, ProviderError) and len(error.args) == 1:
        code = error.args[0]
        for prefix in ("OLLAMA", "DEEPSEEK", "OPENAI_COMPATIBLE", "GEMINI", "ANTHROPIC"):
            if code == prefix + "_CONTEXT_LENGTH_EXCEEDED": return "provider"
            if code == prefix + "_OUTPUT_RESERVE_EXCEEDS_CONTEXT": return "output_reserve"
    return None


class CapacitySigner:
    """Authenticate only diagnostic metadata across Backend restarts; no prompt storage."""
    def __init__(self, path=None):
        root = Path(os.environ.get("EMBODIEDNPC_DATA_ROOT", Path(__file__).resolve().parents[1] / "data"))
        self.path = Path(path) if path else root / "context_capacity.key"

    def _key(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        try:
            with self.path.open("xb") as stream:
                stream.write(os.urandom(32)); stream.flush(); os.fsync(stream.fileno())
        except FileExistsError: pass
        key = self.path.read_bytes()
        if len(key) != 32: raise ValueError("CAPACITY_VERIFICATION_UNAVAILABLE")
        return key

    @staticmethod
    def _body(check):
        return json.dumps(check.model_dump(mode="json", exclude={"proof"}), sort_keys=True,
                          ensure_ascii=False, separators=(",", ":")).encode()

    def sign(self, check):
        return check.model_copy(update={"proof": hmac.new(self._key(), self._body(check), "sha256").hexdigest()})

    def verifies(self, check):
        return hmac.compare_digest(check.proof, hmac.new(self._key(), self._body(check), "sha256").hexdigest())


_active = ContextVar("capacity_feedback", default=None)


class CapacityCapture:
    def __init__(self, request, provider, options, window, signer):
        event = request.context.current_event
        # Identify the original input source, not a frame's moving perception.
        # A successful retry of the same question rechecks the *current* required
        # world inputs. Other questions, NPCs, players and steps cannot clear it.
        event_data = event.model_dump(mode="json") if event else None
        if event_data:
            for key in ("event_id", "trace_id", "occurred_at", "received_at", "source_observation_id"):
                event_data.pop(key, None)
            transient = {"conversation_id", "condition_token", "condition_revision", "scene_instance_id", "visit_id"}
            event_data["payload"] = [p for p in event_data.get("payload", []) if p["key"] not in transient]
        wire = request.recall_identity
        identities = ([wire.observer.model_dump(mode="json"), next((s.model_dump(mode="json") for s in wire.current_subjects
                       if s.entity_id == wire.requester_entity_id), None)] if wire else None)
        self.source = _digest([request.run.agent_entity_id, request.context.character.character_id,
                               identities, event_data, request.reply_language])
        self.key = configuration_key(provider, options)
        self.window = window; self.options = options; self.signer = signer; self.checks = []

    def start(self): return _active.set(self)
    def finish(self, token): _active.reset(token)

    def status(self, error=None):
        cause = context_error(error) if error else None
        if cause and not self.checks:
            from .context_budget_allocator import ContextBudgetPolicy
            reserve = self.options.output_token_reserve or self.options.max_output_tokens or 1024
            p = ContextBudgetPolicy.cognition(self.window, reserve)
            self.checks.append(CapacityCheck(condition_id=_digest([self.source, 0]), configuration_key=self.key,
                state="error", cause=cause, window=self.window, input_cap=p.input_hard_cap,
                required_estimate=0, input_estimate=0, output_reserve=reserve, estimation_reserve=p.estimation_reserve))
        checks = []
        for i, check in enumerate(self.checks):
            if cause and i == len(self.checks)-1:
                check = check.model_copy(update={"state": "error", "cause": cause})
            elif error is None:
                check = check.model_copy(update={"provider_verified": True})
            if check.state == "error":
                try: check = self.signer.sign(check)
                except (OSError, ValueError): pass  # Failure remains visible; never clear without proof.
            checks.append(check)
        return CapacityStatus(checks=tuple(checks))


def record_allocation(allocation, mandatory_tokens, groups):
    capture = _active.get()
    if capture is None or allocation.policy_name != "cognition": return
    included = set(allocation.included_components)
    counts = {}; important = False
    def fully_retained(group):
        if group.group_id in included: return True
        fallbacks = [f for f in groups if f.fallback_for == group.group_id and f.group_id in included]
        if not fallbacks: return False
        original = json.loads(group.serialized_content)
        if not isinstance(original, list): return False
        retained = {json.loads(f.serialized_content).get("seq") for f in fallbacks}
        return all(item.get("seq") in retained for item in original)
    for kind in ("memory", "recent", "knowledge", "task", "relationship"):
        candidates = [g for g in groups if g.group_id == kind or g.group_id.startswith(kind+":")]
        # Fallback copies are not additional evidence and must not inflate loss.
        kept = [g for g in candidates if fully_retained(g)]
        lost = [g for g in candidates if g not in kept and
                allocation.drop_reasons.get(g.group_id) == "global_input_cap"]
        counts[kind] = CapacityCounts(offered=len(candidates), retained=len(kept), dropped=len(lost))
        if candidates and lost:
            if kind in ("memory", "task", "recent") and len(lost)*2 >= len(candidates): important = True
            if kind == "recent" and any(g.priority == 20 for g in lost): important = True
            if kind == "knowledge" and any(g.priority == 10 for g in lost): important = True
    capture.checks.append(CapacityCheck(condition_id=_digest([capture.source, len(capture.checks)]),
        configuration_key=capture.key, state="error" if allocation.safe_failure else "warning" if important else "ok",
        window=capture.window, input_cap=allocation.input_hard_cap, required_estimate=mandatory_tokens,
        input_estimate=allocation.estimated_total, output_reserve=allocation.reserved_output_tokens,
        estimation_reserve=allocation.reserved_estimation_tokens, counts=counts))


def recheck_capacity(checks, provider, options, window, signer):
    from .context_budget_allocator import ContextBudgetPolicy
    reserve = options.output_token_reserve or options.max_output_tokens or 1024
    prepare = getattr(provider, "cognitive_options", None)
    try:
        if prepare: reserve_options = prepare(options, window); reserve = reserve_options.output_token_reserve or reserve_options.max_output_tokens
    except Exception:
        return [{"condition_id": c.condition_id, "proof": c.proof, "resolved": False} for c in checks]
    policy = ContextBudgetPolicy.cognition(window, reserve)
    key = configuration_key(provider, options)
    results = []
    for c in checks:
        try: valid = signer.verifies(c)
        except (OSError, ValueError): valid = False
        # Different adapters/models and actual provider rejections require a real
        # successful response for that exact source. A settings save proves nothing.
        extra = 0 if reserve == c.output_reserve else 64
        resolved = (valid and c.state == "error" and c.cause == "budget" and c.configuration_key == key
                    and c.required_estimate + extra <= policy.input_hard_cap)
        results.append(dict(condition_id=c.condition_id, proof=c.proof, resolved=resolved))
    return results


def create_capacity_router(entry):
    from fastapi import APIRouter, Request
    from fastapi.responses import JSONResponse
    from pydantic import ValidationError
    class Recheck(BaseModel):
        model_config = ConfigDict(extra="forbid", strict=True)
        checks: tuple[CapacityCheck, ...] = Field(max_length=64)
    router = APIRouter()
    @router.post("/v1/context-budget/recheck")
    async def recheck(request: Request):
        body = await request.body()
        if len(body) > 128*1024: return JSONResponse(status_code=413, content={"error": {"code": "REQUEST_TOO_LARGE"}})
        try: data = Recheck.model_validate_json(body)
        except ValidationError: return JSONResponse(status_code=400, content={"error": {"code": "CAPACITY_RECHECK_INVALID"}})
        from .providers import InferenceOptions
        checks = recheck_capacity(data.checks, entry.provider, entry._inference_options or InferenceOptions(max_output_tokens=1024),
                                 entry._context_length, entry._capacity_signer)
        return {"version": 1, "checks": checks}
    return router
