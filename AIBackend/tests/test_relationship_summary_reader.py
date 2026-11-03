"""Whole current-state projection from authenticated, durable pair snapshots."""
from dataclasses import replace
import unittest
from unittest.mock import patch

from app.relationship_summary_reader import RelationshipSummaryPolicy, RelationshipSummaryReader
from tests import test_relationship_pair_state as pair

NPC_A=pair.NPC
NPC_B=replace(NPC_A,persistent_id='npc-b')
PLAYER_A=pair.PLAYER
PLAYER_B=replace(PLAYER_A,persistent_id='player-b')


class RelationshipSummaryReaderTests(unittest.TestCase):
    def setUp(self):
        self.fixture=pair.RelationshipPairStateTests();self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.reflections=self.fixture.repo
        self.reader=RelationshipSummaryReader(self.reflections)

    def add(self,text,*,observer=NPC_A,subject=PLAYER_A):
        quote=text or '今天随便聊聊。'
        candidate=self.fixture.candidate(quote,npc=observer,player=subject)
        return self.fixture.commit(candidate,self.fixture.response(text,quote=quote))

    def test_empty_pair_does_not_invent_relationship(self):
        result=self.reader.get(NPC_A,PLAYER_A)
        self.assertEqual(result.selected_count,0)
        self.assertIsNone(result.current_state)

    def test_all_four_items_are_projected_without_a_relevance_window(self):
        texts=('喜欢直接讨论不同意见。','家庭话题不接受玩笑。','疲惫时希望留出安静。','可以轻松开日常玩笑。')
        for text in texts:self.add(text)
        result=self.reader.get_for_cognition(NPC_A,PLAYER_A)
        self.assertEqual(tuple(i.text for i in result.current_state.items),texts)
        self.assertFalse(result.truncated)
        self.assertNotIn('interpretations',result.model_dump())
        self.assertNotIn('sources',result.model_dump())
        self.assertEqual(self.reader.get_for_cognition(NPC_A,PLAYER_B).selected_count,0)

    def test_correction_never_resurrects_a_predecessor_or_audit_interpretation(self):
        old=self.add('可以轻松开玩笑。')
        candidate=self.fixture.candidate('现在不要开玩笑。')
        self.fixture.commit(candidate,self.fixture.response('现在交流时避免玩笑。',
            operation='CORRECT',target='s0',quote='现在不要开玩笑。'))
        wire=self.reader.get_for_cognition(NPC_A,PLAYER_A).model_dump_json()
        self.assertIn('现在交流时避免玩笑。',wire)
        self.assertNotIn(old.items[0].text,wire)
        self.assertNotIn('A local social impression.',wire)

    def test_standalone_budget_drops_whole_snapshot_and_cognition_defers_to_allocator(self):
        self.add('家庭话题不接受玩笑。');self.add('疲惫时希望留出安静。')
        reader=RelationshipSummaryReader(self.reflections,RelationshipSummaryPolicy(max_tokens=1))
        limited=reader.get(NPC_A,PLAYER_A)
        self.assertTrue(limited.truncated)
        self.assertIsNone(limited.current_state)
        full=reader.get_for_cognition(NPC_A,PLAYER_A)
        self.assertEqual(len(full.current_state.items),2)

    def test_missing_source_is_not_replaced_by_an_old_impression(self):
        self.add('家庭话题不接受玩笑。')
        with patch.object(self.reflections,'_valid_source',return_value=False):
            result=self.reader.get_for_cognition(NPC_A,PLAYER_A)
        self.assertEqual(result.current_state.items,())
        self.assertFalse(result.current_state.has_direct_interaction)
        self.assertEqual(result.current_state.coverage,'SOURCE_UNAVAILABLE')

    def test_reader_requires_trusted_identities(self):
        with self.assertRaises(TypeError):self.reader.get('npc',PLAYER_A)

    def test_standalone_budget_validation(self):
        for changes in ({'max_tokens':0},{'max_chars':0},{'max_tokens':5001}):
            with self.subTest(changes=changes),self.assertRaises(ValueError):RelationshipSummaryPolicy(**changes)
