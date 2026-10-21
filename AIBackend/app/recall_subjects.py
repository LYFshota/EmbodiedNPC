"""D0-only, read-only identity catalog for a future Runtime v2 recall decision."""

from __future__ import annotations

from dataclasses import dataclass
from secrets import token_hex
from types import MappingProxyType
from typing import Literal, Mapping

from .canonical_memory_repository import CanonicalMemoryRepository
from .contracts import CognitiveRecallSubjectIdentity, CognitiveRequest
from .memory_consolidation_context import TrustedPersistentIdentity


ResolutionStatus = Literal[
    "RESOLVED", "UNKNOWN_SUBJECT", "AMBIGUOUS_SUBJECT", "SUBJECT_NOT_DURABLE",
    "SUBJECT_NOT_AUTHORIZED",
]


class RecallSubjectCatalogError(ValueError):
    pass


@dataclass(frozen=True)
class RecallSubjectCandidate:
    subject_ref: str
    identity: TrustedPersistentIdentity
    kind: Literal["player", "npc"]
    source: Literal["observer", "requester", "current", "historical"]
    current_entity_id: str | None = None
    display_name: str | None = None


@dataclass(frozen=True)
class RecallSubjectResolution:
    status: ResolutionStatus
    subject_ref: str | None = None
    identity: TrustedPersistentIdentity | None = None


@dataclass(frozen=True)
class RecallSubjectCatalog:
    observer: TrustedPersistentIdentity
    requester_ref: str | None
    candidates: tuple[RecallSubjectCandidate, ...]
    _by_ref: Mapping[str, RecallSubjectCandidate]
    _non_durable_entities: frozenset[str]

    def model_visible_payload(self) -> dict:
        """Only temporary refs and presentation metadata; no persistent identity fields."""
        private_ids = tuple(candidate.identity.persistent_id for candidate in self.candidates)
        return {
            "requester_ref": self.requester_ref,
            "subjects": [
                {
                    "subject_ref": candidate.subject_ref,
                    "kind": candidate.kind,
                    "source": candidate.source,
                    "display_name": candidate.display_name if candidate.display_name and not any(
                        private_id in candidate.display_name for private_id in private_ids
                    ) else None,
                }
                for candidate in self.candidates
            ],
        }

    def resolve_ref(self, subject_ref: str) -> RecallSubjectResolution:
        candidate = self._by_ref.get(subject_ref)
        if candidate is None:
            return RecallSubjectResolution("UNKNOWN_SUBJECT")
        return RecallSubjectResolution("RESOLVED", candidate.subject_ref, candidate.identity)

    def resolve_identity(self, identity: TrustedPersistentIdentity) -> RecallSubjectResolution:
        """Internal authorization check; never accepts model-generated identity values."""
        for candidate in self.candidates:
            if candidate.identity == identity:
                return RecallSubjectResolution("RESOLVED", candidate.subject_ref, identity)
        return RecallSubjectResolution("SUBJECT_NOT_AUTHORIZED")

    def resolve_current_entity(self, entity_id: str) -> RecallSubjectResolution:
        """Diagnostic boundary for a current entity lacking a durable identity."""
        if entity_id in self._non_durable_entities:
            return RecallSubjectResolution("SUBJECT_NOT_DURABLE")
        for candidate in self.candidates:
            if candidate.current_entity_id == entity_id:
                return RecallSubjectResolution("RESOLVED", candidate.subject_ref, candidate.identity)
        return RecallSubjectResolution("SUBJECT_NOT_AUTHORIZED")


class RecallSubjectCatalogBuilder:
    MAX_SUBJECTS = 16

    def __init__(self, canonical_memories: CanonicalMemoryRepository) -> None:
        self._canonical_memories = canonical_memories

    def build(self, request: CognitiveRequest) -> RecallSubjectCatalog:
        wire = request.recall_identity
        if wire is None:
            raise RecallSubjectCatalogError("RECALL_IDENTITY_UNAVAILABLE")
        owner = self._identity(wire.observer)
        if owner is None:
            raise RecallSubjectCatalogError("RECALL_OBSERVER_NOT_DURABLE")

        context = request.context
        current = {value.entity_id: value for value in wire.current_subjects}
        perceived = {value.entity_id: value for value in context.perception.entities
                     if value.kind in ("player", "npc")}
        requester_id = wire.requester_entity_id
        if requester_id is not None and not (
            context.relevant_player is not None and
            context.relevant_player.player_entity_id == requester_id
        ) and requester_id not in perceived:
            raise RecallSubjectCatalogError("RECALL_REQUESTER_NOT_OBSERVER_LOCAL")

        # A Registry entry alone is not a disclosure. Only this observer's context,
        # its direct requester, and its own identity can supply current candidates.
        allowed = {context.agent_entity_id, *perceived}
        if context.relevant_player is not None:
            allowed.add(context.relevant_player.player_entity_id)
        if any(entity_id not in allowed for entity_id in current):
            raise RecallSubjectCatalogError("RECALL_CURRENT_SUBJECT_NOT_OBSERVER_LOCAL")
        if context.agent_entity_id in current and current[context.agent_entity_id] != wire.observer:
            raise RecallSubjectCatalogError("RECALL_OBSERVER_IDENTITY_CONFLICT")
        for entity_id, value in current.items():
            if entity_id in perceived and value.kind != perceived[entity_id].kind:
                raise RecallSubjectCatalogError("RECALL_CURRENT_SUBJECT_KIND_MISMATCH")
            if (context.relevant_player is not None and
                    entity_id == context.relevant_player.player_entity_id and value.kind != "player"):
                raise RecallSubjectCatalogError("RECALL_REQUESTER_KIND_MISMATCH")

        candidates: list[RecallSubjectCandidate] = []
        by_identity: dict[TrustedPersistentIdentity, RecallSubjectCandidate] = {}
        non_durable: set[str] = set()

        def add(value: CognitiveRecallSubjectIdentity, source: str, display: str | None = None) -> None:
            identity = self._identity(value)
            if identity is None:
                non_durable.add(value.entity_id)
                return
            if identity in by_identity:
                return
            if len(candidates) >= self.MAX_SUBJECTS:
                return
            candidate = RecallSubjectCandidate(
                "subject_" + token_hex(12), identity, value.kind, source,
                value.entity_id if source != "historical" else None,
                display[:80] if display else None,
            )
            candidates.append(candidate)
            by_identity[identity] = candidate

        add(wire.observer, "observer", context.character.display_name)
        if requester_id is not None and requester_id in current:
            display = (context.relevant_player.display_name if context.relevant_player is not None
                       and context.relevant_player.player_entity_id == requester_id else
                       perceived[requester_id].display_name if requester_id in perceived else None)
            add(current[requester_id], "requester", display)
        for entity_id in sorted(perceived):
            if entity_id in current:
                add(current[entity_id], "current", perceived[entity_id].display_name)
            else:
                non_durable.add(entity_id)
        if context.relevant_player is not None:
            entity_id = context.relevant_player.player_entity_id
            if entity_id in current and entity_id != requester_id:
                add(current[entity_id], "current", context.relevant_player.display_name)
            elif entity_id not in current:
                non_durable.add(entity_id)

        for identity in self._canonical_memories.list_known_subjects(owner, limit=self.MAX_SUBJECTS):
            if identity in by_identity or len(candidates) >= self.MAX_SUBJECTS:
                continue
            kind = "player" if identity.identity_type == "Player" else "npc" if (
                identity.identity_namespace == "npc" or identity.identity_type == "Character"
            ) else None
            if kind is None:
                continue
            candidate = RecallSubjectCandidate("subject_" + token_hex(12), identity, kind, "historical")
            candidates.append(candidate)
            by_identity[identity] = candidate

        by_ref = MappingProxyType({candidate.subject_ref: candidate for candidate in candidates})
        requester = next((candidate.subject_ref for candidate in candidates
                          if candidate.current_entity_id == requester_id), None)
        return RecallSubjectCatalog(owner, requester, tuple(candidates), by_ref, frozenset(non_durable))

    @staticmethod
    def _identity(value: CognitiveRecallSubjectIdentity) -> TrustedPersistentIdentity | None:
        if value.persistent_id is None:
            return None
        return TrustedPersistentIdentity(value.identity_namespace, value.identity_type, value.persistent_id)
