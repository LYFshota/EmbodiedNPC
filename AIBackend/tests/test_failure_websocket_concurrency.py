"""Outcome feedback shares existing cognition dispatch, concurrency and retirement."""
import asyncio
import json
import unittest
from tests.test_cognitive_websocket import CognitiveWebSocketTests as _Base
from tests.test_action_feedback import make_request, reply

class UnifiedFeedbackWebSocketTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp=_Base.asyncSetUp
    asyncTearDown=_Base.asyncTearDown
    connect=_Base.connect
    assert_correlation=_Base.assert_correlation
    def payload(self):
        req=make_request(); data=json.loads(req.model_dump_json())
        data['action_feedback']=req.action_feedback.model_dump();data['recall_identity']=req.recall_identity.model_dump()
        return data

    async def test_feedback_duplicates_and_ping_use_single_cognitive_call(self):
        socket,_=self.connect();request=self.payload();self.provider.output=json.dumps(reply())
        socket.message(request);await asyncio.wait_for(self.provider.started.wait(),2)
        socket.message(request);socket.message({'message_type':'ping','request_id':'ping_feedback'})
        self.assertEqual((await socket.result())['message_type'],'pong')
        self.assertEqual(self.provider.calls,1);self.provider.release.set()
        result=await socket.result();self.assertEqual(result['message_type'],'cognitive_response')
        self.assert_correlation(result,request)
        socket.message(request);socket.message({'message_type':'ping','request_id':'after_feedback'})
        self.assertEqual((await socket.result())['message_type'],'pong');self.assertEqual(self.provider.calls,1)

    async def test_feedback_exact_cancel_and_disconnect_cancel_existing_worker(self):
        socket,_=self.connect();request=self.payload();socket.message(request)
        await asyncio.wait_for(self.provider.started.wait(),2)
        socket.message(dict(message_type='cognitive_cancel',schema_version='1.0',request_id=request['request_id'],
            trace_id=request['trace_id'],run_id=request['run']['run_id'],agent_entity_id=request['run']['agent_entity_id']))
        await asyncio.wait_for(self.provider.cancelled.wait(),2)
        socket.message({'message_type':'ping','request_id':'cancelled_feedback'})
        self.assertEqual((await socket.result())['message_type'],'pong')
        self.assertFalse(any(v['message_type']=='cognitive_response' for v in socket.sent))

    async def test_retired_failure_report_is_unsupported_without_model_call(self):
        socket,_=self.connect();socket.message({'message_type':'task_failure_report','request_id':'retired_failure'})
        self.assertEqual((await socket.result())['reason'],'UNSUPPORTED_MESSAGE_TYPE');self.assertEqual(self.provider.calls,0)

del _Base
