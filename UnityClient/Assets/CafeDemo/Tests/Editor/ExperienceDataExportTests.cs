using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CafeDemo.Tests
{
    public sealed class ExperienceDataExportTests
    {
        private string root;
        [SetUp] public void Setup() { root=Path.Combine(Path.GetTempPath(),"ExperienceExportTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root); }
        [TearDown] public void Cleanup() { if(Directory.Exists(root))Directory.Delete(root,true); }
        private static JObject Local() => new() {
            ["npc_personas"]=new JObject { ["characters"]=new JArray(new JObject{["character_id"]="npc-a"}),["overrides_readable"]=true },
            ["player_profile"]=new JObject { ["identity"]=new JObject{["PlayerUid"]="player-a"},["avatar_metadata_available"]=true },
            ["runtime"]=new JObject{["version"]="test"} };
        private string Backend(string player="player-a")
        {
            var path=Path.Combine(root,Guid.NewGuid()+".zip");
            using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            void Write(string name,string content) { using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(content); }
            var sections=new JObject();
            foreach(string name in new[]{"world_knowledge","experiences","conversations","episodes","memories","relationships","tasks","actions","persona_language_projections"})
            {
                sections[name]=new JObject{["status"]="ok",["count"]=name=="conversations"?126:0};
                if(name!="conversations")Write(name+(name=="experiences"?".jsonl":".json"),name=="experiences"?"":"[]");
            }
            Write("backend_manifest.json",new JObject{["player_identity"]=new JObject{["persistent_id"]=player},["sections"]=sections}.ToString());
            Write("conversations.jsonl",string.Join("\n",Enumerable.Range(0,126).Select(i=>new JObject{["text"]="完整原文"+i}.ToString(Newtonsoft.Json.Formatting.None))));
            Write("conversations.md","# 对话\n原文保留");
            Write("secrets.json","SHOULD_NEVER_APPEAR");Write("../escape.txt","SHOULD_NEVER_APPEAR");
            return path;
        }
        private string Diagnostics()
        {
            var path=Path.Combine(root,"diagnostics-"+Guid.NewGuid()+".zip");
            using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open());writer.Write("{\"policy\":\"safe\"}");
            return path;
        }
        private static JObject Read(ZipArchive zip,string name) { using var reader=new StreamReader(zip.GetEntry(name).Open());return JObject.Parse(reader.ReadToEnd()); }

        [Test] public void PersonaDefaultsOverridesAndEffectiveAreSeparateAndReadOnly()
        {
            var d=ScriptableObject.CreateInstance<CharacterDefinition>();
            try
            {
                d.Configure("npc-a","Default","Role","Default history","Quiet","Gentle","Boundaries");
                var store=new CharacterOverrideStore(Path.Combine(root,"personas.json"));
                store.Save("npc-a","Custom","Role","Custom history","Cheerful","Warm","Boundaries");
                string before=File.ReadAllText(store.FilePath);
                var result=ExperienceDataExport.Personas(new[]{d},store);
                Assert.That((string)result["characters"][0]["default"]["backstory"],Is.EqualTo("Default history"));
                Assert.That((string)result["characters"][0]["override"]["backstory"],Is.EqualTo("Custom history"));
                Assert.That((string)result["characters"][0]["effective"]["personality"],Is.EqualTo("Cheerful"));
                Assert.That(File.ReadAllText(store.FilePath),Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(d); }
        }
        [Test] public void ZipMergesCompleteStructuredDataAndOnlySanitizedDiagnostics()
        {
            File.WriteAllText(Path.Combine(root,"model_api_configs.json"),"PRIVATE_API_KEY");
            var result=ExperienceDataExport.BuildArchive(root,Backend(),Local(),Diagnostics());
            Assert.That(result.incomplete,Is.Empty);
            using var zip=ZipFile.OpenRead(result.path);
            Assert.That(zip.GetEntry("conversations.jsonl"),Is.Not.Null);
            using(var reader=new StreamReader(zip.GetEntry("conversations.jsonl").Open()))Assert.That(reader.ReadToEnd().Split('\n').Length,Is.EqualTo(126));
            Assert.That((bool)Read(zip,"manifest.json")["complete"],Is.True);
            Assert.That(zip.GetEntry("npc_personas.json"),Is.Not.Null);
            Assert.That(zip.GetEntry("diagnostics/manifest.json"),Is.Not.Null);
            Assert.That(zip.Entries.Select(e=>e.FullName),Has.None.EqualTo("secrets.json"));
            foreach(var entry in zip.Entries)
            { using var reader=new StreamReader(entry.Open());Assert.That(reader.ReadToEnd(),Does.Not.Contain("PRIVATE_API_KEY").And.Not.Contain("SHOULD_NEVER_APPEAR")); }
        }
        [Test] public void BackendUnavailableStillExportsLocalDataAndReportsMissingSections()
        {
            var result=ExperienceDataExport.BuildArchive(root,null,Local(),Diagnostics(),"BACKEND_HTTP_0");
            Assert.That(result.incomplete,Does.Contain("memories").And.Contain("world_knowledge").And.Contain("tasks"));
            using var zip=ZipFile.OpenRead(result.path);
            Assert.That((bool)Read(zip,"manifest.json")["complete"],Is.False);
            Assert.That(zip.GetEntry("npc_personas.json"),Is.Not.Null);
            Assert.That(zip.GetEntry("conversations.jsonl"),Is.Null);
        }
        [Test] public void NullStringNameErrorIsNotAnUnreadablePlayerProfile()
        {
            var local=Local();local["player_profile"]["name_read_error"]=new JValue((string)null);
            var result=ExperienceDataExport.BuildArchive(root,Backend(),local,Diagnostics());
            Assert.That(result.incomplete,Is.Empty);
        }
        [Test] public void MismatchedPlayerBackendPackageIsRejectedBeforeAnyPrivateEntryIsCopied()
        {
            var result=ExperienceDataExport.BuildArchive(root,Backend("foreign-player"),Local(),Diagnostics());
            using var zip=ZipFile.OpenRead(result.path);
            Assert.That(zip.GetEntry("conversations.jsonl"),Is.Null);
            Assert.That(result.incomplete,Does.Contain("conversations"));
        }
        [Test] public void CancellationNeverPublishesPartialZip()
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ExperienceDataExport.BuildArchive(root,Backend(),Local(),Diagnostics(),token:cancelled.Token));
            Assert.That(Directory.GetFiles(root,"EmbodiedNPC_Experience_*.zip"),Is.Empty);
            Assert.That(Directory.GetFiles(root,"*.partial"),Is.Empty);
        }
        [Test] public void DuplicateAndOversizedManifestPackagesAreUnavailableBeforePrivateCopy()
        {
            foreach(bool duplicate in new[]{true,false})
            {
                string source=Backend();
                using(var zip=ZipFile.Open(source,ZipArchiveMode.Update))
                {
                    using var writer=new StreamWriter(zip.CreateEntry(duplicate?"conversations.jsonl":"extra_manifest.json").Open());
                    writer.Write(duplicate?"PRIVATE_DUPLICATE":new string('x',1024*1024+1));
                }
                var result=ExperienceDataExport.BuildArchive(root,source,Local(),Diagnostics());
                Assert.That(result.incomplete,Does.Contain("conversations"));
                using var output=ZipFile.OpenRead(result.path);
                Assert.That(output.GetEntry("conversations.jsonl"),Is.Null);
            }
        }
        [Test] public void UnboundPlayerCannotCopyPrivateBackendRecords()
        {
            var local = Local(); local["player_profile"]["identity"] = JValue.CreateNull();
            var result = ExperienceDataExport.BuildArchive(root, Backend(), local, Diagnostics());
            Assert.That(result.incomplete, Does.Contain("player_profile").And.Contain("conversations"));
            using var zip = ZipFile.OpenRead(result.path);
            Assert.That(zip.GetEntry("conversations.jsonl"), Is.Null);
            Assert.That((bool)Read(zip, "manifest.json")["complete"], Is.False);
        }

        [Test] public void ExcessiveArchiveEntryCountIsRejected()
        {
            string source=Backend();
            using(var zip=ZipFile.Open(source,ZipArchiveMode.Update))
                for(int i=0;i<33;i++) { using var writer=new StreamWriter(zip.CreateEntry("extra"+i).Open());writer.Write("ignored"); }
            var result=ExperienceDataExport.BuildArchive(root,source,Local(),Diagnostics());
            Assert.That(result.incomplete,Does.Contain("conversations"));
            using var output=ZipFile.OpenRead(result.path);
            Assert.That(output.GetEntry("conversations.jsonl"),Is.Null);
        }
    }
}
