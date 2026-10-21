using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TaskConfirmationTestPresenter : MonoBehaviour, ICognitiveResponsePresenter
    {
        public int Count { get; private set; }
        public bool TryPresentCognitiveUtterance(string text) { Count++; return true; }
    }

    public sealed class TaskCognitionContractTests
    {
        private JObject pair;
        private CognitiveRequest request;
        private CognitiveResponse response;
        private readonly List<GameObject> objects = new();

        // Golden output from the isolated Scripted Provider and real Backend WebSocket handler.
        private const string GoldenProtocolFixture = @"{""request"": {""message_type"": ""cognitive_request"", ""schema_version"": ""1.1"", ""request_id"": ""request_1"", ""trace_id"": ""trace_1"", ""run"": {""run_id"": ""run_01"", ""agent_entity_id"": ""npc_cafe_01"", ""trigger_id"": ""trigger_01"", ""trigger_type"": ""event"", ""started_at"": 10.0}, ""trigger"": {""trigger_id"": ""trigger_01"", ""trigger_type"": ""event"", ""agent_entity_id"": ""npc_cafe_01"", ""source_event_id"": ""event_01"", ""created_at"": 10.0, ""reason"": ""player.utterance""}, ""context"": {""run_id"": ""run_01"", ""agent_entity_id"": ""npc_cafe_01"", ""trigger"": {""trigger_id"": ""trigger_01"", ""trigger_type"": ""event"", ""agent_entity_id"": ""npc_cafe_01"", ""source_event_id"": ""event_01"", ""created_at"": 10.0, ""reason"": ""player.utterance""}, ""current_event"": {""event_id"": ""event_01"", ""trace_id"": null, ""source_observation_id"": null, ""event_type"": ""player.utterance"", ""source_entity_id"": ""player_01"", ""target_entity_id"": ""npc_cafe_01"", ""area_entity_id"": ""area_cafe_counter"", ""payload"": [{""key"": ""text"", ""value"": ""下次来时给我准备拿铁，可以吗？""}], ""occurred_at"": 10.0, ""world_revision"": 1, ""has_world_revision"": true}, ""character"": {""character_id"": ""cafe.merchant"", ""display_name"": ""店员"", ""role_summary"": ""咖啡馆店员"", ""backstory"": ""在社区咖啡馆生活。"", ""personality"": ""温和。"", ""speaking_style"": ""友好简洁"", ""boundaries"": ""不编造事实或能力。""}, ""behavior_mode"": {""mode_id"": ""working""}, ""daily_behavior"": null, ""relevant_player"": {""player_entity_id"": ""player_01"", ""display_name"": ""玩家""}, ""perception"": {""observer_entity_id"": ""npc_cafe_01"", ""perception_revision"": 1, ""entities"": [{""entity_id"": ""cup_01"", ""kind"": ""item"", ""display_name"": ""杯子"", ""aliases"": [], ""semantic_tags"": [""cup""], ""visibility"": ""visible"", ""capabilities"": [""pick_up""]}]}, ""counter_work"": null, ""scene_facts"": null, ""capabilities"": {""available_actions"": [{""action_id"": ""prepare_drink"", ""description"": ""Bring one visible item to one visible destination."", ""bindings"": [{""name"": ""drink_type"", ""kind"": ""enum"", ""required"": true, ""enum_values"": [""latte"", ""cappuccino"", ""americano""]}]}]}}, ""reply_language"": ""zh-CN"", ""recall_identity"": {""observer"": {""entity_id"": ""npc_cafe_01"", ""kind"": ""npc"", ""identity_namespace"": ""npc"", ""identity_type"": ""Character"", ""persistent_id"": ""cafe.merchant.001""}, ""requester_entity_id"": ""player_01"", ""current_subjects"": [{""entity_id"": ""player_01"", ""kind"": ""player"", ""identity_namespace"": ""player"", ""identity_type"": ""Player"", ""persistent_id"": ""synthetic-task-player""}]}, ""task_runtime"": {""command_id"": ""command_1"", ""client_session_id"": ""session_1"", ""visit_id"": ""session_1"", ""scene_instance_id"": ""scene_1""}}, ""response"": {""message_type"": ""cognitive_response"", ""schema_version"": ""1.1"", ""request_id"": ""request_1"", ""trace_id"": ""trace_1"", ""run_id"": ""run_01"", ""agent_entity_id"": ""npc_cafe_01"", ""decision"": {""decision_kind"": ""DIRECT_RESPONSE"", ""episode_directive"": null, ""dialogue_turn_policy"": null, ""utterance"": ""可以，下次来时我会为你准备拿铁。"", ""emotion"": null, ""gesture"": null, ""look_at"": null, ""action_proposal"": null, ""target_daily_mode"": null}, ""validated_goal"": null, ""task_receipt"": {""schema_version"": ""1.1"", ""request_id"": ""request_1"", ""trace_id"": ""trace_1"", ""run_id"": ""run_01"", ""agent_entity_id"": ""npc_cafe_01"", ""command_id"": ""command_1"", ""owner"": {""identity_namespace"": ""npc"", ""identity_type"": ""Character"", ""persistent_id"": ""cafe.merchant.001""}, ""player"": {""identity_namespace"": ""player"", ""identity_type"": ""Player"", ""persistent_id"": ""synthetic-task-player""}, ""visit_id"": ""session_1"", ""scene_instance_id"": ""scene_1"", ""reply_language"": ""zh-CN"", ""operation"": ""create"", ""outcome"": ""created"", ""committed"": true, ""recovered"": false, ""task_id"": ""task_f35d323ec4ea42db9f66af90e0703fbc"", ""task_version"": 1, ""task_status"": ""waiting"", ""error_code"": null}, ""task_result"": ""committed""}}";

        [SetUp]
        public void Setup()
        {
            string path = Environment.GetEnvironmentVariable("EMBODIEDNPC_TASK_TEST_PAIR");
            pair = JObject.Parse(File.Exists(path) ? File.ReadAllText(path) : GoldenProtocolFixture);
            request = pair["request"].ToObject<CognitiveRequest>();
            Assert.That(request.IsValid, Is.True);
            Assert.That(CognitiveResponseValidator.TryParse(pair["response"].ToString(), out response, out var error), Is.True, error);
        }

        [TearDown]
        public void Cleanup() { foreach (var go in objects) if (go != null) Object.DestroyImmediate(go); objects.Clear(); }
        private GameObject Create(string name) { var go = new GameObject(name); objects.Add(go); return go; }

        [Test]
        public void ActualBackendReceiptMatchesOriginalPrivateRequest()
        {
            Assert.That(TaskReceiptValidator.Matches(response, request), Is.True);
            Assert.That(request.Serialize(), Does.Contain("task_runtime"));
            Assert.That(response.decision.decision_kind, Is.EqualTo("DIRECT_RESPONSE"));
        }

        [TestCase("request_id")][TestCase("trace_id")][TestCase("run_id")]
        [TestCase("agent_entity_id")][TestCase("command_id")][TestCase("visit_id")]
        [TestCase("scene_instance_id")][TestCase("reply_language")]
        public void CrossRequestRunSceneAndLanguageReceiptsAreRejected(string field)
        {
            typeof(CognitiveTaskReceiptDto).GetField(field).SetValue(response.task_receipt, "other");
            Assert.That(TaskReceiptValidator.Matches(response, request), Is.False);
        }

        [TestCase("owner")][TestCase("player")]
        public void StableIdentityCannotBeReboundByDisplayOrEntityName(string field)
        {
            var identity = (TaskIdentityDto)typeof(CognitiveTaskReceiptDto).GetField(field).GetValue(response.task_receipt);
            identity.persistent_id = "another-stable-subject";
            Assert.That(TaskReceiptValidator.Matches(response, request), Is.False);
        }

        [TestCase("missing_receipt")][TestCase("unknown_field")][TestCase("coerced_bool")][TestCase("coerced_version")]
        public void InvalidProtocolNeverBecomesAnAcceptance(string invalid)
        {
            var wire = (JObject)pair["response"].DeepClone();
            if (invalid == "missing_receipt") wire.Remove("task_receipt");
            if (invalid == "unknown_field") wire["task_receipt"]["completed_by_model"] = true;
            if (invalid == "coerced_bool") wire["task_receipt"]["committed"] = "true";
            if (invalid == "coerced_version") wire["task_receipt"]["task_version"] = "1";
            Assert.That(CognitiveResponseValidator.TryParse(wire.ToString(), out _, out _), Is.False);
        }

        [Test]
        public void LegacySerializationOmitsPrivateTaskFields()
        {
            request.schema_version = "1.0"; request.task_runtime = null;
            Assert.That(request.IsValid, Is.True);
            Assert.That(JObject.Parse(request.Serialize()).Property("task_runtime"), Is.Null);
        }

        [Test]
        public void VisitStartsOnlyExplicitlyAndSurvivesMenuAndReconnect()
        {
            var visit = new GameVisit();
            Assert.That(visit.VisitId, Is.Null);
            Assert.That(visit.TryStart("session_a", "stable_player"), Is.True);
            Assert.That(visit.TryStart("session_a", "stable_player"), Is.True);
            Assert.That(visit.VisitId, Is.EqualTo("session_a"));
            Assert.That(visit.TryStart("session_b", "stable_player"), Is.False);
            Assert.That(visit.TryStart("session_a", "another_player"), Is.False);
            var nextProcess = new GameVisit();
            Assert.That(nextProcess.TryStart("session_b", "stable_player"), Is.True);
            Assert.That(nextProcess.VisitId, Is.Not.EqualTo(visit.VisitId));
        }

        [Test]
        public void BridgeCannotCaptureBeforeAdmissionAndCreatesNewSceneTokens()
        {
            var first = Create("isolated-task-bridge-1").AddComponent<TaskRuntimeBridge>();
            var second = Create("isolated-task-bridge-2").AddComponent<TaskRuntimeBridge>();
            Assert.That(first.Admit(null, null), Is.False);
            Assert.That(first.TryCapture(request.recall_identity, out _), Is.False);
            var token = typeof(TaskRuntimeBridge).GetField("sceneInstanceId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(token.GetValue(first), Is.Not.EqualTo(token.GetValue(second)));
        }

        private void Own(out AgentOrchestrator orchestrator, out CognitiveRunDriver driver, out TaskConfirmationTestPresenter presenter)
        {
            var root = Create("isolated-task-owner");
            var owner = root.AddComponent<WorldEntity>();
            var identity = request.recall_identity.observer;
            owner.Configure(request.run.agent_entity_id, "character.task.test", EntityKind.Agent, AgentRole.Npc,
                new PersistentIdentityRef(identity.identity_namespace, identity.identity_type, identity.persistent_id));
            var receiver = root.AddComponent<AgentEventReceiver>();
            var wake = root.AddComponent<WakeCognitiveTriggerBridge>(); wake.Configure(receiver);
            orchestrator = root.AddComponent<AgentOrchestrator>(); orchestrator.Configure(owner, wake);
            var trigger = new CognitiveTrigger("trigger_01", CognitiveTriggerTypes.Event, owner.EntityId, "event_01", 10d, "player.utterance");
            Assert.That(orchestrator.TryStartRun(trigger, out var run), Is.True);
            request.run = CognitiveRunDto.From(run); request.trigger = CognitiveTriggerDto.From(trigger);
            request.context.run_id = run.RunId; request.context.trigger = request.trigger;
            response.run_id = run.RunId; response.task_receipt.run_id = run.RunId;

            var player = Create("isolated-task-player").AddComponent<WorldEntity>();
            var stable = request.recall_identity.current_subjects[0];
            player.Configure(stable.entity_id, "character.task.player", EntityKind.Agent, AgentRole.Player,
                new PersistentIdentityRef(stable.identity_namespace, stable.identity_type, stable.persistent_id));
            var registry = Create("isolated-task-registry").AddComponent<EntityRegistry>();
            typeof(EntityRegistry).GetField("autoDiscoverSceneEntities", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(registry, false);
            typeof(EntityRegistry).GetField("entities", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(registry, new List<WorldEntity> { owner, player });
            registry.RebuildRegistry();
            var bridge = root.AddComponent<TaskRuntimeBridge>();
            Assert.That(bridge.Admit(registry, player), Is.True);
            Assert.That(bridge.TryCapture(request.recall_identity, out var runtime), Is.True);
            request.task_runtime = runtime;
            response.task_receipt.command_id = runtime.command_id; response.task_receipt.visit_id = runtime.visit_id;
            response.task_receipt.scene_instance_id = runtime.scene_instance_id;
            presenter = root.AddComponent<TaskConfirmationTestPresenter>();
            driver = root.AddComponent<CognitiveRunDriver>();
            driver.Configure(owner, orchestrator, root.GetComponent<CognitiveContextComposer>(), null, presenter);
            Assert.That(driver.TryOwnRequest(request), Is.True);
        }

        [Test]
        public void FormalDriverPresentsOnlyOwnedCommittedReceiptOnce()
        {
            Own(out _, out var driver, out var presenter);
            Assert.That(driver.TryConsumeResponse(response), Is.True);
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(presenter.Count, Is.EqualTo(1));
        }

        private (TaskRuntimeBridge bridge, EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter toast) SaveToastFixture()
        {
            typeof(TaskRuntimeBridge).GetMethod("ResetRetained",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            Own(out _,out _,out _);
            var owner=objects.Find(x=>x.GetComponent<TaskRuntimeBridge>()!=null);
            owner.name="林薇薇";
            var bridge=owner.GetComponent<TaskRuntimeBridge>();
            var transportRoot=Create("isolated-save-toast-transport");transportRoot.SetActive(false);
            var transport=transportRoot.AddComponent<TaskConsiderationMockTransport>();transport.Online=false;
            typeof(EmbodiedNPC.Communication.BackendWebSocketClient).GetField("connectOnStart",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(transport,false);
            bridge.Register(owner.GetComponent<WorldEntity>(),transport);
            var ui=new GameObject("isolated-save-toast-ui",typeof(RectTransform));objects.Add(ui);
            var toast=ui.AddComponent<EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter>();
            var player=objects.Find(x=>x.GetComponent<WorldEntity>()?.AgentRole==AgentRole.Player).GetComponent<WorldEntity>();
            toast.Configure(null,null,Array.Empty<EmbodiedNPC.Planning.GoalPlanRunner>(),player,null,null,null);
            return (bridge,toast);
        }
        [Test] public void SavedReceiptToastsOnceWithoutMenuReconnectOrRecoveredReplay()
        {
            var (bridge,toast)=SaveToastFixture();
            Assert.That(TaskReceiptValidator.Matches(response,request),Is.True);
            bridge.AcceptReceipt(request,response);bridge.AcceptReceipt(request,response);
            Assert.That(toast.PendingNotificationCount,Is.EqualTo(1));
            Assert.That(toast.LastResultText,Is.EqualTo(EmbodiedNPC.Settings.SettingsUiLanguage.T("委托已保存","Commission saved")));
            var pending=(System.Collections.IList)typeof(EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter).GetField("toasts",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(toast);
            Assert.That(pending[0].GetType().GetField("Name").GetValue(pending[0]),Is.EqualTo(EmbodiedNPC.Settings.SettingsUiLanguage.NpcDisplayName("npc_cafe_01","林薇薇")));
            toast.enabled=false;toast.enabled=true;
            // EditMode does not drive non-ExecuteAlways lifecycle callbacks reliably.
            typeof(EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(toast,null);
            typeof(EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter).GetMethod("OnEnable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(toast,null);
            Assert.That(toast.PendingNotificationCount,Is.Zero);
            bridge.RefreshStatus(request.run.agent_entity_id);
            typeof(TaskRuntimeBridge).GetMethod("OnLifecycleConnection",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(bridge,new object[]{"CONNECTED"});
            bridge.AcceptReceipt(request,response);
            Assert.That(toast.PendingNotificationCount,Is.Zero,"Showing UI or reconnecting must not replay the consumed result.");
            request.task_runtime.command_id="new_command_recovered";response.task_receipt.command_id=request.task_runtime.command_id;
            response.task_receipt.recovered=true;response.task_receipt.outcome="recovered";
            Assert.That(TaskReceiptValidator.Matches(response,request),Is.True);bridge.AcceptReceipt(request,response);
            Assert.That(toast.PendingNotificationCount,Is.Zero);
        }
        [Test] public void FailedHistoricalCancelledAndForeignReceiptsDoNotToastSaved()
        {
            var (bridge,toast)=SaveToastFixture();
            var original=JsonConvert.SerializeObject(response);
            foreach(string kind in new[]{"failed","deduplicated","cancel_pending","blocked","executing","completed","foreign","missing"})
            {
                response=JsonConvert.DeserializeObject<CognitiveResponse>(original);
                var receipt=response.task_receipt;
                if(kind=="failed") {receipt.committed=false;receipt.outcome="failed";receipt.task_id=null;receipt.task_version=null;receipt.task_status=null;receipt.error_code="TASK_NOT_SAVED";}
                if(kind=="deduplicated")receipt.outcome="deduplicated";
                if(kind=="cancel_pending"){receipt.operation="cancel";receipt.outcome="cancellation_requested";}
                if(kind=="blocked")receipt.task_status="blocked";
                if(kind=="executing" || kind=="completed")receipt.task_status=kind;
                if(kind=="foreign")receipt.scene_instance_id="foreign_scene";
                if(kind=="missing")response.task_receipt=null;
                bridge.AcceptReceipt(request,response);
                Assert.That(toast.PendingNotificationCount,Is.Zero,kind);
                if(kind=="failed")Assert.That(bridge.FeedbackCode,Is.EqualTo("save_failed"));
                if(kind=="cancel_pending")Assert.That(bridge.FeedbackCode,Is.EqualTo("cancel_pending"));
                if(kind=="blocked")Assert.That(bridge.FeedbackCode,Is.EqualTo("needs_verification"));
                if(kind=="executing")Assert.That(bridge.FeedbackCode,Is.EqualTo("result_pending"));
                if(kind=="completed")Assert.That(bridge.FeedbackCode,Is.EqualTo("completed"));
            }
        }

        [Test]
        public void CancelledRunCannotPresentLateReceipt()
        {
            Own(out var orchestrator, out var driver, out var presenter);
            orchestrator.CancelCurrentRun();
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(presenter.Count, Is.Zero);
        }

        [Test]
        public void MismatchedReceiptSuppressesPromiseInFormalDriver()
        {
            Own(out _, out var driver, out var presenter);
            response.task_receipt.scene_instance_id = "old_scene";
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(presenter.Count, Is.Zero);
        }
    }
}
