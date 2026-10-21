"""Deterministic direct-dialogue evidence for one durable Relationship pair."""

from __future__ import annotations

from dataclasses import dataclass

from .memory_consolidation_context import (
    ConsolidationEvidenceEvent, MemoryConsolidationContext, TrustedPersistentIdentity,
)


RELATIONSHIP_REFLECTION_POLICY_VERSION = "relationship-reflection/1.0"
MAX_RELATIONSHIP_EVIDENCE_EVENTS = 64  # Matches the Slice 1 persisted evidence bound.


class RelationshipReflectionCandidateError(ValueError):
    pass


@dataclass(frozen=True)
class RelationshipReflectionCandidate:
    observer: TrustedPersistentIdentity
    subject: TrustedPersistentIdentity
    episode_id: str
    episode_input_hash: str
    evidence_events: tuple[ConsolidationEvidenceEvent, ...]

    @property
    def evidence_event_ids(self) -> tuple[str, ...]:
        return tuple(event.event_id for event in self.evidence_events)


class RelationshipReflectionCandidateBuilder:
    """Select only structurally verified NPC-to-Player dialogue in this Episode."""

    @staticmethod
    def _identity(
        context: MemoryConsolidationContext, event: ConsolidationEvidenceEvent,
        entity_id: str | None,
    ) -> TrustedPersistentIdentity | None:
        if entity_id is None:
            return None
        handle = event.participant_handles.get(entity_id)
        return None if handle is None else context.identity_by_handle.get(handle)

    def build(self, context: MemoryConsolidationContext) -> tuple[RelationshipReflectionCandidate, ...]:
        observer = context.observer_persistent_identity
        if observer is None or (observer.identity_namespace, observer.identity_type) != ("npc", "Character"):
            return ()

        by_subject: dict[TrustedPersistentIdentity, list[ConsolidationEvidenceEvent]] = {}
        for event in sorted(context.evidence_events, key=lambda value: value.experience_seq):
            if (event.event_type not in ("dialogue_input", "dialogue_response")
                    or event.source_type != "dialogue"
                    or event.structured_payload.get("initiative") in ("proactive", "dialogue_continuation")):
                continue
            if event.event_type == "dialogue_input":
                if (context.observer_entity_id not in event.target_entity_ids
                        or self._identity(context, event, context.observer_entity_id) != observer):
                    continue
                subject = self._identity(context, event, event.actor_entity_id)
                subjects = (subject,)
            else:
                if (event.actor_entity_id != context.observer_entity_id
                        or self._identity(context, event, event.actor_entity_id) != observer):
                    continue
                subjects = tuple(
                    self._identity(context, event, entity_id)
                    for entity_id in event.target_entity_ids
                )
            for subject in dict.fromkeys(subjects):
                if (subject is None or subject == observer
                        or (subject.identity_namespace, subject.identity_type) != ("player", "Player")):
                    continue
                by_subject.setdefault(subject, []).append(event)

        candidates = []
        for subject, events in sorted(by_subject.items()):
            if not any(event.event_type == "dialogue_input" for event in events):
                continue  # NPC narration alone is not evidence of the Player's attitude.
            ids = tuple(event.event_id for event in events)
            if len(ids) > MAX_RELATIONSHIP_EVIDENCE_EVENTS:
                raise RelationshipReflectionCandidateError("PAIR_EVIDENCE_LIMIT_EXCEEDED")
            if len(ids) != len(set(ids)):
                raise RelationshipReflectionCandidateError("DUPLICATE_PAIR_EVIDENCE_ID")
            candidates.append(RelationshipReflectionCandidate(
                observer=observer, subject=subject, episode_id=context.episode_id,
                episode_input_hash=context.input_hash, evidence_events=tuple(events),
            ))
        return tuple(candidates)
