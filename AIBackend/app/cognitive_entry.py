"""Bounded event cognition: model steps become checked intents, not world effects."""

from __future__ import annotations

from dataclasses import dataclass, replace
import asyncio
import logging
import sqlite3
import time
from typing import Callable

from .action_proposal_validator import ActionProposalValidator
from .contracts import (CognitiveDecision, CognitiveRequest, GoalProposal, SystemDailyModeIntent,
                        SystemKeepMode, SERVICE_ZONE_EVENT, is_service_zone_consideration)
from .cognitive_memory_recall import CognitiveMemoryRecall, qualify_self_statement_delivery, qualify_shared_experience
from .cognitive_step_models import (CognitiveStepResult, DialogueContinuationStep, MemoryRecallStep, RecallOutcome, ContextReadStep,
    TaskCognitiveStep, TaskTerminalStep, TaskReadStep)
from .cognitive_task_models import CognitiveTaskReceipt, TaskExecutionTicket
from .experience_ledger import ExperienceLedger
from .recent_dialogue_context import RecentDialoguePolicy
from .local_model_scheduler import PrioritizedModelExecutor
from .model_requests import CognitiveModelRequestFactory
from .providers import InferenceOptions, ModelProvider
from .relationship_context import RelationshipContextResolver
from .structured_output import extract_json_object
from .world_action_eligibility import (WorldActionNotEligibleError, may_propose_new_world_action,
    COUNTER_CLEANING_EVENT, is_counter_cleaning_consideration, may_propose_autonomous_action)
from .world_knowledge_store import WorldKnowledgeResolver
from . import task_diagnostics as task_diagnostic

logger = logging.getLogger("embodiednpc.backend.cognitive_steps")


@dataclass(frozen=True)
class CognitiveBackendResult:
    decision: CognitiveDecision | SystemDailyModeIntent
    validated_goal: GoalProposal | None
    task_receipt: CognitiveTaskReceipt | None = None
    task_execution: TaskExecutionTicket | None = None
    context_status: object | None = None


class CognitiveBackendEntry:
    """Adapt one request to a terminal response with at most two model steps.

    Ordinary dialogue may plan one batch of context reads; system/continuation routes
    have their own restricted steps and may be preemptible background work.
    Task mutations use the trusted adapter, while action proposals still need
    independent Unity admission and real outcomes.
    """

    def __init__(self, provider: ModelProvider, model_executor: PrioritizedModelExecutor,
                 action_validator: ActionProposalValidator | None = None,
                 memory_recall: CognitiveMemoryRecall | None = None,
                 relationship_context_resolver: RelationshipContextResolver | None = None,
                 experience_ledger: ExperienceLedger | None = None,
                 inference_options: InferenceOptions | None = None,
                 knowledge_resolver: WorldKnowledgeResolver | None = None,
                 context_length: int | None = None,
                 recent_dialogue_policy: RecentDialoguePolicy | None = None,
                 recent_dialogue_policy_source: Callable[[], RecentDialoguePolicy] | None = None,
                 tasks=None) -> None:
        self._provider = provider
        from .context_capacity import CapacitySigner
        self._capacity_signer = CapacitySigner()
        self._tasks = tasks
        self._model_executor = model_executor
        self._action_validator = action_validator or ActionProposalValidator()
        self._memory_recall = memory_recall
        self._relationship_context_resolver = relationship_context_resolver
        self._experience_ledger = experience_ledger
        self._inference_options = inference_options
        self._knowledge_resolver = knowledge_resolver
        self._recent_dialogue_policy = recent_dialogue_policy or RecentDialoguePolicy()
        self._recent_dialogue_policy_source = recent_dialogue_policy_source
        capacity = context_length if context_length is not None else getattr(provider, "context_length", None)
        self._context_length = capacity if type(capacity) is int and capacity > 0 else 8192
        provider_capacity = getattr(provider, "context_length", None)
        if type(provider_capacity) is int and provider_capacity > 0:
            self._context_length = min(self._context_length, provider_capacity)

    @property
    def provider(self) -> ModelProvider:
        return self._provider

    async def run(self, request: CognitiveRequest) -> CognitiveBackendResult:
        task_diagnostic.emit('protocol',request=request,task_state='entered',task_supported=self._tasks is not None and request.schema_version=='1.1' and request.task_runtime is not None)
        from .context_capacity import CapacityCapture
        capture = CapacityCapture(request, self._provider, self._inference_options or InferenceOptions(max_output_tokens=1024),
                                  self._context_length, self._capacity_signer)
        token = capture.start()
        try:
            result = await self._run(request)
            return replace(result, context_status=capture.status())
        except Exception as error:
            task_diagnostic.failure(request,error)
            error.context_status = capture.status(error)
            raise
        finally:
            capture.finish(token)

    async def _run(self, request: CognitiveRequest) -> CognitiveBackendResult:
        started = time.perf_counter()
        recent_policy = self._policy_snapshot()
        event = request.context.current_event
        continuation = event is not None and event.event_type == "npc.dialogue_continue"
        options = self._inference_options or InferenceOptions(max_output_tokens=1024)
        if options.max_output_tokens is None:
            options = options.model_copy(update={"max_output_tokens": 1024})
        prepare_options = getattr(self._provider, "cognitive_options", None) if hasattr(type(self._provider), "cognitive_options") else None
        if prepare_options is not None:
            options = prepare_options(options, self._context_length)
        payload_builder = getattr(self._provider, "budget_payload", None) if hasattr(type(self._provider), "budget_payload") else None
        if request.action_feedback is not None:
            model = CognitiveModelRequestFactory.create_event(request, inference_options=options,
                budget_payload_builder=payload_builder, knowledge_context_limit=self._context_length)
            result = await self._model_executor.execute(self._provider, model, priority="foreground")
            decision = CognitiveDecision.model_validate(extract_json_object(result.text),
                context={"reply_language": request.reply_language})
            if (decision.decision_kind != "DIRECT_RESPONSE" or decision.action_proposal is not None or
                    decision.action_control is not None or decision.target_daily_mode is not None or
                    decision.look_at is not None or decision.episode_directive is not None or
                    decision.dialogue_turn_policy != "WAIT_FOR_PLAYER" or decision.recovery_alternative_id is not None and
                    decision.recovery_alternative_id not in {a.alternative_id for a in request.action_feedback.allowed_alternatives}):
                raise ValueError("COGNITIVE_RECOVERY_DECISION_INVALID")
            return CognitiveBackendResult(decision=decision, validated_goal=None)
        if event is not None and event.event_type in (SERVICE_ZONE_EVENT, COUNTER_CLEANING_EVENT):
            autonomous = event.event_type == COUNTER_CLEANING_EVENT
            if not (is_counter_cleaning_consideration(request) if autonomous else is_service_zone_consideration(request)):
                raise ValueError("SYSTEM_MODE_SOURCE_INVALID")
            from .cognitive_step_models import SystemModeStep, AutonomousWorkStep, CounterTaskStep, CounterTaskTerminal
            context = (await asyncio.to_thread(self._tasks.prepare, request) if autonomous and self._tasks else None)
            projection = None
            if context is not None:
                recovered = await asyncio.to_thread(self._tasks.recover, request, context)
                if recovered is not None: return CognitiveBackendResult(recovered[0], None, recovered[1])
                projection = await asyncio.to_thread(self._tasks.read, context)
                projection['immediate_clean_available'] = not await asyncio.to_thread(self._tasks.has_counter_task, request, context)
            model = CognitiveModelRequestFactory.create_event(request, inference_options=options, task_context=projection,
                budget_payload_builder=payload_builder, knowledge_context_limit=self._context_length)
            result = await self._model_executor.execute_preemptible_background(self._provider, model)
            step = (CounterTaskStep if context else AutonomousWorkStep if autonomous else SystemModeStep).model_validate(extract_json_object(result.text), context={"reply_language": request.reply_language}).root
            task_diagnostic.emit('read' if isinstance(step,TaskReadStep) else 'decision',request=request,decision=step,task_state='validated')
            if isinstance(step, TaskReadStep):
                outcome = await asyncio.to_thread(self._tasks.read, context, step.task_query)
                await asyncio.sleep(0)
                model = CognitiveModelRequestFactory.create_event(request, inference_options=options,task_context=projection,
                    task_read_outcome=outcome,cognition_phase=1,budget_payload_builder=payload_builder,knowledge_context_limit=self._context_length)
                result = await self._model_executor.execute_preemptible_background(self._provider, model)
                step = CounterTaskTerminal.model_validate(extract_json_object(result.text)).root
                task_diagnostic.emit('decision',request=request,decision=step,task_state='validated')
            receipt = None
            intent = getattr(step, 'task_operation', None)
            if intent is not None:
                try: receipt = await asyncio.to_thread(self._tasks.mutate, request, context, intent)
                except (sqlite3.Error, ValueError):
                    operation = 'create' if intent.op=='create_task' else 'cancel' if intent.change=='cancel' else 'update'
                    receipt = self._tasks.receipt(request, context, operation)
                await asyncio.sleep(0)
            decision = CognitiveDecision(decision_kind="NO_ACTION") if isinstance(step, SystemKeepMode) else step
            if autonomous and decision.decision_kind == "AUTONOMOUS_ACTION_INTENT":
                if not may_propose_autonomous_action(request, decision.action_proposal): raise WorldActionNotEligibleError()
                if context is not None and await asyncio.to_thread(self._tasks.has_counter_task, request, context):
                    raise ValueError('TASK_COUNTER_ALREADY_MANAGED')
                return CognitiveBackendResult(decision=decision, validated_goal=self._action_validator.validate_and_adapt(decision, request.context))
            return CognitiveBackendResult(decision=decision, validated_goal=None, task_receipt=receipt)
        if continuation:
            if (event.source_entity_id != request.run.agent_entity_id
                    or event.target_entity_id != request.run.agent_entity_id
                    or request.context.relevant_player is None):
                raise ValueError("DIALOGUE_CONTINUATION_OWNER_INVALID")
        task_context = (await asyncio.to_thread(self._tasks.prepare, request) if self._tasks is not None else None)
        task_diagnostic.emit('protocol',request=request,task_state='prepared',task_supported=task_context is not None)
        task_projection = None
        if task_context is not None:
            if task_context.automatic:
                await asyncio.to_thread(self._tasks.begin_consideration,request,task_context)
            recovered = await asyncio.to_thread(self._tasks.recover, request, task_context)
            await asyncio.sleep(0)
            if recovered is not None:
                return CognitiveBackendResult(recovered[0], None, recovered[1])
            task_projection = await asyncio.to_thread(self._tasks.read, task_context)
            task_projection["available_goal_kinds"] = list(task_context.scope.allowed_goal_kinds)
        recent_dialogue = await self._read_recent_dialogue(request, policy=recent_policy)
        recent_interactions = []
        if self._experience_ledger is not None and event is not None and event.event_type == "player.utterance":
            wire = request.recall_identity
            player = request.context.relevant_player
            conversation = next((p.value for p in event.payload if p.key == 'conversation_id'), None)
            if (wire is not None and player is not None and event.source_entity_id == player.player_entity_id
                    and wire.observer.kind == 'npc' and wire.observer.entity_id == request.run.agent_entity_id
                    and (not event.target_entity_id or event.target_entity_id == request.run.agent_entity_id)):
                identities = {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
                              for p in (wire.observer, *wire.current_subjects)
                              if p.entity_id in {request.run.agent_entity_id, player.player_entity_id}}
                try:
                    recent_interactions = await asyncio.to_thread(self._experience_ledger.read_recent_interactions,
                        request.run.agent_entity_id, conversation, request.request_id, event.event_id, identities)
                except (sqlite3.Error, ValueError) as error:
                    logger.warning('recent_interactions_unavailable request_id=%s reason=%s',
                                   request.request_id, type(error).__name__)
        relationship_context = (
            await asyncio.to_thread(self._relationship_context_resolver.resolve, request)
            if self._relationship_context_resolver is not None else None
        )
        catalog = (await asyncio.to_thread(self._memory_recall.prepare, request)
                   if self._memory_recall is not None and not continuation else None)
        knowledge_snapshot = (await asyncio.to_thread(self._knowledge_resolver.capture, request)
                              if self._knowledge_resolver is not None else None)
        knowledge_context = (await asyncio.to_thread(self._knowledge_resolver.prefetch,
            knowledge_snapshot, request, recent_dialogue) if knowledge_snapshot is not None else None)
        await asyncio.sleep(0)  # Deliver cancellation before submitting foreground work.
        model_request = CognitiveModelRequestFactory.create_event(
            request, subject_catalog=catalog.model_visible_payload() if catalog else None,
            relationship_context=relationship_context, recent_dialogue=recent_dialogue, recent_interactions=recent_interactions,
            inference_options=options, knowledge_context=knowledge_context,
            task_context=task_projection,
            budget_payload_builder=payload_builder,
            knowledge_context_limit=self._context_length)
        if task_context is not None and task_context.automatic:
            task_diagnostic.emit('consideration',request=request,task_state='requested')
        result = (await self._model_executor.execute_preemptible_background(self._provider, model_request)
                  if continuation or task_context is not None and task_context.automatic else await self._model_executor.execute(
                      self._provider, model_request, priority="foreground"))
        if task_context is not None and task_context.automatic:
            task_diagnostic.emit('consideration',request=request,task_state='response_received')
        step = (DialogueContinuationStep if continuation else TaskCognitiveStep if task_context is not None else CognitiveStepResult).model_validate(
            extract_json_object(result.text), context={"reply_language": request.reply_language}).root
        requested = isinstance(step, (MemoryRecallStep, TaskReadStep, ContextReadStep))
        logger.info("cognitive_step=0 request_id=%s trace_id=%s run_id=%s agent_entity_id=%s "
                    "recall_requested=%s latency_ms=%.1f", request.request_id, request.trace_id,
                    request.run.run_id, request.run.agent_entity_id, requested,
                    (time.perf_counter() - started) * 1000)
        if requested:
            task_diagnostic.emit('read',request=request,decision=step,task_state='validated')
            await asyncio.sleep(0)
            knowledge_plan = step.knowledge_plan if isinstance(step, ContextReadStep) else None
            memory_plan = (step.memory_plan if isinstance(step, ContextReadStep)
                           else step.recall_plan if isinstance(step, MemoryRecallStep) else None)
            task_plan = (getattr(step, "task_plan", None) if isinstance(step, ContextReadStep)
                         else step.task_query if isinstance(step, TaskReadStep) else None)

            async def read_branch(call, unavailable):
                try:
                    return await asyncio.to_thread(call)
                except Exception as error:
                    # Read failures are source-local; cancellation is BaseException and
                    # propagates. Never retry or turn an unavailable source into no-match.
                    logger.warning("context_read_unavailable request_id=%s reason=%s",
                                   request.request_id, type(error).__name__)
                    return unavailable

            from .world_knowledge import KnowledgeContext
            jobs, sources = [], []
            if knowledge_plan is not None:
                sources.append("knowledge")
                jobs.append(read_branch(lambda: self._knowledge_resolver.query(
                    knowledge_snapshot, knowledge_plan.semantic_query) if self._knowledge_resolver else
                    KnowledgeContext(status="UNAVAILABLE"),
                    knowledge_snapshot.context.model_copy(update={"status": "UNAVAILABLE"})
                    if knowledge_snapshot else KnowledgeContext(status="UNAVAILABLE")))
            if memory_plan is not None:
                sources.append("memory")
                jobs.append(read_branch(lambda: self._memory_recall.recall(catalog, memory_plan)
                    if self._memory_recall else RecallOutcome(status="RECALL_UNAVAILABLE"),
                    RecallOutcome(status="RECALL_UNAVAILABLE")))
            if task_plan is not None:
                sources.append("task")
                jobs.append(read_branch(lambda: self._tasks.read(task_context, task_plan),
                    dict(status="TASK_READ_UNAVAILABLE", tasks=[], has_more=False)))
            # Providers retain their existing embedding serialization/gates. These are
            # independent reads, not an assumption of parallel GPU model execution.
            outcomes = dict(zip(sources, await asyncio.gather(*jobs)))
            if "knowledge" in outcomes:
                knowledge_context = WorldKnowledgeResolver.merge_results(knowledge_context, outcomes["knowledge"])
            outcome = outcomes.get("memory")
            task_outcome = outcomes.get("task")
            if outcome is not None:
                if memory_plan.subject_role == "shared":
                    outcome = await asyncio.to_thread(qualify_shared_experience, outcome,
                                                      catalog, self._experience_ledger)
                outcome = await asyncio.to_thread(qualify_self_statement_delivery, outcome,
                                                  catalog, self._experience_ledger)
            if knowledge_snapshot is not None:
                knowledge_context = await asyncio.to_thread(self._knowledge_resolver.validate_snapshot,
                    knowledge_snapshot, knowledge_context)
            # SQLite reads may finish in their worker after cancellation; cancelled runs
            # never submit Step 1 and never publish an intermediate result.
            await asyncio.sleep(0)
            started = time.perf_counter()
            execute = (self._model_executor.execute_preemptible_background if task_context is not None and task_context.automatic
                       else self._model_executor.execute)
            result = await execute(
                self._provider, CognitiveModelRequestFactory.create_event(
                    request, subject_catalog=catalog.model_visible_payload() if catalog else None,
                    recall_outcome=outcome, cognition_phase=1, relationship_context=relationship_context,
                    task_context=task_projection, task_read_outcome=task_outcome,
                    recent_dialogue=recent_dialogue, recent_interactions=recent_interactions, inference_options=options,
                    budget_payload_builder=payload_builder,
                    knowledge_context=knowledge_context,
                    knowledge_context_limit=self._context_length),
                **({} if task_context is not None and task_context.automatic else {"priority":"foreground"}))
            # Terminal-only schema AND validation: a second recall is a normal safe
            # request failure, never another iteration or model retry.
            raw_terminal = extract_json_object(result.text)
            decision = (TaskTerminalStep.model_validate(raw_terminal, context={"reply_language": request.reply_language}).root
                        if task_context is not None else CognitiveDecision.model_validate(raw_terminal,
                            context={"reply_language": request.reply_language}))
            logger.info("cognitive_step=1 request_id=%s trace_id=%s run_id=%s agent_entity_id=%s "
                        "recall_status=%s evidence_count=%d latency_ms=%.1f", request.request_id,
                        request.trace_id, request.run.run_id, request.run.agent_entity_id,
                        {source: value.status if hasattr(value, "status") else value["status"]
                         for source, value in outcomes.items()}, len(outcome.evidence.items) if outcome and outcome.evidence else 0,
                        (time.perf_counter() - started) * 1000)
        else:
            decision = step
        await asyncio.sleep(0)
        if knowledge_snapshot is not None and knowledge_context is not None and (
                knowledge_context.items or knowledge_context.constant_items):
            checked = await asyncio.to_thread(self._knowledge_resolver.validate_snapshot,
                                             knowledge_snapshot, knowledge_snapshot.context)
            if checked.status in {"SNAPSHOT_CHANGED", "UNAVAILABLE"}:
                # A mutation during inference cannot yield a reply/action grounded in
                # revoked knowledge. Fail safely; never a third model call.
                raise ValueError("KNOWLEDGE_SNAPSHOT_CHANGED")
        await asyncio.sleep(0)
        task_diagnostic.emit('decision',request=request,decision=decision,task_state='validated')
        if decision.target_daily_mode is not None:
            daily = request.context.daily_behavior
            player = request.context.relevant_player
            if (event is None or event.event_type != "player.utterance" or player is None
                    or event.source_entity_id != player.player_entity_id
                    or event.target_entity_id != request.run.agent_entity_id or daily is None
                    or not any(candidate.target_mode == decision.target_daily_mode for candidate in daily.candidates)):
                raise ValueError("COGNITIVE_DAILY_MODE_NOT_ELIGIBLE")
        if task_context is not None and task_context.automatic and decision.dialogue_turn_policy == 'MAY_CONTINUE':
            raise ValueError('TASK_CONTINUATION_NOT_AUTHORIZED')
        allowed_look_at = {entity.entity_id for entity in request.context.perception.entities}
        if request.context.relevant_player is not None:
            allowed_look_at.add(request.context.relevant_player.player_entity_id)
        if decision.look_at is not None and decision.look_at not in allowed_look_at:
            raise ValueError("COGNITIVE_LOOK_AT_NOT_OBSERVER_LOCAL")
        automatic_action = decision.decision_kind == 'ACTION_PROPOSAL' and task_context is not None and task_context.automatic
        if decision.decision_kind == "ACTION_PROPOSAL" and not automatic_action and not may_propose_new_world_action(request):
            raise WorldActionNotEligibleError()
        if getattr(decision,'task_focus',None) is not None and not automatic_action:
            raise ValueError('TASK_EXECUTION_SOURCE_REQUIRED')
        if decision.recovery_alternative_id is not None:
            raise ValueError("COGNITIVE_RECOVERY_SOURCE_REQUIRED")
        self._validate_action_control(request, decision)
        validated_goal = self._action_validator.validate_and_adapt(decision, request.context)
        ticket = None
        if automatic_action:
            ticket = await asyncio.to_thread(self._tasks.executions.reserve, request, task_context, decision)
        intent = getattr(decision, "task_operation", None)
        receipt = None
        # All language, source, look-at and action checks precede the transaction.
        if intent is not None:
            await asyncio.sleep(0)
            try:
                receipt = await asyncio.to_thread(self._tasks.mutate, request, task_context, intent)
                if receipt.outcome in ("cancellation_requested", "deduplicated"):
                    text = (("The cancellation request was saved; the original execution still needs verification."
                             if receipt.outcome == "cancellation_requested" else "That task is already saved.")
                            if request.reply_language == "en-US" else
                            "取消请求已保存，原执行仍待核对。" if receipt.outcome == "cancellation_requested" else "这个任务已经保存过了。")
                    decision = CognitiveDecision(decision_kind="DIRECT_RESPONSE", utterance=text)
            except (sqlite3.Error, ValueError):
                operation = "create" if intent.op == "create_task" else "cancel" if intent.change == "cancel" else "update"
                receipt = self._tasks.receipt(request, task_context, operation)
                decision = CognitiveDecision(decision_kind="DIRECT_RESPONSE", utterance=(
                    "I couldn't save that task. Please try again." if request.reply_language == "en-US" else
                    "这次任务未能保存，请重试。"))
            await asyncio.sleep(0)  # A cancelled worker may have committed; no cancelled-run presentation.
        if hasattr(decision, "task_operation") or hasattr(decision,'task_focus'):
            decision = CognitiveDecision.model_validate(decision.model_dump(exclude={"task_operation", "future_commitment", "task_focus"}),
                context={"reply_language": request.reply_language})
        return CognitiveBackendResult(decision=decision, validated_goal=validated_goal, task_receipt=receipt, task_execution=ticket)

    @staticmethod
    def _validate_action_control(request: CognitiveRequest, decision: CognitiveDecision) -> None:
        control = decision.action_control
        if control is None:
            return
        event, state, player = request.context.current_event, request.context.runtime_state, request.context.relevant_player
        if (event is None or event.event_type != "player.utterance" or player is None or
                event.source_entity_id != player.player_entity_id or event.target_entity_id != request.run.agent_entity_id or
                not any(p.key == "conversation_id" and p.value for p in event.payload) or
                state is None or state.status != "IN_PROGRESS" or
                control.operation not in (state.allowed_controls or ()) or
                (control.proposal_id, control.proposal_revision, control.trace_id) !=
                (state.proposal_id, state.proposal_revision, state.trace_id)):
            raise ValueError("COGNITIVE_ACTION_CONTROL_NOT_ELIGIBLE")

    def _policy_snapshot(self) -> RecentDialoguePolicy:
        return self._recent_dialogue_policy_source() if self._recent_dialogue_policy_source else self._recent_dialogue_policy

    async def _read_recent_dialogue(self, request: CognitiveRequest, *,
                                    policy: RecentDialoguePolicy | None = None) -> list[dict]:
        policy = policy or self._policy_snapshot()
        event = request.context.current_event
        if event is not None and event.event_type == "npc.dialogue_continue":
            if self._experience_ledger is None:
                raise ValueError("DIALOGUE_CONTINUATION_LEDGER_REQUIRED")
            payload = {item.key: item.value for item in event.payload}
            if (payload.get("continuation_index") != "1" or request.context.relevant_player is None
                    or payload.get("player_entity_id") != request.context.relevant_player.player_entity_id):
                raise ValueError("DIALOGUE_CONTINUATION_PARENT_INVALID")
            payload["last_response_seq"] = int(payload.get("last_response_seq", "0"))
            wire = request.recall_identity
            player = next((p for p in wire.current_subjects if p.entity_id == payload["player_entity_id"]
                           and p.kind == "player"), None) if wire is not None else None
            if wire is None or player is None:
                raise ValueError("DIALOGUE_CONTINUATION_CURRENT_IDENTITY_REQUIRED")
            identities = {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
                          for p in (wire.observer, player)}
            return await asyncio.to_thread(self._experience_ledger.read_continuation_dialogue,
                                           request.run.agent_entity_id, payload.get("conversation_id"),
                                           payload, payload["player_entity_id"], identities,
                                           **policy.limits(self._context_length))
        if self._experience_ledger is None or event is None or event.event_type != "player.utterance":
            return []
        conversation_id = next((item.value for item in event.payload
                                if item.key == "conversation_id"), None)
        if not conversation_id:
            return []
        required = next((item.value for item in event.payload if item.key == "required_response_request_id"), None)
        if required:
            await asyncio.to_thread(self._experience_ledger.require_dialogue_response,
                                    request.run.agent_entity_id, conversation_id, required,
                                    next((item.value for item in event.payload if item.key == "required_response_session_id"), None),
                                    event.source_entity_id)
        wire = request.recall_identity
        player = next((p for p in wire.current_subjects
                       if p.entity_id == event.source_entity_id and p.kind == "player"), None) if wire else None
        if (wire is None or player is None or wire.observer.kind != "npc"
                or wire.observer.entity_id != request.run.agent_entity_id
                or (event.target_entity_id and event.target_entity_id != request.run.agent_entity_id)
                or request.context.relevant_player is None
                or request.context.relevant_player.player_entity_id != player.entity_id):
            return []
        identities = {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
                      for p in (wire.observer, player)}
        try:
            recent_limits = policy.limits(self._context_length)
            return await asyncio.to_thread(self._experience_ledger.read_recent_dialogue,
                                           request.run.agent_entity_id, conversation_id,
                                           request.request_id, event.event_id,
                                           identities=identities, **recent_limits)
        except (sqlite3.Error, ValueError) as error:
            logger.warning("recent_dialogue_unavailable request_id=%s reason=%s",
                           request.request_id, type(error).__name__)
            return []
