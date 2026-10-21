using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TaskRuntimeSubscriptionTests
    {
        private static readonly string[] Events = {
            "TaskRuntimeSnapshotReceived", "TaskExecutionResultReceived",
            "TaskCancelResultReceived", "ConnectionStatusChanged"
        };
        private static int SubscriptionCount(BackendWebSocketClient backend, TaskRuntimeBridge bridge, string name)
        {
            var handlers = (Delegate)typeof(BackendWebSocketClient).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(backend);
            return handlers?.GetInvocationList().Count(h => ReferenceEquals(h.Target, bridge)) ?? 0;
        }
        private static void AssertSubscriptions(BackendWebSocketClient backend, TaskRuntimeBridge bridge, int expected)
        {
            foreach (string name in Events)
                Assert.That(SubscriptionCount(backend, bridge, name), Is.EqualTo(expected), name);
        }
        private static void Lifecycle(TaskRuntimeBridge bridge, string name) =>
            typeof(TaskRuntimeBridge).GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(bridge, null);
        private static void Enable(TaskRuntimeBridge bridge, bool enabled)
        {
            bridge.enabled = enabled;
            // EditMode deterministically exercises the actual callbacks; repeated
            // delivery is intentional and must remain harmless.
            Lifecycle(bridge, enabled ? "OnEnable" : "OnDisable");
        }

        [Test] public void SameTransportRegisterAfterDisableEnableRestoresAllSubscriptions()
        {
            using var f = new TaskConsiderationFixture();
            AssertSubscriptions(f.Backend, f.Bridge, 1);
            Enable(f.Bridge, false);
            AssertSubscriptions(f.Backend, f.Bridge, 0);
            Enable(f.Bridge, true);
            f.Bridge.Register(f.Owner, f.Backend);
            AssertSubscriptions(f.Backend, f.Bridge, 1);
            Assert.That(f.Backend.ExecutionControls, Is.Empty);
            Assert.That(f.Backend.ConnectionAttempts, Is.Zero);
        }

        private static void Connection(TaskConsiderationMockTransport backend, bool connected)
        {
            backend.Online = connected;
            var handlers = (Action<string>)typeof(BackendWebSocketClient).GetField("ConnectionStatusChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(backend);
            handlers?.Invoke(connected ? "CONNECTED" : "DISCONNECTED");
        }
        private static JObject Row(string status="waiting", int version=1) => new() {
            ["task_id"]="subscription_task", ["version"]=version, ["status"]=status,
            ["source"]="player_request", ["goal_kind"]="serve_drink", ["attempt_id"]=null,
            ["phase"]=null, ["cancel_requested"]=false, ["diagnostic"]=null, ["condition"]=null
        };
        private static JObject Identity(CognitiveRecallSubjectIdentityDto value) => new() {
            ["identity_namespace"]=value.identity_namespace, ["identity_type"]=value.identity_type,
            ["persistent_id"]=value.persistent_id
        };
        private static JObject Snapshot(TaskRuntimeSyncRequestDto request, string status="waiting", int version=1) => new() {
            ["message_type"]="task_runtime_snapshot", ["schema_version"]="1.1", ["request_id"]=request.request_id,
            ["agent_entity_id"]=request.agent_entity_id,
            ["task_runtime"]=JObject.Parse(CognitiveRequest.SerializeFields(request.task_runtime)),
            ["owner"]=Identity(request.recall_identity.observer), ["player"]=Identity(request.recall_identity.current_subjects[0]),
            ["opportunity_token"]=null, ["task_ids"]=new JArray(), ["tasks"]=new JArray(Row(status,version))
        };

        [Test] public void RepeatedRegisterHasOneCallbackAndNeverStartsExecution()
        {
            using var f = new TaskConsiderationFixture();
            for(int i=0;i<5;i++) f.Bridge.Register(f.Owner,f.Backend);
            AssertSubscriptions(f.Backend,f.Bridge,1);
            int before=f.Backend.StatusReads.Count;
            Connection(f.Backend,true);
            Assert.That(f.Backend.StatusReads.Count-before,Is.EqualTo(1),"Exactly one bridge connection callback.");
            Assert.That(f.Backend.ExecutionControls,Is.Empty);
            Assert.That(f.Backend.SendCount,Is.Zero);
        }

        [Test] public void DisabledRegisterStaysUnsubscribedAndEnableAloneRestoresStatus()
        {
            using var f = new TaskConsiderationFixture();
            Enable(f.Bridge,false);
            int before=f.Backend.StatusReads.Count;
            f.Bridge.Register(f.Owner,f.Backend);
            AssertSubscriptions(f.Backend,f.Bridge,0);
            Assert.That(f.Backend.StatusReads.Count,Is.EqualTo(before));
            Enable(f.Bridge,true);
            AssertSubscriptions(f.Backend,f.Bridge,1);
            Assert.That(f.Backend.StatusReads.Count,Is.GreaterThan(before));
            f.Backend.ProcessIncomingJson(Snapshot(f.Backend.StatusReads.Last()).ToString());
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId).Single().status,Is.EqualTo("waiting"));
        }

        [Test] public void ReenableDropsOldQueriesAndAcceptsOnlyFreshSnapshot()
        {
            using var f = new TaskConsiderationFixture();
            var old=Snapshot(f.Backend.StatusReads.Last());
            Enable(f.Bridge,false);Enable(f.Bridge,true);f.Bridge.Register(f.Owner,f.Backend);
            Assert.That(f.Backend.ProcessIncomingJson(old.ToString()),Is.True);
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId),Is.Empty);
            var fresh=Snapshot(f.Backend.StatusReads.Last(),version:2);
            Assert.That(f.Backend.ProcessIncomingJson(fresh.ToString()),Is.True);
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId).Single().version,Is.EqualTo(2));
            Assert.That(f.Backend.ProcessIncomingJson(old.ToString()),Is.True);
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId).Single().version,Is.EqualTo(2));
            Assert.That(f.Backend.ExecutionControls,Is.Empty);
        }

        [Test] public void DisconnectReconnectInvalidatesQueriesWithoutDuplicatingSubscriptions()
        {
            using var f = new TaskConsiderationFixture();
            var old=Snapshot(f.Backend.StatusReads.Last());
            Connection(f.Backend,false);
            f.Backend.ProcessIncomingJson(old.ToString());
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId),Is.Empty);
            int before=f.Backend.StatusReads.Count;
            Connection(f.Backend,true);
            Assert.That(f.Backend.StatusReads.Count-before,Is.EqualTo(1));
            AssertSubscriptions(f.Backend,f.Bridge,1);
            f.Backend.ProcessIncomingJson(Snapshot(f.Backend.StatusReads.Last()).ToString());
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId).Single().status,Is.EqualTo("waiting"));
            Assert.That(f.Backend.ExecutionControls,Is.Empty);
        }

        [Test] public void ReconfigureDetachesOldBackendAndIgnoresItsMessages()
        {
            using var f = new TaskConsiderationFixture();
            var old=Snapshot(f.Backend.StatusReads.Last());
            var root=f.Make("replacement_fake_backend");root.SetActive(false);
            var backend=root.AddComponent<TaskConsiderationMockTransport>();
            typeof(BackendWebSocketClient).GetField("connectOnStart",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(backend,false);
            root.SetActive(true);
            f.Source.Configure(f.Bridge,f.Registry,f.Player,f.Router,backend,null);
            AssertSubscriptions(f.Backend,f.Bridge,0);AssertSubscriptions(backend,f.Bridge,1);
            int before=backend.StatusReads.Count;
            Connection(f.Backend,true);f.Backend.ProcessIncomingJson(old.ToString());
            Assert.That(backend.StatusReads.Count,Is.EqualTo(before));
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId),Is.Empty);
            backend.ProcessIncomingJson(Snapshot(backend.StatusReads.Last()).ToString());
            Assert.That(f.Bridge.StatusFor(f.Owner.EntityId).Single().status,Is.EqualTo("waiting"));
            Assert.That(backend.ExecutionControls,Is.Empty);
        }

        [Test] public void CancellationAckRecoversAfterEnableAndIsConsumedOnlyOnce()
        {
            using var f = new TaskConsiderationFixture();
            f.Backend.ProcessIncomingJson(Snapshot(f.Backend.StatusReads.Last()).ToString());
            Assert.That(f.Bridge.TryCancellationCandidate(out var agent,out var task),Is.True);
            Assert.That(f.Bridge.RequestPlayerCancellation(agent,task),Is.True);
            Lifecycle(f.Bridge,"Update");
            var sent=f.Backend.ExecutionControls.Single();var request=sent["runtime_request"];
            var result=new JObject { ["message_type"]="task_cancel_result",["schema_version"]="1.1",
                ["request_id"]=request["request_id"],["command_id"]=request["task_runtime"]["command_id"],
                ["agent_entity_id"]=agent,["task_runtime"]=request["task_runtime"].DeepClone(),
                ["owner"]=Identity(f.Backend.StatusReads.Last().recall_identity.observer),
                ["player"]=Identity(f.Backend.StatusReads.Last().recall_identity.current_subjects[0]),
                ["accepted"]=true,["outcome"]="cancelled",["task"]=Row("cancelled",2),["error_code"]=null };
            Enable(f.Bridge,false);
            f.Backend.ProcessIncomingJson(result.ToString());
            Assert.That(f.Bridge.HasPendingResults,Is.True);
            Enable(f.Bridge,true);f.Bridge.Register(f.Owner,f.Backend);
            f.Backend.ProcessIncomingJson(result.ToString());
            Assert.That(f.Bridge.FeedbackCode,Is.EqualTo("cancelled"));
            Assert.That(f.Bridge.HasPendingResults,Is.False);
            int before=f.Backend.StatusReads.Count;
            f.Backend.ProcessIncomingJson(result.ToString());Lifecycle(f.Bridge,"Update");
            Assert.That(f.Backend.StatusReads.Count,Is.EqualTo(before));
            Assert.That(f.Backend.ExecutionControls.Count,Is.EqualTo(1));
        }

        [Test] public void ResultAckRecoversWithoutReplayOfBeginOrDuplicateSettlement()
        {
            using var f = new TaskConsiderationFixture();
            f.Visible(true);f.Tick();f.Backend.ProcessIncomingJson(f.Snapshot().ToString());
            var runner=f.Root.AddComponent<GoalPlanRunner>();var execution=f.Root.AddComponent<TaskExecutionBridge>();
            Assert.That(execution.RequestStart(f.Backend.Sent,TaskExecutionTests.Proposal(f),f.Backend,runner,_=>{}),Is.True);
            var sent=f.Backend.ExecutionControls.Single();
            Assert.That((string)sent["operation"],Is.EqualTo("not_started"));
            var result=new JObject { ["message_type"]="task_execution_result",["schema_version"]="1.1",
                ["operation"]="not_started",["ticket"]=sent["ticket"].DeepClone(),["accepted"]=true,
                ["start_permitted"]=false,["task_status"]="blocked",["task_version"]=4,["phase"]="failed" };
            Enable(f.Bridge,false);f.Backend.ProcessIncomingJson(result.ToString());
            Assert.That(f.Bridge.HasPendingResults,Is.True);
            Enable(f.Bridge,true);
            for(int i=0;i<4;i++)f.Bridge.Register(f.Owner,f.Backend);
            Assert.That(f.Backend.ExecutionControls.Count,Is.EqualTo(1));
            f.Backend.ProcessIncomingJson(result.ToString());
            Assert.That(f.Bridge.HasPendingResults,Is.False);Assert.That(f.Bridge.FeedbackCode,Is.EqualTo("failed"));
            int before=f.Backend.StatusReads.Count;
            f.Backend.ProcessIncomingJson(result.ToString());Lifecycle(f.Bridge,"Update");
            Assert.That(f.Backend.StatusReads.Count,Is.EqualTo(before));
            Assert.That(f.Backend.ExecutionControls.Count,Is.EqualTo(1));
            Assert.That(runner.HasActiveTask,Is.False);
        }

        [Test] public void DestroyAndReplacementPreserveVisitButRejectOldSceneQueries()
        {
            using var f = new TaskConsiderationFixture();
            var old=Snapshot(f.Backend.StatusReads.Last());
            var original=f.Bridge;string visit=original.DiagnosticVisitId,scene=original.DiagnosticSceneInstanceId;
            Lifecycle(original,"OnDestroy");UnityEngine.Object.DestroyImmediate(original.gameObject);
            AssertSubscriptions(f.Backend,original,0);
            var replacement=f.Make("replacement_scene_bridge").AddComponent<TaskRuntimeBridge>();
            Assert.That(replacement.Admit(f.Registry,f.Player),Is.True);replacement.Register(f.Owner,f.Backend);
            AssertSubscriptions(f.Backend,replacement,1);
            Assert.That(replacement.DiagnosticVisitId,Is.EqualTo(visit));
            Assert.That(replacement.DiagnosticSceneInstanceId,Is.Not.EqualTo(scene));
            f.Backend.ProcessIncomingJson(old.ToString());
            Assert.That(replacement.StatusFor(f.Owner.EntityId),Is.Empty);
            Assert.That(f.Backend.ExecutionControls,Is.Empty);
        }
    }

    public sealed class TaskExecutionTests
    {
        [Test] public void FormalAdmissionPreservesTaskOwnershipAcrossActionWait()
        {
            using var f = new TaskConsiderationFixture(); Offer(f);
            var runner = f.Root.AddComponent<GoalPlanRunner>();
            var motion = f.Root.AddComponent<MotionChannelCoordinator>();
            var admission = f.Root.AddComponent<NpcDefaultRoutineController>();
            TaskConsiderationFixture.Set(admission, "goalPlanRunner", runner);
            TaskConsiderationFixture.Set(admission, "orchestrator", f.Orchestrator);
            TaskConsiderationFixture.Set(admission, "motionChannels", motion);
            var run = f.Orchestrator.CurrentRun;
            Assert.That(f.Source.Matches(f.Backend.Sent, run), Is.True);
            Assert.That(f.Orchestrator.WaitForCurrentRunActionResult(), Is.True);
            Assert.That(admission.IsCognitiveModeTransitionBlocked(run), Is.True,
                "An action wait still cannot authorize an ordinary life-mode change.");
            Assert.That(f.Source.Matches(f.Backend.Sent, run), Is.True,
                "The same ticketed task handoff retains its owned source during action wait.");
            f.Orchestrator.CancelCurrentRun();
            Assert.That(f.Source.Matches(f.Backend.Sent, run), Is.False);
        }
        private static JObject Status(string state="waiting",int version=1) => new() {
            ["task_id"]="task_cancel_fixture",["version"]=version,["status"]=state,["source"]="player_request",["goal_kind"]="serve_drink",
            ["attempt_id"]=null,["phase"]=null,["cancel_requested"]=false,["diagnostic"]=null,["condition"]=null };
        [Test] public void ExplicitCancellationUsesTrustedStatusAndRejectsForeignOrLateSnapshot()
        {
            using var f=new TaskConsiderationFixture();f.Visible(true);f.Tick();
            var snapshot=f.Snapshot(false);snapshot["tasks"]=new JArray(Status());
            Assert.That(f.Backend.ProcessIncomingJson(snapshot.ToString()),Is.True);
            Assert.That(f.Bridge.TryCancellationCandidate(out var agent,out var task),Is.True);
            f.Backend.Online=false;
            Assert.That(f.Bridge.RequestPlayerCancellation(agent,task),Is.True);
            Assert.That(f.Bridge.FeedbackCode,Is.EqualTo("cancel_pending"));Assert.That(f.Backend.ExecutionControls,Is.Empty);
            Assert.That(f.Bridge.TryCancellationCandidate(out _,out _),Is.False);
            f.Backend.Online=true;
            typeof(TaskRuntimeBridge).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(f.Bridge,null);
            var body=f.Backend.ExecutionControls.Single();var sync=body["runtime_request"];
            var identity=sync["recall_identity"];var owner=(JObject)identity["observer"];
            var result=new JObject { ["message_type"]="task_cancel_result",["schema_version"]="1.1",["request_id"]=sync["request_id"],
                ["command_id"]=sync["task_runtime"]["command_id"],["agent_entity_id"]=sync["agent_entity_id"],
                ["task_runtime"]=sync["task_runtime"].DeepClone(),["owner"]=new JObject { ["identity_namespace"]=owner["identity_namespace"],
                    ["identity_type"]=owner["identity_type"],["persistent_id"]=owner["persistent_id"] },
                ["player"]=new JObject { ["identity_namespace"]="player",["identity_type"]="Player",["persistent_id"]="other_player" },
                ["accepted"]=true,["outcome"]="cancelled",["task"]=Status("cancelled",2),["error_code"]=null };
            Assert.That(f.Backend.ProcessIncomingJson(result.ToString()),Is.True);
            Assert.That(f.Bridge.FeedbackCode,Is.EqualTo("cancel_pending"));Assert.That(f.Bridge.HasPendingResults,Is.True);
            result["player"]["persistent_id"]="synthetic-task-player";
            var malformed=(JObject)result.DeepClone();malformed["player"]=null;
            Assert.That(f.Backend.ProcessIncomingJson(malformed.ToString()),Is.False);
            Assert.That(f.Backend.ProcessIncomingJson(result.ToString()),Is.True);Assert.That(f.Bridge.FeedbackCode,Is.EqualTo("cancelled"));
            Assert.That(f.Bridge.HasPendingResults,Is.False);Assert.That(f.Bridge.TryCancellationCandidate(out _,out _),Is.False);
            // The delayed original waiting snapshot cannot resurrect the cancelled UI operation.
            f.Bridge.ObserveSync(f.Backend.Sync);f.Backend.ProcessIncomingJson(snapshot.ToString());
            Assert.That(f.Bridge.StatusFor(agent).Single().status,Is.EqualTo("cancelled"));
            result["grant_action"]=true;Assert.That(f.Backend.ProcessIncomingJson(result.ToString()),Is.False);
            Assert.That(f.Backend.SendCount,Is.Zero);
            f.Registry.Unregister(f.Player);
            f.Player.Configure("player_01","character.synthetic_player",EntityKind.Agent,AgentRole.Player,new PersistentIdentityRef("player","Player","other_player"));
            Assert.That(f.Bridge.StatusFor(agent),Is.Empty,"Changing the admitted player hides the prior player's task data.");
        }
        [Test] public void EvidenceSurvivesBridgeReplacementAndNeverReplaysBegin()
        {
            using var f=new TaskConsiderationFixture();Offer(f);
            var runner=f.Root.AddComponent<GoalPlanRunner>();var execution=f.Root.AddComponent<TaskExecutionBridge>();
            Assert.That(execution.RequestStart(f.Backend.Sent,Proposal(f),f.Backend,runner,_=>{}),Is.True);
            Assert.That(f.Bridge.HasPendingResults,Is.True);
            var original=f.Backend.ExecutionControls.Single();
            UnityEngine.Object.DestroyImmediate(f.Bridge.gameObject);
            var replacement=f.Make("replacement_task_runtime").AddComponent<TaskRuntimeBridge>();
            Assert.That(replacement.Admit(f.Registry,f.Player),Is.True);replacement.Register(f.Owner,f.Backend);
            typeof(TaskRuntimeBridge).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(replacement,null);
            Assert.That(f.Backend.ExecutionControls.Count,Is.EqualTo(2));
            Assert.That(JToken.DeepEquals(original,f.Backend.ExecutionControls[1]),Is.True);
            Assert.That((string)original["operation"],Is.EqualTo("not_started"));Assert.That(runner.HasActiveTask,Is.False);
            var acknowledgement=new JObject { ["message_type"]="task_execution_result",["schema_version"]="1.1",["operation"]="not_started",
                ["ticket"]=original["ticket"].DeepClone(),["accepted"]=true,["start_permitted"]=false,["task_status"]="blocked",["task_version"]=4,["phase"]="failed" };
            Assert.That(f.Backend.ProcessIncomingJson(acknowledgement.ToString()),Is.True);Assert.That(replacement.HasPendingResults,Is.False);
            Assert.That(replacement.FeedbackCode,Is.EqualTo("failed"),"Only the live replacement may consume the acknowledgement.");
        }
        [Test] public void BusyCounterFactWaitsForOneLegalOpportunityAndSilenceDoesNotRearm()
        {
            using var f=new TaskConsiderationFixture();
            var root=f.Root;root.AddComponent<AgentActionCapabilityProfile>().Configure("clean_counter");
            TaskConsiderationFixture.Set(root.GetComponent<CognitiveContextComposer>(),"actionCapabilities",root.GetComponent<AgentActionCapabilityProfile>());
            var motion=root.AddComponent<MotionChannelCoordinator>();var runner=root.AddComponent<GoalPlanRunner>();var admission=root.AddComponent<NpcDefaultRoutineController>();
            TaskConsiderationFixture.Set(admission,"owner",f.Owner);TaskConsiderationFixture.Set(admission,"goalPlanRunner",runner);
            TaskConsiderationFixture.Set(admission,"orchestrator",f.Orchestrator);TaskConsiderationFixture.Set(admission,"motionChannels",motion);
            var daily=root.AddComponent<DailyBehaviorController>();var profile=ScriptableObject.CreateInstance<DailyBehaviorProfile>();
            TaskConsiderationFixture.Set(daily,"profile",profile);
            daily.enabled=true;
            var interest=ScriptableObject.CreateInstance<EmbodiedNPC.Events.EventInterestProfile>();
            interest.Configure(EmbodiedNPC.Events.EventInterestDecision.Ignore,new EmbodiedNPC.Events.EventInterestRule(
                EmbodiedNPC.Events.AgentEventTypes.CounterNeedsCleaning,EmbodiedNPC.Events.EventInterestDecision.Wake,configuredDailyModeId:"work"));
            var receiver=root.GetComponent<EmbodiedNPC.Events.AgentEventReceiver>();receiver.Configure(f.Owner,profile:interest);
            TaskConsiderationFixture.Set(receiver,"dailyBehavior",daily);
            var world=f.Make("counter_fact_world").AddComponent<WorldStateService>();
            var target=f.Make("counter_fact_target");var entity=target.AddComponent<WorldEntity>();entity.Configure("counter_cafe_clean_01","counter.synthetic",EntityKind.Environment);
            TaskConsiderationFixture.Set(f.Registry,"entities",new List<WorldEntity>{f.Owner,f.Player,entity});f.Registry.RebuildRegistry();
            var station=target.AddComponent<CounterCleaningStation>();station.Configure(target.transform,target.transform,world,f.Registry);
            var cap=root.AddComponent<CounterCleaningCapability>();cap.Configure(station);
            var source=root.AddComponent<CounterCleaningCognitionSource>();source.Configure(cap,f.Registry,f.Router,null);
            Assert.That(f.Orchestrator.TryStartRun(new CognitiveTrigger("busy","event",f.Owner.EntityId,"busy_event",1,"player.utterance"),out _),Is.True);
            Assert.That(world.TryMarkCounterNeedsCleaning(f.Registry,station,"isolated_busy_fact"),Is.True);
            Assert.That(f.Backend.SendCount,Is.Zero);Assert.That(source.CanWake(source.CurrentEvent),Is.False);
            f.Orchestrator.CompleteCurrentRun();
            typeof(CounterCleaningCognitionSource).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(source,null);
            Assert.That(f.Backend.SendCount,Is.EqualTo(1));Assert.That(f.Backend.Sent.context.relevant_player,Is.Null);
            Assert.That(f.Backend.Sent.task_runtime.lifecycle_protocol,Is.EqualTo("1.0"));
            Assert.That(f.Backend.Sent.recall_identity.current_subjects.Any(p=>p.kind=="player"),Is.True);
            var request=f.Backend.Sent;var response=f.NoAction();response.task_result="committed";
            response.task_receipt=new CognitiveTaskReceiptDto {schema_version="1.1",request_id=request.request_id,trace_id=request.trace_id,
                run_id=request.run.run_id,agent_entity_id=f.Owner.EntityId,command_id=request.task_runtime.command_id,
                visit_id=request.task_runtime.visit_id,scene_instance_id=request.task_runtime.scene_instance_id,reply_language=request.reply_language,
                operation="create",outcome="created",committed=true,task_id="self_task_synthetic",task_version=1,task_status="waiting",
                owner=new TaskIdentityDto {identity_namespace="npc",identity_type="Character",persistent_id=f.Owner.PersistentIdentity.PersistentId},
                player=new TaskIdentityDto {identity_namespace="player",identity_type="Player",persistent_id="synthetic-task-player"}};
            Assert.That(TaskReceiptValidator.Matches(response,request),Is.True,"A saved self plan stays silent and uses private player routing only.");
            f.Visible(true);f.Bridge.AcceptReceipt(request,response);
            var statusRead=f.Backend.StatusReads.Last();var selfStatus=Status();selfStatus["task_id"]="self_task_synthetic";
            selfStatus["source"]="self_initiated";selfStatus["goal_kind"]="clean_assigned_counter";
            selfStatus["condition"]=JObject.FromObject(f.Bridge.CurrentCondition(f.Owner));
            var saved=new JObject { ["message_type"]="task_runtime_snapshot",["schema_version"]="1.1",["request_id"]=statusRead.request_id,
                ["agent_entity_id"]=f.Owner.EntityId,["task_runtime"]=JObject.Parse(CognitiveRequest.SerializeFields(statusRead.task_runtime)),
                ["owner"]=JObject.Parse(CognitiveRequest.SerializeFields(response.task_receipt.owner)),
                ["player"]=JObject.Parse(CognitiveRequest.SerializeFields(response.task_receipt.player)),
                ["opportunity_token"]=null,["task_ids"]=new JArray(),["tasks"]=new JArray(selfStatus) };
            Assert.That(f.Backend.ProcessIncomingJson(saved.ToString()),Is.True);
            Assert.That(f.Bridge.HasConditionTask(f.Owner.EntityId,f.Bridge.CurrentCondition(f.Owner)),Is.True);
            typeof(CounterCleaningCognitionSource).GetMethod("PublishFact",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(source,null);
            Assert.That(source.LastStatus,Is.EqualTo("managed_by_task"));
            source.RecordResult("no_action");f.Orchestrator.CompleteCurrentRun();
            for(int i=0;i<2;i++) typeof(CounterCleaningCognitionSource).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(source,null);
            Assert.That(f.Backend.SendCount,Is.EqualTo(1));Assert.That(root.GetComponent<GoalPlanRunner>().HasActiveTask,Is.False);
            f.Tick();Assert.That(f.Backend.SyncCount,Is.Zero,"A newly saved/deferred task does not consume the same idle opportunity again.");
            f.Visible(false);f.Tick();f.Visible(true);f.Tick();Assert.That(f.Backend.SyncCount,Is.EqualTo(1),"New valid perception can offer the task without forcing an action.");
            UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(interest);
        }
        internal static CognitiveResponse Proposal(TaskConsiderationFixture f)
        {
            var response=f.NoAction();var req=f.Backend.Sent;
            response.decision=new CognitiveDecision {decision_kind="ACTION_PROPOSAL",action_proposal=new CognitiveActionProposal {
                action_id="prepare_drink",bindings=new Dictionary<string,string>{{"drink_type","latte"}}}};
            response.validated_goal=new GoalProposal {type="prepare_drink",drink_type="latte"};
            response.task_execution=new TaskExecutionTicketDto {schema_version="1.1",request_id=req.request_id,trace_id=req.trace_id,
                run_id=req.run.run_id,agent_entity_id=f.Owner.EntityId,command_id=req.task_runtime.command_id,
                owner=new TaskIdentityDto {identity_namespace="npc",identity_type="Character",persistent_id=f.Owner.PersistentIdentity.PersistentId},
                player=new TaskIdentityDto {identity_namespace="player",identity_type="Player",persistent_id="synthetic-task-player"},
                visit_id=req.task_runtime.visit_id,scene_instance_id=req.task_runtime.scene_instance_id,
                task_id="task_synthetic",attempt_id="attempt_synthetic",task_version=3,
                goal=new JObject { ["kind"]="serve_drink",["drink_type"]="latte"},
                execution_request=new TaskExecutionRequestDto {expected_version=2,world_revision=req.context.current_event.world_revision,
                    scene_instance_id=req.task_runtime.scene_instance_id,proposal_id=GoalPlanRunner.NormalizeProposalId(req.request_id),
                    trace_id=req.trace_id,actor_entity_id=f.Owner.EntityId,player_entity_id=f.Player.EntityId}};
            return response;
        }
        private static void Offer(TaskConsiderationFixture f)
        {f.Visible(true);f.Tick();Assert.That(f.Backend.ProcessIncomingJson(f.Snapshot().ToString()),Is.True);}

        [TestCase("owner")][TestCase("player")][TestCase("scene_instance_id")][TestCase("visit_id")][TestCase("drink_type")][TestCase("execution_protocol")][TestCase("player_entity_id")]
        public void SubstitutedTicketCannotObtainStartPermission(string field)
        {
            using var f=new TaskConsiderationFixture();Offer(f);var response=Proposal(f);
            Assert.That(response.task_execution.Matches(f.Backend.Sent,response),Is.True);
            switch(field)
            {
                case "owner":response.task_execution.owner.persistent_id="other";break;
                case "player":response.task_execution.player.persistent_id="other";break;
                case "scene_instance_id":response.task_execution.scene_instance_id="other_scene";break;
                case "visit_id":response.task_execution.visit_id="other_visit";break;
                case "execution_protocol":f.Backend.Sent.task_consideration.execution_protocol=null;break;
                case "player_entity_id":response.task_execution.execution_request.player_entity_id="other_player_entity";break;
                default:response.decision.action_proposal.bindings["drink_type"]="americano";break;
            }
            Assert.That(response.task_execution.Matches(f.Backend.Sent,response),Is.False);
            Assert.That(f.Driver.TryConsumeResponse(response),Is.False);
            Assert.That(f.Backend.ExecutionControls,Is.Empty);Assert.That(f.Presenter.Count,Is.Zero);
        }
        [Test] public void StrictWireRejectsUnknownFieldsAndCoercedTicketVersion()
        {
            using var f=new TaskConsiderationFixture();Offer(f);var response=Proposal(f);
            var wire=JObject.Parse(CognitiveRequest.SerializeFields(response));
            Assert.That(CognitiveResponseValidator.TryParse(wire.ToString(),out _,out var error),Is.True,error);
            wire["task_execution"]["task_version"]="3";
            Assert.That(CognitiveResponseValidator.TryParse(wire.ToString(),out _,out _),Is.False);
            wire=JObject.Parse(CognitiveRequest.SerializeFields(response));wire["task_execution"]["execution_request"]["grant_all"]=true;
            Assert.That(CognitiveResponseValidator.TryParse(wire.ToString(),out _,out _),Is.False);
            var rejection=new JObject { ["message_type"]="error",["request_id"]=response.request_id,["trace_id"]=response.trace_id,
                ["run_id"]=response.run_id,["agent_entity_id"]=response.agent_entity_id,["reason"]="TASK_VERSION_CONFLICT" };
            Assert.That(CognitiveResponseValidator.TryParseFailure(rejection.ToString(),out _),Is.True);
            rejection.Remove("run_id");Assert.That(CognitiveResponseValidator.TryParseFailure(rejection.ToString(),out _),Is.False);
        }
        [Test] public void TicketAndSilentResponseDoNotAuthorizeOrdinarySystemEvent()
        {
            using var f=new TaskConsiderationFixture();Offer(f);
            Assert.That(f.Driver.TryConsumeResponse(f.NoAction()),Is.True);Assert.That(f.Driver.FlushTaskConsideration(),Is.True);
            Assert.That(f.Backend.ExecutionControls,Is.Empty);Assert.That(f.Root.GetComponent<TaskExecutionBridge>(),Is.Null);
        }

        [Test] public void StoreFailureRetainsEvidenceWithoutIssuingAnotherBegin()
        {
            using var f=new TaskConsiderationFixture();Offer(f);var response=Proposal(f);
            // No world authority/NavMesh is supplied: this must create a proven
            // not_started report, never run an action, even with a valid ticket.
            var runner=f.Root.AddComponent<GoalPlanRunner>();
            var bridge=f.Root.AddComponent<TaskExecutionBridge>();
            Assert.That(bridge.RequestStart(f.Backend.Sent,response,f.Backend,runner,_=>{}),Is.True);
            Assert.That((string)f.Backend.ExecutionControls.Single()["operation"],Is.EqualTo("not_started"));
            var result=new JObject { ["message_type"]="task_execution_result",["schema_version"]="1.1",["operation"]="not_started",
                ["ticket"]=f.Backend.ExecutionControls[0]["ticket"].DeepClone(),["accepted"]=false,["start_permitted"]=false,
                ["task_status"]=null,["task_version"]=null,["phase"]=null,["error_code"]="TASK_EXECUTION_STORE_UNAVAILABLE" };
            Assert.That(f.Backend.ProcessIncomingJson(result.ToString()),Is.True);
            Assert.That(bridge.LastStatus,Is.EqualTo("settlement_pending"));
            result["accepted"]=true;result["task_status"]="blocked";result["task_version"]=4;result["phase"]="failed";result["error_code"]=null;
            Assert.That(f.Backend.ProcessIncomingJson(result.ToString()),Is.True);
            Assert.That(bridge.LastStatus,Is.EqualTo("blocked:failed"));
            Assert.That(f.Backend.ExecutionControls.Count,Is.EqualTo(1));Assert.That(runner.HasActiveTask,Is.False);
        }

        [Test] public void WholeGoalAndGoalSpecificReceiptMustBothMatch()
        {
            using var f=new TaskConsiderationFixture();Offer(f);
            foreach(bool clean in new[]{false,true})
            foreach(string mismatch in new[]{"none","actor","target_or_player","condition_or_drink","old_revision","failed_goal"})
            {
                // Synthetic native receipts verify the contract only. PlayMode supplies actual WorldState evidence separately.
                var ticket=Proposal(f).task_execution;var e=ticket.execution_request;
                if(clean) {ticket.goal=new JObject { ["kind"]="clean_assigned_counter",["counter_key"]="assigned_counter"};
                    e.player_entity_id=null;e.target_entity_id="counter_01";e.condition_revision=1;}
                var outcome=new GoalOutcome("synthetic_outcome",e.proposal_id,0,e.trace_id,clean?"clean_counter":"prepare_drink",
                    mismatch=="actor"?"other_npc":e.actor_entity_id,clean?e.target_entity_id:null,null,
                    mismatch=="failed_goal"?PlanExecutionStatus.Failed:PlanExecutionStatus.Succeeded,"",e.world_revision+1,0);
                int revision=e.world_revision+(mismatch=="old_revision"?0:1);
                DrinkReceipt? drink=clean?null:new DrinkReceipt("synthetic_drink",e.actor_entity_id,e.proposal_id,e.trace_id,
                    mismatch=="target_or_player"?"other_player":e.player_entity_id,mismatch=="condition_or_drink"?"americano":"latte",revision);
                CounterCleaningReceipt? counter=clean?new CounterCleaningReceipt("synthetic_clean",
                    mismatch=="target_or_player"?"other_counter":e.target_entity_id,mismatch=="condition_or_drink"?2:1,revision):null;
                Assert.That(ticket.MatchesWorldReceipt(outcome,drink,counter),Is.EqualTo(mismatch=="none"),clean+":"+mismatch);
            }
        }
    }

    /// <summary>Isolated native Runner fixture. Reuses authored NavMesh, never a production player or network lifecycle.</summary>
    public sealed class TaskExecutionWorldFixture : IDisposable
    {
        public static TaskExecutionWorldFixture Active;
        public readonly TaskConsiderationFixture Base;
        public readonly WorldStateService World;
        public readonly GoalPlanRunner Runner;
        public readonly DrinkPreparationStation Drink;
        public readonly CounterCleaningStation Counter;
        public readonly NavMeshAgent Agent;
        public readonly List<GoalOutcome> Outcomes=new();
        public int Starts;
        private NavMeshDataInstance nav;
        public TaskExecutionWorldFixture(string path)
        {
            Assert.That(Application.isPlaying,Is.True);
            var data=AssetDatabase.LoadAssetAtPath<NavMeshData>("Assets/CafeDemo/V2/CafeDemoV2NavMesh.asset");
            nav=NavMesh.AddNavMeshData(data);
            Base=new TaskConsiderationFixture(path:path);
            var root=Base.Root;root.SetActive(false);
            World=Base.Make("isolated-task3b-world").AddComponent<WorldStateService>();
            var reservations=Base.Make("isolated-task3b-reservations").AddComponent<ResourceReservationService>();
            root.AddComponent<AgentActionCapabilityProfile>().Configure("prepare_drink","clean_counter");
            TaskConsiderationFixture.Set(root.GetComponent<CognitiveContextComposer>(),"actionCapabilities",root.GetComponent<AgentActionCapabilityProfile>());
            root.AddComponent<MotionChannelCoordinator>();
            Agent=root.AddComponent<NavMeshAgent>();Agent.speed=2.5f;Agent.angularSpeed=360;Agent.acceleration=12;Agent.stoppingDistance=.08f;
            Agent.agentTypeID=Enumerable.Range(0,NavMesh.GetSettingsCount()).Select(NavMesh.GetSettingsByIndex)
                .Single(s=>NavMesh.GetSettingsNameFromID(s.agentTypeID)=="CafeDemoHumanoid").agentTypeID;
            Agent.radius=.32f;Agent.height=1.8f;
            Assert.That(NavMesh.SamplePosition(new Vector3(-1f,0f,5f),out var start,2,NavMesh.AllAreas),Is.True);
            root.transform.position=start.position;
            var drinkRoot=Base.Make("isolated-task3b-drink");
            var drinkEntity=drinkRoot.AddComponent<WorldEntity>();drinkEntity.Configure("station_cafe_drinks","station.synthetic",EntityKind.Environment);
            drinkRoot.transform.position=new Vector3(.97f,.03f,5.1f);
            var drinkOperation=Base.Make("isolated-task3b-drink-operation").transform;drinkOperation.position=new Vector3(.27f,.03f,2.6f);
            Drink=drinkRoot.AddComponent<DrinkPreparationStation>();Drink.Configure(drinkRoot.transform,drinkOperation);
            root.AddComponent<DrinkPreparationCapability>().Configure(Drink);
            var counterRoot=Base.Make("isolated-task3b-counter");
            var counterEntity=counterRoot.AddComponent<WorldEntity>();counterEntity.Configure("counter_cafe_clean_01","counter.synthetic",EntityKind.Environment);
            counterRoot.transform.position=new Vector3(-2.08f,0,3.18f);
            var operation=Base.Make("isolated-task3b-counter-operation").transform;operation.position=new Vector3(-1.62f,1.03f,3.18f);
            Counter=counterRoot.AddComponent<CounterCleaningStation>();Counter.Configure(counterRoot.transform,operation,World,Base.Registry);
            root.AddComponent<CounterCleaningCapability>().Configure(Counter);
            TaskConsiderationFixture.Set(Base.Registry,"entities",new List<WorldEntity>{Base.Owner,Base.Player,drinkEntity,counterEntity});Base.Registry.RebuildRegistry();
            var validator=root.AddComponent<GoalValidator>();
            TaskConsiderationFixture.Set(validator,"entityRegistry",Base.Registry);TaskConsiderationFixture.Set(validator,"worldState",World);
            TaskConsiderationFixture.Set(validator,"reservations",reservations);
            Runner=root.AddComponent<GoalPlanRunner>();
            TaskConsiderationFixture.Set(Runner,"entityRegistry",Base.Registry);TaskConsiderationFixture.Set(Runner,"worldState",World);
            TaskConsiderationFixture.Set(Runner,"reservations",reservations);
            var handoff=root.AddComponent<CognitiveActionHandoff>();TaskConsiderationFixture.Set(handoff,"goalPlanRunner",Runner);
            TaskConsiderationFixture.Set(Base.Driver,"actionHandoff",handoff);
            root.SetActive(true);Assert.That(Agent.Warp(start.position),Is.True);
            Base.Driver.Configure(Base.Owner,Base.Orchestrator,root.GetComponent<CognitiveContextComposer>(),Base.Backend,Base.Presenter);
            Runner.GoalOutcomePublished+=value=>Outcomes.Add(value);Runner.TaskStarted+=_=>Starts++;
            Assert.That(World.TryMarkCounterNeedsCleaning(Base.Registry,Counter,"isolated_stage3b_spill"),Is.True);
            Base.Visible(true);
        }
        public void Dispose() {Base.Dispose();if(nav.valid)nav.Remove();Active=null;}
        public object Evidence() => new { starts=Starts,drinks=World.GetDrinkCount(Base.Player.EntityId,"latte"),
            counter=World.GetCounterState(Counter.Entity.EntityId).ToString(),revision=World.WorldRevision,
            phase=Runner.CurrentTaskFeedback?.Progress.Action?.ToString(),active=Runner.HasActiveTask,
            status=Base.Root.GetComponent<TaskExecutionBridge>()?.LastStatus,
            outcomes=Outcomes.Select(o=>new {id=o.OutcomeId,proposal=o.ProposalId,trace=o.TraceId,goal=o.GoalType,status=o.Status.ToString(),revision=o.WorldRevision}).ToArray(),
            controls=Base.Backend.ExecutionControls.ToArray(),request=Base.Backend.Sent==null?null:JObject.Parse(Base.Backend.Sent.Serialize()),
            sync=Base.Backend.Sync==null?null:JObject.Parse(CognitiveRequest.SerializeFields(Base.Backend.Sync)) };
    }
}
