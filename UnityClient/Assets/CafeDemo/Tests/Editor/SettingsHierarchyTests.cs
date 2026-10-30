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
    public sealed class SettingsHierarchyTests
    {
        private const string Session="SettingsHierarchy.Tests.";
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static string Evidence=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Temp/SettingsHierarchy"));
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
            if(!File.Exists(Path.Combine(Evidence,"fake-ready.txt")))Assert.Ignore("Start SettingsHierarchyFakeService.py with Temp/SettingsHierarchy first.");
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty,"Keep unsaved user scene work");
            SessionState.SetString(Session+"scene",SceneManager.GetActiveScene().path);
            foreach(var name in new[]{"EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE","EMBODIEDNPC_KNOWLEDGE_ADMIN_URL"})
            {SessionState.SetBool(Session+name+"has",Environment.GetEnvironmentVariable(name)!=null);SessionState.SetString(Session+name,Environment.GetEnvironmentVariable(name)??"");}
            SessionState.SetBool(Session+"hadVolume",PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));SessionState.SetFloat(Session+"volume",PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session+"hadLanguage",PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));SessionState.SetInt(Session+"language",PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            var game=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            SessionState.SetInt(Session+"size",(int)game.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(EditorWindow.GetWindow(game)));Size(3);
            File.WriteAllText(Path.Combine(Evidence,"session.json"),new JObject{["schema_version"]=1,["base_url"]="http://127.0.0.1:18846",["token"]="hierarchy-fixture-token-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}.ToString());
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE",Path.Combine(Evidence,"session.json"));
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL","http://127.0.0.1:18846");
            using(var reset=UnityWebRequest.Get("http://127.0.0.1:18846/fixture/reset"))yield return reset.SendWebRequest();
            string method=TestContext.CurrentContext.Test.MethodName;
            EditorSceneManager.OpenScene(method.Contains("Cafe")?"Assets/Scenes/CafeDemoV2.unity":"Assets/Scenes/TitleScene.unity");
            Set(Find<BackendWebSocketClient>(),"serverUrl","ws://127.0.0.1:18846/ws");
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
            else yield return Wait(()=>Find<TitleScreenController>().CanEnter);
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
        private IEnumerator Escape()
        {
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Escape));InputSystem.Update();
            if(Find<TitleScreenController>()!=null)Find<TitleScreenController>().SendMessage("Update");yield return null;
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;
        }
        private static void Hit(Selectable control)
        {
            Canvas.ForceUpdateCanvases();var canvas=control.GetComponentInParent<Canvas>();var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(camera,control.transform.position)},hits);
            Assert.IsNotEmpty(hits,"No raycast for "+control.name);
            Assert.AreSame(control,hits[0].gameObject.GetComponentInParent<Selectable>(),"Covered "+control.name+" by "+hits[0].gameObject.name);
        }
        private static Rect ScreenRect(Transform transform)
        {var corners=new Vector3[4];((RectTransform)transform).GetWorldCorners(corners);return Rect.MinMaxRect(corners[0].x,corners[0].y,corners[2].x,corners[2].y);}
        private static void Footer(Transform page,string footerName)
        {
            var footer=page.Find(footerName);Assert.IsNotNull(footer);var bounds=ScreenRect(footer);
            foreach(var button in footer.GetComponentsInChildren<Button>()){Hit(button);Assert.IsTrue(bounds.Contains(ScreenRect(button.transform).center),button.name);}
            foreach(var scroll in page.GetComponentsInChildren<ScrollRect>())Assert.GreaterOrEqual(ScreenRect(scroll.transform).yMin,bounds.yMax-1,"Footer overlaps scroll "+scroll.name);
            var back=Object.FindObjectsByType<Button>().FirstOrDefault(b=>b.name=="BackToPlayerSettings");
            if(back!=null){Hit(back);Assert.IsFalse(ScreenRect(back.transform).Overlaps(bounds));}
        }
        private static IEnumerator Capture(string name)
        {yield return null;ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,name+"-"+Screen.width+".png"));yield return new WaitForSecondsRealtime(.2f);}
        private static void Selected(PlayerSettingsNavigation nav,string key)
        {Assert.AreEqual(key,nav.SelectedNavigation);Assert.AreEqual(SettingsPanelTheme.Accent,((Image)Button(key).targetGraphic).color);}
        private static void ModelView(FirstRunModelSetup form,ModelSettingsPage page)
        {
            Assert.AreEqual(page,form.SettingsPage);
            Assert.AreEqual(page==ModelSettingsPage.Configuration,((TMP_InputField)Get(form,"mainModel")).gameObject.activeInHierarchy);
            Assert.AreEqual(page==ModelSettingsPage.Configuration,((Button)Get(form,"saveButton")).gameObject.activeInHierarchy);
            Assert.AreEqual(page==ModelSettingsPage.Contracts,((Button)Get(form,"contractTest")).gameObject.activeInHierarchy);
            Assert.AreEqual(page==ModelSettingsPage.IndexMaintenance,((TMP_Text)Get(form,"indexState")).gameObject.activeInHierarchy);
            if(page!=ModelSettingsPage.Configuration)Assert.IsFalse(((TMP_InputField)Get(form,"temperature")).gameObject.activeInHierarchy);
        }
        [UnityTest] public IEnumerator FeedbackPrompt_CafeEmbeddingStateAndCompactLayout()
        {
            var host=Find<CafeDemoDeveloperPanel>();host.Open();yield return null;
            var form=host.ModelForm;host.PlayerNavigation.ShowAi(false);yield return null;
            typeof(FirstRunModelSetup).GetMethod("StopProfilePolling",Private).Invoke(form,null);
            var profiles=(ModelProfilesView)Get(form,"profilesView");
            var originalActive=profiles.active_runtime;
            var originalSelected=profiles.selected_to_activate;
            profiles.builtin_embedding_defaults=new EmbeddingConfig{enabled=true,provider_type="builtin",reuse_main_api=false,
                model="embeddinggemma-300m-q4",resource_status="unverified"};
            profiles.active_runtime=new ModelActivationView{embedding=profiles.builtin_embedding_defaults,
                main_model=originalActive?.main_model,main_profile_id=originalActive?.main_profile_id,embedding_profile_id="synthetic-builtin"};
            profiles.selected_to_activate=profiles.active_runtime;
            Set(form,"embeddingEditingPresetId","builtin_gemma");Set(form,"initialEmbeddingPresetId","builtin_gemma");Set(form,"embeddingEditingId",null);
            ((Toggle)Get(form,"embeddingEnabled")).SetIsOnWithoutNotify(true);
            Set(form,"embeddingBaseline",typeof(FirstRunModelSetup).GetMethod("DraftSignature",Private).Invoke(form,new object[]{true}));
            var refresh=typeof(FirstRunModelSetup).GetMethod("RefreshIndexState",Private);
            foreach(int size in new[]{3,5})
            {
                Size(size);SettingsUiLanguage.SetEnglish(size==5);yield return null;
                typeof(FirstRunModelSetup).GetMethod("RefreshEmbeddingFields",Private).Invoke(form,null);
                Set(form,"currentIndex",new EmbeddingIndexView{status="completed",applied=true,counts_known=true,
                    model="embeddinggemma-300m-q4",provider_status="ready",total=0,completed=0,progress=100});refresh.Invoke(form,null);
                yield return null;Canvas.ForceUpdateCanvases();
                var brief=(TMP_Text)Get(form,"embeddingBrief");var summary=(TMP_Text)Get(form,"ordinaryIndexState");
                Assert.That(brief.text,Does.Contain(SettingsUiLanguage.T("已应用","Applied")));
                Assert.That(brief.text,Does.Not.Contain(SettingsUiLanguage.T("尚未确认","unconfirmed")));
                Assert.That(summary.text,Does.Contain(SettingsUiLanguage.T("已加载","Loaded")));
                Assert.That(summary.text,Does.Contain(SettingsUiLanguage.T("无可索引数据","no indexable data")));
                Assert.That(summary.text,Does.Not.Contain("100.0%"));
                Assert.Less(brief.GetComponent<LayoutElement>().minHeight,40);
                Assert.Less(summary.GetComponent<LayoutElement>().minHeight,40);
                Assert.Less(brief.rectTransform.rect.height,80);
                // Scroll the real AI form to the reported area without changing its parent/layout.
                var scroll=brief.GetComponentInParent<ScrollRect>();
                if(scroll!=null)
                {
                    var title=(TMP_Text)Get(form,"embeddingSectionTitle");
                    var local=scroll.content.InverseTransformPoint(title.transform.position);
                    float hidden=scroll.content.rect.height-scroll.viewport.rect.height;
                    scroll.verticalNormalizedPosition=hidden>0?Mathf.Clamp01(1-(-local.y-title.rectTransform.rect.height*.5f-16)/hidden):1;
                }
                yield return Capture("feedback-embedding");
                ((EmbeddingIndexView)Get(form,"currentIndex")).provider_status="failed";refresh.Invoke(form,null);
                Assert.That(summary.text,Does.Contain(SettingsUiLanguage.T("加载或推理失败","Load or inference failed")));
                ((EmbeddingIndexView)Get(form,"currentIndex")).provider_status=null;refresh.Invoke(form,null);
                Assert.That(summary.text,Does.Contain(SettingsUiLanguage.T("状态未确认","Status unconfirmed")));
                ((EmbeddingIndexView)Get(form,"currentIndex")).applied=false;refresh.Invoke(form,null);
                Assert.That(brief.text,Does.Contain(SettingsUiLanguage.T("待重启应用","restart to apply")));
                profiles.selected_to_activate=null;refresh.Invoke(form,null);
                Assert.That(brief.text,Does.Contain(SettingsUiLanguage.T("尚未配置","Not configured")));
                profiles.selected_to_activate=profiles.active_runtime;
                var field=(TMP_InputField)Get(form,"embeddingModel");string original=field.text;
                field.SetTextWithoutNotify(original+"-unsaved");refresh.Invoke(form,null);
                Assert.That(brief.text,Does.Contain(SettingsUiLanguage.T("未保存修改","Unsaved changes")));
                field.SetTextWithoutNotify(original);
                Set(form,"currentIndex",null);refresh.Invoke(form,null);
                Assert.That(brief.text,Does.Contain(SettingsUiLanguage.T("应用状态未确认","Application status unconfirmed")));
            }
            profiles.active_runtime=originalActive;profiles.selected_to_activate=originalSelected;Assert.IsEmpty(Writes);
        }

        [UnityTest] public IEnumerator FeedbackPrompt_CafeDisplayContractsAndBubbles()
        {
            var host=Find<CafeDemoDeveloperPanel>();host.Open();yield return null;
            var form=host.ModelForm;var nav=host.PlayerNavigation;
            foreach(int size in new[]{3,5})
            {
                Size(size);SettingsUiLanguage.SetEnglish(size==5);yield return null;yield return null;
                Button("DisplaySettingsNav").onClick.Invoke();yield return null;Canvas.ForceUpdateCanvases();
                var resolution=Object.FindObjectsByType<TMP_Dropdown>().Single(d=>d.name=="DisplayResolution");
                var free=Object.FindObjectsByType<Toggle>().Single(t=>t.name=="FreeCameraPermission");
                Assert.IsNotNull(free.GetComponent<SettingsToggleFeedback>());
                Assert.IsNotNull(free.transform.Find("Background/SwitchThumb"));
                Assert.AreEqual(SettingsPanelTheme.Muted,free.targetGraphic.color);
                Assert.Less(ScreenRect(resolution.transform).yMax-ScreenRect(free.transform).yMin,240*Screen.height/1080f);
                free.isOn=true;Assert.AreEqual(SettingsPanelTheme.Accent,free.targetGraphic.color);
                free.isOn=false;Assert.AreEqual(SettingsPanelTheme.Muted,free.targetGraphic.color);
                yield return Capture("feedback-display");
                Assert.IsFalse(Object.FindObjectsByType<Button>(FindObjectsInactive.Include).Any(b=>b.name=="ModelMaintenanceNav"));
                nav.ShowIndexMaintenance();yield return null;
                Assert.AreEqual("AiSettingsNav",nav.SelectedNavigation);Assert.IsFalse(nav.IsDeveloper);
                Assert.IsTrue(((TMP_Text)Get(form,"indexState")).gameObject.activeInHierarchy);
                var index=(Button)Get(form,"indexAction");Assert.IsNotNull(index);
                nav.ShowModelContracts();yield return null;
                var contract=(Button)Get(form,"contractTest");
                Assert.IsFalse(contract.transform.parent.Find("Label").gameObject.activeSelf);
                Assert.LessOrEqual(((RectTransform)contract.transform).rect.width,321);
                contract.onClick.Invoke();yield return null;
                Assert.IsTrue(form.BlocksSettingsNavigation,"Paid-request consent remains mandatory");
                form.CancelSettingsConfirmation();
                typeof(FirstRunModelSetup).GetMethod("ApplyProbeResult",Private).Invoke(form,new object[]{new ModelProbeView{
                    success=true,results=new[]{"cognition_step_0","cognition_step_1","dialogue_continuation","memory_consolidation","relationship_reflection","action_feedback"}
                        .Select((name,i)=>new ModelContractResult{@case=name,status=i<3?"PASS":"NOT_RUN"}).ToArray()},null});
                Assert.That(((TMP_Text)Get(form,"contractSummary")).text,Does.Contain("3"));
                Assert.IsFalse(((TMP_InputField)Get(form,"mainModel")).gameObject.activeInHierarchy);
                yield return Capture("feedback-contracts");
                if(size==3)
                {
                    host.ShowTrace();yield return null;var trace=Find<CafeRuntimeTracePanel>();
                    string utc=DateTimeOffset.UtcNow.ToString("O");
                    trace.Buffer.Add(new JObject{["utc"]=utc,["category"]="session",["kind"]="ui_time_fixture"}.ToString());trace.RefreshView();
                    Assert.That(((TMP_InputField)Get(trace,"selectionField")).text,Does.Contain(RuntimeTraceLiveBuffer.LocalTime(utc)));
                    yield return Capture("feedback-local-time");
                }
                // Respect the existing layered Back/ESC behavior before the UI-only bubble preview.
                for(int close=0;host.IsOpen && close<4;close++){host.Close();yield return null;}
                Assert.IsFalse(host.IsOpen);
                // A UI-only bubble uses the shipped view/font/layout without producing speech or requests.
                var template=AssetDatabase.LoadAssetAtPath<EmbodiedNPC.Dialogue.Presentation.DialogueBubbleView>("Assets/CafeDemo/Prefabs/UI/WorldDialogueBubble.prefab");
                var canvas=Object.FindObjectsByType<Canvas>().First(c=>c.renderMode==RenderMode.ScreenSpaceOverlay);
                var bubble=Object.Instantiate(template,canvas.transform);bubble.gameObject.SetActive(true);
                bubble.GetComponentInChildren<TMP_Text>().font=((TMP_InputField)Get(form,"mainModel")).textComponent.font;
                var rt=bubble.RectTransform;rt.anchorMin=rt.anchorMax=new Vector2(.5f,.55f);rt.anchoredPosition=Vector2.zero;
                string sample=SettingsUiLanguage.T("早上好。你想先看看菜单，还是聊聊最近读到的故事？","Good morning. Would you like to talk about a story you have read recently?");
                bubble.SetMessage(new EmbodiedNPC.Dialogue.Presentation.DialogueBubbleMessage("ui-only","synthetic-ui-only",sample,
                    0,1,EmbodiedNPC.Dialogue.Presentation.DialogueSpeakerKind.Npc),new EmbodiedNPC.Dialogue.Presentation.DialogueBubbleVisualStyle());
                Canvas.ForceUpdateCanvases();var text=bubble.GetComponentInChildren<TMP_Text>();text.ForceMeshUpdate();
                Assert.AreEqual(0,text.characterSpacing);Assert.AreEqual(0,text.wordSpacing);Assert.IsFalse(text.enableAutoSizing);
                Assert.LessOrEqual(rt.rect.width,520);Assert.GreaterOrEqual(rt.rect.height,text.preferredHeight);
                yield return Capture("feedback-bubble");Object.Destroy(bubble.gameObject);host.Open();yield return null;
            }
            nav.ShowGeneral();
        }

        [UnityTest] public IEnumerator T1_CafeNavigation_FootersAndRaycasts_TwoResolutions()
        {
            var host=Find<CafeDemoDeveloperPanel>();host.Open();yield return null;var nav=host.PlayerNavigation;var form=host.ModelForm;
            foreach(int size in new[]{3,5})
            {
                Size(size);yield return null;yield return null;Assert.That(Screen.width,Is.EqualTo(size==3?1920:1280));
                foreach(var key in new[]{"GeneralSettingsNav","AudioSettingsNav","AiSettingsNav","NpcCharacterSettingsNav","WorldKnowledgeSettingsNav"})
                {Button(key).onClick.Invoke();yield return Wait(()=>!form.BlocksSettingsNavigation);yield return null;Selected(nav,key);Hit(Button(key));}
                var knowledge=Find<CafeWorldKnowledgePanel>();yield return Wait(()=>!knowledge.IsBusy && knowledge.SelectedId!=null);
                Assert.IsFalse(Button("AdvancedSettingsToggle").gameObject.activeInHierarchy);
                Assert.IsFalse(Button("KnowledgeAdvanced").GetComponentInChildren<TMP_Text>().text.Contains("高级设置"));
                Footer(knowledge.transform,"KnowledgeFooter");Hit(Button("NewKnowledge"));yield return Capture("cafe-knowledge");
                Button("NpcCharacterSettingsNav").onClick.Invoke();yield return null;
                Footer(((GameObject)Get(host,"npcPage")).transform,"SettingsFooter");yield return Capture("cafe-persona");
                var dropdown=((GameObject)Get(host,"npcPage")).GetComponentInChildren<TMP_Dropdown>();Hit(dropdown);dropdown.Show();yield return null;
                Assert.IsTrue(dropdown.IsExpanded);var option=dropdown.GetComponentsInChildren<Toggle>().First();Hit(option);
                yield return Escape();yield return Wait(()=>!dropdown.IsExpanded);Assert.IsTrue(host.IsOpen);Selected(nav,"NpcCharacterSettingsNav");
                Button("DeveloperToolsEntry").onClick.Invoke();yield return null;Selected(nav,"DeveloperTool0");
                Button("DeveloperTool1").onClick.Invoke();yield return null;Selected(nav,"DeveloperTool1");Assert.IsTrue(nav.DailyDebugRoot.gameObject.activeInHierarchy);
                Button("PerceptionDebugNav").onClick.Invoke();yield return null;Assert.IsTrue(((GameObject)Get(host,"debugPage")).activeInHierarchy);
                Button("DeveloperTool2").onClick.Invoke();yield return null;Selected(nav,"DeveloperTool2");
                nav.ShowIndexMaintenance();yield return Wait(()=>form.HasProfileSettings&&!form.BlocksSettingsNavigation);yield return null;
                Selected(nav,"AiSettingsNav");Assert.IsTrue(((TMP_Text)Get(form,"indexState")).gameObject.activeInHierarchy);
                nav.SetDeveloper(true);yield return null;
                Button("ModelContractsNav").onClick.Invoke();yield return null;ModelView(form,ModelSettingsPage.Contracts);Selected(nav,"ModelContractsNav");Hit((Button)Get(form,"contractTest"));yield return Capture("cafe-contracts");
                ((Button)Get(form,"contractTest")).onClick.Invoke();yield return null;Assert.IsTrue(form.BlocksSettingsNavigation);
                var modal=(GameObject)Get(form,"profileModal");foreach(var button in modal.GetComponentsInChildren<Button>())Hit(button);
                yield return Escape();Assert.IsFalse(form.BlocksSettingsNavigation);Assert.IsTrue(nav.IsDeveloper);
                yield return Escape();Assert.IsFalse(nav.IsDeveloper);Assert.IsTrue(host.IsOpen);
            }
            Assert.IsEmpty(Writes,"Navigation/cancel cannot write model, Knowledge or index data");
        }
        [UnityTest] public IEnumerator T2_TitleCapabilitiesAndModelSave_ContextKeyContracts()
        {
            Find<TitleScreenController>().OpenSettings();yield return Wait(()=>!Find<TitleSettingsHost>().IsBusy);
            var host=Find<TitleSettingsHost>();var nav=host.Navigation;var form=Find<FirstRunModelSetup>();
            Assert.IsNull(Find<CafeNpcCharacterSettings>());Assert.IsNull(Find<CafeWorldKnowledgePanel>());Assert.IsNull(Find<CafeUiFlowController>());
            Assert.IsFalse(Object.FindObjectsByType<Button>(FindObjectsInactive.Include).Any(b=>b.name=="NpcCharacterSettingsNav"||b.name=="WorldKnowledgeSettingsNav"));
            foreach(int size in new[]{3,5})
            {
                Size(size);yield return null;Assert.That(Screen.width,Is.EqualTo(size==3?1920:1280));Button("AiSettingsNav").onClick.Invoke();yield return Wait(()=>form.HasProfileSettings&&!form.BlocksSettingsNavigation);yield return null;
                ModelView(form,ModelSettingsPage.Configuration);Hit((Button)Get(form,"saveButton"));yield return Capture("title-ai");
                nav.SetDeveloper(true);yield return null;
                Button("ModelContractsNav").onClick.Invoke();yield return null;ModelView(form,ModelSettingsPage.Contracts);Hit(Button("BackToPlayerSettings"));yield return Capture("title-contracts");
                yield return Escape();Assert.IsFalse(nav.IsDeveloper);Assert.IsTrue(host.IsOpen);
            }
            Button("AiSettingsNav").onClick.Invoke();yield return Wait(()=>Get(form,"contextPolicyView")!=null);
            var main=(TMP_InputField)Get(form,"mainModel");string model=main.text+"-hierarchy";main.text=model;
            nav.ShowIndexMaintenance();yield return null;nav.ShowModelContracts();yield return null;Assert.AreEqual(model,main.text,"Maintenance navigation preserves the model draft");
            nav.ShowAi(false);yield return null;Assert.AreEqual(model,main.text);
            foreach(string action in new[]{"retain","replace","clear"})
            {
                ((TMP_InputField)Get(form,"mainApiKey")).text=action=="replace"?"synthetic-only":"";Set(form,"mainClearKey",action=="clear");
                var command=(JObject)typeof(FirstRunModelSetup).GetMethod("BuildCombinedProfileCommand",Private).Invoke(form,null);Assert.AreEqual(action,(string)command["main"]["key_action"]);
                Assert.IsNotNull(command["embedding"]);Assert.AreEqual(7,(int)command["expected_revision"]);
            }
            Set(form,"mainClearKey",false);((TMP_InputField)Get(form,"mainApiKey")).text="";
            ((TMP_Dropdown)Get(form,"recentMode")).value=1;((TMP_InputField)Get(form,"recentTarget")).text="32";
            ((Button)Get(form,"saveButton")).onClick.Invoke();yield return Wait(()=>Writes.Count==2&&!host.IsBusy);
            Assert.AreEqual("profiles",(string)Writes[0]["resource"]);Assert.AreEqual("context",(string)Writes[1]["resource"]);
            Assert.AreEqual("retain",(string)Writes[0]["key_actions"]["main"]);CollectionAssert.AreEquivalent(new[]{"main","embedding"},Writes[0]["slots"].Values<string>());
            var profiles=(ModelProfilesView)Get(form,"profilesView");Assert.IsTrue(profiles.restart_required);Assert.AreNotEqual(profiles.active_runtime.main_model.model,model);
            var context=(ContextPolicyView)Get(form,"contextPolicyView");Assert.AreEqual(32,context.applied.config.target_messages);
            Assert.AreEqual(4,((Toggle[])Get(form,"embeddingCategories")).Length);
        }
        [UnityTest] public IEnumerator T3_CafeContentDrafts_OriginalOperationsAndEscHost()
        {
            var flow=Find<CafeUiFlowController>();flow.OpenMenu();flow.OpenSettings();yield return null;var host=Find<CafeDemoDeveloperPanel>();
            Button("NpcCharacterSettingsNav").onClick.Invoke();yield return null;var npc=Find<CafeNpcCharacterSettings>();Assert.Greater(npc.CandidateCount,0);
            ((TMP_InputField)Get(npc,"displayName")).text="隔离人设";Button("SaveNpc").onClick.Invoke();
            var store=(CharacterOverrideStore)Get(npc,"store");Assert.IsTrue(store.TryGet(npc.SelectedCharacterId,out _));Assert.IsTrue(store.FilePath.StartsWith(Evidence));
            Button("RestoreDefault").onClick.Invoke();Assert.IsFalse(store.TryGet(npc.SelectedCharacterId,out _));
            Button("WorldKnowledgeSettingsNav").onClick.Invoke();yield return null;var knowledge=Find<CafeWorldKnowledgePanel>();yield return Wait(()=>!knowledge.IsBusy&&knowledge.SelectedId!=null);
            var input=(TMP_InputField)Get(knowledge,"nameInput");input.text="隔离知识草稿";Button("AiSettingsNav").onClick.Invoke();yield return null;
            Assert.IsTrue(knowledge.ConfirmationVisible);Assert.IsTrue(knowledge.gameObject.activeInHierarchy);Selected(host.PlayerNavigation,"WorldKnowledgeSettingsNav");
            yield return Escape();Assert.IsFalse(knowledge.ConfirmationVisible);Assert.IsTrue(knowledge.IsDirty);Assert.AreEqual("隔离知识草稿",input.text);
            Hit(Button("SaveKnowledge"));Button("SaveKnowledge").onClick.Invoke();yield return Wait(()=>!knowledge.IsBusy&&!knowledge.IsDirty);Assert.AreEqual("knowledge",(string)Writes.Last["resource"]);
            Button("DeleteKnowledge").onClick.Invoke();yield return null;Assert.IsTrue(knowledge.ConfirmationVisible);yield return Escape();Assert.IsFalse(knowledge.ConfirmationVisible);
            Button("AiSettingsNav").onClick.Invoke();yield return Wait(()=>host.ModelForm.HasProfileSettings&&!host.ModelForm.BlocksSettingsNavigation);
            Button("DeveloperToolsEntry").onClick.Invoke();yield return null;Button("ModelContractsNav").onClick.Invoke();yield return null;
            ((Button)Get(host.ModelForm,"contractTest")).onClick.Invoke();yield return null;yield return Escape();Assert.IsTrue(host.PlayerNavigation.IsDeveloper);
            yield return Escape();Assert.IsFalse(host.PlayerNavigation.IsDeveloper);yield return Escape();Assert.IsFalse(host.IsOpen);Assert.IsTrue(flow.IsMenuOpen);Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);
            yield return Escape();Assert.IsFalse(flow.IsMenuOpen);Assert.IsFalse(Find<CafeDemoPlayerController>().IsInputSuspended);
        }
    }
}
