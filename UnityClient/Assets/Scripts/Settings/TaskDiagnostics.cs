using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EmbodiedNPC.Settings
{
    public static partial class DiagnosticsExport
    {
        private static readonly HashSet<string> TaskStages=new("protocol decision read persistence visit gate sync opportunity consideration admission execution result error limit".Split(' '));
        private static readonly HashSet<string> TaskStates=new(("entered prepared validated attempted committed failed recovered snapshot_received requested sent send_failed rejected " +
            "established not_ready admitted not_admitted runtime_reconfigured source_disabled transport_changed player_session merged_into_player_run syncing sync_timeout sync_expired no_eligible_tasks offered considering considered " +
            "condition_expired route_failed response_rejected execution_handoff not_started not_executed cancelled reserved awaiting_begin permitted executing settlement_pending settled effect_unknown settlement_rejected gate_changed response_received succeeded completed blocked uncertain timed_out limit_reached sync_send_failed unknown").Split(' '));
        private static readonly HashSet<string> TaskIds=new("request_id trace_id run_id event_id command_id visit_id scene_instance_id task_id attempt_id proposal_id agent_id".Split(' '));
        private static readonly HashSet<string> TaskBools=new(("task_supported persistence_attempted receipt_committed runtime_valid bridge_initialized transport_connected player_visible counter_opportunity dialogue_visible run_busy goal_busy returning engagement_held routine_blocked gate_open offer_tasks is_idle opportunity_offered start_permitted accepted receipt_present").Split(' '));
        private static readonly Dictionary<string,HashSet<string>> TaskEnums=new()
        {
            ["task_stage"]=TaskStages,["task_state"]=TaskStates,
            ["source"]=new("backend unity".Split(' ')),
            ["operation"]=new("none create update edit defer cancel reserve begin settle not_started unknown".Split(' ')),
            ["decision_kind"]=new("DIRECT_RESPONSE NO_ACTION ACTION_PROPOSAL TASK_READ MEMORY_RECALL DAILY_MODE_INTENT AUTONOMOUS_ACTION_INTENT".Split(' ')),
            ["future_commitment"]=new("true false na".Split(' ')),
            ["task_status"]=new("waiting executing blocked completed cancelled".Split(' ')),
            ["receipt_outcome"]=new("created updated deduplicated recovered cancelled cancellation_requested failed".Split(' ')),
            ["phase"]=new("reserved started succeeded failed cancelled uncertain timed_out".Split(' ')),
            ["admission_result"]=new("action_not_eligible action_ownership_unavailable mode_prevalidation_failed action_prevalidation_failed mode_commit_failed action_wait_failed action_handoff_failed action_accepted".Split(' ')),
            ["error_code"]=new(("SCHEMA_ERROR MODEL_ERROR TASK_REJECTED STORE_ERROR OPERATION_REJECTED TASK_NOT_SAVED TASK_CONSIDERATION_STALE TASK_RUNTIME_CONFLICT TASK_RUNTIME_SYNC_FAILED TASK_EXECUTION_REJECTED TASK_EXECUTION_STORE_UNAVAILABLE TASK_NOT_STARTED TASK_START_REJECTED TASK_EXECUTION_TICKET_INVALID TASK_SNAPSHOT_REJECTED TASK_RUNTIME_NOT_READY TASK_OUTCOME_MISMATCH COGNITIVE_REQUEST_FAILED COGNITIVE_ADMISSION_REJECTED INVALID_ACTION_PROPOSAL SEND_FAILED CLIENT_TIMEOUT unknown").Split(' '))
        };
        private static readonly object TaskSync=new();
        private static int taskRecords,taskBytes;
        private static bool taskLimited;
        public static event Action<JObject> TaskDiagnosticRecorded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetTaskDiagnostics() {taskRecords=taskBytes=0;taskLimited=false;}

        private static string TaskCorrelation(string value)
        {
            if(string.IsNullOrEmpty(value) || value.Length>512) return null;
            if(Regex.IsMatch(value,"^h_[a-f0-9]{24}$")) return value;
            using var sha=SHA256.Create();
            return "h_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant().Substring(0,24);
        }
        public static JObject ProjectTask(JObject data)
        {
            try
            {
            if(data==null || data["task_diagnostic_version"]?.Type!=JTokenType.Integer || (int)data["task_diagnostic_version"]!=1 ||
                data["task_stage"]?.Type!=JTokenType.String || !TaskStages.Contains((string)data["task_stage"])) return null;
            var result=new JObject { ["task_diagnostic_version"]=1 };
            foreach(var field in data.Properties())
            {
                if(TaskIds.Contains(field.Name) && field.Value.Type==JTokenType.String)
                {var id=TaskCorrelation((string)field.Value);if(id!=null)result[field.Name]=id;}
                else if(TaskBools.Contains(field.Name) && field.Value.Type==JTokenType.Boolean) result[field.Name]=field.Value.DeepClone();
                else if(TaskEnums.TryGetValue(field.Name,out var values) && field.Value.Type==JTokenType.String && values.Contains((string)field.Value)) result[field.Name]=field.Value.DeepClone();
                else if((field.Name=="task_count" || field.Name=="eligible_count" || field.Name=="waiting_count") && field.Value.Type==JTokenType.Integer)
                    result[field.Name]=(int)Math.Max(0,Math.Min(11,(long)field.Value));
                else if(field.Name=="process_id" && field.Value.Type==JTokenType.Integer && (long)field.Value>0 && (long)field.Value<=int.MaxValue) result[field.Name]=field.Value.DeepClone();
                else if(field.Name=="task_ids" && field.Value is JArray ids)
                    result[field.Name]=new JArray(ids.Take(3).Where(x=>x.Type==JTokenType.String).Select(x=>TaskCorrelation((string)x)).Where(x=>x!=null));
            }
            return result;
            }
            catch (Exception) { return null; } // Malformed log input cannot interrupt safe export.
        }
        public static void RecordTask(string stage,JObject fields)
        {
            try
            {
                var data=(JObject)(fields?.DeepClone()??new JObject());
                data["task_diagnostic_version"]=1;data["task_stage"]=stage;data["source"]="unity";data["process_id"]=Process.GetCurrentProcess().Id;
                var projected=ProjectTask(data);if(projected==null)return;
                string text=projected.ToString(Formatting.None);
                lock(TaskSync)
                {
                    if(taskRecords>=512 || taskBytes+Encoding.UTF8.GetByteCount(text)>256*1024)
                    {
                        if(taskLimited)return;taskLimited=true;
                        projected=new JObject { ["task_diagnostic_version"]=1,["source"]="unity",["process_id"]=Process.GetCurrentProcess().Id,["task_stage"]="limit",["task_state"]="limit_reached" };
                        text=projected.ToString(Formatting.None);
                    }
                    else {taskRecords++;taskBytes+=Encoding.UTF8.GetByteCount(text);}
                }
                UnityEngine.Debug.Log("TASK_DIAGNOSTIC "+text);
                foreach(var listener in TaskDiagnosticRecorded?.GetInvocationList()??Array.Empty<Delegate>())
                    try {((Action<JObject>)listener)((JObject)projected.DeepClone());} catch(Exception) { }
            }
            catch(Exception) { } // Observation never changes the task pipeline.
        }
    }
}
