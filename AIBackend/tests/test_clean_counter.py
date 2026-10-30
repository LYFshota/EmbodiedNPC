"""Single counter contracts and scripted provider path, with network/storage prohibited."""
import unittest
from unittest.mock import patch
from pydantic import ValidationError
from app.action_catalog import CLEAN_COUNTER_REGISTRATION
from app.action_proposal_validator import ActionProposalValidator, ActionProposalValidationError
from app.contracts import CognitiveContextSnapshot, CognitiveDecision, CognitiveRequest, GoalProposal
from app.cognitive_entry import CognitiveBackendEntry
from app.local_model_scheduler import PrioritizedModelExecutor
from tests.test_prepare_drink import context as drink_context, request as drink_request, ScriptedDrinkProvider
from tests.cognitive_test_defaults import DEFAULT_COGNITIVE_CONTEXT_LENGTH


def context():
    value = drink_context().model_dump()
    d = CLEAN_COUNTER_REGISTRATION.descriptor
    value["capabilities"]["available_actions"] = [dict(action_id=d.action_id, description=d.description,
        bindings=[dict(name=b.name, kind=b.kind, required=b.required) for b in d.binding_schema])]
    value["counter_work"] = dict(target_entity_id="counter_cafe_clean_01", needs_cleaning=True, condition_revision=1)
    return CognitiveContextSnapshot.model_validate(value)


def decision():
    return dict(decision_kind="ACTION_PROPOSAL", action_proposal=dict(action_id="clean_counter",
        bindings=dict(target="counter_cafe_clean_01")))


def request():
    value = drink_request().model_dump()
    value["context"] = context().model_dump()
    return CognitiveRequest.model_validate(value)


class CleanCounterTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        for target in ("socket.socket.connect", "sqlite3.connect"):
            guard = patch(target, side_effect=AssertionError("counter tests must be offline/storage-free"))
            guard.start(); self.addCleanup(guard.stop)

    async def test_player_proposal_shared_manifest_validator_and_provider(self):
        provider = ScriptedDrinkProvider(decision())
        result = await CognitiveBackendEntry(provider, PrioritizedModelExecutor(), context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH).run(request())
        self.assertEqual(GoalProposal(type="clean_counter", target_entity_id="counter_cafe_clean_01"), result.validated_goal)
        self.assertEqual(1, len(provider.requests))

    def test_wrong_missing_extra_target_clean_state_customer_and_manifest_rejected(self):
        for bindings in ({}, dict(target="cup_01"), dict(target="counter_cafe_clean_01", destination="player_01")):
            value = decision(); value["action_proposal"]["bindings"] = bindings
            with self.subTest(bindings=bindings), self.assertRaises((ValidationError, ActionProposalValidationError)):
                ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(value), context())
        for mutation in ("clean", "missing", "customer", "manifest", "version"):
            value = context().model_dump()
            if mutation == "clean": value["counter_work"]["needs_cleaning"] = False
            if mutation == "missing": value["counter_work"] = None
            if mutation == "customer":
                value["agent_entity_id"] = value["trigger"]["agent_entity_id"] = value["perception"]["observer_entity_id"] = "npc_cafe_regular_01"
            if mutation == "manifest": value["capabilities"]["available_actions"] = []
            if mutation == "version": value["counter_work"]["condition_revision"] = 0
            with self.subTest(mutation=mutation), self.assertRaises(ActionProposalValidationError):
                ActionProposalValidator().validate_and_adapt(CognitiveDecision.model_validate(decision()), CognitiveContextSnapshot.model_validate(value))
        for args in ({}, dict(target_entity_id="counter_cafe_clean_01", destination_entity_id="player_01"),
                     dict(target_entity_id="counter_cafe_clean_01", drink_type="latte")):
            with self.assertRaises(ValidationError): GoalProposal(type="clean_counter", **args)


def system_request():
    from tests.test_daily_behavior_cognition import request as daily_request
    value = daily_request().model_dump()
    value["context"]["capabilities"] = context().capabilities.model_dump()
    counter = context().counter_work.model_dump()
    counter.update(condition_token="a"*32, event_id=value["context"]["current_event"]["event_id"], trace_id=value["trace_id"])
    value["context"]["counter_work"] = counter
    value["context"]["daily_behavior"].update(current_mode="work", candidates=[])
    value["context"]["relevant_player"] = None
    value["recall_identity"] = None
    event = value["context"]["current_event"]
    event.update(event_type="counter.needs_cleaning", source_entity_id="counter_cafe_clean_01", area_entity_id="area_cafe_counter",
        trace_id=value["trace_id"], source_observation_id=None, has_world_revision=True, world_revision=1,
        payload=[dict(key=k,value=v) for k,v in dict(target_entity_id="counter_cafe_clean_01",condition_token="a"*32,condition_revision="1").items()])
    return CognitiveRequest.model_validate(value)


class AutonomousCounterTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        for target in ("socket.socket.connect", "sqlite3.connect"):
            guard=patch(target, side_effect=AssertionError("autonomy tests must remain offline/storage-free"))
            guard.start(); self.addCleanup(guard.stop)

    async def test_system_step0_choices_and_response_roundtrip(self):
        from app.contracts import CognitiveResponse
        from app.world_action_eligibility import may_propose_new_world_action
        for kind in ("NO_ACTION", "AUTONOMOUS_ACTION_INTENT"):
            output=decision() if kind != "NO_ACTION" else {}
            output["decision_kind"]=kind
            provider=ScriptedDrinkProvider(output); current=system_request()
            self.assertFalse(may_propose_new_world_action(current))
            result=await CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH).run(current)
            response=CognitiveResponse(request_id=current.request_id,trace_id=current.trace_id,run_id=current.run.run_id,
                agent_entity_id=current.run.agent_entity_id,decision=result.decision,validated_goal=result.validated_goal)
            self.assertEqual(response,CognitiveResponse.model_validate_json(response.model_dump_json()))
            self.assertEqual(1,len(provider.requests))
            self.assertEqual("AutonomousWorkStep",provider.requests[0].response_schema["title"])
            self.assertEqual(kind,result.decision.decision_kind)
            self.assertEqual(kind != "NO_ACTION",result.validated_goal is not None)

    async def test_forged_conditions_and_forbidden_outputs_rejected(self):
        from app.world_action_eligibility import is_counter_cleaning_consideration
        for mutation in ("rest","customer","projection","token","trace","version","player"):
            value=system_request().model_dump()
            if mutation=="rest": value["context"]["daily_behavior"]["current_mode"]="rest"
            if mutation=="customer":
                value["run"]["agent_entity_id"]=value["trigger"]["agent_entity_id"]=value["context"]["agent_entity_id"]=value["context"]["trigger"]["agent_entity_id"]=value["context"]["perception"]["observer_entity_id"]="npc_cafe_regular_01"
            if mutation=="projection": value["context"]["counter_work"]=None
            if mutation=="token": value["context"]["counter_work"]["condition_token"]="b"*32
            if mutation=="trace": value["context"]["counter_work"]["trace_id"]="wrong"
            if mutation=="version": value["context"]["counter_work"]["condition_revision"]=2
            if mutation=="player": value["context"]["relevant_player"]=context().relevant_player.model_dump()
            current=CognitiveRequest.model_validate(value); provider=ScriptedDrinkProvider({"decision_kind":"NO_ACTION"})
            with self.subTest(mutation=mutation):
                self.assertFalse(is_counter_cleaning_consideration(current))
                with self.assertRaises(ValueError): await CognitiveBackendEntry(provider,PrioritizedModelExecutor()).run(current)
                self.assertEqual([],provider.requests)
        outputs=[dict(decision_kind="DIRECT_RESPONSE",utterance="我去擦"),decision(),
                 dict(decision_kind="DAILY_MODE_INTENT",target_daily_mode="work"),
                 dict(decision_kind="AUTONOMOUS_ACTION_INTENT",utterance="我去擦",action_proposal=decision()["action_proposal"])]
        for action,bindings in (("bring_object",dict(target="cup_01",destination="player_01")),("prepare_drink",dict(drink_type="latte")),("clean_counter",dict(target="wrong_counter"))):
            outputs.append(dict(decision_kind="AUTONOMOUS_ACTION_INTENT",action_proposal=dict(action_id=action,bindings=bindings)))
        for output in outputs:
            with self.subTest(output=output),self.assertRaises(ValueError):
                await CognitiveBackendEntry(ScriptedDrinkProvider(output),PrioritizedModelExecutor()).run(system_request())
        from tests.test_daily_behavior_cognition import service_zone_request
        with self.assertRaises(ValueError):
            await CognitiveBackendEntry(ScriptedDrinkProvider(dict(decision_kind="AUTONOMOUS_ACTION_INTENT",action_proposal=decision()["action_proposal"])),PrioritizedModelExecutor()).run(service_zone_request())

    async def test_real_websocket_handler_cancel_and_tombstone_without_application_startup(self):
        # Compile only the real endpoint and identity check from source. Importing m3_main
        # would initialize application services/storage, which is outside this test's scope.
        import ast
        import asyncio
        import __future__
        import json
        import logging
        import time
        from pathlib import Path
        from collections import OrderedDict
        from starlette.websockets import WebSocketDisconnect
        from app.contracts import CognitiveResponse, SERVICE_ZONE_EVENT, is_service_zone_consideration
        from app.experience_models import ExperienceAppendRequest
        from app.canonical_memory_read_repository import player_identity_allowed, mentions_player, event_player_scope_allowed
        from app.world_action_eligibility import WorldActionNotEligibleError
        from tests.test_cognitive_websocket import SocketHarness, BlockingProvider
        tree = ast.parse((Path(__file__).parents[1]/"app/m3_main.py").read_text(encoding="utf-8"))
        nodes = [n for n in tree.body if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef))
                 and n.name in {"websocket_endpoint","demo_identity_error"}]
        for node in nodes: node.decorator_list=[]
        provider=BlockingProvider()
        ns=dict(__name__="app.counter_endpoint_isolated",__package__="app",asyncio=asyncio,json=json,time=time,
            OrderedDict=OrderedDict,logger=logging.getLogger("counter-test"),WebSocketDisconnect=WebSocketDisconnect,
            CognitiveRequest=CognitiveRequest,CognitiveResponse=CognitiveResponse,ValidationError=ValidationError,
            ExperienceAppendRequest=ExperienceAppendRequest,
            player_identity_allowed=player_identity_allowed,mentions_player=mentions_player,event_player_scope_allowed=event_player_scope_allowed,
            demo_player_uid="counter-test",SERVICE_ZONE_EVENT=SERVICE_ZONE_EVENT,is_service_zone_consideration=is_service_zone_consideration,
            ActionProposalValidationError=ActionProposalValidationError,WorldActionNotEligibleError=WorldActionNotEligibleError,
            MAX_CONNECTION_COGNITIONS=32,COGNITIVE_INPUT_COMMIT_TIMEOUT_SECONDS=20,MAX_EARLY_COMMITTED_INPUTS=128,
            cognitive_entry=CognitiveBackendEntry(provider,PrioritizedModelExecutor(),context_length=DEFAULT_COGNITIVE_CONTEXT_LENGTH))
        exec(compile(ast.Module(body=nodes,type_ignores=[]),"m3_main.counter_endpoint","exec",flags=__future__.annotations.compiler_flag),ns)
        socket=SocketHarness();task=asyncio.create_task(ns["websocket_endpoint"](socket.socket))
        value=system_request().model_dump();value["context"]["perception"]["entities"]=[]
        cancel=dict(message_type="cognitive_cancel",schema_version="1.0",request_id=value["request_id"],trace_id=value["trace_id"],
            run_id=value["run"]["run_id"],agent_entity_id=value["run"]["agent_entity_id"])
        try:
            socket.message(value);await asyncio.wait_for(provider.started.wait(),2)
            socket.message(cancel);await asyncio.wait_for(provider.cancelled.wait(),2)
            socket.message(value);socket.message(dict(message_type="ping",request_id="after-cancel"))
            self.assertEqual("pong",(await asyncio.wait_for(socket.result(),2))["message_type"])
            self.assertEqual(1,provider.calls)
            self.assertTrue(all(r["message_type"]=="pong" for r in socket.sent))
        finally:
            socket.disconnect();await asyncio.wait_for(task,2)
