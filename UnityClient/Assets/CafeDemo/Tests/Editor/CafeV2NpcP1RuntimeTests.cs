using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;
namespace CafeDemo.Tests
{
 public sealed class CafeV2NpcP1RuntimeTests
 {
  [UnitySetUp] public IEnumerator Load(){
   EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
   var backend=Object.FindAnyObjectByType<BackendWebSocketClient>();backend.enabled=false;
   Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled=false;
   Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled=false;
   yield return new EnterPlayMode();
   var panel=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();panel.ApplyStartupConfig(new ModelConfigView{configured=true});panel.Close();
   yield return null;
  }
  [UnityTearDown] public IEnumerator Cleanup(){if(Application.isPlaying)yield return new ExitPlayMode();}
  [UnityTest] public IEnumerator FixedSeats_FootIkAndFiveModelsRemainStable_AndMerchantCanCarryGiveWalkHome(){
   Assert.That(Application.isPlaying,Is.True);
   var composers=Object.FindObjectsByType<CognitiveContextComposer>();Assert.That(composers.Length,Is.EqualTo(4));
   var customers=composers.Where(x=>x.CharacterDefinition.CharacterId!="cafe.merchant").ToArray();
   var positions=customers.Select(x=>x.transform.position).ToArray();var rotations=customers.Select(x=>x.transform.rotation).ToArray();
   for(int frame=0;frame<180;frame++){
    yield return null;
    for(int i=0;i<customers.Length;i++){Assert.That(Vector3.Distance(customers[i].transform.position,positions[i]),Is.LessThan(.001));Assert.That(Quaternion.Angle(customers[i].transform.rotation,rotations[i]),Is.LessThan(.01));}
   }
   foreach(var customer in customers){
    var animator=customer.GetComponentsInChildren<Animator>().Single();var feet=animator.GetComponent<CafeSeatedFootPose>();Assert.That(feet,Is.Not.Null);
    var so=new SerializedObject(feet);
    foreach(var side in new[]{(HumanBodyBones.LeftFoot,"leftFootLocal"),(HumanBodyBones.RightFoot,"rightFootLocal")}){
     var target=animator.transform.TransformPoint(so.FindProperty(side.Item2).vector3Value);Assert.That(Vector3.Distance(animator.GetBoneTransform(side.Item1).position,target),Is.LessThan(.025));
    }
    Assert.That(animator.GetBoneTransform(HumanBodyBones.Hips).position.y,Is.InRange(.49f,.60f));
    Assert.That(customer.GetComponent<GestureController>().CanPlay("nod"),Is.True);
   }
   Assert.That(Object.FindObjectsByType<Animator>().Length,Is.EqualTo(5));
   var entities=Object.FindObjectsByType<WorldEntity>();var merchant=entities.Single(x=>x.EntityId=="npc_cafe_01");var player=entities.Single(x=>x.EntityId=="player_01");var cup=entities.Single(x=>x.EntityId=="cup_01");
   var gesture=merchant.GetComponent<GestureController>();Assert.That(gesture.CanPlay("wave"),Is.True,"V2 Wave controller must match the active Animator");
   var world=Object.FindAnyObjectByType<WorldStateService>();var held=cup.GetComponent<HeldItemState>();var hold=merchant.transform.Find("HandHoldPoint");var grab=cup.transform.Find("GrabPoint");
   Assert.That(world.TryPickUp("p1-carry-probe","p1",merchant,cup,held,hold,grab).Status,Is.EqualTo(SkillStatus.Succeeded));
   for(int i=0;i<30;i++)yield return null;
   Assert.That(merchant.GetComponent<CarryPresenter>().IsCarrying,Is.True);Assert.That(merchant.GetComponentInChildren<CarryAnchorFollower>().IsReady,Is.True);
   var playerHold=player.transform.Find("PlayerHoldPoint");Assert.That(world.TryTransferOwnership("p1-give-probe","p1",merchant,cup,player,held,playerHold,playerHold).Status,Is.EqualTo(SkillStatus.Succeeded));
   Assert.That(held.HolderEntityId,Is.EqualTo(player.EntityId));
   merchant.GetComponent<NpcDefaultRoutineController>().enabled=false;
   var agent=merchant.GetComponent<NavMeshAgent>();var home=merchant.transform.position;Assert.That(agent.isOnNavMesh,Is.True);
   Assert.That(NavMesh.SamplePosition(new Vector3(1.9f,0,4.5f),out var hit,.5f,agent.areaMask),Is.True);
   Assert.That(agent.SetDestination(hit.position),Is.True);agent.isStopped=false;var until=Time.realtimeSinceStartup+8;
   do{yield return null;}while((agent.pathPending||agent.remainingDistance>agent.stoppingDistance+.08f||agent.velocity.magnitude>.05f)&&Time.realtimeSinceStartup<until);
   Assert.That(agent.pathStatus,Is.EqualTo(NavMeshPathStatus.PathComplete));
   Assert.That(Vector3.Distance(merchant.transform.position,hit.position),Is.LessThan(agent.stoppingDistance+.08f));
   Assert.That(Vector3.Distance(merchant.transform.position,home),Is.GreaterThan(.4f));
   var routine=merchant.GetComponent<ReturnToHomeRoutine>();until=Time.realtimeSinceStartup+8;
   do{routine.TickRoutine(Time.deltaTime);yield return null;}while((Vector3.Distance(merchant.transform.position,home)>.19f||routine.IsRunning)&&Time.realtimeSinceStartup<until);
   Assert.That(Vector3.Distance(merchant.transform.position,home),Is.LessThan(.2f));

  }
  [UnityTest] public IEnumerator Merchant_BringObjectUsesExistingGoalPipeline_AndReturnsHome(){
   var entities=Object.FindObjectsByType<WorldEntity>();var merchant=entities.Single(x=>x.EntityId=="npc_cafe_01");var player=entities.Single(x=>x.EntityId=="player_01");var cup=entities.Single(x=>x.EntityId=="cup_01");
   foreach(var composer in Object.FindObjectsByType<CognitiveContextComposer>()){composer.GetComponent<CognitiveRunDriver>().enabled=false;composer.GetComponent<AgentOrchestrator>().CancelCurrentRun();}
   var home=merchant.GetComponent<ReturnToHomeRoutine>().HomePosition;
   merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(cup.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
   merchant.GetComponent<VisionSensor>().ScanNow();var world=Object.FindAnyObjectByType<WorldStateService>();int revision=world.WorldRevision;
   GoalOutcome? outcome=null;var runner=merchant.GetComponent<GoalPlanRunner>();runner.GoalOutcomePublished+=value=>outcome=value;
   var ev=new AgentEvent(Guid.NewGuid().ToString("N"),AgentEventTypes.PlayerUtterance,player.EntityId,merchant.EntityId,"",new Dictionary<string,string>{{AgentEventPayloadKeys.Text,"把这个杯子拿给我"}},Time.realtimeSinceStartupAsDouble,revision);
   Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(ev),Is.True);
   var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest;Assert.That(request,Is.Not.Null);
   var driver=merchant.GetComponent<CognitiveRunDriver>();driver.enabled=true;Assert.That(driver.TryOwnRequest(request),Is.True);
   var response=new CognitiveResponse{message_type="cognitive_response",schema_version="1.0",request_id=request.request_id,trace_id=request.trace_id,run_id=request.run.run_id,agent_entity_id=merchant.EntityId,
    decision=new CognitiveDecision{decision_kind=CognitiveDecisionKinds.ActionProposal,utterance="我去拿杯子。",emotion="neutral",gesture="none",action_proposal=new CognitiveActionProposal{action_id="bring_object",bindings=new Dictionary<string,string>{{"target",cup.EntityId},{"destination",player.EntityId}}}},
    validated_goal=new GoalProposal{type="bring_object",target_entity_id=cup.EntityId,destination_entity_id=player.EntityId}};
   Assert.That(driver.TryConsumeResponse(response),Is.True);
   var held=cup.GetComponent<HeldItemState>();var agent=merchant.GetComponent<NavMeshAgent>();bool picked=false,carried=false;float until=Time.realtimeSinceStartup+40;
   do{yield return null;picked|=held.HolderEntityId==merchant.EntityId;if(held.HolderEntityId==merchant.EntityId&&merchant.GetComponent<CarryPresenter>().CurrentWeight>.8f&&agent.velocity.magnitude>.1f){carried=true;}}
   while(driver.HasPendingActionOutcome&&Time.realtimeSinceStartup<until);
   Assert.That(driver.HasPendingActionOutcome,Is.False);Assert.That(outcome.HasValue,Is.True);Assert.That(outcome.Value.Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
   Assert.That(picked&&carried,Is.True);Assert.That(held.HolderEntityId,Is.EqualTo(player.EntityId));Assert.That(world.WorldRevision,Is.EqualTo(revision+2));
   Assert.That(cup.GetComponentsInChildren<Renderer>(true).All(x=>!x.enabled),Is.True);
   var routine=merchant.GetComponent<NpcDefaultRoutineController>();Assert.That(routine.IsEngagementHeld,Is.True);
   until=Time.realtimeSinceStartup+30;var returnHome=merchant.GetComponent<ReturnToHomeRoutine>();
   while((routine.IsEngagementHeld||Vector3.Distance(merchant.transform.position,home)>.22f||Quaternion.Angle(merchant.transform.rotation,returnHome.HomeRotation)>1.1f)&&Time.realtimeSinceStartup<until)yield return null;
   Assert.That(Vector3.Distance(merchant.transform.position,home),Is.LessThan(.22f));Assert.That(Quaternion.Angle(merchant.transform.rotation,returnHome.HomeRotation),Is.LessThan(1.1f));
  }
 }
}
