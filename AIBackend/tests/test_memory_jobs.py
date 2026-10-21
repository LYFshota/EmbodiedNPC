from __future__ import annotations

import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from app.consolidation_state import ConsolidationStateRepository
from app.episode_assembler import EpisodeAssembler
from app.episode_models import EpisodeBoundaryPolicy, MemoryJobPolicy
from app.episode_repository import EpisodeIntegrityError, EpisodeRepository
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceAppendRequest, ExperienceDraft
from app.memory_job_repository import MemoryJobRepository
from app.sqlite_database import SQLiteDatabase


class MemoryJobTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.database = SQLiteDatabase(Path(self.directory.name) / "memory.sqlite3")
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.state = ConsolidationStateRepository(self.database.connect)
        self.assembler = EpisodeAssembler(
            self.ledger, self.episodes, EpisodeBoundaryPolicy(max_events=1), clock=lambda: 1000,
            extractor_version="memory-consolidator/2.0",
        )

    def tearDown(self) -> None:
        self.directory.cleanup()

    def append_and_close(self, seq: int, observer: str = "npc_01"):
        draft = ExperienceDraft(
            observer_id=observer, event_type="skill_succeeded", source_type="skill_result",
            source_session_id="session_01", clock_domain="unity_realtime",
            source_event_id=f"skill_{observer}_{seq}", actor_entity_id=observer,
            structured_payload={"seq": seq}, occurred_at=float(seq), observed_at=float(seq),
            provenance="EXECUTED_BY_SELF",
        )
        self.ingestion.append(ExperienceAppendRequest(
            request_id=f"request_{observer}_{seq}", trace_id=f"trace_{observer}_{seq}", draft=draft
        ))
        self.assembler.process_observer(observer)
        episode = self.episodes.list_for_observer(observer)[-1]
        if episode.status == "OPEN":
            episode, job = self.episodes.close_and_enqueue(
                episode.episode_id, "test_close", episode.started_at_utc + 1, "memory-consolidator/2.0"
            )
        else:
            job = self.jobs.get_for_episode(episode.episode_id, "memory-consolidator/2.0")
        return episode, job

    def test_duplicate_close_returns_same_job(self) -> None:
        episode, first = self.append_and_close(1)
        _, second = self.episodes.close_and_enqueue(
            episode.episode_id, "duplicate", episode.ended_at_utc, "memory-consolidator/2.0"
        )
        self.assertEqual(first.job_id, second.job_id)

    def test_job_lookup_and_watermark_are_version_scoped(self) -> None:
        episode, job = self.append_and_close(1)
        self.assertEqual(job.job_id, self.jobs.get_for_episode(
            episode.episode_id, "memory-consolidator/2.0").job_id)
        self.assertIsNone(self.jobs.get_for_episode(episode.episode_id, "other-version/1.0"))
        self.assertEqual(0, self.state.get("npc_01", "other-version/1.0"))

    def test_same_episode_consolidator_with_changed_hash_is_conflict(self) -> None:
        episode, job = self.append_and_close(1)
        with closing(self.database.connect()) as connection, connection:
            connection.execute("UPDATE memory_jobs SET input_hash = ? WHERE job_id = ?", ("0" * 64, job.job_id))
        with self.assertRaises(EpisodeIntegrityError):
            self.episodes.close_and_enqueue(
                episode.episode_id, "duplicate", episode.ended_at_utc, "memory-consolidator/2.0"
            )

    def test_retry_reuses_job_with_deterministic_backoff(self) -> None:
        _, job = self.append_and_close(1)
        base = job.created_at_utc + 1
        running = self.jobs.claim_next(base)
        retry = self.jobs.mark_failed(running.job_id, running.attempt_generation, base + 1, "FAILED", MemoryJobPolicy())
        self.assertEqual(job.job_id, retry.job_id)
        self.assertEqual(("RETRY", 1, base + 6), (retry.status, retry.retry_count, retry.next_retry_at_utc))
        self.assertIsNone(self.jobs.claim_next(base + 5))
        self.assertEqual(job.job_id, self.jobs.claim_next(base + 6).job_id)

    def test_retry_exhaustion_enters_dead_letter(self) -> None:
        _, job = self.append_and_close(1)
        policy = MemoryJobPolicy(max_retries=2)
        base = job.created_at_utc + 1
        running = self.jobs.claim_next(base)
        self.jobs.mark_failed(job.job_id, running.attempt_generation, base + 1, "FAIL_1", policy)
        running = self.jobs.claim_next(base + 6)
        dead = self.jobs.mark_failed(job.job_id, running.attempt_generation, base + 7, "FAIL_2", policy)
        self.assertEqual(("DEAD_LETTER", 2, None), (dead.status, dead.retry_count, dead.next_retry_at_utc))

    def test_stale_running_recovery_and_old_attempt_completion_guard(self) -> None:
        _, job = self.append_and_close(1)
        policy = MemoryJobPolicy(stale_timeout_seconds=10, extractor_version="memory-consolidator/2.0")
        base = job.created_at_utc + 1
        first = self.jobs.claim_next(base)
        recovered = self.jobs.recover_stale(base + 11, policy)
        self.assertEqual("RETRY", recovered[0].status)
        second = self.jobs.claim_next(base + 16)
        self.assertGreater(second.attempt_generation, first.attempt_generation)
        self.assertIsNone(self.jobs.mark_succeeded(job.job_id, first.attempt_generation, base + 17))
        self.assertEqual("RUNNING", self.jobs.get(job.job_id).status)

    def test_backlog_metrics_are_observer_scoped_and_report_last_failure(self) -> None:
        _, first_job = self.append_and_close(1, "npc_01")
        self.append_and_close(1, "npc_02")
        base = first_job.created_at_utc + 1
        running = self.jobs.claim_next(base)
        self.jobs.mark_failed(running.job_id, running.attempt_generation, base + 1, "TEST_FAILURE", MemoryJobPolicy())
        backlog = self.jobs.backlog(base + 10)
        self.assertEqual((1, 1), (backlog.pending_count, backlog.retry_count))
        self.assertEqual({"npc_01": 1, "npc_02": 1}, backlog.jobs_per_observer)
        self.assertEqual("TEST_FAILURE", backlog.last_failure_reason)

    def test_closed_episode_is_immutable(self) -> None:
        episode, _ = self.append_and_close(1)
        with closing(self.database.connect()) as connection, connection:
            with self.assertRaises(sqlite3.IntegrityError):
                connection.execute("UPDATE episodes SET close_reason = 'changed' WHERE episode_id = ?", (episode.episode_id,))

    def test_close_and_job_enqueue_are_atomic(self) -> None:
        draft = ExperienceDraft(
            observer_id="npc_01", event_type="skill_succeeded", source_type="skill_result",
            source_session_id="session_01", clock_domain="unity_realtime", source_event_id="skill_atomic",
            actor_entity_id="npc_01", occurred_at=1, observed_at=1, provenance="EXECUTED_BY_SELF",
        )
        self.ingestion.append(ExperienceAppendRequest(request_id="request_atomic", trace_id="trace_atomic", draft=draft))
        self.assembler.process_observer("npc_01")
        episode = self.episodes.get_open("npc_01")
        with closing(self.database.connect()) as connection, connection:
            connection.execute(
                "CREATE TRIGGER reject_job BEFORE INSERT ON memory_jobs BEGIN SELECT RAISE(ABORT, 'forced'); END"
            )
        with self.assertRaises(sqlite3.IntegrityError):
            self.episodes.close_and_enqueue(episode.episode_id, "test", 10, "memory-consolidator/2.0")
        self.assertEqual("OPEN", self.episodes.get(episode.episode_id).status)
        self.assertIsNone(self.jobs.get_for_episode(episode.episode_id, "memory-consolidator/2.0"))

    def test_watermark_advances_only_over_contiguous_succeeded_jobs(self) -> None:
        _, first_job = self.append_and_close(1)
        _, second_job = self.append_and_close(2)
        base = first_job.created_at_utc + 1
        first = self.jobs.claim_next(base)
        self.jobs.mark_succeeded(first.job_id, first.attempt_generation, base + 1)
        self.assertEqual(1, self.state.advance_contiguous("npc_01", "memory-consolidator/2.0", base + 2))
        self.assertEqual(1, self.state.get("npc_01", "memory-consolidator/2.0"))
        second = self.jobs.claim_next(base + 3)
        self.assertEqual(second_job.job_id, second.job_id)
        self.jobs.mark_succeeded(second.job_id, second.attempt_generation, base + 4)
        self.assertEqual(2, self.state.advance_contiguous("npc_01", "memory-consolidator/2.0", base + 5))
        self.assertEqual(first_job.episode_id, self.episodes.list_for_observer("npc_01")[0].episode_id)

    def test_gap_blocks_watermark(self) -> None:
        with closing(self.database.connect()) as connection, connection:
            connection.execute(
                """
                INSERT INTO memory_consolidation_state(observer_id, extractor_version,
                    consolidated_through_experience_seq, updated_at_utc, schema_version)
                VALUES('npc_01', 'memory-consolidator/2.0', 3, 1, 'consolidation-state/2.0')
                """
            )
        self.assertEqual(3, self.state.advance_contiguous("npc_01", "memory-consolidator/2.0", 2))


if __name__ == "__main__":
    unittest.main()
