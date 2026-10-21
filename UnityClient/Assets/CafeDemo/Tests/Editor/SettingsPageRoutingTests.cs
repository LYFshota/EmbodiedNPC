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
    public sealed class SettingsPageRoutingTests
    {
        private const string Session="SettingsPageRouting.Tests.";
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private static string Evidence=>TestContext.CurrentContext.Test.MethodName.StartsWith("P3E_")
            ? @"D:\EmbodiedNPC-UXP3E-20261018\evidence" : TestContext.CurrentContext.Test.MethodName.StartsWith("P3D_")
            ? @"D:\EmbodiedNPC-StandaloneUIP3D-20261018\evidence" : TestContext.CurrentContext.Test.MethodName.StartsWith("P3A_")
            ? @"D:\EmbodiedNPC-AISettingsP3A-20261017\evidence" : Path.GetFullPath(Path.Combine(Application.dataPath,"../Temp/SettingsPageRouting"));
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
            if(!File.Exists(Path.Combine(Evidence,"fake-ready.txt")))Assert.Ignore("Start SettingsPageRoutingFakeService.py with Temp/SettingsPageRouting first.");
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
            File.WriteAllText(Path.Combine(Evidence,"routing-mode.txt"),method.Contains("Offline")?"offline":"ready");
            yield return new EnterPlayMode();
            {
            bool offline=File.ReadAllText(Path.Combine(Evidence,"routing-mode.txt"))=="offline";
            keyboard=InputSystem.AddDevice<Keyboard>();
            if(SceneManager.GetActiveScene().name=="CafeDemoV2")
            {
                // Every newly injected label/control uses a private font/atlas, preserving font WIP.
                var host=Find<CafeDemoDeveloperPanel>();var texts=((GameObject)Get(host,"panelRoot")).GetComponentsInChildren<TMP_Text>(true);
                var font=TMP_FontAsset.CreateFontAsset(texts[0].font.sourceFontFile);font.isMultiAtlasTexturesEnabled=true;
                foreach(var text in texts)text.font=font;
                var knowledge=(CafeWorldKnowledgePanel)Get(host,"knowledgeSettings");Set(Get(knowledge,"view"),"font",font);
                Set(Get(host,"npcSettings"),"store",new CharacterOverrideStore(Path.Combine(Evidence,"persona.json")));
                if(TestContext.CurrentContext.Test.MethodName.StartsWith("P3D_") || TestContext.CurrentContext.Test.MethodName.StartsWith("P3E_"))typeof(CafeDemoDeveloperPanel).GetMethod("RecheckStartup",Private).Invoke(host,null);
                yield return Wait(()=>offline?(bool)Get(host,"startupConfigResolved"):Find<DialogueController>().IsSetupReady);
                if(TestContext.CurrentContext.Test.MethodName.StartsWith("P3D_") || TestContext.CurrentContext.Test.MethodName.StartsWith("P3E_"))host.Close();
            }
            else yield return Wait(()=>offline?Find<TitleScreenController>().State==TitleStartupState.Unavailable:Find<TitleScreenController>().CanEnter);
            }
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(keyboard!=null)InputSystem.RemoveDevice(keyboard);
            if(Application.isPlaying)
            {
                yield return Wait(()=>Object.FindObjectsByType<BackendWebSocketClient>().All(b=>b.PendingExperienceCount==0));
                foreach(var backend in Object.FindObjectsByType<BackendWebSocketClient>()){var close=backend.DisconnectAsync();while(!close.IsCompleted)yield return null;}yield return new ExitPlayMode();
            }
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
        private static Button NavButton(string name)=>Button(name);
        private static void Click(Button button)
        {
            Assert.IsTrue(button.interactable,button.name);Hit(button);
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,
                position=RectTransformUtility.WorldToScreenPoint(null,button.transform.position)};
            ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerClickHandler);
        }
        private static string PathOf(Transform t)=>t.parent==null?t.name:PathOf(t.parent)+"/"+t.name;
        private static bool Drawn(Graphic graphic)
        {
            if(!graphic.isActiveAndEnabled || graphic.canvas==null || !graphic.canvas.isActiveAndEnabled || graphic.canvasRenderer.cull)return false;
            if(graphic.canvasRenderer.GetAlpha()<=.001f)return false;
            foreach(var group in graphic.GetComponentsInParent<CanvasGroup>())if(group.alpha<=.001f)return false;
            return true;
        }
        private static Transform ModelPage(FirstRunModelSetup form)
        {var t=((Component)Get(form,"mainModel")).transform;while(t!=null&&t.name!="ModelPage")t=t.parent;Assert.IsNotNull(t);return t;}
        private static void VisiblePage(FirstRunModelSetup form,PlayerSettingsNavigation nav,ModelSettingsPage page,string phase)
        {
            Canvas.ForceUpdateCanvases();Assert.AreEqual(page,form.SettingsPage,phase);
            string title=page==ModelSettingsPage.IndexMaintenance?"记忆索引维护":page==ModelSettingsPage.Contracts?"模型合同测试":"AI 配置";
            var root=ModelPage(form);var heading=root.Find("Title").GetComponent<TMP_Text>();Assert.AreEqual(title,heading.text,phase);Assert.IsTrue(Drawn(heading),phase+" title not rendered");
            string key=page==ModelSettingsPage.IndexMaintenance?"ModelMaintenanceNav":page==ModelSettingsPage.Contracts?"ModelContractsNav":"AiSettingsNav";Selected(nav,key);
            Assert.AreEqual(1,Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include).Length,"One live business form");
            var scroll=((GameObject)Get(form,"panel")).GetComponentInChildren<ScrollRect>(true);
            foreach(string field in new[]{"configurationContent","contractContent","memoryIndexSection"})
            {var go=(GameObject)Get(form,field);Assert.IsNotNull(go,phase+field);Assert.IsTrue(go.transform.IsChildOf(scroll.content),phase+" wrong Content "+field);}
            var evidence=new JArray();
            foreach(string field in new[]{"mainBaseUrl","mainApiKey","mainModel","temperature","thinking","maxOutputTokens","contextLength","mainTimeout","saveButton"})
            {
                var control=(Component)Get(form,field);bool visible=control.GetComponentsInChildren<Graphic>(true).Any(Drawn);
                if(page!=ModelSettingsPage.Configuration)
                {
                    Assert.IsFalse(control.gameObject.activeInHierarchy,phase+" active legacy field "+field);Assert.IsFalse(visible,phase+" rendered legacy field "+field);
                    var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(null,control.transform.position)},hits);
                    Assert.IsFalse(hits.Any(h=>h.gameObject.transform==control.transform||h.gameObject.transform.IsChildOf(control.transform)),phase+" raycast legacy field "+field);
                }
                evidence.Add(new JObject{["field"]=field,["path"]=PathOf(control.transform),["activeSelf"]=control.gameObject.activeSelf,["activeInHierarchy"]=control.gameObject.activeInHierarchy,["drawn"]=visible,
                    ["canvasGroups"]=new JArray(control.GetComponentsInParent<CanvasGroup>(true).Select(g=>new JObject{["path"]=PathOf(g.transform),["alpha"]=g.alpha,["blocksRaycasts"]=g.blocksRaycasts}))});
            }
            if(page!=ModelSettingsPage.Configuration)
            {
                string[] forbidden={"模型 / API 配置","API 地址","API 密钥","温度","思考模式","生成参数","保存配置","保存并应用配置"};
                foreach(var text in root.GetComponentsInChildren<TMP_Text>(true))if(Drawn(text))Assert.IsFalse(forbidden.Contains(text.text),phase+" rendered config label "+text.text);
                var actual=page==ModelSettingsPage.IndexMaintenance?(Component)Get(form,"indexState"):(Component)Get(form,"maintenanceTarget");
                Assert.IsTrue(actual.GetComponentsInChildren<Graphic>(true).Any(Drawn),phase+" missing actual content");
            }
            File.WriteAllText(Path.Combine(Evidence,(SceneManager.GetActiveScene().name=="CafeDemoV2"?"cafe":"title")+"-"+page+"-"+phase+".json"),new JObject{["title"]=heading.text,["navigation"]=nav.SelectedNavigation,["contentPath"]=PathOf(scroll.content),["fields"]=evidence}.ToString());
        }
        [UnityTest] public IEnumerator T1_Cafe_Ready()=>CheckRouting(true,false);
        [UnityTest] public IEnumerator T2_Title_Ready()=>CheckRouting(false,false);
        [UnityTest] public IEnumerator T3_Cafe_FirstOffline()=>CheckRouting(true,true);
        [UnityTest] public IEnumerator T4_Title_FirstOffline()=>CheckRouting(false,true);

        [UnityTest] public IEnumerator P3A_Title_SharedGroupsDropdownsAndResolutions()=>CheckAiPresentation(false);
        [UnityTest] public IEnumerator P3A_Cafe_SharedGroupsDropdownsAndResolutions()=>CheckAiPresentation(true);
        private static IEnumerator Reveal(Component control,ScrollRect scroll)
        {
            Canvas.ForceUpdateCanvases();
            var bounds=RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport,control.transform);
            float range=scroll.content.rect.height-scroll.viewport.rect.height;
            if(range>0)scroll.verticalNormalizedPosition=Mathf.Clamp01(scroll.verticalNormalizedPosition-(scroll.viewport.rect.center.y-bounds.center.y)/range);
            yield return null;
        }
        private static void ClickSelectable(Selectable target)
        {
            Hit(target);var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,
                position=ScreenRect(target.transform).center};
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(data,hits);
            ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,data,ExecuteEvents.pointerClickHandler);
        }
        private static IEnumerator CloseDropdown(TMP_Dropdown dropdown)
        {
            dropdown.Hide();
            yield return Wait(()=>!Object.FindObjectsByType<Transform>().Any(t=>t.name=="Dropdown List"));
        }
        private IEnumerator OpenAi(bool cafe)
        {
            if(cafe){yield return Escape();Click((Button)Get(Find<CafeUiFlowController>(),"settingsButton"));}
            else Click((Button)Get(Find<TitleScreenController>(),"settingsButton"));
            yield return null;yield return null;
            var form=cafe?Find<CafeDemoDeveloperPanel>().ModelForm:(FirstRunModelSetup)Get(Find<TitleSettingsHost>(),"form");
            yield return Wait(()=>!form.BlocksSettingsNavigation);yield return null;
            Click(NavButton("AiSettingsNav"));yield return null;
        }
        private IEnumerator CheckAiPresentation(bool cafe)
        {
            yield return OpenAi(cafe);
            var form=cafe?Find<CafeDemoDeveloperPanel>().ModelForm:Find<FirstRunModelSetup>();
            var scroll=((GameObject)Get(form,"panel")).GetComponentInChildren<ScrollRect>(true);
            var main=(TMP_InputField)Get(form,"mainModel");string original=main.text;
            foreach(int size in new[]{3,5})
            {
                Size(size);yield return null;yield return null;scroll.verticalNormalizedPosition=1;yield return null;
                Assert.AreEqual(size==3?1920:1280,Screen.width);
                foreach(string key in new[]{"mainConnectionGroup","recentDialogueGroup","vectorRetrievalGroup","aiStateGroup"})
                    Assert.IsTrue(((Transform)Get(form,key)).gameObject.activeInHierarchy,key);
                Assert.IsFalse(((GameObject)Get(form,"advancedSettings")).activeSelf);
                Assert.IsTrue(((TMP_InputField)Get(form,"mainBaseUrl")).gameObject.activeInHierarchy);
                Assert.IsFalse(((TMP_Text)Get(form,"mainProfileState")).gameObject.activeInHierarchy);
                Hit((Button)Get(form,"saveButton"));
                var titles=new JArray();
                foreach(string key in new[]{"mainSectionTitle","recentTitle","embeddingSectionTitle","aiStateTitle"})
                {
                    var text=(TMP_Text)Get(form,key);titles.Add(new JObject{["name"]=key,["text"]=text.text,["active"]=text.gameObject.activeInHierarchy,
                        ["enabled"]=text.enabled,["color"]=text.color.ToString(),["alpha"]=text.canvasRenderer.GetAlpha(),["cull"]=text.canvasRenderer.cull,
                        ["rect"]=ScreenRect(text.transform).ToString(),["characters"]=text.textInfo.characterCount,["font"]=text.font.name});
                }
                Assert.Greater(((TMP_Text)Get(form,"mainSectionTitle")).textInfo.characterCount,0,"Main group heading must actually render");
                Assert.Greater(((TMP_Text)Get(form,"recentTitle")).textInfo.characterCount,0,"Recent group heading must actually render");
                File.WriteAllText(Path.Combine(Evidence,(cafe?"cafe":"title")+"-headings-"+Screen.width+".json"),titles.ToString());
                yield return Capture((cafe?"cafe":"title")+"-ai-main");
                var provider=(TMP_Dropdown)Get(form,"mainSupplier");yield return Reveal(provider,scroll);ClickSelectable(provider);yield return new WaitForSecondsRealtime(.15f);
                Assert.IsTrue(provider.IsExpanded);Assert.That(provider.options[0].text,Does.Contain("Custom"));
                yield return Capture((cafe?"cafe":"title")+"-provider-list");yield return CloseDropdown(provider);
                var discover=(Button)Get(form,"discoverMainButton");yield return Reveal(discover,scroll);Click(discover);
                var candidates=(TMP_Dropdown)Get(form,"mainCandidates");yield return Wait(()=>candidates.options.Count==2&&!form.IsSavingSettings);
                Assert.IsTrue(candidates.options.All(o=>o.text!="v"));Assert.AreEqual(original,main.text);
                yield return Reveal(candidates,scroll);ClickSelectable(candidates);yield return new WaitForSecondsRealtime(.15f);Assert.IsTrue(candidates.IsExpanded);
                var labels=Object.FindObjectsByType<TMP_Text>().Where(t=>t.transform.GetComponentsInParent<Transform>().Any(p=>p.name=="Dropdown List")).ToArray();
                Assert.IsFalse(labels.Any(t=>t.text=="v"));
                foreach(string model in new[]{"model-p3a-a","model-p3a-b"})
                {
                    var label=labels.Single(t=>t.text==model);Assert.IsTrue(Drawn(label));
                    Assert.Greater(ScreenRect(label.transform).width,200,"Candidate must be readable, not a narrow arrow popup");
                    Hit(label.GetComponentInParent<Toggle>());
                }
                yield return Capture((cafe?"cafe":"title")+"-model-list");yield return CloseDropdown(candidates);
                var advanced=(Button)Get(form,"advancedToggle");yield return Reveal(advanced,scroll);Click(advanced);yield return null;
                var capacity=(TMP_InputField)Get(form,"contextLength");yield return Reveal(capacity,scroll);
                Hit((TMP_Dropdown)Get(form,"contextPresets"));yield return Capture((cafe?"cafe":"title")+"-generation");
                Assert.IsFalse(ScreenRect(capacity.textViewport).Overlaps(ScreenRect(((TMP_Dropdown)Get(form,"contextPresets")).transform)));
                yield return Reveal(advanced,scroll);Click(advanced);yield return null;
                scroll.verticalNormalizedPosition=0;yield return null;Hit((Button)Get(form,"saveButton"));yield return Capture((cafe?"cafe":"title")+"-embedding-status");
            }
            SettingsUiLanguage.SetEnglish(true);yield return null;scroll.verticalNormalizedPosition=1;yield return null;
            var englishCandidates=(TMP_Dropdown)Get(form,"mainCandidates");
            yield return Reveal(englishCandidates,scroll);ClickSelectable(englishCandidates);yield return new WaitForSecondsRealtime(.15f);
            Assert.IsFalse(Object.FindObjectsByType<TMP_Text>().Any(t=>t.text=="v"&&t.transform.GetComponentsInParent<Transform>().Any(p=>p.name=="Dropdown List")));
            yield return Capture((cafe?"cafe":"title")+"-english-model-list");yield return CloseDropdown(englishCandidates);
            SettingsUiLanguage.SetEnglish(false);
            var diagnostics=(Button)Get(form,"diagnosticsToggle");yield return Reveal(diagnostics,scroll);Click(diagnostics);yield return null;
            scroll.verticalNormalizedPosition=0;yield return null;
            Assert.IsTrue(((Transform)Get(form,"developerDiagnostics")).gameObject.activeInHierarchy);
            Assert.That(((TMP_Text)Get(form,"configurationDiagnostics")).text,Does.Contain("Revision:"));
            Hit((Button)Get(form,"recheckButton"));yield return Capture((cafe?"cafe":"title")+"-diagnostics");
            Assert.IsEmpty(Writes,"Layout/discovery cannot save or infer");
        }

        [UnityTest] public IEnumerator P3E_Cafe_LocalizationCameraAndSafeMaintenance()
        {
            var panel=Find<CafeDemoDeveloperPanel>();SettingsUiLanguage.SetEnglish(false);panel.Open();
            yield return Wait(()=>!panel.ModelForm.BlocksSettingsNavigation);
            Click(Button("NpcCharacterSettingsNav"));yield return null;
            var persona=Find<CafeNpcCharacterSettings>();var names=(TMP_Dropdown)Get(persona,"npcDropdown");
            var raw=(TMP_InputField)Get(persona,"displayName");string original=raw.text;
            raw.text="未保存原始姓名";string id=persona.SelectedCharacterId;
            SettingsUiLanguage.SetEnglish(true);yield return null;
            Assert.AreEqual("未保存原始姓名",raw.text);Assert.AreEqual(id,persona.SelectedCharacterId);
            Assert.IsTrue(names.options.Any(o=>o.text=="Lin Weiwei"));
            Assert.AreEqual("Custom",SettingsUiLanguage.NpcDisplayName("cafe.merchant","Custom"));
            Assert.AreEqual("unknown",SettingsUiLanguage.NpcDisplayName("unknown","unknown"));
            Assert.AreEqual("Emotes",((GameObject)Get(Find<PlayerEmotePanel>(),"hint")).transform.Find("Label").GetComponent<TMP_Text>().text);
            SettingsUiLanguage.SetEnglish(false);yield return null;
            Assert.IsTrue(names.options.Any(o=>o.text=="林薇薇"));raw.text=original;
            yield return Capture("persona-zh");
            panel.PlayerNavigation.ShowDisplay();yield return null;
            var toggle=Object.FindObjectsByType<Toggle>().Single(t=>t.name=="FreeCameraPermission");
            Assert.IsFalse(toggle.isOn);ClickSelectable(toggle);yield return null;
            var orbit=Find<CafeDemoOrbitCamera>();Assert.IsFalse(orbit.IsPresentationCameraActive);
            Assert.IsTrue((bool)Get(orbit,"enablePresentationCamera"));panel.Close();yield return null;
            typeof(CafeDemoOrbitCamera).GetMethod("EnterPresentationCamera",Private).Invoke(orbit,null);yield return null;
            Assert.IsTrue(orbit.IsPresentationCameraActive);orbit.SetPresentationCameraAllowed(false);
            Assert.IsFalse(orbit.IsPresentationCameraActive);Assert.IsFalse(orbit.IsPresentationUiHidden);
            DisplaySettings.SetFreeCameraEnabled(false);
            var dialogue=Find<DialogueController>();
            typeof(DialogueController).GetMethod("SetPresentationVisible",Private).Invoke(dialogue,new object[]{true});
            yield return null;Assert.IsFalse(((Button)Get(panel,"entryButton")).gameObject.activeSelf);
            dialogue.HideDialoguePresentation();yield return null;Assert.IsTrue(((Button)Get(panel,"entryButton")).gameObject.activeSelf);
            panel.Open();panel.PlayerNavigation.ShowDisplay();toggle.SetIsOnWithoutNotify(false);yield return null;
            SettingsUiLanguage.SetEnglish(true);yield return Capture("display-en");
            var form=panel.ModelForm;panel.PlayerNavigation.ShowAi(false);yield return null;
            var summary=(TMP_Text)Get(form,"ordinaryIndexState");Assert.IsTrue(summary.gameObject.activeInHierarchy);
            var maintain=(Button)Get(form,"ordinaryIndexAction");Assert.IsTrue(maintain.gameObject.activeInHierarchy);
            Assert.That(summary.text,Does.Contain("Memory Index Maintenance"));
            var scroll=((GameObject)Get(form,"panel")).GetComponentInChildren<ScrollRect>(true);
            yield return Reveal(maintain,scroll);
            var index=(EmbeddingIndexView)Get(form,"currentIndex");index.applied=false;
            typeof(FirstRunModelSetup).GetMethod("RefreshIndexState",Private).Invoke(form,null);
            Assert.IsFalse(maintain.interactable,"Inactive Embedding cannot write indexes");
            index.applied=true;index.cloud=true;index.consented=false;
            typeof(FirstRunModelSetup).GetMethod("RefreshIndexState",Private).Invoke(form,null);
            Click(maintain);yield return null;Assert.IsTrue(form.BlocksSettingsNavigation,"Cloud consent must precede any write");form.CancelSettingsConfirmation();
            panel.PlayerNavigation.ShowIndexMaintenance();yield return null;
            Assert.IsTrue(summary.gameObject.activeInHierarchy);Assert.IsTrue(((TMP_InputField)Get(form,"mainModel")).gameObject.activeInHierarchy);
            panel.ShowDebug();yield return null;
            Assert.IsNotNull(Object.FindObjectsByType<Toggle>().SingleOrDefault(t=>t.name=="HearingToggle"),"Hearing switch must exist in the live prefab, not only authoring Build()");
            var visualizer=Find<CafePerceptionDebugVisualizer>();var debug=Find<CafeNpcDebugPanel>();
            for(int i=0;i<persona.CandidateCount;i++)
            {
                persona.SelectCandidate(i);yield return null;visualizer.SetVisibility(true,true,true,true);yield return null;
                Assert.AreSame(persona.SelectedComposer.GetComponent<EmbodiedNPC.Perception.VisionSensor>(),Get(visualizer,"vision"));
                Assert.IsFalse(visualizer.IsVisionVisible,"Settings must cover debug overlays");
            }
            persona.SelectCandidate(0);panel.Close();panel.Close();yield return null;
            Assert.IsTrue(visualizer.IsVisionVisible,"Closing Settings restores the selected overlay");
            panel.Open();panel.ShowDebug();yield return null;
            Size(5);yield return null;yield return null;yield return Capture("debug-en-720");
            Assert.GreaterOrEqual(((TMP_Text)Get(debug,"selectedNpcText")).fontSize,21);
            SettingsUiLanguage.SetEnglish(false);panel.ShowKnowledge();yield return Wait(()=>!Find<CafeWorldKnowledgePanel>().IsBusy);
            yield return Capture("knowledge-zh-720");
            var knowledge=Find<CafeWorldKnowledgePanel>();var catalog=(KnowledgeCatalogView)Get(knowledge,"catalog");
            catalog.scopes.character_ids=new[]{"cafe.merchant","cafe.regular_customer","cafe.companion_customer","cafe.quiet_customer","unknown-role"};
            catalog.items[0].entry.access.character_ids=catalog.scopes.character_ids.ToArray();
            typeof(CafeWorldKnowledgePanel).GetMethod("BuildScopes",Private).Invoke(knowledge,new object[]{catalog.items[0]});
            SettingsUiLanguage.SetEnglish(true);yield return null;
            var scopeNames=((Transform)Get(knowledge,"scopes")).GetComponentsInChildren<TMP_Text>(true).Select(t=>t.text).ToArray();
            Assert.Contains("Lin Weiwei",scopeNames);Assert.Contains("Shen Zhiyi",scopeNames);Assert.Contains("Wen Yu'an",scopeNames);Assert.Contains("Jiang Yizhou",scopeNames);Assert.Contains("unknown-role",scopeNames);
            Assert.AreEqual("cafe.merchant",catalog.items[0].entry.access.character_ids[0]);
            Assert.IsEmpty(Writes);Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
        }

        [UnityTest] public IEnumerator P3D_Cafe_DraftsKnowledgeAndCompactSettings()
        {
            SettingsUiLanguage.SetEnglish(false);
            var panel=Find<CafeDemoDeveloperPanel>();panel.Open();yield return Wait(()=>!panel.ModelForm.BlocksSettingsNavigation);
            Click(NavButton("AiSettingsNav"));yield return null;
            var form=panel.ModelForm;var scroll=((GameObject)Get(form,"panel")).GetComponentInChildren<ScrollRect>(true);
            var model=(TMP_InputField)Get(form,"mainModel");var url=(TMP_InputField)Get(form,"mainBaseUrl");
            var key=(TMP_InputField)Get(form,"mainApiKey");var discover=(Button)Get(form,"discoverMainButton");
            int revision=((ModelProfilesView)Get(form,"profilesView")).revision;
            var active=((ModelProfilesView)Get(form,"profilesView")).active_runtime;
            model.text="";url.text="";yield return Reveal(discover,scroll);Click(discover);
            yield return Wait(()=>!form.IsSavingSettings);
            Assert.That(((TMP_Text)Get(form,"statusText")).text,Does.Contain("API 地址"));
            url.text="http://127.0.0.1:18846";key.text="synthetic-ui-only";
            Assert.IsTrue(discover.interactable,"No invalid Save needed");Click(discover);
            var candidates=(TMP_Dropdown)Get(form,"mainCandidates");
            yield return Wait(()=>!form.IsSavingSettings && candidates.options.Any(o=>o.text=="fixture-draft-a"));
            Assert.AreEqual("",model.text,"Discovery must not choose or save a model");
            Assert.AreEqual(revision,((ModelProfilesView)Get(form,"profilesView")).revision);
            Assert.AreSame(active,((ModelProfilesView)Get(form,"profilesView")).active_runtime);
            Assert.IsFalse(((Transform)Get(form,"developerDiagnostics")).gameObject.activeInHierarchy);
            Assert.IsFalse(((TMP_Text)Get(form,"mainCapacityState")).gameObject.activeInHierarchy);
            var status=(TMP_Text)Get(form,"statusText");Assert.GreaterOrEqual(status.fontSizeMin,20);
            yield return Capture("ai-footer");
            panel.PlayerNavigation.ShowModelDiagnostics();yield return null;
            Assert.AreEqual(ModelSettingsPage.Diagnostics,form.SettingsPage);
            Assert.IsTrue(((Transform)Get(form,"developerDiagnostics")).gameObject.activeInHierarchy);
            Assert.IsFalse(model.gameObject.activeInHierarchy);
            panel.PlayerNavigation.ShowAi(false);yield return null;Assert.AreEqual("",model.text);Assert.AreEqual("synthetic-ui-only",key.text);
            panel.ShowKnowledge();var knowledge=(CafeWorldKnowledgePanel)Get(panel,"knowledgeSettings");yield return Wait(()=>!knowledge.IsBusy);
            var content=(TMP_InputField)Get(knowledge,"contentInput");content.text="未保存正文 / draft body";
            var before=(KnowledgeEditView)typeof(CafeWorldKnowledgePanel).GetMethod("Edit",Private).Invoke(knowledge,null);
            foreach(bool english in new[]{false,true})
            {
                SettingsUiLanguage.SetEnglish(english);yield return null;Canvas.ForceUpdateCanvases();
                Assert.AreEqual(english?"World Knowledge":"世界知识",knowledge.transform.Find("KnowledgeTitle").GetComponent<TMP_Text>().text);
                Assert.AreEqual(english?"Setting":"设定",CafeWorldKnowledgePanel.TypeCaption("setting"));
                Assert.That(((Transform)Get(knowledge,"list")).GetComponentInChildren<Button>().GetComponentInChildren<TMP_Text>().text,Does.Contain(english?"Reference":"参考"));
                var after=(KnowledgeEditView)typeof(CafeWorldKnowledgePanel).GetMethod("Edit",Private).Invoke(knowledge,null);
                Assert.AreEqual(before.content,after.content);Assert.AreEqual(before.knowledge_type,after.knowledge_type);
                CollectionAssert.AreEqual(before.character_ids,after.character_ids);Assert.IsTrue(knowledge.IsDirty);
                yield return Capture(english?"knowledge-en":"knowledge-zh");
            }
            Set(knowledge,"dirty",false);
            foreach(int size in new[]{3,5})
            {
                Size(size);yield return null;yield return null;panel.PlayerNavigation.ShowGeneral();yield return null;
                Assert.That(panel.PlayerNavigation.GeneralPage.GetComponentsInChildren<TMP_Text>().Single(t=>t.text.Contains("Alt + Enter")).text,Does.Contain("Alt + Enter"));
                Hit(Button("OpenDiagnosticsFolder"));Hit(Button("ExportDiagnostics"));
                Assert.Less(ScreenRect(Button("ExportDiagnostics").transform).xMax,ScreenRect(Button("OpenDiagnosticsFolder").transform).xMin+1);
                yield return Capture("general");panel.PlayerNavigation.ShowDisplay();yield return null;
                var resolution=Button("DisplaySettingsNav");Hit(resolution);
                var dropdown=Object.FindObjectsByType<TMP_Dropdown>().Single(d=>d.name=="DisplayResolution");
                int width=Screen.width,height=Screen.height;dropdown.value=1;Assert.AreEqual(width,Screen.width);Assert.AreEqual(height,Screen.height);
                Hit(dropdown);yield return Capture("display");
                panel.PlayerNavigation.ShowAi(false);yield return null;scroll.verticalNormalizedPosition=1;yield return Capture("ai");Hit((Button)Get(form,"saveButton"));
                status.ForceMeshUpdate();Assert.IsFalse(status.isTextTruncated,"Footer feedback must remain readable");
            }
            Assert.IsEmpty(Writes);Assert.AreEqual(0,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
        }

        [UnityTest] public IEnumerator P3A_ConfigurationContractsFake()
        {
            yield return OpenAi(false);var form=Find<FirstRunModelSetup>();
            yield return Wait(()=>Get(form,"contextPolicyView")!=null);
            var save=(Button)Get(form,"saveButton");int before=Writes.Count;
            ((TMP_InputField)Get(form,"mainModel")).text="p3a-unsaved-model";
            ((TMP_Dropdown)Get(form,"recentMode")).value=1;((TMP_InputField)Get(form,"recentTarget")).text="32";
            File.WriteAllText(Path.Combine(Evidence,"mode.txt"),"contextfail");Click(save);
            yield return Wait(()=>Writes.Count>=before+2&&!form.IsSavingSettings);
            Assert.AreEqual("profiles",(string)Writes[before]["resource"]);Assert.AreEqual("context",(string)Writes[before+1]["resource"]);
            CollectionAssert.AreEquivalent(new[]{"main","embedding"},Writes[before]["slots"].Values<string>().ToArray());
            var profiles=(ModelProfilesView)Get(form,"profilesView");Assert.IsTrue(profiles.restart_required);
            Assert.AreNotEqual("p3a-unsaved-model",profiles.active_runtime.main_model.model);
            Assert.AreEqual("32",((TMP_InputField)Get(form,"recentTarget")).text);Assert.IsNull(Get(form,"profileModal"));
            File.WriteAllText(Path.Combine(Evidence,"mode.txt"),"ready");
            var close=Find<BackendWebSocketClient>().DisconnectAsync();while(!close.IsCompleted)yield return null;
            Find<BackendWebSocketClient>().EnsureConnected();yield return Wait(()=>(bool)Get(form,"contextPolicyConnected"));
            Click(save);yield return Wait(()=>Writes.Count>=before+3&&!form.IsSavingSettings);
            Assert.AreEqual("context",(string)Writes[before+2]["resource"]);
            foreach(string action in new[]{"replace","clear","retain"})
            {
                ((TMP_InputField)Get(form,"mainApiKey")).text=action=="replace"?"synthetic-only":"";Set(form,"mainClearKey",action=="clear");
                int count=Writes.Count;Click(save);yield return Wait(()=>Writes.Count>count&&!form.IsSavingSettings);
                Assert.AreEqual(action,(string)Writes[count]["key_actions"]["main"]);Assert.IsNull(Get(form,"profileModal"),"Editor cannot offer a fake automatic reopen");
            }
            File.WriteAllText(Path.Combine(Evidence,"P3A-contract-writes.json"),Writes.ToString());
        }
        private IEnumerator CheckRouting(bool cafe,bool offline)
        {
            CafeDemoDeveloperPanel cafeHost=cafe?Find<CafeDemoDeveloperPanel>():null;
            if(cafe)
            {
                if(!cafeHost.IsOpen){yield return Escape();yield return null;Click((Button)Get(Find<CafeUiFlowController>(),"settingsButton"));}
            }
            else {Click((Button)Get(Find<TitleScreenController>(),"settingsButton"));}
            yield return Wait(()=>!(cafe?cafeHost.ModelForm:Find<FirstRunModelSetup>()).BlocksSettingsNavigation);yield return null;
            var form=cafe?cafeHost.ModelForm:Find<FirstRunModelSetup>();var nav=cafe?cafeHost.PlayerNavigation:Find<TitleSettingsHost>().Navigation;
            if(!cafe){Assert.IsNull(Find<CafeNpcCharacterSettings>());Assert.IsNull(Find<CafeWorldKnowledgePanel>());}
            Click(NavButton("AiSettingsNav"));yield return null;
            Assert.AreEqual(!offline,form.HasProfileSettings,"First-start failure must not be replaced by a seeded profiles view");
            var main=(TMP_InputField)Get(form,"mainModel");main.text="routing-unsaved-draft";
            for(int round=0;round<3;round++)
            {
                nav.ShowIndexMaintenance();VisiblePage(form,nav,ModelSettingsPage.Configuration,"frame"+round);
                yield return new WaitForSecondsRealtime(2.3f);VisiblePage(form,nav,ModelSettingsPage.Configuration,"stable"+round);
                if(round==0)yield return Capture((cafe?"cafe":"title")+"-index"+(offline?"-offline":""));
                if(!offline){var index=(Button)Get(form,"indexAction");Assert.IsTrue(index.gameObject.activeInHierarchy);}
                Click(NavButton("DeveloperToolsEntry"));yield return null;
                Click(NavButton("ModelContractsNav"));VisiblePage(form,nav,ModelSettingsPage.Contracts,"frame"+round);
                typeof(FirstRunModelSetup).GetMethod("RefreshLanguage",Private).Invoke(form,null);yield return new WaitForSecondsRealtime(.2f);
                VisiblePage(form,nav,ModelSettingsPage.Contracts,"stable"+round);Assert.AreEqual("routing-unsaved-draft",main.text);
                if(round==0)yield return Capture((cafe?"cafe":"title")+"-contracts"+(offline?"-offline":""));
                var contract=(Button)Get(form,"contractTest");Hit(contract);
                if(offline){Assert.IsFalse(contract.interactable);Assert.IsTrue(((TMP_Text)Get(form,"maintenanceTarget")).text.Contains("不可用"));}
                else {Click(contract);yield return null;Assert.IsTrue(form.BlocksSettingsNavigation);var modal=(GameObject)Get(form,"profileModal");Click(modal.GetComponentsInChildren<Button>().Last());yield return null;Assert.IsFalse(form.BlocksSettingsNavigation);}
                yield return Escape();Assert.IsFalse(nav.IsDeveloper);Click(NavButton("AiSettingsNav"));yield return null;
                VisiblePage(form,nav,ModelSettingsPage.Configuration,"stable"+round);Assert.AreEqual("routing-unsaved-draft",main.text);Hit((Button)Get(form,"saveButton"));
                if(cafe&&round==0&&!offline)yield return Capture("cafe-ai");
            }
            // A loaded form also keeps the selected view after subsequent HTTP failure/poll refresh.
            if(!offline)
            {
                File.WriteAllText(Path.Combine(Evidence,"routing-mode.txt"),"offline");
                nav.ShowIndexMaintenance();yield return new WaitForSecondsRealtime(2.6f);
                VisiblePage(form,nav,ModelSettingsPage.Configuration,"disconnected");Click(NavButton("DeveloperToolsEntry"));yield return null;Click(NavButton("ModelContractsNav"));yield return null;
                VisiblePage(form,nav,ModelSettingsPage.Contracts,"disconnected");Assert.IsFalse(((Button)Get(form,"contractTest")).interactable);
            }
            Assert.IsEmpty(Writes,"Routing, cancel and maintenance reachability cannot write or infer");
        }
    }
}
