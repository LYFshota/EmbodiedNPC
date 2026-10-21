using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    [Serializable] public sealed class TaskConsiderationContextDto
    { public string opportunity_token, event_id; public string execution_protocol="1.0"; }
    [Serializable] public sealed class TaskRuntimeSyncRequestDto
    {
        public string message_type = "task_runtime_sync", schema_version = "1.1", request_id, agent_entity_id;
        public CognitiveRecallIdentityDto recall_identity;
        public TaskRuntimeContextDto task_runtime;
        public bool is_idle;
        public bool offer_tasks=true, counter_observed, player_visible=true;
        public TaskConditionDto counter_condition;
    }
    public sealed class TaskRuntimeSnapshot
    {
        public string RequestId, AgentId, Token;
        public TaskRuntimeContextDto Runtime;
        public TaskIdentityDto Owner, Player;
        public string[] TaskIds;
        public TaskStatusDto[] Tasks;
        public static bool TryParse(string json, out TaskRuntimeSnapshot value)
        {
            value = null;
            try
            {
                var root = JObject.Parse(json);
                bool Shape(JObject item, params string[] keys) => item != null && item.Properties().Count() == keys.Length && keys.All(k => item[k] != null);
                bool lifecycle=(string)root["task_runtime"]?["lifecycle_protocol"]=="1.0";
                var keys=new[]{"message_type","schema_version","request_id","agent_entity_id","task_runtime","owner","player","opportunity_token","task_ids"};
                if (!Shape(root,lifecycle?keys.Concat(new[]{"tasks"}).ToArray():keys) ||
                    (string)root["message_type"] != "task_runtime_snapshot" || (string)root["schema_version"] != "1.1") return false;
                foreach (string field in new[]{"message_type","schema_version","request_id","agent_entity_id"})
                    if (root[field].Type != JTokenType.String) return false;
                var runtime = root["task_runtime"] as JObject;
                if (!Shape(runtime,lifecycle?new[]{"command_id","client_session_id","visit_id","scene_instance_id","lifecycle_protocol","player_entity_id"}:new[]{"command_id","client_session_id","visit_id","scene_instance_id"}) || runtime.Properties().Any(p=>p.Value.Type!=JTokenType.String)) return false;
                foreach (string identity in new[]{"owner","player"})
                {
                    var item = root[identity] as JObject;
                    if (!Shape(item,"identity_namespace","identity_type","persistent_id") || item.Properties().Any(p=>p.Value.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))) return false;
                }
                var ids = root["task_ids"] as JArray;
                if (ids == null || ids.Count > 3 || ids.Any(t=>t.Type!=JTokenType.String || !TaskRuntimeContextDto.IsToken((string)t)) || ids.Distinct(new JTokenEqualityComparer()).Count()!=ids.Count) return false;
                var token = root["opportunity_token"];
                if (ids.Count==0 ? token.Type!=JTokenType.Null : token.Type!=JTokenType.String || !TaskRuntimeContextDto.IsToken((string)token)) return false;
                if(lifecycle && !TaskStatusDto.ValidArray(root["tasks"])) return false;
                value = new TaskRuntimeSnapshot { RequestId=(string)root["request_id"],AgentId=(string)root["agent_entity_id"],
                    Tasks=lifecycle?root["tasks"].ToObject<TaskStatusDto[]>():null,
                    Token=token.Type==JTokenType.Null?null:(string)token,TaskIds=ids.Select(t=>(string)t).ToArray(),
                    Runtime=runtime.ToObject<TaskRuntimeContextDto>(),Owner=root["owner"].ToObject<TaskIdentityDto>(),Player=root["player"].ToObject<TaskIdentityDto>() };
                return value.Runtime.IsValid && TaskRuntimeContextDto.IsToken(value.RequestId);
            }
            catch { value=null; return false; }
        }
    }

    /// <summary>One event-driven pending check per NPC. Offers facts, never selects a goal or executes an action.</summary>
    [DisallowMultipleComponent]
    public sealed class TaskConsiderationSource : MonoBehaviour
    {
        private WorldEntity owner, player;
        private EntityRegistry registry;
        private AgentEventRouter router;
        private BackendWebSocketClient backend;
        private TaskRuntimeBridge bridge;
        private DialogueController dialogue;
        private PerceivedStateStore perception;
        private AgentOrchestrator orchestrator;
        private AgentEventReceiver receiver;
        private TaskRuntimeSyncRequestDto pendingSync;
        private TaskRuntimeSnapshot snapshot;
        private AgentEvent currentEvent;
        private bool pending, wasVisible, claimed, wasGoalBusy;
        private double expiresAt, checkAt;
        private string runId;
        private string lastStatus="not_admitted",diagnosticGates;
        private TaskRuntimeContextDto diagnosticRuntime;
        private string[] diagnosticTasks=Array.Empty<string>();
        public string LastStatus { get=>lastStatus; private set {if(lastStatus==value)return;lastStatus=value;ObserveStatus();} }
        public int PublishedCount { get; private set; }
        public bool HasPendingCheck => pending;
        public AgentEvent CurrentEvent => currentEvent;

        private JObject DiagnosticContext() => new() {
            ["agent_id"]=owner?.EntityId,["request_id"]=pendingSync?.request_id,
            ["visit_id"]=diagnosticRuntime?.visit_id??bridge?.DiagnosticVisitId,
            ["scene_instance_id"]=diagnosticRuntime?.scene_instance_id??bridge?.DiagnosticSceneInstanceId,
            ["command_id"]=diagnosticRuntime?.command_id,["run_id"]=runId,["event_id"]=currentEvent?.EventId,
            ["task_ids"]=new JArray(diagnosticTasks),["eligible_count"]=diagnosticTasks.Length };
        private void ObserveStatus()
        {
            try {var data=DiagnosticContext();data["task_state"]=lastStatus.Split(':')[0];
                if(lastStatus.Contains(":"))data["error_code"]=lastStatus.Substring(lastStatus.IndexOf(':')+1);
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("opportunity",data);} catch(Exception) { }
        }
        private void ObserveGates(bool visible)
        {
            try
            {
                var goals=GetComponent<GoalPlanRunner>();var returning=GetComponent<ReturnToHomeRoutine>();var admission=GetComponent<NpcDefaultRoutineController>();
                var data=new JObject { ["task_state"]="gate_changed",["player_visible"]=visible,["counter_opportunity"]=OwnCounterOpportunity(),
                    ["bridge_initialized"]=bridge?.DiagnosticInitialized??false,["transport_connected"]=backend?.IsConnected??false,
                    ["dialogue_visible"]=dialogue?.IsPresentationVisible??false,["run_busy"]=orchestrator?.HasActiveRun??false,
                    ["goal_busy"]=goals?.HasActiveTask??false,["returning"]=returning!=null && (returning.IsRunning || returning.OwnsPath),
                    ["engagement_held"]=admission?.IsEngagementHeld??false,["routine_blocked"]=admission?.IsDailyActivityBlocked??false,["gate_open"]=Conditions() };
                if(diagnosticRuntime!=null)data["runtime_valid"]=bridge?.IsCurrent(diagnosticRuntime)??false;
                string signature=data.ToString(Newtonsoft.Json.Formatting.None);if(signature==diagnosticGates)return;diagnosticGates=signature;
                data.Merge(DiagnosticContext());EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("gate",data);
            }
            catch(Exception) { }
        }

        public void Configure(TaskRuntimeBridge runtime, EntityRegistry entities, WorldEntity currentPlayer,
            AgentEventRouter events, BackendWebSocketClient transport, DialogueController sessions)
        {
            if(owner!=null && bridge==runtime && registry==entities && player==currentPlayer && router==events && backend==transport && dialogue==sessions) return;
            Invalidate("runtime_reconfigured");
            Unsubscribe(); bridge=runtime; registry=entities; player=currentPlayer; router=events; backend=transport; dialogue=sessions;
            owner=GetComponent<WorldEntity>(); receiver=GetComponent<AgentEventReceiver>(); perception=GetComponent<PerceivedStateStore>();
            orchestrator=GetComponent<AgentOrchestrator>(); pending=true; wasVisible=false; checkAt=0; Subscribe(); LastStatus="admitted";
            bridge.Register(owner,backend);
        }
        private void OnEnable() => Subscribe();
        private void Subscribe()
        {
            if (backend!=null) { backend.TaskRuntimeSnapshotReceived-=OnSnapshot; backend.TaskRuntimeSnapshotReceived+=OnSnapshot;
                backend.ConnectionStatusChanged-=OnConnection; backend.ConnectionStatusChanged+=OnConnection;
                backend.CognitiveResponseReceived-=OnCognitiveResponse; backend.CognitiveResponseReceived+=OnCognitiveResponse; }
            if(receiver!=null) { receiver.EventReceived-=OnEvent; receiver.EventReceived+=OnEvent; }
            if(dialogue!=null) { dialogue.ActiveSessionChanged-=OnSession; dialogue.ActiveSessionChanged+=OnSession; }
        }
        private void Unsubscribe()
        {
            if(backend!=null) { backend.TaskRuntimeSnapshotReceived-=OnSnapshot; backend.ConnectionStatusChanged-=OnConnection; backend.CognitiveResponseReceived-=OnCognitiveResponse; }
            if(receiver!=null) receiver.EventReceived-=OnEvent;
            if(dialogue!=null) dialogue.ActiveSessionChanged-=OnSession;
        }
        private void OnDisable() { Unsubscribe(); Invalidate("source_disabled"); pending=false; }
        private void OnConnection(string status) { Invalidate("transport_changed"); pending=status=="CONNECTED"; }
        private void OnSession(DialogueParticipant target) { if(target!=null) Invalidate("player_session"); }
        private void OnEvent(AgentEvent value)
        {
            if(value.EventType==AgentEventTypes.PlayerUtterance && value.SourceEntityId==player?.EntityId)
            { pending=false; Invalidate("merged_into_player_run"); }
        }
        private void OnCognitiveResponse(CognitiveResponse value)
        { if(value.agent_entity_id==owner?.EntityId && value.task_receipt?.committed==true && currentEvent==null)
            pending=!(GetComponent<AgentOrchestrator>()?.CurrentRun?.IsCounterCleaningConsideration??false); }

        internal void SuppressUntilNewOpportunity()
        { pending=false;pendingSync=null;wasVisible=IsPlayerVisible();wasGoalBusy=GetComponent<GoalPlanRunner>()?.HasActiveTask??false; }

        private bool OwnCounterOpportunity() => bridge?.CurrentCondition(owner)!=null &&
            GetComponent<CounterCleaningCapability>()?.IsAuthorized==true && GetComponent<DailyBehaviorController>()?.CurrentMode==DailyBehaviorMode.Work;

        public bool IsPlayerVisible()
        {
            if(perception?.Aggregator==null || player==null) return false;
            Observation latest=null;
            foreach(var observation in perception.Aggregator.Observations)
                if(observation.SubjectEntityId==player.EntityId && observation.Channel==ObservationChannel.Visual &&
                    (latest==null || observation.SourceType==ObservationSourceType.ContinuousScan && latest.SourceType!=ObservationSourceType.ContinuousScan ||
                     observation.SourceType==latest.SourceType && (observation.ObservedAt>latest.ObservedAt || observation.ObservedAt==latest.ObservedAt && observation.PerceptionRevision>latest.PerceptionRevision))) latest=observation;
            return latest!=null && latest.ExpiresAt>=Time.unscaledTimeAsDouble && latest.LifecycleState!=PerceptionLifecycleState.Forgotten &&
                latest.KnownFields.TryGetValue("visibility_state",out var visible) && visible=="current_visible";
        }
        private bool Conditions(CognitiveRun ownRun=null)
        {
            if(!isActiveAndEnabled || registry==null || !registry.IsReady || owner==null || player==null ||
                !registry.TryGetEntity(owner.EntityId,out var currentOwner) || currentOwner!=owner ||
                !registry.TryGetEntity(player.EntityId,out var currentPlayer) || currentPlayer!=player ||
                (!IsPlayerVisible() && !OwnCounterOpportunity()) || backend==null || !backend.IsConnected || dialogue!=null && dialogue.IsPresentationVisible ||
                orchestrator==null || ownRun==null && orchestrator.HasActiveRun) return false;
            var goals=GetComponent<GoalPlanRunner>(); if(goals!=null && goals.HasActiveTask) return false;
            var returning=GetComponent<ReturnToHomeRoutine>(); if(returning!=null && (returning.IsRunning || returning.OwnsPath)) return false;
            var admission=GetComponent<NpcDefaultRoutineController>();
            if(admission!=null && (admission.IsEngagementHeld || (ownRun==null?admission.IsDailyActivityBlocked:admission.IsTaskHandoffBlocked(ownRun)))) return false;
            return true;
        }
        private void Update() => Tick();
        public void Tick()
        {
            if(Time.unscaledTimeAsDouble<checkAt) return;
            checkAt=Time.unscaledTimeAsDouble+0.25;
            bool visible=IsPlayerVisible();
            ObserveGates(visible);
            if(visible && !wasVisible) pending=true;
            wasVisible=visible;
            bool goalBusy=GetComponent<GoalPlanRunner>()?.HasActiveTask??false;
            if(wasGoalBusy && !goalBusy) pending=true;
            wasGoalBusy=goalBusy;
            if(currentEvent!=null && (Time.unscaledTimeAsDouble>=expiresAt || !Conditions(orchestrator?.CurrentRun))) Invalidate("condition_expired");
            if(!visible && !OwnCounterOpportunity()) { pending=false; pendingSync=null; return; }
            if(pendingSync!=null && Time.unscaledTimeAsDouble>=expiresAt) { pendingSync=null; LastStatus="sync_timeout"; }
            if(!pending || pendingSync!=null || !Conditions()) return;
            var identity=new CognitiveRecallIdentityDto {observer=CognitiveRecallSubjectIdentityDto.From(owner),requester_entity_id=player.EntityId,
                current_subjects=new[]{CognitiveRecallSubjectIdentityDto.From(player)}};
            if(!identity.IsValid || !bridge.TryCapture(identity,out var runtime)) return;
            pending=false; pendingSync=new TaskRuntimeSyncRequestDto {request_id="task_sync_"+Guid.NewGuid().ToString("N"),
                agent_entity_id=owner.EntityId,recall_identity=identity,task_runtime=runtime,is_idle=true,player_visible=visible,
                counter_observed=GetComponent<CounterCleaningCapability>()?.IsAuthorized==true,counter_condition=bridge.CurrentCondition(owner)};
            diagnosticRuntime=pendingSync.task_runtime;
            bridge.ObserveSync(pendingSync);
            expiresAt=Time.unscaledTimeAsDouble+15; LastStatus="syncing"; _=SendSync(pendingSync);
        }
        private async System.Threading.Tasks.Task SendSync(TaskRuntimeSyncRequestDto request)
        {
            bool sent=await backend.SendTaskRuntimeSyncAsync(request);
            EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("sync",new JObject { ["task_state"]=sent?"sent":"send_failed",
                ["request_id"]=request.request_id,["agent_id"]=request.agent_entity_id,["visit_id"]=request.task_runtime.visit_id,
                ["scene_instance_id"]=request.task_runtime.scene_instance_id,["offer_tasks"]=request.offer_tasks,["player_visible"]=request.player_visible });
            if(!sent && pendingSync==request) { pendingSync=null; LastStatus="sync_send_failed"; }
        }

        public void OnSnapshot(TaskRuntimeSnapshot value)
        {
            var sent=pendingSync;
            if(sent==null || value.RequestId!=sent.request_id || value.AgentId!=owner.EntityId ||
                !value.Owner.Matches(sent.recall_identity.observer) || !value.Player.Matches(sent.recall_identity.current_subjects[0]) ||
                CognitiveRequest.SerializeFields(value.Runtime)!=CognitiveRequest.SerializeFields(sent.task_runtime)) return;
            pendingSync=null;
            if(Time.unscaledTimeAsDouble>=expiresAt || !Conditions() || !bridge.IsCurrent(value.Runtime)) { pending=IsPlayerVisible(); LastStatus="sync_expired"; return; }
            diagnosticRuntime=value.Runtime;diagnosticTasks=value.TaskIds.ToArray();
            var diagnostic=DiagnosticContext();diagnostic["request_id"]=value.RequestId;diagnostic["task_state"]="snapshot_received";
            EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("sync",diagnostic);
            if(value.TaskIds.Length==0) { LastStatus="no_eligible_tasks"; return; }
            snapshot=value; claimed=false; runId=null;
            currentEvent=new AgentEvent("task_event_"+Guid.NewGuid().ToString("N"),AgentEventTypes.TaskConsideration,
                owner.EntityId,owner.EntityId,null,new Dictionary<string,string>{{"player_entity_id",player.EntityId}},
                Time.unscaledTimeAsDouble,worldRevision:GetComponent<GoalPlanRunner>()?.WorldState?.WorldRevision??0,
                traceId:"task_trace_"+Guid.NewGuid().ToString("N"));
            LastStatus="offered";
            if(router!=null && router.Route(currentEvent)==1) { PublishedCount++;
                var published=DiagnosticContext();published["task_state"]="sent";
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("opportunity",published); }
            else Invalidate("route_failed");
        }
        public bool CanWake(AgentEvent value) => currentEvent!=null && ReferenceEquals(value,currentEvent) && !claimed &&
            snapshot!=null && Time.unscaledTimeAsDouble<expiresAt && Conditions();
        public bool TryBind(CognitiveRequest request,CognitiveRun run)
        {
            if(!run.IsTaskConsideration || currentEvent==null || claimed || !ReferenceEquals(run,orchestrator.CurrentRun) ||
                request.context.current_event?.event_id!=currentEvent.EventId || Time.unscaledTimeAsDouble>=expiresAt || !Conditions(run)) return false;
            request.schema_version="1.1"; request.task_runtime=snapshot.Runtime;
            request.task_consideration=new TaskConsiderationContextDto {opportunity_token=snapshot.Token,event_id=currentEvent.EventId};
            claimed=true; runId=run.RunId; LastStatus="considering"; return true;
        }
        public bool Matches(CognitiveRequest request,CognitiveRun run) => claimed && currentEvent!=null && run!=null &&
            run.IsTaskConsideration && run.RunId==runId && ReferenceEquals(run,orchestrator.CurrentRun) &&
            request.task_consideration?.opportunity_token==snapshot.Token && request.context.current_event?.event_id==currentEvent.EventId &&
            request.trace_id==currentEvent.TraceId && bridge.IsCurrent(request.task_runtime) && Time.unscaledTimeAsDouble<expiresAt && Conditions(run);
        public void RecordResult(string status) { currentEvent=null; snapshot=null; claimed=false; LastStatus=status; }
        public void Invalidate(string reason)
        { currentEvent=null; snapshot=null; claimed=false; pendingSync=null; GetComponent<CognitiveRunDriver>()?.CancelTaskConsideration(reason); LastStatus=reason; }
    }
}
