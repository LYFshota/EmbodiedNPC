"""Bounded canonical evidence projection after lexical or hybrid ranking."""

from __future__ import annotations

import json
import re
from secrets import token_hex

from .canonical_memory_read_repository import CanonicalMemoryReadRepository, identity_parts
from .canonical_recall_models import (
    CanonicalEvidenceItem, CanonicalEvidenceSet, CanonicalReadFilter, CanonicalRecallBudget,
)
from .memory_consolidation_context import TrustedPersistentIdentity
from .recall_subjects import RecallSubjectCatalog
from .memory_consolidation_admission import NPC_SELF_STATEMENT_LABEL, NPC_SELF_STATEMENT_PREFIX


def _measured(result: CanonicalEvidenceSet) -> CanonicalEvidenceSet:
    # Include ALL serialized metadata, separators and the count itself in the budget.
    used = 0
    while True:
        result = result.model_copy(update={"char_budget_used": used})
        measured = len(result.model_dump_json())
        if measured == used:
            return result
        used = measured


class CanonicalMemoryRetrieval:
    def __init__(self, repository: CanonicalMemoryReadRepository):
        self._repository = repository

    def retrieve(self, *, observer: TrustedPersistentIdentity | None, semantic_query: str,
                 subject: TrustedPersistentIdentity | None = None,
                 filters: CanonicalReadFilter = CanonicalReadFilter(),
                 budget: CanonicalRecallBudget = CanonicalRecallBudget(),
                 catalog: RecallSubjectCatalog | None = None) -> CanonicalEvidenceSet:
        """Owner/subject are trusted system inputs (subject normally from the request catalog).

        This is not a model-facing query endpoint. Optional catalog also rechecks
        catalog membership. An open-subject read still requires the durable owner.
        Evidence-local refs identify returned historical subjects only; they do not
        expand or mutate the request catalog's authority.
        """
        try:
            identity_parts(observer)
            if subject is not None:
                identity_parts(subject)
        except ValueError:
            return _measured(CanonicalEvidenceSet(status="INVALID_SCOPE"))
        if catalog is not None and (catalog.observer != observer or subject is not None and
                catalog.resolve_identity(subject).status != "RESOLVED"):
            return _measured(CanonicalEvidenceSet(status="SUBJECT_NOT_AUTHORIZED"))
        if not isinstance(semantic_query, str) or len(semantic_query) > 512:
            raise ValueError("SEMANTIC_QUERY_EXCEEDS_BOUND")
        batch = self._repository.read(observer=observer, subject=subject,
            semantic_query=semantic_query, filters=filters, budget=budget)
        rows, eligible = batch.candidates, batch.eligible_count
        candidate_pruned = batch.truncated
        private_ids = {observer.persistent_id}
        if subject is not None:
            private_ids.add(subject.persistent_id)
        if catalog is not None:
            private_ids.update(c.identity.persistent_id for c in catalog.candidates)
        private_ids.update(identity.persistent_id for row in rows for identity in row.subjects)
        pattern = re.compile("|".join(re.escape(value) for value in sorted(private_ids, key=len, reverse=True)),
                             re.IGNORECASE)

        def redact(value):
            if isinstance(value, str):
                return pattern.sub("[redacted]", value)
            if isinstance(value, dict):
                return {key: redact(item) for key, item in value.items()}
            if isinstance(value, (tuple, list)):
                return [redact(item) for item in value]
            return value

        refs = {}
        selected = []
        truncated = candidate_pruned
        has_time_filter = filters.occurred_after_utc is not None or filters.occurred_before_utc is not None
        for candidate in rows:
            if len(selected) >= budget.top_k:
                truncated = True
                break
            row = candidate.record
            summary = row["summary"]
            if row["provenance_label"] == NPC_SELF_STATEMENT_LABEL and not summary.startswith(NPC_SELF_STATEMENT_PREFIX):
                summary = NPC_SELF_STATEMENT_PREFIX + summary
            subjects = []
            for identity in candidate.subjects:
                if identity not in refs:
                    if len(refs) >= budget.max_subjects:
                        continue
                    existing = catalog.resolve_identity(identity) if catalog is not None else None
                    refs[identity] = dict(
                        subject_ref=existing.subject_ref if existing and existing.status == "RESOLVED"
                        else "evidence_subject_" + token_hex(12),
                        ref_scope="CATALOG" if existing and existing.status == "RESOLVED" else "EVIDENCE_LOCAL",
                        kind="player" if identity.identity_type == "Player" else "npc" if
                        identity.identity_type == "Character" else "unknown")
                subjects.append(refs[identity])
            data = dict(
                memory_id=row["memory_id"], memory_type=row["memory_type"], summary=summary,
                subjects=subjects, subjects_omitted=len(candidate.subjects) - len(subjects),
                occurred_start=row["occurred_start"], occurred_end=row["occurred_end"],
                occurred_time_basis=row["temporal_precision"] or "UNKNOWN",
                temporal_filter_applied=has_time_filter and row["temporal_precision"] == "OBSERVED_UTC",
                learned_at_utc=row["learned_at"], provenance=json.loads(row["provenance_set_json"]),
                provenance_label=row["provenance_label"], episode_id=row["episode_id"], job_id=row["job_id"],
                pipeline_version=row["pipeline_version"], admission_policy_version=row["admission_policy_version"],
                lifecycle_status=row["status"], confidence=row["confidence"], importance=row["importance"],
                source_excerpt=row["source_excerpt"], excerpt_event_id=row["excerpt_event_id"],
                valid_from=row["valid_from"], valid_to=row["valid_to"],
                derivation_type=row.get('derivation_type'),uncertainty=row.get('uncertainty'),
                source_memory_ids=row.get('source_memory_ids',()),source_episode_ids=row.get('source_episode_ids',()),
                reflection_run_id=row.get('reflection_run_id'),derived_sources_stale=row.get('derived_sources_stale',False),
                not_direct_fact=row.get('not_direct_fact',False),
                maintenance_relations=row.get('maintenance_relations',()),
                maintenance_relations_truncated=row.get('maintenance_relations_truncated',False),
                raw_evidence_refs=[dict(event_id=e["experience_event_id"], experience_seq=e["experience_seq"])
                                   for e in candidate.evidence], evidence_refs_truncated=candidate.evidence_truncated)
            # Defense in depth for a source excerpt that quoted a UID. The read path
            # cannot discover arbitrary third-party IDs; admission remains responsible
            # for source text safety. No durable identity column is projected.
            safe = dict(data)
            # Only source-controlled text/identifiers need redaction. Never rewrite
            # fixed enums, safety markers, or generated opaque correlation refs.
            for key in ("summary", "memory_id", "episode_id", "job_id", "pipeline_version",
                        "admission_policy_version", "source_excerpt", "excerpt_event_id", "raw_evidence_refs",
                        "maintenance_relations", "source_memory_ids", "source_episode_ids", "reflection_run_id"):
                safe[key] = redact(data[key])
            safe["content_redacted"] = safe != data
            # A qualified fact is atomic, including its negation/time/conditions.
            # Budget whole evidence items below; never turn a prefix into a fact.
            item = CanonicalEvidenceItem.model_validate(safe)
            partial = item.maintenance_relations_truncated or candidate.evidence_truncated or item.subjects_omitted > 0
            trial = _measured(CanonicalEvidenceSet(status="OK", items=tuple(selected + [item]),
                eligible_count=eligible, candidate_count=batch.candidate_count, truncated=truncated or partial))
            if trial.char_budget_used > budget.total_evidence_chars:
                truncated = True
                continue
            selected.append(item)
            truncated |= partial
        status = "OK" if selected else "EVIDENCE_BUDGET_EXHAUSTED" if rows else "NO_MEMORY_FOUND"
        if status == "NO_MEMORY_FOUND" and batch.embedding_unavailable:
            status = "RECALL_UNAVAILABLE"
        result = _measured(CanonicalEvidenceSet(status=status, items=tuple(selected), eligible_count=eligible,
            candidate_count=batch.candidate_count, truncated=truncated))
        visible_refs = {subject.subject_ref for item in selected for subject in item.subjects}
        result._subject_identities.update({value["subject_ref"]: identity for identity, value in refs.items()
                                           if value["subject_ref"] in visible_refs})
        return result
