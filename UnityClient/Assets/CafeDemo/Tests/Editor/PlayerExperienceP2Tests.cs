using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CafeDemo.Tests
{
    public sealed class PlayerExperienceP2Tests
    {
        [Test] public void ProjectionKeepsUtcErrorsAndCorrelationButNeverRawSecretsOrChat()
        {
            var value=DiagnosticsExport.ProjectLine("2026-10-17T00:01:02+08:00 pid=42 HTTP 503 COGNITIVE_REQUEST_FAILED request_id=123456781234123412341234567890ab");
            Assert.AreEqual("2026-10-16T16:01:02.000Z",(string)value["utc"]);
            Assert.AreEqual(503,(int)value["http_status"]); Assert.AreEqual(42,(int)value["pid"]);
            Assert.AreEqual("123456781234123412341234567890ab",(string)value["request_id"]);
            Assert.IsNull(DiagnosticsExport.ProjectLine("Authorization: Bearer synthetic-private-token COGNITIVE_REQUEST_FAILED"));
            Assert.IsNull(DiagnosticsExport.ProjectLine("https://example.invalid/?key=synthetic-private-key HTTP 403"));
            Assert.IsNull(DiagnosticsExport.ProjectLine("api_key=synthetic-private-key TimeoutException"));
            Assert.IsNull(DiagnosticsExport.ProjectLine("私人聊天：我的秘密，请保密。"));
            Assert.IsNull(DiagnosticsExport.ProjectLine("unknown private line"));
            var old=DiagnosticsExport.ProjectLine("TimeoutException"); Assert.IsNull(old["utc"]);
            var stack=DiagnosticsExport.ProjectLine("EmbodiedNPC.Communication.BackendWebSocketClient:Send (private payload)");
            Assert.IsFalse(stack.ToString().Contains("private payload"));
            Assert.IsNotEmpty((JArray)stack["frames"]);
            var prefixed=DiagnosticsExport.ProjectLine("TIMEOUT scene=CafeDemoV2 request_id=cognitive-request-123456781234123412341234567890ab");
            Assert.AreEqual("cognitive-request-123456781234123412341234567890ab",(string)prefixed["request_id"]);
            Assert.AreEqual("CafeDemoV2",(string)prefixed["scene"]);
        }

        [Test] public void OfflineExportIncludesOnlyBoundedTechnicalLogProjection()
        {
            var root=Path.Combine("D:/EmbodiedNPC-PlayerExperienceP2-20261017/evidence", "export-fixture-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root,"logs"));
            File.WriteAllText(Path.Combine(root,"model-config.json"),"synthetic-private-key");
            File.WriteAllText(Path.Combine(root,"memory.sqlite3"),"synthetic-private-memory");
            File.WriteAllText(Path.Combine(root,"logs","Player.log"),
                "2026-10-17T01:00:00Z COGNITIVE_REQUEST_FAILED HTTP 503 request_id=123456781234123412341234567890ab\n"+
                "NullReferenceException: private-message\nEmbodiedNPC.Communication.BackendWebSocketClient:Send (private payload)\n"+
                "Authorization: Bearer synthetic-private-token\n原始聊天不要导出\n");
            File.WriteAllText(Path.Combine(root,"logs","unknown.log"),"synthetic-private-log");
            DiagnosticsExport.ObserveModelStatus(new ModelConfigView{revision=7,main_model=new MainModelConfig{
                provider_type="openai_compatible",model="synthetic-model",api_key="synthetic-private-config-key"}});
            using(var zip=ZipFile.OpenRead(DiagnosticsExport.Export(root)))
            {
                Assert.AreEqual(2,zip.Entries.Count);
                var all=string.Join("\n",zip.Entries.Select(e=>{using(var r=new StreamReader(e.Open()))return r.ReadToEnd();}));
                Assert.IsTrue(all.Contains("COGNITIVE_REQUEST_FAILED")); Assert.IsTrue(all.Contains("NullReferenceException"));
                Assert.IsTrue(all.Contains("BackendWebSocketClient")); Assert.IsTrue(all.Contains("503"));
                Assert.IsFalse(all.Contains("synthetic-private")); Assert.IsFalse(all.Contains("原始聊天"));
                Assert.IsFalse(all.Contains("private-message")); Assert.IsFalse(all.Contains("private payload"));
                Assert.IsTrue(all.Contains("Missing timestamps remain unknown"));
                Assert.IsTrue(all.Contains("synthetic-model"));
            }
        }
    }
}
