using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using TMPro;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // Opt-in UI smoke using the existing ACK-only TitleP0FakeService on loopback 18846.
    public sealed class NpcWaitingBubbleTests
    {
        private static string Evidence => TestContext.CurrentContext.Test.MethodName.StartsWith("P2_")
            ? "D:/EmbodiedNPC-PlayerExperienceP2-20261017/evidence" : TestContext.CurrentContext.Test.MethodName.StartsWith("UX_")
            ? "D:/EmbodiedNPC-PlayerUXP1-20261017/evidence" : TestContext.CurrentContext.Test.MethodName.StartsWith("P3B_")
            ? "D:/EmbodiedNPC-LocalizationP3B-20261017/evidence" : "D:/EmbodiedNPC-WaitingBubbleP1-20261017/evidence";
        private const string Session = "NpcWaitingBubble.Tests.";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private BackendWebSocketClient backend;
        private DialogueController dialogue;
        private TextUtteranceInput input;
        private DialogueBubbleManager bubbles;
        private WorldEntity npc, player;
        private CognitiveRunDriver driver;
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>();
        private static object Get(object target, string key) => target.GetType().GetField(key, Private).GetValue(target);
        private static void Set(object target, string key, object value) => target.GetType().GetField(key, Private).SetValue(target, value);
        private static IEnumerator Wait(Func<bool> ready)
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(ready(), "Waiting bubble UI timeout");
        }
        private static PropertyInfo SizeProperty => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")
            .GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static EditorWindow GameView => EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));

        [UnitySetUp] public IEnumerator Setup()
        {
            if (!File.Exists(Path.Combine(Evidence, "fake-ready.txt"))) Assert.Ignore("Opt-in ACK-only fixture is not running.");
            Assert.IsFalse(EditorApplication.isPlaying);
            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
            SessionState.SetString(Session + "scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            SessionState.SetInt(Session + "size", (int)SizeProperty.GetValue(GameView));
            if (TestContext.CurrentContext.Test.MethodName.StartsWith("P3B_"))
            {
                SessionState.SetBool(Session + "hadLanguage", PlayerPrefs.HasKey("EmbodiedNPC.Settings.Language"));
                SessionState.SetInt(Session + "language", PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language", 0));
                SettingsUiLanguage.SetEnglish(false);
            }
            SizeProperty.SetValue(GameView, 3); // Existing 1920x1080 Game View entry.
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            Set(Find<BackendWebSocketClient>(), "serverUrl", "ws://127.0.0.1:18846/ws");
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>()) trace.enabled = false;
            foreach (var run in Object.FindObjectsByType<CognitiveRunDriver>()) run.enabled = false;
            yield return new EnterPlayMode();
            backend = Find<BackendWebSocketClient>(); dialogue = Find<DialogueController>();
            input = Find<TextUtteranceInput>(); bubbles = Find<DialogueBubbleManager>();
            npc = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "npc_cafe_01");
            player = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "player_01");
            driver = npc.GetComponent<CognitiveRunDriver>();
            foreach (var composer in Object.FindObjectsByType<CognitiveContextComposer>())
                Set(composer, "characterOverrides", new CharacterOverrideStore(Path.Combine(Evidence, "persona.json")));
            var template = (DialogueBubbleView)Get(bubbles, "bubbleTemplate");
            var font = TMP_FontAsset.CreateFontAsset(((TMP_Text)Get(template, "messageText")).font.sourceFontFile);
            font.isMultiAtlasTexturesEnabled = true;
            foreach (var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None)) text.font = font;
            if(TestContext.CurrentContext.Test.MethodName.StartsWith("P2_"))
            {
                // Font warm-up can consume the startup HTTP owner's original six-second window.
                // Retry that same real Fake HTTP path; never set setupReady or bypass admission.
                typeof(CafeDemoDeveloperPanel).GetMethod("RecheckStartup",Private).Invoke(Find<CafeDemoDeveloperPanel>(),null);
                SizeProperty.SetValue(GameView,3); GameView.Focus(); GameView.Repaint();
            }
            yield return Wait(() => backend.IsConnected && dialogue.IsSetupReady && backend.PendingExperienceCount == 0);
            Find<CafeDemoDeveloperPanel>().Close();
            driver.enabled = true;
            Select(npc);
            Find<CafeDemoOrbitCamera>().enabled = false; // Fixed capture camera, no scene/prefab edits.
            var anchor = npc.GetComponent<DialogueBubbleAnchor>();
            Camera.main.transform.position = npc.transform.position + npc.transform.forward * 3.4f - Vector3.right * 1.3f + Vector3.up * 1.6f;
            Camera.main.transform.LookAt(anchor.HeadPosition + Vector3.up * .25f);
            yield return null; yield return null;
            bubbles.ClearMessages();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying)
            {
                dialogue?.ClearSession();
                if (TestContext.CurrentContext.Test.MethodName.StartsWith("P3B_"))
                {
                    SettingsUiLanguage.SetEnglish(SessionState.GetInt(Session + "language", 0) == 1);
                    if (!SessionState.GetBool(Session + "hadLanguage", false)) PlayerPrefs.DeleteKey("EmbodiedNPC.Settings.Language");
                    PlayerPrefs.Save();
                }
                yield return Wait(() => backend.PendingExperienceCount == 0);
                var close = backend.DisconnectAsync(); while (!close.IsCompleted) yield return null;
                yield return new ExitPlayMode();
            }
            SizeProperty.SetValue(GameView, SessionState.GetInt(Session + "size", 0));
            string previous = SessionState.GetString(Session + "scene", "");
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previous) ? "Assets/Scenes/CafeDemoV2.unity" : previous);
        }
        private void Select(WorldEntity target)
        {
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
            player.transform.position = target.transform.position + target.transform.forward * 1.4f;
            controller.enabled = true; Physics.SyncTransforms();
            Assert.IsTrue(dialogue.SelectTarget(target.GetComponent<DialogueParticipant>()));
        }
        private DialogueBubbleView WaitingView() => bubbles.GetComponentsInChildren<DialogueBubbleView>()
            .SingleOrDefault(v => v.Message.SpeakerEntityId == npc.EntityId && v.Message.Text == "...");
        private string Status => ((TMP_Text)Get(dialogue, "statusText")).text;
        private CognitiveRequest Submit()
        {
            Assert.IsTrue(input.SubmitText("隔离等待气泡测试"));
            Assert.IsNotNull(driver.PendingRequest, Status);
            Assert.IsNotNull(WaitingView(), "An accepted request needs its NPC waiting bubble.");
            return driver.PendingRequest;
        }
        private static string Reply(CognitiveRequest request) => JsonConvert.SerializeObject(new CognitiveResponse {
            message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
            trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = request.run.agent_entity_id,
            decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = "你好，这是隔离测试回复。", dialogue_turn_policy = "WAIT_FOR_PLAYER" }
        });
        private static IEnumerator Capture(string name)
        {
            yield return null; ScreenCapture.CaptureScreenshot(Path.Combine(Evidence, name + "-1920.png"));
            yield return new WaitForSecondsRealtime(.2f);
        }

        [UnityTest] public IEnumerator UX_B_AiLayout720_ProviderListAndFieldOrder()
        {
            var helper=typeof(StartupProgressTests);var size=helper.GetMethod("Size",BindingFlags.Static|BindingFlags.NonPublic);
            try
            {
                size.Invoke(null,new object[]{-720});yield return null;
                var panel=Find<CafeDemoDeveloperPanel>();panel.Open();yield return Wait(()=>!panel.ModelForm.BlocksSettingsNavigation);
                panel.PlayerNavigation.ShowAi(false);yield return null;
                var form=panel.ModelForm;var supplier=(TMP_Dropdown)Get(form,"mainSupplier");
                var profiles=(ModelProfilesView)Get(form,"profilesView");
                profiles.main_presets=profiles.main_presets.Concat(new[]{"custom","deepseek","qwen","glm","kimi","openai","gemini","claude","openrouter","siliconflow","ollama"}
                    .Select(id=>new ModelPresetView{preset_id=id,name=id=="ollama"?"Ollama":id,source=id=="ollama"?"local":"cloud",protocol=id=="ollama"?"ollama":"openai_compatible",base_url="http://127.0.0.1:18846"})).ToArray();
                typeof(FirstRunModelSetup).GetMethod("LoadSupplierOptions",Private).Invoke(form,new object[]{false,0,"fixture-cloud"});
                yield return Wait(()=>supplier.interactable);Canvas.ForceUpdateCanvases();
                var model=(TMP_InputField)Get(form,"mainModel");var url=(TMP_InputField)Get(form,"mainBaseUrl");var key=(TMP_InputField)Get(form,"mainApiKey");
                Assert.Less(url.transform.parent.GetSiblingIndex(),key.transform.parent.GetSiblingIndex());
                Assert.Less(key.transform.parent.GetSiblingIndex(),model.transform.parent.GetSiblingIndex());
                Assert.IsFalse(((TMP_Text)Get(form,"recentState")).text.Contains("8K:"));
                Assert.IsFalse(((TMP_Text)Get(form,"embeddingModeInformation")).gameObject.activeInHierarchy);
                Assert.AreEqual(1280,Screen.width);Assert.AreEqual(720,Screen.height);
                ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"ai-1280.png"));yield return new WaitForSecondsRealtime(.2f);
                supplier.GetComponentInChildren<SettingsDropdownClickToggle>(true).OnPointerClick(
                    new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
                yield return new WaitForSecondsRealtime(.25f);
                Assert.IsTrue(supplier.options.Any(o=>o.text.Contains("Ollama")));
                var list=supplier.transform.Find("Dropdown List");Assert.IsNotNull(list);
                var scroll=list.GetComponent<ScrollRect>();Assert.IsTrue(scroll.vertical);
                Assert.Greater(list.GetComponent<RectTransform>().rect.height,400);
                scroll.verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();yield return null;
                var corners=new Vector3[4];((RectTransform)list).GetWorldCorners(corners);
                Assert.GreaterOrEqual(corners[0].y,-1);Assert.LessOrEqual(corners[2].y,Screen.height+1);
                ScreenCapture.CaptureScreenshot(Path.Combine(Evidence,"providers-1280.png"));yield return new WaitForSecondsRealtime(.2f);
                supplier.Hide();panel.Close();
            }
            finally
            {
                SizeProperty.SetValue(GameView,3);
                int index=SessionState.GetInt("StartupProgress.Tests.temporarySize",-1);
                if(index>=0){var group=helper.GetMethod("SizeGroup",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);group.GetType().GetMethod("RemoveCustomSize").Invoke(group,new object[]{index});SessionState.EraseInt("StartupProgress.Tests.temporarySize");}
            }
        }

        [UnityTest] public IEnumerator UX_C_EntranceOrbitBoundaryAndOpenArea()
        {
            dialogue.ClearSession();
            var orbit=Find<CafeDemoOrbitCamera>();var movement=Find<CafeDemoPlayerController>();
            movement.SetDialogueInputSuspended(false);orbit.SetDialogueInputSuspended(false);
            orbit.enabled=true;orbit.SetSettingsCursorMode(false);
            var controller=movement.Controller;
            foreach(float x in new[]{-6.8f,-6.3f,-5.5f})
            foreach(float angle in new[]{60f,90f,120f})
            foreach(float elevation in new[]{0f,30f,55f})
            {
                controller.enabled=false;player.transform.position=new Vector3(x,.03f,1.07f);controller.enabled=true;
                Set(orbit,"yaw",angle);Set(orbit,"pitch",elevation);Set(orbit,"actualDistance",6f);
                Physics.SyncTransforms();yield return null;yield return null;
                Assert.Greater(orbit.transform.position.x,-7.3f,"Door boundary: "+x+"/"+angle+"/"+elevation);
                Assert.IsFalse(orbit.IsPresentationCameraActive);
            }
            controller.enabled=false;player.transform.position=new Vector3(-6.3f,.03f,1.07f);controller.enabled=true;
            Set(orbit,"yaw",90f);Set(orbit,"pitch",20f);yield return Capture("camera-entrance");
            controller.enabled=false;player.transform.position=new Vector3(-2f,.03f,.4f);controller.enabled=true;
            Set(orbit,"yaw",0f);Set(orbit,"pitch",20f);yield return new WaitForSecondsRealtime(.5f);
            Assert.Greater(orbit.ActualDistance,.5f);
            var start=player.transform.position;var keyboard=UnityEngine.InputSystem.Keyboard.current;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.W));
            yield return new WaitForSecondsRealtime(.2f);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard,new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            Assert.Greater(Vector3.Distance(start,player.transform.position),.1f);
            yield return Capture("camera-open-area");
        }

        [UnityTest] public IEnumerator UX_D_HudMenuExitConfirmationAndEscape()
        {
            dialogue.ClearSession();yield return null;yield return null;
            var panel=Find<CafeDemoDeveloperPanel>();var flow=Find<CafeUiFlowController>();
            var entry=(Button)Get(panel,"entryButton");
            var cap=entry.transform.Find("EscapeKeycap");Assert.IsNotNull(cap);
            Assert.AreEqual("ESC",cap.GetComponentInChildren<TMP_Text>().text);
            Assert.Less(((Image)entry.targetGraphic).color.r,.2f);
            yield return Capture("hud-settings");
            entry.onClick.Invoke();yield return null;Assert.IsTrue(flow.IsMenuOpen);Assert.IsFalse(panel.IsOpen);
            var exit=flow.GetComponentsInChildren<Button>(true).Single(b=>b.name=="ExitGame");
            Assert.IsTrue(exit.gameObject.activeInHierarchy);yield return Capture("game-menu");
            exit.onClick.Invoke();yield return null;Assert.IsTrue(flow.IsExitConfirmationOpen);
            Assert.That(((TMP_Text)Get(flow,"exitWarning")).text,Does.Contain("可能丢失"));
            yield return Capture("exit-confirmation");
            flow.RequestCancel();yield return null;Assert.IsFalse(flow.IsExitConfirmationOpen);Assert.IsTrue(flow.IsMenuOpen);
            flow.RequestCancel();yield return null;Assert.IsFalse(flow.IsMenuOpen);
            flow.RequestCancel();yield return null;Assert.IsTrue(flow.IsMenuOpen);
            flow.Resume();yield return null;Assert.IsFalse(flow.IsMenuOpen);
            Assert.IsFalse(Find<CafeDemoPlayerController>().IsInputSuspended);
        }

        [UnityTest] public IEnumerator UX_E_KnowledgeScopeLayoutAndDraftProbeValidation()
        {
            var panel=Find<CafeDemoDeveloperPanel>();panel.Open();yield return Wait(()=>!panel.ModelForm.BlocksSettingsNavigation);
            var knowledge=(CafeWorldKnowledgePanel)Get(panel,"knowledgeSettings");
            Set(Get(knowledge,"view"),"font",knowledge.GetComponentsInChildren<TMP_Text>(true).First().font);
            var item=new KnowledgeItemView{origin="custom",entry=new KnowledgeEntryView{knowledge_id="custom.ux-layout",name="隔离布局检查",content="仅 UI 草稿，不保存。",knowledge_type="reference",source="synthetic",version=1,
                access=new KnowledgeAccessView{character_ids=new[]{"cafe.merchant"},deployment_ids=new[]{"ui-only"}}}};
            var data=new KnowledgeCatalogView{revision=7,items=new[]{item},scopes=new KnowledgeScopesView{deployment_id="ui-only",character_ids=new[]{"cafe.merchant","cafe.regular"}},
                limits=new KnowledgeLimitsView{name=120,content=4000,knowledge_type=40,attribute_keys=10,attribute_key_length=40,attributes_chars=2000,related_ids=10},index=new KnowledgeIndexView{mode="disabled"}};
            typeof(CafeWorldKnowledgePanel).GetMethod("ApplyCatalog",Private).Invoke(knowledge,new object[]{data,item.entry.knowledge_id});
            Set(knowledge,"nextPoll",float.PositiveInfinity);
            panel.ShowKnowledge();yield return null;Canvas.ForceUpdateCanvases();
            Assert.IsFalse(((TMP_Text)Get(knowledge,"metadata")).gameObject.activeInHierarchy);
            Assert.IsTrue(((TMP_Dropdown)Get(knowledge,"relationPicker")).gameObject.activeInHierarchy);
            Assert.IsTrue(((TMP_Dropdown)Get(knowledge,"accessMode")).gameObject.activeInHierarchy);
            var roles=knowledge.GetComponentsInChildren<Toggle>(true).Where(t=>t.name=="KnowledgeRole").ToArray();
            Assert.AreEqual(2,roles.Length);Assert.IsTrue(roles[0].isOn);Assert.IsFalse(roles[1].isOn);
            foreach(var role in roles)
            {
                Assert.LessOrEqual(((RectTransform)role.transform).rect.width,70);
                Assert.IsNotNull(role.transform.parent.Find("ScopeLabel"));
            }
            roles[0].isOn=false;roles[1].isOn=true;
            var draft=(KnowledgeEditView)typeof(CafeWorldKnowledgePanel).GetMethod("Edit",Private).Invoke(knowledge,null);
            CollectionAssert.AreEqual(new[]{"cafe.regular"},draft.character_ids);
            CollectionAssert.AreEqual(new[]{"cafe.merchant"},item.entry.access.character_ids);
            var scroll=((Transform)Get(knowledge,"form")).GetComponentInParent<ScrollRect>();scroll.verticalNormalizedPosition=0;
            yield return Capture("knowledge-scopes");
            knowledge.GetComponentsInChildren<Button>(true).Single(b=>b.name=="KnowledgeAdvanced").onClick.Invoke();
            Assert.IsTrue(((TMP_Text)Get(knowledge,"metadata")).gameObject.activeInHierarchy);
            typeof(CafeWorldKnowledgePanel).GetMethod("ApplyCatalog",Private).Invoke(knowledge,new object[]{data,item.entry.knowledge_id});Set(knowledge,"nextPoll",float.PositiveInfinity);
            panel.PlayerNavigation.ShowAi(false);yield return null;
            var form=panel.ModelForm;var model=(TMP_InputField)Get(form,"mainModel");var original=model.text;
            var supplier=(TMP_Dropdown)Get(form,"mainSupplier");
            var caption=supplier.transform.parent.Find("Label").GetComponent<TMP_Text>();
            var reference=model.transform.parent.Find("Label").GetComponent<TMP_Text>();
            Assert.AreEqual(reference.fontSize,caption.fontSize);Assert.AreEqual(reference.enableAutoSizing,caption.enableAutoSizing);
            Assert.AreEqual(reference.rectTransform.offsetMin,caption.rectTransform.offsetMin);
            yield return Capture("ai-label-alignment");
            model.SetTextWithoutNotify("");typeof(FirstRunModelSetup).GetMethod("PromptProbe",Private).Invoke(form,new object[]{false,"contracts"});
            Assert.IsNull(Get(form,"profileModal"));Assert.That(((TMP_Text)Get(form,"statusText")).text,Does.Contain("模型 ID"));
            model.SetTextWithoutNotify(original);typeof(FirstRunModelSetup).GetMethod("PromptProbe",Private).Invoke(form,new object[]{false,"contracts"});
            Assert.IsNotNull(Get(form,"profileModal"));Assert.IsTrue(form.CancelSettingsConfirmation());
            panel.Close();
        }

        [UnityTest] public IEnumerator P3B_T1_NamesPromptAndHistoryRefreshInPlace()
        {
            string conversation = dialogue.CurrentConversationId;
            var target = dialogue.SelectedTarget;
            var history = Find<DialogueHistoryPanel>(); history.Toggle();
            yield return Wait(() => history.DisplayText.Contains("历史正文保持原样。"));
            var ids = new[] { "npc_cafe_01", "npc_cafe_regular_01", "npc_cafe_companion_01", "npc_cafe_quiet_01" };
            var zh = new[] { "林薇薇", "沈知意", "温予安", "江亦舟" };
            var en = new[] { "Lin Weiwei", "Shen Zhiyi", "Wen Yu'an", "Jiang Yizhou" };
            foreach (bool english in new[] { false, true, false })
            {
                SettingsUiLanguage.SetEnglish(english); yield return null;
                for (int i=0; i<ids.Length; i++) Assert.AreEqual(english ? en[i] : zh[i], SettingsUiLanguage.NpcDisplayName(ids[i], zh[i]));
                Assert.That(history.DisplayText, Does.Contain(english ? en[0] : zh[0]));
                Assert.IsTrue(((ScrollRect)Get(history,"scrollView")).content.GetComponentsInChildren<TMP_Text>()
                    .Any(t=>t.text == (english ? en[0] : zh[0])));
                Assert.That(history.DisplayText, Does.Contain("历史正文保持原样。"));
                Assert.AreSame(target, dialogue.SelectedTarget); Assert.AreEqual(conversation, dialogue.CurrentConversationId);
                if (english || !File.Exists(Path.Combine(Evidence,"history-zh-1920.png"))) yield return Capture(english ? "history-en" : "history-zh");
            }
            history.Close(); dialogue.HideDialoguePresentation();
            var interactor = player.GetComponent<PlayerDialogueInteractor>();
            yield return Wait(() => interactor.RefreshCandidate() == target);
            SettingsUiLanguage.SetEnglish(true); yield return null;
            Assert.That(interactor.PromptLabel, Does.Contain(en[0])); yield return Capture("prompt-en");
            SettingsUiLanguage.SetEnglish(false); Assert.That(interactor.PromptLabel, Does.Contain(zh[0]));
            Assert.AreEqual(conversation, dialogue.CurrentConversationId);
        }

        [UnityTest] public IEnumerator P3B_T2_BilingualEntryTraceAndWebSocketText()
        {
            var panel = Find<CafeDemoDeveloperPanel>(); panel.Open();
            yield return Wait(() => !panel.ModelForm.BlocksSettingsNavigation); yield return null;
            panel.PlayerNavigation.ShowGeneral();
            CollectionAssert.AreEqual(new[] { "中文 / Chinese", "英文 / English" }, panel.LanguageDropdown.options.Select(o=>o.text));
            foreach (bool english in new[] { false, true })
            {
                panel.LanguageDropdown.value = english ? 1 : 0; yield return null;
                Assert.IsTrue(((GameObject)Get(panel,"panelRoot")).GetComponentsInChildren<TMP_Text>().Any(t=>t.text=="界面语言 / UI Language"));
                Assert.AreEqual(english ? 1 : 0, PlayerPrefs.GetInt("EmbodiedNPC.Settings.Language", 0));
                yield return Capture(english ? "language-en" : "language-zh");
            }
            panel.PlayerNavigation.SetDeveloper(true); yield return null;
            yield return Wait(() => ((TMP_Text)Get(panel,"backendText")).text.Contains("WebSocket"));
            Assert.That(((TMP_Text)Get(panel,"backendText")).text, Does.Contain("WebSocket: Connected"));
            SettingsUiLanguage.SetEnglish(false);
            Assert.That(((TMP_Text)Get(panel,"backendText")).text, Does.Contain("WebSocket：已连接"));
            Object.FindObjectsByType<Button>().Single(b=>b.name=="DeveloperTool2").onClick.Invoke(); yield return null;
            var trace = Find<CafeRuntimeTracePanel>();
            foreach (bool english in new[] { false, true })
            {
                SettingsUiLanguage.SetEnglish(english); yield return null;
                Assert.AreEqual(english ? "Runtime Trace" : "运行追踪", ((GameObject)Get(panel,"tracePage")).transform.Find("TraceTitle").GetComponent<TMP_Text>().text);
                Assert.AreEqual(english ? "All" : "全部", ((TMP_Dropdown)Get(trace,"categoryDropdown")).options[0].text);
                trace.TogglePause();
                Assert.That(((TMP_Text)Get(trace,"pauseButtonText")).text, Does.Contain(english ? "Resume" : "继续"));
                yield return Capture(english ? "trace-en" : "trace-zh"); trace.TogglePause();
            }
            panel.Close();
        }

        [UnityTest] public IEnumerator P3B_T3_LanguageDoesNotChangeIdentityDraftRequestsOrBubbles()
        {
            string id=npc.EntityId, definition=npc.DefinitionId, rawName=npc.GetComponent<CognitiveContextComposer>().CharacterDefinition.DisplayName;
            var request=Submit(); var waiting=WaitingView();
            yield return Wait(()=>driver.LastSendSucceeded==true && backend.PendingExperienceCount==0);
            int sends=backend.CognitiveRequestSendCount, dialogueWrites=0;
            backend.ExperienceAppendSettled += (draft,ack,status)=>{ if(draft.source_type=="dialogue") dialogueWrites++; };
            string originalRequest=JsonConvert.SerializeObject(request);
            input.InputField.text="未发送草稿 / unsent draft"; bool focused=input.IsInputFocused;
            SettingsUiLanguage.SetEnglish(true); yield return null;
            Assert.AreSame(waiting,WaitingView()); Assert.AreEqual("未发送草稿 / unsent draft",input.CurrentText);
            Assert.AreEqual(focused,input.IsInputFocused); Assert.AreEqual(originalRequest,JsonConvert.SerializeObject(driver.PendingRequest));
            Assert.AreEqual(sends,backend.CognitiveRequestSendCount); Assert.AreEqual(0,dialogueWrites);
            Assert.AreEqual(id,npc.EntityId); Assert.AreEqual(definition,npc.DefinitionId);
            Assert.AreEqual(rawName,npc.GetComponent<CognitiveContextComposer>().CharacterDefinition.DisplayName);
            Assert.IsTrue(backend.ProcessIncomingJson(Reply(request))); yield return Wait(()=>backend.PendingExperienceCount==0);
            var reply=bubbles.GetComponentsInChildren<DialogueBubbleView>().Single(v=>v.Message.SpeakerEntityId==npc.EntityId);
            Assert.AreSame(waiting,reply); Assert.AreEqual("你好，这是隔离测试回复。",reply.Message.Text);
            int settled=dialogueWrites; SettingsUiLanguage.SetEnglish(false); yield return null;
            Assert.AreEqual("你好，这是隔离测试回复。",reply.Message.Text);
            Assert.AreEqual(settled,dialogueWrites); Assert.AreEqual(sends,backend.CognitiveRequestSendCount);
            Assert.AreEqual("自定义名字",SettingsUiLanguage.NpcDisplayName(id,"自定义名字"));
            Assert.AreEqual("原始标签",SettingsUiLanguage.NpcDisplayName(null,"原始标签"));
        }

        [UnityTest] public IEnumerator UX_A_WaitingReceiptThenReply_NoGapOrSuccessToast()
        {
            int speech = 0; npc.GetComponent<SpeechPresentationLifecycle>().SpeechStarted += _ => speech++;
            var request = Submit(); var waiting = WaitingView();
            yield return Wait(() => backend.PendingExperienceCount == 0 && driver.LastSendSucceeded == true);
            int sends = backend.CognitiveRequestSendCount;
            Assert.IsEmpty(Status, "Ordinary WAITING must not repeat at the bottom.");
            Assert.AreEqual(0, speech); Assert.IsFalse(npc.GetComponent<SpeechPresentationLifecycle>().IsSpeaking);
            Assert.IsFalse(Find<DialogueHistoryPanel>().DisplayText.Contains("..."));
            yield return Capture("waiting");
            var before = waiting.ScreenRect.center;
            npc.transform.position += Vector3.right * .25f; yield return null; yield return null;
            Assert.Greater(Vector2.Distance(before, waiting.ScreenRect.center), 1, "Waiting follows the NPC anchor.");
            yield return Capture("waiting-follow");
            bubbles.Refresh(Time.unscaledTimeAsDouble + bubbles.BubbleLifetimeSeconds + 1);
            Assert.IsNotNull(WaitingView(), "Waiting must not consume the normal reply lifetime.");
            Assert.AreEqual(sends, backend.CognitiveRequestSendCount);
            typeof(DialogueController).GetMethod("TrackAcceptedFeedback", Private).Invoke(dialogue, new object[] {
                new AcceptedDialogueTurn(true,request.request_id,request.trace_id,dialogue.CurrentConversationId,
                    player.EntityId,npc.EntityId,"尚未显示的回复",0,0,0) });
            Assert.AreSame(waiting,WaitingView(),"A response receipt must not remove the waiting bubble.");
            typeof(DialogueController).GetMethod("HandleRequestFeedback", Private).Invoke(dialogue,new object[]{new CognitiveRequestFeedback(request,"response_received",null)});
            Assert.AreSame(waiting,WaitingView());Assert.IsEmpty(Status);
            double displayedAfter = Time.unscaledTimeAsDouble;
            Assert.IsTrue(backend.ProcessIncomingJson(Reply(request)));
            yield return null;
            Assert.IsNull(WaitingView());
            var reply = bubbles.GetComponentsInChildren<DialogueBubbleView>().Single(v => v.Message.SpeakerEntityId == npc.EntityId);
            Assert.AreSame(waiting, reply, "Replace text on the same view, no overlapping second bubble.");
            Assert.GreaterOrEqual(reply.Message.CreatedAt, displayedAfter); Assert.AreEqual(1, speech);
            Assert.IsEmpty(Status,"Normal reply/ACK progress is silent.");
            Assert.IsFalse(bubbles.GetMessages(npc.EntityId).Any(m => m.Text == "..."));
            yield return Capture("reply");
            bubbles.Refresh(reply.Message.CreatedAt + bubbles.BubbleLifetimeSeconds - .1);
            Assert.AreEqual(1, bubbles.GetMessages(npc.EntityId).Count);
            bubbles.Refresh(reply.Message.CreatedAt + bubbles.BubbleLifetimeSeconds + .1);
            Assert.IsEmpty(bubbles.GetMessages(npc.EntityId));
        }

        [UnityTest] public IEnumerator UX_A_FailureTimeoutAndLateReply_KeepErrorAndClearOnlyCurrentWaiting()
        {
            var failed = Submit();
            Assert.IsTrue(backend.ProcessIncomingJson(JsonConvert.SerializeObject(new {
                message_type = "error", request_id = failed.request_id, trace_id = failed.trace_id,
                run_id = failed.run.run_id, agent_entity_id = failed.run.agent_entity_id, reason = "COGNITIVE_REQUEST_FAILED"
            })));
            Assert.IsNull(WaitingView()); Assert.IsNotEmpty(Status);
            Set(driver, "responseTimeoutSeconds", .3f);
            var timedOut = Submit();
            yield return Wait(() => driver.PendingRequest == null);
            Assert.IsNull(WaitingView()); Assert.IsNotEmpty(Status);
            Set(driver, "responseTimeoutSeconds", 30f);
            var current = Submit(); var currentView = WaitingView();
            backend.ProcessIncomingJson(Reply(timedOut));
            Assert.AreSame(current, driver.PendingRequest);
            Assert.AreSame(currentView, WaitingView());
            var other = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "npc_cafe_regular_01");
            Select(other); Assert.IsNull(WaitingView(), "Switching NPC clears the old owner's waiting bubble.");
            Assert.AreSame(current, driver.PendingRequest, "UI cleanup does not cancel formal work.");
        }

        [UnityTest] public IEnumerator UX_A_CloseDisconnectAnchorAndStorageFeedback_StayPresentationOnly()
        {
            var request = Submit();
            yield return Wait(() => backend.PendingExperienceCount == 0 && driver.LastSendSucceeded == true);
            int sends = backend.CognitiveRequestSendCount;
            var unconfirmed = (System.Collections.Generic.HashSet<string>)Get(dialogue, "unconfirmedRecords");
            unconfirmed.Add("synthetic-unconfirmed");
            typeof(DialogueController).GetMethod("RefreshRequestFeedback", Private).Invoke(dialogue, null);
            Assert.IsTrue(Status.Contains("保存") || Status.Contains("Storage"), Status);
            Assert.IsFalse(Status.Contains("正在等待") || Status.Contains("Waiting for"));
            var waiting=WaitingView(); input.CancelInput(); Assert.AreSame(waiting,WaitingView()); Assert.IsFalse(dialogue.IsPresentationVisible);
            Assert.IsFalse(player.GetComponent<CafeDemoPlayerController>().IsInputSuspended);
            dialogue.ShowDialoguePresentation();
            var anchor = npc.GetComponent<DialogueBubbleAnchor>();
            Assert.IsTrue(bubbles.ShowWaiting(npc.EntityId, request.request_id, request.trace_id, dialogue.CurrentConversationId));
            anchor.enabled = false; Assert.IsNull(WaitingView()); anchor.enabled = true;
            // Replay only the already accepted UI receipt to exercise disconnect cleanup without another cognition request.
            typeof(DialogueController).GetMethod("TrackAcceptedFeedback", Private).Invoke(dialogue, new object[] {
                new AcceptedDialogueTurn(false, request.request_id, request.trace_id, dialogue.CurrentConversationId,
                    player.EntityId, npc.EntityId, "隔离等待气泡测试", 0, 0, 0) });
            Assert.IsNotNull(WaitingView());
            var close = backend.DisconnectAsync(); while (!close.IsCompleted) yield return null;
            yield return Wait(() => WaitingView() == null);
            Assert.IsNotEmpty(Status); Assert.AreEqual(sends, backend.CognitiveRequestSendCount);
            Assert.IsFalse(npc.GetComponent<SpeechPresentationLifecycle>().IsSpeaking);
            Assert.IsFalse(Find<DialogueHistoryPanel>().DisplayText.Contains("..."));
            dialogue.ClearSession(); Assert.IsNull(WaitingView());
        }

        [UnityTest] public IEnumerator P2_InputCloseRetainsWaiting_ReplyAndCancelCleanUp_AiOrder()
        {
            var request=Submit(); var waiting=WaitingView();
            yield return Wait(()=>backend.PendingExperienceCount==0 && driver.LastSendSucceeded==true);
            input.CancelInput(); yield return null;
            Assert.AreSame(waiting,WaitingView()); Assert.AreSame(request,driver.PendingRequest);
            Assert.IsEmpty(Status); Assert.IsFalse(dialogue.IsPresentationVisible);
            Assert.IsTrue(backend.ProcessIncomingJson(Reply(request))); yield return null;
            var reply=bubbles.GetComponentsInChildren<DialogueBubbleView>().Single(v=>v.Message.SpeakerEntityId==npc.EntityId);
            Assert.AreSame(waiting,reply); Assert.IsNull(WaitingView());
            dialogue.ShowDialoguePresentation();
            var second=Submit(); Assert.IsNotNull(WaitingView());
            dialogue.ClearSession(); Assert.IsNull(WaitingView(),"Actual session cancellation clears its wait.");
            yield return Wait(()=>backend.PendingExperienceCount==0);
            var panel=Find<CafeDemoDeveloperPanel>(); panel.Open();
            yield return Wait(()=>!panel.ModelForm.BlocksSettingsNavigation);
            panel.PlayerNavigation.ShowAi(false); yield return null; Canvas.ForceUpdateCanvases();
            var form=panel.ModelForm;
            var fields=new Component[]{(TMP_Dropdown)Get(form,"mainSupplier"),(TMP_InputField)Get(form,"mainBaseUrl"),
                (TMP_InputField)Get(form,"mainApiKey"),(TMP_InputField)Get(form,"mainModel")};
            for(int i=1;i<fields.Length;i++) Assert.Less(fields[i-1].transform.parent.GetSiblingIndex(),fields[i].transform.parent.GetSiblingIndex());
            yield return Capture("p2-ai-order-1920");
            panel.Close();
        }
    }
}
