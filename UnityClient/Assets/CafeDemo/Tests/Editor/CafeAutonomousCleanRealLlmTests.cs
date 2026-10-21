using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Manual one-shot live acceptance. A real world event is the only cognition trigger.</summary>
    [Explicit("Requires the separately authorized isolated P2-D2 backend; may spend one real Provider call.")]
    public sealed class CafeAutonomousCleanRealLlmTests
    {
        private static string Root => Path.Combine(Path.GetTempPath(),"EmbodiedNPC-P2D2-AutonomousClean-20261015");
        private WorldEntity merchant, player;
        private WorldStateService world;
        private GoalPlanRunner runner;
        private CognitiveRunDriver driver;
        private DialogueController dialogue;
        private BackendWebSocketClient live;
        private CounterCleaningCognitionSource source;
        private CognitiveRequest request;
        private CognitiveResponse response;
        private CounterConditionChange change;
        private AgentOrchestrator Runs => merchant.GetComponent<AgentOrchestrator>();
        private CounterCleaningStation Station => merchant.GetComponent<CounterCleaningCapability>().Station;
        private MotionChannelCoordinator Motion => merchant.GetComponent<MotionChannelCoordinator>();
        private static void Save(string name,object data) => File.WriteAllText(Path.Combine(Root,name+".json"),JsonConvert.SerializeObject(data,Formatting.Indented));
        private static float[] Pos(Vector3 p) => new[]{p.x,p.y,p.z};
        private void AssertIsolated()
        {
            foreach(var client in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                if(client==live)continue;
                Assert.That(client.ConnectionAttempts,Is.Zero);Assert.That(client.IsConnected,Is.False);Assert.That(client.enabled,Is.False);
                Assert.That(new SerializedObject(client).FindProperty("serverUrl").stringValue,Is.EqualTo("ws://127.0.0.1:0/ws"));
            }
            foreach(var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if(c is ModelConfigClient || c is FirstRunModelSetup || c is CafeDemoDeveloperPanel || c is DialogueHistoryPanel)
                    Assert.That(c.isActiveAndEnabled,Is.False);
        }
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            foreach(var client in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                var fields=new SerializedObject(client);fields.FindProperty("connectOnStart").boolValue=false;
                fields.FindProperty("serverUrl").stringValue="ws://127.0.0.1:0/ws";fields.ApplyModifiedPropertiesWithoutUndo();client.enabled=false;
            }
            foreach(var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if(c is ModelConfigClient || c is FirstRunModelSetup || c is CafeDemoDeveloperPanel || c is DialogueHistoryPanel ||
                    c is RuntimeTraceRecorder || c is CognitiveRunDriver || c is ServiceZoneDwellCognitionSource ||
                    c is CounterCleaningCognitionSource || c is DialogueContinuationScheduler) c.enabled=false;
            foreach(var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>(FindObjectsInactive.Include))trigger.ConfigureDemo(false,15,35);
            Object.FindAnyObjectByType<LocalTestIdentityProvider>().Configure("P2D2-autonomous-clean-isolated","隔离验收玩家",1,DateTimeOffset.UnixEpoch);
            yield return new EnterPlayMode();
            var entities=Object.FindObjectsByType<WorldEntity>();merchant=entities.Single(x=>x.EntityId=="npc_cafe_01");player=entities.Single(x=>x.EntityId=="player_01");
            world=Object.FindAnyObjectByType<WorldStateService>();runner=merchant.GetComponent<GoalPlanRunner>();driver=merchant.GetComponent<CognitiveRunDriver>();
            source=merchant.GetComponent<CounterCleaningCognitionSource>();dialogue=Object.FindAnyObjectByType<DialogueController>();
            foreach(var npc in entities.Where(x=>x.IsAgent(AgentRole.Npc)))
            {npc.GetComponent<AgentOrchestrator>()?.CancelCurrentRun();var bridge=npc.GetComponent<VisualObservationAgentEventBridge>();if(bridge!=null)bridge.enabled=false;}
            dialogue.SetSetupReady(true);dialogue.ClearSession();
            foreach(var cameraController in Object.FindObjectsByType<CafeDemoOrbitCamera>())cameraController.enabled=false;
            var body=player.GetComponent<CharacterController>();body.enabled=false;player.transform.position=new Vector3(-.6f,.03f,1.5f);body.enabled=true;
            Camera.main.transform.position=player.transform.position+new Vector3(-.7f,1.75f,-.65f);
            Camera.main.transform.LookAt(Station.ApproachPoint.position+Vector3.up*1.2f);Camera.main.fieldOfView=55;
            var routine=merchant.GetComponent<ReturnToHomeRoutine>();float deadline=Time.realtimeSinceStartup+25;
            while((routine.IsRunning||routine.OwnsPath||Motion.IsNavigationActive)&&Time.realtimeSinceStartup<deadline)yield return null;
            merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            float quiet=Time.unscaledTime+1.3f;while(Time.unscaledTime<quiet)yield return null;
            AssertIsolated();Assert.That(routine.IsRunning||routine.OwnsPath||Motion.IsTaskMotionActive||Motion.IsCarryPresentationActive,Is.False);
            Assert.That(merchant.GetComponent<SpeechPresentationLifecycle>().IsSpeaking,Is.False);
            Assert.That(dialogue.HasActiveSession||Runs.HasActiveRun||runner.HasActiveTask,Is.False);
            Assert.That(merchant.GetComponent<DailyBehaviorController>().CurrentMode,Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
            Assert.That(Object.FindAnyObjectByType<ActionFeedbackPresenter>().PendingNotificationCount,Is.Zero);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(Application.isPlaying)
            {
                AssertIsolated();if(live!=null)Object.Destroy(live.gameObject);yield return null;
                yield return new ExitPlayMode();
            }
        }
        [UnityTest] public IEnumerator Live_TrustedCounterEvent_OneRealModelDecision() { yield return Execute(false); }
        [UnityTest] public IEnumerator ControlledReplay_OriginalRealAutonomousDecision() { yield return Execute(true); }
        [UnityTest] public IEnumerator Live_ScriptedSystemCognition_IdentityCompatibility()
        { yield return LiveSystemCognition(false); }
        [UnityTest] public IEnumerator Live_RealModelSystemCognition_FinalE2E()
        { yield return LiveSystemCognition(true); }
        private IEnumerator LiveSystemCognition(bool realModel)
        {
            string root=Path.Combine(Path.GetTempPath(),realModel?"EmbodiedNPC-AutonomousClean-FinalE2E-20261015":"EmbodiedNPC-IdentityCompatibility-P0-20261015");
            var config=JsonConvert.DeserializeObject<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"backend-ready.json")));
            Assert.That((string)config["provider"],Is.EqualTo(realModel?"deepseek_official_responses":"scripted-no-network"));
            int port=Convert.ToInt32(config["port"]);Assert.That(port,Is.Not.EqualTo(8000));
            var clientRoot=new GameObject("IdentityCompatibility_IsolatedClient");clientRoot.SetActive(false);
            live=clientRoot.AddComponent<BackendWebSocketClient>();var fields=new SerializedObject(live);
            fields.FindProperty("connectOnStart").boolValue=false;fields.FindProperty("maxConnectAttempts").intValue=1;
            fields.FindProperty("serverUrl").stringValue="ws://127.0.0.1:"+port+"/ws";fields.ApplyModifiedPropertiesWithoutUndo();clientRoot.SetActive(true);
            Assert.That(live.EnsureConnected(),Is.True);float deadline=Time.realtimeSinceStartup+10;
            while((!live.IsConnected||live.ConnectionStatus!="CONNECTED")&&Time.realtimeSinceStartup<deadline)yield return null;
            Assert.That(live.IsConnected&&live.ConnectionStatus=="CONNECTED",Is.True,live.ConnectionStatus);
            int requests=0,started=0,outcomes=0,presented=0,maxToast=0;bool matched=false,failed=false,walked=false,wiped=false,captured=false;GoalOutcome? outcome=null;
            var feedback=Object.FindAnyObjectByType<ActionFeedbackPresenter>();
            var bubbles=Object.FindAnyObjectByType<DialogueBubbleManager>();int bubbleBefore=bubbles.GetMessages(merchant.EntityId).Count;
            var composer=merchant.GetComponent<CognitiveContextComposer>();
            composer.CognitiveRequestCreated+=r=>{request=r;requests++;File.WriteAllText(Path.Combine(root,"T1-unity-request.json"),r.Serialize());};
            live.CognitiveResponseReceived+=r=>{response=r;matched=source.Matches(request,Runs.CurrentRun);};
            live.CognitiveRequestFailed+=_=>failed=true;
            runner.TaskStarted+=t=>{started++;Assert.That(t.Player,Is.Empty);};
            runner.GoalOutcomePublished+=o=>{outcome=o;outcomes++;};
            var presenter=(MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(driver);
            driver.Configure(merchant,Runs,composer,live,presenter);driver.UtterancePresented+=_=>presented++;driver.enabled=true;source.enabled=true;
            Assert.That(world.TryMarkCounterNeedsCleaning(Object.FindAnyObjectByType<EntityRegistry>(),Station,"identity_compatibility_controlled_spill"),Is.True);
            Assert.That(requests,Is.EqualTo(1));Assert.That(request.context.relevant_player,Is.Null);
            Assert.That(string.IsNullOrEmpty(request.recall_identity?.requester_entity_id),Is.True);
            Assert.That(request.context.perception.entities.Any(e=>e.entity_id=="player_01"&&e.kind=="player"),Is.True);
            Assert.That(source.Matches(request,Runs.CurrentRun),Is.True);
            deadline=Time.realtimeSinceStartup+40;
            while(!failed&&(response==null||runner.HasActiveTask||Runs.HasActiveRun)&&Time.realtimeSinceStartup<deadline)
            {
                walked|=runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.WalkTo;
                wiped|=merchant.GetComponent<CounterCleaningPresenter>().IsPlaying;
                maxToast=Math.Max(maxToast,feedback.PendingNotificationCount);
                if(realModel&&wiped&&!captured){captured=true;ScreenCapture.CaptureScreenshot(Path.Combine(root,"player-view-wiping.png"));}
                yield return null;
            }
            Assert.That(response,Is.Not.Null,live.LastCognitiveIngressStatus);
            Assert.That(matched,Is.True);
            bool action=response.decision.decision_kind==CognitiveDecisionKinds.AutonomousActionIntent;
            Assert.That(action||realModel&&response.decision.decision_kind==CognitiveDecisionKinds.NoAction,Is.True);
            if(action)
            {
                Assert.That(started,Is.EqualTo(1));Assert.That(outcomes,Is.EqualTo(1));
                Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
                Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
                if(realModel)Assert.That(walked&&wiped,Is.True);
            }
            else
            {
                float quiet=Time.realtimeSinceStartup+2;while(Time.realtimeSinceStartup<quiet)yield return null;
                Assert.That(started+outcomes,Is.Zero);Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.NeedsCleaning));
                Assert.That(world.WorldRevision,Is.EqualTo(1));
            }
            Assert.That(requests,Is.EqualTo(1));Assert.That(presented+maxToast,Is.Zero);
            Assert.That(bubbles.GetMessages(merchant.EntityId).Count-bubbleBefore,Is.Zero);
            Assert.That(live.CognitiveRequestSendCount,Is.EqualTo(1));Assert.That(live.CognitiveResponseDispatchCount,Is.EqualTo(1));
            Assert.That(Runs.HasActiveRun||runner.HasActiveTask,Is.False);AssertIsolated();
            File.WriteAllText(Path.Combine(root,"T1-unity-result.json"),JsonConvert.SerializeObject(new {
                status=action?"PASS":"PASS WITH LIMITATIONS",real_time=true,requests,network_requests=live.CognitiveRequestSendCount,network_responses=live.CognitiveResponseDispatchCount,
                source_matches=matched,started,outcomes,outcome=outcome?.Status.ToString(),state=world.GetCounterState(Station.Entity.EntityId).ToString(),world_revision=world.WorldRevision,
                walked,wiped,presented,maxToast,bubble_delta=bubbles.GetMessages(merchant.EntityId).Count-bubbleBefore,
                mode=merchant.GetComponent<DailyBehaviorController>().CurrentMode.ToString(),relevant_player=request.context.relevant_player,
                perceived_player=true,decision=response.decision.decision_kind,request_id=request.request_id,run_id=request.run.run_id,trace_id=request.trace_id
            },Formatting.Indented));
        }
        private IEnumerator Execute(bool controlledReplay)
        {
            string prefix=controlledReplay ? "replay-" : "";
            var config=JsonConvert.DeserializeObject<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"config.json")));
            int port=Convert.ToInt32(config["port"]);Assert.That(port,Is.Not.EqualTo(8000));
            var root=new GameObject("P2D2_Isolated_OnlyClient");root.SetActive(false);live=controlledReplay ? root.AddComponent<CounterFakeBackend>() : root.AddComponent<BackendWebSocketClient>();
            var fields=new SerializedObject(live);fields.FindProperty("connectOnStart").boolValue=false;fields.FindProperty("maxConnectAttempts").intValue=1;
            fields.FindProperty("serverUrl").stringValue="ws://127.0.0.1:"+port+"/ws";fields.ApplyModifiedPropertiesWithoutUndo();root.SetActive(true);
            if(!controlledReplay)Assert.That(live.EnsureConnected(),Is.True);float deadline=Time.realtimeSinceStartup+10;
            while(!live.IsConnected&&Time.realtimeSinceStartup<deadline)yield return null;
            Assert.That(live.IsConnected,Is.True,live.ConnectionStatus);Assert.That(live.ConnectionAttempts,Is.EqualTo(controlledReplay?0:1));
            int requests=0,presented=0,outcomes=0,started=0;GoalOutcome? outcome=null;bool matchedAtIngress=false;
            var phases=new List<object>();var feedback=Object.FindAnyObjectByType<ActionFeedbackPresenter>();
            int bubbleBefore=Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Count;
            var composer=merchant.GetComponent<CognitiveContextComposer>();
            composer.CognitiveRequestCreated+=r=>{requests++;request=r;Save(prefix+"unity-request",r);};
            live.CognitiveResponseReceived+=r=>{response=r;matchedAtIngress=source.Matches(request,Runs.CurrentRun);Save(prefix+"unity-response",r);};
            world.CounterNeedsCleaning+=c=>{change=c;Save(prefix+"world-change",new {c.Token,c.Version,c.WorldRevision,c.Reason,target=c.Station.Entity.EntityId,current=world.IsCurrentCounterChange(c)});};
            runner.GoalOutcomePublished+=o=>{outcome=o;outcomes++;Save(prefix+"unity-outcome",o);};
            runner.TaskStarted+=t=>{started++;Assert.That(t.Player,Is.Empty);};
            runner.TaskPhaseChanged+=t=>phases.Add(new {action=t.Progress.Action.ToString(),label=ActionFeedbackPresenter.FormatPhase(t),time=Time.unscaledTime});
            var presenter=(MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(driver);
            driver.Configure(merchant,Runs,composer,live,presenter);driver.UtterancePresented+=_=>presented++;driver.enabled=true;source.enabled=true;
            Save(prefix+"initial",new {mode="Work",idle=!runner.HasActiveTask,conversation=dialogue.HasActiveSession,run=Runs.HasActiveRun,counter=world.GetCounterState(Station.Entity.EntityId).ToString(),world_revision=world.WorldRevision,position=Pos(merchant.transform.position),nav=Motion.IsNavigationActive,carry=Motion.IsCarryPresentationActive,talking=merchant.GetComponent<SpeechPresentationLifecycle>().IsSpeaking});
            Vector3 initial=merchant.transform.position;Assert.That(world.TryMarkCounterNeedsCleaning(Object.FindAnyObjectByType<EntityRegistry>(),Station,"p2d2_controlled_spill"),Is.True);
            Assert.That(change,Is.Not.Null);Assert.That(world.WorldRevision,Is.EqualTo(1));Assert.That(source.PublishedCount,Is.EqualTo(1));Assert.That(requests,Is.EqualTo(1));
            Assert.That(request.context.current_event.event_type,Is.EqualTo(AgentEventTypes.CounterNeedsCleaning));Assert.That(request.context.relevant_player,Is.Null);
            Assert.That(request.context.counter_work.condition_token,Is.EqualTo(change.Token));Assert.That(request.context.counter_work.condition_revision,Is.EqualTo(change.Version));
            var endpoint=merchant.GetComponent<AgentEventReceiver>();
            var interest=EventInterestEvaluator.Evaluate(source.CurrentEvent,endpoint.EntityId,endpoint.InterestProfile,"working",endpoint.ModeInterestOverrides,"work");
            Assert.That(Runs.CurrentRun.IsCounterCleaningConsideration,Is.True);Assert.That(source.Matches(request,Runs.CurrentRun),Is.True);
            Save(prefix+"event-chain",new {interest=interest.ToString(),request_id=request.request_id,run_id=request.run.run_id,trace_id=request.trace_id,event_id=request.context.current_event.event_id,condition_token=change.Token,condition_revision=change.Version,world_revision=world.WorldRevision,system_run=Runs.CurrentRun.IsCounterCleaningConsideration,source_matches=true,source_status=source.LastStatus,published=source.PublishedCount});
            string originalDecision=null;
            if(controlledReplay)
            {
                var frozen=JsonConvert.DeserializeObject<CognitiveRequest>(File.ReadAllText(Path.Combine(Root,"unity-request.json")));
                Assert.That(request.context.daily_behavior.current_mode,Is.EqualTo(frozen.context.daily_behavior.current_mode));
                Assert.That(request.context.daily_behavior.profile_revision,Is.EqualTo(frozen.context.daily_behavior.profile_revision));
                Assert.That(request.context.daily_behavior.mode_revision,Is.EqualTo(frozen.context.daily_behavior.mode_revision));
                Assert.That(request.context.counter_work.condition_revision,Is.EqualTo(frozen.context.counter_work.condition_revision));
                Assert.That(JsonConvert.SerializeObject(request.context.character),Is.EqualTo(JsonConvert.SerializeObject(frozen.context.character)));
                Assert.That(JsonConvert.SerializeObject(request.context.capabilities),Is.EqualTo(JsonConvert.SerializeObject(frozen.context.capabilities)));
                var original=JsonConvert.DeserializeObject<CognitiveResponse>(File.ReadAllText(Path.Combine(Root,"fallback-response.json")));
                originalDecision=JsonConvert.SerializeObject(original.decision);
                Save("replay-envelope-map",new {original_run=original.run_id,original_trace=original.trace_id,current_run=request.run.run_id,current_trace=request.trace_id,condition_revision=request.context.counter_work.condition_revision,condition_token=request.context.counter_work.condition_token});
                original.request_id=request.request_id;original.trace_id=request.trace_id;original.run_id=request.run.run_id;original.agent_entity_id=merchant.EntityId;
                Assert.That(((CounterFakeBackend)live).Reply(original),Is.True);
                Assert.That(JsonConvert.SerializeObject(original.decision),Is.EqualTo(originalDecision));
            }
            bool walked=false,wiped=false,prop=false,cancelRefused=false,captured=false,sawWipeFeedback=false;int maxToast=0,maxTasks=0;
            float wipeAt=-1,wipeEnd=-1,handMotion=0,facing=180;Vector3 handStart=Vector3.zero;
            var visual=merchant.GetComponent<CounterCleaningPresenter>();var animator=merchant.GetComponentsInChildren<Animator>().Single(x=>x.isHuman&&x.gameObject.activeInHierarchy);var hand=animator.GetBoneTransform(HumanBodyBones.RightHand);
            deadline=Time.realtimeSinceStartup+38;
            while((response==null||runner.HasActiveTask||Runs.HasActiveRun)&&Time.realtimeSinceStartup<deadline)
            {
                walked|=runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.WalkTo;maxToast=Math.Max(maxToast,feedback.PendingNotificationCount);maxTasks=Math.Max(maxTasks,feedback.ActiveTaskCount);
                if(visual.IsPlaying)
                {
                    wiped=true;prop|=visual.PropVisible;if(wipeAt<0){wipeAt=Time.unscaledTime;handStart=hand.position;}
                    sawWipeFeedback|=feedback.PhaseText(merchant.EntityId).Contains("正在擦拭");
                    handMotion=Mathf.Max(handMotion,Vector3.Distance(handStart,hand.position));
                    var direction=Station.OperationPoint.position-merchant.transform.position;direction.y=0;facing=Vector3.Angle(merchant.transform.forward,direction);
                    Assert.That(Motion.ActiveBodyChannel,Is.EqualTo(MotionChannel.Interaction));
                    if(!cancelRefused){Assert.That(driver.CancelLowPrioritySystemConsideration("accepted_runner_must_keep_ownership"),Is.False);cancelRefused=true;}
                    if(!captured&&Time.unscaledTime-wipeAt>.8f){captured=true;ScreenCapture.CaptureScreenshot(Path.Combine(Root,prefix+"01-player-view-wiping.png"));}
                }
                else if(wipeAt>=0&&wipeEnd<0)wipeEnd=Time.unscaledTime;
                yield return null;
            }
            Assert.That(response,Is.Not.Null,live.LastCognitiveIngressStatus);Assert.That(matchedAtIngress,Is.True);
            Assert.That(new[]{CognitiveDecisionKinds.NoAction,CognitiveDecisionKinds.AutonomousActionIntent}.Contains(response.decision.decision_kind),Is.True);
            float quiet=Time.unscaledTime+2.2f;while(Time.unscaledTime<quiet){maxToast=Math.Max(maxToast,feedback.PendingNotificationCount);yield return null;}
            if(wipeAt>=0&&wipeEnd<0)wipeEnd=Time.unscaledTime-2.2f;
            Save(prefix+"observations",new {walked,wiped,prop,handMotion,facing,wipe_seconds=wipeEnd-wipeAt,outcomes,started,presented,maxToast,maxTasks,matchedAtIngress,state=world.GetCounterState(Station.Entity.EntityId).ToString(),world_revision=world.WorldRevision,source_status=source.LastStatus,active_run=Runs.HasActiveRun,active_task=runner.HasActiveTask,ingress=live.LastCognitiveIngressStatus});
            Assert.That(requests,Is.EqualTo(1));Assert.That(controlledReplay?((CounterFakeBackend)live).Requests.Count:live.CognitiveRequestSendCount,Is.EqualTo(1));Assert.That(live.CognitiveResponseDispatchCount,Is.EqualTo(1));
            Assert.That(presented,Is.Zero);Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Count,Is.EqualTo(bubbleBefore));Assert.That(maxToast,Is.Zero);
            Assert.That(Runs.HasActiveRun||runner.HasActiveTask||visual.IsPlaying||visual.PropVisible,Is.False);
            if(response.decision.decision_kind==CognitiveDecisionKinds.AutonomousActionIntent)
            {
                Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome?.FailureCode);
                Assert.That(outcomes,Is.EqualTo(1));Assert.That(started,Is.EqualTo(1));Assert.That(walked&&wiped&&prop,Is.True);
                Assert.That(Vector3.Distance(initial,merchant.transform.position),Is.GreaterThan(.5f));Assert.That(handMotion,Is.GreaterThan(.06f));Assert.That(facing,Is.LessThan(15));
                Assert.That(world.WorldRevision,Is.EqualTo(2));Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
                Assert.That(sawWipeFeedback,Is.True);Assert.That(feedback.LastResultText,Is.Empty,"Autonomous work has no initiating player and must not emit that player's result Toast.");var key=TaskFeedbackKey.From(outcome.Value);
                Assert.That(world.TryGetCounterCleaningReceipt(key.Actor,key.Proposal,key.Trace,out var receipt),Is.True);Assert.That(receipt.ConditionVersion,Is.EqualTo(change.Version));
                Assert.That(wipeEnd-wipeAt,Is.InRange(2.3f,3.5f));
            }
            else {Assert.That(outcomes,Is.Zero);Assert.That(started,Is.Zero);Assert.That(wiped,Is.False);Assert.That(world.WorldRevision,Is.EqualTo(1));Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.NeedsCleaning));}
            var daily=merchant.GetComponent<DailyBehaviorController>();Assert.That(daily.CurrentMode,Is.EqualTo(DailyBehaviorMode.Work));Assert.That(daily.CurrentActivity.HasValue,Is.True);
            ScreenCapture.CaptureScreenshot(Path.Combine(Root,prefix+"02-player-view-final.png"));yield return null;yield return null;
            Save(prefix+"final",new {decision=response.decision.decision_kind,real_time=!controlledReplay,unchanged_decision=controlledReplay?JsonConvert.SerializeObject(response.decision)==originalDecision:true,walked,wiped,prop,sawWipeFeedback,hand_motion=handMotion,facing_error=facing,wipe_seconds=wipeEnd-wipeAt,outcomes,started,presented,bubble_delta=0,max_toast=maxToast,max_tasks=maxTasks,world_revision=world.WorldRevision,state=world.GetCounterState(Station.Entity.EntityId).ToString(),mode=daily.CurrentMode.ToString(),activity=daily.CurrentActivity.ToString(),requests,network_requests=live.CognitiveRequestSendCount,network_responses=live.CognitiveResponseDispatchCount,source_matches_at_ingress=matchedAtIngress,background_cancel_refused_after_acceptance=cancelRefused,ui=feedback.LastResultText,phases});
            AssertIsolated();
        }
    }
}
