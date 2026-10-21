using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TaskConsiderationMockTransport : BackendWebSocketClient
    {
        public bool Online=true;
        public TaskRuntimeSyncRequestDto Sync;
        public CognitiveRequest Sent;
        public int SyncCount,SendCount,CancelCount;
        public readonly List<JObject> ExecutionControls=new();
        public readonly List<TaskRuntimeSyncRequestDto> StatusReads=new();
        public override bool IsConnected=>Online;
        public override bool EnsureConnected()=>false;
        public override Task<bool> SendTaskRuntimeSyncAsync(TaskRuntimeSyncRequestDto request)
        { if(request.offer_tasks) { Sync=request; SyncCount++; } else StatusReads.Add(request); return Task.FromResult(true); }
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest request)
        { Sent=request; SendCount++; return Task.FromResult(true); }
        public override Task<bool> SendCognitiveCancelAsync(CognitiveRequest request)
        { CancelCount++; return Task.FromResult(true); }
        public override Task<bool> SendTaskExecutionControlAsync(JObject control)
        { ExecutionControls.Add((JObject)control.DeepClone());return Task.FromResult(Online); }
    }

    // Reused by isolated PlayMode validation. No host identity/config/Backend startup.
    public sealed class TaskConsiderationFixture : IDisposable
    {
        public readonly List<GameObject> Objects=new();
        public readonly GameObject Root;
        public readonly WorldEntity Owner,Player;
        public readonly EntityRegistry Registry;
        public readonly AgentEventRouter Router;
        public readonly TaskRuntimeBridge Bridge;
        public readonly TaskConsiderationSource Source;
        public readonly TaskConsiderationMockTransport Backend;
        public readonly AgentOrchestrator Orchestrator;
        public readonly CognitiveRunDriver Driver;
        public readonly TaskConfirmationTestPresenter Presenter;
        public readonly PerceivedStateStore Perception;
        private CharacterDefinition character;
        private readonly string privatePath;
        public GameObject Make(string name) {var go=new GameObject(name); Objects.Add(go); return go;}
        public static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
        public TaskConsiderationFixture(string ownerId="npc_cafe_01",string stableOwner="cafe.merchant.synthetic",string path=null)
        {
            typeof(TaskRuntimeBridge).GetMethod("ResetRetained",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            privatePath=path??Path.Combine(Path.GetTempPath(),"task3a_"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(privatePath);
            Root=Make("isolated-task3a-owner");
            Owner=Root.AddComponent<WorldEntity>(); Owner.Configure(ownerId,"character.synthetic",EntityKind.Agent,AgentRole.Npc,new PersistentIdentityRef("npc","Character",stableOwner));
            var registryRoot=Make("isolated-task3a-registry"); registryRoot.SetActive(false);
            Registry=registryRoot.AddComponent<EntityRegistry>(); Set(Registry,"autoDiscoverSceneEntities",false); Set(Registry,"logRegistrationsOnStart",false);
            var playerRoot=Make("isolated-task3a-player"); playerRoot.SetActive(false);
            Player=playerRoot.AddComponent<WorldEntity>(); Player.Configure("player_01","character.synthetic_player",EntityKind.Agent,AgentRole.Player,new PersistentIdentityRef("player","Player","synthetic-task-player"));
            var provider=playerRoot.AddComponent<EmbodiedNPC.Tests.StubPlayerIdentityProvider>(); provider.Configure("synthetic-task-player","Synthetic Player");
            var binding=playerRoot.AddComponent<PlayerIdentityBinding>(); binding.Configure(Player,provider,Registry);
            Set(Registry,"entities",new List<WorldEntity>{Owner,Player});
            Registry.RebuildRegistry(); Assert.That(binding.TryApplyIdentity(out var identityError),Is.True,identityError);
            playerRoot.SetActive(true); registryRoot.SetActive(true); Registry.RebuildRegistry();
            var transportRoot=Make("isolated-task3a-transport"); transportRoot.SetActive(false);
            Backend=transportRoot.AddComponent<TaskConsiderationMockTransport>(); SetBaseConnect(Backend); transportRoot.SetActive(true);
            Bridge=Make("isolated-task3a-bridge").AddComponent<TaskRuntimeBridge>(); Assert.That(Bridge.Admit(Registry,Player),Is.True);
            var receiver=Root.AddComponent<AgentEventReceiver>(); receiver.Configure(Owner);
            var wake=Root.AddComponent<WakeCognitiveTriggerBridge>(); wake.Configure(receiver);
            Orchestrator=Root.AddComponent<AgentOrchestrator>(); Orchestrator.Configure(Owner,wake);
            Perception=Root.AddComponent<PerceivedStateStore>();
            if(Perception.Aggregator==null) typeof(PerceivedStateStore).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Perception,null);
            var context=Root.AddComponent<PerceptionContextProvider>();
            typeof(PerceptionContextProvider).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(context,null);
            character=ScriptableObject.CreateInstance<CharacterDefinition>(); character.Configure("cafe.merchant","Synthetic NPC","Cafe employee","Synthetic history","Independent","Brief","No invented facts");
            var composer=Root.AddComponent<CognitiveContextComposer>(); composer.Configure(Owner,character,Orchestrator,wake,receiver,null,context,binding,null,new CharacterOverrideStore(Path.Combine(privatePath,"characters.json")));
            Presenter=Root.AddComponent<TaskConfirmationTestPresenter>();
            Driver=Root.AddComponent<CognitiveRunDriver>(); Driver.Configure(Owner,Orchestrator,composer,Backend,Presenter);
            Router=Make("isolated-task3a-router").AddComponent<AgentEventRouter>(); Router.Configure(new[]{receiver},null,Registry);
            Source=Root.AddComponent<TaskConsiderationSource>(); Source.Configure(Bridge,Registry,Player,Router,Backend,null);
        }
        private static void SetBaseConnect(BackendWebSocketClient value)=>typeof(BackendWebSocketClient).GetField("connectOnStart",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,false);
        public void Visible(bool visible)
        {
            double now=Time.unscaledTimeAsDouble;
            Perception.Aggregator.ApplyOrRefresh(new Observation("visual_"+Guid.NewGuid().ToString("N"),Owner.EntityId,ObservationSourceType.ContinuousScan,"synthetic_vision",ObservationChannel.Visual,Player.EntityId,
                new Dictionary<string,string>{{"visibility_state",visible?"current_visible":"lost"},{"kind","player"},{"display_name","Synthetic Player"}},Array.Empty<string>(),1f,1,now,now+60));
        }
        public void Tick() {Set(Source,"checkAt",0d); Source.Tick();}
        public JObject Snapshot(bool hasTasks=true)
        {
            var sent=Backend.Sync;
            return JObject.FromObject(new {message_type="task_runtime_snapshot",schema_version="1.1",request_id=sent.request_id,agent_entity_id=Owner.EntityId,
                task_runtime=new {sent.task_runtime.command_id,sent.task_runtime.client_session_id,sent.task_runtime.visit_id,sent.task_runtime.scene_instance_id,sent.task_runtime.lifecycle_protocol,sent.task_runtime.player_entity_id},
                owner=new {identity_namespace="npc",identity_type="Character",persistent_id=Owner.PersistentIdentity.PersistentId},
                player=new {identity_namespace="player",identity_type="Player",persistent_id="synthetic-task-player"},
                tasks=Array.Empty<TaskStatusDto>(),opportunity_token=hasTasks?"opportunity_synthetic":null,task_ids=hasTasks?new[]{"task_synthetic"}:Array.Empty<string>()});
        }
        public CognitiveResponse NoAction()=>new CognitiveResponse {message_type="cognitive_response",schema_version="1.1",request_id=Backend.Sent.request_id,trace_id=Backend.Sent.trace_id,
            run_id=Backend.Sent.run.run_id,agent_entity_id=Owner.EntityId,task_result="none",decision=new CognitiveDecision{decision_kind="NO_ACTION"}};
        public void Dispose() {foreach(var go in Objects) if(go!=null) Object.DestroyImmediate(go); if(character!=null) Object.DestroyImmediate(character);}
    }

    public sealed class TaskConsiderationTests
    {
        private TaskConsiderationFixture f;
        [SetUp] public void Setup()=>f=new TaskConsiderationFixture();
        [TearDown] public void Cleanup()=>f.Dispose();
        [Test] public void NoVisionMeansNoSyncAndNoRun() {f.Tick(); Assert.That(f.Backend.SyncCount,Is.Zero); Assert.That(f.Orchestrator.HasActiveRun,Is.False);}
        [Test] public void EmptySnapshotProducesNoModelCall() {f.Visible(true);f.Tick(); Assert.That(f.Backend.ProcessIncomingJson(f.Snapshot(false).ToString()),Is.True);f.Tick();Assert.That(f.Backend.SendCount,Is.Zero);Assert.That(f.Source.LastStatus,Is.EqualTo("no_eligible_tasks"));}
        [Test] public void ActualEventComposerAndDriverUseOneTaskRunAndCanStaySilent()
        {
            f.Visible(true);f.Tick();Assert.That(f.Backend.ProcessIncomingJson(f.Snapshot().ToString()),Is.True);
            Assert.That(f.Backend.SendCount,Is.EqualTo(1));Assert.That(f.Orchestrator.CurrentRun.IsTaskConsideration,Is.True);
            Assert.That(f.Backend.Sent.context.current_event.event_type,Is.EqualTo(AgentEventTypes.TaskConsideration));
            Assert.That(f.Backend.Sent.task_runtime.visit_id,Is.EqualTo(f.Backend.Sync.task_runtime.visit_id));
            Assert.That(f.Driver.TryConsumeResponse(f.NoAction()),Is.True);Assert.That(f.Driver.FlushTaskConsideration(),Is.True);
            f.Source.Configure(f.Bridge,f.Registry,f.Player,f.Router,f.Backend,null);
            f.Tick();Assert.That(f.Backend.SendCount,Is.EqualTo(1));Assert.That(f.Presenter.Count,Is.Zero);
            Assert.That(f.Backend.SyncCount,Is.EqualTo(1));
        }
        [TestCase(true)] [TestCase(false)]
        public void RetainedDialogueBlocksTaskOpportunityOnlyWhileVisible(bool visible)
        {
            var dialogue = f.Make("isolated-retained-dialogue").AddComponent<EmbodiedNPC.Dialogue.DialogueController>();
            TaskConsiderationFixture.Set(dialogue, "activeContext", new EmbodiedNPC.Dialogue.DialogueContext {
                conversationId="retained", speakerEntityId=f.Player.EntityId, targetAgentId=f.Owner.EntityId });
            TaskConsiderationFixture.Set(dialogue, "presentationVisible", visible);
            f.Source.Configure(f.Bridge,f.Registry,f.Player,f.Router,f.Backend,dialogue);
            f.Visible(true); f.Tick();
            Assert.That(dialogue.HasActiveSession, Is.True);
            Assert.That(f.Backend.SyncCount, Is.EqualTo(visible ? 0 : 1));
            Assert.That(f.Backend.SendCount, Is.Zero, "A retained session never creates or executes work by itself.");
        }
        [Test] public void BusyNpcRetainsOnePendingThenRechecksVisibility()
        {
            f.Orchestrator.TryStartRun(new CognitiveTrigger("busy","event",f.Owner.EntityId,"busy_event",1,"player.utterance"),out _);
            f.Visible(true);f.Tick();f.Tick();Assert.That(f.Source.HasPendingCheck,Is.True);Assert.That(f.Backend.SyncCount,Is.Zero);
            f.Visible(false);f.Orchestrator.CompleteCurrentRun();f.Tick();Assert.That(f.Backend.SyncCount,Is.Zero);
            f.Visible(true);f.Tick();Assert.That(f.Backend.SyncCount,Is.EqualTo(1));
        }
        [Test] public void PlayerLeavingCancelsTaskRunAndSuppressesLateResponse()
        {
            f.Visible(true);f.Tick();f.Backend.ProcessIncomingJson(f.Snapshot().ToString());var response=f.NoAction();
            f.Visible(false);f.Tick();Assert.That(f.Orchestrator.HasActiveRun,Is.False);Assert.That(f.Driver.TryConsumeResponse(response),Is.False);Assert.That(f.Backend.CancelCount,Is.GreaterThan(0));
        }
        [TestCase("request_id")][TestCase("agent_entity_id")][TestCase("scene_instance_id")][TestCase("owner")][TestCase("player")]
        public void MismatchedSyncDoesNotWake(string field)
        {
            f.Visible(true);f.Tick();var value=f.Snapshot();
            if(field=="scene_instance_id") value["task_runtime"][field]="other_scene";
            else if(field=="owner"||field=="player") value[field]["persistent_id"]="other";
            else value[field]="other";
            f.Backend.ProcessIncomingJson(value.ToString());Assert.That(f.Backend.SendCount,Is.Zero);
        }
        [Test] public void UnknownControlFieldsRemainRejected() {f.Visible(true);f.Tick();var value=f.Snapshot();value["grant_execution"]=true;Assert.That(TaskRuntimeSnapshot.TryParse(value.ToString(),out _),Is.False);}
        [Test] public void EvenValidActionEnvelopeCannotEnterTaskWorldHandoff()
        {
            f.Visible(true);f.Tick();f.Backend.ProcessIncomingJson(f.Snapshot().ToString());var response=f.NoAction();
            response.decision=new CognitiveDecision {decision_kind="ACTION_PROPOSAL",action_proposal=new CognitiveActionProposal {action_id="prepare_drink",bindings=new Dictionary<string,string>{{"drink_type","latte"}}}};
            Assert.That(CognitiveActionProposalAdapter.TryMap(response.decision.action_proposal,out response.validated_goal),Is.True);
            Assert.That(response.IsValid,Is.True);Assert.That(f.Driver.TryConsumeResponse(response),Is.False);
            Assert.That(f.Source.LastStatus,Does.Contain("TASK_EXECUTION_TICKET_INVALID"));Assert.That(f.Presenter.Count,Is.Zero);
        }
        [Test] public void PlayerUtteranceCancelsBackgroundBeforeAnotherOpportunity()
        {
            f.Visible(true);f.Tick();f.Backend.ProcessIncomingJson(f.Snapshot().ToString());var old=f.NoAction();
            f.Root.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("foreground_event",AgentEventTypes.PlayerUtterance,f.Player.EntityId,f.Owner.EntityId,null,
                new Dictionary<string,string>{{"text","你好"}},Time.unscaledTimeAsDouble));
            Assert.That(f.Driver.TryConsumeResponse(old),Is.False);Assert.That(f.Backend.CancelCount,Is.GreaterThan(0));
            Assert.That(f.Source.HasPendingCheck,Is.False);
        }
        [Test] public void AutomaticResponseKeepsFrozenChineseHardLimit()
        {
            f.Visible(true);f.Tick();f.Backend.ProcessIncomingJson(f.Snapshot().ToString());f.Backend.Sent.reply_language="zh-CN";
            var response=f.NoAction();response.decision=new CognitiveDecision {decision_kind="DIRECT_RESPONSE",utterance=new string('字',151)};
            Assert.That(response.IsValid,Is.True); // Wire-only ceiling is 250; the owning Run applies Chinese 150.
            Assert.That(f.Driver.TryConsumeResponse(response),Is.False);Assert.That(f.Presenter.Count,Is.Zero);
            Assert.That(f.Source.LastStatus,Does.Contain("UTTERANCE_HARD_LIMIT_EXCEEDED"));
        }
        [Test] public void ReloadedBridgeKeepsVisitButChangesSceneToken()
        {
            f.Visible(true);f.Tick();var runtime=f.Backend.Sync.task_runtime;
            var next=f.Make("new_scene_bridge").AddComponent<TaskRuntimeBridge>();Assert.That(next.Admit(f.Registry,f.Player),Is.True);
            Assert.That(next.TryCapture(f.Backend.Sync.recall_identity,out var nextRuntime),Is.True);
            Assert.That(nextRuntime.visit_id,Is.EqualTo(runtime.visit_id));Assert.That(nextRuntime.scene_instance_id,Is.Not.EqualTo(runtime.scene_instance_id));
        }
    }
}
