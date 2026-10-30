"""Transactional SQLite append-only ledger for observer-local raw experience."""

from __future__ import annotations

import json
import sqlite3
import time
from collections.abc import Callable
from contextlib import closing

from .experience_models import ExperienceDraft, ObserverExperienceEvent
from .recent_dialogue_context import RecentDialogueTimeline


class ExperienceIdConflict(ValueError):
    def __init__(self, event_id: str, reason: str) -> None:
        super().__init__(reason)
        self.event_id = event_id
        self.reason = reason


class RecentDialogueProjection(list[dict]):
    """List-compatible read view; omission metadata never contains private text/IDs."""

    def __init__(self, turns=(), *, truncated=False):
        super().__init__(turns)
        self.truncated = truncated


class ExperienceLedger:
    """Owns append identity, per-observer sequence allocation and replay reads."""

    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]) -> None:
        self._connection_factory = connection_factory

    @staticmethod
    def _payload_json(draft: ExperienceDraft) -> str:
        payload = {
            "source_observation_ids": sorted(draft.source_observation_ids),
            "actor_entity_id": draft.actor_entity_id,
            "target_entity_ids": sorted(draft.target_entity_ids),
            "persistent_participants": sorted(
                (value.model_dump(mode="json") for value in draft.persistent_participants),
                key=lambda value: value["entity_id"],
            ),
            "structured_payload": draft.structured_payload,
            "natural_language_payload": draft.natural_language_payload,
        }
        return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))

    @staticmethod
    def _row_to_event(row: sqlite3.Row) -> ObserverExperienceEvent:
        payload = json.loads(row["payload_json"])
        return ObserverExperienceEvent(
            schema_version=row["schema_version"],
            event_id=row["event_id"],
            observer_id=row["observer_id"],
            experience_seq=row["experience_seq"],
            event_type=row["event_type"],
            source_type=row["source_type"],
            source_session_id=row["source_session_id"],
            clock_domain=row["clock_domain"],
            source_event_id=row["source_event_id"],
            source_observation_ids=tuple(payload["source_observation_ids"]),
            actor_entity_id=payload["actor_entity_id"],
            target_entity_ids=tuple(payload["target_entity_ids"]),
            persistent_participants=tuple(payload.get("persistent_participants", ())),
            structured_payload=payload["structured_payload"],
            natural_language_payload=payload["natural_language_payload"],
            occurred_at=row["occurred_at"],
            observed_at=row["observed_at"],
            ingested_at_utc=row["ingested_at_utc"],
            world_revision=row["world_revision"],
            request_id=row["request_id"],
            trace_id=row["trace_id"],
            conversation_id=row["conversation_id"],
            confidence=row["confidence"],
            provenance=row["provenance"],
            dedup_key=row["dedup_key"],
            payload_hash=row["payload_hash"],
        )

    def append(
        self,
        draft: ExperienceDraft,
        *,
        event_id: str,
        dedup_key: str,
        payload_hash: str,
    ) -> tuple[str, ObserverExperienceEvent]:
        payload_json = self._payload_json(draft)
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                existing = connection.execute(
                    "SELECT * FROM experience_events WHERE event_id = ? OR dedup_key = ?",
                    (event_id, dedup_key),
                ).fetchone()
                if existing is not None:
                    if existing["event_id"] == event_id and existing["dedup_key"] == dedup_key and \
                            existing["payload_hash"] == payload_hash:
                        connection.commit()
                        return "already_exists", self._row_to_event(existing)
                    connection.rollback()
                    raise ExperienceIdConflict(existing["event_id"], "EXPERIENCE_ID_PAYLOAD_MISMATCH")

                if draft.structured_payload.get("initiative") == "dialogue_continuation":
                    try:
                        parent, anchor = self._continuation_anchor(
                            connection, draft.observer_id, draft.conversation_id,
                            draft.structured_payload, draft.target_entity_ids[0])
                        expected = {p.entity_id: p for p in parent.persistent_participants}
                        if ({p.entity_id: p for p in draft.persistent_participants} != expected
                                or draft.source_session_id != parent.source_session_id
                                or draft.clock_domain != parent.clock_domain
                                or draft.occurred_at < anchor.occurred_at):
                            raise ValueError("DIALOGUE_CONTINUATION_IDENTITY_MISMATCH")
                        siblings = connection.execute(
                            "SELECT payload_json FROM experience_events WHERE observer_id = ? "
                            "AND conversation_id = ? AND source_session_id = ? AND event_type = 'dialogue_response' "
                            "AND json_extract(payload_json, '$.structured_payload.parent_request_id') = ?",
                            (draft.observer_id, draft.conversation_id, draft.source_session_id, parent.request_id)).fetchall()
                        if any(json.loads(row[0])["structured_payload"].get("initiative") == "dialogue_continuation"
                               and json.loads(row[0])["structured_payload"].get("parent_request_id") == parent.request_id
                               for row in siblings):
                            raise ValueError("DIALOGUE_CONTINUATION_BUDGET_EXHAUSTED")
                    except ValueError as error:
                        raise ExperienceIdConflict(event_id, str(error)) from error

                if draft.event_type == "social_response":
                    from .memory_consolidation_context import correlated_social_pair
                    parents = connection.execute(
                        "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
                        "AND source_event_id=? AND event_type='observation' LIMIT 2",
                        (draft.observer_id, draft.source_session_id, draft.source_event_id)).fetchall()
                    if len(parents) != 1 or not correlated_social_pair(self._row_to_event(parents[0]), draft):
                        raise ExperienceIdConflict(event_id, "SOCIAL_RESPONSE_PARENT_MISMATCH")
                    parent = self._row_to_event(parents[0])
                    expected = {p.entity_id: p for p in parent.persistent_participants}
                    if any(expected.get(p.entity_id) != p for p in draft.persistent_participants):
                        raise ExperienceIdConflict(event_id, "SOCIAL_RESPONSE_IDENTITY_MISMATCH")

                sequence = int(connection.execute(
                    """
                    INSERT INTO experience_observer_sequences(observer_id, last_seq)
                    VALUES(?, 1)
                    ON CONFLICT(observer_id) DO UPDATE SET last_seq = last_seq + 1
                    RETURNING last_seq
                    """,
                    (draft.observer_id,),
                ).fetchone()[0])
                ingested_at = time.time()
                connection.execute(
                    """
                    INSERT INTO experience_events(
                        event_id, observer_id, experience_seq, dedup_key, payload_hash,
                        event_type, source_type, source_session_id, source_event_id,
                        occurred_at, observed_at, ingested_at_utc, clock_domain,
                        world_revision, request_id, trace_id, conversation_id,
                        provenance, confidence, payload_json, schema_version
                    ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        event_id, draft.observer_id, sequence, dedup_key, payload_hash,
                        draft.event_type, draft.source_type, draft.source_session_id,
                        draft.source_event_id, draft.occurred_at, draft.observed_at,
                        ingested_at, draft.clock_domain, draft.world_revision, draft.request_id,
                        draft.trace_id, draft.conversation_id, draft.provenance,
                        draft.confidence, payload_json,
                        "observer-experience/1.1" if draft.schema_version == "experience-draft/1.1"
                        else "observer-experience/1.0",
                    ),
                )
                row = connection.execute(
                    "SELECT * FROM experience_events WHERE event_id = ?", (event_id,)
                ).fetchone()
                connection.commit()
                return "inserted", self._row_to_event(row)
            except Exception:
                connection.rollback()
                raise

    def read_dialogue_history(self, observer_id: str, conversation_id: str,
                              after_seq: int = 0, limit: int = 100) -> list[dict]:
        """Read display data only; conversation remains metadata, not an episode boundary."""
        if not observer_id or not conversation_id:
            raise ValueError("OBSERVER_AND_CONVERSATION_REQUIRED")
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """SELECT event_id, experience_seq, event_type, payload_json, ingested_at_utc
                   FROM experience_events
                   WHERE observer_id = ? AND conversation_id = ? AND experience_seq > ?
                     AND event_type IN ('dialogue_input', 'dialogue_response')
                   ORDER BY experience_seq ASC LIMIT ?""",
                (observer_id, conversation_id, max(0, after_seq), max(1, min(limit, 100))),
            ).fetchall()
        result = []
        for row in rows:
            payload = json.loads(row["payload_json"])
            result.append({
                "event_id": row["event_id"], "experience_seq": row["experience_seq"],
                "role": "player" if row["event_type"] == "dialogue_input" else "npc",
                "actor": payload["actor_entity_id"],
                "text": payload["natural_language_payload"] or "",
                "timestamp": row["ingested_at_utc"],
            })
        return result

    @classmethod
    def _continuation_anchor(cls, connection, observer_id, conversation_id, payload, player_id):
        rows = connection.execute(
            "SELECT * FROM experience_events WHERE observer_id = ? AND conversation_id = ? "
            "AND event_type = 'dialogue_input' AND request_id = ? AND source_event_id = ?",
            (observer_id, conversation_id, payload.get("parent_request_id"),
             payload.get("parent_event_id"))).fetchall()
        anchor_row = connection.execute("SELECT * FROM experience_events WHERE event_id = ?",
                                        (payload.get("last_response_event_id"),)).fetchone()
        if len(rows) != 1 or anchor_row is None:
            raise ValueError("DIALOGUE_CONTINUATION_ANCHOR_MISSING")
        parent, anchor = cls._row_to_event(rows[0]), cls._row_to_event(anchor_row)
        if (parent.actor_entity_id != player_id or parent.target_entity_ids != (observer_id,)
                or anchor.observer_id != observer_id or anchor.conversation_id != conversation_id
                or anchor.event_type != "dialogue_response" or anchor.actor_entity_id != observer_id
                or anchor.target_entity_ids != (player_id,) or anchor.request_id != parent.request_id
                or anchor.source_event_id != parent.source_event_id
                or anchor.source_session_id != parent.source_session_id
                or anchor.clock_domain != parent.clock_domain
                or anchor.experience_seq != payload.get("last_response_seq")
                or anchor.experience_seq <= parent.experience_seq
                or anchor.structured_payload.get("initiative") in ("proactive", "dialogue_continuation")
                or {p.entity_id: p for p in parent.persistent_participants} !=
                   {p.entity_id: p for p in anchor.persistent_participants}
                or len(parent.persistent_participants) != 2):
            raise ValueError("DIALOGUE_CONTINUATION_ANCHOR_INVALID")
        return parent, anchor

    def read_continuation_dialogue(self, observer_id, conversation_id, payload, player_id, identities,
                                   **recent_limits):
        with closing(self._connection_factory()) as connection:
            parent, anchor = self._continuation_anchor(connection, observer_id, conversation_id,
                                                       payload, player_id)
        if payload.get("source_session_id") != parent.source_session_id:
            raise ValueError("DIALOGUE_CONTINUATION_SESSION_INVALID")
        expected = {p.entity_id: (p.identity_namespace, p.identity_type, p.persistent_id)
                    for p in parent.persistent_participants}
        if expected != identities:
            raise ValueError("DIALOGUE_CONTINUATION_CURRENT_IDENTITY_MISMATCH")
        return self.read_recent_dialogue(observer_id, conversation_id, parent.request_id,
                                        parent.source_event_id, continuation_anchor=anchor,
                                        identities=identities, **recent_limits)

    def require_dialogue_response(self, observer_id, conversation_id, request_id, source_session_id, player_id):
        with closing(self._connection_factory()) as connection:
            rows = connection.execute("SELECT * FROM experience_events WHERE observer_id = ? "
                                      "AND conversation_id = ? AND request_id = ? AND source_session_id = ? "
                                      "AND event_type = 'dialogue_response'", (observer_id, conversation_id,
                                                                             request_id, source_session_id)).fetchall()
        event = self._row_to_event(rows[0]) if len(rows) == 1 else None
        if (event is None or event.observer_id != observer_id or event.conversation_id != conversation_id
                or event.event_type != "dialogue_response" or event.actor_entity_id != observer_id
                or event.target_entity_ids != (player_id,)):
            raise ValueError("REQUIRED_DISPLAYED_RESPONSE_NOT_COMMITTED")

    def read_recent_dialogue(self, observer_id: str, conversation_id: str,
                             current_request_id: str, current_event_id: str,
                             limit: int = 8, max_chars: int = 1200, *,
                              continuation_anchor=None, identities=None, timeline=False,
                              boundary_overlap=2) -> list[dict]:
        """Verified prior speech, or a compatible turn view, before the accepted cutoff.

        Experience sequence is ingest order. Correlation owns pairing, while the
        current input owns the cutoff. A missing/ambiguous current input cannot
        certify a stable history and therefore yields no model-visible history.
        """
        if not observer_id or not conversation_id or not current_request_id or not current_event_id:
            return []
        # The legacy turn view remains compatible. Speech windows require the
        # stronger E1 identity path, and reuse its existing 128-input/512-reply scan.
        if timeline and identities is None:
            return RecentDialogueTimeline()
        boundary_overlap = max(0, min(boundary_overlap, 2))
        max_messages = max(1, min(limit, 64 if timeline else 16))
        char_budget = max(1, min(max_chars, 2400))
        if identities is not None:
            return self._read_identity_recent(observer_id, conversation_id, current_request_id,
                current_event_id, identities, max_messages, char_budget, continuation_anchor,
                timeline=timeline, boundary_overlap=boundary_overlap)
        with closing(self._connection_factory()) as connection:
            current = connection.execute(
                """SELECT experience_seq, source_session_id, clock_domain, observed_at, payload_json
                   FROM experience_events
                   WHERE observer_id = ? AND conversation_id = ? AND event_type = 'dialogue_input'
                     AND (request_id = ? OR source_event_id = ?)
                   ORDER BY experience_seq ASC LIMIT 2""",
                (observer_id, conversation_id, current_request_id, current_event_id),
            ).fetchall()
            if len(current) != 1:
                return []
            boundary = dict(current[0])
            if continuation_anchor is not None:
                boundary["experience_seq"] = continuation_anchor.experience_seq + 1
                boundary["observed_at"] = continuation_anchor.observed_at
                current_request_id, current_event_id = "", ""
            inputs = connection.execute(
                """SELECT experience_seq, request_id, source_event_id, payload_json
                   FROM experience_events
                   WHERE observer_id = ? AND conversation_id = ? AND event_type = 'dialogue_input'
                     AND experience_seq < ? AND request_id <> ?
                     AND (source_event_id IS NULL OR source_event_id <> ?)
                     AND source_session_id = ? AND clock_domain = ? AND observed_at <= ?
                   ORDER BY experience_seq DESC LIMIT ?""",
                (observer_id, conversation_id, boundary["experience_seq"], current_request_id,
                 current_event_id, boundary["source_session_id"], boundary["clock_domain"],
                 boundary["observed_at"], max_messages),
            ).fetchall()
            current_payload = json.loads(boundary["payload_json"])
            inputs = [row for row in inputs if
                      json.loads(row["payload_json"])["actor_entity_id"] == current_payload["actor_entity_id"]
                      and json.loads(row["payload_json"])["target_entity_ids"] == current_payload["target_entity_ids"]
                      and json.loads(row["payload_json"]).get("persistent_participants") ==
                          current_payload.get("persistent_participants")]
            if not inputs:
                return []
            placeholders = ",".join("?" for _ in inputs)
            responses = connection.execute(
                f"""SELECT experience_seq, request_id, source_event_id, payload_json
                   FROM experience_events
                   WHERE observer_id = ? AND conversation_id = ? AND event_type = 'dialogue_response'
                     AND experience_seq < ? AND source_session_id = ? AND clock_domain = ?
                     AND occurred_at <= ? AND request_id <> ?
                     AND (source_event_id IS NULL OR source_event_id <> ?)
                     AND (request_id IN ({placeholders}) OR
                          json_extract(payload_json, '$.structured_payload.parent_request_id') IN ({placeholders}))
                   ORDER BY experience_seq ASC""",
                (observer_id, conversation_id, boundary["experience_seq"],
                 boundary["source_session_id"], boundary["clock_domain"],
                 boundary["observed_at"], current_request_id, current_event_id,
                 *(row["request_id"] for row in inputs), *(row["request_id"] for row in inputs)),
            ).fetchall()
        # A response without the matching accepted input is not a prior turn.
        # Both existing correlation IDs must agree; role alternation and timing
        # never infer a pair. The first valid response wins if data is anomalous.
        by_turn = {}
        input_keys = {(row["request_id"], row["source_event_id"]) for row in inputs}
        for row in responses:
            key = (row["request_id"], row["source_event_id"])
            payload = json.loads(row["payload_json"])
            metadata = payload["structured_payload"]
            if metadata.get("initiative") == "dialogue_continuation":
                key = (metadata.get("parent_request_id"), metadata.get("parent_event_id"))
                # Appends already validate lineage; still check row participants for legacy/anomalous data.
                parent_row = next((r for r in inputs if (r["request_id"], r["source_event_id"]) == key), None)
                if parent_row is None:
                    continue
                parent_payload = json.loads(parent_row["payload_json"])
                if (payload["actor_entity_id"] != observer_id
                        or payload["target_entity_ids"] != [parent_payload["actor_entity_id"]]
                        or payload.get("persistent_participants") != parent_payload.get("persistent_participants")):
                    continue
                by_turn.setdefault(key, []).append(row)
                continue
            if key in input_keys and key not in by_turn:
                by_turn[key] = [row]
        return self._project_dialogue(inputs, by_turn, max_messages, char_budget)

    @staticmethod
    def _participant_map(payload):
        values = payload.get("persistent_participants", ())
        if not isinstance(values, (list, tuple)) or len(values) != 2:
            return None
        result = {}
        for value in values:
            if not isinstance(value, dict):
                return None
            entity = value.get("entity_id")
            identity = tuple(value.get(k) for k in ("identity_namespace", "identity_type", "persistent_id"))
            if entity in result or not entity or any(not isinstance(v, str) or not v.strip() for v in identity):
                return None
            result[entity] = identity
        return result

    @staticmethod
    def _turn_key(row):
        return (row["source_session_id"], row["conversation_id"], row["clock_domain"],
                row["request_id"], row["source_event_id"])

    def _read_identity_recent(self, observer, conversation, request_id, event_id, identities,
                              max_messages, char_budget, continuation_anchor, *, timeline=False,
                              boundary_overlap=2):
        """A bounded observer-local tail, authorized by current and historical durable identities.

        Caller-supplied trusted identities only broaden reads, never the ledger's append rules.
        Runtime NPC EntityId remains fixed in Cafe; Player EntityId may change across Sessions.
        """
        def empty(*, truncated=False):
            return (RecentDialogueTimeline(truncated=truncated, target=max_messages) if timeline
                    else RecentDialogueProjection(truncated=truncated))

        with closing(self._connection_factory()) as connection:
            connection.execute("BEGIN")
            current = connection.execute(
                "WITH request_hits AS (SELECT * FROM experience_events INDEXED BY idx_experience_request "
                "WHERE observer_id=? AND conversation_id=? AND event_type='dialogue_input' "
                "AND request_id=? LIMIT 2), source_hits AS (SELECT * FROM experience_events "
                "INDEXED BY idx_experience_observer_source WHERE observer_id=? AND conversation_id=? "
                "AND event_type='dialogue_input' AND source_type='dialogue' AND source_event_id=? LIMIT 2) "
                "SELECT * FROM request_hits UNION SELECT * FROM source_hits ORDER BY experience_seq LIMIT 2",
                (observer, conversation, request_id, observer, conversation, event_id)).fetchall()
            if len(current) != 1:
                return empty()
            boundary = current[0]
            payload = json.loads(boundary["payload_json"])
            player = payload["actor_entity_id"]
            if (boundary["source_type"] != "dialogue" or boundary["provenance"] != "SYSTEM_CONFIRMED"
                    or boundary["request_id"] != request_id or boundary["source_event_id"] != event_id
                    or payload["target_entity_ids"] != [observer] or player == observer
                    or self._participant_map(payload) != identities or set(identities) != {observer, player}
                    or identities[observer][:2] != ("npc", "Character")
                    or identities[player][:2] != ("player", "Player")):
                return empty()
            cutoff = boundary["experience_seq"]
            observed_at = boundary["observed_at"]
            if continuation_anchor is not None:
                cutoff = continuation_anchor.experience_seq + 1
                observed_at = continuation_anchor.observed_at
            # Scan bounds are independent of the requested model-visible speech window.
            # No new index/schema: the existing (observer_id, experience_seq) range is bounded.
            lower = max(1, cutoff - 4096)
            candidates = connection.execute(
                "SELECT * FROM experience_events WHERE observer_id=? AND experience_seq>=? "
                "AND experience_seq<? AND source_type='dialogue' AND event_type='dialogue_input' "
                "ORDER BY experience_seq DESC LIMIT 129", (observer, lower, cutoff)).fetchall()
            truncated = lower > 1 or len(candidates) > 128
            inputs = []
            for row in candidates[:128]:
                value = json.loads(row["payload_json"])
                historical_player = value["actor_entity_id"]
                expected = {observer: identities[observer], historical_player: identities[player]}
                if (historical_player == observer or value["target_entity_ids"] != [observer]
                        or row["provenance"] != "SYSTEM_CONFIRMED"
                        or not row["conversation_id"] or self._participant_map(value) != expected):
                    continue
                if row["source_session_id"] == boundary["source_session_id"] and (
                        row["clock_domain"] != boundary["clock_domain"] or row["observed_at"] > observed_at):
                    continue
                inputs.append(row)
            if not inputs:
                return empty(truncated=truncated)
            values = ",".join("(?,?,?,?,?,?)" for _ in inputs)
            args = tuple(v for row in inputs for v in (*self._turn_key(row), row["experience_seq"]))
            # Both ordinary IDs must match, within the original Session/Conversation/clock.
            # Continuations are linked by their persisted parent and displayed-response anchor.
            responses = connection.execute(
                f"WITH parents(session,conversation,clock,request,event,seq) AS (VALUES {values}) "
                "SELECT DISTINCT e.* FROM experience_events e JOIN parents p "
                "ON e.source_session_id=p.session AND e.conversation_id=p.conversation "
                "AND e.clock_domain=p.clock AND e.experience_seq>p.seq "
                "AND ((e.request_id=p.request AND e.source_event_id=p.event) OR "
                "(json_extract(e.payload_json,'$.structured_payload.initiative') IN ('dialogue_continuation','action_feedback') "
                "AND json_extract(e.payload_json,'$.structured_payload.parent_request_id')=p.request "
                "AND json_extract(e.payload_json,'$.structured_payload.parent_event_id')=p.event)) "
                "WHERE e.observer_id=? AND e.experience_seq>=? AND e.experience_seq<? "
                "AND e.source_type='dialogue' AND e.event_type='dialogue_response' "
                "ORDER BY e.experience_seq LIMIT 513", (*args, observer, lower, cutoff)).fetchall()
            if len(responses) > 512:
                return empty(truncated=True)
        by_turn = self._verified_dialogue_responses(inputs, responses, boundary, observed_at)
        if timeline:
            return self._project_timeline(inputs, by_turn, max_messages, char_budget,
                                          boundary_overlap, truncated=truncated,
                                          current_session_id=boundary["source_session_id"])
        return self._project_dialogue(inputs, by_turn, max_messages, char_budget,
                                      scoped=True, truncated=truncated)

    def _verified_dialogue_responses(self, inputs, responses, boundary, observed_at):
        parents = {}
        ambiguous = set()
        for row in inputs:
            key = self._turn_key(row)
            if key in parents:
                ambiguous.add(key)
            parents[key] = row
        ordinary, continuations, feedbacks = {}, {}, {}
        for row in responses:
            payload = json.loads(row["payload_json"])
            metadata = payload["structured_payload"]
            continuation = metadata.get("initiative") == "dialogue_continuation"
            key = self._turn_key(row)
            feedback = metadata.get("initiative") == "action_feedback"
            if continuation or feedback:
                key = (*key[:3], metadata.get("parent_request_id"), metadata.get("parent_event_id"))
            parent = parents.get(key)
            if parent is None or key in ambiguous or not parent["request_id"] or not parent["source_event_id"]:
                continue
            parent_payload = json.loads(parent["payload_json"])
            if (row["provenance"] != "EXECUTED_BY_SELF" or payload["actor_entity_id"] != parent["observer_id"]
                    or payload["target_entity_ids"] != [parent_payload["actor_entity_id"]]
                    or self._participant_map(payload) != self._participant_map(parent_payload)
                    or metadata.get("initiative") == "proactive"):
                continue
            if row["source_session_id"] == boundary["source_session_id"] and row["occurred_at"] > observed_at:
                continue
            if feedback and not self._verified_action_feedback(parent, row):
                continue
            (feedbacks if feedback else continuations if continuation else ordinary).setdefault(key, []).append(row)
        result = {}
        for key, replies in ordinary.items():
            if len(replies) != 1:  # Ambiguous displayed speech cannot certify a pair.
                continue
            anchor = replies[0]
            result[key] = [anchor]
            extra = continuations.get(key, [])
            if len(extra) != 1:
                continue
            row = extra[0]
            metadata = json.loads(row["payload_json"])["structured_payload"]
            if (metadata.get("continuation_index") == 1
                    and metadata.get("last_response_event_id") == anchor["event_id"]
                    and metadata.get("last_response_seq") == anchor["experience_seq"]
                    and row["experience_seq"] > anchor["experience_seq"]
                    and row["occurred_at"] >= anchor["occurred_at"]):
                result[key].append(row)
        for key, replies in feedbacks.items():
            # One actual speech per failed revision, at most the existing two recovery revisions.
            if len(replies) <= 2 and len({r["source_event_id"] for r in replies}) == len(replies):
                result.setdefault(key, []).extend(replies)
        return result

    def _verified_action_feedback(self, parent, reply):
        """Join displayed feedback to its original accepted input and trusted failed outcome."""
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT * FROM experience_events WHERE observer_id=? AND source_session_id=? "
                "AND clock_domain=? AND source_type='goal_outcome' AND source_event_id=? LIMIT 2",
                (reply["observer_id"], reply["source_session_id"], reply["clock_domain"], reply["source_event_id"])).fetchall()
        if len(rows) != 1:
            return False
        outcome = rows[0]
        payload = json.loads(outcome["payload_json"])
        expected = self._participant_map(json.loads(parent["payload_json"]))
        identities = {p["entity_id"]: (p["identity_namespace"], p["identity_type"], p["persistent_id"])
                      for p in payload.get("persistent_participants", ())}
        return bool(parent["trace_id"] and reply["trace_id"] == parent["trace_id"] == outcome["trace_id"]
            and outcome["event_type"] in ("goal_failed", "goal_timed_out")
            and payload["structured_payload"].get("status") ==
                ("Failed" if outcome["event_type"] == "goal_failed" else "TimedOut")
            and payload["structured_payload"].get("proposal_revision") in (0, 1)
            and payload["actor_entity_id"] == reply["observer_id"] and outcome["provenance"] == "EXECUTED_BY_SELF"
            and identities.get(reply["observer_id"]) == expected.get(reply["observer_id"])
            and all(expected.get(k) == v for k, v in identities.items())
            and parent["experience_seq"] < outcome["experience_seq"] < reply["experience_seq"]
            and parent["occurred_at"] <= outcome["occurred_at"] <= reply["occurred_at"]
            and outcome["world_revision"] is not None and reply["world_revision"] is not None
            and outcome["world_revision"] <= reply["world_revision"])

    def _project_timeline(self, inputs, by_turn, target, char_budget, overlap, *, truncated=False,
                          current_session_id=None):
        messages, sources = [], {}
        for row in inputs:
            if not (json.loads(row["payload_json"]).get("natural_language_payload") or "").strip():
                continue  # Preserve the existing nonblank accepted-speech carrier requirement.
            replies = by_turn.get(self._turn_key(row), [])
            for source in (row, *replies):
                payload = json.loads(source["payload_json"])
                text = (payload.get("natural_language_payload") or "").strip()
                if not text:
                    continue
                seq = source["experience_seq"]
                message = dict(seq=seq, role="player" if source is row else "npc", text=text,
                               occurred_at=source["occurred_at"])
                if source is not row:
                    if payload["structured_payload"].get("initiative") == "dialogue_continuation":
                        message["anchor_seq"] = payload["structured_payload"]["last_response_seq"]
                    else:
                        message["reply_to_seq"] = row["experience_seq"]
                messages.append(message)
                # Preserve correlation and original clocks internally. Only seq,
                # role, text, time and necessary links go into the model payload.
                sources[seq] = {key: source[key] for key in (
                    "event_id", "request_id", "source_event_id", "source_session_id",
                    "conversation_id", "clock_domain", "occurred_at", "observed_at")}
                sources[seq]["input_seq"] = row["experience_seq"]
                sources[seq]["speaker_entity_id"] = payload["actor_entity_id"]
                if "anchor_seq" in message:
                    sources[seq].update({key: payload["structured_payload"][key] for key in (
                        "parent_request_id", "parent_event_id", "last_response_event_id")})
        messages.sort(key=lambda m: m["seq"])
        for i, message in enumerate(messages):
            if message["role"] != "player":
                continue
            previous = next((m for m in reversed(messages[max(0, i-2):i])
                             if m["role"] == "npc"), None)
            if previous and all(sources[previous["seq"]][key] == sources[message["seq"]][key]
                                for key in ("source_session_id", "conversation_id", "clock_domain")):
                # A continuity hint, NOT verified semantic reply pairing or intent.
                message["context_seq"] = previous["seq"]
        return RecentDialogueTimeline.select(messages, sources, target, char_budget,
                                              overlap, truncated=truncated,
                                              current_session_id=current_session_id)

    def _project_dialogue(self, inputs, by_turn, max_messages, char_budget, *,
                          scoped=False, truncated=False):
        newest = []
        used_messages = 0
        remaining = char_budget
        for row in inputs:
            player_text = (json.loads(row["payload_json"]).get("natural_language_payload") or "").strip()
            if not player_text:
                continue
            key = self._turn_key(row) if scoped else (row["request_id"], row["source_event_id"])
            replies = by_turn.get(key, [])
            npc_utterances = [(json.loads(r["payload_json"]).get("natural_language_payload") or "").strip()
                              for r in sorted(replies, key=lambda r: r["experience_seq"])]
            npc_utterances = [text for text in npc_utterances if text]
            required_chars = len(player_text) + sum(map(len, npc_utterances))
            message_count = 1 + len(npc_utterances)
            # Whole interactions only: never turn a long qualified statement into a misleading prefix.
            if used_messages + message_count > max_messages or required_chars > remaining:
                truncated = True
                break
            used_messages += message_count
            remaining -= required_chars
            newest.append({"input_seq": row["experience_seq"], "player_text": player_text,
                           "npc_text": npc_utterances[0] if npc_utterances else None,
                           "evidence_status": "paired" if npc_utterances else "input_only"})
            if len(npc_utterances) > 1:
                newest[-1]["npc_utterances"] = npc_utterances
        return RecentDialogueProjection(
            ({"turn_index": i, **turn} for i, turn in enumerate(reversed(newest), 1)),
            truncated=truncated)


    def read_range(self, observer_id: str, after_seq: int = 0, limit: int = 100) -> tuple[ObserverExperienceEvent, ...]:
        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        safe_limit = max(1, min(limit, 100))
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """
                SELECT * FROM experience_events
                WHERE observer_id = ? AND experience_seq > ?
                ORDER BY experience_seq ASC LIMIT ?
                """,
                (observer_id, max(0, after_seq), safe_limit),
            ).fetchall()
            return tuple(self._row_to_event(row) for row in rows)

    def paired_response(self, event):
        """Bounded correlation lookup; never infer a pair from speaker order alone."""
        from .memory_consolidation_context import correlated_interaction_pair, is_emote_observation
        response_type = 'social_response' if is_emote_observation(event) else 'dialogue_response'
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT * FROM experience_events WHERE observer_id=? AND experience_seq>? "
                "AND event_type=? AND source_session_id=? AND conversation_id IS ? "
                "AND ((? IS NOT NULL AND request_id=?) OR (? IS NOT NULL AND source_event_id=?)) "
                "ORDER BY experience_seq LIMIT 2",
                (event.observer_id, event.experience_seq, response_type, event.source_session_id, event.conversation_id,
                 event.request_id, event.request_id, event.source_event_id, event.source_event_id)).fetchall()
        matches = [self._row_to_event(r) for r in rows if correlated_interaction_pair(event, self._row_to_event(r))]
        return matches[0] if len(matches) == 1 else None

    def read_recent_interactions(self, observer_id, conversation_id, request_id, event_id, identities):
        """Bounded typed projection from this ledger, never a second history store."""
        from .recent_interactions import read_recent_interactions
        return read_recent_interactions(self, observer_id, conversation_id, request_id, event_id, identities)

    def read_recent_after(
        self, observer_id: str, after_seq: int = 0, limit: int = 16
    ) -> tuple[ObserverExperienceEvent, ...]:
        """Return the newest bounded post-cursor tail in authoritative sequence order."""

        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        safe_limit = max(1, min(limit, 100))
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """
                SELECT * FROM (
                    SELECT * FROM experience_events
                    WHERE observer_id = ? AND experience_seq > ?
                    ORDER BY experience_seq DESC LIMIT ?
                ) recent
                ORDER BY experience_seq ASC
                """,
                (observer_id, max(0, after_seq), safe_limit),
            ).fetchall()
        return tuple(self._row_to_event(row) for row in rows)

    def read_exact_range(
        self, observer_id: str, start_seq: int, end_seq: int
    ) -> tuple[ObserverExperienceEvent, ...]:
        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        if start_seq < 1 or end_seq < start_seq:
            raise ValueError("EXPERIENCE_RANGE_INVALID")
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                """
                SELECT * FROM experience_events
                WHERE observer_id = ? AND experience_seq BETWEEN ? AND ?
                ORDER BY experience_seq ASC
                """,
                (observer_id, start_seq, end_seq),
            ).fetchall()
        events = tuple(self._row_to_event(row) for row in rows)
        expected = list(range(start_seq, end_seq + 1))
        if [event.experience_seq for event in events] != expected:
            raise ValueError("EXPERIENCE_SEQUENCE_GAP")
        return events

    def list_observer_heads(self) -> dict[str, int]:
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT observer_id, last_seq FROM experience_observer_sequences ORDER BY observer_id"
            ).fetchall()
        return {str(row["observer_id"]): int(row["last_seq"]) for row in rows}

    def latest_seq(self, observer_id: str) -> int:
        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT last_seq FROM experience_observer_sequences WHERE observer_id = ?", (observer_id,)
            ).fetchone()
            return 0 if row is None else int(row["last_seq"])

    def get_by_event_id(self, event_id: str) -> ObserverExperienceEvent | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT * FROM experience_events WHERE event_id = ?", (event_id,)
            ).fetchone()
            return None if row is None else self._row_to_event(row)

    def get_by_dedup_key(self, dedup_key: str) -> ObserverExperienceEvent | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT * FROM experience_events WHERE dedup_key = ?", (dedup_key,)
            ).fetchone()
            return None if row is None else self._row_to_event(row)
