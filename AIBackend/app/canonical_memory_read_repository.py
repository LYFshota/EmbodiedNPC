"""Canonical authority filtering and post-ranking evidence hydration in one snapshot."""

from __future__ import annotations

from contextlib import closing
from dataclasses import dataclass
import re
import sqlite3
import unicodedata
from typing import Callable

from .canonical_recall_models import CanonicalReadFilter, CanonicalRecallBudget
from .memory_consolidation_context import TrustedPersistentIdentity
from .canonical_memory_ranking import CanonicalMemoryRanker
from .experience_ledger import ExperienceLedger
from .memory_consolidation_admission import (
    NPC_SELF_STATEMENT_LABEL, NPC_SELF_STATEMENT_PREFIX, raw_is_owner_speech,
)


def validate_demo_player_uid(value: str) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,160}", value):
        raise ValueError("DEMO_PLAYER_UID_INVALID")
    return value


def player_identity_allowed(identity, uid: str) -> bool:
    return (identity.identity_namespace, identity.identity_type, identity.persistent_id) == (
        "player", "Player", uid)


def mentions_player(identity) -> bool:
    return identity.identity_namespace.casefold() == "player" or identity.identity_type.casefold() == "player"


def event_player_scope_allowed(event, uid: str) -> bool:
    """Inspect the complete immutable event, including evidence beyond the output budget."""
    identities = {p.entity_id: p for p in event.persistent_participants}
    if any(mentions_player(p) and not player_identity_allowed(p, uid) for p in identities.values()):
        return False
    if event.source_type == "dialogue":
        if event.event_type == "dialogue_input":
            actor = identities.get(event.actor_entity_id)
            return actor is not None and player_identity_allowed(actor, uid)
        if event.event_type == "dialogue_response":
            # Unknown recipients may hide a foreign Player. Known NPC recipients remain legal.
            return bool(event.target_entity_ids) and all(
                entity in identities and (player_identity_allowed(identities[entity], uid) or
                    (identities[entity].identity_namespace, identities[entity].identity_type) == ("npc", "Character"))
                for entity in event.target_entity_ids)
    return True  # Historical non-player World/Action evidence need not have a Player.


def identity_parts(value: TrustedPersistentIdentity) -> tuple[str, str, str]:
    if not isinstance(value, TrustedPersistentIdentity):
        raise ValueError("DURABLE_IDENTITY_REQUIRED")
    parts = (value.identity_namespace, value.identity_type, value.persistent_id)
    if any(not isinstance(part, str) or not part.strip() or len(part) > limit
           for part, limit in zip(parts, (80, 80, 160))):
        raise ValueError("DURABLE_IDENTITY_REQUIRED")
    return parts


def lexical_terms(text: str) -> frozenset[str]:
    """Unicode normalization, Latin words and overlapping CJK bigrams; no ontology."""
    normalized = unicodedata.normalize("NFKC", text).casefold()
    terms = set(re.findall(r"[a-z0-9]+", normalized))
    for run in re.findall(r"[\u3400-\u9fff]+", normalized):
        terms.update(run[i:i + 2] for i in range(len(run) - 1))
        if len(run) == 1:
            terms.add(run)
    return frozenset(terms)


@dataclass(frozen=True)
class CanonicalReadCandidate:
    record: dict
    subjects: tuple[TrustedPersistentIdentity, ...]
    evidence: tuple[dict, ...]
    evidence_truncated: bool


@dataclass(frozen=True)
class CanonicalReadBatch:
    candidates: tuple[CanonicalReadCandidate, ...]
    eligible_count: int
    candidate_count: int
    truncated: bool
    embedding_unavailable: bool = False


class CanonicalMemoryReadRepository:
    def __init__(self, connection_factory: Callable[[], sqlite3.Connection], ranker=None, *,
                 expected_player_uid: str | None = None):
        self._connect = connection_factory
        self._ranker = ranker or CanonicalMemoryRanker()
        # Generic repository retains its multi-player structure; the Demo deployment
        # binds this once at startup, independently of any request/model subject.
        self.expected_player_uid = (validate_demo_player_uid(expected_player_uid)
                                    if expected_player_uid is not None else None)

    def _source_certificate(self, db, memory_id, observer):
        kind = db.execute('SELECT memory_type FROM memory_records WHERE memory_id=?',(memory_id,)).fetchone()
        if kind and kind[0]=='DERIVED_INSIGHT':
            return 'DERIVED_INSIGHT' if self._insight_metadata(db,memory_id,observer) is not None else None
        subjects = tuple(TrustedPersistentIdentity(*row) for row in db.execute(
            "SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?",
            (memory_id,)))
        rows = db.execute(
            "SELECT e.*,m.experience_seq AS linked_seq FROM memory_evidence m LEFT JOIN experience_events e "
            "ON e.event_id=m.experience_event_id WHERE m.memory_id=? ORDER BY m.experience_seq", (memory_id,)).fetchall()
        complete = bool(rows) and all(row["event_id"] is not None and
            row["linked_seq"] == row["experience_seq"] for row in rows)
        events = tuple(ExperienceLedger._row_to_event(row) for row in rows) if complete else ()
        uid = self.expected_player_uid
        if uid is not None and (not complete or any(mentions_player(s) and not player_identity_allowed(s, uid)
            for s in subjects) or any(not event_player_scope_allowed(e, uid) for e in events)):
            return None
        self_speech = raw_is_owner_speech(events, observer)
        if self_speech and subjects != (observer,) and all(e.event_type == "dialogue_response" for e in events):
            return None  # Old NPC-only claims cannot be upgraded to Player/shared history.
        if self_speech and subjects == (observer,):
            return NPC_SELF_STATEMENT_LABEL
        if not complete:
            return "SOURCE_UNVERIFIED"
        if all(e.source_type == "dialogue" for e in events) and any(
                e.event_type == "dialogue_response" for e in events):
            return "DIALOGUE_ASSERTIONS_NOT_WORLD_PROOF"
        return ""

    def _insight_metadata(self, db, memory_id, observer):
        meta = db.execute('SELECT * FROM memory_derived_insights WHERE memory_id=?',(memory_id,)).fetchone()
        subject = {tuple(s) for s in db.execute('SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?',(memory_id,))}
        sources = db.execute('SELECT r.*,s.source_revision,s.source_status FROM memory_insight_sources s JOIN memory_records r ON r.memory_id=s.source_memory_id WHERE s.memory_id=? ORDER BY r.memory_id LIMIT 9',(memory_id,)).fetchall()
        if not meta or not 2<=len(sources)<=8 or len(subject)!=1:
            return None
        evidence = {r[0] for r in db.execute('SELECT experience_event_id FROM memory_evidence WHERE memory_id=?',(memory_id,))}
        episode_ids = {r[0] for r in db.execute('SELECT episode_id FROM memory_insight_episodes WHERE memory_id=?',(memory_id,))}
        union = set()
        for row in sources:
            peer_subjects = {tuple(s) for s in db.execute('SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects WHERE memory_id=?',(row['memory_id'],))}
            if (row['memory_type'] not in ('EPISODIC','SEMANTIC_FACT') or row['scope']!='NPC_PRIVATE'
                or tuple(row[k] for k in ('identity_namespace','identity_type','persistent_id'))!=identity_parts(observer)
                or not subject.issubset(peer_subjects) or not peer_subjects.issubset(subject|{identity_parts(observer)})
                or self._source_certificate(db,row['memory_id'],observer) in (None,'SOURCE_UNVERIFIED')):
                return None
            ids = {e[0] for e in db.execute('SELECT experience_event_id FROM memory_evidence WHERE memory_id=?',(row['memory_id'],))}
            if not evidence.intersection(ids):
                return None
            union.update(ids)
        if not evidence or not evidence.issubset(union) or len(episode_ids)<2 or episode_ids!={r['episode_id'] for r in sources}:
            return None
        return dict(derivation_type=meta['derivation_type'],uncertainty=meta['uncertainty'],
                    reflection_run_id=meta['run_id'],source_memory_ids=[r['memory_id'] for r in sources],
                    source_episode_ids=sorted(episode_ids),not_direct_fact=True,
                    derived_sources_stale=any(r['revision']!=r['source_revision'] or r['status']!=r['source_status'] for r in sources))

    def read(self, *, observer: TrustedPersistentIdentity, subject: TrustedPersistentIdentity | None,
             semantic_query: str, filters: CanonicalReadFilter, budget: CanonicalRecallBudget
             ) -> CanonicalReadBatch:
        owner = identity_parts(observer)

        clauses = ["r.identity_namespace=?", "r.identity_type=?", "r.persistent_id=?",
                   "r.scope='NPC_PRIVATE'", "r.memory_type IN ('EPISODIC','SEMANTIC_FACT','DERIVED_INSIGHT')",
                   "r.status IN ('ACTIVE','CONFLICTED','SUPERSEDED')",
                   "(r.memory_type='EPISODIC' OR (r.memory_type='SEMANTIC_FACT' AND p.memory_id IS NOT NULL) OR (r.memory_type='DERIVED_INSIGHT' AND EXISTS (SELECT 1 FROM memory_derived_insights d WHERE d.memory_id=r.memory_id)))"]
        args: list = list(owner)
        if subject is not None:
            clauses.append("EXISTS (SELECT 1 FROM memory_subjects s WHERE s.memory_id=r.memory_id "
                           "AND s.identity_namespace=? AND s.identity_type=? AND s.persistent_id=?)")
            args.extend(identity_parts(subject))
        if filters.lifecycle == "CURRENT":
            # Conflicts remain visible and explicitly uncertain; never choose a unique winner.
            clauses.append("r.status IN ('ACTIVE','CONFLICTED')")
            clauses.append("NOT EXISTS (SELECT 1 FROM memory_insight_sources x JOIN memory_records m ON m.memory_id=x.source_memory_id WHERE x.memory_id=r.memory_id AND (m.revision<>x.source_revision OR m.status<>x.source_status))")
        if filters.memory_type is not None:
            clauses.append("r.memory_type=?")
            args.append(filters.memory_type)
        if filters.provenance:
            clauses.append("EXISTS (SELECT 1 FROM json_each(r.provenance_set_json) j WHERE j.value IN ("
                           + ",".join("?" for _ in filters.provenance) + "))")
            args.extend(filters.provenance)
        for value, comparison, column in (
            (filters.occurred_after_utc, ">=", "occurred_end"),
            (filters.occurred_before_utc, "<=", "occurred_start"),
        ):
            if value is not None:
                # Only Semantic Fact records carry a persisted comparable event-clock certificate.
                clauses.append("(COALESCE(p.temporal_precision,'UNKNOWN')<>'OBSERVED_UTC' "
                               f"OR r.{column}{comparison}?)")
                args.append(value)
        where = " AND ".join(clauses)
        source = "memory_records r LEFT JOIN memory_semantic_facts p ON p.memory_id=r.memory_id"
        with closing(self._connect()) as db:
            db.row_factory = sqlite3.Row
            db.execute("PRAGMA query_only=ON")
            db.execute("BEGIN")
            # Neither branch is prefiltered by lexical hits. No identity columns or
            # evidence/subject child rows are loaded for ranking.
            eligible = [dict(row) for row in db.execute(
                "SELECT r.memory_id,r.memory_type,r.summary,r.confidence,r.importance,r.learned_at,"
                f"r.status FROM {source} WHERE {where}", args)]
            certificates = {row["memory_id"]: self._source_certificate(db, row["memory_id"], observer)
                            for row in eligible}
            eligible = [row for row in eligible if certificates[row["memory_id"]] is not None]
            # Old event-keyed self-records are not migrated. Group only certified
            # exact claims before top-k, preserving all their raw evidence links.
            groups = {}
            members = {}
            for row in sorted(eligible, key=lambda r: (r["learned_at"], r["memory_id"])):
                content = row["summary"]
                if content.startswith(NPC_SELF_STATEMENT_PREFIX):
                    content = content[len(NPC_SELF_STATEMENT_PREFIX):]
                key = (row["memory_type"], row["status"],
                       " ".join(unicodedata.normalize("NFKC", content).split()).casefold())
                if row["memory_type"] != "EPISODIC" or certificates[row["memory_id"]] != NPC_SELF_STATEMENT_LABEL:
                    key = (row["memory_id"],)
                representative = groups.setdefault(key, row)
                members.setdefault(representative["memory_id"], []).append(row["memory_id"])
            eligible = list(groups.values())
            ranked = self._ranker.rank(eligible, db, semantic_query, filters, budget)
            rows = [db.execute(
                "SELECT r.*,p.source_excerpt,p.excerpt_event_id,p.valid_from,p.valid_to,"
                f"p.temporal_precision FROM {source} WHERE r.memory_id=?", (memory_id,)
            ).fetchone() for memory_id in ranked.ids[:budget.top_k]]
            output = []
            for row in rows:
                subjects = tuple(TrustedPersistentIdentity(*value) for value in db.execute(
                    "SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects "
                    "WHERE memory_id=? ORDER BY identity_namespace,identity_type,persistent_id",
                    (row["memory_id"],)))
                evidence = db.execute(
                    "SELECT experience_event_id,experience_seq FROM memory_evidence "
                    "WHERE memory_id IN (" + ",".join("?" for _ in members[row["memory_id"]]) + ") "
                    "GROUP BY experience_event_id,experience_seq ORDER BY experience_seq,experience_event_id LIMIT ?",
                    (*members[row["memory_id"]], budget.max_evidence_refs_per_item + 1),
                ).fetchall()
                record = dict(row)
                if row['memory_type']=='DERIVED_INSIGHT':
                    record.update(self._insight_metadata(db,row['memory_id'],observer))
                # Relations are interpretations, never world truth. Hydrate a
                # bounded same-owner/same-subject neighborhood, including labeled
                # historical counterparts even when Current excludes their rows.
                relations = []
                linked = db.execute(
                    "SELECT l.relation,r.memory_id,r.summary,r.status,r.provenance_label "
                    "FROM memory_relations l JOIN memory_records r ON r.memory_id=CASE "
                    "WHEN l.new_memory_id=? THEN l.old_memory_id ELSE l.new_memory_id END "
                    "WHERE l.active=1 AND (l.new_memory_id=? OR l.old_memory_id=?) "
                    "AND r.identity_namespace=? AND r.identity_type=? AND r.persistent_id=? "
                    "AND r.scope='NPC_PRIVATE' AND r.memory_type='SEMANTIC_FACT' "
                    "ORDER BY l.relation_id LIMIT 9", (row['memory_id'],row['memory_id'],row['memory_id'],*owner)).fetchall()
                for peer in linked:
                    peer_subjects=tuple(TrustedPersistentIdentity(*s) for s in db.execute(
                        "SELECT identity_namespace,identity_type,persistent_id FROM memory_subjects "
                        "WHERE memory_id=? ORDER BY identity_namespace,identity_type,persistent_id",(peer['memory_id'],)))
                    peer_label=self._source_certificate(db,peer['memory_id'],observer)
                    if peer_subjects != subjects or peer_label is None or peer_label == 'SOURCE_UNVERIFIED':
                        continue
                    relations.append(dict(relation=peer['relation'],related_memory_id=peer['memory_id'],
                        related_summary=peer['summary'],related_lifecycle_status=peer['status'],
                        related_provenance_label=peer_label or peer['provenance_label']))
                record['maintenance_relations']=relations[:8]
                record['maintenance_relations_truncated']=len(linked)>8
                label = certificates[row["memory_id"]]
                if label:
                    record["provenance_label"] = label
                output.append(CanonicalReadCandidate(
                    record, subjects, tuple(dict(value) for value in evidence[:budget.max_evidence_refs_per_item]),
                    len(evidence) > budget.max_evidence_refs_per_item))
            return CanonicalReadBatch(tuple(output), len(eligible), len(ranked.ids),
                                      ranked.truncated or len(ranked.ids) > budget.top_k,
                                      ranked.embedding_unavailable)
