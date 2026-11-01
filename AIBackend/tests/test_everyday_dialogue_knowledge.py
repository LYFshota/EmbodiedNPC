"""Private everyday background through native cognition and the Provider HTTP envelope.

No live models, user catalogs, player databases or embedding services are used.
"""
import json
import tempfile
import unittest
from pathlib import Path

import httpx
from app.contracts import CognitiveRequest
from app.cognitive_entry import CognitiveBackendEntry
from app.context_token_estimator import ContextTokenEstimator
from app.context_budget_allocator import ContextBudgetExceeded
from app.model_requests import CognitiveModelRequestFactory
from app.deepseek_provider import DeepSeekOfficialResponsesProvider
from app.local_model_scheduler import PrioritizedModelExecutor
from app.providers import InferenceOptions
from app.recent_dialogue_context import RecentDialogueTimeline
from app.world_knowledge import KnowledgeDeployment, KnowledgeQuery, retrieve_knowledge
from app.world_knowledge_store import WorldKnowledgeResolver, WorldKnowledgeStore, read_document
from tests.test_recent_dialogue_cognition import RecordingLedger
from tests.test_world_knowledge_content import TEMPLATE, DEPLOYMENT, bound_request

TOPICS = {
    "cafe.merchant": (("你会追剧吗，喜欢什么悬疑剧？", "merchant.mystery_drama"),
        ("和朋友玩桌游猜身份有什么趣事？", "merchant.boardgame_bluff"),
        ("我坐公交睡过站，你有这样的事吗？", "merchant.bus_nap")),
    "cafe.regular_customer": (("学习之外会玩什么节奏游戏？", "regular.rhythm_game"),
        ("你和同桌借橡皮时有什么学校趣事？", "regular.classroom_eraser"),
        ("你听歌时唱错过副歌吗？", "regular.school_song")),
    "cafe.companion_customer": (("工作之外会看什么喜剧电影？", "companion.comedy_film"),
        ("有拿错雨伞的尴尬小事吗？", "companion.wrong_umbrella"),
        ("和朋友玩桌游时赢过吗？", "companion.friend_boardgame")),
    "cafe.quiet_customer": (("阅读之外会玩什么合作游戏？", "quiet.cooperative_game"),
        ("约朋友看电影时出过什么岔子？", "quiet.ticket_date"),
        ("有什么音乐或吉他的歌喜欢循环听？", "quiet.music_loop")),
}


def knowledge(body):
    return json.loads(next(line.split(": ", 1)[1] for line in body["input"][-1]["content"].splitlines()
        if line.startswith("Knowledge context JSON: ")))


def timeline(player, npc):
    return RecentDialogueTimeline([dict(seq=1, role="player", text=player),
        dict(seq=2, role="npc", text=npc)], current_session_id="current",
        sources={1:{"source_session_id":"current"}, 2:{"source_session_id":"current"}})


class EverydayDialogueKnowledgeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = WorldKnowledgeStore(Path(self.tmp.name)/"knowledge.json", builtin_path=TEMPLATE)
        self.store.initialize()
        self.deployment = read_document(DEPLOYMENT, KnowledgeDeployment)
        self.resolver = WorldKnowledgeResolver(self.store, self.deployment)
        self.entries = {e.knowledge_id:e for e in self.store.snapshot()[2]}

    def request(self, role, text):
        request = bound_request(next(b for b in self.deployment.bindings if b.character_id==role), text)
        raw = request.model_dump(mode="json")
        raw["recall_identity"] = request.recall_identity.model_dump(mode="json")
        raw["context"]["current_event"]["payload"].append(dict(key="conversation_id",value="everyday_test"))
        return CognitiveRequest.model_validate(raw)

    async def wire(self, request, decisions, history=None, capacity=32768):
        bodies = []
        def capture(req):
            bodies.append(json.loads(req.content))
            decision = decisions[len(bodies)-1]
            return httpx.Response(200,json={"status":"completed","output":[{"type":"message",
                "role":"assistant","content":[{"type":"output_text","text":json.dumps(decision,ensure_ascii=False)}]}]})
        provider = DeepSeekOfficialResponsesProvider(base_url="https://offline.invalid",api_key="offline-fixture",
            model="offline-fixture",transport=httpx.MockTransport(capture))
        before = self.store.path.read_bytes()
        rows = history if history is not None else timeline("", "")[:0]
        ledger = RecordingLedger(rows)
        entry = CognitiveBackendEntry(provider,PrioritizedModelExecutor(),knowledge_resolver=self.resolver,
            experience_ledger=ledger,context_length=capacity,
            inference_options=InferenceOptions(temperature=0,think=False,max_output_tokens=512))
        result = await entry.run(request)
        self.assertEqual(self.store.path.read_bytes(), before)
        self.assertLessEqual(len(bodies), 2)
        for body in bodies:
            view=knowledge(body)
            self.assertLessEqual(len(view["items"]),3)
            self.assertTrue(view["not_personal_memory"] and view["not_action_authorization"]
                            and view["not_current_world_state"])
            for item in (*view["constant_items"], *view["items"]):
                self.assertEqual(item["content"],self.entries[item["knowledge_id"]].content)
            tokens=ContextTokenEstimator().estimate_text(json.dumps(body,ensure_ascii=False,separators=(",",":")))
            self.assertLessEqual(tokens+512+max(768,(capacity*3+31)//32),capacity)
        return result,bodies

    def test_new_background_ids_keep_independent_private_character_permissions(self):
        for role, cases in TOPICS.items():
            for _,key in cases:
                entry=self.entries[key]
                self.assertEqual(entry.attributes["timeline"],"pre_game_background")
                self.assertEqual(entry.access.character_ids,(role,))
                for binding in self.deployment.bindings:
                    principal=self.deployment.resolve(bound_request(binding,"背景"))
                    result=retrieve_knowledge(tuple(self.entries.values()),principal,KnowledgeQuery(knowledge_ids=(key,)),
                        bundle_version="test",revision=0,defer_budget=True)
                    self.assertEqual(bool(result.items),binding.character_id==role)

    def test_broad_chat_prefetch_covers_distinct_personal_topics_without_increasing_topk(self):
        questions=("你平时有什么兴趣爱好？","讲一件日常生活里的趣事吧。",
            "工作或学习之外，你会做什么消遣？","你有没有什么尴尬或失败的小事？","我最近喜欢听音乐，你呢？")
        for role in TOPICS:
            seen=set()
            for text in questions:
                request=self.request(role,text)
                result=self.resolver.prefetch(self.resolver.capture(request),request)
                ids={item["knowledge_id"] for item in result.items}
                self.assertTrue(ids,(role,text))
                self.assertLessEqual(len(ids),3)
                seen.update(key for key in ids if self.entries[key].knowledge_type=="personal_background")
            self.assertGreaterEqual(len(seen),3,role)

    async def test_four_personas_specific_life_topics_reach_actual_provider_requests(self):
        for role,cases in TOPICS.items():
            for text,key in cases:
                with self.subTest(role=role,topic=key):
                    result,bodies=await self.wire(self.request(role,text),[
                        dict(decision_kind="DIRECT_RESPONSE",utterance="那件小事我现在想起还会笑。")])
                    self.assertEqual(len(bodies),1)
                    self.assertIn(key,{item["knowledge_id"] for item in knowledge(bodies[0])["items"]})
                    self.assertEqual(result.decision.decision_kind,"DIRECT_RESPONSE")

    async def test_elliptical_followup_uses_current_topic_and_does_not_replay_other_session(self):
        for role,cases in TOPICS.items():
            text,key=cases[1]
            history=timeline(text,self.entries[key].content)
            history.insert(0,dict(seq=0,role="npc",text="上个来源会话的未公开密码。"))
            history.sources[0]={"source_session_id":"older"}
            request=self.request(role,"还有别的吗？")
            query=self.resolver.automatic_query(request,history)
            self.assertIn(text,query)
            self.assertIn("还有别的吗？",query)
            self.assertNotIn("未公开密码",query)
            original=json.dumps(history,ensure_ascii=False)
            _,bodies=await self.wire(request,[dict(decision_kind="DIRECT_RESPONSE",utterance="还有件不同的小事。")],history)
            prior=bodies[0]["input"][-1]["content"]
            self.assertIn("PRIOR DIALOGUE TURNS JSON:",prior)
            self.assertEqual(json.dumps(history,ensure_ascii=False),original)
            ids={item["knowledge_id"] for item in knowledge(bodies[0])["items"]}
            self.assertTrue(ids)
            self.assertTrue(ids-{key},(role,ids))

    async def test_native_knowledge_plan_supplements_a_followup_within_existing_two_steps(self):
        history=timeline("说说歌单。","我有份旧歌单。")
        request=self.request("cafe.merchant","还有别的吗，学校里的事也行。")
        result,bodies=await self.wire(request,[
            dict(decision_kind="CONTEXT_READ",knowledge_plan=dict(semantic_query="薇薇学生时代校园广播话筒没关打喷嚏的糗事")),
            dict(decision_kind="DIRECT_RESPONSE",utterance="上学时我代读校园广播，打完喷嚏还忘了关话筒。")],history)
        self.assertEqual(len(bodies),2)
        self.assertIn("merchant.school_broadcast",{item["knowledge_id"] for item in knowledge(bodies[1])["items"]})
        self.assertEqual(result.decision.decision_kind,"DIRECT_RESPONSE")
        self.assertNotIn('"knowledge_plan"',json.dumps(bodies[1]["text"]["format"]["schema"]))

    async def test_reply_can_end_without_question_and_next_player_turn_still_runs_normally(self):
        request=self.request("cafe.merchant","你平时会听什么音乐？")
        first,bodies=await self.wire(request,[dict(decision_kind="DIRECT_RESPONSE",utterance="我喜欢旋律轻快的歌，能跟着哼就很开心。")])
        self.assertNotIn("？",first.decision.utterance)
        history=timeline("你平时会听什么音乐？",first.decision.utterance)
        second,next_bodies=await self.wire(self.request("cafe.merchant","我也喜欢这种歌。"),[
            dict(decision_kind="DIRECT_RESPONSE",utterance="那种副歌一响就想跟着唱的感觉，挺快乐的。")],history)
        self.assertEqual(second.decision.decision_kind,"DIRECT_RESPONSE")
        self.assertEqual(len(bodies)+len(next_bodies),2)
        ordinary=next_bodies[0]["input"][-1]["content"]
        self.assertIn("自然结束当前回复不等于拒绝继续交流",ordinary)
        self.assertIn("不为延续聊天机械补上",ordinary)

    def test_low_capacity_omits_whole_background_without_truncating_or_expanding_budget(self):
        request=self.request("cafe.merchant","你平时有什么兴趣爱好？")
        context=self.resolver.prefetch(self.resolver.capture(request),request)
        self.assertEqual(len(context.items),3)
        provider=DeepSeekOfficialResponsesProvider(base_url="https://offline.invalid",api_key="offline-fixture",model="offline-fixture")
        saw_pressure=saw_full=False
        for capacity in (8192,12288,14336,16384,32768):
            try:
                model=CognitiveModelRequestFactory.create_event(request,knowledge_context=context,
                    knowledge_context_limit=capacity,inference_options=InferenceOptions(temperature=0,think=False,max_output_tokens=512),
                    budget_payload_builder=provider.budget_payload)
            except ContextBudgetExceeded:
                saw_pressure=True
                continue
            body=provider.budget_payload(model)
            view=knowledge(body)
            saw_pressure |= len(view["items"])<3
            saw_full |= len(view["items"])==3
            for item in view["items"]:
                self.assertEqual(item["content"],self.entries[item["knowledge_id"]].content)
            tokens=ContextTokenEstimator().estimate_text(json.dumps(body,ensure_ascii=False,separators=(",",":")))
            self.assertLessEqual(tokens+512+max(768,(capacity*3+31)//32),capacity)
        self.assertTrue(saw_pressure and saw_full)
