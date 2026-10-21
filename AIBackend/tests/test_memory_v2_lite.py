"""Memory Lite source regressions derived from the P2.1 real-model failures."""
from tests.test_dsn_singleplayer import DsnFixture, OWNER, NPC_SELF_STATEMENT_LABEL


class MemoryLiteAdmissionTests(DsnFixture):
    def test_unbound_skill_cannot_support_joint_subject_but_goal_only_can(self):
        question = self.append('dialogue_input', '请把杯子送给我。')
        pickup = self.append('skill_succeeded', payload={'skill_type': 'PickUp'},
                             targets=('cup_01',), participants=False)
        goal = self.append('goal_succeeded', '交付成功。', payload={
            'goal_type': 'bring_object', 'status': 'succeeded', 'object_id': 'cup_01'})
        job, context = self.close([question, pickup, goal])
        original = self.rows('SELECT * FROM experience_events ORDER BY experience_seq')
        rejected = self.admission.evaluate(context, self.mixed(context, [pickup, goal], '交付杯子成功。'))
        self.assertEqual((), rejected.memories)
        self.assertEqual('SUBJECT_OUTSIDE_CITED_EVIDENCE', rejected.rejections[0].reason)
        accepted = self.finalize(job, context, self.mixed(context, [goal], '交付杯子成功。'))
        self.assertEqual(2, len(accepted.memories[0].subjects))
        self.assertEqual([goal.event_id], [r['experience_event_id'] for r in
            self.rows('SELECT * FROM memory_evidence')])
        self.assertEqual(original, self.rows('SELECT * FROM experience_events ORDER BY experience_seq'))

    def mixed(self, context, events, summary):
        proposal = self.proposal(context, events, summary=summary)
        proposal['episodic_memories'][0]['subject_handles'] = list(context.identity_by_handle)
        return proposal

    def test_player_question_does_not_certify_joint_personal_history(self):
        question = self.append('dialogue_input', '讲一件你以前做点心的小事。')
        speech = self.append('dialogue_response',
            '去年冬天，我在家试着烤了一盘橙皮饼干，第一盘有点焦，第二盘才把火候调好。')
        job, context = self.close([question, speech])
        original = self.rows('SELECT * FROM experience_events ORDER BY experience_seq')
        batch = self.finalize(job, context, self.mixed(context, [question, speech],
            '去年冬天应对方请求讲述在家烤橙皮饼干的经历。'))
        self.assertEqual((), batch.memories)
        self.assertEqual('NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD', batch.rejections[0].reason)
        self.assertEqual([], self.rows('SELECT * FROM memory_records'))
        self.assertEqual(original, self.rows('SELECT * FROM experience_events ORDER BY experience_seq'))

    def test_ordinary_response_cannot_assign_personal_story_to_player(self):
        job, context, speech = self.speech_episode()
        # Both the Player input and the NPC response are legal evidence, but their
        # presence alone cannot certify the Player as the subject of the story.
        question = self.ledger.get_by_event_id(context.evidence_events[0].event_id)
        batch = self.finalize(job, context, self.proposal(context, [question, speech], self_subject=False))
        self.assertEqual((), batch.memories)
        self.assertEqual('NPC_SPEECH_CANNOT_CERTIFY_OTHER_SUBJECT_OR_WORLD', batch.rejections[0].reason)

    def test_own_subject_and_speech_preserve_source_for_both_dialogue_paths(self):
        for auto in (False, True):
            job, context, speech = self.speech_episode(auto=auto)
            batch = self.finalize(job, context, self.proposal(context, [speech]))
            self.assertEqual((OWNER,), batch.memories[0].subjects)
        self.assertTrue(all(item.provenance_label == NPC_SELF_STATEMENT_LABEL for item in self.read().items))

    def test_real_action_evidence_keeps_joint_interaction_path(self):
        request = self.append('dialogue_input', '请把杯子送给我。')
        speech = self.append('dialogue_response', '好，我来拿。')
        outcome = self.append('goal_succeeded', '杯子已交付。', payload={
            'goal_type': 'bring_object', 'status': 'succeeded', 'object_id': 'cup_01'})
        acknowledgement = self.append('dialogue_input', '收到杯子了，谢谢。')
        events = [request, speech, outcome, acknowledgement]
        job, context = self.close(events)
        batch = self.finalize(job, context, self.mixed(context, events,
            '应对方请求递送杯子，交付成功，对方确认收到。'))
        self.assertEqual(1, len(batch.memories))
        self.assertEqual(2, len(batch.memories[0].subjects))
        self.assertEqual('MIXED', self.read(query='杯子').items[0].provenance_label)

    def test_player_direct_preference_is_not_blocked_by_npc_answer(self):
        player = self.append('dialogue_input', '我喜欢卡布奇诺。')
        speech = self.append('dialogue_response', '我喜欢推理小说。')
        job, context = self.close([player, speech])
        proposal = self.proposal(context, [player], self_subject=False, profile=True)
        proposal['semantic_facts'][0]['fact'] = '玩家喜欢卡布奇诺。'
        batch = self.finalize(job, context, proposal)
        self.assertEqual('玩家喜欢卡布奇诺。', batch.memories[0].summary)
        self.assertEqual('Player', batch.memories[0].subjects[0].identity_type)
