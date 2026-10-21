using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning.Recovery;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CounterFakeBackend : BackendWebSocketClient
    {
        public readonly List<CognitiveRequest> Requests = new();
        public readonly List<CognitiveRequest> Cancels = new();
        public TaskCompletionSource<bool> BlockCounterSend;
        public override bool IsConnected => isActiveAndEnabled;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest r)
        { Requests.Add(r); return r.context.current_event.event_type==AgentEventTypes.CounterNeedsCleaning && BlockCounterSend!=null ? BlockCounterSend.Task : Task.FromResult(true); }
        public override Task<bool> SendCognitiveCancelAsync(CognitiveRequest r) { Cancels.Add(r);return Task.FromResult(true); }
        public override Task<bool> SendExperienceAppendRequestAsync(ExperienceAppendRequest r) => Task.FromResult(false);
        public bool Reply(CognitiveResponse r) => ProcessIncomingJson(Newtonsoft.Json.JsonConvert.SerializeObject(r));
    }
    /// <summary>Authored V2, physical navigation/transactions, scripted decisions, all network lifecycles disabled before Play.</summary>
    public sealed class CafeCleanCounterTests
    {
        private WorldEntity merchant, player, item;
        private GoalPlanRunner runner;
        private WorldStateService world;
        private CognitiveRunDriver driver;
        private DialogueController dialogue;
        private ActionFeedbackPresenter feedback;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
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
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("clean-counter-offline", "测试玩家", 1, DateTimeOffset.UnixEpoch);
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
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying) { AssertOffline(); yield return new ExitPlayMode(); }
        }
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
        private CounterCleaningStation Station => merchant.GetComponent<CounterCleaningCapability>().Station;
        private MotionChannelCoordinator Motion => merchant.GetComponent<MotionChannelCoordinator>();
        private CognitiveResponse Propose(bool combined = false)
        {
            // The fixture supplies the response in memory; prevent the live driver send callback for this request.
            driver.enabled = false;
            merchant.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("drink-" + Guid.NewGuid().ToString("N"),
                AgentEventTypes.PlayerUtterance, player.EntityId, merchant.EntityId, "",
                new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "薇薇，能不能帮我把旁边那块柜台擦一下？" },
                Time.realtimeSinceStartupAsDouble, world.WorldRevision));
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request, Is.Not.Null);
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            return new CognitiveResponse { message_type="cognitive_response", schema_version="1.0",
                request_id=request.request_id, trace_id=request.trace_id, run_id=request.run.run_id, agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision { decision_kind=CognitiveDecisionKinds.ActionProposal,
                    target_daily_mode=combined ? "work" : null, utterance="好呀，我去擦一下柜台。",
                    action_proposal=new CognitiveActionProposal { action_id="clean_counter",
                        bindings=new Dictionary<string,string> { ["target"]=Station.Entity.EntityId } } },
                validated_goal=new GoalProposal { type="clean_counter", target_entity_id=Station.Entity.EntityId } };
        }
        private IEnumerator RestAtWindow()
        {
            Assert.That(merchant.GetComponent<DailyBehaviorDemoTrigger>().RequestMode(DailyBehaviorMode.Rest), Is.True);
            var rest=merchant.GetComponent<ReturnToHomeRoutine>();
            float until=Time.realtimeSinceStartup+28;
            while ((!rest.IsAtDailyDestination || rest.IsRunning || rest.OwnsPath || Motion.IsNavigationActive ||
                merchant.GetComponent<DailyBehaviorController>().CurrentMode != DailyBehaviorMode.Rest) &&
                Time.realtimeSinceStartup<until) yield return null;
            Assert.That(rest.IsAtDailyDestination, Is.True);
            float ready=Time.unscaledTime+1.2f; while(Time.unscaledTime<ready) yield return null;
        }
        private IEnumerator ReleaseDialogue()
        {
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            yield return null;
        }
        private Animator Character => merchant.GetComponentsInChildren<Animator>().Single(a=>a.isHuman && a.gameObject.activeInHierarchy);
        private CounterCleaningPresenter Visual => merchant.GetComponent<CounterCleaningPresenter>();
        private static string Evidence => Path.Combine(Path.GetTempPath(),"embodiednpc-clean-counter");
        private IEnumerator Capture(string name)
        {
            Directory.CreateDirectory(Evidence);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,name+".png"));
            for(int i=0;i<3;i++) yield return null;
        }
        private EntityRegistry Registry => Object.FindAnyObjectByType<EntityRegistry>();
        private void Dirty()
        {
            Assert.That(world.GetCounterState(Station.Entity.EntityId), Is.EqualTo(CounterCleanliness.Clean));
            Assert.That(world.TryMarkCounterNeedsCleaning(Registry, Station, "test_controlled_spill"), Is.True);
        }
        [UnityTest] public IEnumerator PlayerRequest_RealNavigationWipingCommitAndUi()
        {
            Dirty(); int revision=world.WorldRevision;
            Assert.That(world.TryMarkCounterNeedsCleaning(Registry,Station,"repeat_spill"), Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            yield return RestAtWindow(); Vector3 initial=merchant.transform.position;
            var control=player.GetComponent<CharacterController>();control.enabled=false;
            player.transform.position=new Vector3(-.6f,.03f,1.5f);control.enabled=true;
            Camera.main.transform.position=player.transform.position+new Vector3(-.7f,1.75f,-.65f);
            Camera.main.transform.LookAt(Station.ApproachPoint.position+Vector3.up*1.2f);Camera.main.fieldOfView=55;
            GoalOutcome? outcome=null; int outcomes=0;runner.GoalOutcomePublished+=o=>{outcome=o;outcomes++;};
            Assert.That(driver.TryConsumeResponse(Propose(true)), Is.True,driver.LastActionHandoffResult);
            Assert.That(runner.CurrentTaskFeedback.ActionId, Is.EqualTo("clean_counter"));
            float until=Time.realtimeSinceStartup+30;
            bool sawWalk=false; while(!Visual.IsPlaying && !outcome.HasValue && Time.realtimeSinceStartup<until)
            { sawWalk|=runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.WalkTo;yield return null; }
            Assert.That(sawWalk,Is.True);Assert.That(Vector3.Distance(initial,merchant.transform.position),Is.GreaterThan(2));
            Assert.That(Visual.IsPlaying && Visual.PropVisible, Is.True);Assert.That(Motion.ActiveBodyChannel,Is.EqualTo(MotionChannel.Interaction));
            Assert.That(ActionFeedbackPresenter.FormatPhase(runner.CurrentTaskFeedback),Is.EqualTo("正在擦拭柜台…"));
            var hand=Character.GetBoneTransform(HumanBodyBones.RightHand);Vector3 first=hand.position;float travel=0; float started=Time.time;
            bool captured=false;
            while(!outcome.HasValue && Time.realtimeSinceStartup<until)
            {
                if(Time.time-started>.5f && Time.time-started<1.8f) travel=Mathf.Max(travel,Vector3.Distance(first,hand.position));
                if(!captured && Time.time-started>.8f){captured=true;Debug.Log("[CounterPose] actor="+merchant.transform.position+" forward="+merchant.transform.forward+" hand="+hand.position);yield return Capture("01-player-wiping");}
                yield return null;
            }
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
            Assert.That(travel,Is.GreaterThan(.08f));Assert.That(outcomes,Is.EqualTo(1));
            Assert.That(world.WorldRevision,Is.EqualTo(revision+1));Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
            Assert.That(feedback.LastResultText,Is.EqualTo("柜台已清洁"));Assert.That(runner.CurrentTaskFeedback,Is.Null);
            yield return null;Assert.That(Visual.IsPlaying||Visual.PropVisible||Motion.IsInteractionMotionActive,Is.False);
            Assert.That(Visual.CurrentWeight,Is.Zero);
            var key=TaskFeedbackKey.From(outcome.Value);Assert.That(world.TryGetCounterCleaningReceipt(key.Actor,key.Proposal,key.Trace,out var receipt),Is.True);
            var commit=typeof(WorldStateService).GetMethod("TryCommitCounterCleaning",Private);
            commit.Invoke(world,new object[]{key.Proposal,key.Trace,Registry,merchant,Station.Entity,receipt.ConditionVersion,null,null});
            Assert.That(world.WorldRevision,Is.EqualTo(revision+1));
            typeof(ActionFeedbackPresenter).GetMethod("ObserveOutcome",Private).Invoke(feedback,new object[]{outcome.Value});
            Assert.That(feedback.LastResultText,Is.EqualTo("柜台已清洁"));
            yield return Capture("02-player-complete");yield return ReleaseDialogue();
            float quiet=Time.unscaledTime+1.2f;while(Time.unscaledTime<quiet)yield return null;
            Assert.That(driver.TryConsumeResponse(Propose()),Is.False,"An already clean counter cannot be accepted.");
            Assert.That(world.WorldRevision,Is.EqualTo(revision+1));
        }
        [UnityTest] public IEnumerator CancellationTimeoutAndInvalidation_CleanUpWithoutCommit()
        {
            Dirty(); int revision=world.WorldRevision;GoalOutcome? outcome=null;runner.GoalOutcomePublished+=o=>outcome=o;
            foreach(var reason in new[]{"cancel","station","timeout","runner","lease"})
            {
                outcome=null;Assert.That(driver.TryConsumeResponse(Propose()),Is.True,driver.LastActionHandoffResult);
                float until=Time.realtimeSinceStartup+28;while(!Visual.IsPlaying && !outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
                Assert.That(Visual.IsPlaying,Is.True);
                if(reason=="cancel")runner.CancelCurrentPlan();if(reason=="station")Station.enabled=false;if(reason=="runner")runner.enabled=false;
                if(reason=="lease")Object.FindAnyObjectByType<ResourceReservationService>().ResetLeases();
                if(reason=="timeout")
                {var executor=typeof(GoalPlanRunner).GetField("executor",Private).GetValue(runner);typeof(PlanExecutor).GetField("retryBudget",Private).SetValue(executor,new RetryBudget(Time.unscaledTime-31));}
                until=Time.realtimeSinceStartup+5;while(!outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
                Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.Not.EqualTo(PlanExecutionStatus.Succeeded));
                Assert.That(Visual.IsPlaying||Visual.PropVisible||Motion.IsInteractionMotionActive,Is.False,reason);
                Assert.That(world.WorldRevision,Is.EqualTo(revision));Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.NeedsCleaning));
                Assert.That(feedback.LastResultText,Does.Not.Contain("已清洁"));Station.enabled=true;runner.enabled=true;
                yield return ReleaseDialogue();float quiet=Time.unscaledTime+1.2f;while(Time.unscaledTime<quiet)yield return null;
            }
            // Authored point becomes unreachable; no broad fallback to the counter's collider.
            Station.ApproachPoint.position+=Vector3.right*100;
            Assert.That(driver.TryConsumeResponse(Propose()),Is.False);Assert.That(runner.IsExecuting,Is.False);
            Assert.That(world.WorldRevision,Is.EqualTo(revision));
        }
        private CounterCleaningCognitionSource Source => merchant.GetComponent<CounterCleaningCognitionSource>();
        private CounterFakeBackend fake;
        private AgentOrchestrator Runs => merchant.GetComponent<AgentOrchestrator>();
        private IEnumerator PrepareAutonomy()
        {
            var playerBody=player.GetComponent<CharacterController>();playerBody.enabled=false;
            player.transform.position=GameObject.Find("CounterFrontPoint").transform.position;playerBody.enabled=true;Physics.SyncTransforms();
            var root=new GameObject("Counter_InMemoryTransport");root.SetActive(false);fake=root.AddComponent<CounterFakeBackend>();
            var fields=new SerializedObject(fake);fields.FindProperty("connectOnStart").boolValue=false;
            fields.FindProperty("serverUrl").stringValue="ws://127.0.0.1:0/ws";fields.ApplyModifiedPropertiesWithoutUndo();root.SetActive(true);
            var presenter=(MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour",Private).GetValue(driver);
            driver.Configure(merchant,Runs,merchant.GetComponent<CognitiveContextComposer>(),fake,presenter);driver.enabled=true;
            var ui=new SerializedObject(dialogue);ui.FindProperty("backendClient").objectReferenceValue=fake;ui.ApplyModifiedPropertiesWithoutUndo();
            var handler=merchant.GetComponent<CognitiveDialogueSubmissionHandler>();
            fields=new SerializedObject(handler);fields.FindProperty("backendClient").objectReferenceValue=fake;fields.ApplyModifiedPropertiesWithoutUndo();
            float until=Time.realtimeSinceStartup+20;var routine=merchant.GetComponent<ReturnToHomeRoutine>();
            while((routine.IsRunning||routine.OwnsPath||Motion.IsNavigationActive) && Time.realtimeSinceStartup<until)yield return null;
            merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            float quiet=Time.unscaledTime+1.2f;while(Time.unscaledTime<quiet)yield return null;
            Source.enabled=true;AssertOffline();
        }
        private CognitiveResponse SystemResponse(CognitiveRequest r, bool action=true) => new CognitiveResponse {
            message_type="cognitive_response",schema_version="1.0",request_id=r.request_id,trace_id=r.trace_id,run_id=r.run.run_id,agent_entity_id=merchant.EntityId,
            decision=new CognitiveDecision { decision_kind=action?CognitiveDecisionKinds.AutonomousActionIntent:CognitiveDecisionKinds.NoAction,
                action_proposal=action?new CognitiveActionProposal {action_id="clean_counter",bindings=new Dictionary<string,string>{{"target",Station.Entity.EntityId}}}:null },
            validated_goal=action?new GoalProposal {type="clean_counter",target_entity_id=Station.Entity.EntityId}:null };
        [UnityTest] public IEnumerator Autonomous_TrustedEventSharedRunnerSilentCompletion()
        {
            yield return PrepareAutonomy();
            var bubbles=Object.FindAnyObjectByType<DialogueBubbleManager>();int before=bubbles.GetMessages(merchant.EntityId).Count,presented=0;
            driver.UtterancePresented+=_=>presented++;GoalOutcome? outcome=null;runner.GoalOutcomePublished+=o=>outcome=o;
            Dirty();Assert.That(Source.PublishedCount,Is.EqualTo(1));Assert.That(fake.Requests.Count,Is.EqualTo(1),Source.LastStatus);
            var request=fake.Requests.Single();Assert.That(request.context.relevant_player,Is.Null);Assert.That(Runs.CurrentRun.IsCounterCleaningConsideration,Is.True);
            Assert.That(request.context.current_event.payload.Any(p=>p.key=="text"||p.key=="conversation_id"),Is.False);
            Assert.That(fake.Reply(SystemResponse(request)),Is.True);yield return null;
            Assert.That(runner.IsExecuting,Is.True,driver.LastActionHandoffResult);
            float until=Time.realtimeSinceStartup+28;while(!Visual.IsPlaying && !outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(Visual.IsPlaying&&Visual.PropVisible,Is.True);Assert.That(runner.CurrentTaskFeedback.Player,Is.Empty);
            var playerBody=player.GetComponent<CharacterController>();playerBody.enabled=false;player.transform.position=new Vector3(-.6f,.03f,1.5f);playerBody.enabled=true;
            Camera.main.transform.position=player.transform.position+new Vector3(-.7f,1.75f,-.65f);Camera.main.transform.LookAt(Station.ApproachPoint.position+Vector3.up*1.2f);
            float poseAt=Time.time+.85f;while(Time.time<poseAt)yield return null;
            Assert.That(Visual.IsPlaying&&Visual.PropVisible,Is.True);
            yield return Capture("03-autonomous-wiping");
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()),Is.True);
            Assert.That(Runs.CurrentRun.State,Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(driver.CancelLowPrioritySystemConsideration("must_not_cancel_formal"),Is.False);Assert.That(runner.IsExecuting,Is.True);
            Assert.That(merchant.GetComponent<CognitiveDialogueSubmissionHandler>().Submit(new UserUtterance {conversationId="busy",speakerEntityId=player.EntityId,targetAgentId=merchant.EntityId,inputMode="text",text="你好"}).Accepted,Is.False);
            while(!outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
            Assert.That(world.WorldRevision,Is.EqualTo(2));Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
            Assert.That(presented,Is.Zero);Assert.That(bubbles.GetMessages(merchant.EntityId).Count,Is.EqualTo(before));
            var drafts=new List<ExperienceDraftDto>();
            foreach(var transport in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                var coordinator=typeof(BackendWebSocketClient).GetField("experienceWrites",Private).GetValue(transport);
                if(coordinator==null)continue;
                var pending=(System.Collections.IEnumerable)coordinator.GetType().GetField("pending",Private).GetValue(coordinator);
                foreach(var entry in pending)
                    drafts.Add(((ExperienceAppendRequest)entry.GetType().GetProperty("Request").GetValue(entry)).draft);
            }
            Assert.That(drafts.Any(d=>d.source_type=="dialogue"),Is.False,"Autonomy must not manufacture DialogueExperience.");
            Assert.That(drafts.Any(d=>d.source_type=="skill_result"&&d.event_type=="skill_succeeded"),Is.True,"The existing evidence path still observes the real skill.");

            Assert.That(feedback.PendingNotificationCount,Is.Zero);Assert.That(feedback.LastResultText,Is.Empty);
            Assert.That(Visual.IsPlaying||Visual.PropVisible,Is.False);Assert.That(Runs.HasActiveRun,Is.False);Assert.That(feedback.ActiveTaskCount,Is.Zero);
            dialogue.ClearSession();yield return ReleaseDialogue();
            float quiet=Time.unscaledTime+2.1f;while(Time.unscaledTime<quiet)yield return null;
            Assert.That(merchant.GetComponent<DailyBehaviorController>().CurrentMode,Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(merchant.GetComponent<DailyBehaviorController>().CurrentActivity.HasValue,Is.True);
        }
        [UnityTest] public IEnumerator Autonomous_NoActionOneOpportunityAndForgedEventRejected()
        {
            yield return PrepareAutonomy();Dirty();Assert.That(fake.Requests.Count,Is.EqualTo(1),Source.LastStatus);
            var e=Source.CurrentEvent;var r=fake.Requests.Single();Assert.That(fake.Reply(SystemResponse(r,false)),Is.True);yield return null;
            Assert.That(Runs.HasActiveRun||runner.IsExecuting,Is.False);Assert.That(world.WorldRevision,Is.EqualTo(1));
            Assert.That(world.TryMarkCounterNeedsCleaning(Registry,Station,"repeat"),Is.False);
            merchant.GetComponent<AgentEventReceiver>().Receive(e);
            var forged=new AgentEvent("forged-counter",e.EventType,e.SourceEntityId,e.TargetEntityId,e.AreaEntityId,
                new Dictionary<string,string>{{"target_entity_id",Station.Entity.EntityId},{"condition_token",r.context.counter_work.condition_token},{"condition_revision","1"}},
                Time.realtimeSinceStartupAsDouble,world.WorldRevision,traceId:e.TraceId);
            merchant.GetComponent<AgentEventReceiver>().Receive(forged);
            Assert.That(Runs.HasActiveRun,Is.False);Assert.That(fake.Requests.Count,Is.EqualTo(1));
            foreach(var npc in Object.FindObjectsByType<WorldEntity>().Where(x=>x.IsAgent(AgentRole.Npc)&&x!=merchant))
            {
                var endpoint=npc.GetComponent<AgentEventReceiver>();
                Assert.That(EventInterestEvaluator.Evaluate(forged,endpoint.EntityId,endpoint.InterestProfile,"working",endpoint.ModeInterestOverrides,"work"),Is.EqualTo(EventInterestDecision.Ignore));
                Assert.That(npc.GetComponent<AgentActionCapabilityProfile>().AllowedActionIds,Is.Empty);
            }
            float quiet=Time.unscaledTime+2;while(Time.unscaledTime<quiet)yield return null;
            Assert.That(fake.Requests.Count,Is.EqualTo(1));Assert.That(feedback.PendingNotificationCount,Is.Zero);
        }
        [UnityTest] public IEnumerator Autonomous_PlayerPreemptsBufferedAndBlockedSend_LateResultsCannotOwnPlayerRun()
        {
            yield return PrepareAutonomy();fake.BlockCounterSend=new TaskCompletionSource<bool>();Dirty();
            Assert.That(fake.Requests.Count,Is.EqualTo(1),Source.LastStatus);var old=fake.Requests.Single();
            var handler=merchant.GetComponent<CognitiveDialogueSubmissionHandler>();
            Assert.That(handler.Submit(new UserUtterance {conversationId="invalid",speakerEntityId="cup_01",targetAgentId=merchant.EntityId,inputMode="text",text="你好"}).Accepted,Is.False);
            Assert.That(Runs.CurrentRun.RunId,Is.EqualTo(old.run.run_id));
            fake.Reply(SystemResponse(old));Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()),Is.True);
            Assert.That(Runs.HasActiveRun,Is.False);Assert.That(runner.IsExecuting,Is.False);
            Assert.That(Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.Input.TextUtteranceInput>().SubmitText("你好，薇薇。"),Is.True);
            var current=fake.Requests.Last();Assert.That(current.run.run_id,Is.Not.EqualTo(old.run.run_id));
            fake.BlockCounterSend.SetResult(true);yield return null;
            Assert.That(fake.Cancels.Count(x=>x.request_id==old.request_id),Is.GreaterThanOrEqualTo(2));
            Assert.That(driver.TryConsumeResponse(SystemResponse(old)),Is.False);
            Assert.That(driver.TryConsumeFailure(new CognitiveTransportFailure(old.request_id,old.run.run_id,merchant.EntityId,"old_error",old.trace_id)),Is.False);
            var timer=(IEnumerator)typeof(CognitiveRunDriver).GetMethod("FailAfterTimeout",Private).Invoke(driver,new object[]{old});
            Assert.That(timer.MoveNext(),Is.True);Assert.That(timer.MoveNext(),Is.False);
            Assert.That(Runs.CurrentRun.RunId,Is.EqualTo(current.run.run_id));Assert.That(runner.IsExecuting,Is.False);
            Assert.That(driver.CancelLowPrioritySystemConsideration("must_not_cancel_player"),Is.False);
            var reply=SystemResponse(current,false);reply.decision.decision_kind=CognitiveDecisionKinds.DirectResponse;reply.decision.utterance="你好。";
            Assert.That(fake.Reply(reply),Is.True);yield return null;Assert.That(Runs.HasActiveRun,Is.False);
            Assert.That(world.WorldRevision,Is.EqualTo(1));Assert.That(fake.Requests.Count,Is.EqualTo(2));
        }
        [UnityTest] public IEnumerator Autonomous_RestAndChangedConditionCannotStart()
        {
            yield return PrepareAutonomy();yield return RestAtWindow();Dirty();
            Assert.That(fake.Requests,Is.Empty);Assert.That(Runs.HasActiveRun,Is.False);
            // An explicit player request may still combine Rest -> Work with the same formal skill.
            Source.enabled=false;Assert.That(driver.TryConsumeResponse(Propose(true)),Is.True);
            float until=Time.realtimeSinceStartup+28;while(runner.IsExecuting && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
            yield return ReleaseDialogue();
            until=Time.realtimeSinceStartup+20;
            while(merchant.GetComponent<NpcDefaultRoutineController>().IsDailyActivityBlocked || Motion.IsNavigationActive ||
                merchant.GetComponent<ReturnToHomeRoutine>().IsRunning || merchant.GetComponent<ReturnToHomeRoutine>().OwnsPath)
            { Assert.That(Time.realtimeSinceStartup,Is.LessThan(until));yield return null; }
            Source.enabled=true;Dirty();Assert.That(fake.Requests.Count,Is.EqualTo(1),Source.LastStatus);var old=fake.Requests.Last();Assert.That(old.context.current_event.event_type,Is.EqualTo(AgentEventTypes.CounterNeedsCleaning));
            // A separate successful authoritative transaction invalidates this condition before the model returns.
            var resources=Object.FindAnyObjectByType<ResourceReservationService>();
            Assert.That(resources.TryAcquire(Station.Entity.EntityId,merchant.EntityId,"other-trace",out var otherLease),Is.True);
            typeof(WorldStateService).GetMethod("TryCommitCounterCleaning",Private).Invoke(world,
                new object[]{"other-authoritative-clean", "other-trace", Registry,merchant,Station.Entity,world.GetCounterConditionRevision(Station.Entity.EntityId),resources,otherLease.ReservationId});
            resources.Release(otherLease.ReservationId,merchant.EntityId);
            Assert.That(world.GetCounterState(Station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));int revision=world.WorldRevision;
            fake.Reply(SystemResponse(old));yield return null;
            Assert.That(runner.IsExecuting||Runs.HasActiveRun,Is.False);Assert.That(world.WorldRevision,Is.EqualTo(revision));
            Assert.That(Visual.IsPlaying||Visual.PropVisible,Is.False);
        }
        [UnityTest] public IEnumerator Autonomous_FinalCapabilityProfileAndLeaseChangesReject()
        {
            yield return PrepareAutonomy();Dirty();var request=fake.Requests.Single();var run=Runs.CurrentRun;
            var cap=merchant.GetComponent<CounterCleaningCapability>();cap.enabled=false;Assert.That(Source.Matches(request,run),Is.False);cap.enabled=true;
            Vector3 point=Station.ApproachPoint.position;Station.ApproachPoint.position+=Vector3.right*100;
            Assert.That(Source.Matches(request,run),Is.False);Station.ApproachPoint.position=point;
            Assert.That(Source.Matches(request,run),Is.True);
            var expiry=typeof(CounterCleaningCognitionSource).GetField("expiresAt",Private);var originalExpiry=expiry.GetValue(Source);
            expiry.SetValue(Source,-1d);Assert.That(Source.Matches(request,run),Is.False);expiry.SetValue(Source,originalExpiry);
            var daily=merchant.GetComponent<DailyBehaviorController>();var profile=daily.Profile;
            var copy=Object.Instantiate(profile);var fields=new SerializedObject(daily);fields.FindProperty("profile").objectReferenceValue=copy;fields.ApplyModifiedPropertiesWithoutUndo();
            // Even an equal-valued replacement increments the authoritative profile revision.
            Assert.That(Source.Matches(request,run),Is.False);
            fields.FindProperty("profile").objectReferenceValue=profile;fields.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(Source.Matches(request,run),Is.False);Object.Destroy(copy);
            Assert.That(fake.Reply(SystemResponse(request)),Is.True);Assert.That(driver.FlushServiceModeResponse(),Is.False);
            Assert.That(runner.IsExecuting||Runs.HasActiveRun,Is.False);Assert.That(world.WorldRevision,Is.EqualTo(1));
            Assert.That(feedback.PendingNotificationCount,Is.Zero);Assert.That(fake.Requests.Count,Is.EqualTo(1));
        }
    }
}
