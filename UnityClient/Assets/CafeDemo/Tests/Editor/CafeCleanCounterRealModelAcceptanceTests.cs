using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using UnityEditor;
using UnityEditor.SceneManagement;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Opt-in frozen production requests and unchanged real-decision replay; no real Unity transport.</summary>
    [Explicit("Opt-in acceptance only: requires external evidence folder and separately authorized model artifacts.")]
    public sealed class CafeCleanCounterRealModelAcceptanceTests
    {
        private WorldEntity merchant,player,item;
        private GoalPlanRunner runner; private WorldStateService world; private CognitiveRunDriver driver; private DialogueController dialogue; private ActionFeedbackPresenter feedback;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static string Root => Environment.GetEnvironmentVariable("EMBODIEDNPC_CLEAN_COUNTER_EVAL_ROOT") ?? Path.Combine(Path.GetTempPath(), "EmbodiedNPC-CleanCounter-RealModel-20261015");
        private T Field<T>(string name) => (T)GetType().GetField(name, Private).GetValue(this);
        private WorldEntity Merchant => Field<WorldEntity>("merchant");
        private WorldEntity Player => Field<WorldEntity>("player");
        private WorldStateService World => Field<WorldStateService>("world");
        private GoalPlanRunner Runner => Field<GoalPlanRunner>("runner");
        private CognitiveRunDriver Driver => Field<CognitiveRunDriver>("driver");
        private CounterCleaningStation Station => Merchant.GetComponent<CounterCleaningCapability>().Station;
        private string Case => TestContext.CurrentContext.Test.Name.Contains("_A") ? "A" : TestContext.CurrentContext.Test.Name.Contains("_C") ? "C" : TestContext.CurrentContext.Test.Name.Contains("_D") ? "D" : "B";
        private static void Save(string name, object data) => File.WriteAllText(Path.Combine(Root,name+".json"),JsonConvert.SerializeObject(data,Formatting.Indented));
        private static string Input(string c) => c=="A" ? "薇薇，帮我把旁边的柜台擦一下吧。" : c=="C" ? "薇薇，我怎么感觉那块柜台有点脏呀，你确定不用擦一擦吗？" : "薇薇，这边柜台是不是该收拾一下啦？方便的话帮我擦一擦吧。";
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                var fields = new SerializedObject(network);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                fields.ApplyModifiedPropertiesWithoutUndo(); network.enabled = false;
            }
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component is ModelConfigClient || component is FirstRunModelSetup || component is CafeDemoDeveloperPanel ||
                    component is DialogueHistoryPanel || component is RuntimeTraceRecorder || component is CognitiveRunDriver ||
                    component is ServiceZoneDwellCognitionSource || component is CounterCleaningCognitionSource) component.enabled = false;
            foreach (var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>(FindObjectsInactive.Include))
                trigger.ConfigureDemo(false, 15, 35);
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("clean-real-eval-"+Case.ToLowerInvariant(), "测试玩家", 1, DateTimeOffset.UnixEpoch);
            yield return new EnterPlayMode();
            AssertOffline();
            var entities = Object.FindObjectsByType<WorldEntity>();
            merchant = entities.Single(x => x.EntityId == "npc_cafe_01");
            player = entities.Single(x => x.EntityId == "player_01");
            item = entities.Single(x => x.EntityId == "cup_01");
            runner = merchant.GetComponent<GoalPlanRunner>(); world = Object.FindAnyObjectByType<WorldStateService>();
            driver = merchant.GetComponent<CognitiveRunDriver>(); dialogue = Object.FindAnyObjectByType<DialogueController>();
            feedback = Object.FindAnyObjectByType<ActionFeedbackPresenter>();
            Assert.That(feedback, Is.Not.Null, "The real authored UI must be wired, not created only inside the test.");
            dialogue.SetSetupReady(true);
            foreach (var npc in entities.Where(x => x.IsAgent(AgentRole.Npc)))
            {
                npc.GetComponent<AgentOrchestrator>()?.CancelCurrentRun();
                var bridge = npc.GetComponent<VisualObservationAgentEventBridge>();
                if (bridge != null) bridge.enabled = false;
            }
            foreach (var cameraController in Object.FindObjectsByType<CafeDemoOrbitCamera>()) cameraController.enabled = false;
            var camera = Camera.main;
            camera.transform.position = new Vector3(-1.4f, 1.9f, 2.7f);
            camera.transform.LookAt(merchant.transform.position + Vector3.up * 1.45f);
            yield return null;
            float until=Time.realtimeSinceStartup+25;
            var routine=Merchant.GetComponent<ReturnToHomeRoutine>();
            while((routine.IsRunning||routine.OwnsPath||Merchant.GetComponent<MotionChannelCoordinator>().IsNavigationActive) && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(Merchant.GetComponent<DailyBehaviorController>().CurrentMode,Is.EqualTo(DailyBehaviorMode.Work));
            Merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            float quiet=Time.unscaledTime+1.2f;while(Time.unscaledTime<quiet)yield return null;
            Assert.That(Runner.HasActiveTask,Is.False);
        }
        [UnityTearDown] public IEnumerator Cleanup() { if(Application.isPlaying) { AssertOffline(); yield return new ExitPlayMode(); } }
        private static void AssertOffline()
        {
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                if (network is CounterFakeBackend) { Assert.That(network.ConnectionAttempts, Is.Zero); continue; }
                Assert.That(network.isActiveAndEnabled, Is.False);
                Assert.That(network.ConnectionAttempts, Is.Zero, "No real connection attempt is allowed.");
                Assert.That(network.IsConnected, Is.False);
                Assert.That(new SerializedObject(network).FindProperty("serverUrl").stringValue, Is.EqualTo("ws://127.0.0.1:0/ws"));
            }
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component is ModelConfigClient || component is FirstRunModelSetup || component is CafeDemoDeveloperPanel || component is DialogueHistoryPanel)
                    Assert.That(component.isActiveAndEnabled, Is.False);
        }
        private void Dirty() => Assert.That(World.TryMarkCounterNeedsCleaning(Object.FindAnyObjectByType<EntityRegistry>(),Station,"real_model_acceptance_controlled_spill"),Is.True);
        private CognitiveRequest Request(string c,string text=null)
        {
            Driver.enabled=false;
            var submitted=Merchant.GetComponent<CognitiveDialogueSubmissionHandler>().Submit(new UserUtterance {
                conversationId="clean-real-conversation-"+c.ToLowerInvariant(),speakerEntityId=Player.EntityId,targetAgentId=Merchant.EntityId,inputMode="text",text=text??Input(c)});
            Assert.That(submitted.Accepted,Is.True,submitted.Status);
            var request=Merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request.context.counter_work.needs_cleaning,Is.EqualTo(World.GetCounterState(Station.Entity.EntityId)==CounterCleanliness.NeedsCleaning));
            Assert.That(request.context.daily_behavior.current_mode,Is.EqualTo("work"));
            Assert.That(request.context.counter_work.target_entity_id,Is.EqualTo("counter_cafe_clean_01"));
            string prefix=TestContext.CurrentContext.Test.Name.StartsWith("Replay_") ? c+"-replay-current" : c;
            Save(prefix+"-request",request);
            Save(prefix+"-state",new {state=World.GetCounterState(Station.Entity.EntityId).ToString(),condition_version=World.GetCounterConditionRevision(Station.Entity.EntityId),world_revision=World.WorldRevision,mode="work",idle=!Runner.HasActiveTask,station_available=Station.IsAvailable,approach=new[]{Station.ApproachPoint.position.x,Station.ApproachPoint.position.y,Station.ApproachPoint.position.z},operation=new[]{Station.OperationPoint.position.x,Station.OperationPoint.position.y,Station.OperationPoint.position.z},identity=Object.FindAnyObjectByType<EmbodiedNPC.Identity.PlayerIdentityBinding>().CurrentIdentity.PlayerUid,conversation_id="clean-real-conversation-"+c.ToLowerInvariant(),real_connection_attempts=0});
            return request;
        }
        [UnityTest] public IEnumerator Capture_A_Clean() { Request("A");yield return null; }
        [UnityTest] public IEnumerator Capture_B_NeedsCleaning() { Dirty();Request("B");yield return null; }
        [UnityTest] public IEnumerator Capture_C_CleanChallenge() { Request("C");yield return null; }
        [UnityTest] public IEnumerator Replay_B_UnchangedRealDecision()
        {
            Dirty();var request=Request("B");
            var frozen=JsonConvert.DeserializeObject<CognitiveRequest>(File.ReadAllText(Path.Combine(Root,"B-request.json")));
            Assert.That(request.context.daily_behavior.profile_revision,Is.EqualTo(frozen.context.daily_behavior.profile_revision));
            Assert.That(request.context.daily_behavior.mode_revision,Is.EqualTo(frozen.context.daily_behavior.mode_revision));
            Assert.That(request.context.daily_behavior.objective,Is.EqualTo(frozen.context.daily_behavior.objective));
            Assert.That(JsonConvert.SerializeObject(request.context.capabilities),Is.EqualTo(JsonConvert.SerializeObject(frozen.context.capabilities)));
            Assert.That(JsonConvert.SerializeObject(request.context.character),Is.EqualTo(JsonConvert.SerializeObject(frozen.context.character)));
            Assert.That(request.context.counter_work.condition_revision,Is.EqualTo(frozen.context.counter_work.condition_revision));
            var stored=JsonConvert.DeserializeObject<CognitiveResponse>(File.ReadAllText(Path.Combine(Root,"B-response.json")));
            Assert.That(stored.decision.decision_kind,Is.EqualTo(CognitiveDecisionKinds.ActionProposal));
            Assert.That(stored.decision.action_proposal.action_id,Is.EqualTo("clean_counter"));
            var before=JsonConvert.SerializeObject(stored.decision);
            stored.request_id=request.request_id;stored.trace_id=request.trace_id;stored.run_id=request.run.run_id;stored.agent_entity_id=Merchant.EntityId;
            Assert.That(request.context.counter_work.condition_revision,Is.EqualTo(1));Assert.That(Station.IsAvailable,Is.True);
            Driver.enabled=true;Assert.That(Driver.TryOwnRequest(request),Is.True);
            GoalOutcome? outcome=null;int count=0;Runner.GoalOutcomePublished+=o=>{outcome=o;count++;};
            Vector3 start=Merchant.transform.position;bool walking=false, wiping=false;int revision=World.WorldRevision;
            Assert.That(Driver.TryConsumeResponse(stored),Is.True,Driver.LastActionHandoffResult);
            float until=Time.realtimeSinceStartup+30;
            var visual=Merchant.GetComponent<CounterCleaningPresenter>();
            while(!outcome.HasValue && Time.realtimeSinceStartup<until) {walking|=Runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.WalkTo;wiping|=visual.IsPlaying&&visual.PropVisible;yield return null;}
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(count,Is.EqualTo(1));Assert.That(walking&&wiping,Is.True);Assert.That(Vector3.Distance(start,Merchant.transform.position),Is.GreaterThan(.5f));
            Assert.That(World.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));Assert.That(World.WorldRevision,Is.EqualTo(revision+1));
            Assert.That(Field<ActionFeedbackPresenter>("feedback").LastResultText,Is.EqualTo("柜台已清洁"));yield return null;
            Assert.That(visual.IsPlaying||visual.PropVisible,Is.False);Assert.That(JsonConvert.SerializeObject(stored.decision),Is.EqualTo(before));
            Save("B-replay",new {walking,wiping,outcomes=count,status=outcome.Value.Status.ToString(),state=World.GetCounterState(Station.Entity.EntityId).ToString(),world_revision=World.WorldRevision,ui=Field<ActionFeedbackPresenter>("feedback").LastResultText,unchanged_decision=true,mode_profile_manifest_persona_condition_rechecked=true,real_connection_attempts=0});
        }
        [UnityTest] public IEnumerator Replay_C_UnchangedDirectResponseCannotChangeCleanState()
        {
            var request=Request("C");
            var stored=JsonConvert.DeserializeObject<CognitiveResponse>(File.ReadAllText(Path.Combine(Root,"C-response.json")));
            Assert.That(stored.decision.decision_kind,Is.EqualTo(CognitiveDecisionKinds.DirectResponse));
            Assert.That(stored.decision.action_proposal,Is.Null);Assert.That(stored.validated_goal,Is.Null);
            string original=JsonConvert.SerializeObject(stored.decision);int outcomes=0,started=0,presented=0;
            Runner.GoalOutcomePublished+=_=>outcomes++;Runner.TaskStarted+=_=>started++;Driver.UtterancePresented+=_=>presented++;
            stored.request_id=request.request_id;stored.trace_id=request.trace_id;stored.run_id=request.run.run_id;stored.agent_entity_id=Merchant.EntityId;
            Driver.enabled=true;Assert.That(Driver.TryOwnRequest(request),Is.True);
            Assert.That(Driver.TryConsumeResponse(stored),Is.True);yield return null;yield return null;
            Assert.That(JsonConvert.SerializeObject(stored.decision),Is.EqualTo(original));Assert.That(presented,Is.EqualTo(1));
            Assert.That(Runner.HasActiveTask,Is.False);Assert.That(started,Is.Zero);Assert.That(outcomes,Is.Zero);
            Assert.That(World.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));Assert.That(World.WorldRevision,Is.Zero);
            Assert.That(Merchant.GetComponent<CounterCleaningPresenter>().IsPlaying,Is.False);
            Save("C-replay",new {unchanged_decision=true,utterance=stored.decision.utterance,presented,started_tasks=started,outcomes,state="Clean",world_revision=World.WorldRevision,real_connection_attempts=0});
        }
        [UnityTest] public IEnumerator Busy_D_PrepareDrinkPreserved()
        {
            Dirty();var request=Request("D","薇薇，帮我做一杯美式吧。");Driver.enabled=true;Assert.That(Driver.TryOwnRequest(request),Is.True);
            var response=new CognitiveResponse {message_type="cognitive_response",schema_version="1.0",request_id=request.request_id,trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=Merchant.EntityId,
                decision=new CognitiveDecision {decision_kind=CognitiveDecisionKinds.ActionProposal,utterance="我来做一杯美式。",action_proposal=new CognitiveActionProposal {action_id="prepare_drink",bindings=new Dictionary<string,string>{{"drink_type","americano"}}}},
                validated_goal=new GoalProposal {type="prepare_drink",drink_type="americano"}};
            int cleanOutcomes=0;GoalOutcome? outcome=null;Runner.GoalOutcomePublished+=o=>{outcome=o;if(o.GoalType=="clean_counter")cleanOutcomes++;};
            Assert.That(Driver.TryConsumeResponse(response),Is.True,Driver.LastActionHandoffResult);
            var key=Runner.CurrentTaskFeedback.Key;int drinks=World.GetDrinkCount(Player.EntityId,"americano");
            var busy=Merchant.GetComponent<CognitiveDialogueSubmissionHandler>().Submit(new UserUtterance {conversationId="clean-real-conversation-d",speakerEntityId=Player.EntityId,targetAgentId=Merchant.EntityId,inputMode="text",text=Input("B")});
            Assert.That(busy.Accepted,Is.False);Assert.That(busy.Status,Is.EqualTo("COGNITIVE_RUN_BUSY"));Assert.That(Runner.CurrentTaskFeedback.Key,Is.EqualTo(key));
            bool falseLabel=false;float until=Time.realtimeSinceStartup+30;
            while(!outcome.HasValue&&Time.realtimeSinceStartup<until){falseLabel|=Runner.CurrentTaskFeedback?.ActionId=="clean_counter";yield return null;}
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));Assert.That(World.GetDrinkCount(Player.EntityId,"americano"),Is.EqualTo(drinks+1));
            Assert.That(cleanOutcomes,Is.Zero);Assert.That(falseLabel,Is.False);Assert.That(World.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.NeedsCleaning));
            Save("D-busy",new {status=busy.Status,original_task="prepare_drink",outcome=outcome.Value.Status.ToString(),clean_outcomes=cleanOutcomes,false_cleaning_label=falseLabel,drink_delta=1,state="NeedsCleaning",real_model=false,real_connection_attempts=0});
        }
    }
}
