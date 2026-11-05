using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class ConnectionRecoveryTests
    {
        private const string Session="ConnectionRecovery.Tests.";
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static string Evidence=>Environment.GetEnvironmentVariable("EMBODIEDNPC_RECOVERY_TEST_EVIDENCE") ?? Path.GetFullPath(Path.Combine(Application.dataPath,"../Temp/ConnectionRecovery"));
        private Keyboard keyboard;
        private static T Find<T>() where T:Object=>Object.FindAnyObjectByType<T>();
        private static object Get(object target,string field)=>target.GetType().GetField(field,Private).GetValue(target);
        private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Private).SetValue(target,value);
        private static Button Button(string name)=>Object.FindObjectsByType<Button>(FindObjectsInactive.Include).Single(b=>b.name==name);
        private static JArray Writes
        {get{using(var file=new FileStream(Path.Combine(Evidence,"stats.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(file))return (JArray)JObject.Parse(reader.ReadToEnd())["writes"];}}
        private static void Size(int index)
        {
            if(index==5)
            {
                var group=SizeGroup();var type=group.GetType();
                index=SessionState.GetInt(Session+"temporarySize",-1);
                if(index<0)
                {
                    var assembly=typeof(EditorWindow).Assembly;var sizeType=assembly.GetType("UnityEditor.GameViewSize");
                    var size=Activator.CreateInstance(sizeType,Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),1280,720,"Hierarchy isolated 720p");
                    index=(int)type.GetMethod("GetTotalCount").Invoke(group,null);type.GetMethod("AddCustomSize").Invoke(group,new[]{size});SessionState.SetInt(Session+"temporarySize",index);
                }
            }
            var game=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");game.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(EditorWindow.GetWindow(game),index);
        }
        private static object SizeGroup()
        {var type=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameViewSizes");return type.GetMethod("GetGroup").Invoke(typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance").GetValue(null),new object[]{0});}
        [UnitySetUp] public IEnumerator Setup()
        {
            if(!File.Exists(Path.Combine(Evidence,"fake-ready.txt")))Assert.Fail("Start SettingsHierarchyFakeService.py with Temp/SettingsHierarchy first.");
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty,"Keep unsaved user scene work");
            SessionState.SetString(Session+"scene",SceneManager.GetActiveScene().path);
            foreach(var name in new[]{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE","EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"})
            {SessionState.SetBool(Session+name+"has",Environment.GetEnvironmentVariable(name)!=null);SessionState.SetString(Session+name,Environment.GetEnvironmentVariable(name)??"");}
            SessionState.SetBool(Session+"hadVolume",PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));SessionState.SetFloat(Session+"volume",PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session+"hadLanguage",PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));SessionState.SetInt(Session+"language",PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            var game=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            SessionState.SetInt(Session+"size",(int)game.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(EditorWindow.GetWindow(game)));Size(5);
            File.WriteAllText(Path.Combine(Evidence,"session.json"),new JObject{["schema_version"]=1,["pid"]=System.Diagnostics.Process.GetCurrentProcess().Id,["created_at"]=DateTime.UtcNow.ToString("o"),["base_url"]="http://127.0.0.1:18847",["token"]="hierarchy-fixture-token-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}.ToString());
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",Path.Combine(Evidence,"session.json"));
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL","http://127.0.0.1:18847");
            using(var reset=UnityWebRequest.Get("http://127.0.0.1:18847/fixture/reset"))yield return reset.SendWebRequest();
            string method=TestContext.CurrentContext.Test.MethodName;
            File.WriteAllText(Path.Combine(Evidence,"mode.txt"), method.Contains("Transient")?"transient":method.Contains("Auth")?"auth":method.Contains("Generation")?"generation":"tls");
            EditorSceneManager.OpenScene(method.Contains("Cafe")?"Assets/Scenes/CafeDemoV2.unity":"Assets/Scenes/TitleScene.unity");
            Set(Find<BackendWebSocketClient>(),"serverUrl","ws://127.0.0.1:18847/ws");
            foreach(var trace in Object.FindObjectsByType<RuntimeTraceRecorder>())trace.enabled=false;
            foreach(var driver in Object.FindObjectsByType<CognitiveRunDriver>())driver.enabled=false;
            yield return new EnterPlayMode();
            keyboard=InputSystem.AddDevice<Keyboard>();
            if(SceneManager.GetActiveScene().name=="CafeDemoV2")
            {
                // Every newly injected label/control uses a private font/atlas, preserving font WIP.
                var host=Find<CafeDemoDeveloperPanel>();var texts=((GameObject)Get(host,"panelRoot")).GetComponentsInChildren<TMP_Text>(true);
                var font=TMP_FontAsset.CreateFontAsset(texts[0].font.sourceFontFile);font.isMultiAtlasTexturesEnabled=true;
                foreach(var text in texts)text.font=font;
                var knowledge=(CafeWorldKnowledgePanel)Get(host,"knowledgeSettings");Set(Get(knowledge,"view"),"font",font);
                Set(Get(host,"npcSettings"),"store",new CharacterOverrideStore(Path.Combine(Evidence,"persona.json")));
                yield return Wait(()=>Find<DialogueController>().IsSetupReady);
            }
            else yield return null;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(keyboard!=null)InputSystem.RemoveDevice(keyboard);
            if(Application.isPlaying)
            {foreach(var backend in Object.FindObjectsByType<BackendWebSocketClient>()){var close=backend.DisconnectAsync();while(!close.IsCompleted)yield return null;}yield return new ExitPlayMode();}
            foreach(var name in new[]{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE","EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"})
                Environment.SetEnvironmentVariable(name,SessionState.GetBool(Session+name+"has",false)?SessionState.GetString(Session+name,""):null);
            if(SessionState.GetBool(Session+"hadVolume",false))PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey,SessionState.GetFloat(Session+"volume",0));else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            SettingsUiLanguage.SetEnglish(SessionState.GetInt(Session+"language",0)==1);if(!SessionState.GetBool(Session+"hadLanguage",false))PlayerPrefs.DeleteKey("EmbodiedNPC.Settings.Language");PlayerPrefs.Save();
            yield return null;var game=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");game.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(EditorWindow.GetWindow(game),SessionState.GetInt(Session+"size",0));
            int temporary=SessionState.GetInt(Session+"temporarySize",-1);if(temporary>=0){var group=SizeGroup();group.GetType().GetMethod("RemoveCustomSize").Invoke(group,new object[]{temporary});SessionState.EraseInt(Session+"temporarySize");}
            var previous=SessionState.GetString(Session+"scene","");EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous)?"Assets/Scenes/CafeDemoV2.unity":previous);
        }
        private static IEnumerator Wait(Func<bool> condition,float timeout=12)
        {float until=Time.realtimeSinceStartup+timeout;while(!condition()&&Time.realtimeSinceStartup<until)yield return null;Assert.IsTrue(condition(),"Settings hierarchy timeout");}
        private static void Mode(string mode)=>File.WriteAllText(Path.Combine(Evidence,"mode.txt"),mode);
        private static int HealthCalls
        {
            get
            {
                using(var file=new FileStream(Path.Combine(Evidence,"health-count.txt"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var reader=new StreamReader(file))
                    return int.TryParse(reader.ReadToEnd(),out int value)?value:-1;
            }
        }
        [UnityTest] public IEnumerator TransientStartup_RecoversWithoutManualRecheck()
        {
            var title=Find<TitleScreenController>();
            yield return Wait(()=>title.State==TitleStartupState.Unavailable && HealthCalls==1);
            yield return Wait(()=>title.CanEnter);
            Assert.AreEqual(2,HealthCalls);
            Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
            Assert.IsFalse(Button("Retry").gameObject.activeSelf);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"recovered-720p.png"));
            yield return new WaitForSecondsRealtime(.3f);
        }
        [UnityTest] public IEnumerator PersistentTls_BoundedRecovery_AndRefreshReusesObservation()
        {
            var title=Find<TitleScreenController>();
            yield return Wait(()=>HealthCalls==3,15);
            yield return new WaitForSecondsRealtime(3);
            Assert.AreEqual(3,HealthCalls);
            Assert.IsFalse(title.CanEnter);
            SettingsUiLanguage.SetEnglish(true);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"tls-failure-720p-en.png"));
            yield return new WaitForSecondsRealtime(.3f);
            title.OpenSettings(); yield return Wait(()=>!Find<TitleSettingsHost>().IsBusy);
            var form=Find<FirstRunModelSetup>();
            var footer=(TMP_Text)Get(form,"statusText");
            Assert.That(footer.text,Does.Not.Contain("重启"));
            Assert.That(footer.text,Does.Not.Contain("restart"));
            Mode("ready");
            form.RecheckStartupStatus();
            yield return Wait(()=>title.CanEnter);
            yield return new WaitForSecondsRealtime(.5f);
            Assert.AreEqual(4,HealthCalls,"A successful manual observation must not trigger another upstream health request.");
            title.OpenSettings(); yield return Wait(()=>!Find<TitleSettingsHost>().IsBusy);
            Mode("auth");
            form.RecheckStartupStatus();
            yield return Wait(()=>HealthCalls==5 && !Find<TitleSettingsHost>().IsBusy);
            Assert.IsTrue(Find<TitleSettingsHost>().TryClose());
            yield return Wait(()=>title.State==TitleStartupState.Unavailable);
            Assert.IsFalse(title.CanEnter,"A newer failed manual check invalidates earlier Ready.");
            Assert.AreEqual(5,HealthCalls);
        }
        [UnityTest] public IEnumerator AuthFailure_DoesNotRetry_AndShowsSafeReason()
        {
            yield return Wait(()=>Find<TitleScreenController>().State==TitleStartupState.Unavailable);
            yield return new WaitForSecondsRealtime(4);
            Assert.AreEqual(1,HealthCalls);
            Assert.IsFalse(Find<TitleScreenController>().CanEnter);
            SettingsUiLanguage.SetEnglish(false);
            Assert.That(((TMP_Text)Get(Find<TitleScreenController>(),"detailText")).text,Does.Contain("密钥"));
            SettingsUiLanguage.SetEnglish(true);
            Assert.That(((TMP_Text)Get(Find<TitleScreenController>(),"detailText")).text,Does.Contain("key"));
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"auth-failure-720p-en.png"));
            yield return new WaitForSecondsRealtime(.3f);
        }
        [UnityTest] public IEnumerator GenerationHealth_IsNeverAutomaticallyRetried()
        {
            yield return Wait(()=>Find<TitleScreenController>().State==TitleStartupState.Unavailable);
            yield return new WaitForSecondsRealtime(4);
            Assert.AreEqual(1,HealthCalls);
            Assert.IsFalse(Find<TitleScreenController>().CanEnter);
            Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
        }
    }
    public sealed class ConnectionRecoveryPolicyTests
    {
        [TestCase("OPENAI_COMPATIBLE_TLS_FAILED",true)]
        [TestCase("DEEPSEEK_CONNECTION_FAILED",true)]
        [TestCase("DEEPSEEK_TIMEOUT",true)]
        [TestCase("DEEPSEEK_AUTH_FAILED",false)]
        [TestCase("DEEPSEEK_RATE_LIMITED",false)]
        [TestCase("DEEPSEEK_TLS_CERTIFICATE_FAILED",false)]
        [TestCase("OPENAI_COMPATIBLE_BAD_REQUEST",false)]
        [TestCase("OPENAI_COMPATIBLE_PROTOCOL_ERROR",false)]
        public void RecoveryRequiresTransientReadOnlyFailure(string code,bool expected)
        {
            var main=new MainModelConfig{provider_type="openai_compatible",health_strategy="models"};
            var view=new ModelConfigView{saved_configured=true,runtime_error_code=code,
                profiles=new ModelProfilesView{active_runtime=new ModelActivationView{main_model=main}}};
            Assert.AreEqual(expected,ModelConfigClient.CanRetryStartup(view));
            main.health_strategy="generation_once";
            Assert.IsFalse(ModelConfigClient.CanRetryStartup(view));
            main.health_strategy="models";view.profiles.restart_required=true;
            Assert.IsFalse(ModelConfigClient.CanRetryStartup(view));
            view.profiles.restart_required=false;view.saved_configured=false;
            Assert.IsFalse(ModelConfigClient.CanRetryStartup(view));
        }
    }
}
