using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeTaskFeedbackTests
    {
        private static EmbodiedNPC.Cognition.CapacityCheckDto CapacityCheck(string state="error",char identity='a') => new()
        { condition_id=new string(identity,64),configuration_key=new string('c',64),state=state,cause="budget",proof=new string('d',64),
          window=16384,input_cap=13824,required_estimate=15000,input_estimate=15000,output_reserve=1024,estimation_reserve=1536,counts=new(),provider_verified=state!="error" };
        private static EmbodiedNPC.Cognition.CapacityStatusDto CapacityStatus(params EmbodiedNPC.Cognition.CapacityCheckDto[] checks) => new(){version=1,checks=checks};

        [Test] public void ContextCapacityCardsPersistDeduplicateAndDismissOnlyWarning()
        {
            var prefab=Resources.Load<GameObject>(CafeUiFlowController.MenuResource);
            var instance=Object.Instantiate(prefab);
            string root=Path.Combine(Path.GetTempPath(),"CapacityUi-"+Guid.NewGuid().ToString("N"),"UserData");
            var preferences=new PortablePreferenceFile(PortablePathSet.Open(root,Path.Combine(Path.GetDirectoryName(root),"UnityPlayer","EmbodiedNPC_Data")));
            var state=new EmbodiedNPC.Cognition.ContextCapacityState();
            var error=CapacityCheck();
            try
            {
                var flow=instance.GetComponent<CafeUiFlowController>();
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                typeof(CafeUiFlowController).GetMethod("Awake",flags).Invoke(flow,null);
                typeof(CafeUiFlowController).GetField("capacityState",flags).SetValue(flow,state);
                typeof(CafeUiFlowController).GetField("capacityPreferences",flags).SetValue(flow,preferences);
                state.Observe(CapacityStatus(error));flow.RefreshCapacityFeedback();
                var cards=instance.GetComponentsInChildren<RectTransform>(true);
                var hard=cards.Single(c=>c.name=="ContextCapacityError");
                Assert.IsTrue(hard.gameObject.activeSelf);Assert.IsEmpty(hard.GetComponentsInChildren<Button>(true));
                state.Observe(CapacityStatus(error));flow.RefreshCapacityFeedback();
                Assert.AreEqual(1,instance.GetComponentsInChildren<RectTransform>(true).Count(c=>c.name=="ContextCapacityError"));
                Assert.IsTrue(hard.gameObject.activeSelf);
                state.Observe(CapacityStatus(CapacityCheck("warning",'b')));
                state.Rechecked(new EmbodiedNPC.Cognition.CapacityRecheckResult{version=1,checks=new[]{new EmbodiedNPC.Cognition.CapacityRecheckDto{condition_id=error.condition_id,proof=error.proof,resolved=true}}});
                flow.RefreshCapacityFeedback();Assert.IsFalse(hard.gameObject.activeSelf);
                var warning=cards.Single(c=>c.name=="ContextCapacityWarning");Assert.IsTrue(warning.gameObject.activeSelf);
                warning.GetComponentInChildren<Button>(true).onClick.Invoke();Assert.IsFalse(warning.gameObject.activeSelf);
                Assert.IsTrue(new PortablePreferenceFile(PortablePathSet.Open(root,Path.Combine(Path.GetDirectoryName(root),"UnityPlayer","EmbodiedNPC_Data"))).ContextWarningDismissed);
                state.Observe(CapacityStatus(error));flow.RefreshCapacityFeedback();
                Assert.IsTrue(hard.gameObject.activeSelf,"Warning opt-out must not suppress a hard error.");
            }
            finally
            {
                var flow=instance.GetComponent<CafeUiFlowController>();
                var field=typeof(CafeUiFlowController).GetField("runtimeFont",BindingFlags.Instance|BindingFlags.NonPublic);
                var font=field.GetValue(flow) as TMP_FontAsset;
                if(font!=null){foreach(var texture in font.atlasTextures)if(texture!=null)Object.DestroyImmediate(texture);
                    if(font.material!=null)Object.DestroyImmediate(font.material);Object.DestroyImmediate(font);field.SetValue(flow,null);}
                Object.DestroyImmediate(instance);
            }
        }
        [Test] public void TaskMenuControlsAreConditionalAndPendingIsNotCancellationSuccess()
        {
            var prefab=Resources.Load<GameObject>(CafeUiFlowController.MenuResource);
            var instance=Object.Instantiate(prefab);
            try
            {
                var flow=instance.GetComponent<CafeUiFlowController>();
                typeof(CafeUiFlowController).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(flow,null);
                flow.RefreshTaskFeedback();
                var cancel=instance.GetComponentsInChildren<Button>(true).Single(b=>b.name=="CancelPlayerCommission");
                Assert.That(cancel.gameObject.activeSelf,Is.False);
                Assert.That(CafeUiFlowController.TaskFeedbackText("saved"),Is.Empty);
                foreach(string code in new[]{"save_failed","cancel_pending","needs_verification","result_pending","failed"})
                    Assert.That(CafeUiFlowController.TaskFeedbackText(code),Is.Not.Empty,code);
                Assert.That(CafeUiFlowController.TaskFeedbackText("cancel_pending"),Is.EqualTo(SettingsUiLanguage.T(
                    "取消请求处理中，尚未确认停止。","Cancellation pending; stopping is not yet confirmed.")));
                Assert.That(CafeUiFlowController.TaskFeedbackText("cancel_pending"),Is.Not.EqualTo(CafeUiFlowController.TaskFeedbackText("cancelled")));
                flow.OpenMenu();Assert.That(flow.IsMenuOpen,Is.False,"Unbound menu does not acquire player controls.");
            }
            finally
            {
                var flow=instance.GetComponent<CafeUiFlowController>();
                var field=typeof(CafeUiFlowController).GetField("runtimeFont",BindingFlags.Instance|BindingFlags.NonPublic);
                var font=field.GetValue(flow) as TMP_FontAsset;
                if(font!=null) { foreach(var texture in font.atlasTextures) if(texture!=null) Object.DestroyImmediate(texture);
                    if(font.material!=null) Object.DestroyImmediate(font.material);Object.DestroyImmediate(font);field.SetValue(flow,null); }
                Object.DestroyImmediate(instance);
            }
        }
    }
    // Real scenes/components, loopback-only P1 fixture. No production config or model calls.
    public sealed class CafeUiFlowP2ATests
    {
        private const string Session = "CafeUiP2A.Tests.";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string Evidence => Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/P2A"));
        private Keyboard keyboard;
        private Mouse mouse;
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static Button Button(string name) => Object.FindObjectsByType<Button>(FindObjectsInactive.Include).Single(b => b.name == name);
        private static void Mode(string value) => File.WriteAllText(Path.Combine(Evidence, "mode.txt"), value);
        private static void Patch() => Set(Find<BackendWebSocketClient>(), "serverUrl", "ws://127.0.0.1:18846/ws");
        private static void PatchCafe(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "CafeDemoV2") return;
            Patch();
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>())
            {
                // OnEnable precedes sceneLoaded. Keep the exact newly created trace path as evidence;
                // teardown/runner can relocate only this test's file without touching other traces.
                if (!string.IsNullOrEmpty(trace.CurrentTracePath)) File.AppendAllLines(Path.Combine(Evidence,"native-traces.txt"), new[]{trace.CurrentTracePath});
                trace.enabled = false;
            }
        }
        [UnitySetUp] public IEnumerator Setup()
        {
            if (!File.Exists(Path.Combine(Evidence,"fake-ready.txt"))) Assert.Ignore("Start SettingsP1FakeService.py with Temp/P2A first.");
            SessionState.SetString(Session+"scene", SceneManager.GetActiveScene().path);
            SessionState.SetBool(Session+"hadVolume",PlayerPrefs.HasKey(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetFloat(Session+"volume",PlayerPrefs.GetFloat(CafeBackgroundMusic.VolumePreferenceKey));
            SessionState.SetBool(Session+"hadLanguage",PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));
            SessionState.SetInt(Session+"language",PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language"));
            var gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            var window = EditorWindow.GetWindow(gameView);
            var size = gameView.GetProperty("selectedSizeIndex", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            SessionState.SetInt(Session+"size",(int)size.GetValue(window)); size.SetValue(window,3);
            string name = TestContext.CurrentContext.Test.MethodName;
            Mode(name.Contains("StartupBlocked") ? "unconfigured" : "ready");
            using (var request=UnityWebRequest.Get("http://127.0.0.1:18846/fixture/reset")) yield return request.SendWebRequest();
            EditorSceneManager.OpenScene(name.Contains("Title") ? "Assets/Scenes/TitleScene.unity" : "Assets/Scenes/CafeDemoV2.unity");
            Patch(); foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>()) trace.enabled=false;
            yield return new EnterPlayMode();
            SceneManager.sceneLoaded += PatchCafe;
            keyboard=InputSystem.AddDevice<Keyboard>(); mouse=InputSystem.AddDevice<Mouse>();
            yield return null;
            if (SceneManager.GetActiveScene().name=="TitleScene") yield return Wait(()=>Find<TitleScreenController>().CanEnter);
            else if (TestContext.CurrentContext.Test.MethodName.Contains("StartupBlocked")) yield return Wait(()=>Find<CafeDemoDeveloperPanel>().IsStartupAdmissionHeld && Find<CafeDemoDeveloperPanel>().IsOpen);
            else yield return Wait(()=>Find<DialogueController>().IsSetupReady);
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded -= PatchCafe;
            if(keyboard!=null)InputSystem.RemoveDevice(keyboard); if(mouse!=null)InputSystem.RemoveDevice(mouse);
            if(Application.isPlaying)
            {
                foreach(var backend in Object.FindObjectsByType<BackendWebSocketClient>())
                {var close=backend.DisconnectAsync(); while(!close.IsCompleted)yield return null;}
                yield return new ExitPlayMode();
            }
            if(SessionState.GetBool(Session+"hadVolume",false))PlayerPrefs.SetFloat(CafeBackgroundMusic.VolumePreferenceKey,SessionState.GetFloat(Session+"volume",.5f));
            else PlayerPrefs.DeleteKey(CafeBackgroundMusic.VolumePreferenceKey);
            SettingsUiLanguage.SetEnglish(SessionState.GetInt(Session+"language",0)==1);
            if(!SessionState.GetBool(Session+"hadLanguage",false))PlayerPrefs.DeleteKey("EmbodiedNPC.Settings.Language");
            PlayerPrefs.Save(); Mode("ready");
            yield return null;
            var type=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            type.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(EditorWindow.GetWindow(type),SessionState.GetInt(Session+"size",5));
            var previous=SessionState.GetString(Session+"scene","Assets/Scenes/CafeDemoV2.unity");
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous)?"Assets/Scenes/CafeDemoV2.unity":previous);
        }
        private static IEnumerator Wait(Func<bool> condition,float timeout=12)
        {float end=Time.realtimeSinceStartup+timeout;while(!condition()&&Time.realtimeSinceStartup<end)yield return null;Assert.IsTrue(condition(),"P2A timeout, scene="+SceneManager.GetActiveScene().name);}
        private void Keys(params Key[] keys) {InputSystem.QueueStateEvent(keyboard,new KeyboardState(keys));InputSystem.Update();}
        private IEnumerator Escape()
        {
            Keys(Key.Escape);
            var title=Find<TitleScreenController>(); if(title!=null)title.SendMessage("Update");
            yield return null; Keys(); yield return null;
        }
        private static void Hit(Button button)
        {
            Canvas.ForceUpdateCanvases();var canvas=button.GetComponentInParent<Canvas>();
            var camera=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
            var pointer=new PointerEventData(EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(camera,button.transform.position)};
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(pointer,hits);
            Assert.IsNotEmpty(hits,"No hit for "+button.name+" position="+pointer.position+" rect="+((RectTransform)button.transform).rect);
            Assert.AreSame(button,hits[0].gameObject.GetComponentInParent<Button>(),"Top hit="+hits[0].gameObject.name);
        }
        private static IEnumerator Capture(string name)
        {yield return null; Assert.AreEqual(1920,Screen.width);Assert.AreEqual(1080,Screen.height);ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,name+".png"));yield return new WaitForSecondsRealtime(.25f);}

        [UnityTest] public IEnumerator T1_MenuHierarchy_InputOwnership_ConfirmationGuards()
        {
            var flow=Find<CafeUiFlowController>();var player=Find<CafeDemoPlayerController>();var orbit=Find<CafeDemoOrbitCamera>();var settings=Find<CafeDemoDeveloperPanel>();
            bool pointerBeforeMenu=orbit.PointerCaptured;
            Assert.IsNotNull(flow);Assert.IsNull(Find<InputSystemUIInputModule>().cancel,"Single Cancel owner in Cafe");
            Keys(Key.W);yield return new WaitForSecondsRealtime(.12f);
            yield return Escape();Assert.IsTrue(flow.IsMenuOpen);Assert.IsTrue(player.IsInputSuspended);Assert.IsTrue(orbit.IsInputSuspended);Assert.IsFalse(orbit.PointerCaptured);
            Assert.AreEqual(CursorLockMode.None,Cursor.lockState);Assert.IsTrue(Cursor.visible);
            Hit(Button("Resume"));yield return Capture("T1-menu-1920");
            var before=player.transform.position;float yaw=orbit.Yaw, pitch=orbit.Pitch, distance=orbit.DesiredDistance;
            bool presentation=orbit.IsPresentationCameraActive, hidden=orbit.IsPresentationUiHidden;
            int sends=Find<BackendWebSocketClient>().CognitiveRequestSendCount;float worldTime=Time.time;
            Keys(Key.W,Key.A,Key.E,Key.G,Key.Tab,Key.H);Find<PlayerEmotePanel>().SendMessage("Update");orbit.SendMessage("Update");
            InputSystem.QueueStateEvent(mouse,new MouseState{delta=new Vector2(120,100),scroll=new Vector2(0,2),buttons=2});InputSystem.Update();
            yield return new WaitForSecondsRealtime(.2f);
            Assert.Less(Vector2.Distance(new Vector2(before.x,before.z),new Vector2(player.transform.position.x,player.transform.position.z)),.01f);
            Assert.AreEqual(yaw,orbit.Yaw);Assert.AreEqual(pitch,orbit.Pitch);Assert.AreEqual(distance,orbit.DesiredDistance);
            Assert.AreEqual(presentation,orbit.IsPresentationCameraActive);Assert.AreEqual(hidden,orbit.IsPresentationUiHidden);
            Assert.IsFalse(Find<PlayerEmotePanel>().IsOpen);Assert.IsFalse(Find<DialogueController>().IsPresentationVisible);
            Assert.Greater(Time.time,worldTime);Assert.AreEqual(1,Time.timeScale);Assert.IsTrue(Find<CafeBackgroundMusic>().GetComponent<AudioSource>().isPlaying);
            Assert.IsTrue(Find<BackendWebSocketClient>().IsConnected);Assert.AreEqual(sends,Find<BackendWebSocketClient>().CognitiveRequestSendCount);
            Keys();InputSystem.QueueStateEvent(mouse,new MouseState());InputSystem.Update();yield return null;
            flow.OpenHelp();Assert.IsTrue(flow.IsHelpOpen);yield return Escape();Assert.IsTrue(flow.IsMenuOpen);Assert.IsFalse(flow.IsHelpOpen);
            flow.OpenSettings();yield return Wait(()=>!settings.ModelForm.BlocksSettingsNavigation);yield return null;
            Assert.IsTrue(settings.IsOpen);yield return Capture("T1-settings-above-menu-1920");Hit(Button("GeneralSettingsNav"));
            Assert.Greater(((GameObject)Get(settings,"panelRoot")).GetComponent<Canvas>().sortingOrder,flow.GetComponent<Canvas>().sortingOrder);
            var dropdown=settings.PlayerNavigation.GeneralPage.GetComponentInChildren<TMP_Dropdown>();dropdown.Show();yield return null;
            Canvas.ForceUpdateCanvases();var option=dropdown.GetComponentsInChildren<Toggle>().Last();
            var dropdownHits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current)
                {position=RectTransformUtility.WorldToScreenPoint(null,option.transform.position)},dropdownHits);
            Assert.IsNotEmpty(dropdownHits);Assert.AreSame(option,dropdownHits[0].gameObject.GetComponentInParent<Toggle>(),"Dropdown must remain above menu and Settings");
            Assert.IsTrue(dropdown.IsExpanded);yield return Escape();yield return Wait(()=>!dropdown.IsExpanded);
            Assert.IsTrue(settings.IsOpen);
            Button("DeveloperToolsEntry").onClick.Invoke();Assert.IsTrue(settings.PlayerNavigation.IsDeveloper);
            Button("ModelMaintenanceNav").onClick.Invoke();yield return Wait(()=>!settings.ModelForm.BlocksSettingsNavigation);
            ((Button)Get(settings.ModelForm,"contractTest")).onClick.Invoke();Assert.IsTrue(settings.ModelForm.BlocksSettingsNavigation);
            var modal=(GameObject)Get(settings.ModelForm,"profileModal");yield return null;Hit(modal.GetComponentsInChildren<Button>().Last());
            Set(settings.ModelForm,"busy",true);yield return Escape();Assert.IsTrue(settings.ModelForm.BlocksSettingsNavigation);Assert.IsTrue(settings.IsOpen);
            Set(settings.ModelForm,"busy",false);yield return Escape();Assert.IsFalse(settings.ModelForm.BlocksSettingsNavigation);Assert.IsTrue(settings.PlayerNavigation.IsDeveloper);
            Button("DeveloperTool2").onClick.Invoke();yield return null;
            var knowledge=Find<CafeWorldKnowledgePanel>();yield return Wait(()=>!knowledge.IsBusy);Set(knowledge,"dirty",true);
            yield return Escape();Assert.IsTrue(knowledge.ConfirmationVisible);Assert.IsTrue(settings.PlayerNavigation.IsDeveloper);
            yield return Escape();Assert.IsFalse(knowledge.ConfirmationVisible);Assert.IsTrue((bool)Get(knowledge,"dirty"));
            // End this synthetic draft without issuing Save/Discard HTTP; verify the next ESC layer.
            Set(knowledge,"dirty",false);yield return Escape();Assert.IsFalse(settings.PlayerNavigation.IsDeveloper);Assert.IsTrue(settings.IsOpen);
            yield return Escape();Assert.IsFalse(settings.IsOpen);Assert.IsTrue(flow.IsMenuOpen);Assert.IsTrue(player.IsInputSuspended);
            flow.SendMessage("OnApplicationFocus",false);flow.SendMessage("OnApplicationFocus",true);Assert.IsTrue(player.IsInputSuspended);Assert.IsFalse(orbit.PointerCaptured);
            Keys(Key.W);yield return null;Button("Resume").onClick.Invoke();Assert.IsFalse(flow.IsMenuOpen);Assert.IsFalse(player.IsInputSuspended);
            Assert.AreEqual(pointerBeforeMenu,orbit.PointerCaptured,"Restore the original pointer owner after menu focus changes");
            before=player.transform.position;yield return new WaitForSecondsRealtime(.15f);
            Assert.Less(Vector2.Distance(new Vector2(before.x,before.z),new Vector2(player.transform.position.x,player.transform.position.z)),.01f,"Held move must release before locomotion resumes");
            Keys();yield return null;Keys(Key.W);yield return new WaitForSecondsRealtime(.15f);Keys();
            Assert.Greater(Vector3.Distance(before,player.transform.position),.015f);
            yield return null;yield return Escape();Assert.IsTrue(flow.IsMenuOpen);flow.RequestCancel();Assert.IsFalse(flow.IsMenuOpen);flow.RequestCancel();Assert.IsFalse(flow.IsMenuOpen,"One frame cannot reopen");
            yield return null;settings.Open();yield return Wait(()=>!settings.ModelForm.BlocksSettingsNavigation);yield return Escape();
            Assert.IsFalse(settings.IsOpen);Assert.IsFalse(flow.IsMenuOpen);Assert.IsFalse(player.IsInputSuspended);
        }

        [UnityTest] public IEnumerator T2_DialogueHistory_DraftFocus_EmoteAndImeRouting()
        {
            var player=Find<CafeDemoPlayerController>();var orbit=Find<CafeDemoOrbitCamera>();var flow=Find<CafeUiFlowController>();
            var npc=Object.FindObjectsByType<DialogueParticipant>().First(x=>x.Entity.EntityId=="npc_cafe_regular_01");
            var body=player.GetComponent<CharacterController>();body.enabled=false;player.transform.position=npc.transform.position+Vector3.back*1.2f;body.enabled=true;
            Set(orbit,"yaw",0f);Set(orbit,"pitch",12f);yield return null;yield return null;
            var interactor=player.GetComponent<PlayerDialogueInteractor>();Assert.IsTrue(interactor.TryBeginDialogue());yield return null;
            var dialogue=Find<DialogueController>();var input=dialogue.DialogueInputField;input.text="未发送的中文草稿";input.Select();input.ActivateInputField();yield return null;
            var context=Get(dialogue,"activeContext");var settings=Find<CafeDemoDeveloperPanel>();settings.Open();yield return Wait(()=>!settings.ModelForm.BlocksSettingsNavigation);
            yield return Escape();Assert.IsFalse(settings.IsOpen);Assert.IsTrue(dialogue.IsPresentationVisible);Assert.AreSame(context,Get(dialogue,"activeContext"));
            Assert.AreEqual("未发送的中文草稿",input.text);Assert.IsTrue(input.isFocused);Assert.IsFalse(flow.IsMenuOpen);
            var history=Find<DialogueHistoryPanel>();history.SendMessage("Toggle");Assert.IsTrue(history.IsOpen);
            yield return Escape();Assert.IsFalse(history.IsOpen);Assert.IsTrue(dialogue.IsPresentationVisible);Assert.AreSame(context,Get(dialogue,"activeContext"));
            // Deterministic router seam: real Windows IME composition requires manual acceptance.
            Set(flow,"composition","中文");yield return Escape();Assert.IsTrue(dialogue.IsPresentationVisible);Assert.AreEqual("未发送的中文草稿",input.text);
            Set(flow,"composition","");yield return Escape();Assert.IsFalse(dialogue.IsPresentationVisible);Assert.AreSame(context,Get(dialogue,"activeContext"));Assert.IsFalse(flow.IsMenuOpen);
            yield return Escape();Assert.IsTrue(flow.IsMenuOpen);yield return Escape();Assert.IsFalse(flow.IsMenuOpen);
            Keys(Key.G);Find<PlayerEmotePanel>().SendMessage("Update");yield return null;Keys();yield return null;
            var emotes=Find<PlayerEmotePanel>();Assert.IsTrue(emotes.IsOpen);yield return Escape();Assert.IsFalse(emotes.IsOpen);Assert.IsFalse(emotes.IsBusy);Assert.IsFalse(flow.IsMenuOpen);
            // Read-only busy state seam: do not play/publish a social action just to test Cancel.
            var action=Find<PlayerEmoteController>();action.enabled=false;Set(action,"<IsActive>k__BackingField",true);
            typeof(PlayerEmotePanel).GetMethod("OnActivityChanged",Private).Invoke(emotes,new object[]{true});
            yield return Escape();Assert.IsFalse(flow.IsMenuOpen);Assert.IsTrue(player.IsInputSuspended);Assert.IsTrue(action.IsActive);
            Set(action,"<IsActive>k__BackingField",false);typeof(PlayerEmotePanel).GetMethod("OnActivityChanged",Private).Invoke(emotes,new object[]{false});action.enabled=true;
            yield return Escape();Assert.IsTrue(flow.IsMenuOpen);yield return Escape();Assert.IsFalse(flow.IsMenuOpen);Assert.IsFalse(player.IsInputSuspended);
            Assert.IsTrue(Find<CafeBackgroundMusic>().GetComponent<AudioSource>().isPlaying);
        }

        [UnityTest] public IEnumerator T3a_TitleCancel_NativeEnterCafe_Menu()
        {
            Assert.IsNull(Find<CafeUiFlowController>());Button("Instructions").onClick.Invoke();yield return Escape();Assert.IsNull(GameObject.Find("InstructionsModal"));
            var title=Find<TitleScreenController>();title.OpenSettings();yield return Wait(()=>!Find<TitleSettingsHost>().IsBusy);yield return Escape();Assert.IsFalse(Find<TitleSettingsHost>().IsOpen);
            yield return Escape();Assert.AreEqual("TitleScene",SceneManager.GetActiveScene().name);
            Button("EnterCafe").onClick.Invoke();yield return Wait(()=>SceneManager.GetActiveScene().name=="CafeDemoV2",25);yield return Wait(()=>Find<DialogueController>().IsSetupReady);
            Assert.AreEqual(1,SceneManager.sceneCount);Assert.AreEqual(1,Object.FindObjectsByType<CafeUiFlowController>().Length);
            yield return Escape();Assert.IsTrue(Find<CafeUiFlowController>().IsMenuOpen);yield return Escape();Assert.IsFalse(Find<CafeDemoPlayerController>().IsInputSuspended);
            Assert.IsTrue(Find<BackendWebSocketClient>().IsConnected);Assert.IsTrue(Find<CafeBackgroundMusic>().GetComponent<AudioSource>().isPlaying);
        }

        [UnityTest] public IEnumerator T3b_StartupBlocked_NoMenuAdmissionBypass()
        {
            var settings=Find<CafeDemoDeveloperPanel>();yield return Wait(()=>!settings.ModelForm.BlocksSettingsNavigation);
            Assert.IsTrue(settings.IsStartupAdmissionHeld);Assert.IsFalse(Find<DialogueController>().IsSetupReady);
            yield return Escape();Assert.IsFalse(settings.IsOpen);Assert.IsTrue(Find<CafeDemoPlayerController>().IsInputSuspended);
            var flow=Find<CafeUiFlowController>();flow.OpenMenu();flow.Resume();Assert.IsFalse(flow.IsMenuOpen);
            yield return Escape();Assert.IsTrue(settings.IsOpen);Assert.IsFalse(flow.IsMenuOpen);Assert.IsFalse(Find<DialogueController>().IsSetupReady);
            Assert.IsTrue(Find<CafeDemoOrbitCamera>().IsInputSuspended);Assert.IsFalse(Find<CafeDemoOrbitCamera>().PointerCaptured);
        }
    }
}
