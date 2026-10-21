using System;
using System.IO;
using System.Reflection;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CafeDemo.Tests
{
    public sealed class KnowledgeAdminSessionPathTests
    {
        private string root, previousFile, previousUrl;
        private const string Url="http://127.0.0.1:18881";
        [SetUp] public void Setup()
        {
            root=Path.Combine(Path.GetTempPath(),"KnowledgeAuth-"+Guid.NewGuid().ToString("N"),"安装 path");Directory.CreateDirectory(root);
            previousFile=Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE");previousUrl=Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL");
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",null);Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL",null);
        }
        [TearDown] public void Cleanup()
        {
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",previousFile);Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL",previousUrl);
            Directory.Delete(Directory.GetParent(root).FullName,true); // This test's GUID-owned temporary directory only.
        }
        private static string Default(string path,bool editor)=>(string)typeof(KnowledgeManagementClient).GetMethod("DefaultSessionPath",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{path,editor});
        private string Write(string url=Url,char token='a')
        {
            string path=Path.Combine(root,"Backend/data/knowledge_admin_session.json");Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,new JObject{["schema_version"]=1,["base_url"]=url,["token"]=new string(token,43)}.ToString());return Path.GetFullPath(path);
        }
        private static (bool valid,string token,string error) Read(KnowledgeManagementClient client)
        {object[] args={null,null,null};bool valid=(bool)typeof(KnowledgeManagementClient).GetMethod("Session",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(client,args);return(valid,(string)args[1],(string)args[2]);}
        [Test] public void EditorKeepsDevelopmentSessionPath()
        {
            Assert.AreEqual(Path.GetFullPath(Path.Combine(root,"UnityClient/Assets/../../AIBackend/data/knowledge_admin_session.json")),Default(Path.Combine(root,"UnityClient/Assets"),true));
        }
        [Test] public void EveryRequestRereadsReplacedSession()
        {
            string path=Write();var client=new KnowledgeManagementClient(()=>Url,path);var before=Read(client);Assert.IsTrue(before.valid);
            Write(token:'b');var after=Read(client);Assert.IsTrue(after.valid);Assert.IsFalse(string.Equals(before.token,after.token));
        }
        [Test] public void EnvironmentOverrideIsAuthoritative()
        {
            string path=Write();Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",path);
            Assert.IsTrue(Read(new KnowledgeManagementClient(()=>Url)).valid);
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_MISSING",Read(new KnowledgeManagementClient(()=>Url,Path.Combine(root,"missing"))).error);
        }
        [Test] public void MissingMalformedAndLockedFilesHaveDistinctErrors()
        {
            string path=Write();File.WriteAllText(path,"invalid-json");var client=new KnowledgeManagementClient(()=>Url,path);
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_INVALID",Read(client).error);
            File.WriteAllText(path,new JObject{["schema_version"]="not-a-version",["base_url"]=Url,["token"]=new string('a',43)}.ToString());
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_INVALID",Read(client).error);Write();
            using(var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.AreEqual("LOCAL_DEVELOPER_SESSION_UNREADABLE",Read(client).error);
            File.Delete(path);Assert.AreEqual("LOCAL_DEVELOPER_SESSION_MISSING",Read(client).error);
        }
        [Test] public void WrongPortAndInheritedUrlCannotRedirectCapability()
        {
            string path=Write("http://127.0.0.1:18882");var client=new KnowledgeManagementClient(()=>Url,path);
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_MISMATCH",Read(client).error);
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL","http://127.0.0.1:18882");
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_MISMATCH",Read(client).error);
        }
        [Test] public void RemoteAddressAndInvalidTokenAreRejected()
        {
            string path=Write();Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL","http://example.com:18881");
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_URL_INVALID",Read(new KnowledgeManagementClient(()=>Url,path)).error);
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL",null);File.WriteAllText(path,new JObject{["schema_version"]=1,["base_url"]=Url,["token"]="short"}.ToString());
            Assert.AreEqual("LOCAL_DEVELOPER_SESSION_INVALID",Read(new KnowledgeManagementClient(()=>Url,path)).error);
        }
    }
}
