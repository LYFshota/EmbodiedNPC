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
        if plan.subject_ref is not None:
            resolution = catalog.resolve_ref(plan.subject_ref)
            if resolution.status != "RESOLVED":
                return RecallOutcome(status="UNKNOWN_SUBJECT")
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
