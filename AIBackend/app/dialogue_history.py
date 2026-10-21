"""Read-only dialogue presentation over the existing ExperienceLedger."""

import asyncio

from fastapi import APIRouter, Query

from .experience_ledger import ExperienceLedger


def create_dialogue_history_router(ledger: ExperienceLedger) -> APIRouter:
    router = APIRouter()

    @router.get("/v1/dialogue-history")
    async def history(
        observer_id: str = Query(min_length=1, max_length=256),
        conversation_id: str = Query(min_length=1, max_length=512),
        after_seq: int = Query(default=0, ge=0),
        limit: int = Query(default=100, ge=1, le=100),
    ) -> dict:
        items = await asyncio.to_thread(
            ledger.read_dialogue_history, observer_id, conversation_id, after_seq, limit,
        )
        return {
            "observer_id": observer_id, "conversation_id": conversation_id,
            "items": items,
            "next_after_seq": items[-1]["experience_seq"] if items else after_seq,
            "has_more": len(items) == limit,
        }

    return router
