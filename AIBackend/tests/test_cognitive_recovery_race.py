"""Deterministic recovery ordering tests: real WS handler, private SQLite, no paid provider."""
import asyncio
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from tests.app_test_isolation import get_test_app
from tests.test_cognitive_tasks import request
from tests.test_cognitive_websocket import BlockingProvider, SocketHarness
from app.cognitive_entry import CognitiveBackendEntry
from app.cognitive_tasks import CognitiveTasks
from app.local_model_scheduler import PrioritizedModelExecutor
from app.sqlite_database import SQLiteDatabase
from app.task_repository import TaskRepository
from app.task_service import TaskService

class CognitiveRecoveryRaceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.main = get_test_app()
        self.root = tempfile.TemporaryDirectory()
        self.addCleanup(self.root.cleanup)
        database = SQLiteDatabase(Path(self.root.name) / "private.sqlite3")
        self.tasks = CognitiveTasks(TaskService(TaskRepository(database.connect)), expected_player_uid="synthetic-task-player")
        self.provider = BlockingProvider()
        self.provider.output = '{"decision_kind":"DIRECT_RESPONSE","utterance":"Hello.","future_commitment":false}'
        entry = CognitiveBackendEntry(self.provider, PrioritizedModelExecutor(), tasks=self.tasks,
                                      context_length=self.provider.context_length)
        for name, value in (("cognitive_entry", entry), ("task_cognition", self.tasks), ("demo_player_uid", "synthetic-task-player")):
            patched = patch.object(self.main, name, value); patched.start(); self.addCleanup(patched.stop)
        self.connections = []
        req = request(language="en-US")
        self.payload = {**req.model_dump(mode="json"), "recall_identity":req.recall_identity.model_dump(mode="json"),
                        "task_runtime":req.task_runtime.model_dump(mode="json")}

    async def asyncTearDown(self):
        for socket, task in self.connections:
            if not task.done(): socket.disconnect()
        await asyncio.wait_for(asyncio.gather(*(t for _,t in self.connections)), 3)

    def connect(self):
        socket = SocketHarness()
        socket.socket.scope.update(client=("127.0.0.1",12345), server=("127.0.0.1",18947))
        task = asyncio.create_task(self.main.websocket_endpoint(socket.socket))
        self.connections.append((socket,task)); return socket,task

    def query(self, socket, payload=None):
        socket.message(dict(message_type="task_control",schema_version="1.1",operation="query",
                            original_request=payload or self.payload))

    async def test_normal_reply_once(self):
        socket,_=self.connect(); socket.message(self.payload)
        await asyncio.wait_for(self.provider.started.wait(),2); self.provider.release.set()
        self.assertEqual((await socket.result())["decision"]["utterance"],"Hello.")
        self.assertEqual(self.provider.calls,1); self.assertEqual(len(socket.sent),1)

    async def test_query_during_five_second_inference_keeps_original_reply(self):
        socket,_=self.connect(); socket.message(self.payload)
        await asyncio.wait_for(self.provider.started.wait(),2)
        self.query(socket); socket.message(dict(message_type="ping",request_id="barrier"))
        # A pong proves the query was handled, without waiting for model completion.
        self.assertEqual((await socket.result())["message_type"],"pong")
        await asyncio.sleep(5)
        self.provider.release.set()
        result=await socket.result()
        self.assertEqual(result["message_type"],"cognitive_response")
        self.assertEqual(result["request_id"],self.payload["request_id"])
        self.assertEqual(self.provider.calls,1)
        self.assertEqual([r["message_type"] for r in socket.sent],["pong","cognitive_response"])

    async def test_query_before_input_commit_is_not_missing_command(self):
        self.payload["context"]["current_event"]["payload"].append(dict(key="conversation_id",value="conversation"))
        with patch.object(self.main,"COGNITIVE_INPUT_COMMIT_TIMEOUT_SECONDS",.1):
            socket,_=self.connect();socket.message(self.payload);self.query(socket)
            socket.message(dict(message_type="ping",request_id="barrier"))
            self.assertEqual((await socket.result())["message_type"],"pong")
            self.assertEqual((await socket.result())["reason"],"COGNITIVE_INPUT_NOT_PERSISTED")
        self.assertEqual(self.provider.calls,0)

    async def test_missing_command_is_terminal_without_inference(self):
        socket,_=self.connect();self.query(socket)
        result=await socket.result();self.assertEqual(result["reason"],"COGNITIVE_TASK_COMMAND_NOT_FOUND")
        self.assertEqual(result["run_id"],self.payload["run"]["run_id"]);self.assertEqual(self.provider.calls,0)

    async def test_active_run_with_changed_command_is_rejected(self):
        import copy
        socket,_=self.connect();socket.message(self.payload)
        await asyncio.wait_for(self.provider.started.wait(),2)
        changed=copy.deepcopy(self.payload);changed["task_runtime"]["command_id"]="different_command"
        self.query(socket,changed)
        self.assertEqual((await socket.result())["reason"],"COGNITIVE_TASK_RECOVERY_FAILED")
        self.provider.release.set();self.assertEqual((await socket.result())["message_type"],"cognitive_response")
        self.assertEqual(self.provider.calls,1)

    async def test_disconnect_then_query_is_finite_missing_not_new_inference(self):
        first,run=self.connect();first.message(self.payload)
        await asyncio.wait_for(self.provider.started.wait(),2);first.disconnect();await asyncio.wait_for(run,2)
        self.assertTrue(self.provider.cancelled.is_set());self.assertEqual(first.sent,[])
        second,_=self.connect();self.query(second)
        self.assertEqual((await second.result())["reason"],"COGNITIVE_TASK_COMMAND_NOT_FOUND")
        self.assertEqual(self.provider.calls,1)

if __name__=="__main__":unittest.main()
