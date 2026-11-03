"""P2 uses real Ledger/Admission/Store/Recall; only model choices are scripted."""
import asyncio
from contextlib import closing
from dataclasses import replace
import json
import sqlite3
import time

from pydantic import ValidationError

from app.base_memory_consolidator import BaseMemoryConsolidator
from app.canonical_memory_read_repository import CanonicalMemoryReadRepository
from app.canonical_memory_repository import CanonicalMemoryRepository, CanonicalMemoryFinalizationError
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_memory_recall import CognitiveMemoryRecall
from app.contracts import CognitiveRequest
from app.experience_models import ExperienceDraft, ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.memory_consolidation_admission import DIALOGUE_EXCHANGE_PREFIX, DIALOGUE_EXCHANGE_LABEL
from app.memory_consolidation_models import MemoryConsolidationResponse
from app.memory_consolidation_worker import MemoryConsolidationWorker
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as VERSION
from app.recall_subjects import RecallSubjectCatalogBuilder
from app.sqlite_database import SQLiteDatabase
from tests.test_dsn_singleplayer import DsnFixture, OWNER, PLAYER, UID
from tests.test_memory_c2b import FakeProvider
from tests.test_memory_d2_cognitive_steps import ScriptedProvider
from tests.test_relationship_b2_context import request


class SharedExperienceTests(DsnFixture):
    def exchange(self, words='你刚提到学摄影，讲讲吧。', story='我以前学摄影时，总是把照片拍糊。',
                 summary='玩家请我谈学摄影，我向玩家讲述以前常把照片拍糊。', **identities):
        events = [self.append('dialogue_input', words, **identities),
                  self.append('dialogue_response', story, **identities)]
        job, context = self.close(events)
        proposal = dict(episodic_memories=[dict(evidence_kind='DIALOGUE_EXCHANGE', summary=summary,
            subject_handles=list(context.identity_by_handle), evidence_event_ids=[e.event_id for e in events],
            importance=.6, confidence=.8)], semantic_facts=[])
        return job, context, proposal

    def test_contract_kind_is_bounded_without_authority_fields(self):
        _, _, proposal = self.exchange()
        self.assertEqual(MemoryConsolidationResponse.model_validate(proposal).episodic_memories[0].evidence_kind,
                         'DIALOGUE_EXCHANGE')
        for changes in ({'evidence_kind':'VERIFIED_WORLD'}, {'provenance':'SYSTEM_CONFIRMED'}):
            bad = json.loads(json.dumps(proposal))
            bad['episodic_memories'][0].update(changes)
            with self.subTest(changes=changes), self.assertRaises(ValidationError):
                MemoryConsolidationResponse.model_validate(bad)

    def test_shared_story_persists_participants_sources_and_attribution(self):
        job, context, proposal = self.exchange()
        batch = self.finalize(job, context, proposal)
        memory = batch.memories[0]
        self.assertEqual(set(memory.subjects), {OWNER, PLAYER})
        self.assertEqual(len(memory.evidence), 2)
        self.assertTrue(memory.summary.startswith(DIALOGUE_EXCHANGE_PREFIX))
        self.assertEqual(self.rows('SELECT provenance_label FROM memory_records')[0]['provenance_label'],
                         DIALOGUE_EXCHANGE_LABEL)
        recalled = self.read(subject=PLAYER, query='学摄影 拍糊')
        self.assertEqual(recalled.status, 'OK')
        self.assertEqual(recalled.items[0].provenance_label, DIALOGUE_EXCHANGE_LABEL)
        self.assertTrue(recalled.items[0].not_current_world_state)
        self.assertEqual(len(recalled.items[0].raw_evidence_refs), 2)

    def test_personal_emotion_exchange_can_be_stored_without_claiming_external_events(self):
        job, context, proposal = self.exchange(words='我最近怕拒绝别人，只想听你怎么想。',
            story='我会先想清楚自己的界限，不需要你解释细节。',
            summary='玩家向我分享不敢拒绝别人的烦恼，我回应自己的看法，没有追问细节。')
        self.assertEqual(len(self.finalize(job, context, proposal).memories), 1)

    def test_untyped_world_event_and_npc_only_player_claim_still_rejected(self):
        _, context, proposal = self.exchange(story='我们已经和第三个人一起去外面拍完照了。')
        candidate = proposal['episodic_memories'][0]
        candidate.update(evidence_kind='EVENT', summary='我和玩家及第三人已经外出拍完照。')
        self.assertEqual(self.admission.evaluate(context, proposal).rejections[0].reason,
                         'NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD')
        candidate.pop('evidence_kind')
        self.assertEqual(self.admission.evaluate(context, proposal).rejections[0].reason,
                         'NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD')
        candidate.update(evidence_kind='DIALOGUE_EXCHANGE',
            evidence_event_ids=[context.evidence_events[-1].event_id])
        self.assertEqual(self.admission.evaluate(context, proposal).rejections[0].reason,
                         'DIALOGUE_EXCHANGE_SOURCE_REQUIRED')

    def test_dialogue_kind_does_not_accept_an_unrelated_or_missing_subject(self):
        _, context, proposal = self.exchange()
        for handles in ([], [next(iter(context.identity_by_handle))], ['participant_99']):
            proposal['episodic_memories'][0]['subject_handles'] = handles
            self.assertFalse(self.admission.evaluate(context, proposal).memories)

    def test_same_pair_and_two_directions_are_required(self):
        first = self.append('dialogue_input', '我想听听你的故事。')
        last = self.append('dialogue_response', '以前学过摄影。', uid='foreign-player', targets=('player_02',))
        _, context = self.close([first, last])
        identities = list(context.identity_by_handle)
        proposal = dict(episodic_memories=[dict(evidence_kind='DIALOGUE_EXCHANGE', summary='谈及摄影。',
            subject_handles=identities, evidence_event_ids=[first.event_id,last.event_id],
            importance=.5,confidence=.7)],semantic_facts=[])
        self.assertFalse(self.admission.evaluate(context, proposal).memories)

    def test_source_loss_cannot_downgrade_dialogue_to_unqualified_fact(self):
        job, context, proposal = self.exchange()
        self.finalize(job, context, proposal)
        with closing(self.database.connect()) as c, c:
            c.execute('DELETE FROM memory_evidence WHERE experience_event_id=?',
                      (context.evidence_events[-1].event_id,))
        result = self.read(subject=PLAYER, query='学摄影 拍糊')
        self.assertEqual(result.items, ())

    def test_player_and_npc_private_scopes_remain_separate(self):
        job, context, proposal = self.exchange()
        self.finalize(job, context, proposal)
        other_player = replace(PLAYER, persistent_id='foreign-player')
        self.assertEqual(self.read(subject=other_player,query='摄影').items, ())
        self.assertEqual(self.read(observer=replace(OWNER,persistent_id='foreign-npc'),query='摄影').items, ())

    def test_finalizer_recomputes_dialogue_admission_and_rolls_back_atomically(self):
        job, context, proposal = self.exchange()
        batch = self.admission.evaluate(context, proposal)
        forged = replace(batch, memories=(replace(batch.memories[0],summary='世界事实已经证实。'),))
        with self.assertRaisesRegex(CanonicalMemoryFinalizationError,'ADMISSION_INTEGRITY_MISMATCH'):
            self.store.finalize_success(job_id=job.job_id, attempt_generation=job.attempt_generation,
                expected_input_hash=job.input_hash,batch=forged,now=time.time())
        with closing(self.database.connect()) as c, c:
            c.execute("CREATE TRIGGER reject_exchange BEFORE INSERT ON memory_evidence "
                      "BEGIN SELECT RAISE(ABORT,'INJECTED_EXCHANGE_FAILURE'); END")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'INJECTED_EXCHANGE_FAILURE'):
            self.finalize(job,context,proposal)
        self.assertEqual(self.rows('SELECT * FROM memory_records'), [])
        self.assertEqual(self.rows('SELECT * FROM memory_subjects'), [])
        self.assertEqual(self.jobs.get(job.job_id).status,'RUNNING')
        with closing(self.database.connect()) as c, c:
            c.execute('DROP TRIGGER reject_exchange')
        self.finalize(job,context,proposal)
        self.assertEqual(len(self.rows('SELECT * FROM memory_records')),1)

    def test_empty_model_result_remains_success_and_worker_deduplicates(self):
        events = [self.append('dialogue_input','你好。'),self.append('dialogue_response','你好。')]
        episode = self.episodes.open_for_event(events[0],'episode-boundary/1.0')
        self.episodes.append_event(episode.episode_id,events[1])
        self.episodes.close_and_enqueue(episode.episode_id,'test_end',time.time(),VERSION)
        provider = FakeProvider()
        worker = MemoryConsolidationWorker(self.jobs,self.resolver,
            BaseMemoryConsolidator(provider,PrioritizedModelExecutor()),self.admission,self.store)
        self.assertTrue(asyncio.run(worker.process_once()))
        self.assertFalse(asyncio.run(worker.process_once()))
        self.assertEqual(len(provider.calls),1)
        self.assertEqual(self.rows('SELECT * FROM memory_records'),[])

    def test_cross_session_real_recall_after_store_restart_without_recent_dialogue(self):
        job,context,proposal = self.exchange()
        self.finalize(job,context,proposal)
        # A new transport session and runtime NPC entity retain the same durable identities.
        data = request(npc=OWNER,player=PLAYER,text='你之前说过学摄影的事情，后来还有继续拍吗？').model_dump()
        data['recall_identity'] = request(npc=OWNER,player=PLAYER).recall_identity.model_dump()
        data['request_id']='new_session_request'
        event=data['context']['current_event']
        event['payload'] = [*event['payload'], dict(key='conversation_id',value='second_conversation')]
        req=CognitiveRequest.model_validate(data)
        draft=ExperienceDraft(schema_version='experience-draft/1.1',observer_id=req.run.agent_entity_id,
            event_type='dialogue_input',source_type='dialogue',source_session_id='second_session',
            clock_domain='backend_utc',source_event_id=event['event_id'],actor_entity_id='player_01',
            target_entity_ids=(req.run.agent_entity_id,),persistent_participants=[
                dict(entity_id=req.run.agent_entity_id,identity_namespace='npc',identity_type='Character',persistent_id=OWNER.persistent_id),
                dict(entity_id='player_01',identity_namespace='player',identity_type='Player',persistent_id=UID)],
            natural_language_payload=event['payload'][0]['value'],occurred_at=100,observed_at=100,
            request_id=req.request_id,conversation_id='second_conversation',provenance='SYSTEM_CONFIRMED')
        self.ingestion.append(ExperienceAppendRequest(request_id='new_append',trace_id='new_trace',draft=draft))
        reopened=SQLiteDatabase(self.database.database_path)
        adapter=CognitiveMemoryRecall(RecallSubjectCatalogBuilder(CanonicalMemoryRepository(reopened.connect)),
            CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(reopened.connect,expected_player_uid=UID)))
        def plan(model):
            self.assertNotIn('照片拍糊',model.messages[-1].content)
            return dict(decision_kind='MEMORY_RECALL',recall_plan=dict(subject_role='shared',
                semantic_query='学摄影 照片拍糊',memory_type='EPISODIC'))
        def answer(model):
            self.assertIn('照片拍糊',model.messages[-1].content)
            self.assertIn(DIALOGUE_EXCHANGE_PREFIX,model.messages[-1].content)
            self.assertIn(DIALOGUE_EXCHANGE_LABEL,model.messages[-1].content)
            return dict(decision_kind='DIRECT_RESPONSE',utterance='上次我讲过学摄影时拍糊的事；后来是否继续，那次没说到。')
        for capacity in (16384,32768):
            provider=ScriptedProvider([plan,answer])
            entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=capacity,
                memory_recall=adapter,experience_ledger=self.ledger)
            self.assertEqual(asyncio.run(entry._read_recent_dialogue(req)),[])
            result=asyncio.run(entry.run(req))
            self.assertEqual(result.decision.decision_kind,'DIRECT_RESPONSE')
            self.assertEqual(len(provider.requests),2)
