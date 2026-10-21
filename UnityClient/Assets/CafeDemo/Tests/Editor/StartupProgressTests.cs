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
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class StartupProgressTests
    {
        private const string Session="StartupProgress.Tests.";
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static string Evidence=>TestContext.CurrentContext.Test.MethodName.StartsWith("P3D_")?@"D:\EmbodiedNPC-StandaloneUIP3D-20261018\evidence":Path.GetFullPath(Path.Combine(Application.dataPath,"../Temp/StartupProgress"));
        private static T Find<T>() where T:Object=>Object.FindAnyObjectByType<T>();
        private static object Get(object target,string field)=>target.GetType().GetField(field,Private).GetValue(target);
        private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Private).SetValue(target,value);
        private static StartupProgressDisplay View(object owner)=>(StartupProgressDisplay)Get(owner,"startupProgress");
        private static void Mode(string mode)=>File.WriteAllText(Path.Combine(Evidence,"mode.txt"),mode);
        private static JObject Stats()
        {using(var file=new FileStream(Path.Combine(Evidence,"stats.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(file))return JObject.Parse(reader.ReadToEnd());}
        private static void Size(int index)
        {
            if(index==-720)
            {
                var group=SizeGroup();index=SessionState.GetInt(Session+"temporarySize",-1);
                if(index<0)
                {
                    var assembly=typeof(EditorWindow).Assembly;var size=Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),1280,720,"Startup UX isolated 720p");
                    index=(int)group.GetType().GetMethod("GetTotalCount").Invoke(group,null);group.GetType().GetMethod("AddCustomSize").Invoke(group,new[]{size});SessionState.SetInt(Session+"temporarySize",index);
                }
            }
            var type=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");type.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(EditorWindow.GetWindow(type),index);
        }
        private static object SizeGroup()
        {var type=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameViewSizes");return type.GetMethod("GetGroup").Invoke(typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance").GetValue(null),new object[]{0});}
        [UnitySetUp] public IEnumerator Setup()
        {
            if(!File.Exists(Path.Combine(Evidence,"fake-ready.txt")))Assert.Ignore("Start StartupProgressFakeService.py with Temp/StartupProgress first.");
            Assert.IsFalse(Application.isPlaying,"Never stop an existing user Play session");
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty,"Preserve unsaved scene work");
            SessionState.SetString(Session+"scene",SceneManager.GetActiveScene().path);
            foreach(var name in new[]{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE","EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"})
            {SessionState.SetBool(Session+name+"has",Environment.GetEnvironmentVariable(name)!=null);SessionState.SetString(Session+name,Environment.GetEnvironmentVariable(name)??"");}
            SessionState.SetBool(Session+"hadVolume",PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));SessionState.SetFloat(Session+"volume",PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session+"hadLanguage",PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));SessionState.SetInt(Session+"language",PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            var game=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");SessionState.SetInt(Session+"size",(int)game.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(EditorWindow.GetWindow(game)));Size(3);
            File.WriteAllText(Path.Combine(Evidence,"session.json"),new JObject{["schema_version"]=1,["base_url"]="http://127.0.0.1:18846",["token"]="startup-fixture-token-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}.ToString());
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",Path.Combine(Evidence,"session.json"));Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL","http://127.0.0.1:18846");
            Mode("ready");EditorSceneManager.OpenScene("Assets/Scenes/TitleScene.unity");
            Set(Find<BackendWebSocketClient>(),"serverUrl","ws://127.0.0.1:18846/ws");
            yield return new EnterPlayMode();
            SceneManager.sceneLoaded+=PatchCafe;
            SettingsUiLanguage.SetEnglish(false);
        }
        private static void PatchCafe(Scene scene,LoadSceneMode mode)
        {
            if(scene.name!="CafeDemoV2")return;
            Set(Find<BackendWebSocketClient>(),"serverUrl","ws://127.0.0.1:18846/ws");
            foreach(var trace in Object.FindObjectsByType<RuntimeTraceRecorder>())trace.enabled=false;
            foreach(var driver in Object.FindObjectsByType<CognitiveRunDriver>())driver.enabled=false;
            // Runtime-only font assignments; never save a scene or bake into shared font WIP.
            var texts=Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include);
            var font=TMP_FontAsset.CreateFontAsset(texts.First(t=>t.font?.sourceFontFile!=null).font.sourceFontFile);font.isMultiAtlasTexturesEnabled=true;
            foreach(var text in texts)text.font=font;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded-=PatchCafe;
            SceneManager.sceneLoaded-=PatchTitleForLanguageTest;
            if(Application.isPlaying)
            {
                Mode("ready");
                foreach(var backend in Object.FindObjectsByType<BackendWebSocketClient>())
                {
                    Assert.AreEqual(0,backend.CognitiveRequestSendCount,"No cognition/LLM requests");
                    // Settle this fixture's temporary observations through Fake ACKs before exit.
                    if(backend.PendingExperienceCount>0)
                    {backend.EnsureConnected();yield return Wait(()=>backend.PendingExperienceCount==0,15);}
                    var close=backend.DisconnectAsync();while(!close.IsCompleted)yield return null;
                }
                yield return Wait(()=>(int)Stats()["active_ws"]==0,5);
                yield return new ExitPlayMode();
            }
            foreach(var name in new[]{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE","EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"})Environment.SetEnvironmentVariable(name,SessionState.GetBool(Session+name+"has",false)?SessionState.GetString(Session+name,""):null);
            if(SessionState.GetBool(Session+"hadVolume",false))PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey,SessionState.GetFloat(Session+"volume",0));else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            SettingsUiLanguage.SetEnglish(SessionState.GetInt(Session+"language",0)==1);if(!SessionState.GetBool(Session+"hadLanguage",false))PlayerPrefs.DeleteKey("EmbodiedNPC.Settings.Language");PlayerPrefs.Save();
            yield return null;Size(SessionState.GetInt(Session+"size",0));
            int temporary=SessionState.GetInt(Session+"temporarySize",-1);if(temporary>=0){var group=SizeGroup();group.GetType().GetMethod("RemoveCustomSize").Invoke(group,new object[]{temporary});SessionState.EraseInt(Session+"temporarySize");}
            var previous=SessionState.GetString(Session+"scene","");EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous)?"Assets/Scenes/CafeDemoV2.unity":previous);
            Assert.AreEqual(0,(int)Stats()["active_ws"]);Assert.LessOrEqual((int)Stats()["max_ws"],1);Assert.AreEqual(0,((JArray)Stats()["writes"]).Count);
        }
        private static IEnumerator Wait(Func<bool> condition,float timeout=15,[System.Runtime.CompilerServices.CallerLineNumber]int line=0)
        {float until=Time.realtimeSinceStartup+timeout;while(!condition()&&Time.realtimeSinceStartup<until)yield return null;Assert.IsTrue(condition(),"Startup progress timeout at line "+line+"; scene="+SceneManager.GetActiveScene().name+"; title="+Find<TitleScreenController>()?.State);}
        private static void Click(Button button)
        {
            Canvas.ForceUpdateCanvases();Assert.IsTrue(button.isActiveAndEnabled&&button.interactable,button.name);
            var data=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(null,button.transform.position),button=PointerEventData.InputButton.Left};
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(data,hits);Assert.IsNotEmpty(hits,button.name);
            Assert.AreSame(button,hits[0].gameObject.GetComponentInParent<Button>(),"Covered "+button.name+" by "+hits[0].gameObject.name);
            ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerClickHandler);
        }
        private static Button ProgressButton(StartupProgressDisplay view,string name)=>view.Root.transform.Find(name).GetComponent<Button>();
        private static IEnumerator Capture(string name)
        {Canvas.ForceUpdateCanvases();ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,name+"-"+Screen.width+".png"));yield return new WaitForSecondsRealtime(.2f);}
        private static void Visible(StartupProgressDisplay view)
        {
            Canvas.ForceUpdateCanvases();Assert.IsTrue(view.Root.activeInHierarchy);
            var body=view.Root.GetComponentInChildren<TMP_Text>();body.ForceMeshUpdate();Assert.IsFalse(body.canvasRenderer.cull);Assert.IsFalse(body.isTextTruncated,"Progress text clipped");
            var corners=new Vector3[4];body.rectTransform.GetWorldCorners(corners);Assert.GreaterOrEqual(corners[0].x,0);Assert.LessOrEqual(corners[2].x,Screen.width+1);Assert.GreaterOrEqual(corners[0].y,0);Assert.LessOrEqual(corners[2].y,Screen.height+1);
            Assert.IsFalse(view.States.Contains(StartupStepState.Running),"Failure must leave loading state");
        }
        [UnityTest] public IEnumerator T1_Title_RealStagesAndFailures()
        {
            var title=Find<TitleScreenController>();
            yield return Wait(()=>title.CanEnter);
            var view=View(title);Assert.IsTrue(view.States.All(s=>s==StartupStepState.Completed));yield return Capture("title-ready");
            int profiles=(int)Stats()["paths"]["/v2/model-config"],runtime=(int)Stats()["paths"]["/v1/runtime-status"];
            title.Retry();Assert.AreEqual(StartupStepState.Running,view.States[0]);Assert.AreEqual(StartupStepState.Waiting,view.States[1]);
            yield return Capture("title-http-running");
            yield return Wait(()=>view.States[0]==StartupStepState.Completed&&view.States[1]==StartupStepState.Running);
            yield return Capture("title-provider-running");yield return Wait(()=>title.CanEnter);
            Assert.AreEqual(profiles+1,(int)Stats()["paths"]["/v2/model-config"]);Assert.AreEqual(runtime+1,(int)Stats()["paths"]["/v1/runtime-status"],"Presentation must not duplicate HTTP");
            foreach(var sample in new[]{("httpfail",StartupUiFailure.Http),("unconfigured",StartupUiFailure.Configuration),("pending",StartupUiFailure.Restart),("providerfail",StartupUiFailure.Provider)})
            {
                Mode(sample.Item1);title.Retry();yield return Wait(()=>title.State!=TitleStartupState.Checking&&title.State!=TitleStartupState.Connecting);
                Assert.AreEqual(sample.Item2,view.Failure,sample.Item1);Assert.IsFalse(title.CanEnter);Visible(view);Click(ProgressButton(view,"StartupDetails"));
                Assert.IsTrue(view.VisibleText.Contains("详情"));yield return Capture("title-"+sample.Item1);
                Assert.IsTrue(ProgressButton(view,"StartupRecheck").interactable);Assert.IsTrue(ProgressButton(view,"StartupConfigure").interactable);
            }
            Size(-720);yield return null;Visible(view);yield return Capture("title-providerfail");Size(3);yield return null;
            Mode("wsfail");var close=Find<BackendWebSocketClient>().DisconnectAsync();while(!close.IsCompleted)yield return null;
            title.Retry();yield return Wait(()=>title.State==TitleStartupState.Connecting);Assert.AreEqual(StartupStepState.Completed,view.States[1]);Assert.AreEqual(StartupStepState.Running,view.States[2]);
            yield return Wait(()=>title.State==TitleStartupState.Unavailable,25);Assert.AreEqual(StartupUiFailure.WebSocket,view.Failure);Visible(view);yield return Capture("title-wsfail");
            Mode("ready");Click(ProgressButton(view,"StartupRecheck"));yield return Wait(()=>title.CanEnter);Assert.AreEqual(StartupUiFailure.None,view.Failure);
            // Exercise the scene exception presentation without damaging BuildSettings/scenes.
            typeof(TitleScreenController).GetMethod("ShowCafeTransition",Private).Invoke(title,null);
            typeof(TitleScreenController).GetMethod("SceneLoadFailed",Private).Invoke(title,new object[]{"ISOLATED_SCENE_ERROR"});
            Assert.AreEqual(StartupUiFailure.Scene,view.Failure);Assert.IsFalse(title.CanEnter);Visible(view);yield return Capture("title-scene-error");
            Click(ProgressButton(view,"StartupConfigure"));yield return null;Assert.IsTrue(Find<TitleSettingsHost>().IsOpen);Assert.IsFalse(view.Root.activeInHierarchy);
            Assert.AreEqual("AiSettingsNav",Find<TitleSettingsHost>().Navigation.SelectedNavigation);
        }
        [UnityTest] public IEnumerator P3C_TitleQuickLanguage_OfflineSettingsAndCafePersistence()
        {
            var title=Find<TitleScreenController>();yield return Wait(()=>title.CanEnter);
            var evidence=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../EmbodiedNPC-LanguageP3C-20261017/evidence"));Directory.CreateDirectory(evidence);
            Button Choice(string name)=>title.transform.Find("QuickLanguage/"+name).GetComponent<Button>();
            void Unclipped()
            {
                Canvas.ForceUpdateCanvases();
                foreach(var text in title.transform.Find("QuickLanguage").GetComponentsInChildren<TMP_Text>())
                {text.ForceMeshUpdate();Assert.IsFalse(text.isTextOverflowing,text.text);var corners=new Vector3[4];text.rectTransform.GetWorldCorners(corners);Assert.GreaterOrEqual(corners[0].x,0);Assert.GreaterOrEqual(corners[0].y,0);Assert.LessOrEqual(corners[2].x,Screen.width+1);Assert.LessOrEqual(corners[2].y,Screen.height+1);}
            }
            Mode("httpfail");title.Retry();yield return Wait(()=>title.State==TitleStartupState.Unavailable);
            var state=title.State;var paths=Stats()["paths"].ToString();var writes=Stats()["writes"].ToString();
            Click(Choice("English"));yield return null;
            Assert.IsTrue(SettingsUiLanguage.IsEnglish);Assert.AreEqual(state,title.State);Assert.IsFalse(title.CanEnter);
            Assert.AreEqual("AI service unavailable",title.StatusText);Assert.AreEqual(paths,Stats()["paths"].ToString());Assert.AreEqual(writes,Stats()["writes"].ToString());
            Assert.IsTrue(((TMP_Text)Get(title,"detailText")).text.StartsWith("Start Backend and retry, or open AI settings.\nError: "));
            Unclipped();ScreenCapture.CaptureScreenshot(Path.Combine(evidence,"title-en-1920.png"));yield return new WaitForSecondsRealtime(.2f);
            title.OpenSettings();yield return Wait(()=>!Find<TitleSettingsHost>().IsBusy);var host=Find<TitleSettingsHost>();host.Navigation.ShowGeneral();yield return null;
            var dropdown=host.Navigation.GeneralPage.GetComponentInChildren<TMP_Dropdown>();Assert.AreEqual(1,dropdown.value);
            dropdown.value=0;Assert.IsFalse(SettingsUiLanguage.IsEnglish);Assert.IsTrue(host.TryClose());yield return null;
            Assert.AreEqual("暂时无法连接 AI 服务",title.StatusText);Unclipped();ScreenCapture.CaptureScreenshot(Path.Combine(evidence,"title-zh-1920.png"));yield return new WaitForSecondsRealtime(.2f);
            Size(-720);yield return null;Click(Choice("English"));Unclipped();Click(Choice("Chinese"));Unclipped();Size(3);yield return null;
            Mode("unconfigured");title.Retry();yield return Wait(()=>title.State==TitleStartupState.Unconfigured);
            Click(Choice("English"));Assert.AreEqual(TitleStartupState.Unconfigured,title.State);Assert.IsFalse(title.CanEnter);
            Assert.AreEqual(1,PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            Mode("ready");title.Retry();yield return Wait(()=>title.CanEnter);Click((Button)Get(title,"enterButton"));yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2");
            yield return Wait(()=>Find<DialogueController>().IsSetupReady);Assert.IsTrue(SettingsUiLanguage.IsEnglish);
            Assert.AreEqual("Lin Weiwei",SettingsUiLanguage.NpcDisplayName("npc_cafe_01","林薇薇"));
            var transport=Find<BackendWebSocketClient>();Assert.AreEqual(0,transport.CognitiveRequestSendCount);yield return Wait(()=>transport.PendingExperienceCount==0);
            var close=transport.DisconnectAsync();while(!close.IsCompleted)yield return null;
            SceneManager.sceneLoaded+=PatchTitleForLanguageTest;
            yield return SceneManager.LoadSceneAsync("TitleScene");SceneManager.sceneLoaded-=PatchTitleForLanguageTest;
            title=Find<TitleScreenController>();yield return Wait(()=>title.CanEnter);Assert.IsTrue(SettingsUiLanguage.IsEnglish);
            Assert.AreEqual("Settings",((Button)Get(title,"settingsButton")).GetComponentInChildren<TMP_Text>().text);
            Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);Assert.AreEqual(writes,Stats()["writes"].ToString());
        }
        private static void PatchTitleForLanguageTest(Scene scene,LoadSceneMode mode)
        {if(scene.name=="TitleScene")Set(Find<BackendWebSocketClient>(),"serverUrl","ws://127.0.0.1:18846/ws");}
        [UnityTest] public IEnumerator P3D_TitleCafe_CompactProgressAndFailure()
        {
            var title=Find<TitleScreenController>();yield return Wait(()=>title.CanEnter);
            Click((Button)Get(title,"enterButton"));var view=View(title);
            Assert.AreEqual("正在进入咖啡馆…",view.VisibleText);
            Assert.IsFalse(view.Root.activeInHierarchy);Assert.AreEqual("正在进入咖啡馆…",title.StatusText);
            yield return Capture("title-simple-loading");
            yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2");
            var cafe=Find<CafeDemoDeveloperPanel>();yield return Wait(()=>View(cafe)!=null);
            var cafeView=View(cafe);Assert.AreEqual("正在进入咖啡馆…",cafeView.VisibleText);
            Assert.IsTrue(cafe.IsStartupAdmissionHeld);Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);
            yield return Capture("cafe-simple-loading");yield return Wait(()=>Find<DialogueController>().IsSetupReady);
            Assert.IsFalse(cafeView.Root.activeInHierarchy);Assert.IsFalse(cafe.IsStartupAdmissionHeld);
            // Test the same presentation's failure branch without changing admission or sending another request.
            cafeView.Fail(2,StartupUiFailure.Provider,"UI_ONLY_PROVIDER_FAILURE");
            Assert.That(cafeView.VisibleText,Does.Contain("Provider"));Assert.That(cafeView.VisibleText,Does.Not.Contain("正在进入"));
            Assert.IsTrue(ProgressButton(cafeView,"StartupRecheck").gameObject.activeSelf);
            Assert.IsTrue(ProgressButton(cafeView,"StartupDetails").gameObject.activeSelf);
            cafeView.ClearFailure();
        }
        [UnityTest] public IEnumerator T2_TitleCafe_ProgressAndAdmission()
        {
            var title=Find<TitleScreenController>();yield return Wait(()=>title.CanEnter);var view=View(title);
            Click((Button)Get(title,"enterButton"));Assert.AreEqual(StartupStepState.Running,view.States[0]);
            yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2"||view.States[1]==StartupStepState.Running);
            Assert.AreEqual("TitleScene",SceneManager.GetActiveScene().name);Assert.AreEqual(StartupStepState.Completed,view.States[0]);
            ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"title-scene-loading-1920.png"));
            yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2");
            var cafe=Find<CafeDemoDeveloperPanel>();yield return Wait(()=>View(cafe)!=null);var cafeView=View(cafe);
            Assert.IsTrue(cafe.IsStartupAdmissionHeld);Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);Assert.AreEqual(StartupStepState.Completed,cafeView.States[0]);
            yield return Capture("cafe-http-running");
            yield return Wait(()=>Find<DialogueController>().IsSetupReady);
            Assert.IsFalse(cafe.IsStartupAdmissionHeld);Assert.IsFalse(Find<CafeDemoPlayerController>().IsInputSuspended);Assert.IsFalse(Find<CafeDemoOrbitCamera>().IsInputSuspended);
            Assert.IsFalse(cafeView.Root.activeInHierarchy);Assert.IsTrue(cafeView.States.All(s=>s==StartupStepState.Completed));
            Assert.AreEqual(1,Object.FindObjectsByType<BackendWebSocketClient>().Length);Assert.AreEqual(1,Object.FindObjectsByType<AudioListener>().Length);Assert.AreEqual(1,Object.FindObjectsByType<CafeBackgroundMusic>().Length);
            yield return Capture("cafe-ready");
        }
        [UnityTest] public IEnumerator T3_Cafe_HttpFailureAndRecoveryEntry()=>CafeFailure("httpfail",StartupUiFailure.Http);
        [UnityTest] public IEnumerator T4_Cafe_WsFailureAndRecoveryEntry()=>CafeFailure("wsfail",StartupUiFailure.WebSocket);
        private IEnumerator CafeFailure(string mode,StartupUiFailure expected)
        {
            var title=Find<TitleScreenController>();yield return Wait(()=>title.CanEnter);Mode(mode);Click((Button)Get(title,"enterButton"));
            yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2");var cafe=Find<CafeDemoDeveloperPanel>();
            yield return Wait(()=>(bool)Get(cafe,"startupConfigResolved")&&View(cafe).Failure==expected&&(mode!="wsfail"||Find<BackendWebSocketClient>().ConnectionStatus=="OFFLINE"),25);
            if(cafe.IsOpen)Assert.IsFalse(((TMP_Text)Get(cafe.ModelForm,"statusText")).text.Contains("正在连接"),"Auto-opened form must not retain stale loading text");
            // Existing startup failure opens the original AI form; close it to inspect the gate.
            for(int i=0;i<4&&cafe.IsOpen;i++){cafe.Close();yield return null;}
            Assert.IsFalse(cafe.IsOpen);var view=View(cafe);Visible(view);Assert.IsTrue(cafe.IsStartupAdmissionHeld);Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);
            Assert.IsFalse(Find<DialogueController>().IsSetupReady);Assert.AreEqual(StartupStepState.Failed,view.States[4]);
            Click(ProgressButton(view,"StartupDetails"));Visible(view);yield return Capture("cafe-"+mode);
            Size(-720);yield return null;Visible(view);yield return Capture("cafe-"+mode);Size(3);yield return null;
            Click(ProgressButton(view,"StartupConfigure"));yield return null;Assert.IsTrue(cafe.IsOpen);Assert.IsTrue(cafe.ModelForm.gameObject.activeInHierarchy);Assert.IsTrue(cafe.IsStartupAdmissionHeld);
            Assert.AreEqual("AiSettingsNav",cafe.PlayerNavigation.SelectedNavigation);
            for(int i=0;i<4&&cafe.IsOpen;i++){cafe.Close();yield return null;}
            if(mode=="httpfail")
            {
                Mode("providerfail");Click(ProgressButton(view,"StartupRecheck"));
                yield return Wait(()=>!cafe.ModelForm.IsSavingSettings&&view.Failure==StartupUiFailure.Provider);
                Assert.IsTrue(cafe.IsStartupAdmissionHeld);Assert.IsFalse(Find<DialogueController>().IsSetupReady);
                Assert.IsTrue(((TMP_Text)Get(cafe.ModelForm,"statusText")).text.Contains("Provider"));
                for(int i=0;i<4&&cafe.IsOpen;i++){cafe.Close();yield return null;}Visible(view);
            }
            Mode("ready");Click(ProgressButton(view,"StartupRecheck"));
            Assert.IsTrue(((TMP_Text)Get(cafe.ModelForm,"statusText")).text.Contains("正在重新检查"));
            yield return Wait(()=>Find<DialogueController>().IsSetupReady);
            yield return Wait(()=>!cafe.ModelForm.IsSavingSettings);
            for(int i=0;i<4&&cafe.IsOpen;i++){cafe.Close();yield return null;}
            Assert.IsFalse(cafe.IsStartupAdmissionHeld);Assert.IsFalse(view.Root.activeInHierarchy);Assert.IsFalse(Find<CafeDemoPlayerController>().IsInputSuspended);
        }
    }
}
