"""Persistent consolidation watermark API; Mem-P2 runtime does not advance it."""

from __future__ import annotations

import sqlite3
from collections.abc import Callable
from contextlib import closing


class ConsolidationStateRepository:
    def __init__(self, connection_factory: Callable[[], sqlite3.Connection]) -> None:
        self._connection_factory = connection_factory

    def get(self, observer_id: str, extractor_version: str = "memory-extractor/1.0") -> int:
        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                """
                SELECT consolidated_through_experience_seq FROM memory_consolidation_state
                WHERE observer_id = ? AND extractor_version = ?
                """,
                (observer_id, extractor_version),
            ).fetchone()
        return 0 if row is None else int(row["consolidated_through_experience_seq"])

    def advance_contiguous(self, observer_id: str, extractor_version: str, now: float) -> int:
        """Future admission hook. Advance only across adjacent CLOSED/SUCCEEDED ranges."""
        if not observer_id:
            raise ValueError("OBSERVER_ID_REQUIRED")
        with closing(self._connection_factory()) as connection:
            try:
                connection.execute("BEGIN IMMEDIATE")
                watermark = self.advance_in_transaction(connection, observer_id, extractor_version, now)
                connection.commit()
                return watermark
            except Exception:
                connection.rollback()
                raise

    @staticmethod
    def advance_in_transaction(connection: sqlite3.Connection, observer_id: str,
                               extractor_version: str, now: float) -> int:
        """Advance the existing cursor inside the caller's finalization transaction."""
        row = connection.execute(
            """
            SELECT consolidated_through_experience_seq FROM memory_consolidation_state
            WHERE observer_id = ? AND extractor_version = ?
            """,
            (observer_id, extractor_version),
        ).fetchone()
        watermark = 0 if row is None else int(row[0])
        while True:
            episode = connection.execute(
                """
                SELECT end_seq FROM episodes
                WHERE observer_id = ? AND start_seq = ? AND status = 'CLOSED'
                """,
                (observer_id, watermark + 1),
            ).fetchone()
            if episode is None:
                break
            job = connection.execute(
                """
                SELECT status FROM memory_jobs
                WHERE observer_id = ? AND episode_id = (
                    SELECT episode_id FROM episodes
                    WHERE observer_id = ? AND start_seq = ? AND status = 'CLOSED'
                ) AND extractor_version = ?
                """,
                (observer_id, observer_id, watermark + 1, extractor_version),
            ).fetchone()
            if job is None or job["status"] != "SUCCEEDED":
                break
            watermark = int(episode["end_seq"])
        connection.execute(
            """
            INSERT INTO memory_consolidation_state(
                observer_id, extractor_version, consolidated_through_experience_seq,
                updated_at_utc, schema_version
            ) VALUES(?, ?, ?, ?, 'consolidation-state/2.0')
            ON CONFLICT(observer_id, extractor_version) DO UPDATE SET
                consolidated_through_experience_seq = excluded.consolidated_through_experience_seq,
                updated_at_utc = excluded.updated_at_utc
            """,
            (observer_id, extractor_version, watermark, now),
        )
        return watermark
