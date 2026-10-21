using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class TaskDiagnosticsTests
    {
        private const string Key="TaskDiagnosticsTests.";
        private static string Evidence
        {
            get {var path=Environment.GetEnvironmentVariable("EMBODIEDNPC_TASK_DIAGNOSTICS_TEST_ROOT");
                Assert.That(!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Application.dataPath)),Is.True,"Private external test root required.");
                Directory.CreateDirectory(path);return path;}
        }
        private static void ResetLogBudget()=>typeof(DiagnosticsExport).GetMethod("ResetTaskDiagnostics",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        [Test] public void ReleaseProjectionIsTypedHashedAndBounded()
        {
            ResetLogBudget();
            var raw=new JObject { ["task_diagnostic_version"]=1,["task_stage"]="decision",["task_state"]="validated",
                ["request_id"]="abc",["future_commitment"]="false",["operation"]="none",["receipt_committed"]="true",
                ["utterance"]="PRIVATE_DIALOGUE",["prompt"]="PRIVATE_PROMPT",["opportunity_token"]="REUSABLE_TOKEN",["task_count"]=1000 };
            var safe=DiagnosticsExport.ProjectTask(raw);
            var malformed=(JObject)raw.DeepClone();malformed["task_diagnostic_version"]=long.MaxValue;
            Assert.That(DiagnosticsExport.ProjectTask(malformed),Is.Null);
            Assert.That((string)safe["request_id"],Is.EqualTo("h_ba7816bf8f01cfea414140de"));
            Assert.That(safe["receipt_committed"],Is.Null);Assert.That((int)safe["task_count"],Is.EqualTo(11));
            foreach(string secret in new[]{"PRIVATE_DIALOGUE","PRIVATE_PROMPT","REUSABLE_TOKEN"})Assert.That(safe.ToString(),Does.Not.Contain(secret));
            var projected=DiagnosticsExport.ProjectLine("TASK_DIAGNOSTIC "+safe.ToString(Newtonsoft.Json.Formatting.None));
            Assert.That(JToken.DeepEquals(projected["task_diagnostic"],safe),Is.True);
            Assert.That(JToken.DeepEquals(DiagnosticsExport.ProjectLine(projected.ToString())["task_diagnostic"],safe),Is.True,"Export reprojects safely without double-hashing IDs.");
            var records=new List<JObject>();Action<JObject> receive=x=>records.Add(x);DiagnosticsExport.TaskDiagnosticRecorded+=receive;
            try {typeof(DiagnosticsExport).GetField("taskRecords",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,512);
                for(int i=0;i<5;i++)DiagnosticsExport.RecordTask("protocol",new JObject { ["task_state"]="entered" });
                Assert.That(records.Count,Is.EqualTo(1));Assert.That((string)records[0]["task_state"],Is.EqualTo("limit_reached"));}
            finally {DiagnosticsExport.TaskDiagnosticRecorded-=receive;ResetLogBudget();}
            File.WriteAllText(Path.Combine(Evidence,"projection.json"),projected.ToString());
        }

        [Test] public void SourceLogsActualNoActionAndDeduplicatesStableGates()
        {
            ResetLogBudget();var rows=new List<JObject>();Action<JObject> receive=x=>rows.Add(x);DiagnosticsExport.TaskDiagnosticRecorded+=receive;
            try {using var f=new TaskConsiderationFixture(path:Path.Combine(Evidence,"no-action-private"));
                f.Visible(true);f.Tick();f.Tick(); // First capture makes runtime validity observable on the next gate sample.
                int stable=rows.Count;for(int i=0;i<20;i++)f.Tick();Assert.That(rows.Count,Is.EqualTo(stable));
                Assert.That(f.Backend.ProcessIncomingJson(f.Snapshot().ToString()),Is.True);
                Assert.That(f.Backend.SendCount,Is.EqualTo(1));Assert.That(f.Driver.TryConsumeResponse(f.NoAction()),Is.True);
                Assert.That(f.Driver.FlushTaskConsideration(),Is.True);
                Assert.That(rows.Any(x=>(string)x["task_stage"]=="visit" && (bool?)x["runtime_valid"]==true),Is.True);
                Assert.That(rows.Any(x=>(string)x["task_state"]=="considering"),Is.True);
                Assert.That(rows.Any(x=>(string)x["decision_kind"]=="NO_ACTION"),Is.True);
                Assert.That(rows.Any(x=>(string)x["task_state"]=="executing"),Is.False);
                Assert.That(f.Backend.ConnectionAttempts,Is.Zero);
                File.WriteAllText(Path.Combine(Evidence,"no-action.json"),new JArray(rows).ToString());}
            finally {DiagnosticsExport.TaskDiagnosticRecorded-=receive;}
        }

        [UnityTest] public IEnumerator ActualRunnerReceiptIsDistinctFromProposalAndPermit()
        {
            Assert.That(Application.isPlaying,Is.False);Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty,Is.False);
            SessionState.SetString(Key+"scene",UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            SessionState.SetString(Key+"oldEnv",Environment.GetEnvironmentVariable("EMBODIEDNPC_TASK_UI_ACCEPTANCE_ROOT")??"");
            SessionState.SetBool(Key+"restore",true);
            Environment.SetEnvironmentVariable("EMBODIEDNPC_TASK_UI_ACCEPTANCE_ROOT",null);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            yield return new EnterPlayMode();
            yield return VerifyWorldExecution();
        }
        private IEnumerator VerifyWorldExecution()
        {
            // SubsystemRegistration resets the budget across the Play domain reload.
            var rows=new List<JObject>();Action<JObject> receive=x=>rows.Add(x);DiagnosticsExport.TaskDiagnosticRecorded+=receive;
            try
            {
                using var f=new TaskExecutionWorldFixture(Path.Combine(Evidence,"world-private"));
                f.Base.Player.transform.position=new Vector3(.97f,0,3.4f);
                f.Base.Visible(true);f.Base.Tick();Assert.That(f.Base.Backend.ProcessIncomingJson(f.Base.Snapshot().ToString()),Is.True);
                var response=TaskExecutionTests.Proposal(f.Base);var ticket=response.task_execution;
                Assert.That(f.Base.Driver.TryConsumeResponse(response),Is.True);Assert.That(f.Base.Driver.FlushTaskConsideration(),Is.True);
                Assert.That(f.World.GetDrinkCount(f.Base.Player.EntityId,"latte"),Is.Zero);
                Assert.That(rows.Any(x=>(string)x["task_state"]=="executing"),Is.False);
                var begin=f.Base.Backend.ExecutionControls.Single(x=>(string)x["operation"]=="begin_execution");
                var ack=new JObject { ["message_type"]="task_execution_result",["schema_version"]="1.1",["operation"]="begin_execution",
                    ["ticket"]=ticket.Wire,["accepted"]=true,["start_permitted"]=true,["task_status"]="executing",["task_version"]=ticket.task_version+1,["phase"]="started" };
                Assert.That(f.Base.Backend.ProcessIncomingJson(ack.ToString()),Is.True);
                double deadline=Time.realtimeSinceStartupAsDouble+25;
                while(f.World.GetDrinkCount(f.Base.Player.EntityId,"latte")==0 && Time.realtimeSinceStartupAsDouble<deadline)
                {f.Base.Visible(true);yield return null;}
                Assert.That(f.World.GetDrinkCount(f.Base.Player.EntityId,"latte"),Is.EqualTo(1));
                var settle=f.Base.Backend.ExecutionControls.Single(x=>(string)x["operation"]=="settle_execution");
                Assert.That((string)settle["result"]?["receipt"]?["kind"],Is.EqualTo("drink_prepared"));
                Assert.That(rows.Any(x=>(string)x["task_state"]=="executing"),Is.True,"Only the actual Runner TaskStarted proves world execution began.");
                Assert.That(rows.Any(x=>(string)x["task_state"]=="succeeded" && (bool?)x["receipt_present"]==true),Is.True);
                ack["operation"]="settle_execution";ack["start_permitted"]=false;ack["task_status"]="completed";ack["task_version"]=ticket.task_version+2;ack["phase"]="succeeded";
                Assert.That(f.Base.Backend.ProcessIncomingJson(ack.ToString()),Is.True);
                Assert.That(rows.Any(x=>(string)x["task_status"]=="completed" && (string)x["phase"]=="succeeded"),Is.True);
                Assert.That(f.Base.Backend.ConnectionAttempts,Is.Zero);
                File.WriteAllText(Path.Combine(Evidence,"world.json"),new JObject { ["actual_drinks"]=1,["real_drink_receipt"]=true,["backend_ack_is_fake"]=true,["diagnostics"]=new JArray(rows) }.ToString());
            }
            finally {DiagnosticsExport.TaskDiagnosticRecorded-=receive;}
        }
        [UnityTearDown] public IEnumerator Restore()
        {
            if(Application.isPlaying)yield return new ExitPlayMode();
            if(SessionState.GetBool(Key+"restore",false))
            {
                SessionState.SetBool(Key+"restore",false);
                Environment.SetEnvironmentVariable("EMBODIEDNPC_TASK_UI_ACCEPTANCE_ROOT",SessionState.GetString(Key+"oldEnv","") is string old && old.Length>0?old:null);
                string scene=SessionState.GetString(Key+"scene","");if(scene.Length>0)EditorSceneManager.OpenScene(scene);
            }
        }
    }
}
