from dataclasses import replace
import unittest

from app.relationship_reflection_candidate import RelationshipReflectionCandidateBuilder
from tests import test_memory_c2a as fixtures


class ContinuationMemoryTests(unittest.TestCase):
    def test_autonomous_speech_cannot_support_player_profile_or_world_event_memory(self):
        fixture = fixtures.MemoryC2ATests()
        fixture.setUp()
        try:
            _, context = fixture.work()
            event = context.evidence_events[0].model_copy(update=dict(event_type="dialogue_response",
                            actor_entity_id=context.observer_entity_id,
                            target_entity_ids=("player_01",), provenance="EXECUTED_BY_SELF",
                            structured_payload={"initiative": "dialogue_continuation"}))
            context = replace(context, evidence_events=(event,))
            for episodic in (True, False):
                response = fixture.response(context, episodic=episodic,
                                            summary="The NPC delivered a cup to the player.")
                batch = fixture.admission.evaluate(context, response)
                self.assertEqual((), batch.memories)
            self.assertEqual((), RelationshipReflectionCandidateBuilder().build(context))
        finally: fixture.doCleanups()
