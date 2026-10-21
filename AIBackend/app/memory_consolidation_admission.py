"""Deterministic evidence/identity admission; deliberately no natural-language interpretation."""

from __future__ import annotations

from dataclasses import replace
import hashlib
import json
import re
import unicodedata

from .memory_consolidation_context import MemoryConsolidationContext, TrustedPersistentIdentity
from .memory_consolidation_models import (
    AdmittedCanonicalMemory, ConsolidationAdmissionBatch, ConsolidationCandidateRejection,
    MemoryConsolidationResponse, EpisodicMemoryCandidate,
)
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
from .consolidation_evidence_projection import ConsolidationEvidenceProjector
from .semantic_memory_models import SemanticFactCandidate, locate_source_excerpt


class MemoryConsolidationAdmissionError(ValueError):
    """Batch/context integrity failure; never a normal candidate rejection."""


class _CandidateRejection(MemoryConsolidationAdmissionError):
    """Caught only at the candidate boundary in evaluate()."""


def normalize_text(value: str) -> str:
    return " ".join(unicodedata.normalize("NFKC", value).split())


def identity_key(identity: TrustedPersistentIdentity) -> tuple[str, str, str]:
    # Identity provider owns canonicalization; preserve exact identity components.
    return identity.identity_namespace, identity.identity_type, identity.persistent_id


def semantic_id(prefix: str, value: object) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return prefix + hashlib.sha256(encoded.encode("utf-8")).hexdigest()


NPC_SELF_STATEMENT_LABEL = "NPC_SELF_STATEMENT"
NPC_SELF_STATEMENT_PREFIX = "NPC曾自述（未独立证实）："
SEMANTIC_ADMISSION_POLICY_VERSION = "memory-consolidation-admission/2.0"


def is_owner_speech(event, owner: TrustedPersistentIdentity | None,
                    actor_identity: TrustedPersistentIdentity | None) -> bool:
    """Execution provenance certifies speaking, never the events described by speech."""
    return bool(owner and (owner.identity_namespace, owner.identity_type) == ("npc", "Character")
                and ((event.source_type == "dialogue" and event.event_type == "dialogue_response") or
                     (event.source_type == "social_interaction" and event.event_type == "social_response"))
                and event.natural_language_payload and event.actor_entity_id
                and actor_identity == owner
                and event.provenance in {"EXECUTED_BY_SELF", "HEARD", "SYSTEM_CONFIRMED"})


def context_is_owner_speech(context, events) -> bool:
    def own(event):
        return event.actor_entity_id == context.observer_entity_id and is_owner_speech(
            event, context.observer_persistent_identity,
            context.identity_by_handle.get(event.participant_handles.get(event.actor_entity_id)))
    def question(event):
        actor = context.identity_by_handle.get(event.participant_handles.get(event.actor_entity_id))
        target = context.identity_by_handle.get(event.participant_handles.get(context.observer_entity_id))
        return (event.source_type == "dialogue" and event.event_type == "dialogue_input" and actor
                and (actor.identity_namespace, actor.identity_type) == ("player", "Player")
                and context.observer_entity_id in event.target_entity_ids
                and target == context.observer_persistent_identity)
    if any(e.event_type == 'social_response' for e in events):
        # A witnessed gesture can contextualize speech, never certify the claims made in that reply.
        return any(own(e) for e in events) and all(own(e) or
            (e.event_type == 'observation' and e.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote')
            for e in events)
    # A cited Player question may provide context, never independent story verification.
    return any(own(event) for event in events) and all(own(event) or question(event) for event in events)


def raw_is_owner_speech(events, owner) -> bool:
    def identity(event, entity):
        return next((TrustedPersistentIdentity.from_participant(p) for p in event.persistent_participants
                     if p.entity_id == entity), None)
    def own(event):
        return event.actor_entity_id == event.observer_id and is_owner_speech(event, owner, identity(event, event.actor_entity_id))
    def question(event):
        actor = identity(event, event.actor_entity_id)
        return (event.source_type == "dialogue" and event.event_type == "dialogue_input" and actor
                and (actor.identity_namespace, actor.identity_type) == ("player", "Player")
                and event.observer_id in event.target_entity_ids and identity(event, event.observer_id) == owner)
    if any(e.event_type == 'social_response' for e in events):
        return any(own(e) for e in events) and all(own(e) or
            (e.event_type == 'observation' and e.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote')
            for e in events)
    return any(own(event) for event in events) and all(own(event) or question(event) for event in events)


def source_texts(event) -> tuple[str, ...]:
    """Only literal content strings from this model-visible event, never metadata IDs."""
    def strings(value):
        if isinstance(value, str):
            yield value
        elif isinstance(value, dict):
            for item in value.values():
                yield from strings(item)
        elif isinstance(value, (list, tuple)):
            for item in value:
                yield from strings(item)
    return tuple(strings(event.natural_language_payload)) + tuple(strings(event.structured_payload))


class MemoryConsolidationAdmission:
    """Admit candidates against the owner, visible evidence and source identity.

    Ordinary candidate rejection does not invalidate other candidates. Batch
    integrity failures do. Structural support is not proof that a model summary
    is semantically true; self-report limitations must survive projection.
    """

    def __init__(self, projector=None):
        self.projector = projector or ConsolidationEvidenceProjector()
    def evaluate(self, context: MemoryConsolidationContext,
                 response: MemoryConsolidationResponse | dict, *,
                 visible_event_ids: tuple[str, ...] | None = None) -> ConsolidationAdmissionBatch:
        if not isinstance(context, MemoryConsolidationContext):
            raise MemoryConsolidationAdmissionError("CONSOLIDATION_CONTEXT_INTEGRITY_INVALID")
        if context.pipeline_version != SEMANTIC_MEMORY_CONSOLIDATOR_VERSION:
            raise MemoryConsolidationAdmissionError("UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION")
        # Revalidate even a caller-created model instance (including model_construct).
        response = MemoryConsolidationResponse.model_validate(
            response.model_dump() if isinstance(response, MemoryConsolidationResponse) else response
        )
        self._validate_context(context)
        if visible_event_ids is None:
            visible_event_ids = self.projector.project(context).visible_event_ids
        visible_ids = frozenset(visible_event_ids)
        by_id = {event.event_id: event for event in (*context.reference_events, *context.evidence_events)}
        if len(visible_ids) != len(visible_event_ids) or not visible_ids.issubset(by_id):
            raise MemoryConsolidationAdmissionError("CONSOLIDATION_CONTEXT_INTEGRITY_INVALID")
        memories: dict[str, AdmittedCanonicalMemory] = {}
        rejections: list[ConsolidationCandidateRejection] = []
        for kind, candidates in (("episodic", response.episodic_memories), ("semantic_fact", response.semantic_facts)):
            for index, candidate in enumerate(candidates):
                try:
                    memory = self._admit_candidate(context, candidate, by_id, visible_ids)
                except _CandidateRejection as exc:
                    rejections.append(ConsolidationCandidateRejection(kind, index, str(exc)))
                    continue
                previous = memories.get(memory.memory_id)
                if previous is not None:
                    repeated_self = (memory.subjects == (context.observer_persistent_identity,) and
                        context_is_owner_speech(context, [by_id[event_id]
                            for event_id, _ in previous.evidence + memory.evidence]))
                    earlier = min((previous, memory), key=lambda m: (
                        min(seq for _, seq in m.evidence), m.confidence, m.importance or 0))
                    memory = replace(previous, evidence=tuple(sorted(set(previous.evidence + memory.evidence))),
                                     confidence=earlier.confidence if repeated_self else max(previous.confidence, memory.confidence),
                                     importance=(earlier.importance if repeated_self else max(previous.importance, memory.importance)
                                                 if memory.memory_type == "EPISODIC" else None))
                memories[memory.memory_id] = memory
        return ConsolidationAdmissionBatch(context, response, tuple(memories[key] for key in sorted(memories)),
                                           policy_version=SEMANTIC_ADMISSION_POLICY_VERSION,
                                           rejections=tuple(rejections),
                                           visible_event_ids=visible_event_ids)

    @staticmethod
    def _validate_context(context: MemoryConsolidationContext) -> None:
        # Local structural invariants only. Ledger/hash/identity authority is still
        # reconstructed by C1 inside the store's finalization transaction.
        events = context.evidence_events
        if (not events or context.start_seq < 1 or context.end_seq < context.start_seq
                or not context.observer_entity_id or not context.input_hash
                or len(events) != context.end_seq - context.start_seq + 1
                or any(e.experience_seq != context.start_seq + i for i, e in enumerate(events))
                or len({e.event_id for e in events}) != len(events)
                or any(handle not in context.identity_by_handle
                       or (entity not in {event.actor_entity_id, *event.target_entity_ids} and not (
                           event.event_type == 'observation' and event.provenance == 'HEARD' and
                           event.structured_payload.get('known_fields', {}).get('event_type') == 'heard_speech' and
                           event.structured_payload.get('known_fields', {}).get('observer_entity_id') == context.observer_entity_id == entity))
                       for event in events for entity, handle in event.participant_handles.items())):
            raise MemoryConsolidationAdmissionError("CONSOLIDATION_CONTEXT_INTEGRITY_INVALID")
        if (len(context.reference_events) > 2 or any(e.experience_seq >= context.start_seq for e in context.reference_events)
                or len({e.event_id for e in (*context.reference_events, *events)}) != len(events)+len(context.reference_events)):
            raise MemoryConsolidationAdmissionError("CONSOLIDATION_CONTEXT_INTEGRITY_INVALID")

    def _admit_candidate(self, context: MemoryConsolidationContext,
                         candidate: EpisodicMemoryCandidate | SemanticFactCandidate,
                         by_id: dict, visible_ids: frozenset[str]) -> AdmittedCanonicalMemory:
        episodic = hasattr(candidate, "subject_handles")
        handles = candidate.subject_handles if episodic else (candidate.subject_handle,)
        if len(set(handles)) != len(handles):
            raise _CandidateRejection("DUPLICATE_SUBJECT_HANDLE")
        if len(set(candidate.evidence_event_ids)) != len(candidate.evidence_event_ids):
            raise _CandidateRejection("DUPLICATE_EVIDENCE_ID")
        if any(value not in by_id for value in candidate.evidence_event_ids):
            raise _CandidateRejection("EVIDENCE_OUTSIDE_CONTEXT")
        if any(value not in visible_ids for value in candidate.evidence_event_ids):
            raise _CandidateRejection("EVIDENCE_NOT_MODEL_VISIBLE")
        events = [by_id[value] for value in sorted(candidate.evidence_event_ids)]
        # Raw hearing remains Episode evidence; free-form attributed memories are deferred.
        if any(e.event_type == 'observation' and
               e.structured_payload.get('known_fields', {}).get('event_type') == 'heard_speech' for e in events):
            raise _CandidateRejection('HEARD_SPEECH_REQUIRES_ATTRIBUTED_MEMORY_SUPPORT')
        reference_ids = {e.event_id for e in context.reference_events}
        if any(not context.start_seq <= e.experience_seq <= context.end_seq and e.event_id not in reference_ids for e in events):
            raise _CandidateRejection("EVIDENCE_OUTSIDE_CONTEXT")
        if not any(context.start_seq <= e.experience_seq <= context.end_seq for e in events):
            raise _CandidateRejection("REFERENCE_ONLY_EVIDENCE")
        subjects = []
        for handle in handles:
            identity = context.identity_by_handle.get(handle)
            if identity is None:
                raise _CandidateRejection("UNKNOWN_SUBJECT_HANDLE")
            for event in events:
                participants = {event.actor_entity_id, *event.target_entity_ids}
                if event.source_type == 'observation' and event.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote':
                    participants.add(context.observer_entity_id)
                if not any(entity in participants and value == handle
                           for entity, value in event.participant_handles.items()):
                    raise _CandidateRejection("SUBJECT_OUTSIDE_CITED_EVIDENCE")
            subjects.append(identity)
        subjects = tuple(sorted(set(subjects)))
        evidence = tuple((event.event_id, event.experience_seq) for event in events)
        owner = context.observer_persistent_identity
        owner_speech_context = context_is_owner_speech(context, events)
        self_speech = subjects == (owner,) and owner_speech_context
        # Autonomous speech is admissible only as the persistent owner's own claim.
        # Ordinary NPC-only speech follows exactly the same subject boundary.
        autonomous = any(e.structured_payload.get("initiative") == "dialogue_continuation" for e in events)
        npc_only = all((e.source_type == "dialogue" and e.event_type == "dialogue_response") or
                       (e.source_type == "social_interaction" and e.event_type == "social_response") for e in events)
        # A Player input accompanying ordinary speech is only dialogue context.
        # The schema cannot prove whether a free-form summary describes the
        # conversation or a shared past. Reject ambiguous Episodic attribution;
        # never rewrite its subjects/evidence. Independent Player facts still use
        # their own input. Genuine outcome evidence does not match this context.
        social_speech = any(e.event_type == 'social_response' for e in events)
        if (autonomous or npc_only or social_speech or (episodic and owner_speech_context)) and not self_speech:
            raise _CandidateRejection("NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD")
        if episodic:
            summary = normalize_text(candidate.summary)
            if self_speech and summary.startswith(NPC_SELF_STATEMENT_PREFIX):
                summary = summary[len(NPC_SELF_STATEMENT_PREFIX):]
            if not summary or len(summary) > 500:
                raise _CandidateRejection("SUMMARY_INVALID")
            self._check_summary(context, summary)
            if self_speech:
                summary = NPC_SELF_STATEMENT_PREFIX + summary
                if len(summary) > 500:
                    raise _CandidateRejection("SUMMARY_INVALID")
            owner_scope = (["persistent", identity_key(owner)] if owner else
                           ["runtime", context.observer_entity_id, context.source_session_id])
            content_key = (["npc-self-statement/1.0", owner_scope, summary.casefold(),
                            [identity_key(s) for s in subjects]] if self_speech else
                           [owner_scope, sorted(candidate.evidence_event_ids), summary,
                            [identity_key(s) for s in subjects]])
            memory = AdmittedCanonicalMemory(
                memory_id=semantic_id("memory_", content_key),
                memory_type="EPISODIC", owner=owner, subjects=subjects, summary=summary,
                importance=candidate.importance, confidence=candidate.confidence, evidence=evidence,
            )
        else:
            if owner is None:
                raise _CandidateRejection("SEMANTIC_DURABLE_OWNER_REQUIRED")
            # Excerpts must be contained in one original content value of a cited,
            # visible event. Joining fields could fabricate a cross-field anchor.
            texts = {event.event_id: next((text for text in source_texts(event)
                      if candidate.source_excerpt in text), None) for event in events}
            try:
                excerpt_event_id = locate_source_excerpt(candidate, texts)
            except ValueError as exc:
                raise _CandidateRejection(str(exc)) from exc
            summary = candidate.fact.strip()
            self._check_summary(context, summary)
            if self_speech and not summary.startswith(NPC_SELF_STATEMENT_PREFIX):
                summary = NPC_SELF_STATEMENT_PREFIX + summary
            if len(summary) > 500:
                raise _CandidateRejection("SUMMARY_INVALID")
            # Evidence/episode-local identity makes retries idempotent without
            # merging independent claims across Episodes. Maintenance is M3-B.
            memory = AdmittedCanonicalMemory(
                memory_id=semantic_id("memory_", [context.pipeline_version, context.episode_id,
                    context.input_hash, identity_key(owner), identity_key(subjects[0]),
                    summary, sorted(candidate.evidence_event_ids), candidate.source_excerpt]),
                memory_type="SEMANTIC_FACT", owner=owner, subjects=subjects,
                summary=summary, importance=None, confidence=candidate.confidence, evidence=evidence,
                source_excerpt=candidate.source_excerpt, excerpt_event_id=excerpt_event_id,
            )
        return memory

    @staticmethod
    def _check_summary(context: MemoryConsolidationContext, summary: str) -> None:
        text = summary.casefold()
        identifiers = {context.observer_entity_id}
        for event in context.evidence_events:
            identifiers.update(value for value in (event.actor_entity_id, *event.target_entity_ids) if value)
        identifiers.update(identity.persistent_id for identity in context.identity_by_handle.values())
        if context.observer_persistent_identity:
            identifiers.add(context.observer_persistent_identity.persistent_id)
        if re.search(r"participant_[0-9]+", text) or any(
            normalize_text(value).casefold() in text for value in identifiers
        ):
            raise _CandidateRejection("SUMMARY_IDENTIFIER_LEAKAGE")
