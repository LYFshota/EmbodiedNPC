"""P1 request semantics and unchanged evidence/authority contracts, not model quality scores."""
import json
import unittest
from app.cognitive_step_models import MemoryRecallPlan, RecallOutcome, CognitiveStepResult
from app.model_requests import CognitiveModelRequestFactory
from app.providers import InferenceOptions
from app.canonical_memory_retrieval import _measured
from app.canonical_recall_models import CanonicalEventRef
from tests import test_memory_d2_cognitive_steps as fixtures
from tests.test_memory_d2_cognitive_steps import cognitive_request, recall, TERMINAL, OWNER


class DialogueP1Tests(unittest.IsolatedAsyncioTestCase):
    def fixture(self, scripts):
        fixture=fixtures.MemoryD2Tests()
        return fixture, fixture.entry(scripts)

    async def test_subject_schema_survives_actual_factory_without_new_wire_fields(self):
        fixture,entry=self.fixture([recall,TERMINAL])
        await entry.run(cognitive_request())
        schema=fixture.provider.requests[0].response_schema['$defs']['MemoryRecallPlan']
        self.assertEqual(set(schema['properties']),{'semantic_query','subject_ref','memory_type'})
        self.assertIn('requester_ref',schema['properties']['subject_ref']['description'])
        self.assertEqual(fixture.rows.calls[0]['subject'].identity_type,'Player')

    async def test_npc_self_query_remains_authorized_without_forcing_requester(self):
        def self_query(request):
            content=request.messages[-1].content
            catalog=json.JSONDecoder().raw_decode(content.split('Recall subject catalog JSON: ')[1])[0]
            owner=next(s['subject_ref'] for s in catalog['subjects'] if s['source']=='observer')
            return dict(decision_kind='MEMORY_RECALL',recall_plan=dict(semantic_query='自己的旧口味',subject_ref=owner))
        fixture,entry=self.fixture([self_query,TERMINAL])
        await entry.run(cognitive_request('你自己以前喜欢什么？'))
        self.assertEqual(fixture.rows.calls[0]['subject'],OWNER)

    async def test_unknown_ref_still_fails_closed_without_read_or_retry(self):
        fixture,entry=self.fixture([lambda r:recall(r,'not_in_catalog'),TERMINAL])
        await entry.run(cognitive_request())
        self.assertEqual(fixture.rows.calls,[])
        self.assertEqual(len(fixture.provider.requests),2)
        content=fixture.provider.requests[-1].messages[-1].content
        outcome=json.JSONDecoder().raw_decode(content.split('HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ')[1])[0]
        self.assertEqual(outcome['status'],'UNKNOWN_SUBJECT')

    async def test_active_old_and_restored_fact_preserve_order_sources_and_conditions(self):
        fixture,entry=self.fixture([recall,TERMINAL])
        req=cognitive_request('最新暂时改回的偏好是什么？')
        catalog=entry._memory_recall.prepare(req)
        outcome=entry._memory_recall.recall(catalog,MemoryRecallPlan(semantic_query='最新变化',subject_ref=catalog.requester_ref))
        old=outcome.evidence.items[0].model_copy(update={'summary':'早前喜欢茶，最近暂时喝水。'})
        new=old.model_copy(update={'memory_id':'new','summary':'现在暂时恢复喝茶，不是永久讨厌水。',
            'source_excerpt':'早前喝水。','raw_evidence_refs':(CanonicalEventRef(event_id='later',experience_seq=9),)})
        # Deliberately put the old state first: assembly may not silently pick a winner.
        evidence=_measured(outcome.evidence.model_copy(update={'items':(old,new)}))
        model=CognitiveModelRequestFactory.create_event(req,subject_catalog=catalog.model_visible_payload(),
            cognition_phase=1, recall_outcome=outcome.model_copy(update={'evidence':evidence}),knowledge_context_limit=16384)
        content=model.messages[-1].content
        actual=RecallOutcome.model_validate(json.JSONDecoder().raw_decode(content.split('HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: ')[1])[0])
        self.assertEqual(actual.evidence.items,(old,new))
        self.assertIn('Retrieval rank and learned_at are not event chronology',model.messages[0].content)

    async def test_no_memory_preserves_current_input_and_own_prior_mistake_verbatim(self):
        fixture,entry=self.fixture([recall,TERMINAL]);fixture.rows.empty=True
        req=cognitive_request('我现在只接受无咖啡因，不是少放浓缩。')
        catalog=entry._memory_recall.prepare(req)
        recent=[dict(turn_index=1,input_seq=1,player_text='不能替我确认库存。',
                     npc_text='稍等我问清楚再回你。',evidence_status='paired')]
        model=CognitiveModelRequestFactory.create_event(req,subject_catalog=catalog.model_visible_payload(),
            cognition_phase=1, recall_outcome=RecallOutcome(status='NO_MEMORY_FOUND'),recent_dialogue=recent,
            inference_options=InferenceOptions(max_output_tokens=512),knowledge_context_limit=16384)
        content=model.messages[-1].content
        self.assertEqual(CognitiveModelRequestFactory.context_from(model).current_event,req.context.current_event)
        self.assertEqual(json.JSONDecoder().raw_decode(content.split('PRIOR DIALOGUE TURNS JSON: ')[1])[0],recent)
        self.assertIn('Missing evidence proves neither',model.messages[0].content)
        self.assertIn('replace it with corrected content or advice',model.messages[0].content)
        focus=json.JSONDecoder().raw_decode(content.split('CURRENT TASK BINDING JSON: ')[1])[0]
        self.assertEqual(focus['current_player_text'],req.context.current_event.payload[0].value)
        self.assertEqual(focus['source_entity_id'],req.context.current_event.source_entity_id)
        self.assertEqual(focus['available_action_ids'],[a.action_id for a in req.context.capabilities.available_actions])
        self.assertGreater(content.index('CURRENT TASK BINDING JSON: '),content.index('HISTORICAL MEMORY EVIDENCE / RecallOutcome JSON: '))


if __name__=='__main__': unittest.main()
