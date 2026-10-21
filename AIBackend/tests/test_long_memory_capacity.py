"""Capacity and evidence regressions; all persistence belongs to TemporaryDirectory."""
import unittest
from dataclasses import replace
import time
from app.episode_assembler import EpisodeAssembler
from app.episode_models import EpisodeBoundaryPolicy
from app.experience_models import ExperienceDraft, ExperienceAppendRequest
from app.memory_consolidation_admission import MemoryConsolidationAdmission
from app.canonical_memory_repository import CanonicalMemoryRepository
from app.memory_consolidation_worker import MemoryConsolidationWorker
from app.memory_pipeline import SEMANTIC_MEMORY_CONSOLIDATOR_VERSION as VERSION
from app.memory_maintenance_worker import MemoryMaintenanceWorker, maintenance_request
from app.memory_maintenance_models import MaintenanceInput
from app.memory_maintenance_repository import MemoryMaintenanceRepository
from app.memory_consolidation_context import TrustedPersistentIdentity
from app.context_token_estimator import ContextTokenEstimator
from app.task_capacity import task_policy,task_options,check_result,request_fits
from app.providers import ModelRequest,ModelMessage,ModelResult,ModelUsage,ProviderError
from app.base_memory_consolidator import BaseMemoryConsolidator
from app.local_model_scheduler import PrioritizedModelExecutor
from tests import test_consolidation_evidence_projection as fixtures


class LongMemoryCapacityTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.fixture = fixtures.ConsolidationEvidenceProjectionTests()
        self.fixture.addCleanup = self.addCleanup
        self.fixture.setUp()
        self.serial = 0

    def append(self, kind, text, request):
        self.serial += 1
        actor, target = ('player_01','npc_01') if kind == 'dialogue_input' else ('npc_01','player_01')
        value = ExperienceDraft(schema_version='experience-draft/1.1', observer_id='npc_01',
            event_type=kind, source_type='dialogue', source_session_id='session', clock_domain='unity_realtime',
            source_event_id=f'source_{self.serial}', actor_entity_id=actor, target_entity_ids=(target,),
            persistent_participants=(dict(entity_id='npc_01',identity_namespace='npc',identity_type='Character',persistent_id='merchant.001'),
                dict(entity_id='player_01',identity_namespace='player',identity_type='Player',persistent_id='player.001')),
            structured_payload={}, natural_language_payload=text, occurred_at=self.serial, observed_at=self.serial,
            request_id=request, conversation_id='conversation', provenance='SYSTEM_CONFIRMED')
        ack = self.fixture.ingestion.append(ExperienceAppendRequest(request_id=f'append_{self.serial}',trace_id='test',draft=value))
        return self.fixture.ledger.get_by_event_id(ack.event_id)

    def pair(self, index, text='我喜欢茶。'):
        return (self.append('dialogue_input',text,f'turn_{index}'),
                self.append('dialogue_response','我明白了。',f'turn_{index}'))

    def assembler(self, probe, batch=64):
        return EpisodeAssembler(self.fixture.ledger,self.fixture.episodes,
            EpisodeBoundaryPolicy(processing_batch=batch),extractor_version=VERSION,capacity_probe=probe)

    def test_known_16k_uses_capacity_and_reserves_complete_json(self):
        provider = self.fixture.provider
        provider.context_length = 16384
        consolidator = BaseMemoryConsolidator(provider, PrioritizedModelExecutor())
        policy = consolidator.budget_policy
        self.assertEqual(policy.max_context_tokens, 16384)
        # The real 3K trial truncated a long JSON twice; reserve 4K inside 16K.
        self.assertGreaterEqual(policy.input_hard_cap, 11000)
        self.assertEqual(policy.response_reserve, 4096)
        self.assertLessEqual(policy.input_hard_cap + policy.response_reserve + policy.estimation_reserve, 16384)

    def test_small_and_unknown_providers_do_not_inherit_ui_64k(self):
        provider = self.fixture.provider
        provider.context_length = 4096
        consolidator = BaseMemoryConsolidator(provider, PrioritizedModelExecutor())
        self.assertEqual(consolidator.budget_policy.max_context_tokens, 4096)
        del provider.context_length
        self.assertEqual(BaseMemoryConsolidator(provider, PrioritizedModelExecutor()).budget_policy.max_context_tokens, 8192)

    def test_larger_core_fits_16k_without_dropping_dialogue(self):
        job = self.fixture.fixture(observations=0, kinds=('dialogue_input','dialogue_response') * 13)
        context = self.fixture.resolver.resolve(job)
        provider = self.fixture.provider
        provider.context_length = 16384
        projection = BaseMemoryConsolidator(provider, PrioritizedModelExecutor()).project(context)
        self.assertEqual(projection.visible_event_ids, tuple(e.event_id for e in context.evidence_events))
        self.assertGreater(projection.budget_allocation.estimated_total, 6000)

    def test_capacity_boundaries_keep_pairs_and_reference_sources(self):
        for i in range(3):self.pair(i)
        assembler = self.assembler(lambda events:len(events)<=2,batch=1)
        while assembler.process_observer('npc_01').processed:pass
        episodes=self.fixture.episodes.list_for_observer('npc_01')
        self.assertEqual([(1,2),(3,4),(5,6)],[(e.start_seq,e.end_seq) for e in episodes])
        self.assertEqual('capacity',episodes[0].close_reason)
        job=self.fixture.jobs.get_for_episode(episodes[1].episode_id,VERSION)
        context=self.fixture.resolver.resolve(job)
        self.assertEqual([1,2],[e.experience_seq for e in context.reference_events])
        ref,current=context.reference_events[0],context.evidence_events[0]
        handle=current.participant_handles['player_01']
        def fact(ids):return {'episodic_memories':[],'semantic_facts':[{'subject_handle':handle,'fact':'喜欢茶。',
            'source_excerpt':'我喜欢茶。','evidence_event_ids':ids,'confidence':.9}]}
        admission=MemoryConsolidationAdmission()
        rejected=admission.evaluate(context,fact([ref.event_id]))
        self.assertEqual('REFERENCE_ONLY_EVIDENCE',rejected.rejections[0].reason)
        admitted=admission.evaluate(context,fact([ref.event_id,current.event_id]))
        self.assertEqual(len(admitted.memories),1)
        self.assertEqual({1,3},{seq for _,seq in admitted.memories[0].evidence})
        before=[e.payload_hash for e in self.fixture.ledger.read_range('npc_01')]
        self.assertEqual(assembler.process_observer('npc_01').processed,0)
        self.assertEqual(before,[e.payload_hash for e in self.fixture.ledger.read_range('npc_01')])

    def test_pending_input_waits_and_pair_can_exceed_soft_page(self):
        self.append('dialogue_input','我喜欢茶。','pending')
        assembler=self.assembler(lambda e:True,batch=1)
        self.assertEqual(assembler.process_observer('npc_01').processed,0)
        self.append('dialogue_response','好的。','pending')
        self.assertEqual(assembler.process_observer('npc_01').processed,2)
        self.assertEqual(self.fixture.episodes.get_open('npc_01').end_seq,2)

    def test_overlimit_indivisible_pair_is_preserved_for_safe_failure(self):
        self.pair(0)
        assembler=self.assembler(lambda events:len(events)<=1)
        assembler.process_observer('npc_01')
        episode=self.fixture.episodes.list_for_observer('npc_01')[0]
        self.assertEqual((1,2,'indivisible_capacity_exceeded'),
                         (episode.start_seq,episode.end_seq,episode.close_reason))
        self.assertEqual(len(self.fixture.ledger.read_range('npc_01')),2)

    def test_restart_after_input_half_keeps_confirmed_pair_together(self):
        pair=self.pair(0)
        self.fixture.episodes.open_for_event(pair[0],'episode-boundary/1.0')
        self.assembler(lambda events:len(events)<=1).process_observer('npc_01')
        episodes=self.fixture.episodes.list_for_observer('npc_01')
        self.assertEqual([(1,2)],[(e.start_seq,e.end_seq) for e in episodes])
        self.assertEqual('indivisible_capacity_exceeded',episodes[0].close_reason)

    def test_actual_provider_usage_overrun_is_rejected_before_write(self):
        provider=self.fixture.provider;provider.context_length=16384
        policy=task_policy(provider,'consolidation')
        request=ModelRequest(messages=(ModelMessage(role='user',content='test'),),inference_options=task_options(provider,policy))
        for usage in (ModelUsage(prompt_tokens=policy.input_hard_cap+1,completion_tokens=10),
                      ModelUsage(prompt_tokens=100,completion_tokens=policy.response_reserve+1)):
            with self.assertRaises(ProviderError):check_result(ModelResult(text='{}',usage=usage),request,policy)

    def test_restart_idle_sweep_cannot_split_already_confirmed_pair(self):
        first=self.append('dialogue_input','我喜欢茶。','delayed')
        self.fixture.episodes.open_for_event(first,'episode-boundary/1.0')
        self.serial+=1000
        self.append('dialogue_response','好的。','delayed')
        assembler=self.assembler(lambda events:True)
        self.assertEqual(assembler.idle_sweep(time.time()+1000),0)
        self.assertEqual(assembler.process_observer('npc_01').processed,1)
        episodes=self.fixture.episodes.list_for_observer('npc_01')
        self.assertEqual([(1,2)],[(e.start_seq,e.end_seq) for e in episodes])

    def test_explicit_task_policy_cannot_override_smaller_provider_window(self):
        provider=self.fixture.provider;provider.context_length=16384
        policy=task_policy(provider,'consolidation')
        request=ModelRequest(messages=(ModelMessage(role='user',content='test'),),inference_options=task_options(provider,policy))
        provider.context_length=4096
        self.assertFalse(request_fits(provider,request,policy))

    async def test_16k_worker_and_transaction_share_projection_and_output_cap(self):
        job=self.fixture.fixture(observations=0,kinds=('dialogue_input','dialogue_response')*13)
        provider=self.fixture.provider;provider.context_length=16384
        con=BaseMemoryConsolidator(provider,PrioritizedModelExecutor())
        admission=MemoryConsolidationAdmission(projector=con.projector)
        store=CanonicalMemoryRepository(self.fixture.database.connect,admission=admission)
        worker=MemoryConsolidationWorker(self.fixture.jobs,self.fixture.resolver,con,admission,store)
        await worker.process_once()
        self.assertEqual(self.fixture.jobs.get(job.job_id).status,'SUCCEEDED')
        self.assertEqual(provider.calls[0].inference_options.max_output_tokens,4096)
        self.assertEqual(len(provider.calls),1)

    def test_maintenance_keeps_complete_evidence_above_old_4800_cap(self):
        provider=self.fixture.provider;provider.context_length=16384
        identity=TrustedPersistentIdentity('npc','Character','merchant.001')
        records=tuple({'memory_id':f'm{i}','summary':'完整带条件证据。'*100,'memory_type':'SEMANTIC_FACT',
            'evidence_event_ids':[f'e{i}']} for i in range(8))
        context=MaintenanceInput('j',1,identity,identity,('m0',),records)
        self.assertGreater(ContextTokenEstimator().estimate_text(maintenance_request(context).model_dump_json()),4800)
        worker=MemoryMaintenanceWorker(MemoryMaintenanceRepository(self.fixture.database.connect),provider,PrioritizedModelExecutor())
        bounded,request=worker._bounded(context)
        self.assertEqual(bounded.records,records)
        self.assertEqual(request.inference_options.max_output_tokens,1600)


if __name__ == '__main__':
    unittest.main()
