using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    // Validate exact wire shape/scalars before trusting status or cancellation.
    // Later correlation checks still bind the reply to this runtime and attempt.
    internal static class TaskCancelResultWire
    {
        internal static bool TryParse(string json,out JObject value)
        {
            value=null;
            try
            {
                var o=JObject.Parse(json);
                if((string)o["message_type"]!="task_cancel_result" || (string)o["schema_version"]!="1.1" ||
                    o["accepted"]?.Type!=JTokenType.Boolean || o["request_id"]?.Type!=JTokenType.String || o["command_id"]?.Type!=JTokenType.String ||
                    !TaskRuntimeContextDto.IsToken((string)o["request_id"]) || !TaskRuntimeContextDto.IsToken((string)o["command_id"])) return false;
                string[] keys=(bool)o["accepted"]?new[]{"message_type","schema_version","request_id","command_id","agent_entity_id","task_runtime","owner","player","accepted","outcome","task","error_code"}:
                    new[]{"message_type","schema_version","request_id","command_id","accepted","error_code"};
                if(!o.Properties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(keys.OrderBy(n=>n))) return false;
                if((bool)o["accepted"])
                {
                    var runtime=o["task_runtime"] as JObject;
                    if(runtime==null || runtime.Count!=6 || runtime.Properties().Any(p=>p.Value.Type!=JTokenType.String) ||
                        !runtime.Properties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"command_id","client_session_id","visit_id","scene_instance_id","lifecycle_protocol","player_entity_id"}.OrderBy(n=>n)) ||
                        (string)runtime["lifecycle_protocol"]!="1.0" || !runtime.ToObject<TaskRuntimeContextDto>().IsValid ||
                        o["agent_entity_id"].Type!=JTokenType.String || !TaskRuntimeContextDto.IsToken((string)o["agent_entity_id"]) || o["outcome"].Type!=JTokenType.String) return false;
                    foreach(string key in new[]{"owner","player"})
                    {
                        var identity=o[key] as JObject;
                        if(identity==null || identity.Count!=3 || !identity.Properties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"identity_namespace","identity_type","persistent_id"}.OrderBy(n=>n)) ||
                            identity.Properties().Any(p=>p.Value.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))) return false;
                    }
                    string status=(string)o["task"]?["status"],outcome=(string)o["outcome"];
                    if(!TaskStatusDto.Valid(o["task"]) || o["error_code"].Type!=JTokenType.Null ||
                        !(outcome=="too_late"?status=="completed":outcome=="cancelled"?status=="cancelled":
                          outcome=="cancellation_requested" && (bool)o["task"]["cancel_requested"])) return false;
                }
                else if(o["error_code"].Type!=JTokenType.String || !new[]{"TASK_STORE_UNAVAILABLE","TASK_CANCEL_REJECTED"}.Contains((string)o["error_code"])) return false;
                value=o;return true;
            }
            catch { return false; }
        }
    }
    [Serializable] public sealed class TaskConditionDto
    {
        public string scene_instance_id,target_entity_id;
        public int condition_revision;
        public bool Matches(TaskConditionDto other) => other!=null && scene_instance_id==other.scene_instance_id &&
            target_entity_id==other.target_entity_id && condition_revision==other.condition_revision;
    }
    [Serializable] public sealed class TaskStatusDto
    {
        public string task_id,status,source,goal_kind,attempt_id,phase,diagnostic;
        public int version;
        public bool cancel_requested;
        public TaskConditionDto condition;
        internal static bool ValidArray(JToken token) => token is JArray list && list.Count<=11 &&
            list.All(Valid) && list.Select(t=>(string)t["task_id"]).Distinct().Count()==list.Count;
        internal static bool Valid(JToken token)
        {
            var o=token as JObject;
            if(o==null || !o.Properties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"task_id","version","status","source",
                "goal_kind","attempt_id","phase","cancel_requested","diagnostic","condition"}.OrderBy(n=>n)) ||
                o["version"].Type!=JTokenType.Integer || (int)o["version"]<1 || o["cancel_requested"].Type!=JTokenType.Boolean) return false;
            foreach(string key in new[]{"task_id","status","source","goal_kind"}) if(o[key].Type!=JTokenType.String) return false;
            foreach(string key in new[]{"attempt_id","phase","diagnostic"}) if(o[key].Type!=JTokenType.String && o[key].Type!=JTokenType.Null) return false;
            if(!TaskRuntimeContextDto.IsToken((string)o["task_id"]) || !new[]{"waiting","executing","blocked","completed","cancelled"}.Contains((string)o["status"]) ||
                !new[]{"player_request","self_initiated"}.Contains((string)o["source"]) ||
                !new[]{"serve_drink","clean_assigned_counter"}.Contains((string)o["goal_kind"])) return false;
            if(o["condition"].Type!=JTokenType.Null)
            {
                var c=o["condition"] as JObject;
                if(c==null || c.Count!=3 || c["scene_instance_id"]?.Type!=JTokenType.String ||
                    c["target_entity_id"]?.Type!=JTokenType.String || c["condition_revision"]?.Type!=JTokenType.Integer ||
                    !TaskRuntimeContextDto.IsToken((string)c["scene_instance_id"]) || !TaskRuntimeContextDto.IsToken((string)c["target_entity_id"]) ||
                    (int)c["condition_revision"]<1) return false;
            }
            string phase=(string)o["phase"],attempt=(string)o["attempt_id"],status=(string)o["status"];
            return (phase==null ? attempt==null : TaskRuntimeContextDto.IsToken(attempt) &&
                new[]{"reserved","started","succeeded","failed","cancelled","timed_out","uncertain"}.Contains(phase)) &&
                (status!="executing" || phase=="reserved" || phase=="started") &&
                (status!="completed" || phase=="succeeded") && (o["diagnostic"].Type==JTokenType.Null || ((string)o["diagnostic"]).Length<=240);
        }
    }

    public sealed partial class TaskRuntimeBridge
    {
        // Same-process evidence only. No start permits, model retries, disk queue or automatic re-execution.
        private static readonly Dictionary<string,JObject> retained=new();
        // Presentation-only process cache, like the presenter's consumed drink receipts; never a Toast backlog.
        private static readonly HashSet<string> savedNotifications=new(StringComparer.Ordinal);
        private static void ResetRetained() { retained.Clear();savedNotifications.Clear(); }
        private readonly Dictionary<string,WorldEntity> owners=new();
        private readonly Dictionary<string,TaskRuntimeSyncRequestDto> reads=new();
        private readonly Dictionary<string,TaskStatusDto[]> statuses=new();
        private readonly Dictionary<string,TaskIdentityDto> statusOwners=new();
        private readonly Dictionary<string,JObject> reservations=new();
        private readonly Dictionary<string,double> sentAt=new();
        private BackendWebSocketClient transport;
        private bool lifecycleSubscribed;
        private bool LifecycleActive => this!=null && isActiveAndEnabled && lifecycleSubscribed;
        private double retryAt;
        public string FeedbackCode {get;private set;}="";
        public string Diagnostic {get;private set;}="";
        public bool HasPendingResults => retained.Count>0;
        public string[] StatusAgentIds => statuses.Keys.ToArray();
        public string CancellationOwnerLabel(string agent) => owners.TryGetValue(agent,out var owner)?
            owner.GetComponent<CognitiveContextComposer>()?.CharacterDefinition?.DisplayName??agent:agent;
        private bool CurrentAdmission => IsCurrent(new TaskRuntimeContextDto {command_id="current",client_session_id=processVisit.VisitId,
            visit_id=processVisit.VisitId,scene_instance_id=sceneInstanceId,lifecycle_protocol="1.0",player_entity_id=player?.EntityId});
        public TaskStatusDto[] StatusFor(string agent) => CurrentAdmission && owners.TryGetValue(agent,out var owner) &&
            statusOwners.TryGetValue(agent,out var key) && key.Matches(CognitiveRecallSubjectIdentityDto.From(owner)) &&
            statuses.TryGetValue(agent,out var rows)?rows:Array.Empty<TaskStatusDto>();
        private void ApplyStatuses(string agent,IEnumerable<TaskStatusDto> incoming)
        {
            var rows=StatusFor(agent).ToDictionary(t=>t.task_id);
            foreach(var task in incoming) if(!rows.TryGetValue(task.task_id,out var previous) || task.version>=previous.version) rows[task.task_id]=task;
            statuses[agent]=rows.Values.Where(t=>t.status!="completed" && t.status!="cancelled")
                .Concat(rows.Values.Where(t=>t.status=="completed" || t.status=="cancelled").OrderByDescending(t=>t.version).Take(3)).Take(11).ToArray();
            var owner=CognitiveRecallSubjectIdentityDto.From(owners[agent]);
            statusOwners[agent]=new TaskIdentityDto {identity_namespace=owner.identity_namespace,identity_type=owner.identity_type,persistent_id=owner.persistent_id};
        }
        public bool CanRetainExecution => retained.Count<32;

        public void IncludeCurrentPlayer(CognitiveRecallIdentityDto identity)
        {
            if(!admitted || identity==null || player==null) return;
            var value=CognitiveRecallSubjectIdentityDto.From(player);
            if(value!=null && !(identity.current_subjects??Array.Empty<CognitiveRecallSubjectIdentityDto>()).Any(p=>p.entity_id==value.entity_id))
                identity.current_subjects=(identity.current_subjects??Array.Empty<CognitiveRecallSubjectIdentityDto>()).Take(15).Concat(new[]{value}).ToArray();
        }
        public void Register(WorldEntity owner,BackendWebSocketClient backend)
        {
            if(owner==null || backend==null) return;
            if(transport!=backend)
            {
                UnsubscribeLifecycle();transport=backend;
            }
            SubscribeLifecycle();
            owners[owner.EntityId]=owner; RefreshStatus(owner.EntityId);
        }
        private void SubscribeLifecycle()
        {
            // Transport identity survives disable; subscription ownership does not.
            if(lifecycleSubscribed || !isActiveAndEnabled || transport==null) return;
            transport.TaskRuntimeSnapshotReceived+=OnLifecycleSnapshot;transport.TaskExecutionResultReceived+=OnEvidenceResult;
            transport.TaskCancelResultReceived+=OnCancelResult;transport.ConnectionStatusChanged+=OnLifecycleConnection;
            lifecycleSubscribed=true;
        }
        private void UnsubscribeLifecycle()
        {
            // Queries from the previous subscription/connection cannot become new
            // snapshots after re-enable. Retained result evidence is independent.
            reads.Clear();sentAt.Clear();
            if(!lifecycleSubscribed) return;
            if(!ReferenceEquals(transport,null))
            {
                transport.TaskRuntimeSnapshotReceived-=OnLifecycleSnapshot;transport.TaskExecutionResultReceived-=OnEvidenceResult;
                transport.TaskCancelResultReceived-=OnCancelResult;transport.ConnectionStatusChanged-=OnLifecycleConnection;
            }
            lifecycleSubscribed=false;
        }
        private void OnEnable()
        {
            SubscribeLifecycle();
            foreach(string agent in owners.Keys.ToArray()) RefreshStatus(agent);
        }
        private void OnDisable() { UnsubscribeLifecycle(); }
        private void OnDestroy() { UnsubscribeLifecycle();transport=null; }
        private void OnLifecycleConnection(string value)
        {
            if(!LifecycleActive) return;
            reads.Clear();sentAt.Clear();
            if(value=="CONNECTED") foreach(string agent in owners.Keys.ToArray()) RefreshStatus(agent);
            else if(retained.Count>0) FeedbackCode="result_pending";
        }
        public TaskConditionDto CurrentCondition(WorldEntity owner)
        {
            var counter=owner==null?null:owner.GetComponent<CounterCleaningCapability>()?.Capture();
            return counter?.needs_cleaning==true ? new TaskConditionDto {scene_instance_id=sceneInstanceId,
                target_entity_id=counter.target_entity_id,condition_revision=counter.condition_revision}:null;
        }
        internal TaskRuntimeSyncRequestDto CaptureSync(WorldEntity owner,bool offer,bool idle,bool visible)
        {
            if(owner==null || player==null) return null;
            var identity=new CognitiveRecallIdentityDto {observer=CognitiveRecallSubjectIdentityDto.From(owner),
                requester_entity_id=player.EntityId,current_subjects=new[]{CognitiveRecallSubjectIdentityDto.From(player)}};
            if(!identity.IsValid || !TryCapture(identity,out var context)) return null;
            return new TaskRuntimeSyncRequestDto {request_id="task_sync_"+Guid.NewGuid().ToString("N"),agent_entity_id=owner.EntityId,
                recall_identity=identity,task_runtime=context,is_idle=idle,offer_tasks=offer,player_visible=visible,
                counter_observed=owner.GetComponent<CounterCleaningCapability>()?.IsAuthorized==true,counter_condition=CurrentCondition(owner)};
        }
        internal void ObserveSync(TaskRuntimeSyncRequestDto value)
        { if(value!=null) { reads[value.request_id]=value;sentAt[value.request_id]=Time.unscaledTimeAsDouble; } }
        public void RefreshStatus(string agent)
        {
            if(!LifecycleActive || transport?.IsConnected!=true || !owners.TryGetValue(agent,out var owner)) return;
            var sync=CaptureSync(owner,false,false,false);if(sync==null) return;
            ObserveSync(sync);_=transport.SendTaskRuntimeSyncAsync(sync);
        }
        private void OnLifecycleSnapshot(TaskRuntimeSnapshot value)
        {
            if(!LifecycleActive) return;
            if(!reads.TryGetValue(value.RequestId,out var sent)) return;
            reads.Remove(value.RequestId);sentAt.Remove(value.RequestId);
            if(value.Tasks==null || value.AgentId!=sent.agent_entity_id || !IsCurrent(value.Runtime) ||
                CognitiveRequest.SerializeFields(value.Runtime)!=CognitiveRequest.SerializeFields(sent.task_runtime) ||
                !value.Owner.Matches(sent.recall_identity.observer) || !value.Player.Matches(sent.recall_identity.current_subjects[0]) ||
                !owners.TryGetValue(value.AgentId,out var owner) || !value.Owner.Matches(CognitiveRecallSubjectIdentityDto.From(owner))) return;
            ApplyStatuses(value.AgentId,value.Tasks);
            ObserveDiagnosticSnapshot(value);
            if(value.Tasks.Any(t=>t.status=="blocked")) FeedbackCode=value.Tasks.Any(t=>t.phase=="uncertain")?"needs_verification":"failed";
        }
        private readonly Dictionary<string,string> diagnosticSnapshots=new();
        private void ObserveDiagnosticSnapshot(TaskRuntimeSnapshot value)
        {
            try
            {
                string signature=string.Join(";",value.Tasks.Select(t=>t.task_id+":"+t.version+":"+t.status+":"+t.phase));
                if(diagnosticSnapshots.TryGetValue(value.AgentId,out var previous) && previous==signature)return;
                if(diagnosticSnapshots.Count>=128)diagnosticSnapshots.Clear();
                diagnosticSnapshots[value.AgentId]=signature;
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("sync",new JObject { ["task_state"]="snapshot_received",
                    ["request_id"]=value.RequestId,["agent_id"]=value.AgentId,["visit_id"]=value.Runtime.visit_id,
                    ["scene_instance_id"]=value.Runtime.scene_instance_id,["runtime_valid"]=true,["task_count"]=value.Tasks.Length,
                    ["eligible_count"]=value.TaskIds.Length,["task_ids"]=new JArray(value.Tasks.Where(t=>t.status=="waiting" || t.status=="executing" || t.status=="blocked").Take(3).Select(t=>t.task_id)),
                    ["waiting_count"]=value.Tasks.Count(t=>t.status=="waiting") });
            }
            catch(Exception) { }
        }
        public bool HasConditionTask(string agent,TaskConditionDto condition) => condition!=null &&
            StatusFor(agent).Any(t=>t.goal_kind=="clean_assigned_counter" && (t.status=="waiting" || t.status=="executing" || t.status=="blocked") && condition.Matches(t.condition));
        public void AcceptReceipt(CognitiveRequest request,CognitiveResponse response)
        {
            if(response.task_receipt==null || !IsCurrent(request.task_runtime) || !TaskReceiptValidator.Matches(response,request)) return;
            EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("persistence",new JObject { ["task_state"]=response.task_receipt.committed?"committed":"failed",
                ["request_id"]=request.request_id,["run_id"]=request.run.run_id,["agent_id"]=request.run.agent_entity_id,
                ["visit_id"]=request.task_runtime.visit_id,["scene_instance_id"]=request.task_runtime.scene_instance_id,
                ["task_id"]=response.task_receipt.task_id,["receipt_committed"]=response.task_receipt.committed,["operation"]=response.task_receipt.operation,
                ["task_status"]=response.task_receipt.task_status,["error_code"]=response.task_receipt.error_code,["receipt_outcome"]=response.task_receipt.outcome });
            FeedbackCode=!response.task_receipt.committed?"save_failed":response.task_receipt.outcome=="cancellation_requested"?
                "cancel_pending":response.task_receipt.task_status switch {
                    "cancelled"=>"cancelled", "blocked"=>"needs_verification", "executing"=>"result_pending", "completed"=>"completed", _=>"saved" };
            if(response.task_receipt.committed && owners.TryGetValue(request.run.agent_entity_id,out var owner))
                owner.GetComponent<TaskConsiderationSource>()?.SuppressUntilNewOpportunity();
            NotifySaved(request,response.task_receipt);
            RefreshStatus(request.run.agent_entity_id);
        }
        private void NotifySaved(CognitiveRequest request,CognitiveTaskReceiptDto receipt)
        {
            // Recovered/deduplicated commands and status reads are historical, not new UI events.
            if(!receipt.committed || receipt.recovered || receipt.task_status!="waiting" ||
                !(receipt.operation=="create" && receipt.outcome=="created" || receipt.operation=="update" && receipt.outcome=="updated") ||
                request.context.current_event?.event_type!="player.utterance") return;
            string key=receipt.visit_id+":"+receipt.command_id;
            if(!savedNotifications.Add(key)) return;
            try {if(owners.TryGetValue(request.run.agent_entity_id,out var owner))
                foreach(var presenter in FindObjectsByType<EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter>(FindObjectsSortMode.None))
                    if(presenter.ObserveTaskSaved(owner,player))break;}
            catch(Exception) { } // UI observation cannot affect the task receipt or synchronization.
        }
        internal void TrackReservation(CognitiveRequest request,CognitiveResponse response)
        {
            var ticket=response.task_execution;
            if(!IsCurrent(request.task_runtime) || ticket?.Matches(request,response)!=true) return;
            reservations[ticket.attempt_id]=ticket.Wire;
            var row=StatusFor(ticket.agent_entity_id).FirstOrDefault(t=>t.task_id==ticket.task_id);
            if(row!=null && row.version<=ticket.task_version)
            { row.version=ticket.task_version;row.status="executing";row.attempt_id=ticket.attempt_id;row.phase="reserved"; }
        }
        internal void RetainEvidence(JObject body)
        {
            string operation=(string)body?["operation"];
            if(operation!="settle_execution" && operation!="not_started" && operation!="mark_execution_unknown") return;
            string attempt=(string)body["ticket"]?["attempt_id"];if(!TaskRuntimeContextDto.IsToken(attempt)) return;
            retained[attempt]=(JObject)body.DeepClone();FeedbackCode=retained.Values.Any(b=>(string)b["operation"]=="cancel_task")?"cancel_pending":"result_pending";retryAt=0;
        }
        private bool RetainedBelongsHere(JObject body)
        {
            var identity=body["ticket"] as JObject;
            if((string)body["operation"]=="cancel_task") identity=body["runtime_request"]?["recall_identity"]?["observer"] as JObject;
            string agent=(string)body["ticket"]?["agent_entity_id"]??(string)body["runtime_request"]?["agent_entity_id"];
            var original=body["original_request"]?["task_runtime"]??body["runtime_request"]?["task_runtime"];
            if((string)original?["client_session_id"]!=processVisit.VisitId || !owners.TryGetValue(agent??"",out var owner)) return false;
            var expected=(body["ticket"]?["owner"]??identity)?.ToObject<TaskIdentityDto>();
            var expectedPlayer=(body["ticket"]?["player"]??body["runtime_request"]?["recall_identity"]?["current_subjects"]?[0])?.ToObject<TaskIdentityDto>();
            return expected?.Matches(CognitiveRecallSubjectIdentityDto.From(owner))==true && expectedPlayer?.Matches(CognitiveRecallSubjectIdentityDto.From(player))==true;
        }
        private void OnEvidenceResult(TaskExecutionResultDto value)
        {
            // A delegate already being dispatched can outlive Unsubscribe/Destroy.
            if(!LifecycleActive) return;
            if(value.operation=="begin_execution")
            {
                if(value.accepted && reservations.TryGetValue(value.ticket.attempt_id,out var reservation) && JToken.DeepEquals(reservation,value.ticket.Wire))
                {
                    var active=StatusFor(value.ticket.agent_entity_id).FirstOrDefault(t=>t.task_id==value.ticket.task_id);
                    if(active!=null && active.version<=value.task_version) { active.version=value.task_version.Value;active.phase="started";active.status="executing"; }
                    RefreshStatus(value.ticket.agent_entity_id);
                }
                return;
            }
            if(!retained.TryGetValue(value.ticket.attempt_id,out var body) ||
                (string)body["operation"]!=value.operation || !JToken.DeepEquals(body["ticket"],value.ticket.Wire) || !RetainedBelongsHere(body)) return;
            if(!value.accepted) { FeedbackCode=value.error_code=="TASK_EXECUTION_STORE_UNAVAILABLE"?"result_pending":"needs_verification";
                Diagnostic=value.error_code;if(value.error_code!="TASK_EXECUTION_STORE_UNAVAILABLE") body["local_rejected"]=true;return; }
            retained.Remove(value.ticket.attempt_id);FeedbackCode=value.task_status=="blocked"?
                value.phase=="uncertain"?"needs_verification":"failed":value.task_status;
            var row=StatusFor(value.ticket.agent_entity_id).FirstOrDefault(t=>t.task_id==value.ticket.task_id);
            if(row!=null && row.version<=value.task_version)
            { row.version=value.task_version.Value;row.status=value.task_status;row.phase=value.phase;row.attempt_id=value.ticket.attempt_id; }
            RefreshStatus(value.ticket.agent_entity_id);
            reservations.Remove(value.ticket.attempt_id);
        }
        public bool TryCancellationCandidate(out string agent,out TaskStatusDto task)
        {
            agent=null;task=null;
            foreach(var pair in statuses)
            {
                var candidate=StatusFor(pair.Key).Where(t=>t.source=="player_request" && !t.cancel_requested &&
                    (t.status=="waiting" || t.status=="executing" || t.status=="blocked"))
                    .OrderByDescending(t=>t.status=="executing").FirstOrDefault();
                if(candidate==null || retained.Values.Any(b=>(string)b["operation"]=="cancel_task" && (string)b["task_id"]==candidate.task_id)) continue;
                agent=pair.Key;task=candidate;return true;
            }
            return false;
        }
        public bool RequestPlayerCancellation(string agent,TaskStatusDto task)
        {
            if(!owners.TryGetValue(agent,out var owner) || !StatusFor(agent).Any(t=>ReferenceEquals(t,task)) ||
                task.source!="player_request" || task.cancel_requested || retained.Count>=32) return false;
            var sync=CaptureSync(owner,false,false,false);if(sync==null) return false;
            var body=new JObject { ["message_type"]="task_control",["schema_version"]="1.1",["operation"]="cancel_task",
                ["runtime_request"]=JObject.Parse(CognitiveRequest.SerializeFields(sync)),["task_id"]=task.task_id,
                ["expected_version"]=task.version,["attempt_id"]=task.attempt_id };
            retained[sync.task_runtime.command_id]=body;FeedbackCode="cancel_pending";retryAt=0;
            owner.GetComponent<TaskExecutionBridge>()?.RequestPlayerStop(task.task_id,task.attempt_id);
            return true;
        }
        private void OnCancelResult(JObject value)
        {
            if(!LifecycleActive) return;
            string command=(string)value["command_id"];
            if(command==null || !retained.TryGetValue(command,out var body) || (string)body["operation"]!="cancel_task" ||
                (string)value["request_id"]!=(string)body["runtime_request"]?["request_id"] || !RetainedBelongsHere(body)) return;
            if((bool)value["accepted"])
            {
                if(!TaskStatusDto.Valid(value["task"]) || (string)value["task"]?["task_id"]!=(string)body["task_id"] ||
                    (int)value["task"]["version"]<(int)body["expected_version"] ||
                    (string)value["task"]?["attempt_id"]!=(string)body["attempt_id"] ||
                    (string)value["agent_entity_id"]!=(string)body["runtime_request"]?["agent_entity_id"] ||
                    !value["player"].ToObject<TaskIdentityDto>().Matches(body["runtime_request"]?["recall_identity"]?["current_subjects"]?[0]?.ToObject<CognitiveRecallSubjectIdentityDto>()) ||
                    !JToken.DeepEquals(value["task_runtime"],body["runtime_request"]?["task_runtime"]) ||
                    !JToken.DeepEquals(value["owner"],body["runtime_request"]?["recall_identity"]?["observer"] is JObject o?
                        new JObject { ["identity_namespace"]=o["identity_namespace"],["identity_type"]=o["identity_type"],["persistent_id"]=o["persistent_id"]}:null)) return;
                retained.Remove(command);FeedbackCode=(string)value["outcome"];
                ApplyStatuses((string)value["agent_entity_id"],new[]{value["task"].ToObject<TaskStatusDto>()});
                // Consuming the correlated retained command above also deduplicates acknowledgement replays.
                // An executing task's cancellation_requested outcome is not a confirmed cancellation.
                if(FeedbackCode=="cancelled" && (string)value["task"]["status"]=="cancelled" &&
                    owners.TryGetValue((string)value["agent_entity_id"],out var owner))
                    try { foreach(var presenter in FindObjectsByType<EmbodiedNPC.Dialogue.Presentation.ActionFeedbackPresenter>(FindObjectsSortMode.None))
                        if(presenter.ObserveTaskCancelled(owner,player))break; } catch(Exception) { /* Presentation never changes the accepted receipt. */ }
                RefreshStatus((string)body["runtime_request"]["agent_entity_id"]);
            }
            else { FeedbackCode=(string)value["error_code"]=="TASK_STORE_UNAVAILABLE"?"cancel_pending":"needs_verification";
                Diagnostic=(string)value["error_code"];if(Diagnostic!="TASK_STORE_UNAVAILABLE") body["local_rejected"]=true; }
        }
        private void Update()
        {
            foreach(var key in sentAt.Where(p=>Time.unscaledTimeAsDouble-p.Value>15).Select(p=>p.Key).ToArray())
            { sentAt.Remove(key);reads.Remove(key); }
            if(transport?.IsConnected!=true || Time.unscaledTimeAsDouble<retryAt) return;
            retryAt=Time.unscaledTimeAsDouble+3;
            foreach(var body in retained.Values.ToArray()) if(body["local_rejected"]==null && RetainedBelongsHere(body))
                _=transport.SendTaskExecutionControlAsync((JObject)body.DeepClone());
        }
    }
}
