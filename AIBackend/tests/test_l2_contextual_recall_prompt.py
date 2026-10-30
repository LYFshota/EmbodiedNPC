"""L2 stays in the existing Step 0/1 prompt and bounded decision contracts."""
import hashlib
import json
import unittest

from app.cognitive_step_models import RecallOutcome, MemoryRecallPlan
from app.model_requests import CognitiveModelRequestFactory
from tests.test_memory_d2_cognitive_steps import cognitive_request
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


class ContextualRecallPromptTests(unittest.TestCase):
    def prompt(self, *, outcome=None, continuation=False):
        request = cognitive_request("今天想喝点咖啡。")
        if continuation:
            event = request.context.current_event.model_copy(update={"event_type": "npc.dialogue_continue"})
            request = request.model_copy(update={"context": request.context.model_copy(update={"current_event": event})})
        return CognitiveModelRequestFactory.create_event(request, cognition_phase=1 if outcome else 0, recall_outcome=outcome,
                                                         knowledge_context_limit=DEFAULT_COGNITIVE_CONTEXT_LENGTH)

    def test_step0_allows_optional_topic_benefit_without_extra_step(self):
        request = self.prompt()
        policy = request.messages[0].content
        self.assertIn("contextual history when it materially helps", policy)
        self.assertIn("Use sufficient recent speech or typed interaction evidence directly", policy)
        self.assertIn("actions normally need no recall", policy)
        self.assertIn("At most one recall and two model steps", policy)
        self.assertIn("MemoryRecallStep", request.response_schema["$defs"])

    def test_step0_keeps_recent_dialogue_and_unrelated_topic_restraint(self):
        policy = self.prompt().messages[0].content
        for rule in ("farewells, refusals and complete answers normally WAIT_FOR_PLAYER",
                     "Current event, capability validation and Unity outcome remain authoritative",
                     "Use sufficient recent speech or typed interaction evidence directly",
                     "Greetings, general knowledge and executable actions normally need no recall",
                     "Do not repeat unrelated history"):
            self.assertIn(rule, policy)

    def test_contextual_plan_preserves_identity_and_open_fact_contract(self):
        policy = self.prompt().messages[0].content
        for rule in ("at most 512 chars", "Use catalog refs, never names/IDs/UIDs",
                     "Null searches only this observer's history", "SEMANTIC_FACT supplies claims",
                     "EPISODIC experiences", "optional subject_ref and memory_type"):
            self.assertIn(rule, policy)
        plan = self.prompt().response_schema["$defs"]["MemoryRecallPlan"]
        self.assertEqual(set(plan["properties"]), {"semantic_query", "subject_ref", "memory_type"})
        self.assertFalse(plan["additionalProperties"])
        self.assertEqual(plan["required"], ["semantic_query"])
        self.assertEqual(plan["properties"]["semantic_query"]["maxLength"], 512)
        with self.assertRaises(ValueError):
            MemoryRecallPlan(semantic_query="旧偏好", attribute_key="favorite_color")

    def test_step0_selects_stated_facts_by_answer_target_including_npc_history(self):
        policy = self.prompt().messages[0].content
        for rule in ("Select memory_type by the evidence needed for the answer",
                     "not time words or Player/NPC identity",
                     "preserving negation, conditions, changes and corrections even from past conversations",
                     "For old NPC self-accounts, recall the NPC subject with concrete anchors: "
                     "SEMANTIC_FACT for stated facts or preferences, EPISODIC for concrete experiences"):
            self.assertIn(rule, policy)
        self.assertNotIn("recall the NPC subject with EPISODIC", policy)
        self.assertIn("NPC_SELF_STATEMENT is not direct Player evidence", policy)

    def test_step0_retains_action_location_insight_and_mixed_type_boundaries(self):
        request = self.prompt()
        policy = request.messages[0].content
        for rule in ("EPISODIC experiences cover concrete events, actions, interactions and outcomes",
                     "including where an object was placed; a location answer can require an action experience",
                     "DERIVED_INSIGHT supplies tentative patterns across experiences, "
                     "not direct facts or a substitute for stated preferences",
                     "Omit memory_type only when the answer needs multiple evidence types"):
            self.assertIn(rule, policy)
        memory_type = request.response_schema["$defs"]["MemoryRecallPlan"]["properties"]["memory_type"]
        self.assertEqual(memory_type["anyOf"], [
            {"enum": ["EPISODIC", "SEMANTIC_FACT", "DERIVED_INSIGHT"], "type": "string"},
            {"type": "null"},
        ])
        self.assertIsNone(memory_type["default"])

    def test_step1_natural_use_is_optional_and_current_wishes_win(self):
        request = self.prompt(outcome=RecallOutcome(status="OK"))
        policy = request.messages[0].content
        for rule in ("Use relevant OK evidence for the requested subject and exact fact category",
                     "naturally and without needless repetition, intimacy claims",
                     "Current explicit Player wishes take precedence",
                     "CONFLICTED remains unresolved even outside top-k",
                     "The CURRENT EVENT is the current input to respond to",
                     "required decision_kind field"):
            self.assertIn(rule, policy)
        self.assertNotIn("MemoryRecallStep", request.response_schema["$defs"])

    def test_step1_retains_source_action_and_no_invented_history_guards(self):
        policy = self.prompt(outcome=RecallOutcome(status="NO_MEMORY_FOUND")).messages[0].content
        for rule in ("not current world state", "not action authorization", "not_current_world_state does not invalidate reported preferences",
                     "NPC_SELF_STATEMENT proves only what the NPC said",
                     "do not invent old details", "NO_MEMORY_FOUND",
                     "A historical action supplies neither current capability nor authorization"):
            self.assertIn(rule, policy)

    def test_continuation_has_no_contextual_recall_instructions_or_schema(self):
        request = self.prompt(continuation=True)
        policy = request.messages[0].content
        self.assertNotIn("materially useful", policy)
        self.assertNotIn("For contextual recall", policy)
        self.assertIn("MEMORY_RECALL and ACTION_PROPOSAL are forbidden", policy)
        self.assertNotIn("MemoryRecallStep", request.response_schema["$defs"])

    def test_decision_format_matches_action_schema_in_both_steps(self):
        for outcome in (None, RecallOutcome(status="OK")):
            request = self.prompt(outcome=outcome)
            for rule in ("no explanation outside JSON", "omit other variants' fields even when null",
                         "Do not add action, terminal or terminal_decision", "not a list",
                         "utterance belongs at the decision root"):
                self.assertIn(rule, request.messages[0].content)
            definitions = request.response_schema["$defs"]
            proposal = definitions["CognitiveActionProposal"]["properties"]
            self.assertEqual("object", proposal["bindings"]["type"])
            self.assertNotIn("utterance", proposal)
            self.assertIn("utterance", definitions["ActionProposalStep"]["properties"])

    def test_prior_self_account_does_not_use_new_background_permission(self):
        for outcome in (None, RecallOutcome(status="OK")):
            policy = self.prompt(outcome=outcome).messages[0].content
            for rule in ("creative self-background permission does not apply",
                         "supported by retrieved evidence or supplied recent history",
                         "unsupported times, places, actions or people", "verified world events",
                         "turn them into shared Player experiences",
                         "may naturally develop plausible own personal background",
                         "An empty self Recall does not forbid a new plausible personal detail"):
                self.assertIn(rule, policy)

    def test_world_fact_guard_preserves_general_drink_conversation(self):
        for outcome in (None, RecallOutcome(status="NO_MEMORY_FOUND")):
            policy = self.prompt(outcome=outcome).messages[0].content
            for rule in ("Cafe identity and general coffee knowledge do not establish",
                         "menu, prices, stock or equipment status", "authoritative current context",
                         "recipe knowledge does not grant an action capability",
                         "General drink and taste discussion remains allowed",
                         "Without menu evidence, ask preferences or express uncertainty"):
                self.assertIn(rule, policy)

    def test_continuation_retains_terminal_only_contract(self):
        request=self.prompt(continuation=True)
        self.assertEqual(request.response_schema['title'],'CognitiveDecision')
        self.assertIn('MEMORY_RECALL and ACTION_PROPOSAL are forbidden',request.messages[0].content)
        self.assertIn('This run cannot trigger a further autonomous turn',request.messages[0].content)
        self.assertNotIn('MemoryRecallPlan',request.response_schema.get('$defs',{}))
        self.assertIn('DERIVED_INSIGHT is a tentative model synthesis',request.messages[0].content)


if __name__ == "__main__":
    unittest.main()
