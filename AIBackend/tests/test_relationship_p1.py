"""P1: bounded social deltas through the real worker, repository and cognition."""
import asyncio
from contextlib import closing
import unittest

from pydantic import ValidationError
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetPolicy
from app.model_requests import CognitiveModelRequestFactory
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from app.relationship_context import RelationshipContextResolver
from app.relationship_reflection_model import RelationshipReflectionModel, RelationshipReflectionModelResponse
from app.relationship_reflection_repository import PairUpdateRejected, RelationshipReflectionRepository
from app.relationship_reflection_worker import RelationshipReflectionWorker
from app.relationship_summary_reader import RelationshipSummaryReader
from app.sqlite_database import SQLiteDatabase
from tests import test_relationship_pair_state as pair
from tests.test_relationship_b2_context import request, projected
from tests.test_relationship_reflection_execution import FakeProvider, RecordingExecutor, StubScanner
from tests.test_memory_d2_cognitive_steps import ScriptedProvider


class RelationshipP1Tests(unittest.TestCase):
    def setUp(self):
        self.f=pair.RelationshipPairStateTests();self.f.setUp()
        self.addCleanup(self.f.doCleanups)

    @staticmethod
    def update(text,quote,*,operation='ADD',target=None,kind='BOUNDARY',event='e0'):
        return dict(operation=operation,target=target,kind=kind,text=text,event=event,quote=quote)

    def response(self,*updates):
        return RelationshipReflectionModelResponse(outcome='INTERPRETATION',
            interpretation='新增的交流方式与边界。',state_updates=updates)

    def seed(self):
        for text in ('喜欢轻松玩笑。','不拿家人开玩笑。','疲惫时留出安静。'):
            self.f.commit(self.f.candidate(text),self.f.response(text,quote=text))
        return self.f.repo.read_current_pair_state(pair.NPC,pair.PLAYER)

    def test_one_episode_one_model_call_can_commit_three_independent_updates(self):
        words='可以轻松开玩笑。不要拿家人开玩笑。不同意见请直接说。'
        candidate=self.f.candidate(words)
        response=self.response(self.update('通常可以轻松开玩笑。','可以轻松开玩笑。',kind='EXPLICIT'),
            self.update('不拿家人开玩笑。','不要拿家人开玩笑。'),
            self.update('不同意见直接表达。','不同意见请直接说。',kind='EXPLICIT'))
        provider=FakeProvider([response.model_dump_json()]);executor=RecordingExecutor()
        worker=RelationshipReflectionWorker(StubScanner(candidate),RelationshipReflectionModel(provider,executor),self.f.repo)
        result=asyncio.run(worker.process_batch())
        state=self.f.repo.read_current_pair_state(pair.NPC,pair.PLAYER)
        self.assertEqual((result.persisted,len(provider.requests),executor.background_calls),(1,1,1))
        self.assertEqual([i.text for i in state.items],[u.text for u in response.state_updates])
        self.assertEqual(len({i.item_id for i in state.items}),3)

    def test_no_change_does_not_upgrade_existing_understanding_or_rewrite_anchors(self):
        previous=self.seed()
        for _ in range(3):
            candidate=self.f.candidate('你好。')
            state=self.f.commit(candidate,self.f.response())
            self.assertEqual(state.items,previous.items)
            self.assertFalse(state.changed)

    def test_correct_keeps_other_items_and_targets_original_snapshot(self):
        previous=self.seed()
        words='今天不要开玩笑。家人可以偶尔提起但不能调侃。'
        candidate=self.f.candidate(words)
        response=self.response(self.update('今天不使用玩笑。','今天不要开玩笑。',operation='CORRECT',target='s0'),
            self.update('可以提家人，但仍不调侃。','家人可以偶尔提起但不能调侃。',operation='CORRECT',target='s1'))
        state=self.f.commit(candidate,response)
        self.assertEqual(state.items[2],previous.items[2])
        self.assertEqual([i.predecessor_id for i in state.items[:2]],[i.item_id for i in previous.items[:2]])

    def test_duplicate_targets_and_updates_are_rejected_without_partial_commit(self):
        previous=self.seed();candidate=self.f.candidate('今天不要开玩笑。')
        first=self.update('今天不使用玩笑。','今天不要开玩笑。',operation='CORRECT',target='s0')
        second=self.update('玩笑的适用范围不明确。','今天不要开玩笑。',operation='CONFLICT',target='s0')
        with self.assertRaises(ValidationError):self.response(first,second)
        # A bypassed model validation still cannot reach a partial repository commit.
        response=self.response(first).model_copy(update={'state_updates':(self.response(first).state_updates[0],self.response(second).state_updates[0])})
        with self.assertRaises(PairUpdateRejected):self.f.commit(candidate,response)
        self.assertEqual(self.f.repo.read_current_pair_state(pair.NPC,pair.PLAYER),previous)
        add=self.update('交流时避免追问。','今天不要开玩笑。')
        with self.assertRaises(ValidationError):self.response(add,add)

    def test_invalid_last_source_rolls_back_the_entire_batch(self):
        previous=self.seed();candidate=self.f.candidate('今天不要开玩笑。')
        response=self.response(self.update('今天不使用玩笑。','今天不要开玩笑。',operation='CORRECT',target='s0'),
            self.update('可以追问所有隐私。','不存在的玩家引文。'))
        with self.assertRaises(PairUpdateRejected):self.f.commit(candidate,response)
        self.assertEqual(self.f.repo.read_current_pair_state(pair.NPC,pair.PLAYER),previous)
        self.assertIsNone(self.f.repo.get(self.f.record(candidate,response).reflection_id))

    def test_capacity_limited_batch_applies_neither_correction_nor_add(self):
        previous=self.f.fill_capacity()
        words='工作时保持正式。不要问财务。'
        candidate=self.f.candidate(words)
        response=self.response(self.update('工作时保持正式称呼。','工作时保持正式。',operation='CORRECT',target='s0'),
            self.update('不讨论财务。','不要问财务。'))
        state=self.f.commit(candidate,response)
        self.assertEqual(state.items,previous.items)
        self.assertEqual(len(state.capacity_limited),2)
        self.assertFalse(state.changed)
        visible=RelationshipSummaryReader(self.f.repo).get_for_cognition(pair.NPC,pair.PLAYER).model_dump_json()
        self.assertNotIn('不讨论财务。',visible)
        self.assertNotIn('工作时保持正式称呼。',visible)
        reopened=RelationshipReflectionRepository(SQLiteDatabase(self.f.path).connect)
        self.assertEqual(reopened.read_current_pair_state(pair.NPC,pair.PLAYER),state)

    def test_batch_exceeding_one_remaining_slot_does_not_fill_it_partially(self):
        previous=self.seed();words='不问健康。不问财务。'
        state=self.f.commit(self.f.candidate(words),self.response(
            self.update('不讨论健康。','不问健康。'),self.update('不讨论财务。','不问财务。')))
        self.assertEqual(state.items,previous.items)
        self.assertEqual(len(state.capacity_limited),2)

    def test_multi_update_cas_rejects_stale_base(self):
        previous=self.seed();candidate=self.f.candidate('今天不要开玩笑。不要追问细节。')
        self.f.commit(self.f.candidate('你好。'),self.f.response())
        response=self.response(self.update('今天不使用玩笑。','今天不要开玩笑。',operation='CORRECT',target='s0'),
            self.update('不要追问细节。','不要追问细节。'))
        with self.assertRaisesRegex(PairUpdateRejected,'BASE_CHANGED'):self.f.commit(candidate,response,previous)
        self.assertIsNone(self.f.repo.get(self.f.record(candidate,response).reflection_id))

    def test_database_failure_rolls_back_multi_update_snapshot(self):
        previous=self.seed();candidate=self.f.candidate('今天不要开玩笑。不要追问细节。')
        response=self.response(self.update('今天不使用玩笑。','今天不要开玩笑。',operation='CORRECT',target='s0'),
            self.update('不要追问细节。','不要追问细节。'))
        with closing(self.f.db.connect()) as connection,connection:
            connection.execute("CREATE TRIGGER reject_p1 BEFORE INSERT ON relationship_reflections "
                "BEGIN SELECT RAISE(ABORT,'INJECTED_P1_FAILURE'); END")
        import sqlite3
        with self.assertRaisesRegex(sqlite3.IntegrityError,'INJECTED_P1_FAILURE'):self.f.commit(candidate,response)
        reopened=RelationshipReflectionRepository(SQLiteDatabase(self.f.path).connect)
        self.assertEqual(reopened.read_current_pair_state(pair.NPC,pair.PLAYER),previous)
        self.assertIsNone(reopened.get(self.f.record(candidate,response).reflection_id))

    def test_three_update_wire_fits_background_output_reserve_at_16k_and_32k(self):
        response=self.response(*(self.update(f'交流偏好{i}：'+ '保留条件与不确定程度。'*5,
            f'约定{i}：'+ '请直接表达不同意见。'*5) for i in range(3)))
        for capacity in (16384,32768):
            model=RelationshipReflectionModel(FakeProvider([]),RecordingExecutor(),context_length=capacity)
            self.assertEqual(model.policy.response_reserve,1024)
            self.assertLessEqual(ContextTokenEstimator().estimate_text(response.model_dump_json()),model.policy.response_reserve)

    def test_current_full_relationship_enters_cognition_once_at_16k_and_32k(self):
        self.f.fill_capacity()
        resolver=RelationshipContextResolver(RelationshipSummaryReader(self.f.repo))
        for capacity in (16384,32768):
            provider=ScriptedProvider([{'decision_kind':'DIRECT_RESPONSE','utterance':'你好。'}])
            entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=capacity,
                relationship_context_resolver=resolver)
            asyncio.run(entry.run(request()))
            wire=provider.requests[0].messages[-1].content
            current=projected(provider.requests[0])
            self.assertEqual(len(current['current_state']['items']),4)
            self.assertFalse(current['truncated'])
            self.assertEqual(wire.count('Relationship context JSON:'),1)
            self.assertNotIn('interpretations',current)
            self.assertIn('Relationship guides pair-specific social distance',wire)

    def test_plural_schema_does_not_accept_single_update_preview_payload(self):
        for payload in ({'outcome':'NO_CHANGE','interpretation':None,'state_update':None},
            {'outcome':'INTERPRETATION','interpretation':'x'},
            {'outcome':'INTERPRETATION','interpretation':'x','state_updates':[self.update(str(i),'原文') for i in range(4)]}):
            with self.subTest(payload=payload),self.assertRaises(ValidationError):RelationshipReflectionModelResponse.model_validate(payload)

    def test_global_pressure_omits_all_current_conflicts_without_losing_one_side(self):
        for i in range(4):
            text=f'旧理解{i}：'+'交流方式必须保留完整条件。'*8
            self.f.commit(self.f.candidate(text),self.f.response(text,quote=text))
        for i in range(4):
            text=f'新信号{i}：'+'交流方式仍有未澄清的条件。'*8
            self.f.commit(self.f.candidate(text),self.f.response(text,quote=text,operation='CONFLICT',target=f's{i}'))
        social=RelationshipContextResolver(RelationshipSummaryReader(self.f.repo)).resolve(request())
        original=social.model_dump_json()
        baseline=CognitiveModelRequestFactory.create_event(request(),knowledge_context_limit=32768)
        needed=ContextTokenEstimator().estimate_text(baseline.model_dump_json())+350
        capacity=needed+1024
        while (missing:=needed-ContextBudgetPolicy.cognition(capacity,1024).input_hard_cap)>0:capacity+=missing
        model=CognitiveModelRequestFactory.create_event(request(),relationship_context=social,knowledge_context_limit=capacity)
        visible=projected(model)
        self.assertTrue(visible['truncated'])
        self.assertNotIn('current_state',visible)
        self.assertEqual(social.model_dump_json(),original)
        self.assertNotIn('旧理解0',model.messages[-1].content)
        self.assertNotIn('新信号0',model.messages[-1].content)

    def test_only_new_episode_is_evidence_and_old_quotes_are_not_replayed(self):
        self.f.commit(self.f.candidate('请叫我旧称呼。'),self.f.response('交流时使用约定称呼。',quote='请叫我旧称呼。'))
        candidate=self.f.candidate('你好。');provider=FakeProvider(['{"outcome":"NO_CHANGE","interpretation":null,"state_updates":[]}'])
        asyncio.run(RelationshipReflectionModel(provider,RecordingExecutor()).interpret(candidate,
            self.f.repo.read_current_pair_state(pair.NPC,pair.PLAYER)))
        visible=provider.requests[0].messages[-1].content
        self.assertNotIn('请叫我旧称呼。',visible)
        self.assertIn('交流时使用约定称呼。',visible)
        self.assertIn('NOT NEW EVIDENCE',visible)
