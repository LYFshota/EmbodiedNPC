using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using EmbodiedNPC.Settings;

namespace CafeDemo.Tests
{
    public sealed class TaskReleaseProjectionTests
    {
        [Test] public void TaskFieldsSurviveActualTechnicalOnlyWriterAndSafeExport()
        {
            string root=Environment.GetEnvironmentVariable("EMBODIEDNPC_TASK_DIAGNOSTICS_TEST_ROOT");
            Assert.That(!string.IsNullOrWhiteSpace(root) && Path.IsPathRooted(root),Is.True,"External private fixture required.");
            string path=Path.Combine(root,"release-trace.jsonl");Directory.CreateDirectory(root);
            var data=new JObject { ["task_diagnostic"]=new JObject { ["task_diagnostic_version"]=1,["task_stage"]="decision",
                ["task_state"]="validated",["decision_kind"]="NO_ACTION",["future_commitment"]="na",["task_id"]="synthetic_task",
                ["utterance"]="PRIVATE_DIALOGUE",["prompt"]="PRIVATE_PROMPT",["opportunity_token"]="REUSABLE_TOKEN" } };
            using(var writer=new RuntimeTraceJsonlWriter(path,"synthetic_session","CafeDemoV2",true,4096))
                Assert.That(writer.TryWrite("task","task_diagnostic",null,null,null,null,null,null,data,DateTime.UtcNow,1),Is.True);
            string text=File.ReadAllText(path);var parsed=JObject.Parse(text);
            Assert.That((string)parsed["data"]?["task_diagnostic"]?["decision_kind"],Is.EqualTo("NO_ACTION"));
            foreach(string secret in new[]{"PRIVATE_DIALOGUE","PRIVATE_PROMPT","REUSABLE_TOKEN","synthetic_task"})Assert.That(text,Does.Not.Contain(secret));
            var exported=DiagnosticsExport.ProjectLine(parsed["data"].ToString());
            Assert.That(exported?["task_diagnostic"]?["task_id"],Is.Not.Null);
            using(var writer=new RuntimeTraceJsonlWriter(Path.Combine(root,"limited-trace.jsonl"),"synthetic","CafeDemoV2",true,128))
            {for(int i=0;i<20;i++)writer.TryWrite("task","task_diagnostic",null,null,null,null,null,null,data,DateTime.UtcNow,1);
                Assert.That(writer.LastError,Is.EqualTo("TRACE_SIZE_LIMIT"));}
            string exportRoot=Path.Combine(root,"export-private");Directory.CreateDirectory(Path.Combine(exportRoot,"logs"));
            File.WriteAllText(Path.Combine(exportRoot,"logs","Player.log"),"TASK_DIAGNOSTIC "+parsed["data"]?["task_diagnostic"]?.ToString(Newtonsoft.Json.Formatting.None)+"\nPRIVATE_DIALOGUE\n");
            using var archive=ZipFile.OpenRead(DiagnosticsExport.Export(exportRoot));
            using var reader=new StreamReader(archive.Entries.Single(x=>x.Name.StartsWith("technical-")).Open());
            string exportedText=reader.ReadToEnd();Assert.That(exportedText,Does.Contain("NO_ACTION"));
            Assert.That(exportedText,Does.Not.Contain("PRIVATE_DIALOGUE"));
        }
        [Test] public void RecorderSubscriptionUsesExistingWriterAndUnsubscribes()
        {
            string root=Environment.GetEnvironmentVariable("EMBODIEDNPC_TASK_DIAGNOSTICS_TEST_ROOT");
            Assert.That(!string.IsNullOrWhiteSpace(root) && Path.IsPathRooted(root),Is.True);
            var obj=new UnityEngine.GameObject("isolated-task-trace");obj.SetActive(false);
            var recorder=obj.AddComponent<RuntimeTraceRecorder>();
            typeof(RuntimeTraceRecorder).GetField("outputDirectoryOverride",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(recorder,Path.Combine(root,"recorder-private"));
            string path=null;
            try
            {
                obj.SetActive(true);
                typeof(RuntimeTraceRecorder).GetMethod("StartRecording",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(recorder,null);
                recorder.RefreshTraceSubscriptions();recorder.RefreshTraceSubscriptions();path=recorder.CurrentTracePath;
                DiagnosticsExport.RecordTask("decision",new JObject { ["task_state"]="validated",["decision_kind"]="NO_ACTION" });
            }
            finally {typeof(RuntimeTraceRecorder).GetMethod("OnDisable",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(recorder,null);UnityEngine.Object.DestroyImmediate(obj);}
            Assert.That(File.ReadAllLines(path).Count(x=>x.Contains("task_diagnostic_version")),Is.EqualTo(1));
            long length=new FileInfo(path).Length;
            DiagnosticsExport.RecordTask("decision",new JObject { ["task_state"]="validated",["decision_kind"]="NO_ACTION" });
            Assert.That(new FileInfo(path).Length,Is.EqualTo(length));
        }
    }
}
