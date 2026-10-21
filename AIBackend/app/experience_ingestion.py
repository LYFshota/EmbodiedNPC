"""Thin normalization and persistence boundary for Mem-P1 experience drafts."""

from __future__ import annotations

import hashlib
import json
import logging
import sqlite3
import time

from .experience_ledger import ExperienceIdConflict, ExperienceLedger
from .experience_models import ExperienceAppendRequest, ExperienceAppendResult, ExperienceDraft


logger = logging.getLogger("embodiednpc.backend.experience")


class ExperienceIngestionService:
    def __init__(self, ledger: ExperienceLedger) -> None:
        self.ledger = ledger

    @staticmethod
    def _identity(draft: ExperienceDraft) -> str:
        parts: list[object] = [draft.source_session_id, draft.observer_id, draft.source_type]
        if draft.source_type == "observation":
            parts.extend((sorted(draft.source_observation_ids), draft.structured_payload["perception_revision"]))
        elif draft.source_type == "dialogue":
            if draft.structured_payload.get("initiative") == "proactive":
                parts.extend((draft.event_type, draft.source_event_id))
            else:
                parts.extend((draft.event_type, draft.request_id))
        else:
            parts.append(draft.source_event_id)
        return json.dumps(parts, ensure_ascii=False, separators=(",", ":"))

    @staticmethod
    def _canonical_draft(draft: ExperienceDraft) -> str:
        payload = draft.model_dump(mode="json")
        payload["source_observation_ids"] = sorted(payload["source_observation_ids"])
        payload["target_entity_ids"] = sorted(payload["target_entity_ids"])
        payload["persistent_participants"] = sorted(
            payload["persistent_participants"], key=lambda value: value["entity_id"]
        )
        return json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":"))

    @classmethod
    def prepare_identity(cls, draft: ExperienceDraft) -> tuple[str, str, str]:
        identity_hash = hashlib.sha256(cls._identity(draft).encode("utf-8")).hexdigest()
        event_id = "exp_" + identity_hash
        dedup_key = "dedup_" + hashlib.sha256(("experience|" + identity_hash).encode("utf-8")).hexdigest()
        payload_hash = hashlib.sha256(cls._canonical_draft(draft).encode("utf-8")).hexdigest()
        return event_id, dedup_key, payload_hash

    def append(self, request: ExperienceAppendRequest) -> ExperienceAppendResult:
        started = time.perf_counter()
        event_id, dedup_key, payload_hash = self.prepare_identity(request.draft)
        try:
            status, event = self.ledger.append(
                request.draft, event_id=event_id, dedup_key=dedup_key, payload_hash=payload_hash
            )
            reason = "STORED" if status == "inserted" else "IDEMPOTENT_REPLAY"
            result = ExperienceAppendResult(
                request_id=request.request_id,
                trace_id=request.trace_id,
                event_id=event.event_id,
                observer_id=event.observer_id,
                experience_seq=event.experience_seq,
                status=status,
                reason=reason,
            )
        except ExperienceIdConflict as error:
            result = ExperienceAppendResult(
                request_id=request.request_id,
                trace_id=request.trace_id,
                event_id=error.event_id,
                observer_id=request.draft.observer_id,
                experience_seq=0,
                status="id_conflict",
                reason=error.reason,
            )
        except (sqlite3.Error, OSError):
            logger.exception("Experience append storage failed event_id=%s", event_id)
            result = ExperienceAppendResult(
                request_id=request.request_id,
                trace_id=request.trace_id,
                event_id=event_id,
                observer_id=request.draft.observer_id,
                experience_seq=0,
                status="storage_failed",
                reason="SQLITE_WRITE_FAILED",
            )
        logger.info(
            "Experience append event_id=%s observer_id=%s experience_seq=%s event_type=%s "
            "source_type=%s status=%s latency_ms=%.2f reason=%s request_id=%s trace_id=%s",
            result.event_id,
            result.observer_id,
            result.experience_seq,
            request.draft.event_type,
            request.draft.source_type,
            result.status,
            (time.perf_counter() - started) * 1000,
            result.reason,
            request.request_id,
            request.trace_id,
        )
        return result
