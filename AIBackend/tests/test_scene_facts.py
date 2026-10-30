from __future__ import annotations
import copy
import json
import unittest
from pydantic import ValidationError
from app.contracts import CognitiveRequest
from app.model_requests import CognitiveModelRequestFactory
from app.context_token_estimator import ContextTokenEstimator
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.providers import InferenceOptions
from tests.test_memory_d2_cognitive_steps import cognitive_request
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH

def request_dict():
    original = cognitive_request("可以坐你对面吗？")
    data = original.model_dump()
    data['recall_identity'] = original.recall_identity.model_dump()
    data['context']['perception']['entities'] = [
        {'entity_id':'seat_b','kind':'environment','display_name':'对面座位','aliases':[],
         'semantic_tags':['seat'],'visibility':'current_visible','capabilities':[]},
        {'entity_id':'npc_b','kind':'npc','display_name':'温予安','aliases':[],
         'semantic_tags':[],'visibility':'current_visible','capabilities':[]},
    ]
    data['context']['scene_facts'] = {'self':{'posture':'seated','seat_entity_id':'seat_a'},
        'observed_seats':[{'seat_entity_id':'seat_b','relation_to_self':'opposite',
                           'occupancy':'occupied','occupant_entity_id':'npc_b'}]}
    return data

class SceneFactsTests(unittest.TestCase):
    def test_old_request_and_unity_empty_optional_objects_are_compatible(self):
        old = cognitive_request("你好")
        self.assertIsNone(old.context.scene_facts)
        data = old.model_dump()
        data['context']['scene_facts'] = {'self':{'posture':'','seat_entity_id':''},'observed_seats':[]}
        parsed = CognitiveRequest.model_validate(data)
        self.assertEqual('unknown', parsed.context.scene_facts.self.posture)

    def test_occupied_seat_reaches_real_factory_without_private_identity(self):
        request = CognitiveRequest.model_validate(request_dict())
        prompt = '\n'.join(m.content for m in CognitiveModelRequestFactory.create_event(request, knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH).messages)
        self.assertIn('"occupancy":"occupied"', prompt)
        self.assertIn('"occupant_entity_id":"npc_b"', prompt)
        self.assertIn('Its established identity and background relationships between characters are valid setting.', prompt)
        self.assertNotIn('persistent_id', prompt)

    def test_empty_and_unknown_do_not_disclose_an_occupant(self):
        for occupancy in ('empty','unknown'):
            data = request_dict()
            seat = data['context']['scene_facts']['observed_seats'][0]
            seat.update(occupancy=occupancy, occupant_entity_id=None)
            self.assertEqual(occupancy, CognitiveRequest.model_validate(data).context.scene_facts.observed_seats[0].occupancy)
            seat['occupant_entity_id'] = 'npc_b'
            with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)

    def test_unseen_or_remembered_seat_or_occupant_is_rejected(self):
        for index in (0,1):
            for visibility in ('remembered','lost'):
                data=request_dict();data['context']['perception']['entities'][index]['visibility']=visibility
                with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)

    def test_bounded_unique_seats_and_self_relation(self):
        data=request_dict();row=data['context']['scene_facts']['observed_seats'][0]
        for invalid in ([row,row],[{**row,'seat_entity_id':f'seat_{i}'} for i in range(5)]):
            other=copy.deepcopy(data);other['context']['scene_facts']['observed_seats']=invalid
            with self.assertRaises(ValidationError): CognitiveRequest.model_validate(other)
        row['relation_to_self']='self'
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)
        row['relation_to_self']='opposite';data['context']['scene_facts']['self']=None
        with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)

    def test_four_visible_seats_are_accepted_but_five_are_rejected(self):
        data = request_dict()
        data['context']['scene_facts']['self'] = None
        seats = data['context']['scene_facts']['observed_seats'] = []
        for index in range(5):
            seat_id = f'seat_{index}'
            data['context']['perception']['entities'].append({
                'entity_id': seat_id, 'kind': 'environment', 'display_name': seat_id,
                'aliases': [], 'semantic_tags': ['seat'], 'visibility': 'current_visible', 'capabilities': []})
            seats.append({'seat_entity_id': seat_id, 'relation_to_self': 'unknown',
                          'occupancy': 'empty', 'occupant_entity_id': None})
            if index == 3:
                self.assertEqual(4, len(CognitiveRequest.model_validate(data).context.scene_facts.observed_seats))
            if index == 4:
                with self.assertRaises(ValidationError): CognitiveRequest.model_validate(data)

    def test_final_wording_review_preserves_facts_and_old_dialogue(self):
        data = request_dict()
        data['context']['perception']['entities'][0]['display_name'] = '座位'
        request = CognitiveRequest.model_validate(data)
        snapshot = request.model_dump_json()
        old_reply = '现在我看到桌子2的D座是空的，你可以坐那儿。'
        history = [{'input_seq': 1, 'player_text': '有空位吗', 'npc_text': old_reply,
                    'evidence_status': 'paired'}]
        original_history = copy.deepcopy(history)
        model_request = CognitiveModelRequestFactory.create_event(
            request, recent_dialogue=history, knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH)
        decoded = CognitiveModelRequestFactory.context_from(model_request)
        self.assertEqual(request.context.scene_facts, decoded.scene_facts)
        self.assertEqual('座位', decoded.perception.entities[0].display_name)
        self.assertIn(old_reply, model_request.messages[-1].content)
        self.assertEqual(original_history, history)
        self.assertEqual(snapshot, request.model_dump_json())
        system = model_request.messages[0].content
        user = model_request.messages[-1].content
        self.assertEqual(0, system.count('表达检查：'))
        self.assertEqual(1, user.count('表达检查：'))
        self.assertGreater(user.index('表达检查：'), user.index('CURRENT TASK BINDING JSON:'))
        self.assertGreater(user.index('表达检查：'), user.index(old_reply))

class SeatAvailabilityProjectionTests(unittest.TestCase):
    old_reply = '现在我看到桌子2的D座是空的，你可以坐那儿。'

    def build(self, data, *, history=None, **kwargs):
        request = CognitiveRequest.model_validate(data)
        before = request.model_dump_json()
        history = history if history is not None else [
            {'input_seq': 1, 'player_text': '有空位吗', 'npc_text': self.old_reply,
             'evidence_status': 'paired'}]
        original_history = copy.deepcopy(history)
        model_request = CognitiveModelRequestFactory.create_event(
            request, recent_dialogue=history, knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH, **kwargs)
        self.assertEqual(request.context, CognitiveModelRequestFactory.context_from(model_request))
        self.assertEqual(before, request.model_dump_json())
        self.assertEqual(original_history, history)
        return model_request

    def availability(self, model_request):
        marker = 'CURRENT SEAT AVAILABILITY JSON: '
        lines = [line[len(marker):] for line in model_request.messages[-1].content.splitlines()
                 if line.startswith(marker)]
        self.assertEqual(1, len(lines))
        return json.loads(lines[0])

    def test_current_state_partitions_replace_old_invitation_each_request(self):
        # Sequential requests for the same ID must not reuse an earlier empty state.
        for state, bucket in [('empty', 'confirmed_empty_seat_ids'),
                              ('occupied', 'occupied_seat_ids'), ('unknown', 'unknown_seat_ids')]:
            with self.subTest(state=state):
                data = request_dict()
                data['context']['scene_facts']['observed_seats'][0].update(
                    occupancy=state, occupant_entity_id=None)
                model_request = self.build(data)
                self.assertEqual({
                    'scope': 'current_observer_observed_seats',
                    'confirmed_empty_seat_ids': ['seat_b'] if bucket == 'confirmed_empty_seat_ids' else [],
                    'occupied_seat_ids': ['seat_b'] if bucket == 'occupied_seat_ids' else [],
                    'unknown_seat_ids': ['seat_b'] if bucket == 'unknown_seat_ids' else [],
                }, self.availability(model_request))
                self.assertIn(self.old_reply, model_request.messages[-1].content)

    def test_visible_or_remembered_seat_without_current_fact_is_not_filled_from_history(self):
        for visibility in ('current_visible', 'remembered', 'lost'):
            with self.subTest(visibility=visibility):
                data = request_dict()
                data['context']['perception']['entities'][0]['visibility'] = visibility
                data['context']['scene_facts']['observed_seats'] = []
                projection = self.availability(self.build(data))
                self.assertEqual([], projection['confirmed_empty_seat_ids'])
                self.assertEqual([], projection['occupied_seat_ids'])
                self.assertEqual([], projection['unknown_seat_ids'])

    def test_mixed_seats_only_recommend_current_empty_and_keep_unknown_separate(self):
        data = request_dict()
        data['context']['scene_facts']['self'] = None
        data['context']['scene_facts']['observed_seats'][0]['relation_to_self'] = 'unknown'
        for seat_id, state in [('seat_empty', 'empty'), ('seat_unknown', 'unknown')]:
            data['context']['perception']['entities'].append({
                'entity_id': seat_id, 'kind': 'environment', 'display_name': '座位',
                'aliases': [], 'semantic_tags': ['seat'], 'visibility': 'current_visible', 'capabilities': []})
            data['context']['scene_facts']['observed_seats'].append({
                'seat_entity_id': seat_id, 'relation_to_self': 'unknown', 'occupancy': state})
        self.assertEqual({
            'scope': 'current_observer_observed_seats', 'confirmed_empty_seat_ids': ['seat_empty'],
            'occupied_seat_ids': ['seat_b'], 'unknown_seat_ids': ['seat_unknown'],
        }, self.availability(self.build(data)))

    def test_projection_never_creates_spatial_relations_or_changes_location_question(self):
        data = request_dict()
        for item in data['context']['current_event']['payload']:
            if item['key'] == 'text': item['value'] = '那张椅子在哪里？'
        data['context']['scene_facts']['observed_seats'][0].update(occupancy='empty', occupant_entity_id=None)
        explicit = self.build(data)
        data['context']['scene_facts']['self'] = None
        data['context']['scene_facts']['observed_seats'][0]['relation_to_self'] = 'unknown'
        unknown = self.build(data)
        # Availability alone says nothing about geometry; original facts retain it.
        self.assertEqual(self.availability(explicit), self.availability(unknown))
        for model_request, relation in [(explicit, 'opposite'), (unknown, 'unknown')]:
            self.assertEqual(relation, CognitiveModelRequestFactory.context_from(
                model_request).scene_facts.observed_seats[0].relation_to_self)
            self.assertIn('那张椅子在哪里？', model_request.messages[-1].content)

    def test_no_scene_facts_supplies_no_availability_evidence(self):
        data = request_dict()
        data['context']['scene_facts'] = None
        self.assertNotIn('CURRENT SEAT AVAILABILITY JSON: ', self.build(data).messages[-1].content)

    def test_full_provider_wire_keeps_projection_when_optional_history_is_trimmed(self):
        provider = DeepSeekOfficialResponsesProvider(
            base_url='https://api.deepseek.com', api_key='offline-placeholder', model='deepseek-flash')
        history = [
            {'input_seq': 1, 'player_text': '旧聊天', 'npc_text': '很长的旧聊天。' * 10000},
            {'input_seq': 2, 'player_text': '有空位吗', 'npc_text': self.old_reply},
        ]
        with self.assertLogs('embodiednpc.backend.context_budget', level='INFO') as logs:
            model_request = self.build(request_dict(), history=history,
                inference_options=InferenceOptions(max_output_tokens=512),
                budget_payload_builder=provider.budget_payload)
        budget = json.loads(next(record.message.split('context_budget=', 1)[1]
                                 for record in logs.records if record.message.startswith('context_budget=')))
        self.assertIn('recent:0', budget['dropped_components'])
        self.assertGreater(budget['component_costs']['current_seat_availability'], 0)
        self.assertFalse(budget['safe_failure'])
        wire = provider.budget_payload(model_request)  # Pure body builder; no HTTP call.
        self.assertEqual([message.model_dump() for message in model_request.messages], wire['input'])
        self.assertEqual(model_request.response_schema, wire['text']['format']['schema'])
        wire_cost = ContextTokenEstimator().estimate_text(json.dumps(wire, ensure_ascii=False, separators=(',', ':')))
        self.assertEqual(budget['estimated_total'], wire_cost)
        self.assertLessEqual(wire_cost, budget['input_hard_cap'])
        self.assertIn(self.old_reply, wire['input'][-1]['content'])
        self.assertEqual(['seat_b'], self.availability(model_request)['occupied_seat_ids'])
        self.assertEqual([], self.availability(model_request)['confirmed_empty_seat_ids'])


if __name__ == '__main__': unittest.main()
