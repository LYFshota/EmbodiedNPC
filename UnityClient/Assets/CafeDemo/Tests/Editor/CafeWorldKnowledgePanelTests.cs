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
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Opt-in one isolated Play session against the real Knowledge management API.</summary>
    public sealed class CafeWorldKnowledgePanelTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string Root => Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_UI_TEST_ROOT");
        private CafeDemoDeveloperPanel owner;
        private CafeWorldKnowledgePanel knowledge;
        private readonly System.Collections.Generic.List<PresentedCognitiveUtterance> replies = new();
        private readonly JArray observedReplies = new();
        private JObject inspection;
        private static bool Integration => Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_INTEGRATION") == "true";
        private static void Set(object o, string field, object value) => o.GetType().GetField(field, Private).SetValue(o, value);
        private T Find<T>(string name) where T : Component => knowledge.GetComponentsInChildren<T>(true).Single(x => x.name == name);
        private void Click(string name)
        {
            var button = name.StartsWith("ConfirmChoice")
                ? knowledge.GetComponentsInChildren<Button>(true).Single(x => x.name == name && x.gameObject.activeInHierarchy)
                : Find<Button>(name);
            button.onClick.Invoke();
        }
        private static IEnumerator Wait(Func<bool> condition, string message)
        {
            float until = Time.realtimeSinceStartup + 15;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(condition(), Is.True, message);
        }
        private IEnumerator Saved()
        {
            yield return Wait(() => !knowledge.IsBusy, "knowledge mutation completed");
            Assert.That(knowledge.IsDirty, Is.False, Find<TMP_Text>("KnowledgeSaveStatus").text);
        }

        [UnitySetUp] public IEnumerator Load()
        {
            SessionState.SetBool("KnowledgeTest.Loaded", false);
            if (string.IsNullOrEmpty(Root) || !File.Exists(Path.Combine(Root, "session.json")))
                Assert.Ignore("Requires an opt-in isolated Knowledge API and EMBODIEDNPC_KNOWLEDGE_UI_TEST_ROOT.");
            Assert.That(EditorSceneManager.GetActiveScene().isDirty, Is.False, "Never replace a dirty user scene.");
            SessionState.SetString("KnowledgeTest.Scene", EditorSceneManager.GetActiveScene().path);
            SessionState.SetBool("KnowledgeTest.English", SettingsUiLanguage.IsEnglish);
            SessionState.SetBool("KnowledgeTest.Loaded", true);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            foreach (var backend in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { Set(backend, "connectOnStart", Integration); Set(backend, "serverUrl", "ws://127.0.0.1:18889/ws"); }
            if (Integration) Object.FindAnyObjectByType<LocalTestIdentityProvider>().Configure("knowledge-acceptance-player", "验收玩家", 1, DateTimeOffset.UnixEpoch);
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            yield return new EnterPlayMode();
            owner = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            owner.Open(); owner.ShowKnowledge();
            knowledge = Object.FindAnyObjectByType<CafeWorldKnowledgePanel>();
            yield return Wait(() => !knowledge.IsBusy && knowledge.SelectedId != null, "load authorized knowledge catalog");
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!SessionState.GetBool("KnowledgeTest.Loaded", false)) yield break;
            if (Application.isPlaying && Integration)
            {
                var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
                if (backend != null) { var disconnect = backend.DisconnectAsync(); while (!disconnect.IsCompleted) yield return null; }
            }
            if (Application.isPlaying) yield return new ExitPlayMode();
            SettingsUiLanguage.SetEnglish(SessionState.GetBool("KnowledgeTest.English", false));
            string scene = SessionState.GetString("KnowledgeTest.Scene", "Assets/Scenes/CafeDemoV2.unity");
            if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
        }

        [UnityTest] public IEnumerator NativeEditor_SaveConflictDraftGuardsAndExistingNavigation()
        {
            SettingsUiLanguage.SetEnglish(false);
            Assert.That(Object.FindAnyObjectByType<CafeDemoPlayerController>().IsInputSuspended, Is.True);
            Assert.That(Object.FindAnyObjectByType<CafeDemoOrbitCamera>().IsInputSuspended, Is.True);
            knowledge.NewKnowledge(); yield return null;
            Find<TMP_InputField>("KnowledgeName").text = "隔离测试 · 玉露参考";
            Find<TMP_InputField>("KnowledgeContent").text = "玉露是遮阴栽培的日本绿茶，常用较低水温冲泡，滋味鲜醇。此为静态饮品参考，不表示本店库存、菜单或已经制作。";
            Assert.That(knowledge.IsDirty, Is.True);
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.EqualTo(Find<TMP_InputField>("KnowledgeName").gameObject));
            Click("KnowledgeAdvanced"); Click("AddKnowledgeAttribute");
            Find<TMP_InputField>("AttributeKey").text = "category";
            Find<TMP_InputField>("AttributeValue").text = "tea";
            Find<TMP_InputField>("RelatedKnowledgeSearch").text = "咖啡馆";
            Find<TMP_Dropdown>("RelatedKnowledgePicker").value = 1;
            Assert.That(Find<TMP_Text>("RelatedName").text, Is.EqualTo("咖啡馆基本设定"));
            Find<TMP_Dropdown>("KnowledgeAccessMode").value = 1;
            foreach (var role in knowledge.GetComponentsInChildren<Toggle>(true).Where(x => x.name == "KnowledgeRole"))
                role.isOn = role.transform.parent.Find("ScopeLabel").GetComponent<TMP_Text>().text == "cafe.merchant";
            knowledge.Save(); Assert.That(knowledge.IsBusy, Is.True);
            knowledge.Save(); // No duplicate submission while busy.
            yield return Saved();
            string id = knowledge.SelectedId;
            Assert.That(id, Does.StartWith("custom."));
            Assert.That(Find<TMP_Text>("KnowledgeMetadata").text, Does.Contain("Version: 1"));
            Find<TMP_InputField>("KnowledgeContent").text += "\n参考资料可在编辑后更新。";
            knowledge.Save(); yield return Saved();
            Assert.That(knowledge.SelectedId, Is.EqualTo(id));
            Assert.That(Find<TMP_Text>("KnowledgeMetadata").text, Does.Contain("Version: 2"));

            Find<TMP_InputField>("KnowledgeName").text += " 草稿";
            owner.ShowNpc(); Assert.That(knowledge.ConfirmationVisible, Is.True);
            Click("ConfirmChoice2"); Assert.That(knowledge.gameObject.activeSelf, Is.True);
            Assert.That(knowledge.IsDirty, Is.True);
            owner.Close(); Assert.That(owner.IsOpen, Is.True);
            Click("ConfirmChoice2"); Assert.That(knowledge.IsDirty, Is.True);
            owner.ShowNpc(); Click("ConfirmChoice0"); yield return Saved();
            Assert.That(knowledge.gameObject.activeSelf, Is.False);
            owner.ShowKnowledge(); Assert.That(knowledge.SelectedId, Is.EqualTo(id));

            var api = new KnowledgeManagementClient(() => "http://127.0.0.1:18889");
            KnowledgeCatalogView catalog = null; string error = null; bool completed = false;
            owner.StartCoroutine(api.Catalog((data, code) => { catalog = data; error = code; completed = true; }));
            yield return Wait(() => completed, "external developer catalog");
            Assert.That(error, Is.Null);
            var entry = catalog.items.Single(i => i.entry.knowledge_id == id).entry;
            completed = false;
            owner.StartCoroutine(api.Save(id, new KnowledgeEditView { expected_revision = catalog.revision,
                name = entry.name, content = entry.content + "\n其他开发者更新。", knowledge_type = entry.knowledge_type,
                attributes = entry.attributes, access_mode = "restricted", character_ids = new[] { "cafe.merchant" } },
                (_, code) => { error = code; completed = true; }));
            yield return Wait(() => completed, "external developer save");
            Assert.That(error, Is.Null);
            Find<TMP_InputField>("KnowledgeContent").text += "\n本地未保存文本。";
            string draft = knowledge.DraftContent;
            knowledge.Save(); yield return Wait(() => !knowledge.IsBusy, "revision conflict response");
            Assert.That(knowledge.IsDirty, Is.True); Assert.That(knowledge.DraftContent, Is.EqualTo(draft));
            Assert.That(Find<TMP_Text>("KnowledgeSaveStatus").text, Does.Contain("版本冲突"));

            string session = Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE");
            try
            {
                Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE", Path.Combine(Root, "missing-session.json"));
                knowledge.Save(); yield return Wait(() => !knowledge.IsBusy, "missing authorization response");
                Assert.That(knowledge.IsDirty, Is.True); Assert.That(knowledge.DraftContent, Is.EqualTo(draft));
                Assert.That(Find<TMP_Text>("KnowledgeSaveStatus").text, Does.Contain("授权"));
            }
            finally { Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE", session); }
            string sessionJson = File.ReadAllText(session);
            try
            {
                // Only this test's private capability file changes; the target port is closed.
                File.WriteAllText(session, sessionJson.Replace("18889", "18890"));
                Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL", "http://127.0.0.1:18890");
                knowledge.Save(); yield return Wait(() => !knowledge.IsBusy, "disconnected Backend response");
                Assert.That(knowledge.IsDirty, Is.True); Assert.That(knowledge.DraftContent, Is.EqualTo(draft));
                Assert.That(Find<TMP_Text>("KnowledgeSaveStatus").text, Does.Contain("断开"));
            }
            finally
            {
                File.WriteAllText(session, sessionJson);
                Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL", "http://127.0.0.1:18889");
            }
            Click("ReloadKnowledge"); Click("ConfirmChoice1");
            yield return Wait(() => !knowledge.IsBusy, "reload after explicitly discarding draft");
            Assert.That(knowledge.DraftContent, Does.Contain("其他开发者更新"));
            Click("DeleteKnowledge"); Assert.That(knowledge.ConfirmationVisible, Is.True);
            Click("ConfirmChoice1"); Assert.That(knowledge.SelectedId, Is.EqualTo(id));
            Click("DeleteKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            Assert.That(knowledge.SelectedId, Is.Null);
            var builtin = knowledge.GetComponentsInChildren<Button>(true).First(b => b.name == "KnowledgeItem" && b.GetComponentInChildren<TMP_Text>().text.StartsWith("咖啡馆基本设定"));
            builtin.onClick.Invoke(); string original = knowledge.DraftContent;
            Find<TMP_InputField>("KnowledgeContent").text = "隔离更新：静态世界设定。";
            knowledge.Save(); yield return Saved();
            Click("RestoreKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            Assert.That(knowledge.DraftContent, Is.EqualTo(original));
            Click("DeleteKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            Assert.That(Find<TMP_Text>("KnowledgeMetadata").text, Does.Contain("已隐藏"));
            Click("RestoreKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            Assert.That(knowledge.DraftContent, Is.EqualTo(original));

            // Leave one real edited entry for the external K1 retrieval check.
            knowledge.NewKnowledge(); Find<TMP_InputField>("KnowledgeName").text = "隔离测试 · 玉露参考";
            Find<TMP_InputField>("KnowledgeContent").text = "玉露是遮阴栽培的日本绿茶，常用较低水温冲泡，滋味鲜醇。此为静态饮品参考，不表示本店库存、菜单或已经制作。";
            knowledge.Save(); yield return Saved();
            yield return Capture(1920, 1080, "knowledge-1920-zh.png");
            SettingsUiLanguage.SetEnglish(true); Click("KnowledgeAdvanced");
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(Find<ScrollRect>("KnowledgeForm").content);
            Find<ScrollRect>("KnowledgeForm").verticalNormalizedPosition = 0;
            yield return Capture(1280, 720, "knowledge-1280-en.png");
            Assert.That(Find<TMP_Text>("KnowledgeSaveStatus").text, Does.Contain("Saved"));
            Assert.That(Find<ScrollRect>("KnowledgeForm").content.rect.height,
                Is.GreaterThan(Find<ScrollRect>("KnowledgeForm").viewport.rect.height));
            Find<TMP_InputField>("KnowledgeContent").Select(); Find<TMP_InputField>("KnowledgeContent").ActivateInputField();
            yield return null;
            foreach (Action page in new Action[] { owner.ShowOverview, owner.ShowModel, owner.ShowNpc, owner.ShowDebug, owner.ShowTrace })
            { page(); yield return null; Assert.That(knowledge.gameObject.activeSelf, Is.False); Assert.That(owner.IsOpen, Is.True); }
            owner.Close(); Assert.That(owner.IsOpen, Is.False);
            Assert.That(EventSystem.current.currentSelectedGameObject == null ||
                !EventSystem.current.currentSelectedGameObject.transform.IsChildOf(knowledge.transform), Is.True);
            Assert.That(Object.FindAnyObjectByType<CafeDemoPlayerController>().IsInputSuspended, Is.False);
            Assert.That(Object.FindAnyObjectByType<CafeDemoOrbitCamera>().IsInputSuspended, Is.False);
            if (Integration) yield return IntegratedNpcUse();
        }

        private IEnumerator Inspect(string text = "", string id = "", string role = "cafe.merchant", bool consolidate = false)
        {
            var session = JObject.Parse(File.ReadAllText(Path.Combine(Root, "session.json")));
            string url = "http://127.0.0.1:18889/acceptance/" + (consolidate ? "consolidate" :
                "read?text=" + Uri.EscapeDataString(text) + "&knowledge_id=" + Uri.EscapeDataString(id) + "&role=" + Uri.EscapeDataString(role));
            using (var request = consolidate ? UnityWebRequest.PostWwwForm(url, "") : UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", "Bearer " + (string)session["token"]);
                request.timeout = 70; var operation = request.SendWebRequest();
                while (!operation.isDone) yield return null;
                Assert.That(request.result, Is.EqualTo(UnityWebRequest.Result.Success));
                inspection = JObject.Parse(request.downloadHandler.text);
            }
        }
        private IEnumerator AwaitIndexed(string id)
        {
            float end = Time.realtimeSinceStartup + 35;
            do { yield return Inspect(id: id); if (((JArray)inspection["indexed_ids"]).Any(x => (string)x == id)) yield break; yield return null; }
            while (Time.realtimeSinceStartup < end);
            Assert.Fail("Updated Knowledge vector did not become ready");
        }
        private IEnumerator Chat(string agent, string question, string scenario)
        {
            owner.Close();
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            dialogue.ClearSession();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) { driver.enabled = false; driver.GetComponent<AgentOrchestrator>().CancelCurrentRun(); }
            var player = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "player_01");
            player.GetComponent<CafeDemoPlayerController>().enabled = false;
            var npc = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == agent);
            player.transform.position = npc.transform.position + Vector3.forward * 1.5f; Physics.SyncTransforms();
            var driverForNpc = npc.GetComponent<CognitiveRunDriver>(); Set(driverForNpc, "responseTimeoutSeconds", 70f);
            driverForNpc.UtterancePresented -= RecordReply; driverForNpc.UtterancePresented += RecordReply;
            driverForNpc.enabled = true;
            var scheduler = Object.FindAnyObjectByType<DialogueContinuationScheduler>(); scheduler.SetFeatureEnabled(false);
            Assert.That(dialogue.SelectTarget(npc.GetComponent<DialogueParticipant>()), Is.True, dialogue.LastEligibility.FailureCode.ToString());
            int before = replies.Count;
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>(FindObjectsInactive.Include).SubmitText(question), Is.True);
            float end = Time.realtimeSinceStartup + 80;
            while (replies.Count == before && Time.realtimeSinceStartup < end) yield return null;
            string answer = replies.Count > before ? replies.Last().Utterance : null;
            observedReplies.Add(JObject.FromObject(new { scenario, agent, question, answer, presented = answer != null,
                actionProposal = driverForNpc.LastValidatedGoalProposal != null }));
            File.WriteAllText(Path.Combine(Root, "npc-replies.json"), observedReplies.ToString());
            if (answer != null) yield return Wait(() => Object.FindAnyObjectByType<BackendWebSocketClient>().PendingExperienceCount == 0, "dialogue input/response ACK");
            dialogue.ClearSession(); driverForNpc.enabled = false;
        }
        private void RecordReply(PresentedCognitiveUtterance reply) => replies.Add(reply);

        [UnityTest] public IEnumerator KnowledgeBudget_NewRecipeRealNpc()
        {
            if (!Integration) Assert.Ignore("Requires isolated configured Main + Gemma.");
            yield return Wait(() => Object.FindAnyObjectByType<BackendWebSocketClient>().IsConnected &&
                Object.FindAnyObjectByType<DialogueController>().IsSetupReady, "real Backend and configured startup readiness");
            var recipe = knowledge.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "KnowledgeItem" &&
                b.GetComponentInChildren<TMP_Text>().text.StartsWith("隔离验收 · 月影茶静态配方"));
            if (recipe == null) knowledge.NewKnowledge(); else recipe.onClick.Invoke();
            Find<TMP_InputField>("KnowledgeName").text = "隔离验收 · 月影茶静态配方";
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的新配方由桂花和薄荷组成。此为开发者静态参考资料，不证明菜单、实时库存、制作或交付情况。";
            Click("KnowledgeAdvanced"); Find<TMP_Dropdown>("KnowledgeAccessMode").value = 1;
            foreach (var role in knowledge.GetComponentsInChildren<Toggle>(true).Where(x => x.name == "KnowledgeRole"))
                role.isOn = role.transform.parent.Find("ScopeLabel").GetComponent<TMP_Text>().text == "cafe.merchant";
            knowledge.Save(); yield return Saved();
            string id = knowledge.SelectedId;
            yield return AwaitIndexed(id);
            yield return Inspect(id: id, role: "cafe.regular_customer");
            Assert.That(inspection["hybrid"]["items"].Count(), Is.EqualTo(0));
            yield return Chat("npc_cafe_01", "月影茶的新配方使用哪两种原料？", "Context Budget 定向复验");
            Assert.That(replies.Count, Is.EqualTo(1));
            Assert.That(replies[0].Utterance, Does.Contain("桂花"));
            Assert.That(replies[0].Utterance, Does.Contain("薄荷"));
            yield return Inspect();
            File.WriteAllText(Path.Combine(Root, "final-runtime.json"), inspection.ToString());
            var calls = inspection["calls"].Where(c => (string)c["event_type"] == "player.utterance" && (string)c["status"] == "OK").ToArray();
            Assert.That(calls.Length, Is.InRange(1, 2));
            Assert.That(calls.Any(c => c["knowledge"].Any(k => (string)k["id"] == id)), Is.True,
                "Resolver success is insufficient: final provider request must contain the custom recipe");
            foreach (var call in calls)
            {
                Assert.That((int)call["estimated_input"], Is.LessThanOrEqualTo(7168));
                Assert.That((int)call["knowledge_tokens"], Is.LessThanOrEqualTo(1000));
                Assert.That(call["knowledge"].Count(), Is.LessThanOrEqualTo(3));
            }
        }

        [UnityTest] public IEnumerator KnowledgeAcceptance_ResumeSemanticCases()
        {
            if (!Integration) Assert.Ignore("Requires isolated configured Main + Gemma.");
            yield return Wait(() => Object.FindAnyObjectByType<BackendWebSocketClient>().IsConnected &&
                Object.FindAnyObjectByType<DialogueController>().IsSetupReady, "isolated Backend startup readiness");
            // Continue real consolidated history from the previous isolated acceptance, not fabricated Memory.
            yield return Inspect();
            Assert.That(inspection["memories"].Any(m => (string)m["observer_entity_id"] == "npc_cafe_01" &&
                ((string)m["summary"]).Contains("卡布奇诺")), Is.True);
            knowledge.NewKnowledge();
            Find<TMP_InputField>("KnowledgeName").text = "隔离验收 · 月影茶静态配方";
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的静态配方由陈皮和生姜组成。仅为开发者参考资料，不证明本店菜单、库存、制作、交付或任何共同经历。";
            Click("KnowledgeAdvanced"); Find<TMP_Dropdown>("KnowledgeAccessMode").value = 1;
            foreach (var role in knowledge.GetComponentsInChildren<Toggle>(true).Where(x => x.name == "KnowledgeRole"))
                role.isOn = role.transform.parent.Find("ScopeLabel").GetComponent<TMP_Text>().text == "cafe.merchant";
            knowledge.Save(); yield return Saved(); string id = knowledge.SelectedId;
            yield return AwaitIndexed(id);
            yield return Chat("npc_cafe_01", "月影茶的配方使用哪两种原料？", "新增知识真实使用");
            yield return Chat("npc_cafe_regular_01", "月影茶配方使用哪两种原料？", "角色权限隔离");
            owner.Open(); owner.ShowKnowledge();
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的新配方由桂花和薄荷组成。旧配方已被替换。仅为开发者静态参考资料，不证明菜单、实时库存、制作、交付或共同经历。";
            knowledge.Save(); yield return Saved(); yield return AwaitIndexed(id);
            yield return Chat("npc_cafe_01", "月影茶现在的配方是哪两种原料？再回想我以前告诉你的饮品喜好。这份配方资料是你和我一起制作茶的亲身经历吗？", "更新知识与个人Memory及Recall");
            yield return Chat("npc_cafe_01", "按月影茶和拿铁的配方资料，你能确定现在的库存数量，以及已经检查、制作、交付给我的记录吗？", "静态知识与实时世界动作边界");
            owner.Open(); owner.ShowKnowledge(); Click("DeleteKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            yield return Inspect(id: id);
            Assert.That(inspection["hybrid"]["items"].Count(), Is.EqualTo(0));
            Assert.That(((JArray)inspection["indexed_ids"]).Any(x => (string)x == id), Is.False);
            yield return Chat("npc_cafe_quiet_01", "月影茶的配方是什么？店里的星铃节在哪一天，暗号是什么？", "知识删除与专有设定缺失");
            yield return Inspect(); File.WriteAllText(Path.Combine(Root, "final-runtime.json"), inspection.ToString());
            Assert.That(observedReplies.Count, Is.EqualTo(5));
            Assert.That(observedReplies.All(r => (bool)r["presented"] && !(bool)r["actionProposal"]), Is.True);
            var calls = inspection["calls"].Where(c => (string)c["event_type"] == "player.utterance" && (string)c["status"] == "OK").ToArray();
            foreach (var call in calls)
            {
                Assert.That((int)call["estimated_input"], Is.LessThanOrEqualTo(7168));
                Assert.That((int)call["knowledge_tokens"], Is.LessThanOrEqualTo(1000));
                Assert.That(call["knowledge"].Count(), Is.LessThanOrEqualTo(3));
                if ((string)call["agent"] != "npc_cafe_01")
                    Assert.That(call["knowledge"].Any(k => (string)k["id"] == id), Is.False);
            }
            Assert.That(calls.Any(c => (bool)c["step1"] && (bool)c["memory"] && c["knowledge"].Any(k =>
                (string)k["id"] == id && (int)k["version"] == 2)), Is.True, "Real Step 1 must preserve both authorized updated Knowledge and Memory.");
            // Real reply semantics are reviewed individually; transport success alone is not acceptance.
        }

        [UnityTest] public IEnumerator KnowledgeAcceptance_RecallBudgetRecheck()
        {
            if (!Integration) Assert.Ignore("Requires isolated configured Main + Gemma.");
            yield return Wait(() => Object.FindAnyObjectByType<BackendWebSocketClient>().IsConnected &&
                Object.FindAnyObjectByType<DialogueController>().IsSetupReady, "isolated Backend readiness");
            knowledge.NewKnowledge(); Find<TMP_InputField>("KnowledgeName").text = "隔离验收 · 月影茶静态配方";
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的静态配方由陈皮和生姜组成。仅为开发者参考，不证明库存、动作完成或共同经历。";
            Click("KnowledgeAdvanced"); Find<TMP_Dropdown>("KnowledgeAccessMode").value = 1;
            foreach (var role in knowledge.GetComponentsInChildren<Toggle>(true).Where(x => x.name == "KnowledgeRole"))
                role.isOn = role.transform.parent.Find("ScopeLabel").GetComponent<TMP_Text>().text == "cafe.merchant";
            knowledge.Save(); yield return Saved(); string id = knowledge.SelectedId;
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的新配方由桂花和薄荷组成。旧配方已被替换。仅为开发者静态参考资料，不证明菜单、实时库存、制作、交付或共同经历。";
            knowledge.Save(); yield return Saved(); yield return AwaitIndexed(id);
            yield return Chat("npc_cafe_01", "月影茶现在的配方是哪两种原料？再回想我以前告诉你的饮品喜好。这份配方资料是你和我一起制作茶的亲身经历吗？", "Step1预算修正定向复验");
            yield return Inspect(); File.WriteAllText(Path.Combine(Root, "recall-recheck.json"), inspection.ToString());
            var call = inspection["calls"].Last(c => (string)c["agent"] == "npc_cafe_01" && (bool)c["step1"]);
            int capacity = (int)call["model_context"], outputReserve = (int)call["output_reserve"];
            Assert.That((int)call["estimated_input"] + outputReserve + 512, Is.LessThanOrEqualTo(capacity));
            Assert.That((int)call["usage"]["prompt_tokens"] + (int)call["usage"]["completion_tokens"], Is.LessThanOrEqualTo(capacity));
            Assert.That((string)call["recall_status"], Is.EqualTo("OK"));
            Assert.That(call["memory_evidence"]["items"].Count(), Is.GreaterThan(0));
            Assert.That(call["knowledge"].Any(k => (string)k["id"] == id && (int)k["version"] == 2), Is.True);
            Assert.That(replies.Single().Utterance, Does.Contain("桂花").And.Contain("薄荷").And.Contain("卡布奇诺"));
        }

        [UnityTest] public IEnumerator CaptureActualUnityRequestWithoutInference()
        {
            if (!Integration) Assert.Ignore("Only needed for isolated integration budget diagnosis.");
            owner.Close();
            var npc = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "npc_cafe_01");
            var player = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "player_01");
            player.GetComponent<CafeDemoPlayerController>().enabled = false;
            player.transform.position = npc.transform.position + Vector3.forward * 1.5f; Physics.SyncTransforms();
            yield return null; yield return null;
            npc.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            string json = null;
            npc.GetComponent<CognitiveContextComposer>().CognitiveRequestCreated += request =>
            { if (request.context.current_event.event_type == "player.utterance") json = JsonUtility.ToJson(request); };
            var dialogue = Object.FindAnyObjectByType<DialogueController>();
            Assert.That(dialogue.SelectTarget(npc.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>(FindObjectsInactive.Include).SubmitText("月影茶的新配方使用哪两种原料？"), Is.True);
            yield return Wait(() => json != null, "capture real composed request, driver remains disabled");
            File.WriteAllText(Path.Combine(Root, "runtime-request.json"), json);
            npc.GetComponent<AgentOrchestrator>().CancelCurrentRun(); dialogue.ClearSession();
        }

        private IEnumerator IntegratedNpcUse()
        {
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            yield return Wait(() => backend.IsConnected, "real isolated Backend WebSocket");
            Assert.That(Object.FindAnyObjectByType<DialogueController>().IsSetupReady, Is.True);
            string id = knowledge.SelectedId;
            yield return Inspect(id: id);
            Assert.That((string)inspection["lexical"]["items"][0]["knowledge_id"], Is.EqualTo(id));
            yield return AwaitIndexed(id);
            const string semanticQuestion = "Which Japanese green tea is cultivated under shade and brewed with cooler water?";
            yield return Inspect(semanticQuestion);
            Assert.That(inspection["lexical_scores"].Children<JProperty>().All(p => (int)p.Value == 0), Is.True);
            Assert.That(inspection["hybrid"]["items"].Any(i => (string)i["knowledge_id"] == id), Is.True);
            yield return Chat("npc_cafe_01", "我喜欢卡布奇诺。这家店是怎样的一家咖啡馆？", "普通设定及玩家偏好");
            yield return Chat("npc_cafe_01", semanticQuestion, "跨词汇语义检索");
            yield return Inspect(consolidate: true);
            yield return Chat("npc_cafe_01", "拿铁与卡布奇诺的奶味和奶泡有什么区别？", "多条候选融合");
            owner.Open(); owner.ShowKnowledge();
            Find<TMP_InputField>("KnowledgeName").text = "隔离验收 · 月影茶静态配方";
            Find<TMP_InputField>("KnowledgeContent").text = "隔离验收专用虚构饮品：月影茶的静态配方由桂花和薄荷组成。此为开发者参考资料，不证明本店菜单、库存、制作或交付情况。";
            if (!knowledge.GetComponentsInChildren<Transform>(true).Single(t => t.name == "KnowledgeAdvancedContent").gameObject.activeSelf) Click("KnowledgeAdvanced");
            Find<TMP_Dropdown>("KnowledgeAccessMode").value = 1;
            foreach (var role in knowledge.GetComponentsInChildren<Toggle>(true).Where(x => x.name == "KnowledgeRole"))
                role.isOn = role.transform.parent.Find("ScopeLabel").GetComponent<TMP_Text>().text == "cafe.merchant";
            knowledge.Save(); yield return Saved();
            yield return Inspect(id: id, role: "cafe.regular_customer");
            Assert.That(inspection["hybrid"]["items"].Count(), Is.EqualTo(0));
            yield return AwaitIndexed(id);
            yield return Chat("npc_cafe_regular_01", "月影茶配方是什么？它用到哪两种原料？", "另一NPC访问限制");
            yield return Chat("npc_cafe_01", "月影茶的新配方使用哪两种原料？", "更新版本使用");
            owner.Open(); owner.ShowKnowledge(); Click("DeleteKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            yield return Inspect(id: id);
            Assert.That(inspection["hybrid"]["items"].Count(), Is.EqualTo(0));
            yield return Chat("npc_cafe_quiet_01", "店里的星铃节是哪天？这个节日的暗号是什么？", "专有知识缺失");
            yield return Chat("npc_cafe_01", "现在吧台还有多少拿铁？你已经检查、做好并交付给我了吗？", "实时世界与动作边界");
            yield return Chat("npc_cafe_01", "你还记得我喜欢什么吗？再说说卡布奇诺的一般口感。这些饮品资料是我们一起经历的吗？", "个人Memory与Knowledge");
            yield return Inspect();
            File.WriteAllText(Path.Combine(Root, "final-runtime.json"), inspection.ToString());
            Assert.That(observedReplies.Count, Is.EqualTo(8));
            Assert.That(observedReplies.All(r => (bool)r["presented"]), Is.True, "Every case must have a real presented NPC answer");
        }

        private static IEnumerator Capture(int width, int height, string filename)
        {
            var assembly = typeof(Editor).Assembly;
            var sizes = assembly.GetType("UnityEditor.GameViewSizes");
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizes).GetProperty("instance").GetValue(null);
            var group = sizes.GetMethod("GetGroup").Invoke(singleton, new object[] { 0 });
            var sizeType = assembly.GetType("UnityEditor.GameViewSizeType");
            var size = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"), Enum.ToObject(sizeType, 1), width, height, "Knowledge temporary");
            int index = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            var gameType = assembly.GetType("UnityEditor.GameView");
            var window = EditorWindow.GetWindow(gameType);
            var property = gameType.GetProperty("selectedSizeIndex", BindingFlags.Public | Private);
            int previous = (int)property.GetValue(window);
            try
            {
                property.SetValue(window, index); window.Focus();
                yield return new WaitForSecondsRealtime(.5f);
                Assert.That(Screen.width, Is.EqualTo(width)); Assert.That(Screen.height, Is.EqualTo(height));
                ScreenCapture.CaptureScreenshot(Path.Combine(Root, filename));
                yield return new WaitForSecondsRealtime(.5f);
            }
            finally
            {
                property.SetValue(window, previous);
                group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { index });
            }
        }
    }
}
