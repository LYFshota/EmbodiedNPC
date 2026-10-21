"""M3/M4 的结构化 LLM 后端入口；``app.main`` 转发到这里。"""

from __future__ import annotations

from .portable_runtime import require_portable_owner

require_portable_owner()  # Refuse an unowned Portable import before any store is initialized.

import asyncio
import json
import logging
import os
import time
from collections import OrderedDict
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from pydantic import ValidationError

from .contracts import CognitiveRequest, CognitiveResponse, TaskRuntimeSyncRequest
from .action_proposal_validator import ActionProposalValidationError
from .cognitive_entry import CognitiveBackendEntry
from .cognitive_tasks import CognitiveTasks
import sqlite3
from .task_service import TaskService
from .task_repository import TaskRepository
from .world_knowledge_store import WorldKnowledgeResolver
from .knowledge_embeddings import KnowledgeEmbeddingIndexer
from .knowledge_management_api import KnowledgeManagement, create_knowledge_management_router
from .local_access import LocalBackendAccessMiddleware
from .cognitive_memory_recall import CognitiveMemoryRecall
from .canonical_memory_read_repository import (
    CanonicalMemoryReadRepository, validate_demo_player_uid, player_identity_allowed,
    mentions_player, event_player_scope_allowed,
)
from .canonical_memory_retrieval import CanonicalMemoryRetrieval
from .recall_subjects import RecallSubjectCatalogBuilder
from .canonical_embeddings import CanonicalEmbeddingIndex, CanonicalEmbeddingIndexer, OllamaEmbeddingProvider
from .canonical_memory_ranking import CanonicalMemoryRanker
from .provider_registry import build_model_provider
from .providers import ProviderError
from .failure_service import TaskFailureReport, TaskFailureResponse, TaskFailureResponseService
from .sqlite_database import SQLiteDatabase
from .experience_ingestion import ExperienceIngestionService
from .experience_ledger import ExperienceLedger
from .dialogue_history import create_dialogue_history_router
from .experience_models import ExperienceAppendRequest
from .episode_assembler import EpisodeAssembler
from .episode_models import EpisodeBoundaryPolicy, MemoryJobPolicy
from .episode_processing import EpisodeProcessingLoop, EpisodeStorageGuard
from .episode_repository import EpisodeRepository
from .memory_job_repository import MemoryJobRepository
from .local_model_scheduler import PrioritizedModelExecutor
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
from .base_memory_consolidator import BaseMemoryConsolidator
from .canonical_memory_repository import CanonicalMemoryRepository
from .memory_consolidation_admission import MemoryConsolidationAdmission
from .memory_consolidation_context import MemoryContextResolver
from .memory_consolidation_worker import MemoryConsolidationWorker
from .memory_maintenance_repository import MemoryMaintenanceRepository
from .memory_maintenance_worker import MemoryMaintenanceWorker
from .memory_maintenance_models import MaintenancePolicy
from .memory_reflection_models import ReflectionPolicy
from .relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from .relationship_reflection_model import RelationshipReflectionModel
from .relationship_reflection_repository import RelationshipReflectionRepository
from .relationship_reflection_scanner import RelationshipReflectionScanner
from .relationship_reflection_worker import RelationshipReflectionWorker
from .relationship_summary_reader import RelationshipSummaryReader
from .relationship_context import RelationshipContextResolver
from .world_action_eligibility import WorldActionNotEligibleError, is_counter_cleaning_consideration
from .contracts import SERVICE_ZONE_EVENT, is_service_zone_consideration
from .runtime_settings_store import RuntimeSettingsStore
from .context_policy_settings import ContextPolicySettings, create_context_policy_router
from .runtime_settings_api import create_runtime_settings_router
from .model_api_config_store import ModelApiConfigStore
from .model_api_config_api import create_model_api_config_router
from .model_profile_api import create_model_profile_router
from .model_profile_config import has_activation
from .embedding_index_control import EmbeddingControl, ManagedEmbeddingIndexer, create_embedding_index_router
from .model_config_runtime import (
    build_provider_from_model_config, build_embedding_from_model_config, cognition_options,
)


logging.Formatter.converter = time.gmtime
logging.basicConfig(level=logging.INFO,
    format="%(asctime)sZ pid=%(process)d %(levelname)s %(name)s %(message)s",
    datefmt="%Y-%m-%dT%H:%M:%S")
logger = logging.getLogger("embodiednpc.backend")

# Trusted deployment configuration, never a value selected by client/name/model.
demo_player_uid = validate_demo_player_uid(os.getenv("EMBODIEDNPC_DEMO_PLAYER_UID", "cafe-local-player-001"))


def cognitive_failure_diagnostic(error: Exception) -> str | None:
    """Only stable adapter codes cross this read-only feedback boundary."""
    from .context_budget_allocator import ContextBudgetExceeded
    if isinstance(error, ContextBudgetExceeded):
        return "CONTEXT_BUDGET_EXCEEDED"
    if not isinstance(error, ProviderError) or len(error.args) != 1 or not isinstance(error.args[0], str):
        return None
    codes = {f"{prefix}_{category}": category
             for prefix in ("OLLAMA", "DEEPSEEK", "OPENAI_COMPATIBLE", "GEMINI", "ANTHROPIC")
             for category in ("TIMEOUT", "AUTH_FAILED", "RATE_LIMITED", "UNAVAILABLE",
                              "CONTEXT_LENGTH_EXCEEDED", "OUTPUT_RESERVE_EXCEEDS_CONTEXT")}
    return codes.get(error.args[0])


def demo_identity_error(request, *, local_demo: bool = False) -> str | None:
    if isinstance(request, ExperienceAppendRequest):
        return None if event_player_scope_allowed(request.draft, demo_player_uid) else "DEMO_PLAYER_IDENTITY_REJECTED"
    if not isinstance(request, CognitiveRequest):
        return None
    wire = request.recall_identity
    current = wire.current_subjects if wire is not None else ()
    if any((s.kind == "player" or (s.identity_namespace and s.identity_type and mentions_player(s)))
           and (s.kind != "player" or not player_identity_allowed(s, demo_player_uid)) for s in current):
        return "DEMO_PLAYER_IDENTITY_REJECTED"
    required = {s.entity_id for s in current if s.kind == "player"}
    if request.context.relevant_player:
        required.add(request.context.relevant_player.player_entity_id)
    event = request.context.current_event
    if event is not None and event.event_type == "player.utterance":
        required.add(event.source_entity_id)
    if wire and wire.requester_entity_id:
        required.add(wire.requester_entity_id)
    # Single-machine Demo convention, not authenticated event provenance. Keep
    # validating supplied identities, but do not require identities for nearby players
    # merely perceived by a valid system run with no player requester.
    system_without_requester = (local_demo and request.context.relevant_player is None
                               and (wire is None or not wire.requester_entity_id)
                               and is_counter_cleaning_consideration(request))
    if not system_without_requester:
        required.update(e.entity_id for e in request.context.perception.entities if e.kind == "player")
    if any(not any(s.entity_id == entity and s.kind == "player" and player_identity_allowed(s, demo_player_uid)
                   for s in current) for entity in required):
        return "DEMO_PLAYER_IDENTITY_REJECTED"
    if event is not None and event.event_type in {"player.utterance", "npc.dialogue_continue"} and not required:
        return "DEMO_PLAYER_IDENTITY_REJECTED"
    return None

@asynccontextmanager
async def lifespan(_: FastAPI):
    knowledge_management.auth.start()
    attempted = set()

    async def cleanup():
        # Each worker owns its drain; lifespan owns independent resource cleanup.
        # The caller shields this whole sequence, including shared-provider close.
        failures = []
        try:
            knowledge_management.auth.invalidate()
        except Exception:
            failures.append("auth.invalidate")
            logger.error("BACKEND_CLEANUP_FAILED resource=auth.invalidate")
        embedding_safe = True
        resources = (
            ("knowledge", knowledge_embedding_indexer),
            ("canonical", canonical_embedding_indexer),
            ("maintenance", memory_maintenance_worker),
            ("relationship", relationship_reflection_worker),
            ("consolidation", memory_consolidation_worker),
            ("episode", episode_processing_loop),
        )
        for name, worker in resources:
            if name not in attempted:
                continue
            previous_task = getattr(worker, "_task", None)
            legacy_unjoined = (isinstance(worker, CanonicalEmbeddingIndexer)
                               and not isinstance(worker, (ManagedEmbeddingIndexer, KnowledgeEmbeddingIndexer))
                               and previous_task is not None
                               and (not previous_task.done() or previous_task.cancelled()))
            stopped = False
            try:
                await worker.stop()
                stopped = True
            except (Exception, asyncio.CancelledError):
                failures.append(name)
                logger.error("BACKEND_CLEANUP_FAILED resource=%s", name)
            if name in {"knowledge", "canonical"}:
                task = getattr(worker, "_task", None)
                quiescent = getattr(worker, "is_quiescent", None)
                if quiescent is None:
                    quiescent = ((stopped and task is None) or
                                 (task is not None and task.done() and not task.cancelled()))
                    # Legacy canonical stop cancels a bare to_thread wait. A done
                    # asyncio task alone cannot prove its native embed has ended.
                    if legacy_unjoined:
                        quiescent = False
                embedding_safe = embedding_safe and bool(quiescent)
        close = getattr(canonical_embedding_provider, "close", None)
        if callable(close):
            if embedding_safe:
                try:
                    await asyncio.to_thread(close)
                except Exception:
                    failures.append("embedding")
                    logger.error("BACKEND_CLEANUP_FAILED resource=embedding")
            else:
                failures.append("embedding_not_quiescent")
                logger.error("BACKEND_CLEANUP_FAILED resource=embedding code=RESOURCE_NOT_QUIESCENT")
        if failures:
            logger.error("BACKEND_CLEANUP_INCOMPLETE count=%s", len(failures))

    try:
        try:
            # Register before start so partial startup is cleaned as well.
            attempted.add("episode")
            episode_processing_loop.start()
            if canonical_embedding_indexer is not None:
                attempted.add("canonical")
                canonical_embedding_indexer.start()
            if knowledge_embedding_indexer is not None:
                attempted.add("knowledge")
                knowledge_embedding_indexer.start()
            if provider.name != "mock":
                attempted.add("consolidation")
                memory_consolidation_worker.start()
                attempted.add("relationship")
                relationship_reflection_worker.start()
                attempted.add("maintenance")
                memory_maintenance_worker.start()
            yield
        finally:
            closing = asyncio.create_task(cleanup(), name="backend-resource-cleanup")
            cancelled = False
            while not closing.done():
                try:
                    await asyncio.shield(closing)
                except asyncio.CancelledError:
                    cancelled = True
            closing.result()
            if cancelled:
                raise asyncio.CancelledError
    finally:
        try:
            knowledge_management.auth.stop()
        except Exception:
            # Cleanup failures must not replace the startup/body/cancellation error.
            logger.error("BACKEND_CLEANUP_FAILED resource=auth.stop")


app = FastAPI(
    title="EmbodiedNPC Local Backend",
    version="0.3.0",
    description="M3：可切换 Provider 的结构化本地 LLM 后端。",
    lifespan=lifespan,
)

# P1 SavedSettings remains a separate legacy compatibility path. P4 model config
# is loaded once here; later API saves do not replace the startup objects.
runtime_settings_store = RuntimeSettingsStore()
context_policy_settings = ContextPolicySettings()
app.include_router(create_context_policy_router(context_policy_settings))
saved_settings_document = runtime_settings_store.load()
model_api_config_store = ModelApiConfigStore()
startup_model_config_document = model_api_config_store.load()
app.include_router(create_model_api_config_router(model_api_config_store))
app.include_router(create_model_profile_router(model_api_config_store, active_document=startup_model_config_document))


memory_database_path = Path(
    os.getenv(
        "EMBODIEDNPC_MEMORY_DB",
        str(Path(__file__).resolve().parents[1] / "data" / "embodiednpc_memory.sqlite3"),
    )
)
database = SQLiteDatabase(memory_database_path)
experience_ledger = ExperienceLedger(database.connect)
app.include_router(create_dialogue_history_router(experience_ledger))
experience_ingestion_service = ExperienceIngestionService(experience_ledger)
episode_repository = EpisodeRepository(database.connect)
memory_job_repository = MemoryJobRepository(database.connect)
episode_boundary_policy = EpisodeBoundaryPolicy(
    idle_timeout_seconds=float(os.getenv("EPISODE_IDLE_TIMEOUT_SECONDS", "180")),
    max_events=int(os.getenv("EPISODE_MAX_EVENTS", "64")),
    max_duration_seconds=float(os.getenv("EPISODE_MAX_DURATION_SECONDS", "900")),
    processing_batch=int(os.getenv("EPISODE_PROCESSING_BATCH", "64")),
)
memory_job_policy = MemoryJobPolicy(
    extractor_version=SEMANTIC_MEMORY_CONSOLIDATOR_VERSION,
    max_retries=int(os.getenv("MEMORY_JOB_MAX_RETRIES", "5")),
    stale_timeout_seconds=float(os.getenv("MEMORY_JOB_STALE_TIMEOUT_SECONDS", "120")),
)
episode_storage_guard = EpisodeStorageGuard()
episode_assembler = EpisodeAssembler(
    experience_ledger, episode_repository, episode_boundary_policy,
    extractor_version=memory_job_policy.extractor_version,
    storage_call=episode_storage_guard.call,
)
episode_processing_loop = EpisodeProcessingLoop(
    episode_assembler,
    memory_job_repository,
    interval_seconds=float(os.getenv("EPISODE_POLL_INTERVAL_SECONDS", "1")),
    job_policy=memory_job_policy,
    storage_guard=episode_storage_guard,
)
model_executor = PrioritizedModelExecutor()
provider = (build_provider_from_model_config(startup_model_config_document)
            if has_activation(startup_model_config_document) else build_model_provider())
canonical_embedding_provider = None
canonical_embedding_indexer = None
if has_activation(startup_model_config_document):
    canonical_embedding_provider = build_embedding_from_model_config(startup_model_config_document)
elif os.getenv("CANONICAL_EMBEDDING_ENABLED", "true").lower() == "true" and provider.name != "mock":
    canonical_embedding_provider = OllamaEmbeddingProvider(
        os.getenv("CANONICAL_EMBEDDING_BASE_URL", os.getenv("OLLAMA_BASE_URL", "http://127.0.0.1:11434")),
        os.getenv("CANONICAL_EMBEDDING_MODEL", "qwen3-embedding:0.6b"),
        float(os.getenv("CANONICAL_EMBEDDING_TIMEOUT_SECONDS", "5")),
    )
if canonical_embedding_provider is not None:
    managed = (getattr(startup_model_config_document, "embedding_identity_version", 1) == 2 or
               getattr(getattr(startup_model_config_document, "embedding", None), "provider_type", None) == "builtin")
    if managed:
        canonical_embedding_provider = EmbeddingControl(canonical_embedding_provider,
            model_api_config_store.path.with_name("embedding_index_control.json"),
            cloud=startup_model_config_document.embedding.provider_type not in ("ollama", "builtin"),
            auto_start_builtin=has_activation(startup_model_config_document) and
                startup_model_config_document.embedding.provider_type == "builtin")
    canonical_embedding_indexer = (ManagedEmbeddingIndexer if managed else CanonicalEmbeddingIndexer)(
        CanonicalEmbeddingIndex(database.connect), canonical_embedding_provider,
        interval_seconds=float(os.getenv("CANONICAL_EMBEDDING_INTERVAL_SECONDS", "30")),
        batch_size=int(os.getenv("CANONICAL_EMBEDDING_BATCH_SIZE", "8")),
    )
app.include_router(create_embedding_index_router(model_api_config_store, startup_model_config_document,
                                               canonical_embedding_indexer))
app.include_router(create_runtime_settings_router(
    runtime_settings_store, provider, model_config_store=model_api_config_store,
    startup_model_config_document=startup_model_config_document,
    active_embedding_provider=canonical_embedding_provider,
    episode_worker_status=episode_processing_loop.snapshot))
relationship_reflection_repository = RelationshipReflectionRepository(database.connect)
relationship_summary_reader = RelationshipSummaryReader(relationship_reflection_repository)
knowledge_resolver = WorldKnowledgeResolver.from_environment(canonical_embedding_provider)
knowledge_embedding_indexer = (KnowledgeEmbeddingIndexer(knowledge_resolver.ranker.index,
    canonical_embedding_provider, interval_seconds=30, batch_size=8)
    if knowledge_resolver.ranker is not None else None)
knowledge_management = KnowledgeManagement(knowledge_resolver, knowledge_embedding_indexer)
app.add_middleware(LocalBackendAccessMiddleware, session=knowledge_management.auth)
app.include_router(create_knowledge_management_router(knowledge_management))
task_cognition = CognitiveTasks(TaskService(TaskRepository(database.connect)), expected_player_uid=demo_player_uid)
cognitive_entry = CognitiveBackendEntry(provider, model_executor, tasks=task_cognition,
    recent_dialogue_policy_source=context_policy_settings.snapshot,
    context_length=getattr(getattr(startup_model_config_document, "main_model", None), "context_length", None),
    knowledge_resolver=knowledge_resolver,
    experience_ledger=experience_ledger,
    memory_recall=CognitiveMemoryRecall(
        RecallSubjectCatalogBuilder(CanonicalMemoryRepository(database.connect)),
        CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(database.connect,
            CanonicalMemoryRanker(canonical_embedding_provider), expected_player_uid=demo_player_uid)),
    ),
    relationship_context_resolver=RelationshipContextResolver(relationship_summary_reader),
    inference_options=cognition_options(startup_model_config_document))
from .context_capacity import create_capacity_router
app.include_router(create_capacity_router(cognitive_entry))
failure_response_service = TaskFailureResponseService(provider, model_executor)
active_context_length = getattr(getattr(startup_model_config_document, 'main_model', None), 'context_length', None)
base_memory_consolidator = BaseMemoryConsolidator(provider, model_executor, context_length=active_context_length)
memory_context_resolver = MemoryContextResolver(experience_ledger, episode_repository)
consolidation_admission = MemoryConsolidationAdmission(projector=base_memory_consolidator.projector)
episode_assembler.capacity_probe = lambda events: base_memory_consolidator.fits_events(
    memory_context_resolver, events, episode_boundary_policy.version)
memory_consolidation_worker = MemoryConsolidationWorker(
    memory_job_repository,
    memory_context_resolver,
    base_memory_consolidator,
    consolidation_admission,
    CanonicalMemoryRepository(database.connect, admission=consolidation_admission),
    job_policy=memory_job_policy,
    interval_seconds=float(os.getenv("MEMORY_JOB_POLL_INTERVAL_SECONDS", "1")),
)
relationship_reflection_worker = RelationshipReflectionWorker(
    RelationshipReflectionScanner(
        memory_job_repository,
        memory_context_resolver,
        RelationshipReflectionCandidateBuilder(),
        relationship_reflection_repository,
    ),
    RelationshipReflectionModel(provider, model_executor, context_length=active_context_length),
    relationship_reflection_repository,
    interval_seconds=float(os.getenv("RELATIONSHIP_REFLECTION_POLL_INTERVAL_SECONDS", "1")),
)
memory_maintenance_worker = MemoryMaintenanceWorker(
    MemoryMaintenanceRepository(database.connect, canonical_embedding_provider, expected_player_uid=demo_player_uid),
    provider, model_executor,
    policy=MaintenancePolicy(
        backlog_threshold=int(os.getenv("MEMORY_MAINTENANCE_BACKLOG_THRESHOLD", "4")),
        max_wait_seconds=float(os.getenv("MEMORY_MAINTENANCE_MAX_WAIT_SECONDS", "300")),
    ),
    reflection_policy=ReflectionPolicy(
        min_episodes=int(os.getenv('MEMORY_REFLECTION_MIN_EPISODES', '3')),
        min_wait_seconds=float(os.getenv('MEMORY_REFLECTION_MIN_WAIT_SECONDS', '300')),
    ),
    context_length=active_context_length,
    interval_seconds=float(os.getenv("MEMORY_MAINTENANCE_POLL_INTERVAL_SECONDS", "30")),
)
@app.get("/live")
async def live():
    """Process liveness only: never resolves configuration or probes a Provider."""
    return {"status": "alive", "service": "embodiednpc-backend"}


@app.get("/health")
async def health_check() -> dict[str, object]:
    provider_health = await provider.health()
    return {
        "status": "ok" if provider_health.available else "degraded",
        "service": "embodiednpc-backend",
        "provider": provider_health.model_dump(),
    }


@app.post("/v1/task-failure-response", response_model=TaskFailureResponse)
async def get_task_failure_response(report: TaskFailureReport) -> TaskFailureResponse:
    return await failure_response_service.respond(report)


# Bound retained request contexts per connection; model execution uses the shared executor.
MAX_CONNECTION_COGNITIONS = 32
MAX_CONNECTION_FAILURE_EXPLANATIONS = 4  # Includes the one active explanation and queued reports.
FAILURE_EXPLANATION_TIMEOUT_SECONDS = 30
COGNITIVE_INPUT_COMMIT_TIMEOUT_SECONDS = 20
MAX_EARLY_COMMITTED_INPUTS = 128


@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket) -> None:
    await websocket.accept()
    local_demo = (websocket.client is not None and websocket.client.host in ("127.0.0.1", "::1")
                  and (websocket.scope.get("server") or (None,))[0] in ("127.0.0.1", "::1"))
    send_lock = asyncio.Lock()
    closed = False
    cognition_tasks: dict[tuple[str, str, str], asyncio.Task[None]] = {}
    failure_tasks: dict[tuple[str, str, str, int], asyncio.Task[None]] = {}
    failure_slot = asyncio.Semaphore(1)
    cognition_traces: dict[tuple[str, str, str], str] = {}
    continuation_keys: set[tuple[str, str, str]] = set()
    retired_cognitions: OrderedDict[tuple[str, str, str, str], None] = OrderedDict()
    continuation_turns: OrderedDict[tuple[str, ...], None] = OrderedDict()

    def retire(key, trace):
        retired_cognitions[(*key, trace)] = None
        if len(retired_cognitions) > 256:
            retired_cognitions.popitem(last=False)
    pending_dialogue_inputs: dict[tuple[str, str, str, str, str], asyncio.Future[bool]] = {}
    committed_dialogue_inputs: OrderedDict[tuple[str, str, str, str, str], None] = OrderedDict()

    def cognitive_input_key(request: CognitiveRequest) -> tuple[str, str, str, str, str] | None:
        event = request.context.current_event
        if event is None or event.event_type != "player.utterance":
            return None
        conversation = next((item.value for item in event.payload
                             if item.key == "conversation_id"), None)
        if not conversation:
            return None
        return (request.run.agent_entity_id, conversation, request.request_id,
                request.trace_id, event.event_id)

    def appended_input_key(request: ExperienceAppendRequest) -> tuple[str, str, str, str, str] | None:
        draft = request.draft
        if draft.source_type != "dialogue" or draft.event_type != "dialogue_input" or \
                not all((draft.conversation_id, draft.request_id, draft.trace_id,
                         draft.source_event_id)):
            return None
        return (draft.observer_id, draft.conversation_id, draft.request_id,
                draft.trace_id, draft.source_event_id)

    async def send_json(payload: dict) -> None:
        nonlocal closed
        async with send_lock:
            if closed:
                return
            try:
                await websocket.send_json(payload)
            except Exception:
                closed = True
                raise

    def cognition_error(request: CognitiveRequest, reason: str) -> dict:
        return {
            "message_type": "error", "request_id": request.request_id,
            "trace_id": request.trace_id, "run_id": request.run.run_id,
            "agent_entity_id": request.run.agent_entity_id, "reason": reason,
        }

    async def run_cognition(request: CognitiveRequest,
                            input_commit: asyncio.Future[bool] | None) -> None:
        try:
            if input_commit is not None:
                started = time.perf_counter()
                try:
                    persisted = await asyncio.wait_for(asyncio.shield(input_commit),
                                                       COGNITIVE_INPUT_COMMIT_TIMEOUT_SECONDS)
                except asyncio.TimeoutError:
                    persisted = False
                logger.info("Cognitive input commit request_id=%s agent_entity_id=%s persisted=%s latency_ms=%.2f",
                            request.request_id, request.run.agent_entity_id, persisted,
                            (time.perf_counter() - started) * 1000)
                if not persisted:
                    await send_json(cognition_error(request, "COGNITIVE_INPUT_NOT_PERSISTED"))
                    return
            result = await cognitive_entry.run(request)
            response = CognitiveResponse(
                schema_version=request.schema_version,
                request_id=request.request_id, trace_id=request.trace_id,
                run_id=request.run.run_id, agent_entity_id=request.run.agent_entity_id,
                decision=result.decision, validated_goal=result.validated_goal, task_receipt=result.task_receipt,
                context_status=result.context_status if request.context_diagnostics_version == 1 else None,
                task_execution=result.task_execution,
                task_result=(("committed" if result.task_receipt.committed else "failed") if result.task_receipt else "none") if request.schema_version == "1.1" else None,
            )
            excluded = {"task_receipt", "task_result"} if request.schema_version == "1.0" else set()
            if result.task_execution is None: excluded.add('task_execution')
            if response.context_status is None: excluded.add('context_status')
            payload = response.model_dump(exclude=excluded)
        except (ActionProposalValidationError, WorldActionNotEligibleError):
            logger.warning("Cognitive action proposal rejected request_id=%s", request.request_id)
            payload = cognition_error(request, "INVALID_ACTION_PROPOSAL")
        except Exception as error:
            from .task_models import TaskError
            if isinstance(error,TaskError):
                await send_json(cognition_error(request,error.code))
                return
            diagnostic = cognitive_failure_diagnostic(error)
            adapter_codes = {"OPENAI_COMPATIBLE_" + suffix for suffix in (
                "THINKING_DISABLE_NOT_HONORED", "OUTPUT_RESERVE_VIOLATED", "PROTOCOL_ERROR",
                "THINKING_CONTROL_UNVERIFIED", "TOTAL_GENERATION_BOUND_UNVERIFIED",
                "REQUEST_REJECTED", "PARAMETER_REJECTED", "SCHEMA_REJECTED")}
            adapter_code = (error.args[0] if isinstance(error, ProviderError) and len(error.args) == 1
                            and isinstance(error.args[0], str) and error.args[0] in adapter_codes else None)
            eligibility_codes = {"COGNITIVE_DAILY_MODE_NOT_ELIGIBLE", "COGNITIVE_LOOK_AT_NOT_OBSERVER_LOCAL"}
            eligibility_code = (error.args[0] if isinstance(error, ValueError) and len(error.args) == 1
                                and isinstance(error.args[0], str) and error.args[0] in eligibility_codes else None)
            logger.warning("Cognitive request failed request_id=%s error=%s diagnostic=%s adapter_code=%s eligibility_code=%s",
                           request.request_id, type(error).__name__, diagnostic, adapter_code, eligibility_code)
            payload = cognition_error(request, "COGNITIVE_REQUEST_FAILED")
            if diagnostic is not None:
                payload["diagnostic_category"] = diagnostic
            if request.context_diagnostics_version == 1 and diagnostic in ("CONTEXT_BUDGET_EXCEEDED", "CONTEXT_LENGTH_EXCEEDED", "OUTPUT_RESERVE_EXCEEDS_CONTEXT") and getattr(error, "context_status", None) is not None:
                payload["context_status"] = error.context_status.model_dump(mode="json")
        # Cancellation propagates. A transport failure is not a second cognition response.
        await send_json(payload)

    def cognition_done(key: tuple[str, str, str], task: asyncio.Task[None],
                       input_key: tuple[str, str, str, str, str] | None) -> None:
        cognition_tasks.pop(key, None)
        trace = cognition_traces.pop(key, None)
        if key in continuation_keys:
            continuation_keys.discard(key)
            if trace is not None:
                retire(key, trace)
        if input_key is not None:
            pending_dialogue_inputs.pop(input_key, None)
        if not task.cancelled():
            error = task.exception()
            if error is not None:
                logger.warning("Cognitive task send failed request_id=%s run_id=%s agent_entity_id=%s error=%s",
                               *key, type(error).__name__)

    async def run_failure_explanation(report: TaskFailureReport) -> None:
        # Language only: no task transition, execution permission or settlement.
        # The timeout includes queue wait; the receive loop never waits for inference.
        if closed:
            return
        try:
            async with asyncio.timeout(FAILURE_EXPLANATION_TIMEOUT_SECONDS):
                async with failure_slot:
                    response = await failure_response_service.respond(report)
        except TimeoutError:
            logger.warning("Task failure explanation fallback code=TIMEOUT")
            response = TaskFailureResponseService._safe_fallback(report)
        except Exception as error:
            logger.warning("Task failure explanation fallback code=FAILED error=%s", type(error).__name__)
            response = TaskFailureResponseService._safe_fallback(report)
        # Cancellation propagates; a failed send is observed, never retried as a
        # second explanation. This closure can only send on its owning socket.
        await send_json(response.model_dump())

    def failure_done(key: tuple[str, str, str, int], task: asyncio.Task[None]) -> None:
        if failure_tasks.get(key) is task:
            failure_tasks.pop(key)
        if not task.cancelled() and task.exception() is not None:
            logger.warning("Task failure explanation send failed code=SEND_FAILED error=%s",
                           type(task.exception()).__name__)

    logger.info("WebSocket client connected")
    try:
        while not closed:
            try:
                message = await websocket.receive_json()
            except json.JSONDecodeError:
                await send_json({"message_type": "error", "request_id": "unknown-request",
                                 "reason": "INVALID_JSON"})
                continue
            if closed:
                break
            if not isinstance(message, dict):
                await send_json({"message_type": "error", "request_id": "unknown-request",
                                 "reason": "MESSAGE_MUST_BE_OBJECT"})
                continue
            message_type = message.get("message_type")
            if message_type == 'task_runtime_sync':
                try:
                    sync=TaskRuntimeSyncRequest.model_validate(message)
                    if not local_demo:
                        raise ValueError('TASK_LOCAL_RUNTIME_REQUIRED')
                    snapshot=await asyncio.to_thread(task_cognition.synchronize,websocket,sync)
                    await send_json(snapshot)
                except (ValueError,sqlite3.Error):
                    from .task_diagnostics import emit
                    emit('sync',task_state='failed',request_id=message.get('request_id'),error_code='TASK_RUNTIME_SYNC_FAILED')
                    await send_json(dict(message_type='error',request_id=message.get('request_id','unknown-request'),reason='TASK_RUNTIME_SYNC_FAILED'))
                continue
            if message_type == "task_control":
                if message.get('operation') == 'cancel_task':
                    from .task_lifecycle import TaskCancelControl, cancel
                    control = None
                    try:
                        control = TaskCancelControl.model_validate(message)
                        if not local_demo: raise ValueError('TASK_LOCAL_RUNTIME_REQUIRED')
                        reply = await asyncio.to_thread(cancel, task_cognition, control)
                        await send_json(reply)
                    except (ValueError, sqlite3.Error) as error:
                        await send_json(dict(message_type='task_cancel_result',schema_version='1.1',accepted=False,
                            request_id=control.runtime_request.request_id if control else
                                message.get('runtime_request',{}).get('request_id') if isinstance(message.get('runtime_request'),dict) else None,
                            command_id=control.runtime_request.task_runtime.command_id if control else None,
                            error_code='TASK_STORE_UNAVAILABLE' if isinstance(error,sqlite3.Error) else 'TASK_CANCEL_REJECTED'))
                    continue
                if message.get('operation') != 'query':
                    from .task_execution import TaskExecutionControl
                    control = None
                    try:
                        control=TaskExecutionControl.model_validate(message)
                        original=control.original_request
                        if not local_demo or demo_identity_error(original,local_demo=local_demo) is not None:
                            raise ValueError('TASK_LOCAL_RUNTIME_REQUIRED')
                        task_cognition.bind(websocket,original,recovery=True)
                        reply=await asyncio.to_thread(task_cognition.executions.control,control)
                        await send_json(reply)
                    except (ValueError,sqlite3.Error) as error:
                        # A failure is never an action permit. Preserve correlation
                        # when the request itself was valid, without leaking data.
                        from .task_diagnostics import emit
                        emit('execution',request=control.original_request if control else None,task_state='rejected',start_permitted=False,
                            error_code='TASK_EXECUTION_STORE_UNAVAILABLE' if isinstance(error,sqlite3.Error) else 'TASK_EXECUTION_REJECTED')
                        await send_json(dict(message_type='task_execution_result',schema_version='1.1',
                            operation=control.operation if control else message.get('operation'),
                            ticket=control.ticket.model_dump(mode='json') if control else None,
                            accepted=False,start_permitted=False,task_status=None,task_version=None,phase=None,
                            error_code='TASK_EXECUTION_STORE_UNAVAILABLE' if isinstance(error,sqlite3.Error) else 'TASK_EXECUTION_REJECTED'))
                    continue
                # Recovery uses the immutable original request/command, never a new
                # model inference or another mutation. Results use the original Run.
                original = None
                try:
                    if set(message) != {"message_type", "schema_version", "operation", "original_request"} or message["schema_version"] != "1.1" or message["operation"] != "query":
                        raise ValueError("TASK_CONTROL_INVALID")
                    original = CognitiveRequest.model_validate(message["original_request"])
                    if original.schema_version != "1.1" or demo_identity_error(original, local_demo=local_demo) is not None:
                        raise ValueError("TASK_CONTROL_IDENTITY_INVALID")
                    task_cognition.bind(websocket, original, recovery=True)
                    context = task_cognition.prepare(original,recovery=True)
                    if context is None:
                        raise ValueError("TASK_CONTROL_SOURCE_INVALID")
                    recovered = await asyncio.to_thread(task_cognition.recover, original, context)
                    if recovered is None:
                        await send_json(cognition_error(original, "COGNITIVE_TASK_COMMAND_NOT_FOUND"))
                    else:
                        await send_json(CognitiveResponse(schema_version="1.1", request_id=original.request_id,
                            trace_id=original.trace_id, run_id=original.run.run_id, agent_entity_id=original.run.agent_entity_id,
                            decision=recovered[0], task_receipt=recovered[1], task_result="committed" if recovered[1] else "none").model_dump(exclude={'task_execution'}))
                except (ValueError, sqlite3.Error):
                    await send_json(cognition_error(original, "COGNITIVE_TASK_RECOVERY_FAILED") if original is not None else
                        {"message_type": "error", "request_id": "unknown-request", "reason": "COGNITIVE_TASK_RECOVERY_FAILED"})
                continue
            request_id = message.get("request_id")
            if not isinstance(request_id, str) or not request_id or len(request_id) > 160:
                request_id = "unknown-request"
            validators = {"cognitive_request": CognitiveRequest,
                          "task_failure_report": TaskFailureReport,
                          "experience_append_request": ExperienceAppendRequest}
            validator = validators.get(message_type) if isinstance(message_type, str) else None
            if validator is not None:
                try:
                    request = validator.model_validate(message)
                except ValidationError as error:
                    reason = {
                        "cognitive_request": "COGNITIVE_REQUEST_SCHEMA_INVALID",
                        "experience_append_request": "EXPERIENCE_APPEND_SCHEMA_INVALID",
                    }.get(message_type, "REQUEST_SCHEMA_INVALID")
                    if message_type == "experience_append_request":
                        fields = ";".join(
                            f"{'.'.join(str(part) for part in item['loc'])}:{item['type']}"
                            for item in error.errors(include_url=False, include_input=False)[:8]
                        )
                        logger.warning("Experience append schema rejected request_id=%s fields=%s",
                                       request_id, fields)
                    error_response = {"message_type": "error", "request_id": request_id, "reason": reason}
                    if message_type == "cognitive_request":
                        raw_run = message.get("run") if isinstance(message.get("run"), dict) else {}
                        for key, value in (("trace_id", message.get("trace_id")),
                                           ("run_id", raw_run.get("run_id")),
                                           ("agent_entity_id", raw_run.get("agent_entity_id"))):
                            if isinstance(value, str) and value:
                                error_response[key] = value
                    await send_json(error_response)
                    continue

                identity_error = demo_identity_error(request, local_demo=local_demo)
                if identity_error is not None:
                    error_response = {"message_type": "error", "request_id": request_id, "reason": identity_error}
                    if message_type == "cognitive_request":
                        error_response.update(trace_id=request.trace_id, run_id=request.run.run_id,
                                              agent_entity_id=request.run.agent_entity_id)
                    await send_json(error_response)
                    continue

            if message_type == "cognitive_cancel":
                allowed = {"message_type", "schema_version", "request_id", "trace_id", "run_id", "agent_entity_id"}
                if (set(message) != allowed or message.get("schema_version") != "1.0"
                        or any(not isinstance(message.get(k), str) or not message[k] or len(message[k]) > 160
                               for k in allowed - {"message_type", "schema_version"})):
                    await send_json({"message_type": "error", "request_id": request_id,
                                     "reason": "COGNITIVE_CANCEL_SCHEMA_INVALID"})
                    continue
                key = (message["request_id"], message["run_id"], message["agent_entity_id"])
                trace = message["trace_id"]
                # Only exact owned continuation/service-mode tickets on this connection are cancellable.
                if key in cognition_tasks:
                    if key in continuation_keys and cognition_traces.get(key) == trace:
                        retire(key, trace)
                        cognition_tasks[key].cancel()
                else:
                    retire(key, trace)  # Cancel-before-admission / duplicate-after-completion tombstone.
            elif message_type == "ping":
                await send_json(
                    {"message_type": "pong", "request_id": request_id, "schema_version": "1.0"}
                )
            elif message_type == "cognitive_request":
                try:
                    task_cognition.bind(websocket, request)
                except ValueError:
                    await send_json(cognition_error(request, "COGNITIVE_TASK_RUNTIME_INVALID"))
                    continue
                key = (request.request_id, request.run.run_id, request.run.agent_entity_id)
                continuation = (request.context.current_event is not None and
                                request.context.current_event.event_type == "npc.dialogue_continue")
                service_mode = is_service_zone_consideration(request)
                if request.context.current_event is not None and request.context.current_event.event_type == SERVICE_ZONE_EVENT and not service_mode:
                    await send_json(cognition_error(request, "SYSTEM_MODE_SOURCE_INVALID"))
                    continue
                from .world_action_eligibility import COUNTER_CLEANING_EVENT, is_counter_cleaning_consideration
                counter_work = is_counter_cleaning_consideration(request)
                if request.context.current_event is not None and request.context.current_event.event_type == COUNTER_CLEANING_EVENT and not counter_work:
                    await send_json(cognition_error(request, "AUTONOMOUS_SOURCE_INVALID"))
                    continue
                cancellable = continuation or service_mode or counter_work or request.task_consideration is not None
                if cancellable and (*key, request.trace_id) in retired_cognitions:
                    continue
                if continuation:
                    payload = {item.key: item.value for item in request.context.current_event.payload}
                    turn_key = (request.run.agent_entity_id, payload.get("source_session_id", ""),
                                payload.get("conversation_id", ""), payload.get("parent_request_id", ""),
                                payload.get("parent_event_id", ""))
                    if turn_key in continuation_turns:
                        await send_json(cognition_error(request, "COGNITIVE_CONTINUATION_ALREADY_ATTEMPTED"))
                        continue
                    continuation_turns[turn_key] = None
                    if len(continuation_turns) > 256:
                        continuation_turns.popitem(last=False)
                if key in cognition_tasks or len(cognition_tasks) >= MAX_CONNECTION_COGNITIONS:
                    logger.warning("Cognitive admission rejected request_id=%s run_id=%s agent_entity_id=%s",
                                   *key)
                    await send_json(cognition_error(request, "COGNITIVE_REQUEST_FAILED"))
                    continue
                input_key = cognitive_input_key(request)
                input_commit = None
                if input_key is not None:
                    input_commit = asyncio.get_running_loop().create_future()
                    if input_key in committed_dialogue_inputs:
                        committed_dialogue_inputs.pop(input_key)
                        input_commit.set_result(True)
                    else:
                        pending_dialogue_inputs[input_key] = input_commit
                task = asyncio.create_task(run_cognition(request, input_commit),
                                           name="cognition:" + ":".join(key))
                cognition_tasks[key] = task
                cognition_traces[key] = request.trace_id
                if cancellable:
                    continuation_keys.add(key)
                task.add_done_callback(lambda done, owned_key=key, dialogue_key=input_key:
                                       cognition_done(owned_key, done, dialogue_key))
            elif message_type == "task_failure_report":
                key = (request.request_id, request.trace_id, request.proposal_id, request.proposal_revision)
                if key in failure_tasks:
                    continue  # The in-flight owned response already fulfils this correlation.
                if len(failure_tasks) >= MAX_CONNECTION_FAILURE_EXPLANATIONS:
                    logger.warning("Task failure explanation fallback code=CAPACITY")
                    await send_json(TaskFailureResponseService._safe_fallback(request).model_dump())
                    continue
                task = asyncio.create_task(run_failure_explanation(request), name="failure-explanation")
                failure_tasks[key] = task
                task.add_done_callback(lambda done, owned_key=key: failure_done(owned_key, done))
            elif message_type == "experience_append_request":
                response = await asyncio.to_thread(experience_ingestion_service.append, request)
                await send_json(response.model_dump())
                input_key = appended_input_key(request)
                if input_key is not None:
                    committed = response.status in ("inserted", "already_exists")
                    waiting = pending_dialogue_inputs.get(input_key)
                    if waiting is not None and not waiting.done():
                        if committed or response.status != "storage_failed":
                            waiting.set_result(committed)
                    elif committed:
                        committed_dialogue_inputs[input_key] = None
                        if len(committed_dialogue_inputs) > MAX_EARLY_COMMITTED_INPUTS:
                            committed_dialogue_inputs.popitem(last=False)
            else:
                await send_json(
                    {
                        "message_type": "error",
                        "request_id": request_id,
                        "reason": "UNSUPPORTED_MESSAGE_TYPE",
                    }
                )
    except WebSocketDisconnect:
        logger.info("WebSocket client disconnected")
    except Exception:
        logger.exception("WebSocket message handling failed")
        await send_json(
            {
                "message_type": "error",
                "request_id": "unknown-request",
                "reason": "BACKEND_MESSAGE_HANDLING_FAILED",
            }
        )
    finally:
        closed = True
        task_cognition.disconnect(websocket)
        owned_tasks = (*cognition_tasks.values(), *failure_tasks.values())
        for task in owned_tasks:
            task.cancel()
        await asyncio.gather(*owned_tasks, return_exceptions=True)
        cognition_tasks.clear()
        failure_tasks.clear()
        pending_dialogue_inputs.clear()
        committed_dialogue_inputs.clear()
