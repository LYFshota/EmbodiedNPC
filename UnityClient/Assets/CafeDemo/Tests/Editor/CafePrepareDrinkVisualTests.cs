using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
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
    /// <summary>Authored V2, physical navigation/transactions, scripted decisions, all network lifecycles disabled before Play.</summary>
    public sealed class CafePrepareDrinkVisualTests
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
                    component is ServiceZoneDwellCognitionSource) component.enabled = false;
            foreach (var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>(FindObjectsInactive.Include))
                trigger.ConfigureDemo(false, 15, 35);
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("prepare-drink-visual-offline", "测试玩家", 1, DateTimeOffset.UnixEpoch);
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
            if(TestContext.CurrentContext.Test.MethodName.StartsWith("P3E_"))
                EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        }
        private static void AssertOffline()
        {
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                Assert.That(network.isActiveAndEnabled, Is.False);
                Assert.That(network.ConnectionAttempts, Is.Zero, "No real connection attempt is allowed.");
                Assert.That(network.IsConnected, Is.False);
                Assert.That(new SerializedObject(network).FindProperty("serverUrl").stringValue, Is.EqualTo("ws://127.0.0.1:0/ws"));
            }
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component is ModelConfigClient || component is FirstRunModelSetup || component is CafeDemoDeveloperPanel || component is DialogueHistoryPanel)
                    Assert.That(component.isActiveAndEnabled, Is.False);
        }
        private DrinkPreparationStation Station => merchant.GetComponent<DrinkPreparationCapability>().Station;
        private MotionChannelCoordinator Motion => merchant.GetComponent<MotionChannelCoordinator>();
        private CognitiveResponse Propose(string drink, bool combined = false)
        {
            // The fixture supplies the response in memory; prevent the live driver send callback for this request.
            driver.enabled = false;
            merchant.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("drink-" + Guid.NewGuid().ToString("N"),
                AgentEventTypes.PlayerUtterance, player.EntityId, merchant.EntityId, "",
                new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "请帮我做一杯" + DrinkTypes.Name(drink) },
                Time.realtimeSinceStartupAsDouble, world.WorldRevision));
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request, Is.Not.Null);
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            return new CognitiveResponse { message_type="cognitive_response", schema_version="1.0",
                request_id=request.request_id, trace_id=request.trace_id, run_id=request.run.run_id, agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision { decision_kind=CognitiveDecisionKinds.ActionProposal,
                    target_daily_mode=combined ? "work" : null, utterance="好呀，我去准备" + DrinkTypes.Name(drink) + "，稍等一下。",
                    action_proposal=new CognitiveActionProposal { action_id="prepare_drink",
                        bindings=new Dictionary<string,string> { ["drink_type"]=drink } } },
                validated_goal=new GoalProposal { type="prepare_drink", drink_type=drink } };
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
        private DrinkPreparationPresenter Visual => merchant.GetComponent<DrinkPreparationPresenter>();
        private static string Evidence => Path.Combine(Path.GetTempPath(),"embodiednpc-prepare-drink-p1");
        private IEnumerator Capture(string name)
        {
            Directory.CreateDirectory(Evidence);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,name+".png"));
            for(int i=0;i<3;i++) yield return null;
        }
        [UnityTest] public IEnumerator AllDrinks_AnimateHumanoidHands_FromPlayerCounterView_ThenClear()
        {
            // A legal player position at the front of the bar. No NPC or authoritative item is repositioned.
            var control=player.GetComponent<CharacterController>();control.enabled=false;
            player.transform.position=GameObject.Find("CounterFrontPoint").transform.position;control.enabled=true;
            player.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(merchant.transform.position-player.transform.position,Vector3.up));
            Camera.main.transform.position=player.transform.position+new Vector3(-.7f,1.65f,-.65f);
            Camera.main.transform.LookAt(merchant.transform.position+Vector3.up*1.2f);
            Camera.main.fieldOfView=55;
            Physics.SyncTransforms();
            int previous=world.WorldRevision;
            var hand=Character.GetBoneTransform(HumanBodyBones.RightHand);
            GoalOutcome? outcome=null;runner.GoalOutcomePublished+=o=>outcome=o;
            foreach(var drink in new[]{"latte","cappuccino","americano"})
            {
                outcome=null; Assert.That(Visual,Is.Not.Null);
                Vector3 idle=merchant.transform.InverseTransformPoint(hand.position);
                if(drink=="latte") yield return Capture("00-idle");
                Assert.That(driver.TryConsumeResponse(Propose(drink)),Is.True,driver.LastActionHandoffResult);
                float until=Time.realtimeSinceStartup+30;
                while(!Visual.IsPlaying && Time.realtimeSinceStartup<until && !outcome.HasValue) yield return null;
                Assert.That(Visual.IsPlaying,Is.True);
                Assert.That(Motion.ActiveBodyChannel,Is.EqualTo(MotionChannel.Interaction));
                Assert.That(Visual.SteamActive,Is.True);
                Assert.That(Visual.PropVisible,Is.True);
                Assert.That(Visual.GetComponentInChildren<Animator>(),Is.Not.Null);
                Assert.That(Vector3.Distance(merchant.transform.position,Station.ApproachPoint.position),Is.LessThan(.85f));
                float started=Time.time;
                Vector3 first=Vector3.zero,last=Vector3.zero;bool sampled=false;float maxFromIdle=0,excursion=0,forwardReach=0;
                bool firstCapture=false,secondCapture=false;
                while(!outcome.HasValue && Time.realtimeSinceStartup<until)
                {
                    Vector3 position=merchant.transform.InverseTransformPoint(hand.position);
                    maxFromIdle=Mathf.Max(maxFromIdle,Vector3.Distance(position,idle));
                    forwardReach=Mathf.Max(forwardReach,position.z);
                    if(Time.time-started>.55f && Time.time-started<1.9f)
                    {
                        if(!sampled){first=position;sampled=true;}
                        excursion=Mathf.Max(excursion,Vector3.Distance(first,position));last=position;
                    }
                    if(drink=="latte" && Time.time-started>.65f && !firstCapture)
                    { firstCapture=true;yield return Capture("01-prepare-reach"); }
                    if(drink=="latte" && Time.time-started>1.35f && !secondCapture)
                    { secondCapture=true;yield return Capture("02-prepare-operate"); }
                    yield return null;
                }
                Assert.That(outcome.HasValue,Is.True);
                Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
                Debug.Log("[DrinkVisual] "+drink+" hand displacement="+maxFromIdle+"; operation excursion="+excursion+"; first="+first+"; last="+last);
                Assert.That(maxFromIdle,Is.GreaterThan(.12f),"The actual Humanoid hand must move beyond idle sway.");
                Assert.That(excursion,Is.GreaterThan(.025f),"The operating interval must contain real hand motion.");
                Assert.That(forwardReach,Is.GreaterThan(.22f),"Hands must reach forward toward the work area.");
                yield return null;
                Assert.That(Visual.IsPlaying || Visual.PropVisible,Is.False);Assert.That(Visual.CurrentWeight,Is.Zero);Assert.That(Visual.SteamActive,Is.False);
                Assert.That(Character.GetLayerWeight(Character.GetLayerIndex(DrinkPreparationPresenter.LayerName)),Is.Zero);
                Assert.That(world.GetDrinkCount(player.EntityId,drink),Is.EqualTo(1));Assert.That(world.WorldRevision,Is.EqualTo(++previous));
                Assert.That(feedback.LastResultText,Is.EqualTo(DrinkTypes.Name(drink)+" +1"));
                yield return ReleaseDialogue();
            }
            yield return Capture("03-complete-idle");
            AssertOffline();
        }
        [UnityTest] public IEnumerator P3E_OneDrinkSlowerCompleteClipAndVisibleStatus()
        {
            string evidence=@"D:\EmbodiedNPC-UXP3E-20261018\evidence";Directory.CreateDirectory(evidence);
            typeof(CharacterOverrideStore).GetField("shared",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
                new CharacterOverrideStore(Path.Combine(evidence,"empty-action-persona.json")));
            var control=player.GetComponent<CharacterController>();control.enabled=false;
            player.transform.position=GameObject.Find("CounterFrontPoint").transform.position;control.enabled=true;
            Camera.main.transform.position=player.transform.position+new Vector3(-.7f,1.65f,-.65f);
            Camera.main.transform.LookAt(merchant.transform.position+Vector3.up*1.2f);Camera.main.fieldOfView=55;Physics.SyncTransforms();
            GoalOutcome? outcome=null;runner.GoalOutcomePublished+=value=>outcome=value;
            Assert.IsTrue(driver.TryConsumeResponse(Propose("latte")),driver.LastActionHandoffResult);
            float until=Time.realtimeSinceStartup+30;
            while(!Visual.IsPlaying && !outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
            Assert.IsTrue(Visual.IsPlaying);float started=Time.time;
            Assert.That(Character.GetCurrentAnimatorStateInfo(Character.GetLayerIndex(DrinkPreparationPresenter.LayerName)).speed,Is.EqualTo(.83f).Within(.001));
            yield return null;yield return null;
            Assert.Greater(feedback.VisibleLabelCount,0,"Front-of-counter status must be visible");
            ScreenCapture.CaptureScreenshot(Path.Combine(evidence,"drink-status-front.png"));yield return null;
            while(!outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
            Assert.IsTrue(outcome.HasValue);Assert.AreEqual(PlanExecutionStatus.Succeeded,outcome.Value.Status);
            Assert.GreaterOrEqual(Time.time-started,PrepareDrinkActionRunner.PreparationSeconds-.15f);
            yield return null;Assert.IsFalse(Visual.IsPlaying || Visual.SteamActive || Visual.PropVisible);
            Assert.AreEqual(1,world.GetDrinkCount(player.EntityId,"latte"));Assert.AreEqual(0,feedback.ActiveTaskCount);
            AssertOffline();
        }
        [UnityTest] public IEnumerator TerminalFailures_ClearArmsAndSteam_WithoutDrinkOrRevision()
        {
            var station=Station; int revision=world.WorldRevision; GoalOutcome? outcome=null;
            runner.GoalOutcomePublished+=o=>outcome=o;
            foreach(var reason in new[]{"cancel","station","actor","timeout","runner"})
            {
                outcome=null;Assert.That(driver.TryConsumeResponse(Propose("latte")),Is.True,driver.LastActionHandoffResult);
                float until=Time.realtimeSinceStartup+28;
                while(!Visual.IsPlaying && !outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
                Assert.That(Visual.IsPlaying,Is.True);
                if(reason=="cancel")runner.CancelCurrentPlan();
                if(reason=="station")station.enabled=false;
                if(reason=="actor")merchant.enabled=false;
                if(reason=="runner")runner.enabled=false;
                if(reason=="timeout")
                {
                    var executor=typeof(GoalPlanRunner).GetField("executor",Private).GetValue(runner);
                    typeof(PlanExecutor).GetField("retryBudget",Private).SetValue(executor,new RetryBudget(Time.unscaledTime-31));
                }
                until=Time.realtimeSinceStartup+5;while(!outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
                Assert.That(outcome.HasValue,Is.True);
                Assert.That(outcome.Value.Status,Is.EqualTo(reason=="timeout"?PlanExecutionStatus.TimedOut:
                    reason=="cancel"||reason=="runner"?PlanExecutionStatus.Cancelled:PlanExecutionStatus.Failed));
                Assert.That(Visual.IsPlaying || Visual.SteamActive || Visual.PropVisible,Is.False,reason);
                Assert.That(Visual.CurrentWeight,Is.Zero);Assert.That(Motion.IsInteractionMotionActive,Is.False);
                Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);Assert.That(world.WorldRevision,Is.EqualTo(revision));
                Assert.That(feedback.LastResultText,Does.Not.Contain("+1"));
                station.enabled=true;merchant.enabled=true;runner.enabled=true;
                yield return ReleaseDialogue();float quiet=Time.unscaledTime+1.2f;while(Time.unscaledTime<quiet)yield return null;
            }
            AssertOffline();
        }
        [UnityTest] public IEnumerator PresenterDisable_ClearsImmediately_WithoutChangingFormalCompletion()
        {
            GoalOutcome? outcome=null;runner.GoalOutcomePublished+=o=>outcome=o;
            Assert.That(driver.TryConsumeResponse(Propose("americano")),Is.True);
            float until=Time.realtimeSinceStartup+28;
            while(!Visual.IsPlaying && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(Visual.IsPlaying && Visual.SteamActive,Is.True);Visual.enabled=false;
            Assert.That(Visual.IsPlaying || Visual.SteamActive || Visual.PropVisible,Is.False);Assert.That(Visual.CurrentWeight,Is.Zero);
            Assert.That(world.GetDrinkCount(player.EntityId,"americano"),Is.Zero);
            while(!outcome.HasValue && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(world.GetDrinkCount(player.EntityId,"americano"),Is.EqualTo(1));
            Visual.enabled=true;yield return null;Assert.That(Visual.IsPlaying || Visual.SteamActive,Is.False);
        }
        [UnityTest] public IEnumerator CarryTalkingAndNavigation_KeepOriginalPriorityAndRecover()
        {
            var speech=merchant.GetComponent<SpeechPresentationLifecycle>();
            var talking=merchant.GetComponentInChildren<DialogueAnimationPresenter>();
            speech.BeginPlayback("离线表现验证");yield return null;talking.Tick(.3f);
            Assert.That(talking.CurrentWeight,Is.EqualTo(1));Assert.That(Visual.IsPlaying,Is.False);
            GoalOutcome? outcome=null;runner.GoalOutcomePublished+=o=>outcome=o;
            Assert.That(driver.TryConsumeResponse(Propose("latte")),Is.True);
            float until=Time.realtimeSinceStartup+28;
            while(!Visual.IsPlaying && Time.realtimeSinceStartup<until)yield return null;
            Assert.That(Visual.IsPlaying,Is.True);talking.Tick(.3f);Assert.That(talking.CurrentWeight,Is.Zero);
            Assert.That(Motion.TryBeginGesture("wave",false,Time.unscaledTime,3,out _),Is.False);
            // Exercise existing projection arbitration without changing item ownership or task authority.
            Motion.SetCarryPresentationActive(true);Visual.Tick(.1f);
            Assert.That(Visual.IsPlaying || Visual.SteamActive,Is.False);Motion.SetCarryPresentationActive(false);
            Visual.Tick(.1f);Assert.That(Visual.IsPlaying,Is.True);
            Motion.SetNavigationActive(true);Visual.Tick(.1f);Assert.That(Visual.IsPlaying,Is.False);
            Motion.SetNavigationActive(false);Visual.Tick(.1f);Assert.That(Visual.IsPlaying,Is.True);
            runner.CancelCurrentPlan();yield return null;Assert.That(Visual.CurrentWeight,Is.Zero);
            yield return ReleaseDialogue();speech.BeginPlayback("制作表现结束后正常交谈");talking.Tick(.3f);
            Assert.That(talking.CurrentWeight,Is.EqualTo(1));Assert.That(Visual.IsPlaying,Is.False);
            Assert.That(world.WorldRevision,Is.Zero);AssertOffline();
        }
    }
}
