using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;
namespace CafeDemo.Tests {
 public sealed class CafeGoalControlE2ETests {
  const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
  static string E=>Environment.GetEnvironmentVariable("GOAL_CONTROL_EVIDENCE");
  CafeP1aLifecycleTests f; CognitiveDialogueSubmissionHandler merchant; CognitiveRunDriver driver;
  AgentOrchestrator orchestrator;GoalPlanRunner runner;DialogueController dialogue;WorldEntity player;WorldStateService world;
  BackendWebSocketClient backend;TaskRuntimeBridge bridge;TaskConsiderationSource source;
  List<GoalOutcome> outcomes;List<PresentedCognitiveUtterance> utterances;
  T Field<T>(string n)=>(T)typeof(CafeP1aLifecycleTests).GetField(n,F).GetValue(f);
  static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,F).Invoke(o,a);
  static void Save(string n,object o)=>File.WriteAllText(Path.Combine(E,n+".json"),JsonConvert.SerializeObject(o,Formatting.Indented));
  IEnumerator Wait(Func<bool> p,float seconds,string reason){double until=Time.realtimeSinceStartupAsDouble+seconds;while(!p()&&Time.realtimeSinceStartupAsDouble<until)yield return null;Assert.That(p(),Is.True,reason+" run="+orchestrator?.CurrentRun?.State+" task="+source?.LastStatus+" action="+driver?.LastActionHandoffResult);}
  [UnitySetUp]public IEnumerator Setup(){
   if(string.IsNullOrEmpty(E)) Assert.Ignore("Opt-in isolated native Backend acceptance; requires GOAL_CONTROL_EVIDENCE.");
   f=new CafeP1aLifecycleTests();Call(f,"PrepareScene");
   foreach(var id in Object.FindObjectsByType<LocalTestIdentityProvider>(FindObjectsInactive.Include))id.Configure("goal-cancel-20261026-player","验收玩家",1,DateTimeOffset.UtcNow);
   yield return new EnterPlayMode();f=new CafeP1aLifecycleTests();Call(f,"BindScene");
   merchant=Field<CognitiveDialogueSubmissionHandler>("merchant");driver=Field<CognitiveRunDriver>("driver");orchestrator=Field<AgentOrchestrator>("orchestrator");runner=Field<GoalPlanRunner>("runner");dialogue=Field<DialogueController>("dialogue");player=Field<WorldEntity>("player");world=Field<WorldStateService>("world");outcomes=Field<List<GoalOutcome>>("outcomes");utterances=Field<List<PresentedCognitiveUtterance>>("utterances");
   backend=Object.FindAnyObjectByType<BackendWebSocketClient>(FindObjectsInactive.Include);backend.enabled=true;
   bridge=Object.FindAnyObjectByType<TaskRuntimeBridge>(FindObjectsInactive.Include);bridge.enabled=true;
   Assert.That(bridge.Admit(Object.FindAnyObjectByType<EntityRegistry>(),player),Is.True);
   driver.Configure(merchant.GetComponent<WorldEntity>(),orchestrator,merchant.GetComponent<CognitiveContextComposer>(),backend,(MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour",F).GetValue(driver));driver.enabled=true;
   backend.EnsureConnected();yield return Wait(()=>backend.IsConnected,18,"real WebSocket connect");bridge.Register(merchant.GetComponent<WorldEntity>(),backend);
   source=merchant.GetComponent<TaskConsiderationSource>()??merchant.gameObject.AddComponent<TaskConsiderationSource>();source.enabled=false;
   source.Configure(bridge,Object.FindAnyObjectByType<EntityRegistry>(),player,Object.FindAnyObjectByType<AgentEventRouter>(),backend,dialogue);
   var executor=typeof(GoalPlanRunner).GetField("executor",F).GetValue(runner);var set=typeof(PlanExecutor).GetField("runnerSet",F).GetValue(executor);
   ((Dictionary<PlannerActionType,IPlanActionRunner>)typeof(PlanActionRunnerSet).GetField("runners",F).GetValue(set))[PlannerActionType.PrepareDrink]=new PrepareDrinkActionRunner(20);
   Save("runtime-"+"control",new{pid=System.Diagnostics.Process.GetCurrentProcess().Id,session=ClientSessionIdentity.Current,scene=bridge.DiagnosticSceneInstanceId,initialState=runner.CaptureExecutionState(player.EntityId),worldRevision=world.WorldRevision,drinks=world.GetDrinkCount(player.EntityId,"latte")});
  }
  [UnityTearDown]public IEnumerator Teardown(){if(string.IsNullOrEmpty(E))yield break;if(Application.isPlaying)yield return new ExitPlayMode();UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);}
  CognitiveRequest Submit(string text){var previous=merchant.GetComponent<CognitiveContextComposer>().LastRequest;Call(dialogue,"HandleSubmitted",new UserUtterance{conversationId=dialogue.CurrentConversationId,speakerEntityId=player.EntityId,targetAgentId=merchant.GetComponent<WorldEntity>().EntityId,inputMode="text",text=text});Assert.That(merchant.GetComponent<CognitiveContextComposer>().LastRequest,Is.Not.SameAs(previous),"actual dialogue input accepted");var request=merchant.GetComponent<CognitiveContextComposer>().LastRequest;Save("request-"+request.request_id,JObject.Parse(request.Serialize()));return request;}
  IEnumerator Speak(string text){int before=utterances.Count;var r=Submit(text);yield return Wait(()=>utterances.Any(u=>u.RequestId==r.request_id),55,"present response "+text);Assert.That(utterances.Count,Is.GreaterThan(before));Save("speech-"+r.request_id,new{request_id=r.request_id,text=utterances.Last(u=>u.RequestId==r.request_id).Utterance,conversation=dialogue.CurrentConversationId,runtime=r.context.runtime_state});}
  IEnumerator Flush(){yield return Wait(()=>backend.PendingExperienceCount==0,25,"Ledger acknowledgements");}
  [UnityTest] public IEnumerator Cappuccino_StopFromPlayerInput() => CancelCase("cappuccino", "请现在给我做一杯卡布奇诺。", "不对，先别做了");
  [UnityTest] public IEnumerator Latte_StopIsCancellationNotPause() => CancelCase("latte", "请现在给我做一杯拿铁。", "先停一下");
  IEnumerator CancelCase(string drink,string order,string stop) {
   yield return Speak(order);
   var original=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
   Assert.That(runner.HasActiveTask,Is.True,"model must propose and Unity accept an immediate goal");
   string proposal=runner.ActiveProposalId;double accepted=Time.realtimeSinceStartupAsDouble;
   yield return Wait(()=>runner.CurrentTaskFeedback?.Progress.Action==PlannerActionType.PrepareDrink,12,"preparing");
   Call(f,"MovePlayer",merchant.transform.position+merchant.transform.right*1.2f);
   Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()),Is.True);
   yield return new WaitForSecondsRealtime(2);Assert.That(outcomes,Is.Empty);
   double cancellationInput=Time.realtimeSinceStartupAsDouble;
   yield return Speak(stop);
   var control=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
   Assert.That(control.context.runtime_state.status,Is.EqualTo("IN_PROGRESS"));
   Assert.That(driver.LastActionControlResult,Is.EqualTo("cancelled"));
   Assert.That(runner.HasActiveTask,Is.False);Assert.That(outcomes,Has.Count.EqualTo(1));
   Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Cancelled));
   Assert.That(outcomes[0].ProposalId,Is.EqualTo(proposal));Assert.That(outcomes[0].TraceId,Is.EqualTo(original.trace_id));
   Assert.That(world.GetDrinkCount(player.EntityId,drink),Is.Zero);Assert.That(world.WorldRevision,Is.Zero);
   Assert.That(world.TryGetDrinkReceipt(merchant.GetComponent<WorldEntity>().EntityId,proposal,original.trace_id,out _),Is.False);
   yield return Speak("刚才那杯咖啡现在是什么状态？");
   var followup=merchant.GetComponent<CognitiveContextComposer>().LastRequest;
   Assert.That(followup.context.runtime_state.status,Is.EqualTo("CANCELLED"));
   Assert.That(followup.context.runtime_state.proposal_id,Is.EqualTo(proposal));
   Assert.That(followup.context.runtime_state.receipt_id,Is.Null);
   Assert.That(utterances.Last().Utterance.Contains("我不会说")||utterances.Last().Utterance.Contains("我不说它"),Is.False,"internal contract must not become NPC dialogue");
   yield return new WaitForSecondsRealtime(1);Assert.That(outcomes,Has.Count.EqualTo(1));
   yield return Flush();
   Save("case-"+drink,new {drink,seconds=20,accepted,cancellationInput,cancelPresented=utterances.First(u=>u.RequestId==control.request_id).PresentedAt,
    original=original.request_id,cancel=control.request_id,followup=followup.request_id,proposal,controlResult=driver.LastActionControlResult,
    state=runner.CaptureExecutionState(player.EntityId),worldRevision=world.WorldRevision,drinks=world.GetDrinkCount(player.EntityId,drink),
    outcomes=outcomes.Select(o=>new{o.OutcomeId,o.ProposalId,o.TraceId,status=o.Status.ToString(),o.FailureCode,o.WorldRevision}),
    utterances=utterances.Select(u=>new{request_id=u.RequestId,text=u.Utterance,conversation=u.ConversationId,at=u.PresentedAt})});
  }
 }
}
