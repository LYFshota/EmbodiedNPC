"""SQLite persistence for one Reflection result per durable pair and Episode."""

from __future__ import annotations

import json
import sqlite3
from collections.abc import Callable
from contextlib import closing
from typing import TYPE_CHECKING

from .canonical_memory_read_repository import identity_parts
from .memory_consolidation_context import (
    TrustedPersistentIdentity, MemoryContextResolver, MemoryConsolidationContextError,
)
from .relationship_reflection_models import RelationshipReflectionRecord

from .relationship_pair_state import PAIR_STATE_MAX_ITEMS, PairState, PairStateItem, StateEvidence
from .experience_ledger import ExperienceLedger
from .episode_repository import EpisodeRepository
from .relationship_reflection_candidate import (
    RELATIONSHIP_REFLECTION_POLICY_VERSION,
    RelationshipReflectionCandidate, RelationshipReflectionCandidateBuilder, RelationshipReflectionCandidateError,
)


if TYPE_CHECKING:
    from .relationship_reflection_model import RelationshipReflectionModelResponse


_PAIR = """observer_identity_namespace=? AND observer_identity_type=? AND observer_persistent_id=?
    AND subject_identity_namespace=? AND subject_identity_type=? AND subject_persistent_id=?"""


class PairUpdateRejected(ValueError):
    pass


class RelationshipReflectionRepository:
    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]) -> None:
        self._connection_factory = connection_factory

    @staticmethod
    def _record(row: sqlite3.Row) -> RelationshipReflectionRecord:
        return RelationshipReflectionRecord(
            reflection_id=row["reflection_id"],
            observer=TrustedPersistentIdentity(
                row["observer_identity_namespace"], row["observer_identity_type"],
                row["observer_persistent_id"],
            ),
            subject=TrustedPersistentIdentity(
                row["subject_identity_namespace"], row["subject_identity_type"],
                row["subject_persistent_id"],
            ),
            episode_id=row["episode_id"], episode_input_hash=row["episode_input_hash"],
            outcome=row["outcome"], interpretation=row["interpretation"],
            evidence_event_ids=tuple(json.loads(row["evidence_event_ids_json"])),
            policy_version=row["policy_version"], generated_at=row["generated_at"],
        )

    def insert(self, record: RelationshipReflectionRecord) -> None:
        observer = identity_parts(record.observer)
        subject = identity_parts(record.subject)
        with closing(self._connection_factory()) as connection:
            with connection:
                connection.execute(
                    """
                    INSERT INTO relationship_reflections (
                        reflection_id,
                        observer_identity_namespace, observer_identity_type, observer_persistent_id,
                        subject_identity_namespace, subject_identity_type, subject_persistent_id,
                        episode_id, episode_input_hash, outcome, interpretation,
                        evidence_event_ids_json, policy_version, generated_at
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        record.reflection_id, *observer, *subject, record.episode_id,
                        record.episode_input_hash, record.outcome.value, record.interpretation,
                        json.dumps(record.evidence_event_ids, ensure_ascii=False, separators=(",", ":")),
                        record.policy_version, record.generated_at,
                    ),
                )

    def source_sessions(self, evidence: tuple[StateEvidence, ...]) -> tuple[str, ...]:
        """Read clocks of already pair-validated immutable anchors, without deriving intent."""
        sessions = set()
        with closing(self._connection_factory()) as connection:
            for ref in evidence:
                row = connection.execute(
                    "SELECT source_session_id,payload_hash FROM experience_events WHERE event_id=?",
                    (ref.event_id,)).fetchone()
                if row is None or row["payload_hash"] != ref.payload_hash:
                    return ()  # Missing provenance must not become an earlier/current visit claim.
                sessions.add(row["source_session_id"])
        return tuple(sorted(sessions))

    def get(self, reflection_id: str) -> RelationshipReflectionRecord | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT * FROM relationship_reflections WHERE reflection_id=?", (reflection_id,)
            ).fetchone()
        return None if row is None else self._record(row)

    def exists_for_episode_pair(
        self, observer: TrustedPersistentIdentity, subject: TrustedPersistentIdentity,
        episode_id: str, policy_version: str,
    ) -> bool:
        # A policy upgrade must not re-infer already processed legacy Episodes.
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                """
                SELECT 1 FROM relationship_reflections
                WHERE observer_identity_namespace=? AND observer_identity_type=?
                  AND observer_persistent_id=? AND subject_identity_namespace=?
                  AND subject_identity_type=? AND subject_persistent_id=?
                  AND episode_id=? AND (policy_version=? OR policy_version='relationship-reflection/1.0') LIMIT 1
                """,
                (*identity_parts(observer), *identity_parts(subject), episode_id, policy_version),
            ).fetchone()
        return row is not None

    def list_recent_interpretations(
        self, observer: TrustedPersistentIdentity, subject: TrustedPersistentIdentity,
        *, limit: int = 5,
    ) -> tuple[RelationshipReflectionRecord, ...]:
        if not 1 <= limit <= 64:
            raise ValueError("RELATIONSHIP_READ_LIMIT_OUT_OF_RANGE")
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """
                SELECT * FROM relationship_reflections
                WHERE observer_identity_namespace=? AND observer_identity_type=?
                  AND observer_persistent_id=? AND subject_identity_namespace=?
                  AND subject_identity_type=? AND subject_persistent_id=?
                  AND outcome='INTERPRETATION'
                ORDER BY generated_at DESC, reflection_id DESC LIMIT ?
                """,
                (*identity_parts(observer), *identity_parts(subject), limit),
            ).fetchall()
        # The bounded newest set is returned in source chronology for a future Summary reader.
        return tuple(self._record(row) for row in reversed(rows))

    @staticmethod
    def _pair(observer, subject):
        if not isinstance(observer, TrustedPersistentIdentity) or not isinstance(subject, TrustedPersistentIdentity):
            raise TypeError("TRUSTED_RELATIONSHIP_IDENTITIES_REQUIRED")
        if ((observer.identity_namespace, observer.identity_type) != ("npc", "Character")
                or (subject.identity_namespace, subject.identity_type) != ("player", "Player")):
            raise PairUpdateRejected("NPC_PLAYER_PAIR_REQUIRED")
        return (*identity_parts(observer), *identity_parts(subject))

    @staticmethod
    def _valid_source(connection, evidence, observer, subject):
        row = connection.execute("SELECT * FROM experience_events WHERE event_id=?",
                                 (evidence.event_id,)).fetchone()
        episode = connection.execute("SELECT * FROM episodes WHERE episode_id=?",
                                     (evidence.episode_id,)).fetchone()
        if row is None or episode is None or episode["status"] != "CLOSED":
            return False
        if (row["payload_hash"] != evidence.payload_hash or row["observer_id"] != episode["observer_id"]
                or not episode["start_seq"] <= row["experience_seq"] <= episode["end_seq"]
                or row["event_type"] != "dialogue_input" or row["source_type"] != "dialogue"):
            return False
        payload = json.loads(row["payload_json"])
        identities = {v["entity_id"]: (v["identity_namespace"], v["identity_type"], v["persistent_id"])
                      for v in payload.get("persistent_participants", [])}
        return bool(identities.get(row["observer_id"]) == identity_parts(observer)
            and identities.get(payload["actor_entity_id"]) == identity_parts(subject)
            and row["observer_id"] in payload["target_entity_ids"]
            and payload["structured_payload"].get("initiative") not in ("proactive", "dialogue_continuation")
            and evidence.quote in (payload.get("natural_language_payload") or ""))

    def _current(self, connection, observer, subject):
        pair = self._pair(observer, subject)
        row = connection.execute("SELECT * FROM relationship_reflections WHERE " + _PAIR +
            " AND state_revision IS NOT NULL ORDER BY state_revision DESC LIMIT 1", pair).fetchone()
        if row is None:
            legacy = connection.execute("SELECT * FROM relationship_reflections WHERE " + _PAIR +
                " ORDER BY generated_at DESC,reflection_id DESC LIMIT 1", pair).fetchone()
            interaction = None
            if legacy:
                # Preserve validated historical interaction without promoting old impressions
                # to a complete current state or scheduling paid history reprocessing.
                for event_id in reversed(json.loads(legacy["evidence_event_ids_json"])):
                    source = connection.execute("SELECT * FROM experience_events WHERE event_id=?",
                                                (event_id,)).fetchone()
                    if source is None:
                        continue
                    text = json.loads(source["payload_json"]).get("natural_language_payload")
                    if not text or not text.strip() or len(text) > 1000:
                        continue
                    ref = StateEvidence(event_id=event_id, episode_id=legacy["episode_id"],
                                        payload_hash=source["payload_hash"], quote=text)
                    if self._valid_source(connection, ref, observer, subject):
                        interaction = ref
                        break
            return PairState(coverage="LEGACY_PARTIAL" if legacy else "SINCE_B1",
                             interaction_evidence=interaction)
        state = PairState.model_validate_json(row["pair_state_json"])
        items = tuple(item for item in state.items if all(
            self._valid_source(connection, ref, observer, subject) for ref in item.evidence))
        interaction = state.interaction_evidence
        if interaction and not self._valid_source(connection, interaction, observer, subject):
            interaction = None
        if items != state.items or interaction != state.interaction_evidence:
            state = state.model_copy(update=dict(items=items, interaction_evidence=interaction,
                                                coverage="SOURCE_UNAVAILABLE"))
        return state

    def read_current_pair_state(
        self, observer: TrustedPersistentIdentity, subject: TrustedPersistentIdentity,
    ) -> PairState:
        """Read the latest pair snapshot and revalidate its bounded raw sources."""
        with closing(self._connection_factory()) as connection:
            with connection:
                connection.execute("BEGIN")
                return self._current(connection, observer, subject)

    def finalize_pair_update(
        self, record: RelationshipReflectionRecord, candidate: RelationshipReflectionCandidate,
        response: "RelationshipReflectionModelResponse", previous: PairState,
    ) -> PairState:
        """No model under lock: authenticate evidence, CAS and insert one snapshot atomically."""
        from .memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION
        from pydantic import ValidationError

        with closing(self._connection_factory()) as connection:
            connection.execute("BEGIN IMMEDIATE")
            try:
                pair = self._pair(record.observer, record.subject)
                current = self._current(connection, record.observer, record.subject)
                if current != previous:
                    raise PairUpdateRejected("RELATIONSHIP_BASE_CHANGED")
                if (record.policy_version != RELATIONSHIP_REFLECTION_POLICY_VERSION
                        or record.observer != candidate.observer or record.subject != candidate.subject
                        or record.episode_id != candidate.episode_id
                        or record.episode_input_hash != candidate.episode_input_hash
                        or record.evidence_event_ids != candidate.evidence_event_ids
                        or record.outcome.value != response.outcome
                        or record.interpretation != response.interpretation):
                    raise PairUpdateRejected("RELATIONSHIP_RECORD_MISMATCH")
                episode_row = connection.execute("SELECT * FROM episodes WHERE episode_id=?",
                                                 (record.episode_id,)).fetchone()
                job = connection.execute("SELECT 1 FROM memory_jobs WHERE episode_id=? AND input_hash=? "
                    "AND status='SUCCEEDED' AND extractor_version=?", (record.episode_id,
                    record.episode_input_hash, SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)).fetchone()
                if episode_row is None or episode_row["status"] != "CLOSED" or job is None:
                    raise PairUpdateRejected("RELATIONSHIP_CLOSED_SUCCEEDED_EPISODE_REQUIRED")
                episode = EpisodeRepository._episode(episode_row)
                rows = connection.execute("SELECT rowid AS ledger_order,* FROM experience_events "
                    "WHERE observer_id=? AND experience_seq BETWEEN ? AND ? ORDER BY experience_seq",
                    (episode.observer_id, episode.start_seq, episode.end_seq)).fetchall()
                events = tuple(ExperienceLedger._row_to_event(row) for row in rows)
                if (len(events) != episode.event_count or EpisodeRepository.input_hash_for(episode, events)
                        != record.episode_input_hash):
                    raise PairUpdateRejected("RELATIONSHIP_EPISODE_SOURCE_CHANGED")
                resolver = MemoryContextResolver(ExperienceLedger(self._connection_factory),
                                                EpisodeRepository(self._connection_factory))
                context = resolver._build(episode, events, record.episode_input_hash,
                                          SEMANTIC_MEMORY_CONSOLIDATOR_VERSION)
                if candidate not in RelationshipReflectionCandidateBuilder().build(context):
                    raise PairUpdateRejected("RELATIONSHIP_CANDIDATE_SOURCE_CHANGED")
                order = max(row["ledger_order"] for row in rows)
                items = list(current.items)
                capacity_limited = None
                updates = response.state_updates
                if response.outcome == "NO_CHANGE" and updates:
                    raise PairUpdateRejected("NO_CHANGE_STATE_UPDATE_FORBIDDEN")
                if order <= current.watermark:
                    updates = ()  # Mark late work processed, never roll current state back.
                targets = set()
                for number, update in enumerate(updates):
                    event = next((e for i, e in enumerate(candidate.evidence_events)
                                  if update.event == f"e{i}"), None)
                    if event is None:
                        raise PairUpdateRejected("RELATIONSHIP_EVENT_HANDLE_INVALID")
                    row = next(r for r in rows if r["event_id"] == event.event_id)
                    ref = StateEvidence(event_id=event.event_id, episode_id=record.episode_id,
                                        payload_hash=row["payload_hash"], quote=update.quote)
                    if not self._valid_source(connection, ref, record.observer, record.subject):
                        raise PairUpdateRejected("RELATIONSHIP_PLAYER_QUOTE_REQUIRED")
                    old = None
                    if update.target is not None:
                        old = next((v for i, v in enumerate(current.items)
                                    if update.target == f"s{i}"), None)
                        if old is None or update.target in targets:
                            raise PairUpdateRejected("RELATIONSHIP_STATE_HANDLE_INVALID")
                        targets.add(update.target)
                    kind = "CONFLICT" if update.operation == "CONFLICT" else update.kind
                    if old and old.text == update.text and old.kind == kind:
                        continue  # An identical reading is not a new change or independent support.
                    # An explicit correction stands on its new direct evidence. Prior snapshots
                    # retain the replaced evidence; unresolved conflict must carry both sides.
                    evidence = (tuple(dict.fromkeys((*old.evidence, ref)))
                                if old and update.operation == "CONFLICT" else (ref,))
                    item = PairStateItem(item_id=f"{record.reflection_id}:{number}",
                        kind=kind, change_type=update.operation,
                        text=update.text, evidence=evidence,
                        predecessor_id=old.item_id if old else None,
                        predecessor_text=old.text if old else None)
                    if old:
                        items[items.index(old)] = item
                    elif not any(v.kind == item.kind and v.text == item.text for v in items):
                        if len(items) >= PAIR_STATE_MAX_ITEMS:
                            # Source checks and CAS have already succeeded. Retain the exact
                            # proposal as an unapplied diagnostic, not a NO_CHANGE rewrite.
                            capacity_limited = item
                        else:
                            items.append(item)
                # The newest real Player input proves interaction, never a visit count or intimacy.
                interaction = current.interaction_evidence
                if order > current.watermark:
                    for event in reversed(candidate.evidence_events):
                        if event.event_type == "dialogue_input":
                            row = next(r for r in rows if r["event_id"] == event.event_id)
                            text = event.natural_language_payload or ""
                            # Interaction provenance keeps the whole utterance, never a clipped claim.
                            if text.strip():
                                interaction = StateEvidence(event_id=event.event_id,
                                    episode_id=record.episode_id, payload_hash=row["payload_hash"], quote=text)
                            break
                state = PairState(revision=current.revision+1, reflection_id=record.reflection_id,
                    items=tuple(items), capacity_limited=capacity_limited,
                    interaction_evidence=interaction, coverage=current.coverage,
                    late_episode_skipped=current.late_episode_skipped or order <= current.watermark,
                    watermark=max(current.watermark, order), changed=tuple(items) != current.items)
                connection.execute("""INSERT INTO relationship_reflections (
                    reflection_id,observer_identity_namespace,observer_identity_type,observer_persistent_id,
                    subject_identity_namespace,subject_identity_type,subject_persistent_id,
                    episode_id,episode_input_hash,outcome,interpretation,evidence_event_ids_json,
                    policy_version,generated_at,pair_state_json,state_revision,base_reflection_id)
                    VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", (
                    record.reflection_id,*pair,record.episode_id,record.episode_input_hash,
                    record.outcome.value,record.interpretation,json.dumps(record.evidence_event_ids),
                    record.policy_version,record.generated_at,state.model_dump_json(),state.revision,
                    current.reflection_id))
                connection.commit()
                return state
            except (MemoryConsolidationContextError, RelationshipReflectionCandidateError) as exc:
                connection.rollback()
                raise PairUpdateRejected("RELATIONSHIP_SOURCE_IDENTITY_INVALID") from exc
            except ValidationError as exc:
                connection.rollback()
                raise PairUpdateRejected("RELATIONSHIP_STATE_CAPACITY_OR_SHAPE_INVALID") from exc
            except BaseException:
                connection.rollback()
                raise
