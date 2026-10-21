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
    public sealed class CafePrepareDrinkTests
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
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("prepare-drink-offline", "测试玩家", 1, DateTimeOffset.UnixEpoch);
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
        [UnityTest] public IEnumerator CombinedRestWork_RealNavigationWaitReceiptUiBubbleAndDaily() => Recipe(true);
        [UnityTest] public IEnumerator RestWithoutModeChange_ReturnsToDailyWindowAfterRealPreparation() => Recipe(false);
        private IEnumerator Recipe(bool combined)
        {
            yield return RestAtWindow();
            Vector3 start=merchant.transform.position;
            Assert.That(Vector3.Distance(start,Station.ApproachPoint.position),Is.GreaterThan(1));
            GoalOutcome? outcome=null; runner.GoalOutcomePublished += value=>outcome=value;
            int before=world.WorldRevision, starts=0, utterances=0;
            runner.TaskStarted += _=>starts++; driver.UtterancePresented += _=>utterances++;
            Assert.That(driver.TryConsumeResponse(Propose("latte",combined)),Is.True,
                driver.LastActionHandoffResult+"/"+driver.LastDailyModeResult);
            Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);
            float until=Time.realtimeSinceStartup+30; bool walking=false, preparing=false;
            double preparedAt=0;
            while(!outcome.HasValue && Time.realtimeSinceStartup<until)
            {
                if(runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.WalkTo) walking=true;
                if(Motion.IsInteractionMotionActive && !preparing)
                {
                    preparing=true; preparedAt=Time.unscaledTimeAsDouble;
                    Assert.That(Vector3.Distance(merchant.transform.position,Station.ApproachPoint.position),Is.LessThan(.85));
                    Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);
                    Assert.That(feedback.PhaseText(merchant.EntityId),Does.Contain("拿铁"));
                }
                yield return null;
            }
            Assert.That(outcome.HasValue,Is.True);
            Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
            Assert.That(walking && preparing,Is.True);
            Assert.That(Time.unscaledTimeAsDouble-preparedAt,Is.GreaterThanOrEqualTo(2.4));
            Assert.That(Vector3.Distance(start,merchant.transform.position),Is.GreaterThan(1));
            Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.EqualTo(1));
            Assert.That(world.WorldRevision,Is.EqualTo(before+1));
            Assert.That(world.TryGetDrinkReceipt(merchant.EntityId,outcome.Value.ProposalId,outcome.Value.TraceId,out var receipt),Is.True);
            Assert.That(receipt.WorldRevision,Is.EqualTo(world.WorldRevision));
            Assert.That(starts,Is.EqualTo(1)); Assert.That(Motion.IsInteractionMotionActive,Is.False);
            yield return null;
            Assert.That(feedback.LastResultText,Is.EqualTo("拿铁 +1"));
            Assert.That(feedback.NotificationsShown,Is.EqualTo(1));
            var messages=Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId);
            Assert.That(messages.Count(x=>x.Text=="拿铁做好了，请享用。"),Is.EqualTo(1));
            Assert.That(utterances,Is.EqualTo(1));
            typeof(GoalPlanRunner).GetMethod("PublishGoalOutcome",Private).Invoke(runner,new object[]{(GoalOutcome?)outcome.Value});
            feedback.enabled=false;feedback.enabled=true;yield return null;
            Assert.That(feedback.NotificationsShown,Is.EqualTo(1));
            yield return ReleaseDialogue();
            var daily=merchant.GetComponent<DailyBehaviorController>(); var routine=merchant.GetComponent<ReturnToHomeRoutine>();
            until=Time.realtimeSinceStartup+25; bool resumedNavigation=false;
            while((daily.IsActivitySuspended || !routine.IsAtDailyDestination) && Time.realtimeSinceStartup<until)
            { resumedNavigation |= routine.OwnsPath; yield return null; }
            Assert.That(daily.IsFormalTaskOccupied,Is.False);Assert.That(daily.IsActivitySuspended,Is.False);
            Assert.That(routine.IsAtDailyDestination,Is.True);
            if(!combined) Assert.That(resumedNavigation,Is.True,"Rest must actually navigate back after task release.");
            AssertOffline();
        }
        [UnityTest] public IEnumerator Work_ThreeIndependentDrinksAndRevisionReplayAreIdempotent()
        {
            int revision=world.WorldRevision; GoalOutcome? outcome=null;
            runner.GoalOutcomePublished += v=>outcome=v;
            foreach(string drink in new[]{"latte","cappuccino","americano","americano"})
            {
                int expected=world.GetDrinkCount(player.EntityId,drink)+1;
                outcome=null;
                Assert.That(driver.TryConsumeResponse(Propose(drink)),Is.True,driver.LastActionHandoffResult);
                float until=Time.realtimeSinceStartup+32;
                while(!outcome.HasValue && Time.realtimeSinceStartup<until) yield return null;
                Assert.That(outcome.HasValue,Is.True);
                Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
                Assert.That(world.GetDrinkCount(player.EntityId,drink),Is.EqualTo(expected));
                Assert.That(world.WorldRevision,Is.EqualTo(++revision));
                yield return null;Assert.That(feedback.LastResultText,Is.EqualTo(DrinkTypes.Name(drink)+" +1"));
                yield return ReleaseDialogue();
            }
            var original=outcome.Value;
            var commit=typeof(WorldStateService).GetMethod("TryCommitDrink",Private);
            var args=new object[]{original.ProposalId,original.TraceId,Object.FindAnyObjectByType<EntityRegistry>(),merchant,
                Station.Entity,player,"americano"};
            for(int i=0;i<3;i++)
            { var result=(SkillResult)commit.Invoke(world,args);Assert.That(result.FailureCode,Is.EqualTo("DRINK_ALREADY_COMMITTED")); }
            Assert.That(world.WorldRevision,Is.EqualTo(revision));
            Assert.That(world.GetDrinkCount(player.EntityId,"americano"),Is.EqualTo(2));
            args[6]="latte"; Assert.That(((SkillResult)commit.Invoke(world,args)).FailureCode,Is.EqualTo("DRINK_ORDER_CONFLICT"));
            // Same original actor/proposal/trace, later revision, full actual plan again: no second cup or +1.
            outcome=null;
            Assert.That(runner.TrySubmitGoal(new GoalProposal{type="prepare_drink",drink_type="americano"},original.TraceId,
                original.ProposalId,1,world.WorldRevision,player.EntityId),Is.True);
            float deadline=Time.realtimeSinceStartup+32;
            while(!outcome.HasValue && Time.realtimeSinceStartup<deadline) yield return null;
            Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
            yield return null;
            Assert.That(world.GetDrinkCount(player.EntityId,"americano"),Is.EqualTo(2));
            Assert.That(world.WorldRevision,Is.EqualTo(revision));Assert.That(feedback.NotificationsShown,Is.EqualTo(4));
        }
        [UnityTest] public IEnumerator CancelDuringPreparation_NoDrink() => Failure("cancel");
        [UnityTest] public IEnumerator InvalidParametersAndCustomerCapabilities_CannotStartTask()
        {
            Assert.That(merchant.GetComponent<AgentActionCapabilityProfile>().TryBuildManifest(out var manifest),Is.True);
            var action=manifest.available_actions.Single(x=>x.action_id=="prepare_drink");
            Assert.That(action.bindings.Single().kind,Is.EqualTo("enum"));
            Assert.That(action.bindings.Single().enum_values,Is.EqualTo(new[]{"latte","cappuccino","americano"}));
            foreach(var goal in new[]{new GoalProposal{type="prepare_drink"},new GoalProposal{type="prepare_drink",drink_type="mocha"},
                new GoalProposal{type="prepare_drink",drink_type="latte",destination_entity_id=player.EntityId},
                new GoalProposal{type="prepare_drink",drink_type="latte",target_entity_id="cup_01"}})
                Assert.That(runner.TrySubmitGoal(goal,"invalid_drink_trace",Guid.NewGuid().ToString("N"),0,world.WorldRevision,player.EntityId),Is.False);
            Assert.That(runner.TrySubmitGoal(new GoalProposal{type="prepare_drink",drink_type="latte"},"invalid_drink_trace",
                "no_requesting_player",0,world.WorldRevision),Is.False);
            var registry=Object.FindAnyObjectByType<EntityRegistry>();
            var reservations=Object.FindAnyObjectByType<ResourceReservationService>();
            BuiltInActionCatalog.Instance.TryGet("prepare_drink",out var registration);
            foreach(var customer in Object.FindObjectsByType<WorldEntity>().Where(x=>x.IsAgent(AgentRole.Npc) && x!=merchant))
            {
                Assert.That(customer.GetComponent<AgentActionCapabilityProfile>().AllowedActionIds,Is.Empty);
                var context=new ActionAdmissionContext(registry,reservations,world,customer,customer.GetComponent<UnityEngine.AI.NavMeshAgent>());
                Assert.That(registration.Binder.Bind(new GoalProposal{type="prepare_drink",drink_type="latte"},registration.Descriptor,context).IsBound,Is.False);
            }
            Assert.That(runner.HasActiveTask,Is.False);Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);
            Assert.That(feedback.NotificationsShown,Is.Zero);yield return null;
        }
        [UnityTest] public IEnumerator StationInvalidDuringPreparation_NoDrink() => Failure("station");
        [UnityTest] public IEnumerator ActorInvalidDuringPreparation_NoDrink() => Failure("actor");
        [UnityTest] public IEnumerator DeadlineDuringPreparation_CleansInteraction_NoDrink() => Failure("timeout");
        [UnityTest] public IEnumerator RunnerDisabledDuringPreparation_SettlesOnce_NoDrink() => Failure("disable");
        [UnityTest] public IEnumerator StationNavigationBecomesUnreachable_NoDrink() => Failure("path");
        private IEnumerator Failure(string reason)
        {
            int revision=world.WorldRevision,terminals=0;GoalOutcome? outcome=null;
            runner.GoalOutcomePublished+=v=>{outcome=v;terminals++;};
            Assert.That(driver.TryConsumeResponse(Propose("latte")),Is.True,driver.LastActionHandoffResult);
            if(reason=="path")
            {
                var unavailable=new GameObject("test_unreachable_station_approach");
                unavailable.transform.position=Station.ApproachPoint.position+Vector3.up*100;
                Station.Configure(unavailable.transform,Station.OperationPoint);
            }
            else
            {
                float ready=Time.realtimeSinceStartup+28;
                while(!Motion.IsInteractionMotionActive && Time.realtimeSinceStartup<ready && !outcome.HasValue) yield return null;
                Assert.That(Motion.IsInteractionMotionActive,Is.True);
                Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);
                if(reason=="cancel") runner.CancelCurrentPlan();
                if(reason=="station") Station.enabled=false;
                if(reason=="actor") merchant.enabled=false;
                if(reason=="disable") runner.enabled=false;
                if(reason=="timeout")
                {
                    var executor=typeof(GoalPlanRunner).GetField("executor",Private).GetValue(runner);
                    typeof(PlanExecutor).GetField("retryBudget",Private).SetValue(executor,new RetryBudget(Time.unscaledTime-31));
                }
            }
            float until=Time.realtimeSinceStartup+8;
            while(!outcome.HasValue && Time.realtimeSinceStartup<until) yield return null;
            Assert.That(outcome.HasValue,Is.True);
            Assert.That(outcome.Value.Status,Is.EqualTo(reason=="timeout" ? PlanExecutionStatus.TimedOut :
                reason=="cancel" || reason=="disable" ? PlanExecutionStatus.Cancelled : PlanExecutionStatus.Failed));
            yield return null;runner.CancelCurrentPlan();yield return null;
            Assert.That(terminals,Is.EqualTo(1));Assert.That(Motion.IsInteractionMotionActive,Is.False);
            Assert.That(runner.HasActiveTask,Is.False); Assert.That(feedback.ActiveTaskCount,Is.Zero);
            Assert.That(world.GetDrinkCount(player.EntityId,"latte"),Is.Zero);Assert.That(world.WorldRevision,Is.EqualTo(revision));
            Assert.That(feedback.LastResultText,Does.Not.Contain("+1"));Assert.That(feedback.NotificationsShown,Is.EqualTo(1));
            AssertOffline();
        }
    }
}
