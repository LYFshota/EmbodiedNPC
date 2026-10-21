"""Transactional persistence for Mem-P2 range-based Episodes."""

from __future__ import annotations

import hashlib
import json
import sqlite3
from collections.abc import Callable
from contextlib import closing

from .episode_models import DurableMemoryJob, Episode
from .experience_models import ObserverExperienceEvent


class EpisodeIntegrityError(ValueError):
    pass


class EpisodeRepository:
    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]) -> None:
        self._connection_factory = connection_factory

    @staticmethod
    def episode_id_for(observer_id: str, start_seq: int, policy_version: str) -> str:
        value = f"{observer_id}|{start_seq}|{policy_version}"
        return "episode_" + hashlib.sha256(value.encode("utf-8")).hexdigest()

    @staticmethod
    def input_hash_for(episode: Episode, events: tuple[ObserverExperienceEvent, ...]) -> str:
        value = {
            "policy_version": episode.policy_version,
            "observer_id": episode.observer_id,
            "start_seq": episode.start_seq,
            "end_seq": episode.end_seq,
            "events": [(event.experience_seq, event.event_id, event.payload_hash) for event in events],
        }
        encoded = json.dumps(value, sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(encoded.encode("utf-8")).hexdigest()

    @staticmethod
    def _episode(row: sqlite3.Row) -> Episode:
        return Episode(**dict(row))

    @staticmethod
    def _job(row: sqlite3.Row) -> DurableMemoryJob:
        return DurableMemoryJob(**dict(row))

    def get(self, episode_id: str) -> Episode | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
        return None if row is None else self._episode(row)

    def get_open(self, observer_id: str) -> Episode | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT * FROM episodes WHERE observer_id = ? AND status = 'OPEN'", (observer_id,)
            ).fetchone()
        return None if row is None else self._episode(row)

    def previous_closed(self, observer_id: str, start_seq: int) -> Episode | None:
        with closing(self._connection_factory()) as connection:
            row = connection.execute("SELECT * FROM episodes WHERE observer_id=? AND status='CLOSED' "
                "AND end_seq<? ORDER BY end_seq DESC LIMIT 1", (observer_id, start_seq)).fetchone()
        return None if row is None else self._episode(row)

    def list_for_observer(self, observer_id: str) -> tuple[Episode, ...]:
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT * FROM episodes WHERE observer_id = ? ORDER BY start_seq", (observer_id,)
            ).fetchall()
        return tuple(self._episode(row) for row in rows)

    def list_open(self) -> tuple[Episode, ...]:
        with closing(self._connection_factory()) as connection:
            rows = connection.execute(
                "SELECT * FROM episodes WHERE status = 'OPEN' ORDER BY observer_id"
            ).fetchall()
        return tuple(self._episode(row) for row in rows)

    def last_covered_seq(self, observer_id: str) -> int:
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                "SELECT COALESCE(MAX(end_seq), 0) AS value FROM episodes WHERE observer_id = ?", (observer_id,)
            ).fetchone()
        return int(row["value"])

    def open_for_event(self, event: ObserverExperienceEvent, policy_version: str) -> Episode:
        now = event.ingested_at_utc
        episode_id = self.episode_id_for(event.observer_id, event.experience_seq, policy_version)
        goal_id, goal_type = self._goal_metadata(event)
        with closing(self._connection_factory()) as connection:
            with connection:
                connection.execute(
                    """
                    INSERT INTO episodes(
                        episode_id, observer_id, source_session_id, start_seq, end_seq,
                        started_at_utc, ended_at_utc, status, close_reason, conversation_id,
                        primary_goal_id, primary_goal_type, event_count, policy_version,
                        schema_version, created_at_utc, updated_at_utc
                    ) VALUES(?, ?, ?, ?, ?, ?, NULL, 'OPEN', NULL, ?, ?, ?, 1, ?, 'episode/1.0', ?, ?)
                    """,
                    (
                        episode_id, event.observer_id, event.source_session_id,
                        event.experience_seq, event.experience_seq, now, event.conversation_id,
                        goal_id, goal_type, policy_version, now, now,
                    ),
                )
                row = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
        return self._episode(row)

    def append_event(self, episode_id: str, event: ObserverExperienceEvent) -> Episode:
        goal_id, goal_type = self._goal_metadata(event)
        with closing(self._connection_factory()) as connection:
            with connection:
                current = connection.execute(
                    "SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)
                ).fetchone()
                if current is None or current["status"] != "OPEN":
                    raise EpisodeIntegrityError("OPEN_EPISODE_REQUIRED")
                if current["observer_id"] != event.observer_id or event.experience_seq != current["end_seq"] + 1:
                    raise EpisodeIntegrityError("EPISODE_SEQUENCE_NOT_CONTIGUOUS")
                conversation_id = current["conversation_id"] or event.conversation_id
                connection.execute(
                    """
                    UPDATE episodes SET end_seq = ?, event_count = event_count + 1,
                        conversation_id = ?, primary_goal_id = COALESCE(primary_goal_id, ?),
                        primary_goal_type = COALESCE(primary_goal_type, ?), updated_at_utc = ?
                    WHERE episode_id = ? AND status = 'OPEN'
                    """,
                    (event.experience_seq, conversation_id, goal_id, goal_type, event.ingested_at_utc, episode_id),
                )
                row = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
        return self._episode(row)

    def close_and_enqueue(
        self, episode_id: str, close_reason: str, ended_at_utc: float, extractor_version: str
    ) -> tuple[Episode, DurableMemoryJob]:
        # Episode closure and its versioned Job commit together. Deterministic
        # IDs/hash checks accept a matching retry, never a new interpretation
        # of already-closed input. The caller preserves the original arguments.
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                row = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
                if row is None:
                    raise EpisodeIntegrityError("EPISODE_NOT_FOUND")
                episode = self._episode(row)
                event_rows = connection.execute(
                    """
                    SELECT * FROM experience_events
                    WHERE observer_id = ? AND experience_seq BETWEEN ? AND ?
                    ORDER BY experience_seq
                    """,
                    (episode.observer_id, episode.start_seq, episode.end_seq),
                ).fetchall()
                events = tuple(self._event(row_value) for row_value in event_rows)
                if [event.experience_seq for event in events] != list(range(episode.start_seq, episode.end_seq + 1)):
                    raise EpisodeIntegrityError("EXPERIENCE_SEQUENCE_GAP")
                input_hash = self.input_hash_for(episode, events)
                idempotency_key = hashlib.sha256(
                    f"{episode.episode_id}|{extractor_version}|{input_hash}".encode("utf-8")
                ).hexdigest()
                job_id = "memory_job_" + hashlib.sha256(
                    f"{episode.episode_id}|{extractor_version}".encode("utf-8")
                ).hexdigest()
                if episode.status == "OPEN":
                    connection.execute(
                        """
                        UPDATE episodes SET status = 'CLOSED', close_reason = ?, ended_at_utc = ?, updated_at_utc = ?
                        WHERE episode_id = ? AND status = 'OPEN'
                        """,
                        (close_reason, ended_at_utc, ended_at_utc, episode_id),
                    )
                    connection.execute(
                        """
                        INSERT INTO memory_jobs(
                            job_id, observer_id, episode_id, input_hash, extractor_version,
                            idempotency_key, status, retry_count, attempt_generation,
                            next_retry_at_utc, created_at_utc, updated_at_utc, started_at_utc,
                            finished_at_utc, error_code, schema_version
                        ) VALUES(?, ?, ?, ?, ?, ?, 'PENDING', 0, 0, NULL, ?, ?, NULL, NULL, NULL, 'memory-job/1.0')
                        """,
                        (job_id, episode.observer_id, episode_id, input_hash, extractor_version,
                         idempotency_key, ended_at_utc, ended_at_utc),
                    )
                existing = connection.execute(
                    "SELECT * FROM memory_jobs WHERE episode_id = ? AND extractor_version = ?",
                    (episode_id, extractor_version),
                ).fetchone()
                if existing is None:
                    raise EpisodeIntegrityError("CLOSED_EPISODE_JOB_MISSING")
                if existing["input_hash"] != input_hash or existing["idempotency_key"] != idempotency_key:
                    raise EpisodeIntegrityError("MEMORY_JOB_INPUT_HASH_CONFLICT")
                closed = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
                connection.commit()
                return self._episode(closed), self._job(existing)
            except Exception:
                connection.rollback()
                raise

    def enqueue_for_closed_episode(self, episode_id: str, extractor_version: str, now: float) -> DurableMemoryJob:
        """Idempotently enqueue a CLOSED Episode for one extractor semantic version."""
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                row = connection.execute("SELECT * FROM episodes WHERE episode_id = ?", (episode_id,)).fetchone()
                if row is None:
                    raise EpisodeIntegrityError("EPISODE_NOT_FOUND")
                episode = self._episode(row)
                if episode.status != "CLOSED":
                    raise EpisodeIntegrityError("EPISODE_NOT_CLOSED")
                event_rows = connection.execute(
                    "SELECT * FROM experience_events WHERE observer_id = ? AND experience_seq BETWEEN ? AND ? "
                    "ORDER BY experience_seq",
                    (episode.observer_id, episode.start_seq, episode.end_seq),
                ).fetchall()
                events = tuple(self._event(value) for value in event_rows)
                if [event.experience_seq for event in events] != list(range(episode.start_seq, episode.end_seq + 1)):
                    raise EpisodeIntegrityError("EXPERIENCE_SEQUENCE_GAP")
                input_hash = self.input_hash_for(episode, events)
                idempotency_key = hashlib.sha256(
                    f"{episode_id}|{extractor_version}|{input_hash}".encode("utf-8")
                ).hexdigest()
                job_id = "memory_job_" + hashlib.sha256(
                    f"{episode_id}|{extractor_version}".encode("utf-8")
                ).hexdigest()
                connection.execute(
                    """
                    INSERT OR IGNORE INTO memory_jobs(
                        job_id, observer_id, episode_id, input_hash, extractor_version,
                        idempotency_key, status, retry_count, attempt_generation,
                        next_retry_at_utc, created_at_utc, updated_at_utc, started_at_utc,
                        finished_at_utc, error_code, schema_version
                    ) VALUES(?, ?, ?, ?, ?, ?, 'PENDING', 0, 0, NULL, ?, ?, NULL, NULL, NULL, 'memory-job/1.0')
                    """,
                    (job_id, episode.observer_id, episode_id, input_hash, extractor_version,
                     idempotency_key, now, now),
                )
                existing = connection.execute(
                    "SELECT * FROM memory_jobs WHERE episode_id = ? AND extractor_version = ?",
                    (episode_id, extractor_version),
                ).fetchone()
                if existing is None or existing["input_hash"] != input_hash:
                    raise EpisodeIntegrityError("MEMORY_JOB_INPUT_HASH_CONFLICT")
                connection.commit()
                return self._job(existing)
            except Exception:
                connection.rollback()
                raise

    @staticmethod
    def _goal_metadata(event: ObserverExperienceEvent) -> tuple[str | None, str | None]:
        if event.source_type != "goal_outcome":
            return None, None
        goal_id = event.source_event_id
        goal_type = event.structured_payload.get("goal_type")
        return goal_id, goal_type if isinstance(goal_type, str) and goal_type else None

    @staticmethod
    def _event(row: sqlite3.Row) -> ObserverExperienceEvent:
        from .experience_ledger import ExperienceLedger
        return ExperienceLedger._row_to_event(row)
