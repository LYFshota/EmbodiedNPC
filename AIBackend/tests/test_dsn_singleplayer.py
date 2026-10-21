"""DSN-SP deterministic source/identity gate, using only isolated fixture databases."""
from contextlib import closing
from dataclasses import replace
import os
from pathlib import Path
import tempfile
import time
import unittest

from app.canonical_memory_read_repository import CanonicalMemoryReadRepository, validate_demo_player_uid
from app.canonical_memory_retrieval import CanonicalMemoryRetrieval
from app.canonical_memory_repository import CanonicalMemoryRepository, CanonicalMemoryFinalizationError
from app.canonical_recall_models import CanonicalRecallBudget
from app.experience_ingestion import ExperienceIngestionService
from app.experience_ledger import ExperienceLedger
from app.experience_models import ExperienceDraft, ExperienceAppendRequest
from app.episode_repository import EpisodeRepository
from app.memory_job_repository import MemoryJobRepository
from app.memory_consolidation_context import MemoryContextResolver, TrustedPersistentIdentity
from app.memory_consolidation_admission import MemoryConsolidationAdmission, NPC_SELF_STATEMENT_LABEL
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as V2
from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from app.sqlite_database import SQLiteDatabase

UID = os.getenv('EMBODIEDNPC_DEMO_PLAYER_UID', 'cafe-local-player-001')
OWNER = TrustedPersistentIdentity('npc', 'Character', 'dsn.npc.001')
PLAYER = TrustedPersistentIdentity('player', 'Player', UID)


class DsnFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.database = SQLiteDatabase(Path(self.temp.name)/'dsn.sqlite3')
        self.ledger = ExperienceLedger(self.database.connect)
        self.ingestion = ExperienceIngestionService(self.ledger)
        self.episodes = EpisodeRepository(self.database.connect)
        self.jobs = MemoryJobRepository(self.database.connect)
        self.resolver = MemoryContextResolver(self.ledger, self.episodes)
        self.admission = MemoryConsolidationAdmission()
        self.store = CanonicalMemoryRepository(self.database.connect)
        self.serial = 0
        self.last_input = None

    def append(self, kind, text=None, *, uid=UID, owner=OWNER.persistent_id, actor=None,
               targets=None, payload=None, participants=True):
        self.serial += 1
        n = self.serial
        actor = actor or ('player_01' if kind == 'dialogue_input' else 'npc_01')
        targets = targets if targets is not None else (('npc_01',) if kind == 'dialogue_input' else ('player_01',))
        entities = {actor, *targets}
        identities = [dict(entity_id=e, identity_namespace='npc' if e=='npc_01' else 'player',
            identity_type='Character' if e=='npc_01' else 'Player', persistent_id=owner if e=='npc_01' else uid)
            for e in sorted(entities)] if participants else []
        dialogue = kind.startswith('dialogue_')
        parent = self.last_input if kind=='dialogue_response' and not payload else None
        draft = ExperienceDraft(schema_version='experience-draft/1.1', observer_id='npc_01',
            event_type=kind, source_type='dialogue' if dialogue else 'goal_outcome',
            source_session_id='dsn_session', clock_domain='backend_utc', source_event_id=parent.source_event_id if parent else f'event_{n}',
            actor_entity_id=actor, target_entity_ids=targets, persistent_participants=identities,
            structured_payload=payload or {}, natural_language_payload=text,
            occurred_at=float(n), observed_at=float(n), request_id=parent.request_id if parent else f'request_{n}',
            conversation_id='dsn_conversation', provenance='SYSTEM_CONFIRMED' if kind=='dialogue_input' else 'EXECUTED_BY_SELF')
        request = ExperienceAppendRequest(request_id=f'append_{n}', trace_id=f'trace_{n}', draft=draft)
        ack = self.ingestion.append(request)
        self.assertEqual('inserted', ack.status)
        self.assertEqual('already_exists', self.ingestion.append(request).status)
        result = self.ledger.get_by_event_id(ack.event_id)
        if kind=='dialogue_input': self.last_input=result
        return result

    def close(self, events):
        episode = self.episodes.open_for_event(events[0], 'episode-boundary/1.0')
        for event in events[1:]: self.episodes.append_event(episode.episode_id, event)
        self.episodes.close_and_enqueue(episode.episode_id, 'test_close', time.time(), V2)
        job = self.jobs.claim_next(time.time())
        return job, self.resolver.resolve(job)

    def proposal(self, context, events, *, self_subject=True, profile=False, summary='小时候常去旧书店读推理小说。', confidence=.6):
        identity = context.observer_persistent_identity if self_subject else next(i for i in context.identity_by_handle.values() if i.identity_type=='Player')
        handle = next(h for h, i in context.identity_by_handle.items() if i==identity)
        evidence = [e.event_id for e in events]
        return dict(episodic_memories=[] if profile else [dict(summary=summary, subject_handles=[handle],
            evidence_event_ids=evidence, confidence=confidence, importance=.6)],
            semantic_facts=[dict(subject_handle=handle,fact=events[0].natural_language_payload,source_excerpt=events[0].natural_language_payload,
                evidence_event_ids=evidence,confidence=confidence)] if profile else [])

    def finalize(self, job, context, proposal):
        batch = self.admission.evaluate(context, proposal)
        self.assertTrue(self.store.finalize_success(job_id=job.job_id, attempt_generation=job.attempt_generation,
            expected_input_hash=job.input_hash, batch=batch, now=time.time()).applied)
        self.assertEqual('SUCCEEDED', self.jobs.get(job.job_id).status)
        return batch

    def read(self, *, observer=OWNER, subject=None, budget=CanonicalRecallBudget(), query='旧书店 推理小说 卡布奇诺 打开门'):
        return CanonicalMemoryRetrieval(CanonicalMemoryReadRepository(self.database.connect,
            expected_player_uid=UID)).retrieve(observer=observer,subject=subject,semantic_query=query,budget=budget)

    def rows(self, sql, args=()):
        with closing(self.database.connect()) as db: return [dict(r) for r in db.execute(sql,args)]

    def speech_episode(self, *, auto=False, uid=UID, owner=OWNER.persistent_id):
        first = self.append('dialogue_input','你小时候喜欢什么？',uid=uid,owner=owner)
        response = self.append('dialogue_response','小时候常去旧书店读推理小说。',uid=uid,owner=owner)
        events = [first, response]
        if auto:
            response = self.append('dialogue_response','那里还常有散文。',uid=uid,owner=owner,payload=dict(
                initiative='dialogue_continuation',continuation_index=1,parent_request_id=first.request_id,
                parent_event_id=first.source_event_id,last_response_event_id=response.event_id,
                last_response_seq=response.experience_seq))
            events.append(response)
        job, context = self.close(events)
        return job, context, response

class DsnP0Tests(DsnFixture):
    def test_uid_configuration_rejects_blank_whitespace_and_invalid(self):
        self.assertEqual('cafe-local-player-001', validate_demo_player_uid('cafe-local-player-001'))
        self.assertEqual(UID, validate_demo_player_uid(UID))
        for value in ('', ' ', 'other user', 'x'*161):
            with self.assertRaises(ValueError): validate_demo_player_uid(value)

    def test_entry_checks_fixed_foreign_missing_wrong_kind_and_namespace(self):
        from app import m3_main
        from tests.test_memory_d0_subjects import request
        self.assertEqual(str(Path(os.environ['EMBODIEDNPC_MEMORY_DB']).resolve()), str(Path(m3_main.database.database_path).resolve()))
        self.assertIsNone(m3_main.demo_identity_error(request(requester_uid=UID)))
        self.assertIsNotNone(m3_main.demo_identity_error(request(requester_uid='foreign')))
        self.assertIsNotNone(m3_main.demo_identity_error(request(requester_uid=None)))
        original = request(requester_uid=UID)
        for changes in (dict(kind='npc'),dict(identity_namespace='npc'),dict(identity_type='Character')):
            subject = original.recall_identity.current_subjects[0].model_copy(update=changes)
            bad = original.model_copy(update={'recall_identity': original.recall_identity.model_copy(update={'current_subjects':(subject,)})})
            self.assertIsNotNone(m3_main.demo_identity_error(bad))
        self.assertIsNotNone(m3_main.demo_identity_error(original.model_copy(update={'recall_identity':None})))

    def test_entry_rejects_foreign_and_unknown_dialogue_before_ack(self):
        from app import m3_main
        for uid, participants, rejected in ((UID,True,False),('foreign',True,True),(UID,False,True)):
            event = self.append('dialogue_input','喜欢卡布奇诺。',uid=uid,participants=participants)
            draft = ExperienceDraft.model_validate({k:v for k,v in event.model_dump().items()
                if k in ExperienceDraft.model_fields and k!='schema_version'} | {'schema_version':'experience-draft/1.1'})
            self.assertEqual(rejected, m3_main.demo_identity_error(ExperienceAppendRequest(request_id='check',trace_id='check',draft=draft)) is not None)

    def test_normal_and_autonomous_self_reports_share_source_rule(self):
        for auto in (False,True):
            job, context, speech = self.speech_episode(auto=auto)
            batch = self.finalize(job,context,self.proposal(context,[speech]))
            self.assertEqual(1,len(batch.memories))
            self.assertEqual((OWNER,),batch.memories[0].subjects)
            self.assertTrue(batch.memories[0].summary.startswith('NPC曾自述'))
        self.assertTrue(all(i.provenance_label==NPC_SELF_STATEMENT_LABEL for i in self.read().items))

    def test_npc_only_cannot_certify_player_profile_or_shared_world_history(self):
        for auto in (False,True):
            _, context, speech = self.speech_episode(auto=auto)
            for profile in (False,True):
                batch = self.admission.evaluate(context,self.proposal(context,[speech],self_subject=False,
                    profile=profile,summary='店员已把杯子送给玩家。'))
                self.assertEqual((),batch.memories)
                self.assertEqual('NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD',batch.rejections[0].reason)

    def test_npc_self_claim_of_action_is_only_speech_in_recall(self):
        job, context, speech = self.speech_episode()
        self.finalize(job,context,self.proposal(context,[speech],summary='本人声称昨天送过杯子。'))
        item, = self.read(query='杯子').items
        self.assertEqual(NPC_SELF_STATEMENT_LABEL,item.provenance_label)
        self.assertTrue(item.summary.startswith('NPC曾自述'))

    def test_self_profile_recall_has_source_qualifier(self):
        job, context, speech = self.speech_episode(auto=True)
        self.finalize(job,context,self.proposal(context,[speech],profile=True))
        # auto=True returns the last displayed statement about essays; the
        # default query is about the earlier bookshop claim, not this source.
        item, = self.read(query=speech.natural_language_payload).items
        self.assertEqual('SEMANTIC_FACT',item.memory_type)
        self.assertEqual(NPC_SELF_STATEMENT_LABEL,item.provenance_label)
        self.assertTrue(item.summary.startswith('NPC曾自述'))

    def test_model_hidden_and_unpersisted_evidence_cannot_enter_memory(self):
        job, context, speech = self.speech_episode()
        proposal = self.proposal(context,[speech])
        hidden = self.admission.evaluate(context,proposal,visible_event_ids=(context.evidence_events[0].event_id,))
        self.assertEqual('EVIDENCE_NOT_MODEL_VISIBLE',hidden.rejections[0].reason)
        bad = self.proposal(context,[speech]); bad['episodic_memories'][0]['evidence_event_ids']=['not_displayed']
        self.assertEqual('EVIDENCE_OUTSIDE_CONTEXT',self.admission.evaluate(context,bad).rejections[0].reason)
        with self.assertRaises(CanonicalMemoryFinalizationError):
            self.store.finalize_success(job_id=job.job_id,attempt_generation=job.attempt_generation,
                expected_input_hash=job.input_hash,batch=hidden,now=time.time())
        self.assertEqual([],self.rows('SELECT * FROM memory_records'))

    def test_open_subject_and_explicit_foreign_subject_cannot_bypass_uid(self):
        good, c, speech = self.speech_episode()
        self.finalize(good,c,self.proposal(c,[speech]))
        bad, c, speech = self.speech_episode(uid='foreign')
        self.finalize(bad,c,self.proposal(c,[speech],summary='本人另有外国书店经历。'))
        self.assertEqual(1,self.read(subject=None).eligible_count)
        self.assertEqual('NO_MEMORY_FOUND',self.read(subject=TrustedPersistentIdentity('player','Player','foreign')).status)

    def test_subject_player_uid_is_filtered_even_with_legal_raw(self):
        job,c,speech=self.speech_episode(); batch=self.finalize(job,c,self.proposal(c,[c.evidence_events[0]],self_subject=False,profile=True))
        with closing(self.database.connect()) as db:
            db.execute('UPDATE memory_subjects SET persistent_id=? WHERE identity_type=?',('foreign','Player')); db.commit()
        self.assertEqual('NO_MEMORY_FOUND',self.read().status)

    def test_entire_evidence_is_checked_beyond_sixteen_projected_refs(self):
        job,c,speech=self.speech_episode(); memory=self.finalize(job,c,self.proposal(c,[speech])).memories[0]
        for n in range(17):
            self.append('dialogue_input',f'问询{n}',uid='foreign' if n==16 else UID)
            event=self.append('dialogue_response',f'重复自述{n}',uid='foreign' if n==16 else UID)
            with closing(self.database.connect()) as db:
                db.execute('INSERT INTO memory_evidence VALUES(?,?,?)',(memory.memory_id,event.event_id,event.experience_seq)); db.commit()
        self.assertEqual('NO_MEMORY_FOUND',self.read(budget=CanonicalRecallBudget(max_evidence_refs_per_item=16)).status)

    def test_unknown_legacy_dialogue_is_conservative(self):
        event=self.append('dialogue_input','旧记录',participants=False)
        job,c=self.close([event]); batch=self.finalize(job,c,dict(episodic_memories=[dict(summary='旧对话记录',subject_handles=[],
            evidence_event_ids=[event.event_id],importance=.6,confidence=.9)],semantic_facts=[]))
        self.assertEqual(1,len(batch.memories))
        self.assertEqual('NO_MEMORY_FOUND',self.read().status)

    def test_legacy_self_summary_is_qualified_without_rewriting_record(self):
        job,c,speech=self.speech_episode(); memory=self.finalize(job,c,self.proposal(c,[speech])).memories[0]
        with closing(self.database.connect()) as db:
            db.execute('UPDATE memory_records SET summary=?,provenance_label=? WHERE memory_id=?',('小时候常去旧书店。','EXECUTED_BY_SELF',memory.memory_id));db.commit()
        item,=self.read().items
        self.assertEqual(NPC_SELF_STATEMENT_LABEL,item.provenance_label)
        self.assertTrue(item.summary.startswith('NPC曾自述'))
        self.assertEqual('小时候常去旧书店。',self.rows('SELECT summary FROM memory_records')[0]['summary'])

    def test_player_likes_and_nonplayer_action_outcome_stay_readable(self):
        first=self.append('dialogue_input','我喜欢卡布奇诺。'); job,c=self.close([first])
        proposal=self.proposal(c,[first],self_subject=False,profile=True)
        proposal['semantic_facts'][0]['fact']='玩家喜欢卡布奇诺。'
        pref=self.finalize(job,c,proposal).memories[0]
        event=self.append('goal_succeeded',targets=(),payload={'goal_id':'goal_1','goal_type':'open_door'})
        job,c=self.close([event]); self.finalize(job,c,self.proposal(c,[event],summary='完成打开门的任务。'))
        result=self.read()
        self.assertEqual(2,len(result.items))
        self.assertEqual('SEMANTIC_FACT',next(i for i in result.items if i.memory_id==pref.memory_id).memory_type)
        self.assertEqual('EXECUTED_BY_SELF',next(i for i in result.items if i.memory_type=='EPISODIC').provenance_label)

    def test_npc_only_relationship_is_empty_and_player_expression_survives(self):
        response=self.append('dialogue_response','我小时候去过旧书店。'); _,c=self.close([response])
        self.assertEqual((),RelationshipReflectionCandidateBuilder().build(c))
        first=self.append('dialogue_input','我很喜欢你的想法。'); _,c=self.close([first])
        candidate,=RelationshipReflectionCandidateBuilder().build(c)
        self.assertEqual(PLAYER,candidate.subject)

    def test_mixed_autonomous_cannot_certify_action_or_player(self):
        _,c,speech=self.speech_episode(auto=True)
        batch=self.admission.evaluate(c,self.proposal(c,[c.evidence_events[0],speech],self_subject=False))
        self.assertEqual((),batch.memories)

    def test_cited_player_question_does_not_upgrade_or_block_own_narrative(self):
        for auto in (False, True):
            job,c,speech=self.speech_episode(auto=auto)
            evidence=[c.evidence_events[0],speech]
            batch=self.finalize(job,c,self.proposal(c,evidence))
            self.assertEqual(1,len(batch.memories))
            self.assertTrue(batch.memories[0].summary.startswith('NPC曾自述'))
        self.assertEqual({'NPC_SELF_STATEMENT'},{i.provenance_label for i in self.read().items})


if __name__=='__main__': unittest.main()
