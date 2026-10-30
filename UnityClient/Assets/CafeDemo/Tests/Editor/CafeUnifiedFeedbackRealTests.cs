using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
 // Initial actions and fault injection reuse the deterministic fixture. Only feedback uses the real native WS/provider.
 public sealed class CafeUnifiedFeedbackRealTests : PlayerPresentationFixture
 {
  protected override bool UsesStartupPanel=>false;
  protected override bool ClearSceneAfterTest=>true;
  const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic;
  static string E=>Environment.GetEnvironmentVariable("C3_REAL_EVIDENCE");
  string folder;
  CafeReplyPresentationLifecycleTests fixture;
  ReplyLifecycleFakeBackend fake;
  BackendWebSocketClient wire;
  CognitiveRunDriver driver;
  GoalPlanRunner runner;
  DialogueController dialogue;
  readonly List<CognitiveResponse> responses=new();
  readonly List<CognitiveTransportFailure> failures=new();
  readonly List<GoalOutcome> outcomes=new();
  readonly List<GoalPlanPresentation> terminal=new();
  readonly List<SkillResult> effects=new();
  int sentDrafts;
  static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,F|BindingFlags.Public).Invoke(target,args);
  static void Set(object target,string name,object value){var t=target.GetType();while(t!=null&&t.GetField(name,F)==null)t=t.BaseType;t.GetField(name,F).SetValue(target,value);}
  T Field<T>(string name)=>(T)typeof(CafeReplyPresentationLifecycleTests).GetField(name,F).GetValue(fixture);
  void Save(string name,object value)=>File.WriteAllText(Path.Combine(folder,name+".json"),JsonConvert.SerializeObject(value,Formatting.Indented));
  CognitiveRequest Send(string text)=>(CognitiveRequest)Call(fixture,"Send",text);
  IEnumerator Until(Func<bool> condition,float seconds,string reason){double end=Time.realtimeSinceStartupAsDouble+seconds;while(!condition()&&Time.realtimeSinceStartupAsDouble<end)yield return null;Assert.That(condition(),Is.True,reason+" presentation="+driver?.LastActionOutcomePresentationResult);}
  IEnumerator TaskResult(Task<bool> task){yield return Until(()=>task.IsCompleted,10,"native send completes");Assert.That(task.IsFaulted,Is.False);Assert.That(task.Result,Is.True);}

  [UnitySetUp] public IEnumerator ConfigureRealFeedback()
  {
   if(string.IsNullOrEmpty(E))Assert.Ignore("Opt-in isolated C3 acceptance requires C3_REAL_EVIDENCE.");
   string name=TestContext.CurrentContext.Test.MethodName;folder=Path.Combine(E,"cases",name.Substring(0,1)+"-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(folder);
   File.WriteAllText(Path.Combine(E,"case-mode.json"),JsonConvert.SerializeObject(new{paid=!name.StartsWith("Native"),@case=name}));
   fixture=new CafeReplyPresentationLifecycleTests();
   foreach(var field in typeof(PlayerPresentationFixture).GetFields(F|BindingFlags.Public))field.SetValue(fixture,field.GetValue(this));
   yield return (IEnumerator)Call(fixture,"PrepareReplyFixture");
   fake=Field<ReplyLifecycleFakeBackend>("fake");driver=Field<CognitiveRunDriver>("driver");dialogue=Field<DialogueController>("dialogue");
   runner=merchant.GetComponent<GoalPlanRunner>();world.SkillResultPublished+=effects.Add;
   var root=new GameObject("C3RealNativeTransport");root.SetActive(false);wire=root.AddComponent<BackendWebSocketClient>();
   Set(wire,"connectOnStart",false);Set(wire,"serverUrl","ws://127.0.0.1:18963/ws");root.SetActive(true);
   wire.CognitiveResponseReceived+=response=>{responses.Add(response);Save("responses",responses);};
   wire.CognitiveRequestFailed+=failure=>{failures.Add(failure);Save("transport-failures",failures);driver.TryConsumeFailure(failure);};
   fake.ForwardRequest=request=>{Save("request-"+request.request_id,JsonConvert.DeserializeObject(request.Serialize()));return request.action_feedback==null?Task.FromResult(true):wire.SendCognitiveRequestAsync(request);};
   fake.ForwardCancel=request=>{Save("cancel-"+request.request_id,new{request.request_id,request.trace_id,request.run.run_id});return wire.SendCognitiveCancelAsync(request);};
   wire.EnsureConnected();yield return Until(()=>wire.IsConnected,15,"isolated native WS connects");
   Save("environment",new{unity=Application.unityVersion,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
    initialWorld=world.WorldRevision,session=ClientSessionIdentity.Current,test=TestContext.CurrentContext.Test.MethodName,run=Environment.GetEnvironmentVariable("C3_RUN_STEM"),
    note="Scripted initial proposal; real executor/fault receipt, native WS, existing CognitiveBackendEntry, paid feedback only"});
  }
  [UnityTearDown] public IEnumerator PreserveEvidence()
  {
   if(!string.IsNullOrEmpty(folder))
   {
    Save("outcomes",outcomes);Save("effects",effects);Save("terminal",terminal);
    Save("requests",fake?.Requests.Select(r=>JsonConvert.DeserializeObject(r.Serialize())).ToArray());
    Save("shown",fixture==null?null:Field<List<PresentedCognitiveUtterance>>("shown"));
    Save("drafts",fixture==null?null:Field<List<ExperienceDraft>>("drafts").Select(ExperienceAppendRequest.FromDraft).ToArray());
    Save("bubble-messages",Object.FindAnyObjectByType<DialogueBubbleManager>()?.GetMessages(merchant.EntityId));
   }
   if(wire!=null)wire.enabled=false;
   yield return null;
  }
  IEnumerator FlushLedger()
  {
   var drafts=Field<List<ExperienceDraft>>("drafts");
   while(sentDrafts<drafts.Count){var packet=ExperienceAppendRequest.FromDraft(drafts[sentDrafts++]);yield return TaskResult(wire.SendExperienceAppendRequestAsync(packet));}
   yield return Until(()=>wire.PendingExperienceCount==0,12,"actual Ledger ACKs");
  }
  IEnumerator FinalContext()
  {
   // A real next input supplies the trusted read cutoff; no extra paid inference is sent.
   merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
   Place(GameObject.Find("CounterFrontPoint").transform.position);
   Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()),Is.True);
   var next=Send("刚才的尝试结果是什么？");Save("next-context-request",JsonConvert.DeserializeObject(next.Serialize()));
   driver.CancelPendingDialogueRequest("acceptance_context_cutoff");yield return FlushLedger();
  }
  IEnumerator AwaitModel(CognitiveRequest request)
  {
   yield return Until(()=>responses.Any(r=>r.request_id==request.request_id)||failures.Count>0,75,"real feedback response");
   Assert.That(failures,Is.Empty,"Preserve the transport failure, never replace it with a scripted response.");
   Assert.That(driver.PendingRequest,Is.SameAs(request),"A timeout must not be hidden by late delivery.");
  }
  void Deliver(CognitiveRequest request)
  {
   var response=responses.Single(r=>r.request_id==request.request_id);Assert.That(fake.Reply(response),Is.True);
   Call(fixture,"AssertWorldReply",request);
   Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
   foreach(var value in terminal.ToArray())CallStaticReplay(value);
   Assert.That(fake.Requests.Count(r=>r.action_feedback!=null),Is.EqualTo(1));
   fake.Reply(response);Assert.That(Field<List<PresentedCognitiveUtterance>>("shown").Count(s=>s.RequestId==request.request_id),Is.EqualTo(1));
  }
  void CallStaticReplay(GoalPlanPresentation p)=>typeof(CafeReplyPresentationLifecycleTests).GetMethod("ReplayTerminal",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{driver,p});

  [UnityTest] public IEnumerator Real_FailedDrinkExplainsWithClosedInputAndOneLedgerReply()
  {
   yield return (IEnumerator)Call(fixture,"FailDrink",outcomes,terminal);var request=driver.PendingRequest;
   dialogue.HideDialoguePresentation("acceptance_close");yield return AwaitModel(request);Deliver(request);
   Assert.That(world.GetDrinkCount(player.GetComponent<WorldEntity>().EntityId,"latte"),Is.Zero);
   Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(dialogue.IsPresentationVisible,Is.False);
   yield return FinalContext();
  }
  [UnityTest] public IEnumerator Real_PartialGiveFailurePreservesPickupAndExplains()=>Recovery(true);
  [UnityTest] public IEnumerator Real_AuthorizedAlternativeRevalidatesAndExecutesOnce()=>Recovery(false);
  sealed class FailingGive:ImmediatePlanActionRunner { public override PlannerActionType ActionType=>PlannerActionType.Give;protected override SkillResult Execute(PlanActionExecutionContext c)=>c.WorldState.RecordAttempt(c.SkillId,c.TraceId,"Give",c.Bindings.Actor,c.Bindings.Target,c.Bindings.Destination,SkillStatus.Failed,"C3_GIVE_UNAVAILABLE"); }
  sealed class FailingPickUp:ImmediatePlanActionRunner {public override PlannerActionType ActionType=>PlannerActionType.PickUp;protected override SkillResult Execute(PlanActionExecutionContext c)=>c.WorldState.RecordAttempt(c.SkillId,c.TraceId,"PickUp",c.Bindings.Actor,c.Bindings.Target,null,SkillStatus.Failed,"C3_PICKUP_UNAVAILABLE");}
  IEnumerator Recovery(bool partial)
  {
   var registry=Object.FindAnyObjectByType<EntityRegistry>();Assert.That(registry.TryGetEntity("cup_01",out var first),Is.True);
   var secondObject=Object.Instantiate(first.gameObject);secondObject.name="C3_RealAuthorizedSecondCup";
   var second=secondObject.GetComponent<WorldEntity>();second.Configure("cup_recovery_02",first.DefinitionId,EntityKind.Object,AgentRole.None);registry.Register(second);
   second.transform.position=first.transform.position+Vector3.right*.35f;
   Place(GameObject.Find("CounterFrontPoint").transform.position);
   merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(first.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));
   var vision=merchant.GetComponent<VisionSensor>();vision.enabled=true;vision.ScanNow();Assert.That(vision.CurrentVisibleEntityIds,Does.Contain(first.EntityId));
   Call(fixture,"ObserveForExecution",first);Call(fixture,"ObserveForExecution",second);Call(fixture,"ObserveForExecution",player.GetComponent<WorldEntity>());
   merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
   var executor=typeof(GoalPlanRunner).GetField("executor",F).GetValue(runner);
   var set=(PlanActionRunnerSet)executor.GetType().GetField("runnerSet",F).GetValue(executor);
   var runners=(Dictionary<PlannerActionType,IPlanActionRunner>)typeof(PlanActionRunnerSet).GetField("runners",F).GetValue(set);
   var fault=partial?PlannerActionType.Give:PlannerActionType.PickUp;var realRunner=runners[fault];runners[fault]=partial?(IPlanActionRunner)new FailingGive():new FailingPickUp();
   runner.GoalOutcomePublished+=outcomes.Add;var adapter=new GoalOutcomeExperienceAdapter(merchant.EntityId,Field<List<ExperienceDraft>>("drafts").Add,registry,world);runner.GoalOutcomePublished+=adapter.Handle;
   runner.PresentationReady+=p=>{if(p.Outcome.HasValue)terminal.Add(p);};
   var initial=Send("请把这组已授权杯子中的一只拿给我。");
   var response=(CognitiveResponse)typeof(CafeReplyPresentationLifecycleTests).GetMethod("Reply",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{initial,"我试试。"});
   response.decision.decision_kind="ACTION_PROPOSAL";response.decision.action_proposal=new CognitiveActionProposal{action_id="bring_object",bindings=new Dictionary<string,string>{{"target",first.EntityId},{"destination","player_01"}}};
   response.validated_goal=new GoalProposal{type="bring_object",target_entity_id=first.EntityId,destination_entity_id="player_01"};
   vision.ScanNow();Assert.That(fake.Reply(response),Is.True);
   Set(runner,"activeAuthorization",new ExecutionAuthorization("bring_object",ActionSelectionMode.Any,new[]{first.EntityId,second.EntityId},ActionConfirmationPolicy.None,0,world.WorldRevision));
   yield return Until(()=>outcomes.Count>0,25,"executor reaches real failure");
   Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(outcomes[0].Status,Is.EqualTo(PlanExecutionStatus.Failed));
   var request=driver.PendingRequest;Assert.That(request?.action_feedback,Is.Not.Null);int committed=world.WorldRevision;
   if(!partial)
   {
    runners[fault]=realRunner;var pos=merchant.transform.position+Vector3.forward*.9f;pos.y=second.transform.position.y;second.transform.position=pos;
    Assert.That(NavMesh.SamplePosition(merchant.transform.position+Vector3.forward*.7f,out var hit,1,NavMesh.AllAreas),Is.True);
    var approach=new GameObject("C3RealRecoveryApproach").transform;approach.SetParent(second.transform,true);approach.position=hit.position;
    var grab=new GameObject("C3RealRecoveryGrab").transform;grab.SetParent(second.transform,false);
    Set(second.GetComponent<InteractionAnchorProvider>(),"pickupApproachPoint",approach);Set(second.GetComponent<InteractionAnchorProvider>(),"grabPoint",grab);
    Assert.That(new NavMeshPlannerNavigationProbe(merchant.GetComponent<NavMeshAgent>()).HasCompletePath(merchant.transform.position,approach.position),Is.True);
    merchant.transform.rotation=Quaternion.LookRotation(Vector3.ProjectOnPlane(second.GetComponent<PerceivableEntity>().VisualAnchor.position-merchant.transform.position,Vector3.up));vision.ScanNow();
    Call(fixture,"ObserveForExecution",second);Call(fixture,"ObserveForExecution",player.GetComponent<WorldEntity>());
   }
   Save("world-before-reply",new{worldRevision=committed,holder=first.GetComponent<HeldItemState>().HolderEntityId,completed=request.action_feedback.completed_actions});
   yield return AwaitModel(request);var decision=responses.Single(r=>r.request_id==request.request_id).decision;
   Deliver(request);
   if(partial)
   {
    Assert.That(world.WorldRevision,Is.EqualTo(committed));Assert.That(first.GetComponent<HeldItemState>().HolderEntityId,Is.EqualTo(merchant.EntityId));
    Assert.That(outcomes,Has.Count.EqualTo(1));Assert.That(effects.Count(s=>s.SkillType=="PickUp"&&s.TargetEntityId==first.EntityId&&s.Status==SkillStatus.Succeeded),Is.EqualTo(1));
   }
   else
   {
    Assert.That(decision.recovery_alternative_id,Is.EqualTo("bring_"+second.EntityId),"Record a legitimate model decline; do not rewrite the decision or resample.");
    yield return Until(()=>outcomes.Count==2,25,"revalidated alternative completes");Assert.That(outcomes[1].Status,Is.EqualTo(PlanExecutionStatus.Succeeded));
    Assert.That(outcomes[1].ProposalId,Is.EqualTo(outcomes[0].ProposalId));Assert.That(outcomes[1].TraceId,Is.EqualTo(outcomes[0].TraceId));Assert.That(outcomes[1].ProposalRevision,Is.EqualTo(1));
    Assert.That(second.GetComponent<HeldItemState>().HolderEntityId,Is.EqualTo("player_01"));
    Assert.That(effects.Count(s=>s.SkillType=="PickUp"&&s.TargetEntityId==second.EntityId&&s.Status==SkillStatus.Succeeded),Is.EqualTo(1));
   }
   Save("world-after-reply",new{worldRevision=world.WorldRevision,firstHolder=first.GetComponent<HeldItemState>().HolderEntityId,secondHolder=second.GetComponent<HeldItemState>().HolderEntityId});
   yield return FinalContext();
  }
  [UnityTest] public IEnumerator Native_PreemptCancelsBackendWorkerBeforeReply()=>Preempt(false);
  [UnityTest] public IEnumerator Real_NewUtterancePreemptsRunningFeedback()=>Preempt(true);
  IEnumerator Preempt(bool paid)
  {
   yield return (IEnumerator)Call(fixture,"FailDrink",outcomes,terminal);var old=driver.PendingRequest;
   yield return Until(()=>File.Exists(Path.Combine(E,"provider-started-"+old.request_id+".json")),8,"backend model worker starts");
   var next=Send("先聊别的，你平时喜欢做什么？");var run=merchant.GetComponent<AgentOrchestrator>().CurrentRun;
   Assert.That(fake.Cancels.Count(r=>r.request_id==old.request_id),Is.EqualTo(1));
   yield return Until(()=>File.Exists(Path.Combine(E,"provider-cancelled-"+old.request_id+".json")),8,"native cancellation reaches the existing backend worker");
   Assert.That(driver.PendingRequest,Is.SameAs(next));Assert.That(merchant.GetComponent<AgentOrchestrator>().CurrentRun,Is.SameAs(run));
   var response=(CognitiveResponse)typeof(CafeReplyPresentationLifecycleTests).GetMethod("Reply",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{next,"我平时喜欢看街上的招牌。"});
   fake.Reply(response);Call(fixture,"AssertWorldReply",next);
   Assert.That(Field<List<PresentedCognitiveUtterance>>("shown").Any(s=>s.RequestId==old.request_id),Is.False);Assert.That(outcomes,Has.Count.EqualTo(1));
   yield return FlushLedger();
  }
 }
}
