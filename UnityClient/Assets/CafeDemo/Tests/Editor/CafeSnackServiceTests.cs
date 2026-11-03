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
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeSnackContractTests
    {
        [Test] public void CatalogHasTwoLocationsAndOnlyValidBindingsMap()
        {
            Assert.That(SnackCatalog.Ids,Is.EqualTo(new[]{"croissant","macaron","cupcake","pudding"}));
            Assert.That(SnackCatalog.Get("croissant").location,Is.EqualTo("showcase"));
            Assert.That(SnackCatalog.Get("pudding").location,Is.EqualTo("rack"));
            foreach(string snack in SnackCatalog.Ids) foreach(string quantity in SnackCatalog.QuantityIds)
            {
                var proposal=new CognitiveActionProposal { action_id="serve_snack",bindings=new Dictionary<string,string> { ["snack_type"]=snack,["quantity"]=quantity } };
                Assert.That(CognitiveActionProposalAdapter.TryMap(proposal,out var goal),Is.True);
                Assert.That(goal.snack_type,Is.EqualTo(snack)); Assert.That(goal.quantity,Is.EqualTo(quantity)); Assert.That(goal.drink_type,Is.Null);
            }
            foreach(string invalid in new[]{"cheesecake","chocolate_cake","",null})
                Assert.That(new CognitiveActionProposal { action_id="serve_snack",bindings=new Dictionary<string,string> { ["snack_type"]=invalid,["quantity"]="1" } }.IsValid,Is.False);
            foreach(string invalid in new[]{"0","5","1.5","01",null})
                Assert.That(new CognitiveActionProposal { action_id="serve_snack",bindings=new Dictionary<string,string> { ["snack_type"]="croissant",["quantity"]=invalid } }.IsValid,Is.False);
        }
        [TestCase("croissant", "1")]
        [TestCase("pudding", "3")]
        public void SnackCompletionUsesNaturalSpeechAndOtherStatusesCannotClaimSuccess(string snack, string quantity)
        {
            var policy = new ServeSnackPresentationPolicy();
            var bindings = new Dictionary<string, string> { ["snack_type"] = snack, ["quantity"] = quantity };
            ActionPresentationInfo Present(ActionExecutionStatus status) => policy.Present(new ActionExecutionResult(
                "serve_snack", "snack_presentation", "snack_trace", status, "", bindings, 1, 2));
            bool english = EmbodiedNPC.Settings.SettingsUiLanguage.IsEnglish;
            string name = SnackCatalog.Name(snack, english);
            string success = Present(ActionExecutionStatus.Succeeded).Message;
            Assert.That(success, Does.Contain(name));
            Assert.That(success, Does.Contain(english ? "is ready" : "准备好了"));
            Assert.That(success, Does.Contain(english ? "Enjoy" : "请享用"));
            Assert.That(success, Does.Not.Contain(english ? "Provided:" : "已提供"));
            if (quantity != "1") Assert.That(success, Does.Contain(quantity));
            foreach (var status in new[] { ActionExecutionStatus.Accepted, ActionExecutionStatus.Cancelled,
                ActionExecutionStatus.Failed, ActionExecutionStatus.Rejected, ActionExecutionStatus.TimedOut })
            {
                Assert.That(Present(status).Message, Does.Not.Contain(english ? "is ready" : "准备好了"));
                Assert.That(Present(status).Message, Does.Not.Contain(english ? "Enjoy" : "请享用"));
            }
        }
        [Test] public void SnackClipReusesReachOnOneMaskedLayerWithoutBodyOrPropCurves()
        {
            var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/CafeDemo/Animations/CafeServeSnack.anim");
            Assert.That(clip,Is.Not.Null); Assert.That(clip.isHumanMotion,Is.True); Assert.That(clip.isLooping,Is.False);
            Assert.That(clip.length,Is.EqualTo(ServeSnackActionRunner.ServiceSeconds).Within(.001f));
            Assert.That(AnimationUtility.GetCurveBindings(clip).All(b=>b.type==typeof(Animator)&&
                (b.propertyName.StartsWith("Right")||b.propertyName.StartsWith("Left"))),Is.True);
            Assert.That(clip.events,Is.Empty);
        }
    }
    public sealed class CafeSnackServiceTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel=>false;
        protected override bool ClearSceneAfterTest=>true;
        protected override bool EnableAmbient=>true;
        private GoalPlanRunner Runner=>merchant.GetComponent<GoalPlanRunner>();
        private SnackServiceCapability Cap=>merchant.GetComponent<SnackServiceCapability>();
        private MotionChannelCoordinator Motion=>merchant.GetComponent<MotionChannelCoordinator>();
        private DrinkPreparationPresenter Visual=>merchant.GetComponent<DrinkPreparationPresenter>();
        private ActionFeedbackPresenter Feedback=>Object.FindAnyObjectByType<ActionFeedbackPresenter>();
        private static string Output=>Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ?? Path.GetTempPath();
        private GoalOutcome? outcome;
        private void Prepare()
        {
            foreach(var npc in Object.FindObjectsByType<WorldEntity>().Where(x=>x.IsAgent(AgentRole.Npc)))
            {
                npc.GetComponent<CognitiveRunDriver>().enabled=false; npc.GetComponent<AgentOrchestrator>().CancelCurrentRun();
                var vision=npc.GetComponent<VisionSensor>(); if(vision!=null) vision.enabled=false;
                var bridge=npc.GetComponent<VisualObservationAgentEventBridge>(); if(bridge!=null) bridge.enabled=false;
                var source=npc.GetComponent<TaskConsiderationSource>(); if(source!=null) source.enabled=false;
                npc.GetComponent<DailyBehaviorController>().AmbientEnabled=false;
            }
            foreach(var camera in Object.FindObjectsByType<CafeDemoOrbitCamera>()) camera.enabled=false;
            Object.FindAnyObjectByType<DialogueController>().ClearSession("snack_offline");
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            Place(new Vector3(1,.03f,-1.5f)); Runner.GoalOutcomePublished+=x=>outcome=x;
            Assert.That(Cap.IsAuthorized&&Visual.IsSnackReady,Is.True);
        }
        private void Order(string snack,string quantity="1")
        {
            outcome=null;
            var driver=merchant.GetComponent<CognitiveRunDriver>(); driver.enabled=false;
            merchant.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("snack-"+Guid.NewGuid().ToString("N"),AgentEventTypes.PlayerUtterance,
                "player_01",merchant.EntityId,"",new Dictionary<string,string> { [AgentEventPayloadKeys.Text]=$"请给我{quantity}份{SnackCatalog.Name(snack)}" },
                Time.realtimeSinceStartupAsDouble,world.WorldRevision));
            var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest; Assert.That(request,Is.Not.Null);
            driver.enabled=true; Assert.That(driver.TryOwnRequest(request),Is.True);
            var response=new CognitiveResponse { message_type="cognitive_response",schema_version=request.schema_version,task_result=request.schema_version=="1.1"?"none":null,request_id=request.request_id,
                trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision { decision_kind="ACTION_PROPOSAL",utterance="好呀，我去取点心，请稍等。",
                    action_proposal=new CognitiveActionProposal { action_id="serve_snack",bindings=new Dictionary<string,string> { ["snack_type"]=snack,["quantity"]=quantity } } },
                validated_goal=new GoalProposal { type="serve_snack",snack_type=snack,quantity=quantity } };
            Assert.That(response.IsValid,Is.True); Assert.That(driver.TryConsumeResponse(response),Is.True,driver.LastActionHandoffResult);
            Assert.That(Runner.HasActiveTask,Is.True,driver.LastActionHandoffResult);
        }
        private IEnumerator UntilInteraction()
        {
            float deadline=Time.realtimeSinceStartup+20;
            while(!Motion.IsInteractionMotionActive&&!outcome.HasValue&&Time.realtimeSinceStartup<deadline) yield return null;
            Assert.That(Motion.IsInteractionMotionActive,Is.True,outcome?.FailureCode);
        }
        private IEnumerator Complete()
        {
            float deadline=Time.realtimeSinceStartup+20;
            while(!outcome.HasValue&&Time.realtimeSinceStartup<deadline) yield return null;
            Assert.That(outcome.HasValue,Is.True); Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded),outcome.Value.FailureCode);
            Assert.That(Runner.HasActiveTask,Is.False);
        }
        private void Capture(string name)
        {
            Directory.CreateDirectory(Output); var camera=Camera.main;
            camera.transform.position=merchant.transform.position+new Vector3(-1.1f,1.65f,.1f);
            camera.transform.LookAt(merchant.transform.position+merchant.transform.forward*.45f+Vector3.up*1.05f); camera.fieldOfView=55;
            var old=camera.targetTexture; var previous=RenderTexture.active; var rt=new RenderTexture(1200,850,24); var image=new Texture2D(1200,850,TextureFormat.RGB24,false);
            try { camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,1200,850),0,0); image.Apply();
                File.WriteAllBytes(Path.Combine(Output,name+".png"),image.EncodeToPNG()); }
            finally { camera.targetTexture=old; RenderTexture.active=previous;rt.Release();Object.Destroy(rt);Object.Destroy(image); }
        }
        private void AssertOffline()
        {
            foreach(var backend in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            { Assert.That(backend.ConnectionAttempts,Is.Zero); Assert.That(backend.CognitiveRequestSendCount,Is.Zero); }
        }
        [UnityTest] public IEnumerator TwoLocations_RealNavigationReachReceiptsMultiQuantityAndUi()
        {
            Prepare(); yield return Wait(.6f); var registry=Object.FindAnyObjectByType<EntityRegistry>();
            int entityCount=registry.RegisteredEntities.Count(); var held=Object.FindAnyObjectByType<HeldItemState>();
            var parent=held.transform.parent; int before=world.WorldRevision;
            foreach(var order in new[]{("croissant","1"),("pudding","3")})
            {
                Vector3 start=merchant.transform.position; Order(order.Item1,order.Item2); string proposal=Runner.ActiveProposalId,trace=Runner.ActiveTraceId;
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.False);
                yield return UntilInteraction(); Assert.That(Vector3.Distance(start,merchant.transform.position),Is.GreaterThan(.5f));
                Assert.That(Vector3.Distance(merchant.transform.position,Cap.Approach(order.Item1).position),Is.LessThan(.3f));
                Assert.That(Vector3.Angle(merchant.transform.forward,Cap.Operation(order.Item1).position-merchant.transform.position+Vector3.up*(merchant.transform.position.y-Cap.Operation(order.Item1).position.y)),Is.LessThan(2));
                double began=Time.timeAsDouble; yield return Wait(.8f);
                Assert.That(Visual.IsPlaying,Is.True); Assert.That(Visual.CurrentWeight,Is.GreaterThan(.9f)); Assert.That(Visual.PropVisible||Visual.SteamActive,Is.False);
                Assert.That(Feedback.PhaseText(merchant.EntityId),Does.Contain(SnackCatalog.Name(order.Item1)));
                Capture(order.Item1+"-reach"); Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.False);
                yield return Complete(); Assert.That(Time.timeAsDouble-began,Is.GreaterThanOrEqualTo(2.3));
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out var receipt),Is.True);
                Assert.That(receipt.SnackType,Is.EqualTo(order.Item1)); Assert.That(receipt.Quantity,Is.EqualTo(order.Item2));
                var state=Runner.CaptureExecutionState("player_01"); Assert.That(state.IsValid,Is.True);
                Assert.That(state.status,Is.EqualTo("SUCCEEDED")); Assert.That(state.receipt_id,Is.EqualTo(receipt.Id));
                int committed=world.WorldRevision;
                typeof(WorldStateService).GetMethod("TryCommitSnack",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                    .Invoke(world,new object[]{proposal,trace,registry,merchant,Cap.Station,player.GetComponent<WorldEntity>(),order.Item1,order.Item2});
                Assert.That(world.WorldRevision,Is.EqualTo(committed),"Replayed service completion must be idempotent.");
                Assert.That(Feedback.LastResultText,Is.EqualTo($"已提供：{SnackCatalog.Name(order.Item1)} × {order.Item2}"));
                Assert.That(Feedback.PendingNotificationCount,Is.GreaterThan(0));
                yield return null; Assert.That(Visual.CurrentWeight,Is.Zero); Assert.That(Motion.IsInteractionMotionActive,Is.False);
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
                yield return Wait(.4f);
            }
            Assert.That(world.WorldRevision,Is.EqualTo(before+2)); Assert.That(registry.RegisteredEntities.Count(),Is.EqualTo(entityCount));
            Assert.That(held.transform.parent,Is.SameAs(parent)); Assert.That(held.IsHeld,Is.False); Assert.That(world.GetDrinkCount("player_01","latte"),Is.Zero);
            AssertOffline();
        }
        [UnityTest] public IEnumerator SavedSnackEffectGuardChecksOrderAndCanRevokeBeforeCommit()
        {
            Prepare();yield return Wait(.5f);
            var registry=Object.FindAnyObjectByType<EntityRegistry>();
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            foreach(bool revoke in new[]{false,true})
            {
                int before=world.WorldRevision;Order("croissant","1");
                string proposal=Runner.ActiveProposalId,trace=Runner.ActiveTraceId;
                bool allowed=true;int checks=0;
                Func<string,string,string,string,int,string,string,bool> guard=(action,target,recipient,drink,condition,snack,quantity)=>
                {checks++;return allowed && action=="serve_snack" && target==Cap.Station.EntityId && recipient=="player_01" && snack=="croissant" && quantity=="1";};
                typeof(WorldStateService).GetMethod("RegisterTaskGuard",flags).Invoke(world,new object[]{merchant.EntityId,proposal,trace,guard});
                yield return UntilInteraction();
                var wrong=(SkillResult)typeof(WorldStateService).GetMethod("TryCommitSnack",flags).Invoke(world,
                    new object[]{proposal,trace,registry,merchant,Cap.Station,player.GetComponent<WorldEntity>(),"croissant","2"});
                Assert.That(wrong.Status,Is.EqualTo(SkillStatus.Failed));
                Assert.That(world.WorldRevision,Is.EqualTo(before));
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.False);
                allowed=!revoke;
                float deadline=Time.realtimeSinceStartup+10;
                while(!outcome.HasValue&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.That(outcome.HasValue,Is.True);Assert.That(checks,Is.GreaterThanOrEqualTo(2));
                Assert.That(outcome.Value.Status,Is.EqualTo(revoke?PlanExecutionStatus.Failed:PlanExecutionStatus.Succeeded));
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.EqualTo(!revoke));
                Assert.That(world.WorldRevision,Is.EqualTo(before+(revoke?0:1)));
                Assert.That(Motion.IsInteractionMotionActive,Is.False);
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
                yield return Wait(.4f);
            }
            AssertOffline();
        }
        [UnityTest] public IEnumerator CancelDuringTravelAndReach_NeverCommitsOrNotifiesSuccess()
        {
            Prepare(); yield return Wait(.5f); int before=world.WorldRevision;
            for(int stage=0;stage<2;stage++)
            {
                Order("macaron","2"); string proposal=Runner.ActiveProposalId,trace=Runner.ActiveTraceId;
                if(stage==0) yield return Wait(.15f); else { yield return UntilInteraction(); yield return Wait(.8f); }
                Assert.That(outcome.HasValue,Is.False);
                if(stage==1) Assert.That(Runner.CancelOwnedGoal(Runner.CaptureExecutionState("player_01")),Is.EqualTo("cancelled"));
                else Runner.CancelCurrentPlan();
                yield return null;
                Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Cancelled)); Assert.That(Runner.HasActiveTask,Is.False);
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.False); Assert.That(world.WorldRevision,Is.EqualTo(before));
                Assert.That(Feedback.LastResultText,Does.Contain("已取消")); Assert.That(Feedback.LastResultText,Does.Not.Contain("已提供"));
                Assert.That(Visual.CurrentWeight,Is.Zero); Assert.That(Motion.IsInteractionMotionActive,Is.False);
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement(); yield return Wait(.3f);
            }
            AssertOffline();
        }
        [UnityTest] public IEnumerator DisabledCapabilityOrRunner_ReleasesWithoutReceipt()
        {
            Prepare(); yield return Wait(.5f); int before=world.WorldRevision;
            for(int stage=0;stage<3;stage++)
            {
                Order("cupcake"); string proposal=Runner.ActiveProposalId,trace=Runner.ActiveTraceId;
                yield return UntilInteraction(); yield return Wait(.4f);
                if(stage==0) Cap.enabled=false; else if(stage==1) Runner.enabled=false; else Visual.enabled=false;
                yield return Wait(.5f); Assert.That(Runner.HasActiveTask,Is.False);
                Assert.That(outcome.HasValue,Is.True); Assert.That(outcome.Value.Status,Is.Not.EqualTo(PlanExecutionStatus.Succeeded));
                Assert.That(world.TryGetSnackReceipt(merchant.EntityId,proposal,trace,out _),Is.False); Assert.That(world.WorldRevision,Is.EqualTo(before));
                Assert.That(Visual.CurrentWeight,Is.Zero); Assert.That(Motion.IsInteractionMotionActive,Is.False);
                Cap.enabled=true; Runner.enabled=true; Visual.enabled=true;
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement(); yield return Wait(.3f);
            }
            AssertOffline();
        }
        [UnityTest] public IEnumerator SnackPreemptsAmbientAndRestModeResumesAfterService()
        {
            Prepare(); yield return Wait(1.2f); var daily=merchant.GetComponent<DailyBehaviorController>(); daily.AmbientEnabled=true;
            Assert.That(daily.TryStartAmbient(DailyAmbientActivity.CounterMove),Is.True,daily.LastAmbientReason); yield return Wait(.3f);
            long old=daily.AmbientInstance; Assert.That(old,Is.Not.Zero); Order("pudding"); Assert.That(daily.AmbientInstance,Is.Zero);
            merchant.GetComponent<DailyBehaviorDemoTrigger>().RequestMode(DailyBehaviorMode.Rest); yield return Wait(.15f);
            Assert.That(daily.CurrentMode,Is.EqualTo(DailyBehaviorMode.Work)); yield return Complete();
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement(); yield return Wait(1.3f);
            Assert.That(daily.CurrentMode,Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(merchant.GetComponent<CafeAmbientPresenter>().TickAmbient(old,10),Is.False); AssertOffline();
        }
        [UnityTest] public IEnumerator ExistingCoffeeAndCleaning_KeepTheirOwnEffectsAndPresentation()
        {
            Prepare(); yield return Wait(.6f); outcome=null;
            string token=Guid.NewGuid().ToString("N");
            Assert.That(Runner.TrySubmitGoal(new GoalProposal { type="prepare_drink",drink_type="latte" },token,token,0,world.WorldRevision,"player_01"),Is.True);
            yield return UntilInteraction(); yield return Wait(.6f);
            Assert.That(Visual.IsPlaying&&Visual.PropVisible&&Visual.SteamActive,Is.True);
            Assert.That(Runner.CurrentTaskFeedback.Progress.Action,Is.EqualTo(PlannerActionType.PrepareDrink));
            yield return Complete(); Assert.That(world.GetDrinkCount("player_01","latte"),Is.EqualTo(1));
            yield return null; Assert.That(Visual.IsPlaying||Visual.PropVisible||Visual.SteamActive,Is.False);
            var station=merchant.GetComponent<CounterCleaningCapability>().Station;
            Assert.That(world.TryMarkCounterNeedsCleaning(Object.FindAnyObjectByType<EntityRegistry>(),station,"snack_regression_spill"),Is.True);
            outcome=null; token=Guid.NewGuid().ToString("N");
            Assert.That(Runner.TrySubmitGoal(new GoalProposal { type="clean_counter",target_entity_id=station.Entity.EntityId },token,token,0,world.WorldRevision,"player_01"),Is.True);
            yield return UntilInteraction(); yield return Wait(.6f);
            Assert.That(merchant.GetComponent<CounterCleaningPresenter>().IsPlaying,Is.True); Assert.That(Visual.IsPlaying,Is.False);
            yield return Complete(); Assert.That(world.GetCounterState(station.Entity.EntityId),Is.EqualTo(CounterCleanliness.Clean));
            Assert.That(world.GetDrinkCount("player_01","latte"),Is.EqualTo(1)); AssertOffline();
        }
        [UnityTest] public IEnumerator HeldPhysicalItemRejectsSnackWithoutMovingOrChangingOwnership()
        {
            Prepare(); yield return Wait(.6f);
            var item=Object.FindObjectsByType<WorldEntity>().Single(x=>x.EntityId=="cup_01");
            var held=item.GetComponent<HeldItemState>();
            Assert.That(world.TryPickUp("snack-busy-check","snack-busy-check",merchant,item,held,
                merchant.transform.Find("HandHoldPoint"),item.transform.Find("GrabPoint")).Status,Is.EqualTo(SkillStatus.Succeeded));
            int revision=world.WorldRevision; var parent=item.transform.parent; Vector3 position=merchant.transform.position;
            Assert.That(Runner.TrySubmitGoal(new GoalProposal { type="serve_snack",snack_type="croissant",quantity="1" },
                "snack-busy","snack-busy",0,revision,"player_01"),Is.False);
            Assert.That(Runner.HasActiveTask,Is.False); Assert.That(world.WorldRevision,Is.EqualTo(revision));
            Assert.That(held.HolderEntityId,Is.EqualTo(merchant.EntityId)); Assert.That(item.transform.parent,Is.SameAs(parent));
            Assert.That(merchant.transform.position,Is.EqualTo(position)); Assert.That(world.TryGetSnackReceipt(merchant.EntityId,"snack-busy","snack-busy",out _),Is.False);
            AssertOffline();
        }
    }
}
