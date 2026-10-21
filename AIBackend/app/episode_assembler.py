"""Deterministic Episode boundaries over contiguous observer-local ledger ranges."""

from __future__ import annotations

import logging
from collections.abc import Callable
from typing import TypeVar

from .episode_models import Episode, EpisodeBoundaryPolicy, EpisodeProcessResult
from .episode_repository import EpisodeRepository
from .experience_ledger import ExperienceLedger
from .experience_models import ObserverExperienceEvent
from .memory_consolidation_context import correlated_interaction_pair, is_emote_observation


logger = logging.getLogger("embodiednpc.backend.episodes")
TERMINAL_EVENTS = frozenset(("goal_succeeded", "goal_failed", "goal_cancelled", "goal_timed_out"))
SEMANTIC_END = "END"
T = TypeVar("T")


class EpisodeAssembler:
    """Group persisted experiences without reinterpreting their source or owner.

    Page boundaries are not semantic boundaries. Storage retries must stay in
    the current assembly frame so a committed terminal append keeps its original
    close reason, time and extractor when close_and_enqueue is retried.
    """

    def __init__(
        self,
        ledger: ExperienceLedger,
        episodes: EpisodeRepository,
        policy: EpisodeBoundaryPolicy | None = None,
        extractor_version: str = "memory-extractor/1.0",
        clock: Callable[[], float] | None = None,
        capacity_probe: Callable[[tuple[ObserverExperienceEvent, ...]], bool] | None = None,
        storage_call: Callable | None = None,
    ) -> None:
        import time
        self.ledger = ledger
        self.episodes = episodes
        self.policy = policy or EpisodeBoundaryPolicy()
        self.extractor_version = extractor_version
        self._clock = clock or time.time
        self.capacity_probe = capacity_probe
        self._storage_call = storage_call

    def _storage(self, operation: Callable[..., T], *args: object) -> T:
        # Retry this atomic call without unwinding the assembly frame. In
        # particular, append may already have committed a terminal event.
        if self._storage_call is None:
            return operation(*args)
        phase = ("capacity_preview" if operation is self.capacity_probe
                 else getattr(operation, "__name__", "repository_operation"))
        return self._storage_call(phase, lambda: operation(*args))

    def process_all(self) -> tuple[EpisodeProcessResult, ...]:
        return tuple(self.process_observer(observer_id) for observer_id in self._storage(self.ledger.list_observer_heads))

    def process_observer(self, observer_id: str) -> EpisodeProcessResult:
        covered = self._storage(self.episodes.last_covered_seq, observer_id)
        events = self._storage(self.ledger.read_range, observer_id, covered, self.policy.processing_batch)
        if self.capacity_probe:
            # A processing page is not a semantic boundary. Finish a confirmed interaction
            # even when its response falls beyond this page's soft event-count limit.
            while events:
                end = events[-1].experience_seq
                for event in events:
                    if event.event_type == 'dialogue_input' or is_emote_observation(event):
                        response = self._storage(self.ledger.paired_response, event)
                        if response:
                            end = max(end, response.experience_seq)
                if end == events[-1].experience_seq:
                    break
                events = self._storage(self.ledger.read_exact_range, observer_id, covered+1, end)
        expected = covered + 1
        processed = closed = anomalies = 0
        gap_at: int | None = None
        groups = self._interaction_groups(events) if self.capacity_probe else tuple((e,) for e in events)
        for group in groups:
            event = group[0]
            pending_overflow = False
            if event.experience_seq != expected:
                gap_at = expected
                logger.error("Episode processing stopped observer=%s gap_at_seq=%s", observer_id, expected)
                break
            current = self._storage(self.episodes.get_open, observer_id)
            if current is not None:
                last_event = self._storage(self.ledger.read_exact_range, observer_id, current.end_seq, current.end_seq)[0]
                first_event = self._storage(self.ledger.read_exact_range, observer_id, current.start_seq, current.start_seq)[0]
                if event.source_session_id == last_event.source_session_id and event.observed_at < last_event.observed_at:
                    anomalies += 1
                    logger.warning(
                        "Episode clock anomaly observer=%s previous_seq=%s current_seq=%s",
                        observer_id, last_event.experience_seq, event.experience_seq,
                    )
                reason = self._close_before(current, first_event, last_event, event)
                if self.capacity_probe:
                    prior = self._storage(self.ledger.read_exact_range, observer_id, current.start_seq, current.end_seq)
                    fits_joined = self._storage(self.capacity_probe, (*prior, *group))
                    pending = any(correlated_interaction_pair(p, event) and
                        not any(correlated_interaction_pair(p, r) for r in prior) for p in prior)
                    if pending and reason in (None, 'capacity', 'idle_timeout', 'max_events', 'max_duration'):
                        # A crash may have persisted only the input half of a group.
                        # Finish its confirmed reply before any capacity/count boundary.
                        reason = None
                        pending_overflow = not fits_joined
                    elif reason is None and not fits_joined:
                        reason = 'capacity'
                if reason is not None:
                    self._close(current, reason, last_event.ingested_at_utc)
                    closed += 1
                    current = None
            fits_alone = not self.capacity_probe or self._storage(self.capacity_probe, group)
            end_reason = None
            for event in group:
                if event.experience_seq != expected:
                    gap_at = expected
                    break
                current = (self._storage(self.episodes.open_for_event, event, self.policy.version) if current is None
                           else self._storage(self.episodes.append_event, current.episode_id, event))
                processed += 1
                expected += 1
                if self._is_semantic_end(event):
                    end_reason = 'semantic_end'
                elif event.event_type in TERMINAL_EVENTS:
                    end_reason = event.event_type
            if gap_at is not None:
                break
            if not fits_alone or pending_overflow:
                # Preserve an indivisible over-limit source range for the worker's explicit safe failure.
                end_reason = 'indivisible_capacity_exceeded'
            if end_reason:
                self._close(current, end_reason, event.ingested_at_utc)
                closed += 1
        return EpisodeProcessResult(observer_id, processed, closed, anomalies, gap_at)

    def _interaction_groups(self, events):
        """Only correlation certifies pairing; an in-flight input waits until response or idle."""
        groups = []
        index = 0
        while index < len(events):
            first = events[index]
            if first.event_type == 'dialogue_input' or is_emote_observation(first):
                matches = [i for i in range(index+1, len(events)) if correlated_interaction_pair(first, events[i])]
                if len(matches) == 1:
                    end = matches[0]+1
                    cursor = index+1
                    while cursor < end:
                        if events[cursor].event_type == 'dialogue_input' or is_emote_observation(events[cursor]):
                            nested = [i for i in range(cursor+1, len(events))
                                      if correlated_interaction_pair(events[cursor], events[i])]
                            if len(nested) == 1:
                                end = max(end, nested[0]+1)
                        cursor += 1
                    group = events[index:end]
                    # Do not bridge an explicit source/conversation boundary.
                    if all((e.source_session_id, e.conversation_id) ==
                           (first.source_session_id, first.conversation_id) for e in group):
                        groups.append(group)
                        index = end
                        continue
                interrupted = any(e.event_type == 'dialogue_input' or
                    (e.source_session_id, e.conversation_id) != (first.source_session_id, first.conversation_id)
                    for e in events[index+1:])
                if not interrupted and self._clock()-first.ingested_at_utc < self.policy.idle_timeout_seconds:
                    break
            groups.append((first,))
            index += 1
        return tuple(groups)

    def idle_sweep(self, now: float | None = None) -> int:
        current_time = self._clock() if now is None else now
        closed = 0
        for episode in self._storage(self.episodes.list_open):
            last = self._storage(self.ledger.read_exact_range, episode.observer_id, episode.end_seq, episode.end_seq)[0]
            if current_time - last.ingested_at_utc >= self.policy.idle_timeout_seconds:
                if self.capacity_probe:
                    prior = self._storage(self.ledger.read_exact_range, episode.observer_id, episode.start_seq, episode.end_seq)
                    if any((e.event_type == 'dialogue_input' or is_emote_observation(e)) and
                           not any(correlated_interaction_pair(e, r) for r in prior) and
                           self._storage(self.ledger.paired_response, e) for e in prior):
                        # Resume a persisted half-interaction before sweeping its idle boundary.
                        continue
                self._close(episode, "idle_timeout", current_time)
                closed += 1
        return closed

    def rebuild_episode(self, episode_id: str) -> tuple[ObserverExperienceEvent, ...]:
        episode = self._storage(self.episodes.get, episode_id)
        if episode is None:
            raise ValueError("EPISODE_NOT_FOUND")
        return self._storage(self.ledger.read_exact_range, episode.observer_id, episode.start_seq, episode.end_seq)

    def _close_before(
        self,
        episode: Episode,
        first: ObserverExperienceEvent,
        last: ObserverExperienceEvent,
        current: ObserverExperienceEvent,
    ) -> str | None:
        if current.source_session_id != episode.source_session_id:
            return "source_session_changed"
        if current.conversation_id and episode.conversation_id and current.conversation_id != episode.conversation_id:
            return "conversation_changed"
        gap = current.observed_at - last.observed_at
        if gap > self.policy.idle_timeout_seconds:
            return "idle_timeout"
        if episode.event_count >= self.policy.max_events:
            return "max_events"
        duration = current.observed_at - first.observed_at
        if duration > self.policy.max_duration_seconds:
            return "max_duration"
        return None

    def _close(self, episode: Episode, reason: str, ended_at_utc: float) -> None:
        try:
            closed, job = self._storage(
                self.episodes.close_and_enqueue,
                episode.episode_id, reason, ended_at_utc, self.extractor_version,
            )
        except Exception:
            logger.error("EPISODE_CLOSE_INCOMPLETE episode_id=%s reason=%s",
                         episode.episode_id, reason)
            raise
        logger.info(
            "Episode closed episode_id=%s observer_id=%s range=%s..%s reason=%s job_id=%s job_status=%s",
            closed.episode_id, closed.observer_id, closed.start_seq, closed.end_seq,
            reason, job.job_id, job.status,
        )

    @staticmethod
    def _is_semantic_end(event: ObserverExperienceEvent) -> bool:
        return event.source_type == "dialogue" and event.event_type == "dialogue_response" and \
            event.structured_payload.get("episode_directive") == SEMANTIC_END
