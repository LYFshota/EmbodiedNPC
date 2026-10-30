"""Resolve request-local subject references for one Recall; no model/world writes."""

import sqlite3

from .canonical_memory_retrieval import CanonicalMemoryRetrieval
from .canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from .cognitive_step_models import MemoryRecallPlan, RecallOutcome
from .contracts import CognitiveRequest
from .recall_subjects import RecallSubjectCatalog, RecallSubjectCatalogBuilder, RecallSubjectCatalogError


class CognitiveMemoryRecall:
    """Map a low-authority Recall plan through the trusted subject catalog.

    Missing/invalid scope yields an explicit unavailable/unknown result, not
    an unscoped search. Historical evidence never certifies current world state.
    """

    def __init__(self, subjects: RecallSubjectCatalogBuilder, retrieval: CanonicalMemoryRetrieval,
                 budget: CanonicalRecallBudget = CanonicalRecallBudget()):
        self._subjects = subjects
        self._retrieval = retrieval
        self._budget = budget

    def prepare(self, request: CognitiveRequest) -> RecallSubjectCatalog | None:
        try:
            return self._subjects.build(request)
        except (RecallSubjectCatalogError, sqlite3.Error):
            return None

    def recall(self, catalog: RecallSubjectCatalog | None, plan: MemoryRecallPlan) -> RecallOutcome:
        if catalog is None:
            return RecallOutcome(status="RECALL_UNAVAILABLE")
        subject = None
        requester = catalog.resolve_ref(catalog.requester_ref).identity if catalog.requester_ref else None
        if plan.subject_role in {"requester", "shared"} and requester is None:
            return RecallOutcome(status="UNKNOWN_SUBJECT")
        if plan.subject_role == "requester":
            subject = requester
        elif plan.subject_role == "observer":
            subject = catalog.observer
        if plan.subject_ref is not None:
            resolution = catalog.resolve_ref(plan.subject_ref)
            if resolution.status != "RESOLVED":
                return RecallOutcome(status="UNKNOWN_SUBJECT")
            if plan.subject_role in {"requester", "observer"} and resolution.identity != subject:
                return RecallOutcome(status="UNKNOWN_SUBJECT")
            if plan.subject_role == "shared":
                if resolution.identity not in (requester, catalog.observer):
                    return RecallOutcome(status="UNKNOWN_SUBJECT")
            else:
                subject = resolution.identity
        try:
            evidence = self._retrieval.retrieve(
                observer=catalog.observer, subject=subject, catalog=catalog,
                semantic_query=plan.semantic_query,
                filters=CanonicalReadFilter(memory_type=plan.memory_type),
                budget=self._budget,
            )
        except sqlite3.Error:
            return RecallOutcome(status="RECALL_UNAVAILABLE")
        return RecallOutcome(status=evidence.status, evidence=evidence)


def qualify_self_statement_delivery(outcome, catalog, ledger):
    """A self-memory's subjects identify the speaker, not who heard the speech.

    Consult original immutable Ledger sources; never certify from a summary,
    current entity ID alone, or a generated/unpresented model response.
    """
    from .cognitive_step_models import SelfStatementDelivery
    from .memory_consolidation_context import TrustedPersistentIdentity
    delivery = []
    if outcome.evidence is None:
        return outcome
    requester = catalog.resolve_ref(catalog.requester_ref).identity if catalog and catalog.requester_ref else None
    for item in outcome.evidence.items:
        if item.provenance_label != "NPC_SELF_STATEMENT":
            continue
        verified = []
        if requester is not None and ledger is not None:
            for ref in item.raw_evidence_refs:
                try:
                    event = ledger.get_by_event_id(ref.event_id)
                    if event is None or event.experience_seq != ref.experience_seq:
                        continue
                    identities = {p.entity_id: TrustedPersistentIdentity(
                        p.identity_namespace, p.identity_type, p.persistent_id) for p in event.persistent_participants}
                    if (event.source_type == "dialogue" and event.event_type == "dialogue_response"
                            and event.actor_entity_id == event.observer_id
                            and identities.get(event.observer_id) == catalog.observer
                            and event.provenance == "EXECUTED_BY_SELF"
                            and event.natural_language_payload
                            and any(identities.get(target) == requester for target in event.target_entity_ids)):
                        verified.append(ref.event_id)
                except (OSError, ValueError, sqlite3.Error):
                    continue
        delivery.append(SelfStatementDelivery(memory_id=item.memory_id,
            status="PRESENTED_TO_REQUESTER" if verified else "UNKNOWN", verified_event_ids=tuple(verified)))
    return outcome.model_copy(update={"self_statement_delivery": tuple(delivery)})


def qualify_shared_experience(outcome, catalog, ledger):
    """Narrow a shared-intent read using existing raw pair-local evidence only.

    This is not a new memory scope or store. It can remove candidates, never
    authorize new ones or fetch beyond the original bounded Recall result.
    """
    from .canonical_memory_retrieval import _measured
    from .memory_consolidation_context import TrustedPersistentIdentity
    if outcome.evidence is None or outcome.status != "OK":
        return outcome
    requester = catalog.resolve_ref(catalog.requester_ref).identity if catalog and catalog.requester_ref else None
    if requester is None or ledger is None:
        return RecallOutcome(status="RECALL_UNAVAILABLE")
    pair = {requester, catalog.observer}
    kept = []
    for item in outcome.evidence.items:
        for ref in item.raw_evidence_refs:
            try:
                event = ledger.get_by_event_id(ref.event_id)
                if event is None or event.experience_seq != ref.experience_seq or event.provenance == "HEARD":
                    continue
                identities = {p.entity_id: TrustedPersistentIdentity(
                    p.identity_namespace, p.identity_type, p.persistent_id) for p in event.persistent_participants}
                involved = {identities.get(entity) for entity in (event.actor_entity_id, *event.target_entity_ids)}
                if identities.get(event.observer_id) == catalog.observer and pair <= involved:
                    kept.append(item); break
            except (OSError, ValueError, sqlite3.Error):
                continue
    evidence = _measured(outcome.evidence.model_copy(update={"items": tuple(kept),
        "status": "OK" if kept else "NO_MEMORY_FOUND", "truncated": outcome.evidence.truncated or len(kept) < len(outcome.evidence.items)}))
    return outcome.model_copy(update={"evidence": evidence, "status": evidence.status})
