"""Full authoritative Episode evidence and trusted persistent identities."""

from __future__ import annotations

import json
from dataclasses import dataclass
from types import MappingProxyType
from typing import Any, Mapping

from pydantic import BaseModel, ConfigDict

from .episode_models import DurableMemoryJob, Episode
from .episode_repository import EpisodeRepository
from .experience_ledger import ExperienceLedger
from .experience_models import ObserverExperienceEvent, PersistentParticipantIdentity, is_heard_speech
from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION


class MemoryConsolidationContextError(ValueError):
    pass


@dataclass(frozen=True, order=True)
class TrustedPersistentIdentity:
    identity_namespace: str
    identity_type: str
    persistent_id: str

    @classmethod
    def from_participant(cls, value: PersistentParticipantIdentity) -> "TrustedPersistentIdentity":
        return cls(value.identity_namespace, value.identity_type, value.persistent_id)


class ConsolidationParticipantDescriptor(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    handle: str
    historical_entity_ids: tuple[str, ...]


class ConsolidationEvidenceEvent(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)

    experience_seq: int
    event_id: str
    event_type: str
    source_type: str
    actor_entity_id: str | None
    target_entity_ids: tuple[str, ...]
    participant_handles: dict[str, str]
    structured_payload: dict[str, Any]
    natural_language_payload: str | None
    occurred_at: float
    observed_at: float
    world_revision: int | None
    confidence: float | None
    provenance: str


@dataclass(frozen=True)
class MemoryConsolidationContext:
    """Internal context; persistent identities are deliberately absent from model serialization."""

    episode_id: str
    observer_entity_id: str
    observer_persistent_identity: TrustedPersistentIdentity | None
    start_seq: int
    end_seq: int
    close_reason: str
    source_session_id: str
    evidence_events: tuple[ConsolidationEvidenceEvent, ...]
    participant_descriptors: tuple[ConsolidationParticipantDescriptor, ...]
    identity_by_handle: Mapping[str, TrustedPersistentIdentity]
    provenance: tuple[str, ...]
    input_hash: str
    pipeline_version: str
    reference_events: tuple[ConsolidationEvidenceEvent, ...] = ()

    def model_visible_payload(
        self, events: tuple[ConsolidationEvidenceEvent, ...] | None = None,
        *, reference_events: tuple[ConsolidationEvidenceEvent, ...] = (),
    ) -> dict[str, Any]:
        events = self.evidence_events if events is None else events
        handles = {handle for event in (*reference_events, *events) for handle in event.participant_handles.values()}
        return {
            "episode_id": self.episode_id,
            "observer_entity_id": self.observer_entity_id,
            "start_seq": self.start_seq,
            "end_seq": self.end_seq,
            "close_reason": self.close_reason,
            "source_session_id": self.source_session_id,
            "evidence_events": [value.model_dump(mode="json") for value in events],
            "participant_descriptors": [
                value.model_dump(mode="json") for value in self.participant_descriptors if value.handle in handles
            ],
            "provenance": sorted({event.provenance for event in events}),
            "input_hash": self.input_hash,
            "pipeline_version": self.pipeline_version,
            **({"read_only_reference_events": [e.model_dump(mode='json') for e in reference_events]}
               if reference_events else {}),
        }

    def to_model_visible_json(
        self, events: tuple[ConsolidationEvidenceEvent, ...] | None = None,
        *, reference_events: tuple[ConsolidationEvidenceEvent, ...] = (),
    ) -> str:
        return json.dumps(
            self.model_visible_payload(events, reference_events=reference_events), ensure_ascii=False, sort_keys=True, separators=(",", ":")
        )


class MemoryContextResolver:
    def __init__(
        self,
        ledger: ExperienceLedger,
        episodes: EpisodeRepository,
    ) -> None:
        self.ledger = ledger
        self.episodes = episodes

    def resolve(self, job: DurableMemoryJob) -> MemoryConsolidationContext:
        if job.extractor_version != SEMANTIC_MEMORY_CONSOLIDATOR_VERSION:
            raise MemoryConsolidationContextError("UNSUPPORTED_CONSOLIDATION_PIPELINE_VERSION")
        episode = self.episodes.get(job.episode_id)
        if episode is None:
            raise MemoryConsolidationContextError("EPISODE_NOT_FOUND")
        if episode.status != "CLOSED":
            raise MemoryConsolidationContextError("EPISODE_NOT_CLOSED")
        if episode.observer_id != job.observer_id:
            raise MemoryConsolidationContextError("OBSERVER_SCOPE_MISMATCH")
        try:
            events = self.ledger.read_exact_range(
                episode.observer_id, episode.start_seq, episode.end_seq
            )
        except ValueError as exc:
            raise MemoryConsolidationContextError(str(exc)) from exc
        if self.episodes.input_hash_for(episode, events) != job.input_hash:
            raise MemoryConsolidationContextError("JOB_INPUT_HASH_MISMATCH")
        return self._build(episode, events, job.input_hash, job.extractor_version)

    def preview(self, events, policy_version='episode-boundary/1.0'):
        first, last = events[0], events[-1]
        episode = Episode(episode_id=self.episodes.episode_id_for(first.observer_id, first.experience_seq, policy_version),
            observer_id=first.observer_id, source_session_id=first.source_session_id,
            start_seq=first.experience_seq, end_seq=last.experience_seq,
            started_at_utc=first.ingested_at_utc, ended_at_utc=last.ingested_at_utc,
            status='CLOSED', close_reason='capacity_boundary_preview', conversation_id=first.conversation_id,
            primary_goal_id=None, primary_goal_type=None, event_count=len(events), policy_version=policy_version,
            schema_version='episode/1.0', created_at_utc=first.ingested_at_utc, updated_at_utc=last.ingested_at_utc)
        return self._build(episode, events, self.episodes.input_hash_for(episode, events),
                           SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)

    def _build(self, episode, events, input_hash, pipeline_version):
        reference = ()
        previous = self.episodes.previous_closed(episode.observer_id, episode.start_seq)
        if (previous and previous.end_seq == episode.start_seq-1 and previous.close_reason == 'capacity'
                and previous.conversation_id == episode.conversation_id
                and previous.source_session_id == episode.source_session_id and previous.event_count >= 2):
            pair = self.ledger.read_exact_range(previous.observer_id, previous.end_seq-1, previous.end_seq)
            if correlated_dialogue_pair(*pair):
                reference = pair

        identity_by_entity, entities_by_identity = self._resolve_identities((*reference, *events))
        identities = sorted(entities_by_identity)
        handle_by_identity = {
            identity: f"participant_{index}" for index, identity in enumerate(identities, start=1)
        }
        descriptors = tuple(
            ConsolidationParticipantDescriptor(
                handle=handle_by_identity[identity],
                historical_entity_ids=tuple(sorted(entities_by_identity[identity])),
            )
            for identity in identities
        )
        evidence = tuple(
            self._evidence_event(event, identity_by_entity, handle_by_identity) for event in events
        )
        reference_evidence = tuple(self._evidence_event(e, identity_by_entity, handle_by_identity) for e in reference)
        hidden_mapping = MappingProxyType({handle_by_identity[key]: key for key in identities})
        owner_identity = identity_by_entity.get(episode.observer_id)
        provenance = tuple(sorted({event.provenance for event in events}))

        return MemoryConsolidationContext(
            episode_id=episode.episode_id,
            observer_entity_id=episode.observer_id,
            observer_persistent_identity=owner_identity,
            start_seq=episode.start_seq,
            end_seq=episode.end_seq,
            close_reason=episode.close_reason or "",
            source_session_id=episode.source_session_id,
            evidence_events=evidence,
            participant_descriptors=descriptors,
            identity_by_handle=hidden_mapping,
            provenance=provenance,
            input_hash=input_hash,
            pipeline_version=pipeline_version,
            reference_events=reference_evidence,
        )

    @staticmethod
    def _resolve_identities(events: tuple[ObserverExperienceEvent, ...]) -> tuple[
        dict[str, TrustedPersistentIdentity], dict[TrustedPersistentIdentity, set[str]]
    ]:
        identity_by_entity: dict[str, TrustedPersistentIdentity] = {}
        entities_by_identity: dict[TrustedPersistentIdentity, set[str]] = {}
        for event in events:
            participants = {
                value for value in (event.actor_entity_id, *event.target_entity_ids) if value is not None
            }
            if is_emote_observation(event) or is_heard_speech(event):
                participants.add(event.observer_id)
            for participant in event.persistent_participants:
                if participant.entity_id not in participants:
                    raise MemoryConsolidationContextError("PERSISTENT_PARTICIPANT_NOT_IN_EVIDENCE")
                identity = TrustedPersistentIdentity.from_participant(participant)
                existing = identity_by_entity.get(participant.entity_id)
                if existing is not None and existing != identity:
                    raise MemoryConsolidationContextError("PERSISTENT_PARTICIPANT_IDENTITY_CONFLICT")
                identity_by_entity[participant.entity_id] = identity
                entities_by_identity.setdefault(identity, set()).add(participant.entity_id)
        return identity_by_entity, entities_by_identity

    @staticmethod
    def _evidence_event(
        event: ObserverExperienceEvent,
        identity_by_entity: Mapping[str, TrustedPersistentIdentity],
        handle_by_identity: Mapping[TrustedPersistentIdentity, str],
    ) -> ConsolidationEvidenceEvent:
        entity_ids = tuple(value.entity_id for value in event.persistent_participants)
        handles = {
            entity_id: handle_by_identity[identity_by_entity[entity_id]]
            for entity_id in entity_ids if entity_id in identity_by_entity
        }
        return ConsolidationEvidenceEvent(
            experience_seq=event.experience_seq,
            event_id=event.event_id,
            event_type=event.event_type,
            source_type=event.source_type,
            actor_entity_id=event.actor_entity_id,
            target_entity_ids=event.target_entity_ids,
            participant_handles=handles,
            structured_payload=event.structured_payload,
            natural_language_payload=event.natural_language_payload,
            occurred_at=event.occurred_at,
            observed_at=event.observed_at,
            world_revision=event.world_revision,
            confidence=event.confidence,
            provenance=event.provenance,
        )


def correlated_dialogue_pair(player, npc):
    return bool(player.event_type == 'dialogue_input' and npc.event_type == 'dialogue_response'
        and npc.structured_payload.get('initiative') != 'dialogue_continuation'
        and player.observer_id == npc.observer_id and player.source_session_id == npc.source_session_id
        and player.conversation_id == npc.conversation_id and player.clock_domain == npc.clock_domain
        and npc.actor_entity_id == player.observer_id and player.actor_entity_id in npc.target_entity_ids
        and ((player.request_id and player.request_id == npc.request_id)
             or (player.source_event_id and player.source_event_id == npc.source_event_id)))


def is_emote_observation(event):
    return (event.event_type == 'observation' and event.source_type == 'observation'
            and event.structured_payload.get('known_fields', {}).get('event_type') == 'player.emote')


def correlated_social_pair(action, reply):
    fields = action.structured_payload.get('known_fields', {})
    return bool(is_emote_observation(action) and reply.event_type == 'social_response'
        and reply.source_type == 'social_interaction' and reply.actor_entity_id == action.observer_id
        and reply.observer_id == action.observer_id and reply.source_session_id == action.source_session_id
        and reply.clock_domain == action.clock_domain and reply.occurred_at >= action.occurred_at
        and reply.source_event_id == action.source_event_id and reply.trace_id == action.trace_id
        and reply.source_observation_ids == action.source_observation_ids
        and reply.target_entity_ids == (action.actor_entity_id,)
        and fields.get('social_observer_entity_id') == action.observer_id
        and fields.get('social_response_eligible') == 'true'
        and reply.structured_payload.get('emote_id') == fields.get('emote_id'))


def correlated_interaction_pair(action, reply):
    return correlated_dialogue_pair(action, reply) or correlated_social_pair(action, reply)
