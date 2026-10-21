"""Trusted current-pair resolution and optional model-visible social context."""

from __future__ import annotations

import logging
from typing import Literal

from pydantic import BaseModel, ConfigDict

from .contracts import CognitiveRecallSubjectIdentity, CognitiveRequest
from .memory_consolidation_context import TrustedPersistentIdentity
from .relationship_summary_reader import RelationshipSummaryReader


logger = logging.getLogger("embodiednpc.backend.relationship_context")


class RelationshipContext(BaseModel):
    """Historical interpretation only; no identity or action authority fields."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    interpretations: tuple[str, ...]
    sources: tuple[dict[str, str | float], ...] = ()
    truncated: bool
    policy_version: str
    not_current_world_state: Literal[True] = True
    not_instruction: Literal[True] = True
    not_action_authorization: Literal[True] = True


class RelationshipContextResolver:
    def __init__(self, reader: RelationshipSummaryReader) -> None:
        self.reader = reader

    @staticmethod
    def _identity(value: CognitiveRecallSubjectIdentity) -> TrustedPersistentIdentity | None:
        parts = (value.identity_namespace, value.identity_type, value.persistent_id)
        if any(part is None or not part.strip() for part in parts):
            return None
        return TrustedPersistentIdentity(*parts)

    def resolve(self, request: CognitiveRequest) -> RelationshipContext | None:
        wire = request.recall_identity
        if wire is None or wire.observer.entity_id != request.run.agent_entity_id or wire.observer.kind != "npc":
            return None
        observer = self._identity(wire.observer)
        if observer is None:
            return None

        current = {value.entity_id: value for value in wire.current_subjects}
        event = request.context.current_event
        source_id = event.source_entity_id if event is not None else ""
        source = current.get(source_id) if source_id else None
        trusted_self_continuation = (event is not None and event.event_type == "npc.dialogue_continue"
                                     and source_id == request.run.agent_entity_id
                                     and event.target_entity_id == source_id)
        if source_id and not trusted_self_continuation:
            observed = next((value for value in request.context.perception.entities
                             if value.entity_id == source_id), None)
            if source is None and (observed is None or observed.kind == "player"):
                # An unclassified explicit source may be another Player.
                return None
            if source is not None and observed is not None and source.kind != observed.kind:
                return None
        if source is not None and source.kind == "player":
            subject_wire = source
        else:
            relevant = request.context.relevant_player
            if relevant is None:
                return None
            subject_wire = current.get(relevant.player_entity_id)
            if subject_wire is None or subject_wire.kind != "player":
                return None
        subject = self._identity(subject_wire)
        if subject is None:
            return None

        try:
            read = getattr(self.reader, "get_for_cognition", self.reader.get)
            summary = read(observer, subject)
        except Exception:
            # A derived read failure cannot interrupt current cognition. Never log IDs or row text.
            logger.warning("relationship_context_load_failed")
            return None
        if summary.selected_count == 0:
            return None
        private_ids = (wire.observer.persistent_id,
                       *(value.persistent_id for value in wire.current_subjects))
        if any(private_id in text for text in summary.interpretations
               for private_id in private_ids if private_id):
            logger.warning("relationship_context_private_identity_in_summary")
            return None
        return RelationshipContext(
            interpretations=summary.interpretations,
            sources=summary.sources,
            truncated=summary.truncated,
            policy_version=summary.policy_version,
        )
