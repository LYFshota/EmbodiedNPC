using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class NpcDefaultRoutineTests : PlayerPresentationFixture
    {
        private NpcDefaultRoutineController controller;
        private ReturnToHomeRoutine routine;
        private NavMeshAgent agent;
        private DialogueController dialogue;
        private GoalPlanRunner runner;
        private AgentOrchestrator orchestrator;
        private MotionChannelCoordinator channels;
        private static string RoutineEvidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_ROUTINE_EVIDENCE")
            ?? "C:/Users/95265/AppData/Local/Temp/embodiednpc-routine-p1";
        private static void Set(object obj, string field, object value) => obj.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private void Prepare(bool shortLease = true)
        {
            ControlledPerception();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None)) driver.enabled = false;
            controller = merchant.GetComponent<NpcDefaultRoutineController>();
            routine = merchant.GetComponent<ReturnToHomeRoutine>(); agent = merchant.GetComponent<NavMeshAgent>();
            dialogue = Object.FindAnyObjectByType<DialogueController>(); runner = merchant.GetComponent<GoalPlanRunner>();
            orchestrator = merchant.GetComponent<AgentOrchestrator>(); channels = merchant.GetComponent<MotionChannelCoordinator>();
            dialogue.ClearSession("routine_test"); controller.ReleaseEngagement();
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            if (shortLease) { Set(controller, "engagementHoldSeconds", .35f); Set(controller, "resumeDelay", .15f); }
        }
        private void Away()
        {
            Assert.That(agent.Warp(new Vector3(-2.2f, .03f, -.8f)), Is.True);
            merchant.transform.rotation = Quaternion.Euler(0, 90, 0);
        }
        private static float PlanarDistance(Vector3 a, Vector3 b) => Vector3.ProjectOnPlane(a-b, Vector3.up).magnitude;
        private IEnumerator WaitReturning()
        {
            float end = Time.realtimeSinceStartup + 5;
            while (!routine.OwnsPath && Time.realtimeSinceStartup < end) yield return null;
            Assert.That(routine.OwnsPath, Is.True);
        }
        private IEnumerator WaitHome()
        {
            bool walked = false; float end = Time.realtimeSinceStartup + 18;
            while (Time.realtimeSinceStartup < end)
            {
                walked |= merchant.GetComponentInChildren<LocomotionPresenter>().PlanarSpeed > .1f;
                if (!routine.IsRunning && PlanarDistance(merchant.transform.position, routine.HomePosition) <= agent.stoppingDistance + .09f &&
                    Quaternion.Angle(merchant.transform.rotation, routine.HomeRotation) < 1.1f) break;
                yield return null;
            }
            Assert.That(walked, Is.True, "Actual NavMesh velocity must drive walking.");
            Assert.That(PlanarDistance(merchant.transform.position, routine.HomePosition), Is.LessThanOrEqualTo(agent.stoppingDistance + .09f));
            Assert.That(Quaternion.Angle(merchant.transform.rotation, routine.HomeRotation), Is.LessThan(1.1f));
            Assert.That(routine.IsRunning || routine.OwnsPath, Is.False);
        }
        private bool ShowDialogue(WorldEntity entity)
        {
            Place(entity.transform.position + new Vector3(0,0,-1.5f));
            return dialogue.SelectTarget(entity.GetComponent<DialogueParticipant>());
        }
        private void PublishAccepted(string target, string speaker = null)
        {
            // Publisher-boundary fixture: only an already accepted, valid utterance is delivered.
            var utterance = new UserUtterance { text="继续聊聊", conversationId="routine_conversation",
                speakerEntityId=speaker ?? player.GetComponent<WorldEntity>().EntityId, targetAgentId=target,
                inputMode="text" };
            Assert.That(utterance.IsValid, Is.True);
            var callbacks = (Action<UserUtterance>)typeof(DialogueController)
                .GetField("DialogueSubmissionAccepted", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialogue);
            callbacks?.Invoke(utterance);
        }
        private void PublishGoal(PlanExecutionStatus status, string target, string destination)
        {
            var outcome = new GoalOutcome(Guid.NewGuid().ToString("N"), "routine_proposal",0,"routine_trace",
                "future_player_directed_goal",merchant.EntityId,target,destination,status,"",world.WorldRevision,Time.unscaledTimeAsDouble);
            typeof(GoalPlanRunner).GetMethod("PublishGoalOutcome",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(runner,new object[]{(GoalOutcome?)outcome});
        }
        [UnityTest] public IEnumerator AtHome_IsIdle()
        {
            Prepare(); var position=merchant.transform.position; var rotation=merchant.transform.rotation; int revision=world.WorldRevision;
            yield return Wait(.4f);
            Assert.That(agent.hasPath || routine.IsRunning, Is.False);
            Assert.That(merchant.transform.position, Is.EqualTo(position)); Assert.That(merchant.transform.rotation, Is.EqualTo(rotation));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(customer.GetComponent<NpcDefaultRoutineController>(), Is.Null);
            Assert.That(Object.FindObjectsByType<NpcDefaultRoutineController>(FindObjectsSortMode.None).Length, Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator AwayAndIdle_ReturnsHome()
        { Prepare(); Away(); yield return WaitReturning(); yield return WaitHome(); }
        [UnityTest] public IEnumerator FormalTask_BlocksDefault()
        {
            Prepare(); Away(); Set(runner,"activeGoal",new GoalProposal{type="bring_object"});
            Vector3 destination=new Vector3(3,.03f,-1); agent.SetDestination(destination);
            yield return Wait(.5f); Assert.That(runner.HasActiveTask,Is.True); Assert.That(routine.OwnsPath,Is.False);
            Assert.That(PlanarDistance(agent.destination,destination),Is.LessThan(.02f));
            Set(runner,"activeGoal",null); agent.ResetPath();
        }
        [UnityTest] public IEnumerator CognitiveRun_BlocksDefault()
        {
            Prepare(); Away(); ReceiveUtterance(); Assert.That(orchestrator.HasActiveRun,Is.True);
            yield return Wait(.5f); Assert.That(routine.IsRunning,Is.False); Assert.That(agent.hasPath,Is.False);
            orchestrator.CompleteCurrentRun(); yield return WaitReturning();
        }
        private void ReceiveUtterance()
        {
            Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(new AgentEvent(Guid.NewGuid().ToString("N"),
                AgentEventTypes.PlayerUtterance,player.GetComponent<WorldEntity>().EntityId,merchant.EntityId,"",
                new Dictionary<string,string>{{AgentEventPayloadKeys.Text,"拿杯子"}},Time.realtimeSinceStartupAsDouble,world.WorldRevision)),Is.True);
        }
        [UnityTest] public IEnumerator VisibleDialogue_BlocksBeyondLease()
        {
            Prepare(); Away(); controller.RefreshEngagement("test"); Assert.That(ShowDialogue(merchant),Is.True);
            yield return Wait(.8f); Assert.That(controller.IsEngagementHeld,Is.False); Assert.That(routine.IsRunning,Is.False);
            Assert.That(agent.hasPath,Is.False); dialogue.HideDialoguePresentation();
            Assert.That(dialogue.HasActiveSession,Is.True,"Retained conversation must not permanently block fallback.");
            yield return WaitReturning();
        }
        [UnityTest] public IEnumerator OtherNpcDialogue_DoesNotBlockMerchant()
        { Prepare(); Away(); Assert.That(ShowDialogue(customer),Is.True); yield return WaitReturning(); }
        [UnityTest] public IEnumerator AcceptedUtterance_RefreshesLease()
        {
            Prepare(); Away(); PublishAccepted(customer.EntityId); Assert.That(controller.IsEngagementHeld,Is.False);
            PublishAccepted(merchant.EntityId,customer.EntityId); Assert.That(controller.IsEngagementHeld,Is.False);
            PublishAccepted(merchant.EntityId); Assert.That(controller.IsEngagementHeld,Is.True);
            Assert.That(controller.LastEngagementReason,Is.EqualTo("player_utterance"));
            yield return Wait(.2f); Assert.That(routine.OwnsPath,Is.False);
        }
        [UnityTest] public IEnumerator PlayerDirectedGoalSuccess_RefreshesLease()
        {
            Prepare(); var playerId=player.GetComponent<WorldEntity>().EntityId;
            PublishGoal(PlanExecutionStatus.Failed,customer.EntityId,playerId); Assert.That(controller.IsEngagementHeld,Is.False);
            PublishGoal(PlanExecutionStatus.Succeeded,customer.EntityId,customer.EntityId); Assert.That(controller.IsEngagementHeld,Is.False);
            PublishGoal(PlanExecutionStatus.Succeeded,customer.EntityId,playerId); Assert.That(controller.IsEngagementHeld,Is.True);
            controller.ReleaseEngagement(); PublishGoal(PlanExecutionStatus.Succeeded,playerId,""); Assert.That(controller.IsEngagementHeld,Is.True);
            Assert.That(controller.LastEngagementReason,Is.EqualTo("player_directed_goal")); yield return null;
        }
        [UnityTest] public IEnumerator EngagementExpires_WithSeparateQuietDelay()
        {
            Prepare(); Away(); controller.RefreshEngagement("test"); yield return Wait(.38f);
            Assert.That(controller.IsEngagementHeld,Is.False); Assert.That(routine.OwnsPath,Is.False);
            yield return WaitReturning();
        }
        [UnityTest] public IEnumerator FormalPreemptsReturnWithoutPathClobber()
        {
            Prepare(); Away(); yield return WaitReturning();
            Vector3 destination=new Vector3(3,.03f,-1); agent.SetDestination(destination);
            Set(runner,"activeGoal",new GoalProposal{type="bring_object"});
            yield return Wait(.3f); Assert.That(routine.OwnsPath,Is.False); Assert.That(agent.hasPath,Is.True);
            Assert.That(PlanarDistance(agent.destination,destination),Is.LessThan(.02f));
            Set(runner,"activeGoal",null); agent.ResetPath(); yield return WaitReturning();
        }
        [UnityTest] public IEnumerator RunStarted_PreemptsHomeBeforeSameFrameAdmission()
        {
            Prepare(); Away(); yield return WaitReturning(); ReceiveUtterance();
            Assert.That(orchestrator.HasActiveRun,Is.True);
            Assert.That(routine.OwnsPath||agent.hasPath,Is.False,"Home must be released before a same-frame formal admission reads busy navigation.");
            orchestrator.CompleteCurrentRun(); yield return WaitReturning();
        }
        [UnityTest] public IEnumerator DialoguePreemptsOwnedHomePath()
        {
            Prepare(); Away(); yield return WaitReturning(); Assert.That(ShowDialogue(merchant),Is.True);
            yield return Wait(.2f); Assert.That(routine.OwnsPath,Is.False); Assert.That(agent.hasPath,Is.False);
            dialogue.HideDialoguePresentation(); yield return WaitReturning();
        }
        [UnityTest] public IEnumerator AuthorityRegression()
        {
            Prepare(); int revision=world.WorldRevision, outcomes=0, events=0, runs=0;
            var held=Object.FindObjectsByType<HeldItemState>(FindObjectsSortMode.None).Select(h=>h.HolderEntityId).ToArray();
            runner.GoalOutcomePublished+=_=>outcomes++; merchant.GetComponent<AgentEventReceiver>().EventReceived+=_=>events++;
            orchestrator.RunStarted+=_=>runs++; Away(); yield return WaitReturning(); yield return WaitHome();
            Assert.That(world.WorldRevision,Is.EqualTo(revision)); Assert.That(outcomes+events+runs,Is.Zero);
            Assert.That(Object.FindObjectsByType<HeldItemState>(FindObjectsSortMode.None).Select(h=>h.HolderEntityId).ToArray(),Is.EqualTo(held));
        }
        [UnityTest] public IEnumerator EmergencyInteractionGestureAndSpeech_BlockWithoutLeaseRefresh()
        {
            Prepare(); Away(); channels.BeginEmergencyMotion(); yield return Wait(.25f); Assert.That(routine.OwnsPath,Is.False);
            channels.EndEmergencyMotion(); channels.SetInteractionActive(true); yield return Wait(.25f); Assert.That(routine.OwnsPath,Is.False);
            channels.SetInteractionActive(false); channels.BeginTaskMotion(); yield return Wait(.25f); Assert.That(routine.OwnsPath,Is.False);
            channels.EndTaskMotion(); Assert.That(channels.TryBeginGesture("wave",false,Time.unscaledTimeAsDouble,2,out long id),Is.True);
            yield return Wait(.25f); Assert.That(routine.OwnsPath,Is.False); channels.CompleteGesture(id);
            var speech=merchant.GetComponent<SpeechPresentationLifecycle>(); speech.BeginPlayback("等一下");
            yield return Wait(.4f); Assert.That(routine.OwnsPath,Is.False); Assert.That(controller.IsEngagementHeld,Is.False);
            speech.Cancel(); yield return WaitReturning();
        }
        [UnityTest] public IEnumerator FailedHome_CooldownAndSingleWarning()
        {
            Prepare(); Away(); Set(routine,"homePosition",new Vector3(999,0,999));
            LogAssert.Expect(LogType.Warning,new Regex("\\[ReturnHome\\] Home navigation unavailable"));
            yield return Wait(1.5f); Assert.That(routine.IsRunning||agent.hasPath,Is.False);
            LogAssert.NoUnexpectedReceived(); Set(routine,"homePosition",new Vector3(-.8f,.03f,4.35f));
            yield return WaitReturning();
        }
        [UnityTest] public IEnumerator DisableReenable_BalancesSubscriptionsAndReleasesOwnedPath()
        {
            Prepare(); Away(); yield return WaitReturning(); controller.enabled=false;
            Assert.That(agent.hasPath,Is.False); PublishAccepted(merchant.EntityId); Assert.That(controller.IsEngagementHeld,Is.False);
            controller.enabled=true; PublishAccepted(merchant.EntityId); Assert.That(controller.IsEngagementHeld,Is.True);
            controller.ReleaseEngagement(); yield return WaitReturning();
        }
        private void CaptureRoutine(string name)
        {
            if (!File.Exists(RoutineEvidence + "/capture.flag")) return;
            var camera=Camera.main; var previous=camera.targetTexture; var active=RenderTexture.active;
            // Evidence framing must not drift with the unattended Editor mouse; use the actual MainCamera.
            camera.GetComponent<CafeDemoOrbitCamera>().enabled = false;
            Vector3 focus = merchant.transform.position + Vector3.up * .85f;
            Vector3 offset = new Vector3(-1.8f, 1.6f, -2.4f);
            if (name == "01-startup")
            {
                var cup = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "cup_01");
                focus = cup.GetComponentInChildren<Renderer>().bounds.center;
                offset = new Vector3(-.9f, .8f, -1.6f);
            }
            if (name.Contains("give") || name.Contains("dialogue"))
            {
                focus = (merchant.transform.position + player.transform.position) * .5f + Vector3.up * .85f;
                offset = new Vector3(-2.5f, 2.2f, -3.5f);
            }
            camera.transform.position = focus + offset; camera.transform.LookAt(focus);
            var rt=new RenderTexture(1280,720,24); var texture=new Texture2D(1280,720,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt;
                texture.ReadPixels(new Rect(0,0,1280,720),0,0); texture.Apply();
                File.WriteAllBytes(RoutineEvidence+"/"+name+".png",texture.EncodeToPNG());
            }
            finally {camera.targetTexture=previous; RenderTexture.active=active; Object.Destroy(rt); Object.Destroy(texture);}
        }
        [UnityTest] public IEnumerator CafeMainCamera_BringGiveHoldDialogueExpiryReturn()
        {
            Prepare(false); yield return Wait(1.2f); Assert.That(routine.IsRunning||agent.hasPath,Is.False);
            Vector3 startupCameraPosition=Camera.main.transform.position;
            Quaternion startupCameraRotation=Camera.main.transform.rotation;
            CaptureRoutine("01-startup");
            var entities=Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            var cup=entities.Single(e=>e.EntityId=="cup_01"); var playerEntity=player.GetComponent<WorldEntity>();
            var held=cup.GetComponent<HeldItemState>(); int revision=world.WorldRevision, successes=0;
            bool givePublished = false;
            world.SkillResultPublished += result =>
            {
                if (result.SkillType != "Give" || result.Status != SkillStatus.Succeeded) return;
                givePublished = true;
                Assert.That(result.WorldRevisionBefore, Is.EqualTo(revision + 1));
                Assert.That(result.WorldRevisionAfter, Is.EqualTo(revision + 2));
                Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled), Is.True);
                Assert.That(cup.GetComponent<PerceivableEntity>().enabled, Is.False);
            };
            Vector3 successPosition=Vector3.zero; double succeededAt=0;
            runner.GoalOutcomePublished+=outcome=>
            {
                if(outcome.Status!=PlanExecutionStatus.Succeeded) return;
                successes++; succeededAt=Time.unscaledTimeAsDouble; successPosition=merchant.transform.position;
                Assert.That(givePublished, Is.True, "Committed Give must precede immediate GoalOutcome.");
                Assert.That(held.HolderEntityId,Is.EqualTo(playerEntity.EntityId));
                Assert.That(PlanarDistance(successPosition,routine.HomePosition),Is.GreaterThan(1));
                Assert.That(routine.OwnsPath,Is.False,"Give must succeed before returning home.");
            };
            Place(new Vector3(-2.2f,.03f,-.8f));
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            var vision=merchant.GetComponent<VisionSensor>(); vision.enabled=true; vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds, Does.Contain(cup.EntityId));
            Assert.That(merchant.GetComponent<PerceivedStateStore>().Aggregator.Entities[cup.EntityId]
                .KnownFields["visibility_state"], Is.EqualTo("current_visible"));
            ReceiveUtterance();
            var driver=merchant.GetComponent<CognitiveRunDriver>(); var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            driver.enabled=true; Assert.That(driver.TryOwnRequest(request),Is.True);
            var response=new CognitiveResponse {message_type="cognitive_response",schema_version="1.0",request_id=request.request_id,
                trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision {decision_kind=CognitiveDecisionKinds.ActionProposal,utterance="我去拿杯子。",emotion="neutral",gesture="none",
                    action_proposal=new CognitiveActionProposal {action_id="bring_object",bindings=new Dictionary<string,string>{{"target",cup.EntityId},{"destination",playerEntity.EntityId}}}},
                validated_goal=new GoalProposal {type="bring_object",target_entity_id=cup.EntityId,destination_entity_id=playerEntity.EntityId}};
            string admissionCode=""; runner.PresentationReady+=presentation=>admissionCode=presentation.Code;
            bool consumed=driver.TryConsumeResponse(response);
            Assert.That(consumed,Is.True,"admission="+admissionCode+" responseValid="+response.IsValid+
                " relevantPlayer="+(request.context.relevant_player==null?"null":request.context.relevant_player.player_entity_id)+
                " run="+orchestrator.HasActiveRun);
            bool pickedUp=false,carried=false,carriedMidWalk=false,approached=false;
            float deadline=Time.realtimeSinceStartup+35;
            while(driver.HasPendingActionOutcome&&Time.realtimeSinceStartup<deadline)
            {
                if(held.HolderEntityId==merchant.EntityId)
                {
                    Assert.That(cup.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled), Is.True);
                    Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled, Is.True);
                    if (!pickedUp) CaptureRoutine("02a-pickup");
                    pickedUp=true;
                    if(agent.velocity.magnitude>.1f && merchant.GetComponent<CarryPresenter>().CurrentWeight>.9f && !carried)
                    {carried=true;CaptureRoutine("02-carry");}
                    if (carried && !carriedMidWalk && agent.velocity.magnitude > .1f &&
                        PlanarDistance(merchant.transform.position, routine.HomePosition) > 2f)
                    { carriedMidWalk = true; CaptureRoutine("02b-carry-walk"); }
                }
                else if (!pickedUp && !approached && agent.velocity.magnitude > .1f)
                { approached = true; CaptureRoutine("01a-approach"); }
                Assert.That(routine.OwnsPath,Is.False); yield return null;
            }
            Assert.That(driver.HasPendingActionOutcome,Is.False); Assert.That(pickedUp&&carried,Is.True);
            Assert.That(carriedMidWalk, Is.True, "Visible Carry must survive the actual walk away from the counter.");
            Assert.That(successes,Is.EqualTo(1)); Assert.That(world.WorldRevision,Is.EqualTo(revision+2));
            Assert.That(cup.transform.parent, Is.SameAs(player.transform.Find("PlayerHoldPoint")));
            Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled), Is.True);
            Assert.That(cup.GetComponent<Collider>().enabled, Is.False);
            Assert.That(cup.GetComponent<PerceivableEntity>().isActiveAndEnabled, Is.False);
            Assert.That(cup.gameObject.activeInHierarchy && cup.isActiveAndEnabled && held.isActiveAndEnabled, Is.True);
            Assert.That(Object.FindAnyObjectByType<EntityRegistry>().TryGetEntity(cup.EntityId, out var resolvedCup), Is.True);
            Assert.That(resolvedCup, Is.SameAs(cup));
            vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds, Does.Not.Contain(cup.EntityId));
            Assert.That(merchant.GetComponent<PerceivedStateStore>().Aggregator.Entities[cup.EntityId]
                .KnownFields["visibility_state"], Is.Not.EqualTo("current_visible"));
            var proximity = merchant.GetComponent<ProximitySensor>();
            proximity.ScanNow();
            Assert.That(proximity.CurrentNearbyEntityIds, Does.Not.Contain(cup.EntityId));
            int committedEvents = world.EventHistory.Count;
            var rejected = world.TryPickUp("stored-pickup-rejected", "stored-recheck", merchant, cup, held,
                merchant.transform.Find("HandHoldPoint"), cup.transform.Find("GrabPoint"));
            Assert.That(rejected.Status, Is.EqualTo(SkillStatus.Failed));
            Assert.That(rejected.FailureCode, Is.EqualTo("ITEM_ALREADY_HELD"));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            Assert.That(world.EventHistory.Count, Is.EqualTo(committedEvents + 1));
            Assert.That(controller.IsEngagementHeld,Is.True); Assert.That(controller.LastEngagementReason,Is.EqualTo("player_directed_goal"));
            var speech=merchant.GetComponent<SpeechPresentationLifecycle>(); long speechId=speech.BeginPlayback("杯子给你了，还需要什么？");
            yield return Wait(1.2f); Assert.That(routine.OwnsPath,Is.False); Assert.That(speech.IsSpeaking,Is.True);
            CaptureRoutine("03-give-speech-hold"); speech.EndPlayback(speechId);
            Assert.That(controller.IsEngagementHeld,Is.True); Assert.That(ShowDialogue(merchant),Is.True);
            Vector3 interactionPosition=merchant.transform.position;
            yield return Wait(10.5f); Assert.That(controller.IsEngagementHeld,Is.False);
            Assert.That(dialogue.IsPresentationVisible,Is.True); Assert.That(routine.OwnsPath||agent.hasPath,Is.False);
            Assert.That(PlanarDistance(merchant.transform.position,interactionPosition),Is.LessThan(.05f));
            CaptureRoutine("04-visible-dialogue-over-10s"); dialogue.HideDialoguePresentation();
            Assert.That(dialogue.HasActiveSession,Is.True); yield return Wait(.5f); Assert.That(routine.OwnsPath,Is.False);
            yield return WaitReturning(); CaptureRoutine("05-return-walk"); yield return WaitHome(); yield return Wait(.4f);
            Assert.That(world.WorldRevision,Is.EqualTo(revision+2)); Assert.That(held.HolderEntityId,Is.EqualTo(playerEntity.EntityId));
            Assert.That(successes,Is.EqualTo(1)); Assert.That(orchestrator.HasActiveRun,Is.False);
            Assert.That(merchant.GetComponentInChildren<LocomotionPresenter>().NormalizedSpeed,Is.Zero);
            Object.FindAnyObjectByType<CafeDemoOrbitCamera>().enabled=false;
            Camera.main.transform.SetPositionAndRotation(startupCameraPosition,startupCameraRotation);
            CaptureRoutine("06-home-idle");
            if(File.Exists(RoutineEvidence+"/capture.flag")) File.WriteAllText(RoutineEvidence+"/manual-acceptance.txt",
                "CafeDemo Play Mode / MainCamera / controlled accepted ActionProposal\n"+
                "Give SUCCESS at "+succeededAt+" position="+successPosition+" before Home\n"+
                "Speech held, visible dialogue held 10.5s beyond lease; retained session allowed return\n"+
                "Actual carry/walk, returned startup position/rotation/Idle; revision="+world.WorldRevision+" holder="+held.HolderEntityId);
            if (File.Exists(RoutineEvidence + "/capture.flag"))
            {
                var recorder = Object.FindAnyObjectByType<RuntimeTraceRecorder>();
                File.WriteAllText(RoutineEvidence + "/trace-path.txt", recorder.CurrentTracePath);
            }
        }
        [UnityTest] public IEnumerator CafeMainCamera_FormalBringPreemptsReturnAndResumes()
        {
            Prepare(); yield return Wait(.2f);
            Vector3 startupCameraPosition=Camera.main.transform.position;
            Quaternion startupCameraRotation=Camera.main.transform.rotation;
            Assert.That(agent.Warp(new Vector3(-2.2f,.03f,4.35f)),Is.True);
            Place(new Vector3(-1.2f,.03f,-1.8f));
            yield return WaitReturning(); yield return Wait(.2f);
            Assert.That(agent.velocity.magnitude,Is.GreaterThan(.1f));
            CaptureRoutine("07-return-before-preemption");
            var cup=Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e=>e.EntityId=="cup_01");
            var playerEntity=player.GetComponent<WorldEntity>();
            merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
            var vision=merchant.GetComponent<VisionSensor>(); vision.enabled=true; vision.ScanNow();
            ReceiveUtterance();
            Assert.That(routine.OwnsPath||agent.hasPath,Is.False,"RunStarted must release the home path synchronously.");
            var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            var driver=merchant.GetComponent<CognitiveRunDriver>(); driver.enabled=true;
            Assert.That(driver.TryOwnRequest(request),Is.True);
            var response=new CognitiveResponse {message_type="cognitive_response",schema_version="1.0",request_id=request.request_id,
                trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=merchant.EntityId,
                decision=new CognitiveDecision {decision_kind=CognitiveDecisionKinds.ActionProposal,utterance="我来拿杯子。",emotion="neutral",gesture="none",
                    action_proposal=new CognitiveActionProposal {action_id="bring_object",bindings=new Dictionary<string,string>{{"target",cup.EntityId},{"destination",playerEntity.EntityId}}}},
                validated_goal=new GoalProposal {type="bring_object",target_entity_id=cup.EntityId,destination_entity_id=playerEntity.EntityId}};
            int successes=0; runner.GoalOutcomePublished+=outcome=>{if(outcome.Status==PlanExecutionStatus.Succeeded)successes++;};
            Assert.That(driver.TryConsumeResponse(response),Is.True);
            Assert.That(runner.HasActiveTask,Is.True);
            bool formalWalk=false; float deadline=Time.realtimeSinceStartup+35;
            while(driver.HasPendingActionOutcome&&Time.realtimeSinceStartup<deadline)
            {
                Assert.That(routine.OwnsPath,Is.False,"Fallback must not reissue Home during the formal plan.");
                if(agent.hasPath&&!agent.pathPending&&agent.velocity.magnitude>.1f)
                {
                    Assert.That(PlanarDistance(agent.destination,routine.HomePosition),Is.GreaterThan(.5f));
                    if(!formalWalk){formalWalk=true;CaptureRoutine("08-formal-preemption-walk");}
                }
                yield return null;
            }
            Assert.That(driver.HasPendingActionOutcome,Is.False); Assert.That(formalWalk,Is.True);
            Assert.That(successes,Is.EqualTo(1));
            Assert.That(cup.GetComponent<HeldItemState>().HolderEntityId,Is.EqualTo(playerEntity.EntityId));
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            yield return WaitReturning(); yield return WaitHome(); yield return Wait(.4f);
            Object.FindAnyObjectByType<CafeDemoOrbitCamera>().enabled=false;
            Camera.main.transform.SetPositionAndRotation(startupCameraPosition,startupCameraRotation);
            CaptureRoutine("09-preemption-resumed-home");
            if(File.Exists(RoutineEvidence+"/capture.flag"))File.WriteAllText(RoutineEvidence+"/manual-preemption.txt",
                "Actual returning path synchronously released by cognition; accepted bring_object walked, picked up and gave; SUCCESS=1; then resumed startup position and rotation.");
        }
    }
}
