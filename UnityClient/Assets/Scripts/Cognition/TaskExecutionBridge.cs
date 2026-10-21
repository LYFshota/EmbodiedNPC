using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EmbodiedNPC.Cognition
{
    [Serializable]
    public sealed class TaskExecutionRequestDto
    {
        public int expected_version, world_revision;
        public string scene_instance_id, proposal_id, trace_id, actor_entity_id;
        public string player_entity_id, target_entity_id;
        public int? condition_revision;
    }

    [Serializable]
    public sealed class TaskExecutionTicketDto
    {
        public string schema_version, request_id, trace_id, run_id, agent_entity_id, command_id;
        public TaskIdentityDto owner, player;
        public string visit_id, scene_instance_id, task_id, attempt_id;
        public int task_version;
        public JObject goal;
        public TaskExecutionRequestDto execution_request;
        internal static readonly JsonSerializerSettings Strict = new() { MissingMemberHandling=MissingMemberHandling.Error };
        public bool IsValid => schema_version=="1.1" && owner?.identity_namespace=="npc" && owner.identity_type=="Character" &&
            !string.IsNullOrWhiteSpace(owner.persistent_id) && player?.identity_namespace=="player" && player.identity_type=="Player" &&
            !string.IsNullOrWhiteSpace(player.persistent_id) && new[]{request_id,trace_id,run_id,agent_entity_id,command_id,
                visit_id,scene_instance_id,task_id,attempt_id}.All(TaskRuntimeContextDto.IsToken) && task_version>1 &&
            execution_request!=null && execution_request.expected_version==task_version-1 && execution_request.world_revision>=0 &&
            execution_request.scene_instance_id==scene_instance_id && execution_request.trace_id==trace_id &&
            execution_request.actor_entity_id==agent_entity_id && execution_request.proposal_id==GoalPlanRunner.NormalizeProposalId(request_id) &&
            ValidGoal();
        private bool ValidGoal()
        {
            if(goal==null || goal.Properties().Any(p=>p.Value.Type!=JTokenType.String)) return false;
            return (string)goal["kind"]=="serve_drink" ? goal.Count==2 && DrinkTypes.IsValid((string)goal["drink_type"]) &&
                !string.IsNullOrEmpty(execution_request.player_entity_id) && execution_request.target_entity_id==null && execution_request.condition_revision==null :
                (string)goal["kind"]=="clean_assigned_counter" && goal.Count==2 && (string)goal["counter_key"]=="assigned_counter" &&
                execution_request.player_entity_id==null && !string.IsNullOrEmpty(execution_request.target_entity_id) && execution_request.condition_revision>0;
        }
        public bool Matches(CognitiveRequest request,CognitiveResponse response)
        {
            var identity=request?.recall_identity;
            var participant=identity?.current_subjects?.FirstOrDefault(p=>p.entity_id==request.context.relevant_player?.player_entity_id && p.kind=="player");
            var runtime=request?.task_runtime;
            return IsValid && runtime?.IsValid==true && request.task_consideration?.execution_protocol=="1.0" &&
                request.context.current_event?.event_type=="npc.task_consideration" &&
                request_id==request.request_id && trace_id==request.trace_id && run_id==request.run.run_id && agent_entity_id==request.run.agent_entity_id &&
                command_id==runtime.command_id && visit_id==runtime.visit_id && scene_instance_id==runtime.scene_instance_id &&
                owner.Matches(identity?.observer) && player.Matches(participant) && request.context.current_event.has_world_revision &&
                request.context.current_event.world_revision==execution_request.world_revision && MatchesProposal(response?.decision?.action_proposal) &&
                ((string)goal["kind"]=="serve_drink" ? execution_request.player_entity_id==request.context.relevant_player.player_entity_id :
                    execution_request.target_entity_id==request.context.counter_work?.target_entity_id &&
                    execution_request.condition_revision==request.context.counter_work?.condition_revision) &&
                response?.decision?.target_daily_mode==null && response.decision.dialogue_turn_policy!="MAY_CONTINUE";
        }
        public bool MatchesProposal(CognitiveActionProposal proposal) => proposal?.bindings?.Count==1 &&
            ((string)goal?["kind"]=="serve_drink" ? proposal.action_id=="prepare_drink" &&
                proposal.bindings.TryGetValue("drink_type",out var drink) && drink==(string)goal["drink_type"] :
                proposal.action_id=="clean_counter" && proposal.bindings.TryGetValue("target",out var target) && target==execution_request.target_entity_id);
        internal bool MatchesWorldReceipt(GoalOutcome outcome,DrinkReceipt? drink,CounterCleaningReceipt? counter)
        {
            var expected=execution_request;
            if(outcome.Status!=PlanExecutionStatus.Succeeded || outcome.ProposalRevision!=0 || outcome.ActorEntityId!=agent_entity_id ||
                outcome.ProposalId!=expected.proposal_id || outcome.TraceId!=trace_id) return false;
            if((string)goal["kind"]=="serve_drink")
                return outcome.GoalType=="prepare_drink" && !counter.HasValue && drink.HasValue &&
                    TaskRuntimeContextDto.IsToken(drink.Value.Id) && drink.Value.Actor==agent_entity_id &&
                    drink.Value.Proposal==expected.proposal_id && drink.Value.Trace==trace_id &&
                    drink.Value.Player==expected.player_entity_id && drink.Value.DrinkType==(string)goal["drink_type"] &&
                    expected.world_revision<drink.Value.WorldRevision && drink.Value.WorldRevision<=outcome.WorldRevision;
            return outcome.GoalType=="clean_counter" && !drink.HasValue && counter.HasValue &&
                TaskRuntimeContextDto.IsToken(counter.Value.Id) && outcome.TargetEntityId==expected.target_entity_id &&
                counter.Value.Target==expected.target_entity_id && counter.Value.ConditionVersion==expected.condition_revision &&
                expected.world_revision<counter.Value.WorldRevision && counter.Value.WorldRevision<=outcome.WorldRevision;
        }
        internal JObject Wire => JObject.Parse(CognitiveRequest.SerializeFields(this));
        internal static bool StrictScalars(JObject value)
        {
            // Json.NET's permissive numeric/string coercions are not wire authority.
            if(value==null) return false;
            foreach(var p in value.Properties())
            {
                if(p.Value is JObject child) { if(!StrictScalars(child)) return false; }
                else if(p.Name=="task_version" || p.Name=="expected_version" || p.Name=="world_revision" || p.Name=="condition_revision")
                { if(p.Value.Type!=JTokenType.Integer && !(p.Name=="condition_revision" && p.Value.Type==JTokenType.Null)) return false; }
                else if(p.Value.Type!=JTokenType.String && p.Value.Type!=JTokenType.Null) return false;
            }
            return true;
        }
        // Shape/type checks reject coercion and extra fields; Matches/LiveGuard
        // separately establish identity, correlation and current world authority.
        internal static bool StrictShape(JObject value)
        {
            bool Keys(JObject item,params string[] keys)=>item!=null && item.Properties().Select(p=>p.Name).OrderBy(k=>k)
                .SequenceEqual(keys.OrderBy(k=>k));
            return StrictScalars(value) && Keys(value,"schema_version","request_id","trace_id","run_id","agent_entity_id","command_id",
                "owner","player","visit_id","scene_instance_id","task_id","task_version","attempt_id","goal","execution_request") &&
                Keys(value["owner"] as JObject,"identity_namespace","identity_type","persistent_id") &&
                Keys(value["player"] as JObject,"identity_namespace","identity_type","persistent_id") &&
                Keys(value["execution_request"] as JObject,"expected_version","world_revision","scene_instance_id","proposal_id","trace_id",
                    "actor_entity_id","player_entity_id","target_entity_id","condition_revision");
        }
    }

    [Serializable]
    public sealed class TaskExecutionResultDto
    {
        public string message_type,schema_version,operation,task_status,phase,error_code;
        public int? task_version;
        public bool accepted,start_permitted;
        public TaskExecutionTicketDto ticket;
        public static bool TryParse(string json,out TaskExecutionResultDto value)
        {
            value=null;
            try
            {
                var wire=JObject.Parse(json);
                if((string)wire["message_type"]!="task_execution_result" || wire["accepted"]?.Type!=JTokenType.Boolean ||
                    wire["start_permitted"]?.Type!=JTokenType.Boolean || !TaskExecutionTicketDto.StrictShape(wire["ticket"] as JObject) ||
                    wire["task_version"]?.Type!=JTokenType.Integer && wire["task_version"]?.Type!=JTokenType.Null) return false;
                value=JsonConvert.DeserializeObject<TaskExecutionResultDto>(json,TaskExecutionTicketDto.Strict);
                return value.schema_version=="1.1" && value.ticket?.IsValid==true &&
                    new[]{"begin_execution","not_started","settle_execution","mark_execution_unknown"}.Contains(value.operation) &&
                    value.ValidResult();
            }
            catch(JsonException) { value=null;return false; }
        }
        private bool ValidResult()
        {
            if(!accepted) return !start_permitted && task_status==null && task_version==null && phase==null &&
                (error_code=="TASK_EXECUTION_REJECTED" || error_code=="TASK_EXECUTION_STORE_UNAVAILABLE");
            if(error_code!=null || !(task_version>ticket.task_version)) return false;
            if(operation=="begin_execution") return start_permitted && phase=="started" && task_status=="executing" && task_version==ticket.task_version+1;
            if(start_permitted || operation=="not_started" && phase!="failed" && phase!="cancelled") return false;
            return phase=="succeeded" ? task_status=="completed" : phase=="cancelled" ? task_status=="cancelled" || task_status=="blocked" :
                (phase=="failed" || phase=="timed_out" || phase=="uncertain") && task_status=="blocked";
        }
    }

    /// <summary>Owns one ticket, one start attempt and retryable evidence; never selects tasks or plans actions.</summary>
    [DisallowMultipleComponent]
    public sealed class TaskExecutionBridge : MonoBehaviour
    {
        private BackendWebSocketClient backend;
        private CognitiveRequest request;
        private CognitiveResponse response;
        private TaskExecutionTicketDto ticket;
        private GoalPlanRunner runner;
        private WorldStateService world;
        private TaskRuntimeBridge runtime;
        private EntityRegistry registry;
        private WorldEntity actor,player;
        private Action<bool> ready;
        private JObject pendingControl;
        // Local handshake only: awaiting_begin -> permitted -> executing ->
        // settling -> settled (or blocked rejection). These are not TaskRecord
        // status/phase values. A permit is not a world commit or settlement.
        private string stage="none",stationId;
        private double deadline,retryAt;
        private readonly HashSet<string> spent=new();
        public string LastStatus {get;private set;}="none";
        private bool playerStopRequested;
        public bool RequestPlayerStop(string taskId,string attemptId)
        {
            if(ticket?.task_id!=taskId || ticket.attempt_id!=attemptId || stage=="settled") return false;
            playerStopRequested=true;
            if(stage=="executing") runner.InterruptCurrentPlan("TASK_PLAYER_CANCEL_REQUESTED");
            else if(stage=="permitted") NotStarted();
            return true;
        }
        public bool Owns(string proposal,string trace) => ticket!=null && ticket.execution_request.proposal_id==proposal && ticket.trace_id==trace;

        internal bool RequestStart(CognitiveRequest original,CognitiveResponse result,BackendWebSocketClient transport,
            GoalPlanRunner goals,Action<bool> callback)
        {
            if(ticket!=null && stage!="settled" || result.task_execution?.Matches(original,result)!=true ||
                spent.Contains(result.task_execution.attempt_id) || transport==null || !transport.IsConnected || goals==null)
            { EmbodiedNPC.Settings.DiagnosticsExport.RecordTask("admission",new JObject { ["task_state"]="rejected",["request_id"]=original?.request_id,
                ["run_id"]=original?.run?.run_id,["error_code"]="TASK_START_REJECTED" });return false; }
            request=JsonConvert.DeserializeObject<CognitiveRequest>(original.Serialize());
            response=JsonConvert.DeserializeObject<CognitiveResponse>(CognitiveRequest.SerializeFields(result));
            ticket=response.task_execution; backend=transport; runner=goals; ready=callback;
            actor=GetComponent<WorldEntity>(); registry=FindAnyObjectByType<EntityRegistry>(); runtime=FindAnyObjectByType<TaskRuntimeBridge>();
            world=runner.WorldState;playerStopRequested=false;
            if(runtime?.CanRetainExecution!=true)
            {ObserveExecution("rejected",error:"TASK_RUNTIME_NOT_READY");return false;}
            player=registry!=null && registry.TryGetEntity(request.context.relevant_player.player_entity_id,out var entity)?entity:null;
            stationId=(string)ticket.goal["kind"]=="serve_drink"?GetComponent<DrinkPreparationCapability>()?.Station?.Entity?.EntityId:
                GetComponent<CounterCleaningCapability>()?.Station?.Entity?.EntityId;
            backend.TaskExecutionResultReceived-=OnResult;backend.TaskExecutionResultReceived+=OnResult;
            backend.CognitiveResponseReceived-=OnTaskMutation;backend.CognitiveResponseReceived+=OnTaskMutation;
            runner.GoalOutcomePublished-=OnOutcome;runner.GoalOutcomePublished+=OnOutcome;
            runner.TaskStarted-=OnWorldStarted;runner.TaskStarted+=OnWorldStarted;
            spent.Add(ticket.attempt_id); stage="awaiting_begin"; deadline=Time.unscaledTimeAsDouble+5;
            response.validated_goal.RequestingPlayerId=request.context.relevant_player.player_entity_id;
            if(!LiveGuard() || !runner.TryPrevalidateGoal(response.validated_goal,ticket.execution_request.world_revision,out _))
            { NotStarted(); return true; }
            Send(Control("begin_execution")); LastStatus="awaiting_begin";return true;
        }

        internal bool HasPermission(CognitiveRequest original,CognitiveRun run,CognitiveActionProposal proposal) =>
            stage=="permitted" && ticket?.Matches(original,response)==true && ticket.MatchesProposal(proposal) && LiveGuard() &&
            (GetComponent<TaskConsiderationSource>()?.Matches(original,run)??false);

        internal bool ConsumeStart(CognitiveRequest original,CognitiveResponse result)
        {
            if(!HasPermission(original,GetComponent<AgentOrchestrator>()?.CurrentRun,result.decision.action_proposal) ||
                !JToken.DeepEquals(ticket.Wire,result.task_execution?.Wire) || !runner.TryPrevalidateGoal(result.validated_goal,
                    ticket.execution_request.world_revision,out _)) return false;
            stage="executing"; LastStatus="executing";
            world.RegisterTaskGuard(ticket.agent_entity_id,ticket.execution_request.proposal_id,ticket.trace_id,AllowsEffect);
            return true;
        }

        // Recheck current Visit/scene, registered identities and live capability
        // at use time. A previously valid ticket cannot authorize a changed world.
        private bool LiveGuard() => this!=null && isActiveAndEnabled && !playerStopRequested && runtime?.IsCurrent(request?.task_runtime)==true &&
            actor!=null && actor.EntityId==ticket.agent_entity_id && actor.TryGetPersistentIdentityKey(out var ownerKey) && ticket.owner.identity_namespace==ownerKey.Namespace &&
            ticket.owner.identity_type==ownerKey.IdentityType && ticket.owner.persistent_id==ownerKey.PersistentId &&
            registry!=null && registry.IsReady && registry.TryGetEntity(actor.EntityId,out var a) && a==actor &&
            player!=null && player.EntityId==request.context.relevant_player.player_entity_id && registry.TryGetEntity(player.EntityId,out var p) && p==player &&
            player.TryGetPersistentIdentityKey(out var playerKey) && ticket.player.identity_namespace==playerKey.Namespace &&
            ticket.player.identity_type==playerKey.IdentityType && ticket.player.persistent_id==playerKey.PersistentId &&
            ((string)ticket.goal["kind"]=="clean_assigned_counter" || (GetComponent<TaskConsiderationSource>()?.IsPlayerVisible()??false)) && world!=null && runner!=null &&
            ((string)ticket.goal["kind"]=="serve_drink" ? GetComponent<DrinkPreparationCapability>()?.IsAuthorized==true &&
                GetComponent<DrinkPreparationCapability>().Station.Entity.EntityId==stationId :
                GetComponent<CounterCleaningCapability>()?.IsAuthorized==true &&
                GetComponent<CounterCleaningCapability>().Station.Entity.EntityId==stationId);

        private bool AllowsEffect(string action,string target,string recipient,string drink,int condition) =>
            stage=="executing" && LiveGuard() && target==stationId && ((string)ticket.goal["kind"]=="serve_drink" ?
                action=="prepare_drink" && recipient==request.context.relevant_player.player_entity_id && drink==(string)ticket.goal["drink_type"] :
                action=="clean_counter" && target==ticket.execution_request.target_entity_id && condition==ticket.execution_request.condition_revision);

        private JObject Control(string operation,JObject evidence=null) => new() { ["message_type"]="task_control",["schema_version"]="1.1",
            ["operation"]=operation,["original_request"]=JObject.Parse(request.Serialize()),["ticket"]=ticket.Wire,["result"]=evidence };
        private void Send(JObject control) { pendingControl=control;retryAt=Time.unscaledTimeAsDouble+2;
            runtime?.RetainEvidence(control);_=backend.SendTaskExecutionControlAsync(control); }
        private void OnResult(TaskExecutionResultDto value)
        {
            if(ticket==null || !JToken.DeepEquals(ticket.Wire,value.ticket?.Wire) || (string)pendingControl?["operation"]!=value.operation) return;
            ObserveExecution(value.accepted?(value.operation=="begin_execution"?"permitted":"settled"):"rejected",
                value.task_status,value.phase,value.error_code,value.start_permitted);
            if(!value.accepted && value.error_code=="TASK_EXECUTION_STORE_UNAVAILABLE" && value.operation!="begin_execution")
            { LastStatus="settlement_pending";retryAt=Time.unscaledTimeAsDouble+3;return; }
            pendingControl=null;
            if(value.operation=="begin_execution")
            {
                if(stage!="awaiting_begin") return;
                if(!value.accepted || !value.start_permitted) { NotStarted();return; }
                stage="permitted";
                if(!LiveGuard()) { NotStarted();return; }
                var callback=ready;ready=null;callback?.Invoke(true);
                if(stage=="permitted") NotStarted(); // Current Run may have expired while the permit travelled.
            }
            else
            {
                if(value.accepted) { stage="settled";LastStatus=value.task_status+":"+value.phase; }
                else { LastStatus="settlement_rejected";stage="blocked"; }
            }
        }
        private JObject Evidence(string status,string effect,string code,GoalOutcome? outcome=null,JObject receipt=null) => new()
        {
            ["attempt_id"]=ticket.attempt_id,["scene_instance_id"]=ticket.scene_instance_id,
            ["proposal_id"]=ticket.execution_request.proposal_id,["trace_id"]=ticket.trace_id,["actor_entity_id"]=ticket.agent_entity_id,
            ["outcome_id"]=outcome?.OutcomeId??("not_started_"+ticket.attempt_id),
            ["goal_type"]=(string)ticket.goal["kind"]=="serve_drink"?"prepare_drink":"clean_counter",
            ["status"]=status,["effect"]=effect,["world_revision"]=outcome?.WorldRevision??world?.WorldRevision??ticket.execution_request.world_revision,
            ["failure_code"]=code,["receipt"]=receipt
        };
        internal void NotStarted()
        {
            if(ticket==null || stage=="settling" || stage=="settled") return;
            stage="settling";LastStatus="not_started";
            ObserveExecution("not_started",error:"TASK_NOT_STARTED");
            Send(Control("not_started",Evidence("failed","not_committed","TASK_NOT_STARTED")));
            var callback=ready;ready=null;callback?.Invoke(false);
        }
        internal void Unknown()
        {
            if(ticket==null || stage=="settled" || stage=="settling") return;
            stage="settling";LastStatus="effect_unknown";Send(Control("mark_execution_unknown"));
            ObserveExecution("effect_unknown");
            var callback=ready;ready=null;callback?.Invoke(false);
        }
        // Settle only the owned attempt from real Outcome/receipt evidence.
        // A missing acknowledgement or language explanation cannot prove success.
        private void OnOutcome(GoalOutcome outcome)
        {
            if(stage!="executing" || !Owns(outcome.ProposalId,outcome.TraceId) || outcome.ProposalRevision!=0 ||
                outcome.ActorEntityId!=ticket.agent_entity_id || outcome.GoalType!=response.validated_goal.type) return;
            JObject receipt=null; var key=TaskFeedbackKey.From(outcome);
            bool drink=runner.TryGetDrinkReceipt(key,out var d),clean=runner.TryGetCounterCleaningReceipt(key,out var c);
            if(drink) receipt=new JObject { ["kind"]="drink_prepared",["receipt_id"]=d.Id,["actor_entity_id"]=d.Actor,["proposal_id"]=d.Proposal,
                ["trace_id"]=d.Trace,["world_revision"]=d.WorldRevision,["player"]=JObject.FromObject(ticket.player),
                ["player_entity_id"]=d.Player,["drink_type"]=d.DrinkType };
            if(clean) receipt=new JObject { ["kind"]="counter_cleaned",["receipt_id"]=c.Id,["actor_entity_id"]=outcome.ActorEntityId,
                ["proposal_id"]=outcome.ProposalId,["trace_id"]=outcome.TraceId,["world_revision"]=c.WorldRevision,
                ["target_entity_id"]=c.Target,["condition_revision"]=c.ConditionVersion };
            bool success=outcome.Status==PlanExecutionStatus.Succeeded;
            if(success && !ticket.MatchesWorldReceipt(outcome,drink?(DrinkReceipt?)d:null,clean?(CounterCleaningReceipt?)c:null) ||
                !success && receipt!=null || outcome.Status==PlanExecutionStatus.TimedOut)
            { Unknown();return; }
            stage="settling";LastStatus="settlement_pending";
            ObserveExecution(success?"succeeded":outcome.Status==PlanExecutionStatus.Cancelled?"cancelled":"failed",receiptPresent:receipt!=null);
            Send(Control("settle_execution",Evidence(success?"succeeded":outcome.Status==PlanExecutionStatus.Cancelled?"cancelled":"failed",
                success?"committed":"not_committed",success?null:string.IsNullOrEmpty(outcome.FailureCode)?"TASK_ACTION_FAILED":outcome.FailureCode,outcome,receipt)));
        }
        private void OnTaskMutation(CognitiveResponse value)
        {
            var receipt=value.task_receipt;
            if(stage=="executing" && receipt?.committed==true && receipt.task_id==ticket.task_id && receipt.operation=="cancel" &&
                receipt.agent_entity_id==ticket.agent_entity_id && receipt.task_version>=ticket.task_version &&
                receipt.visit_id==ticket.visit_id && receipt.scene_instance_id==ticket.scene_instance_id &&
                receipt.owner.Matches(request.recall_identity.observer) && receipt.player.Matches(request.recall_identity.current_subjects
                    .FirstOrDefault(p=>p.kind=="player" && p.entity_id==request.context.relevant_player.player_entity_id)))
                runner.InterruptCurrentPlan("TASK_CANCEL_REQUESTED");
        }
        private void OnWorldStarted(TaskFeedbackSnapshot value)
        {
            if(stage=="executing" && value!=null && value.Key.Revision==0 && Owns(value.Key.Proposal,value.Key.Trace))
                ObserveExecution("executing");
        }
        private void ObserveExecution(string state,string status=null,string phase=null,string error=null,bool permit=false,bool? receiptPresent=null)
        {
            try {if(ticket==null)return;
                var data=new JObject { ["task_state"]=state,["request_id"]=ticket.request_id,["run_id"]=ticket.run_id,["trace_id"]=ticket.trace_id,
                    ["task_id"]=ticket.task_id,["attempt_id"]=ticket.attempt_id,["proposal_id"]=ticket.execution_request.proposal_id,
                    ["agent_id"]=ticket.agent_entity_id,["visit_id"]=ticket.visit_id,["scene_instance_id"]=ticket.scene_instance_id,
                    ["task_status"]=status,["phase"]=phase,["error_code"]=error,["start_permitted"]=permit };
                if(receiptPresent.HasValue)data["receipt_present"]=receiptPresent.Value;
                EmbodiedNPC.Settings.DiagnosticsExport.RecordTask(state=="executing" || state=="permitted"?"execution":"result",data);
            } catch(Exception) { }
        }
        private void Update()
        {
            if(stage=="awaiting_begin" && Time.unscaledTimeAsDouble>=deadline) { NotStarted();return; }
            if(stage=="executing" && !LiveGuard()) runner.InterruptCurrentPlan("TASK_GUARD_EXPIRED");
            // Only evidence retries. Begin is never resent, including after reconnect.
            if(runtime==null && stage=="settling" && pendingControl!=null && backend.IsConnected && Time.unscaledTimeAsDouble>=retryAt)
            { retryAt=Time.unscaledTimeAsDouble+3;_=backend.SendTaskExecutionControlAsync(pendingControl); }
        }
        private void OnDisable()
        {
            if(stage=="executing") { runner?.InterruptCurrentPlan("TASK_RUNTIME_DISABLED");if(stage=="executing") Unknown(); }
            else if(stage=="awaiting_begin" || stage=="permitted") NotStarted();
            if(backend!=null) { backend.TaskExecutionResultReceived-=OnResult;backend.CognitiveResponseReceived-=OnTaskMutation; }
            if(runner!=null) {runner.GoalOutcomePublished-=OnOutcome;runner.TaskStarted-=OnWorldStarted;}
        }
    }
}
