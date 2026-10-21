"""SQLite persistence for one Reflection result per durable pair and Episode."""

from __future__ import annotations

import json
import sqlite3
from collections.abc import Callable
from contextlib import closing

from .canonical_memory_read_repository import identity_parts
from .memory_consolidation_context import TrustedPersistentIdentity
from .relationship_reflection_models import RelationshipReflectionRecord


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
        with closing(self._connection_factory()) as connection:
            row = connection.execute(
                """
                SELECT 1 FROM relationship_reflections
                WHERE observer_identity_namespace=? AND observer_identity_type=?
                  AND observer_persistent_id=? AND subject_identity_namespace=?
                  AND subject_identity_type=? AND subject_persistent_id=?
                  AND episode_id=? AND policy_version=? LIMIT 1
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
