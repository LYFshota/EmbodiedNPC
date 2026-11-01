"""Retained speech + trusted terminal outcomes at the actual Provider HTTP boundary.

Fixtures/scenarios are promoted from UnifiedOutcomeFeasibility-20261101.
No live database, network, keys or model responses are used by these tests.
"""
import asyncio
from contextlib import closing
import json
import math
from pathlib import Path
import tempfile
import unittest
import httpx
from app.contracts import CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.context_budget_allocator import ContextBudgetExceeded
from app.context_token_estimator import ContextTokenEstimator
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.experience_ledger import ExperienceLedger
from app.experience_ingestion import ExperienceIngestionService
from app.experience_models import ExperienceAppendRequest
from app.local_model_scheduler import PrioritizedModelExecutor
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.recent_action_results import read_dialogue_linked_results
from app.sqlite_database import SQLiteDatabase
from tests.test_recent_dialogue_cognition import request


def proposal(request_id):
    return (request_id if request_id.startswith('proposal_') else 'proposal_' + request_id)[:120]

class Fixture:
    def __init__(self, name, *, npc='npc_cafe_01', player='player_01'):
        self.name=name; self.npc=npc; self.player=player; self.serial=0
        self.temp = tempfile.TemporaryDirectory()
        self.db=SQLiteDatabase(Path(self.temp.name)/'fixture.sqlite3')
        self.ledger=ExperienceLedger(self.db.connect);self.writer=ExperienceIngestionService(self.ledger)
        self.identities={npc:('npc','Character','merchant.private'),player:('player','Player','cafe-local-player-001')}
        self.session='session_one';self.conversation='conversation_one';self.time=100.0
    def append(self, kind, text=None, *, req=None, trace=None, source=None, at=None, session=None,
               conversation=None, structured=None, identities=None, observer=None, actor=None, target=None, world_revision=5):
        self.serial+=1; at=self.time if at is None else at; self.time=max(self.time,at)
        dialogue=kind.startswith('dialogue_'); source_type='dialogue' if dialogue else ('skill_result' if kind.startswith('skill_') else 'goal_outcome')
        player_input=kind=='dialogue_input'; observer=observer or self.npc
        people=identities or self.identities
        draft=dict(schema_version='experience-draft/1.1',observer_id=observer,event_type=kind,source_type=source_type,
            source_session_id=session or self.session,clock_domain='unity_realtime',source_event_id=source or 'source_'+str(self.serial),
            source_observation_ids=[],actor_entity_id=actor or (self.player if player_input else observer),
            target_entity_ids=target or ([observer] if player_input else [self.player]),
            persistent_participants=[dict(entity_id=k,identity_namespace=v[0],identity_type=v[1],persistent_id=v[2]) for k,v in people.items()],
            structured_payload=structured or (dict(direction='player_to_npc' if player_input else 'npc_to_player',model_content_is_not_world_truth=not player_input) if dialogue else {}),
            natural_language_payload=text,occurred_at=at,observed_at=at,world_revision=world_revision,
            request_id=req,trace_id=trace,conversation_id=(conversation or self.conversation) if dialogue else None,
            provenance='SYSTEM_CONFIRMED' if player_input else 'EXECUTED_BY_SELF')
        response=self.writer.append(ExperienceAppendRequest.model_validate(dict(request_id='append_'+str(self.serial),trace_id='appendtrace_'+str(self.serial),draft=draft)))
        assert response.status=='inserted',response
        with closing(self.db.connect()) as c:
            return self.ledger._row_to_event(c.execute('SELECT * FROM experience_events WHERE observer_id=? ORDER BY experience_seq DESC LIMIT 1',(observer,)).fetchone())
    def order(self, key='one', *, at=100, text='请按刚才选好的选项制作。', reply='好，稍等一下。'):
        req='order_'+key;trace='trace_'+key;source='input_'+key
        a=self.append('dialogue_input',text,req=req,trace=trace,source=source,at=at)
        b=self.append('dialogue_response',reply,req=req,trace=trace,source=source,at=at+1)
        return a,b
    def outcome(self, key='one', *, status='Succeeded', at=105, revision=0, skill=False, **kw):
        suffix={'Succeeded':'succeeded','Failed':'failed','Cancelled':'cancelled','TimedOut':'timed_out'}[status]
        payload=dict(goal_type='prepare_drink',status=status,failure_code='' if status=='Succeeded' else 'STOPPED',
            destination_entity_id=self.player,proposal_id=proposal('order_'+key),proposal_revision=revision,
            drink_type='flat_white',drink_receipt_id='receipt_'+key)
        if skill: payload=dict(skill_id='prepare_drink:'+proposal('order_'+key),skill_type='PrepareDrink',status=status,failure_code='')
        payload.update(kw.pop('payload',{}))
        return self.append(('skill_' if skill else 'goal_')+suffix,trace=kw.pop('trace','trace_'+key),at=at,structured=payload,**kw)
    def boundary(self, age=4740, *, at=None, runtime=None):
        when=105+age if at is None else at
        b=self.append('dialogue_input','之前那杯咖啡做好了吗？',req='request_current',trace='trace_current',source='event_01',at=when)
        req=request(b.natural_language_payload,agent_id=self.npc,conversation_id=self.conversation)
        raw=req.model_dump();raw['recall_identity']=req.recall_identity.model_dump()
        raw['context']['current_event'].update(occurred_at=when,world_revision=5,has_world_revision=True)
        raw['context']['runtime_state']=runtime or dict(source='unity_execution_snapshot',actor_entity_id=self.npc,
            player_entity_id=self.player,source_session_id=self.session,scene_instance_id='scene_one',captured_at=when,world_revision=5,status='UNKNOWN')
        self.req=CognitiveRequest.model_validate(raw);self.current=b
        return b
    def read(self, limit=16, max_chars=2400):
        b=self.current
        return self.ledger.read_recent_dialogue(self.npc,self.conversation,b.request_id,b.source_event_id,
            identities=self.identities,limit=limit,max_chars=max_chars,timeline=True,boundary_overlap=2)
    def native(self):
        b=self.current
        return self.ledger.read_recent_interactions(self.npc,self.conversation,b.request_id,b.source_event_id,self.identities)

def section(body, marker, default=None):
    user = body['input'][-1]['content']
    return json.JSONDecoder().raw_decode(user.split(marker, 1)[1])[0] if marker in user else default


class DialogueLinkedActionResultsTests(unittest.IsolatedAsyncioTestCase):
    def fixture(self):
        fixture = Fixture('linked')
        self.addCleanup(fixture.temp.cleanup)
        return fixture

    def provider(self, bodies, *, recall=False):
        def capture(req):
            bodies.append(json.loads(req.content))
            text = ('{"decision_kind":"MEMORY_RECALL","recall_plan":{"semantic_query":"订单结果"}}'
                    if recall and len(bodies) == 1 else
                    '{"decision_kind":"DIRECT_RESPONSE","utterance":"离线验证占位。"}')
            return httpx.Response(200, json={'status': 'completed', 'output': [{'type': 'message',
                'role': 'assistant', 'content': [{'type': 'output_text', 'text': text}]}]})
        return DeepSeekOfficialResponsesProvider(base_url='https://offline.invalid', api_key='offline-fixture',
            model='offline-fixture', transport=httpx.MockTransport(capture))

    async def wire(self, f, *, capacity=32768, linked=True, dispatch=True):
        history = f.read()
        if linked:
            b = f.current
            history.linked_outcomes = read_dialogue_linked_results(f.ledger, history, f.npc,
                f.conversation, b.request_id, b.source_event_id, f.identities)
        bodies = []
        provider = self.provider(bodies)
        model = CognitiveModelRequestFactory.create_event(f.req, recent_dialogue=history,
            recent_interactions=f.native(), knowledge_context_limit=capacity,
            inference_options=InferenceOptions(temperature=0, think=False, max_output_tokens=512),
            budget_payload_builder=provider.budget_payload)
        body = provider.budget_payload(model)
        if dispatch:
            await provider.generate(model)
            self.assertEqual(bodies, [body])
        speech = section(body, 'PRIOR DIALOGUE TURNS JSON: ', [])
        view = section(body, 'RECENT EXECUTED ACTION RESULTS JSON: ', {'results': []})
        retained = {m['seq'] for m in speech}
        for item in history.linked_outcomes:
            if retained.intersection(item['linked_dialogue_seqs']):
                self.assertIn(item['event_id'], [r['event_id'] for r in view['results']])
        for item in view['results']:
            if 'linked_dialogue_seqs' in item:
                self.assertTrue(set(item['linked_dialogue_seqs']).issubset(retained))
            self.assertTrue(item['historical_only'])
        estimated = ContextTokenEstimator().estimate_text(json.dumps(body, ensure_ascii=False, separators=(',', ':')))
        self.assertLessEqual(estimated + 512 + max(768, (capacity * 3 + 31) // 32), capacity)
        return body, speech, view['results'], history

    async def age_case(self, age):
        f = self.fixture(); f.order(); f.outcome(); f.boundary(age)
        a, old_speech, old_results, _ = await self.wire(f, linked=False)
        b, speech, results, history = await self.wire(f)
        self.assertEqual(len(old_results), 1 if age == 899 else 0)
        self.assertEqual(len(results), 1)
        self.assertTrue(results[0]['completion_confirmed'])
        self.assertEqual(old_speech, speech)
        self.assertEqual(len(history), 2)
        self.assertEqual(a['input'][0], b['input'][0])

    async def test_age_899(self): await self.age_case(899)
    async def test_age_901(self): await self.age_case(901)
    async def test_age_79_minutes(self): await self.age_case(4740)

    async def test_79_minutes_ten_retained_speech_messages(self):
        f = self.fixture()
        for i in range(4): f.order('chat_' + str(i), at=20 + i * 10)
        f.order(); f.outcome(); f.boundary()
        _, before, old_results, _ = await self.wire(f, linked=False)
        _, after, results, _ = await self.wire(f)
        self.assertEqual(len(before), 10); self.assertEqual(before, after)
        self.assertEqual(old_results, []); self.assertEqual(len(results), 1)

    async def test_same_drink_different_orders(self):
        f = self.fixture(); f.order('one'); f.outcome('one')
        f.order('two', at=120); f.outcome('two', at=125, status='Failed'); f.boundary()
        _, _, results, _ = await self.wire(f)
        by = {r['proposal_id']: r for r in results}
        self.assertTrue(by['proposal_order_one']['completion_confirmed'])
        self.assertFalse(by['proposal_order_two']['completion_confirmed'])
        self.assertNotEqual(by['proposal_order_one']['linked_input_seq'], by['proposal_order_two']['linked_input_seq'])

    async def test_five_results_do_not_consume_speech_slots(self):
        f = self.fixture()
        for i in range(5): f.order(str(i), at=100 + i * 10); f.outcome(str(i), at=105 + i * 10)
        f.boundary(); _, speech, results, _ = await self.wire(f)
        self.assertEqual(len(results), 5); self.assertEqual(len(speech), 10)

    async def test_order_beyond_generic_64_row_scan(self):
        f = self.fixture(); f.order(); f.outcome()
        for i in range(66): f.outcome('unrelated_' + str(i), at=110 + i)
        f.boundary(); _, _, results, _ = await self.wire(f)
        self.assertEqual([r['proposal_id'] for r in results], ['proposal_order_one'])

    async def status_case(self, status):
        f = self.fixture(); f.order(); f.outcome(status=status); f.boundary()
        _, _, results, _ = await self.wire(f)
        self.assertEqual(len(results), 1); self.assertFalse(results[0]['completion_confirmed'])
    async def test_failed(self): await self.status_case('Failed')
    async def test_cancelled(self): await self.status_case('Cancelled')
    async def test_timed_out(self): await self.status_case('TimedOut')

    async def test_skill_only_old(self):
        f = self.fixture(); f.order(); f.outcome(skill=True); f.boundary()
        _, _, results, _ = await self.wire(f); self.assertEqual(results, [])

    async def test_old_success_does_not_replace_new_in_progress(self):
        f = self.fixture(); f.order(); f.outcome(); f.order('new', at=4800)
        state = dict(source='unity_execution_snapshot', actor_entity_id=f.npc, player_entity_id=f.player,
            source_session_id=f.session, scene_instance_id='scene_one', captured_at=4845, world_revision=5,
            status='IN_PROGRESS', proposal_id='proposal_order_new', trace_id='trace_new', proposal_revision=0,
            action_id='prepare_drink', stage='action', step='PrepareDrink', drink_type='flat_white')
        f.boundary(at=4845, runtime=state); body, _, results, _ = await self.wire(f)
        context = section(body, 'Cognitive context JSON: ')
        self.assertEqual(context['runtime_state']['status'], 'IN_PROGRESS')
        self.assertEqual(context['runtime_state']['proposal_id'], 'proposal_order_new')
        self.assertEqual(results[0]['proposal_id'], 'proposal_order_one')

    async def isolation_case(self, mismatch):
        f = self.fixture(); f.order(); kw = {}
        if mismatch == 'npc': kw = dict(observer='npc_other', identities={'npc_other': ('npc', 'Character', 'other.private'), f.player: f.identities[f.player]})
        elif mismatch == 'player': kw = dict(target=['player_other'], identities={f.npc: f.identities[f.npc], 'player_other': ('player', 'Player', 'other.player')})
        elif mismatch == 'persistent': kw = dict(identities={**f.identities, f.player: ('player', 'Player', 'replaced.player')})
        elif mismatch == 'session': kw = dict(session='other_session')
        elif mismatch == 'trace': kw = dict(trace='other_trace')
        elif mismatch == 'proposal': kw = dict(payload={'proposal_id': 'proposal_other'})
        elif mismatch == 'receipt': kw = dict(payload={'drink_receipt_id': None})
        f.outcome(**kw); f.boundary(); _, _, results, _ = await self.wire(f)
        self.assertEqual(results, [])
    async def test_isolate_npc(self): await self.isolation_case('npc')
    async def test_isolate_player(self): await self.isolation_case('player')
    async def test_isolate_persistent_identity(self): await self.isolation_case('persistent')
    async def test_isolate_source_session(self): await self.isolation_case('session')
    async def test_isolate_trace(self): await self.isolation_case('trace')
    async def test_isolate_proposal(self): await self.isolation_case('proposal')
    async def test_missing_receipt(self): await self.isolation_case('receipt')

    async def test_outside_message_window(self):
        f = self.fixture(); f.order(); f.outcome()
        for i in range(12): f.order('chat_' + str(i), at=200 + i * 2, text='闲聊。', reply='嗯。')
        f.boundary(); _, _, results, history = await self.wire(f)
        self.assertEqual(results, []); self.assertLessEqual(len(history), 18)

    async def test_new_conversation_retained_order(self):
        f = self.fixture(); f.order(); f.outcome(); f.conversation = 'new_conversation'; f.boundary()
        _, _, results, _ = await self.wire(f); self.assertEqual(len(results), 1)

    async def test_low_token_budget_bundles_order_and_result_including_fallback(self):
        f = self.fixture(); f.order(); f.outcome(); f.order('chat', at=120, text='聊聊今天。', reply='今天过得怎样？'); f.boundary()
        found_retained = found_dropped = found_unrelated = False
        for capacity in range(13000, 17501, 64):
            try:
                _, speech, results, _ = await self.wire(f, capacity=capacity, dispatch=False)
            except ContextBudgetExceeded: continue
            order_kept = any(m['seq'] in (1, 2) for m in speech)
            self.assertEqual(order_kept, bool(results))
            found_retained |= order_kept
            found_dropped |= not order_kept
            found_unrelated |= not order_kept and any(m['seq'] in (4, 5) for m in speech)
        self.assertTrue(found_retained and found_dropped and found_unrelated)

    async def test_tiny_capacity_fails_safely(self):
        f = self.fixture(); f.order(); f.outcome(); f.boundary()
        with self.assertRaises(ContextBudgetExceeded): await self.wire(f, capacity=2048)

    async def test_recent_skill_is_not_goal_success(self):
        f = self.fixture(); f.order(); f.outcome(skill=True); f.boundary(899)
        _, _, results, _ = await self.wire(f)
        self.assertEqual(results[0]['result_level'], 'skill'); self.assertFalse(results[0]['completion_confirmed'])

    async def test_nonunique_trace_fails_closed(self):
        f = self.fixture(); f.order(); f.outcome()
        f.append('dialogue_input', '另一个请求。', req='other_order', trace='trace_one', source='other_event', at=110)
        f.boundary(); _, _, results, _ = await self.wire(f); self.assertEqual(results, [])

    async def test_result_committed_after_current_input_excluded(self):
        f = self.fixture(); f.order(); f.boundary(); f.outcome(at=105)
        _, _, results, _ = await self.wire(f); self.assertEqual(results, [])

    async def test_character_window_not_expanded(self):
        f = self.fixture(); f.order(text='旧' * 900, reply='旧' * 900); f.outcome()
        f.order('latest', at=120, text='新' * 900, reply='新' * 900); f.boundary()
        _, _, results, history = await self.wire(f)
        self.assertEqual(results, []); self.assertLessEqual(sum(len(m['text']) for m in history), 2400)

    async def test_latest_revision_cancellation_supersedes_old_success(self):
        f = self.fixture(); f.order(); f.outcome(); f.outcome(revision=1, status='Cancelled', at=110); f.boundary()
        _, _, results, _ = await self.wire(f)
        self.assertEqual(len(results), 1); self.assertEqual(results[0]['status'], 'CANCELLED')
        self.assertFalse(results[0]['completion_confirmed'])

    async def test_native_entry_step0_and_step1_use_same_linked_evidence(self):
        f = self.fixture(); f.order(); f.outcome(); f.boundary(); bodies = []
        await CognitiveBackendEntry(self.provider(bodies, recall=True), PrioritizedModelExecutor(),
            experience_ledger=f.ledger, context_length=32768,
            inference_options=InferenceOptions(temperature=0, think=False, max_output_tokens=512)).run(f.req)
        self.assertEqual(len(bodies), 2)
        views = [section(b, 'RECENT EXECUTED ACTION RESULTS JSON: ') for b in bodies]
        self.assertTrue(all(v['results'][0]['receipt_id'] == 'receipt_one' for v in views))

    async def test_same_revision_conflicts_do_not_claim_success(self):
        f = self.fixture(); f.order(); f.outcome(); f.outcome(status='Failed', at=110); f.boundary()
        _, _, results, _ = await self.wire(f)
        self.assertEqual(results[0]['status'], 'UNDETERMINED'); self.assertNotIn('receipt_id', results[0])

    async def test_new_conversation_without_order_does_not_retrieve_it(self):
        f = self.fixture(); f.order(); f.outcome(); f.conversation = 'new_conversation'
        for i in range(12): f.order('new_' + str(i), at=200 + i * 2)
        f.boundary(); _, _, results, _ = await self.wire(f); self.assertEqual(results, [])

    async def test_cross_source_session_not_bridged(self):
        f = self.fixture(); f.order(); f.outcome(); f.session = 'session_two'; f.conversation = 'visit_two'; f.boundary(at=20)
        _, _, results, history = await self.wire(f)
        self.assertEqual(len(history), 2); self.assertEqual(results, [])

    async def test_segmented_reply_remains_one_record_and_links_historical_outcome(self):
        # Unity persists one full reply with natural paragraph boundaries, not UI markers.
        for reply in ('好的，稍等一下。', '好的。\n我来制作。', '好的。\r\n我来制作。\r\n请稍等。'):
            with self.subTest(reply=reply):
                f = self.fixture()
                original = f.order(reply=reply)
                f.outcome(); f.boundary()
                def ledger_rows():
                    with closing(f.db.connect()) as connection:
                        return [tuple(row) for row in connection.execute(
                            'SELECT * FROM experience_events ORDER BY experience_seq')]
                before = ledger_rows()
                _, speech, results, history = await self.wire(f)
                self.assertEqual(len(history), 2)
                self.assertEqual(len(speech), 2)
                self.assertEqual([message['seq'] for message in speech],
                                 [event.experience_seq for event in original])
                self.assertEqual(speech[1]['text'], reply)
                self.assertNotIn('[[PAUSE]]', json.dumps(speech, ensure_ascii=False))
                self.assertEqual(len(results), 1)
                self.assertTrue(results[0]['completion_confirmed'])
                self.assertEqual(results[0]['receipt_id'], 'receipt_one')
                self.assertEqual(results[0]['linked_dialogue_seqs'],
                                 [event.experience_seq for event in original])
                self.assertEqual(before, ledger_rows())
