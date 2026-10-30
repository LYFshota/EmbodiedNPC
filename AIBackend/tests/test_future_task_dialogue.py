"""Future timing is model context; only validated TaskService commits establish obligations."""
import unittest
from tests import test_cognitive_tasks as task_fixture
request, ACCEPT, task_data = task_fixture.request, task_fixture.ACCEPT, task_fixture.task_data
from tests.test_recent_dialogue_cognition import RecordingLedger
from app.cognitive_tasks import CognitiveTasks
from app.task_service import TaskService
from app.task_repository import TaskRepository
from app.sqlite_database import SQLiteDatabase


class FutureTaskDialogueTests(unittest.IsolatedAsyncioTestCase):
    setUp = task_fixture.CognitionTaskTests.setUp
    entry = task_fixture.CognitionTaskTests.entry

    def with_text(self, req, text):
        from app.contracts import CognitiveRequest
        value = req.model_dump(mode="json")
        value["recall_identity"] = req.recall_identity.model_dump(mode="json")
        value["task_runtime"] = req.task_runtime.model_dump(mode="json")
        value["context"]["current_event"]["payload"] = [dict(key="text", value=text), dict(key="conversation_id", value="future_dialogue")]
        return CognitiveRequest.model_validate(value)
    async def test_future_followup_receives_prior_timing_and_does_not_execute(self):
        history = RecordingLedger([dict(seq=1, role="player", text="下次来做杯卡布奇诺"),
                                   dict(seq=2, role="npc", text="等你下次来。", reply_to_seq=1)])
        req = request(number="clarify")
        req = self.with_text(req, "多糖一点")
        def clarification(model):
            prompt = model.messages[-1].content
            policy = model.messages[0].content
            self.assertIn("下次来做杯卡布奇诺", prompt)
            self.assertIn("多糖一点", prompt)
            self.assertIn("omission of a time word is NOT a new immediate order", policy)
            self.assertIn("not task state", policy)
            self.assertEqual(task_data(model)["tasks"], [])
            return dict(decision_kind="DIRECT_RESPONSE", utterance="好，你喜欢甜一些。我们说的还是下次来时那杯。",
                        future_commitment=False)
        result = await self.entry([clarification], experience_ledger=history).run(req)
        self.assertIsNone(result.task_receipt)
        self.assertEqual(result.decision.decision_kind, "DIRECT_RESPONSE")
        self.assertEqual(len(self.provider.requests), 1)
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(req).scope).tasks, ())

    async def test_create_followup_edit_reopen_cancel_preserves_task_identity(self):
        first = await self.entry([ACCEPT]).run(request())
        def edit(model):
            task = task_data(model)["tasks"][0]
            return dict(decision_kind="DIRECT_RESPONSE", utterance="下次那杯换成卡布奇诺。", future_commitment=True,
                        task_operation=dict(op="update_task", task_ref=task["task_ref"], expected_version=task["version"],
                                            change="edit", goal=dict(kind="serve_drink", drink_type="cappuccino"), reason="玩家补充"))
        second = await self.entry([edit]).run(request(number="edit_future"))
        self.assertEqual(second.task_receipt.task_id, first.task_receipt.task_id)
        self.assertEqual(second.task_receipt.task_version, 2)
        self.tasks = CognitiveTasks(TaskService(TaskRepository(SQLiteDatabase(self.path).connect)),
                                   expected_player_uid="synthetic-task-player")
        req = request(number="new_session")
        req = req.model_copy(update={"task_runtime": req.task_runtime.model_copy(update={"client_session_id":"session_2", "visit_id":"session_2"})})
        def cancel(model):
            task = task_data(model)["tasks"][0]
            self.assertEqual(task["goal"]["drink_type"], "cappuccino")
            self.assertEqual(task["trigger_kind"], "next_visit")
            return dict(decision_kind="DIRECT_RESPONSE", utterance="好，取消下次那杯。", future_commitment=False,
                        task_operation=dict(op="update_task", task_ref=task["task_ref"], expected_version=task["version"],
                                            change="cancel", reason="玩家取消"))
        last = await self.entry([cancel]).run(req)
        self.assertEqual(last.task_receipt.task_id, first.task_receipt.task_id)
        self.assertEqual(last.task_receipt.task_status, "cancelled")
        self.assertEqual(last.task_receipt.task_version, 3)

    async def test_existing_future_task_does_not_block_new_immediate_action(self):
        first = await self.entry([ACCEPT]).run(request())
        req = request(number="now")
        req = self.with_text(req, "现在再做杯拿铁")
        immediate = dict(decision_kind="ACTION_PROPOSAL", utterance="好，我现在做拿铁。",
                         action_proposal=dict(action_id="prepare_drink", bindings=dict(drink_type="latte")))
        result = await self.entry([immediate]).run(req)
        self.assertEqual(result.decision.decision_kind, "ACTION_PROPOSAL")
        saved = self.service.get_task(self.tasks.prepare(req).scope, first.task_receipt.task_id)
        self.assertEqual(saved.version, 1)
        self.assertEqual(saved.status, "waiting")

    async def test_rich_preference_preserves_core_goal_and_task_version(self):
        req = self.with_text(request(), "下次见面帮我做杯少糖卡布奇诺吧。")
        accepted = dict(decision_kind="DIRECT_RESPONSE", utterance="好呀，下次来给你做卡布奇诺。你喜欢少糖，我听到了。",
                        future_commitment=True, task_operation=dict(op="create_task", source="requester",
                        goal=dict(kind="serve_drink", drink_type="cappuccino"), trigger_kind="next_visit"))
        first = await self.entry([accepted]).run(req)
        followup = self.with_text(request(number="detail"), "奶泡我也喜欢绵密一点。")
        def acknowledge(model):
            self.assertIn("奶泡我也喜欢绵密一点", model.messages[-1].content)
            task = task_data(model)["tasks"][0]
            self.assertEqual(task["goal"], dict(kind="serve_drink", drink_type="cappuccino"))
            return dict(decision_kind="DIRECT_RESPONSE", utterance="你喜欢绵密的奶泡，配卡布奇诺确实很合适。",
                        future_commitment=False)
        result = await self.entry([acknowledge]).run(followup)
        self.assertIsNone(result.task_receipt)
        saved = self.service.get_task(self.tasks.prepare(followup).scope, first.task_receipt.task_id)
        self.assertEqual(saved.version, 1)
        self.assertEqual(saved.goal.model_dump(), dict(kind="serve_drink", drink_type="cappuccino"))

    async def test_social_plans_preferences_and_capability_questions_need_no_fake_task(self):
        cases = (
            ("下次我们聊聊《小王子》吧。", "好呀，下次聊聊《小王子》，我很好奇你喜欢哪一段。"),
            ("以后叫我小洛吧，我喜欢这个称呼。", "好呀，小洛，这样叫你很亲切。"),
            ("游戏真的能做少糖咖啡、以后也记住吗？", "我可以和你聊口味；实际制作和以后能否记住，要以真实结果为准。"),
        )
        for index, (text, utterance) in enumerate(cases):
            with self.subTest(text=text):
                req = self.with_text(request(number=f"conversation_{index}"), text)
                result = await self.entry([dict(decision_kind="DIRECT_RESPONSE", utterance=utterance,
                                                future_commitment=False)]).run(req)
                self.assertIsNone(result.task_receipt)
                self.assertEqual(result.decision.utterance, utterance)
                self.assertEqual(self.service.query_tasks(self.tasks.prepare(req).scope).tasks, ())

    async def test_rich_dialogue_does_not_extend_task_schema_or_allow_unsaved_work(self):
        from copy import deepcopy
        from pydantic import ValidationError
        for goal in (dict(kind="serve_drink", drink_type="latte", sugar_level="low"),
                     dict(kind="discuss_book", title="小王子")):
            response = deepcopy(ACCEPT)
            response["task_operation"]["goal"] = goal
            with self.subTest(goal=goal), self.assertRaises(ValidationError):
                await self.entry([response]).run(request())
        with self.assertRaises(ValidationError):
            await self.entry([dict(decision_kind="DIRECT_RESPONSE", utterance="下次帮你做拿铁。",
                                   future_commitment=True)]).run(request())
        self.assertEqual(self.service.query_tasks(self.tasks.prepare(request()).scope).tasks, ())
