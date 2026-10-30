using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;

namespace EmbodiedNPC.Tests
{
    public sealed class ModelProfileSettingsTests
    {
        [Test] public void P3A_NewMainIsBlank_CustomFirst_PlaceholderCannotSubmit()
        {
            var view = (ModelProfilesView)Get("profilesView");
            view.main_presets = new[] { view.main_presets[0], Preset("custom", "openai_compatible", ""), view.main_presets[1] };
            Invoke("ChooseInitial", false);
            var dropdown = (TMP_Dropdown)Get("mainSupplier");
            Assert.AreEqual("custom", ((ModelPresetView[])Get("mainChoices"))[0].preset_id);
            Assert.AreEqual(-1, dropdown.value); Assert.IsNotNull(dropdown.placeholder);
            Assert.AreEqual("", ((TMP_InputField)Get("mainModel")).text);
            Assert.AreEqual("32768", ((TMP_InputField)Get("contextLength")).text);
            Assert.IsInstanceOf<ArgumentException>(Assert.Throws<TargetInvocationException>(()=>Invoke("BuildProfileCommand",false)).InnerException);
            Invoke("RequestSupplierSwitch", false, 0);
            Assert.AreEqual("openai_compatible", ((ModelPresetView[])Get("mainChoices"))[dropdown.value].protocol);
            Assert.AreEqual("main-custom", Get("mainEditingId"));
        }

        [TestCase("deepseek")] [TestCase("qwen")] [TestCase("custom")]
        public void P3A_SavedMainResolvesByPresetIdAfterDisplayReordering(string id)
        {
            var view=(ModelProfilesView)Get("profilesView");
            var preset=Preset(id,"openai_compatible","http://127.0.0.1:18846");
            view.main_presets=new[]{Preset("other","openai_compatible","https://unused.invalid"),preset};
            view.main_profiles=new[]{new MainProfileView{profile_id="existing-slot",preset_id=id,
                config=new MainModelConfig{provider_type="openai_compatible",base_url=preset.base_url,model="preserved-id",context_length=49152}}};
            view.selected_to_activate=new ModelActivationView{main_profile_id="existing-slot"};
            Invoke("ChooseInitial",false);
            Assert.AreEqual(id,((ModelPresetView[])Get("mainChoices"))[((TMP_Dropdown)Get("mainSupplier")).value].preset_id);
            Assert.AreEqual("existing-slot",Get("mainEditingId"));
            Assert.AreEqual("preserved-id",((TMP_InputField)Get("mainModel")).text);
            Assert.AreEqual("49152",((TMP_InputField)Get("contextLength")).text);
        }

        [Test] public void P3A_DiscoveryHasOnlyRealModelsAndOneChevronAfterRefresh()
        {
            var dropdown=(TMP_Dropdown)Get("mainCandidates");
            for(int i=0;i<3;i++)
            {
                FirstRunModelSetup.ApplyDiscoveredModels(dropdown,new[]{"one","two"}); Invoke("RefreshLanguage");
                CollectionAssert.AreEqual(new[]{"one","two"},dropdown.options.Select(o=>o.text));
                Assert.AreEqual(-1,dropdown.value);
                Assert.AreEqual(1,dropdown.transform.Cast<Transform>().Count(t=>t.name=="SettingsChevron"&&t.gameObject.activeSelf));
                Assert.IsFalse(dropdown.captionText!=null&&dropdown.captionText.enabled);
            }
            Invoke("OnMainCandidateChosen",0);Assert.AreEqual("one",((TMP_InputField)Get("mainModel")).text);
        }

        [Test] public void P3A_GroupsDoNotHideMainAddressRecentOrEmbeddingBehindGeneration()
        {
            Invoke("EnsureCompactLayout");
            var generation=((GameObject)Get("advancedSettings")).transform;
            Assert.IsFalse(generation.gameObject.activeSelf);
            Assert.IsTrue(((Component)Get("mainBaseUrl")).transform.IsChildOf((Transform)Get("mainConnectionGroup")));
            Assert.IsTrue(((Component)Get("recentMode")).transform.IsChildOf((Transform)Get("recentDialogueGroup")));
            Assert.IsTrue(((Component)Get("embeddingTimeout")).transform.IsChildOf((Transform)Get("vectorRetrievalGroup")));
            Assert.IsFalse(((Component)Get("mainProfileState")).gameObject.activeInHierarchy);
            var supplierLabel=((Component)Get("mainSupplier")).transform.parent.Find("Label").GetComponent<TMP_Text>();
            var modelLabel=((Component)Get("mainModel")).transform.parent.Find("Label").GetComponent<TMP_Text>();
            Assert.AreEqual(modelLabel.fontSize,supplierLabel.fontSize);
            Assert.AreEqual(modelLabel.enableAutoSizing,supplierLabel.enableAutoSizing);
            Assert.AreEqual(modelLabel.rectTransform.offsetMin,supplierLabel.rectTransform.offsetMin);
            string before=(string)Invoke("DraftSignature",false);
            Invoke("ToggleAdvancedSettings");Invoke("ToggleAdvancedSettings");
            Assert.AreEqual(before,Invoke("DraftSignature",false));
        }

        [Test] public void P3A_BriefStateDistinguishesAppliedDraftAndRestartWithoutIds()
        {
            Invoke("EnsureCompactLayout");var view=(ModelProfilesView)Get("profilesView");
            var activation=new ModelActivationView{main_profile_id="internal-private-id",embedding_profile_id="off",
                main_model=new MainModelConfig{provider_type="mock",model="runtime-model",context_length=16384},embedding=new EmbeddingConfig{enabled=false}};
            view.configured=true;view.active_runtime=activation;view.selected_to_activate=activation;
            Invoke("RefreshBriefAiState");
            string Read()=>((TMP_Text)Get("mainConnectionState")).text;
            Assert.That(Read(),Does.Contain(SettingsUiLanguage.IsEnglish?"Applied":"已生效"));
            Assert.That(Read(),Does.Not.Contain("internal-private-id"));
            ((TMP_InputField)Get("mainModel")).text="unsaved";
            Assert.That(Read(),Does.Contain(SettingsUiLanguage.IsEnglish?"Unsaved":"未保存"));
            Set("mainBaseline",Invoke("DraftSignature",false));view.restart_required=true;Invoke("RefreshBriefAiState");
            Assert.That(Read(),Does.Contain(SettingsUiLanguage.IsEnglish?"restart":"重启"));
        }

        [Test] public void PortableRestart_EditorNeverQuitsOrClaimsSupervision()
        {
            Assert.That(PortableAIConfigRestart.UnavailableReason, Does.Contain("Editor"));
            Assert.IsFalse(PortableAIConfigRestart.Request());
            Assert.IsFalse(PortableAIConfigRestart.Request());
            Assert.AreEqual(73, PortableAIConfigRestart.ExitCode);
        }

        private void PrepareRestartOffer()
        {
            ((ModelProfilesView)Get("profilesView")).restart_required = true;
            Set("modelSaveConfirmed", true); Set("restartOfferPending", true);
            Set("contextPolicyError", null);
            Assert.IsFalse((bool)Invoke("HasUnsavedSettings"));
            Assert.IsFalse((bool)Invoke("ContextPolicyDirty"));
        }

        [Test] public void PortableRestart_CafeSaveOnlyExplainsNextStartup()
        {
            PrepareRestartOffer(); Invoke("OfferSavedRestart");
            Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain(SettingsUiLanguage.IsEnglish ? "next time" : "下次启动"));
            Assert.IsNull(Get("profileModal")); Assert.IsFalse((bool)Get("restartExitRequested"));
        }

        [Test] public void PortableRestart_TitleInEditorExplainsManualRecovery()
        {
            PrepareRestartOffer(); int exits = 0; form.RestartToApplyRequested += () => exits++;
            Invoke("OfferSavedRestart");
            Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain("Editor"));
            Assert.IsNull(Get("profileModal")); Assert.AreEqual(0, exits);
        }

        [TestCase("busy", true)]
        [TestCase("contextSaveInProgress", true)]
        [TestCase("modelSaveConfirmed", false)]
        [TestCase("contextPolicyError", "CONTEXT_POLICY_APPLY_UNCONFIRMED")]
        public void PortableRestart_UnsettledOrFailedSaveNeverRequestsExit(string field, object value)
        {
            PrepareRestartOffer(); Set(field, value);
            int exits = 0; form.RestartToApplyRequested += () => exits++;
            Invoke("OfferSavedRestart"); Assert.IsNull(Get("profileModal")); Assert.AreEqual(0, exits);
            Assert.IsFalse((bool)Get("restartExitRequested"));
        }

        [Test] public void PortableRestart_UnchangedActiveDoesNotOfferRestart()
        {
            PrepareRestartOffer(); ((ModelProfilesView)Get("profilesView")).restart_required = false;
            Invoke("OfferSavedRestart"); Assert.IsNull(Get("profileModal"));
            Assert.IsFalse((bool)Get("restartExitRequested"));
        }

        [Test] public void PortableRestart_TitleHostTracksSavedPendingIndependentlyOfReady()
        {
            var hostType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CafeDemo.TitleSettingsHost")).FirstOrDefault(t => t != null);
            Assert.IsNotNull(hostType);
            var host = root.AddComponent(hostType);
            hostType.GetField("form", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(host, form);
            var profiles = (ModelProfilesView)Get("profilesView");
            profiles.restart_required = false; Assert.AreEqual(false, hostType.GetProperty("AIRequiresRestart").GetValue(host));
            profiles.restart_required = true; Assert.AreEqual(true, hostType.GetProperty("AIRequiresRestart").GetValue(host));
        }

        [Test] public void NewProfiles32k_DefaultsAllowSupportedContextPresets()
        {
            var view = (ModelProfilesView)Get("profilesView");
            var preset = Preset("custom", "openai_compatible", "https://gateway.invalid/v1");
            preset.default_context_length = 32768;
            view.main_presets = new[] { preset };
            Invoke("LoadSupplierOptions", false, 0, "custom"); Invoke("LoadEditingProfile", false);
            var field = (TMP_InputField)Get("contextLength");
            Assert.AreEqual("32768", field.text);
            Assert.AreEqual("1024", ((TMP_InputField)Get("maxOutputTokens")).text);
            Assert.IsTrue(field.transform.parent.gameObject.activeSelf);
            Invoke("OnContextPresetChosen", 1);
            ((TMP_InputField)Get("mainModel")).text = "arbitrary-model-name";
            Assert.AreEqual("32768", field.text);
            var local = Preset("ollama", "ollama", "http://127.0.0.1:11434");
            local.source = "local"; local.default_context_length = 32768;
            view.main_presets = new[] { local };
            Invoke("LoadSupplierOptions", false, 1, "ollama"); Invoke("LoadEditingProfile", false);
            Assert.AreEqual("32768", field.text);
        }

        [Test] public void ContextPresets_AllowSmallerModelsAndRetainLegalCustomValues()
        {
            var field = (TMP_InputField)Get("contextLength");
            var presets = (TMP_Dropdown)Get("contextPresets");
            Assert.IsFalse(field.readOnly);
            Assert.IsTrue(presets.interactable);
            CollectionAssert.AreEqual(new[] { "16K", SettingsUiLanguage.T("32K（推荐）", "32K (Recommended)"), "64K" }, presets.options.ConvertAll(o => o.text));
            for (int i = 0; i < 3; i++)
            {
                Invoke("OnContextPresetChosen", i);
                Assert.AreEqual((16384 << i).ToString(), field.text);
                Assert.AreEqual(i, presets.value);
                Invoke("BuildProfileCommand", false);
            }
            field.text = "16384";
            Invoke("OnContextLengthChanged", field.text);
            Assert.AreEqual(0, presets.value);
            foreach (string custom in new[] { "8192", "4096", "12000", "49152", "52000", "131072", "1000000" })
            {
                field.text = custom; Invoke("OnContextLengthChanged", field.text);
                string command = Invoke("BuildProfileCommand", false).ToString();
                Assert.That(command, Does.Contain("\"context_length\": " + custom));
                Assert.AreEqual(3, presets.value);
            }
            foreach (string invalid in new[] { "0", "-1", "not-a-number", "1000001" })
            {
                field.text = invalid;
                Assert.IsInstanceOf<ArgumentException>(Assert.Throws<TargetInvocationException>(
                    () => Invoke("BuildProfileCommand", false)).InnerException);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ContextRecommendation_LocalizesWithoutChangingManual8k(bool english)
        {
            bool previous = SettingsUiLanguage.IsEnglish;
            try
            {
                Set("mainConnectionState", Control<TextMeshProUGUI>("capacityState", main));
                Set("embeddingConnectionState", Control<TextMeshProUGUI>("embeddingState", embedding));
                var field = (TMP_InputField)Get("contextLength");
                field.text = "8192";
                SettingsUiLanguage.SetEnglish(english);
                Invoke("RefreshLanguage");
                var presets = (TMP_Dropdown)Get("contextPresets");
                CollectionAssert.AreEqual(new[] { "16K", english ? "32K (Recommended)" : "32K（推荐）", "64K", "8K" },
                    presets.options.ConvertAll(o => o.text));
                Assert.AreEqual("8192", field.text);
                Assert.AreEqual(3, presets.value);
                Invoke("RefreshContextCapacityState");
                string capacity = ((TMP_Text)(Get("mainCapacityState") ?? Get("mainConnectionState"))).text;
                StringAssert.Contains("32K", capacity);
                StringAssert.Contains("64K", capacity);
                Assert.That(capacity, Does.Contain(english ? "not a capability guarantee" : "不代表模型支持"));
            }
            finally { SettingsUiLanguage.SetEnglish(previous); }
        }

        [Test] public void SavedCloud8k_DoesNotAdoptNewDefaultOnReload()
        {
            var view = (ModelProfilesView)Get("profilesView");
            var preset = Preset("custom", "openai_compatible", "https://gateway.invalid/v1");
            preset.default_context_length = 32768;
            view.main_presets = new[] { preset };
            view.main_profiles = new[] { new MainProfileView { profile_id = "main-custom", preset_id = "custom",
                config = new MainModelConfig { provider_type = "openai_compatible", model = "saved-model",
                    base_url = preset.base_url, context_length = 8192, max_output_tokens = 512 } } };
            Invoke("LoadSupplierOptions", false, 0, "custom"); Invoke("LoadEditingProfile", false);
            Assert.AreEqual("8192", ((TMP_InputField)Get("contextLength")).text);
            Assert.AreEqual("512", ((TMP_InputField)Get("maxOutputTokens")).text);
            form.PrepareUnconfiguredDefaults();
            Assert.AreEqual("8192", ((TMP_InputField)Get("contextLength")).text);
            Assert.IsTrue(((TMP_Dropdown)Get("contextPresets")).options.Any(o => o.text == "8K"));
            string command = Invoke("BuildProfileCommand", false).ToString();
            Assert.That(command, Does.Contain("\"context_length\": 8192"));
            Assert.That(command, Does.Contain("\"key_action\": \"retain\""));
        }

        [TestCase(8192)] [TestCase(16384)] [TestCase(32768)] [TestCase(65536)] [TestCase(49152)] [TestCase(52000)] [TestCase(0)]
        public void SavedCapacityAndActiveRemainUnchangedWhileDraftChanges(int capacity)
        {
            var view = (ModelProfilesView)Get("profilesView");
            var preset = Preset("custom", "openai_compatible", "https://gateway.invalid/v1");
            view.main_presets = new[] { preset };
            var saved = new MainModelConfig { provider_type = "openai_compatible", model = "saved",
                base_url = preset.base_url, context_length = capacity, key_present = true };
            view.main_profiles = new[] { new MainProfileView { profile_id = "main-custom", preset_id = "custom", config = saved } };
            view.active_runtime = new ModelActivationView { main_profile_id = "main-custom", main_model = saved };
            Set("mainConnectionState", Control<TextMeshProUGUI>("CapacityStatus", main));
            Set("embeddingConnectionState", Control<TextMeshProUGUI>("EmbeddingStatus", embedding));
            Invoke("LoadSupplierOptions", false, 0, "custom"); Invoke("LoadEditingProfile", false);
            Assert.AreEqual((capacity > 0 ? capacity : 8192).ToString(), ((TMP_InputField)Get("contextLength")).text);
            string command = Invoke("BuildProfileCommand", false).ToString();
            Assert.That(command, Does.Contain("\"context_length\": " + (capacity == 0 ? "null" : capacity.ToString())));
            string frozen = JsonUtility.ToJson(view);
            Invoke("OnContextPresetChosen", 0); Invoke("OnContextLengthChanged", "16384");
            Assert.AreEqual(frozen, JsonUtility.ToJson(view));
            string status = ((TMP_Text)Get("mainConnectionState")).text;
            Assert.That(status, Does.Contain("16384"));
            Assert.That(status, Does.Contain(capacity > 0 ? capacity.ToString() : "8192"));
            Assert.IsTrue(status.Contains("尚未验证") || status.Contains("unverified"));
            if (capacity == 0) Assert.IsTrue(status.Contains("最低 16K") || status.Contains("recommended 16K"));
        }

        [Test] public void ContextBudgetFeedback_HasConfigurationRouteWithoutClaimingCapacityProof()
        {
            foreach (string code in new[] { "CONTEXT_BUDGET_EXCEEDED", "COGNITIVE_PROMPT_BUDGET_EXCEEDED" })
            {
                var message = P0FeedbackText.Dialogue(code);
                Assert.That(message.Chinese, Does.Contain("AI 配置 → 高级设置"));
                Assert.That(message.Chinese, Does.Contain("NPC 上下文容量不足"));
                Assert.That(message.English, Does.Contain("larger context window"));
            }
        }

        private GameObject root;
        private FirstRunModelSetup form;
        private Transform main, embedding;
        private readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private object Get(string name) => typeof(FirstRunModelSetup).GetField(name, flags).GetValue(form);
        private void Set(string name, object value) => typeof(FirstRunModelSetup).GetField(name, flags).SetValue(form, value);
        private object Invoke(string name, params object[] values) => typeof(FirstRunModelSetup).GetMethod(name, flags | BindingFlags.Static).Invoke(form, values);

        [SetUp] public void Setup()
        {
            root = new GameObject("SyntheticModelProfileForm", typeof(RectTransform)); root.SetActive(false);
            form = root.AddComponent<FirstRunModelSetup>(); Set("developerMode", true); Set("panel", root);
            main = new GameObject("Main", typeof(RectTransform), typeof(VerticalLayoutGroup)).transform; main.SetParent(root.transform);
            embedding = new GameObject("EmbeddingFields", typeof(RectTransform), typeof(VerticalLayoutGroup)).transform; embedding.SetParent(main);
            Set("embeddingFields", embedding.gameObject);
            var connections = new GameObject("EmbeddingConnections", typeof(RectTransform)); connections.transform.SetParent(embedding);
            Set("embeddingConnectionFields", connections);
            foreach (string field in new[] { "mainBaseUrl", "mainApiKey", "mainModel", "temperature", "maxOutputTokens", "contextLength", "mainTimeout",
                "embeddingBaseUrl", "embeddingApiKey", "embeddingModel", "embeddingTimeout" })
                Set(field, Control<TMP_InputField>(field, field.StartsWith("embedding") ? embedding : main));
            foreach (string field in new[] { "mainProvider", "mainCandidates", "embeddingProvider", "embeddingCandidates" })
                Set(field, Control<TMP_Dropdown>(field, field.StartsWith("embedding") ? embedding : main));
            foreach (string field in new[] { "thinking", "embeddingEnabled", "reuseMainApi" }) Set(field, Control<Toggle>(field, field == "thinking" ? main : embedding));
            foreach (string field in new[] { "saveButton", "restartBackendButton", "recheckButton", "discoverMainButton", "testMainButton", "discoverEmbeddingButton", "testEmbeddingButton" })
                Set(field, Control<Button>(field, main));
            Set("statusText", Control<TextMeshProUGUI>("Status", main));
            Invoke("EnsureControls");
            Set("profilesView", new ModelProfilesView { schema_version = 2, revision = 1, main_profiles = Array.Empty<MainProfileView>(), embedding_profiles = Array.Empty<EmbeddingProfileView>(),
                default_main_preset_id = "deepseek", default_embedding_preset_id = "aliyun", main_presets = new[] {
                    Preset("deepseek", "deepseek_official_responses", "https://main.invalid"), Preset("qwen", "openai_compatible", "https://qwen.invalid/v1") },
                embedding_presets = new[] { Preset("aliyun", "openai_compatible", "https://embed.invalid/v1") } });
            Invoke("EnsureProfileControls"); Invoke("ChooseInitial", false); Invoke("ChooseInitial", true);
            // Existing contract cases explicitly choose their synthetic provider; product first-run remains blank.
            Invoke("LoadSupplierOptions", false, 0, "deepseek"); Invoke("LoadEditingProfile", false); Set("initialMainPresetId", "deepseek");
        }
        private ModelPresetView Preset(string id, string protocol, string endpoint) => new ModelPresetView { preset_id = id, name = id,
            source = "cloud", protocol = protocol, base_url = endpoint, schema_profile = "raw", thinking_parameter = "none", token_parameter = "max_tokens", send_temperature = true };
        private T Control<T>(string name, Transform parent) where T : Component
        {
            var row = new GameObject(name + "Row", typeof(RectTransform)); row.transform.SetParent(parent);
            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); label.transform.SetParent(row.transform);
            var go = new GameObject(name, typeof(RectTransform), typeof(T)); go.transform.SetParent(row.transform);
            T control = go.GetComponent<T>();
            if (control is Button button) { var caption = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI)); caption.transform.SetParent(go.transform); }
            return control;
        }
        [TearDown] public void Cleanup() => UnityEngine.Object.DestroyImmediate(root);

        [Test] public void P3D_DiscoveryUpdatesOnDraftChangesAndExplainsMissingFields()
        {
            bool original=SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(false);Invoke("EnsureCompactLayout");
                var supplier=(TMP_Dropdown)Get("mainSupplier");var button=(Button)Get("discoverMainButton");
                supplier.SetValueWithoutNotify(-1);Invoke("RefreshBriefAiState");Assert.IsTrue(button.interactable);
                Assert.IsFalse(((IEnumerator)Invoke("ProbeCurrentProfile",false,"discover")).MoveNext());
                Assert.That(((TMP_Text)Get("statusText")).text,Does.Contain("供应商"));
                supplier.value=0;((TMP_InputField)Get("mainBaseUrl")).text="";Assert.IsTrue(button.interactable);
                Assert.IsFalse(((IEnumerator)Invoke("ProbeCurrentProfile",false,"discover")).MoveNext());
                Assert.That(((TMP_Text)Get("statusText")).text,Does.Contain("API 地址"));
                ((TMP_InputField)Get("mainBaseUrl")).text="https://synthetic.invalid";Set("mainKeyPresent",false);
                Assert.IsFalse(((IEnumerator)Invoke("ProbeCurrentProfile",false,"discover")).MoveNext());
                Assert.That(((TMP_Text)Get("statusText")).text,Does.Contain("API 密钥"));
                Set("defaultsRestored",true);Invoke("RefreshCandidateControls");Assert.IsFalse(button.interactable);
                Assert.IsFalse(((IEnumerator)Invoke("ProbeCurrentProfile",false,"discover")).MoveNext());
                Assert.That(((TMP_Text)Get("statusText")).text,Does.Contain("旧配置"));
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        private ModelProfilesView AddBuiltin(bool newUser = true)
        {
            var view = (ModelProfilesView)Get("profilesView");
            var builtin = Preset("builtin_gemma", "builtin", ""); builtin.source = "local";
            view.embedding_presets = new[] { view.embedding_presets[0], builtin };
            view.builtin_embedding_defaults = new EmbeddingConfig { provider_type = "builtin", reuse_main_api = false,
                model = "embeddinggemma-300m-q4", dimensions = 768, model_version = "pinned-revision",
                resource_directory = "C:/synthetic-models", resource_status = "missing" };
            view.builtin_resource_status = "missing";
            if (newUser) { view.revision = 0; view.default_embedding_preset_id = "builtin_gemma"; }
            Invoke("ChooseInitial", true); Invoke("EnsureCompactLayout");
            return view;
        }

        [TestCase(false)] [TestCase(true)]
        public void BuiltinLifecycleCopyDistinguishesDraftAppliedLoadingAndUserChoice(bool english)
        {
            var view=AddBuiltin(); view.builtin_embedding_defaults.resource_status="unverified";
            view.builtin_resource_status="unverified";
            string before=JsonUtility.ToJson(view), draft=(string)Invoke("DraftSignature",true);
            string caption=(string)Invoke("BuiltinSetupCaption",english);
            Assert.That(caption,Does.Contain(english?"first main-model save":"首次保存主模型配置"));
            Assert.That(caption,Does.Not.Contain(english?"is loaded":"已加载"));
            Assert.AreEqual(before,JsonUtility.ToJson(view));Assert.AreEqual(draft,Invoke("DraftSignature",true));
            view.configured=true;
            Assert.That((string)Invoke("BuiltinSetupCaption",english),Does.Contain(english?"next main-model save":"下次保存主模型配置"));
            view.configured=false;
            view.embedding_profiles=new[]{new EmbeddingProfileView{profile_id="embedding-builtin_gemma",preset_id="builtin_gemma",config=view.builtin_embedding_defaults}};
            view.selected_to_activate=new ModelActivationView{embedding_profile_id="embedding-builtin_gemma",embedding=view.builtin_embedding_defaults};
            Invoke("ChooseInitial",true);
            Assert.That((string)Invoke("BuiltinSetupCaption",english),Does.Contain(english?"is saved":"已保存"));
            view.active_runtime=view.selected_to_activate;
            foreach(var state in new[]{"unloaded","ready","failed","unknown"})
            {
                Set("currentIndex",new EmbeddingIndexView{status="waiting",applied=true,provider_status=state});
                caption=(string)Invoke("BuiltinSetupCaption",english);
                string expected=state switch{ "unloaded"=>english?"not loaded yet":"尚未加载", "ready"=>english?"is loaded":"已加载",
                    "failed"=>english?"failed":"失败", _=>english?"unconfirmed":"尚未确认"};
                Assert.That(caption,Does.Contain(expected),state);
                if(state!="ready")Assert.That(caption,Does.Not.Contain(english?"is loaded":"已加载"));
            }
            view.builtin_embedding_defaults.resource_status="missing";
            Assert.That((string)Invoke("BuiltinSetupCaption",english),Does.Contain(english?"missing":"缺失"));
            ((Toggle)Get("embeddingEnabled")).SetIsOnWithoutNotify(false);
            Assert.IsEmpty((string)Invoke("BuiltinSetupCaption",english),"Explicit off must not advertise default enablement.");
            view.builtin_embedding_defaults.resource_status="unverified";
            var ollama=Preset("ollama","ollama","http://synthetic.invalid");ollama.source="local";
            view.embedding_presets=view.embedding_presets.Concat(new[]{ollama}).ToArray();
            Invoke("ConfigureEmbeddingSuppliers",1,"builtin_gemma");
            Invoke("SelectEmbeddingCategory",1);
            Assert.AreEqual("ollama",Get("embeddingEditingPresetId"));
            Assert.IsEmpty((string)Invoke("BuiltinSetupCaption",english),"Ollama keeps its existing provider description.");
            Invoke("SelectEmbeddingCategory",2);
            Assert.AreEqual("aliyun",Get("embeddingEditingPresetId"));
            Assert.IsEmpty((string)Invoke("BuiltinSetupCaption",english),"Cloud keeps its existing provider description.");
        }

        [TestCase(false)] [TestCase(true)] public void BuiltinFirstInstall_FixedFieldsNoCredentialsAndNoActivation(bool english)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(english); var view = AddBuiltin();
                Assert.AreEqual("embedding-builtin_gemma", Get("embeddingEditingId"));
                Assert.IsTrue(((Toggle)Get("embeddingEnabled")).isOn);
                Assert.IsNull(view.active_runtime); Assert.IsFalse(view.configured);
                Assert.AreEqual("EmbeddingGemma 300M Q4", ((TMP_InputField)Get("embeddingModel")).text);
                Assert.IsFalse(((TMP_InputField)Get("embeddingModel")).interactable);
                foreach (string field in new[] { "embeddingBaseUrl", "embeddingApiKey", "embeddingDimensions", "embeddingVersion", "embeddingTimeout" })
                    Assert.IsFalse(((Component)Get(field)).transform.parent.gameObject.activeSelf, field);
                Assert.That(((Toggle[])Get("embeddingCategories"))[0].GetComponentInChildren<TMP_Text>(true).text,
                    Is.EqualTo(english ? "Built-in model (new configuration default) · EmbeddingGemma 300M Q4" : "内置模型（新配置默认） · EmbeddingGemma 300M Q4"));
                Assert.IsNull(embedding.Find("BuiltinEmbeddingInformation"));
                Assert.IsFalse(((TMP_Text)Get("embeddingConnectionState")).gameObject.activeSelf);
                Assert.That(((TMP_Text)Get("indexState")).text, Does.Contain(english ? "missing" : "缺失"));
                string json = Invoke("BuildProfileCommand", true).ToString();
                var command = JsonUtility.FromJson<BuiltinCommand>(json);
                Assert.AreEqual("builtin", command.config.provider_type);
                Assert.AreEqual("embeddinggemma-300m-q4", command.config.model);
                Assert.AreEqual(768, command.config.dimensions);
                Assert.That(json, Does.Not.Contain("base_url")); Assert.That(json, Does.Not.Contain("api_key"));
                Assert.That(json, Does.Not.Contain("resource_status"));
                Assert.AreEqual("retain", command.key_action);
                Assert.That(((TMP_Text)Get("embeddingConnectionState")).text, Does.Contain(english ? "missing" : "缺失"));
                Assert.That(((TMP_Text)Get("embeddingConnectionState")).text, Does.Not.Contain(english ? "Model loaded" : "模型已加载"));
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        [TestCase("ollama", false)] [TestCase("openai_compatible", true)]
        public void BuiltinOption_DoesNotMigrateExistingActivationOrSavedKey(string provider, bool key)
        {
            var view = AddBuiltin(false);
            view.revision = 7; view.configured = true;
            string preset = provider == "ollama" ? "ollama" : "aliyun";
            var external = Preset(preset, provider, provider == "ollama" ? "http://localhost:11434" : "https://embed.invalid/v1");
            external.source = provider == "ollama" ? "local" : "cloud";
            view.embedding_presets = new[] { external, view.embedding_presets[1] };
            var config = new EmbeddingConfig { provider_type = provider, model = "existing-vector", base_url = external.base_url, key_present = key };
            view.embedding_profiles = new[] { new EmbeddingProfileView { profile_id = "existing-embedding", preset_id = preset, config = config } };
            view.selected_to_activate = view.active_runtime = new ModelActivationView { embedding_profile_id = "existing-embedding", embedding = config };
            Invoke("ChooseInitial", true);
            Assert.AreEqual("existing-embedding", Get("embeddingEditingId")); Assert.AreSame(config, view.active_runtime.embedding);
            Assert.AreEqual(key, Get("embeddingKeyPresent"));
            Invoke("RequestSupplierSwitch", true, 1); Assert.AreSame(config, view.active_runtime.embedding);
            Invoke("RequestSupplierSwitch", true, 0);
            Assert.AreEqual("existing-vector", ((TMP_InputField)Get("embeddingModel")).text);
            Assert.AreEqual(key, Get("embeddingKeyPresent"));
        }

        [TestCase("unloaded", null, "尚未加载")]
        [TestCase("ready", null, "已加载")]
        [TestCase("failed", "BUILTIN_EMBEDDING_FILE_MISSING", "资源缺失")]
        [TestCase("failed", "BUILTIN_EMBEDDING_SHA_MISMATCH", "SHA 不匹配")]
        [TestCase("failed", "BUILTIN_EMBEDDING_ONNX_INIT_FAILED", "加载 / 推理失败")]
        public void BuiltinRuntimeStatus_IsAuthoritative(string status, string code, string expected)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(false); var view = AddBuiltin();
                view.active_runtime = new ModelActivationView { embedding = view.builtin_embedding_defaults };
                view.active_runtime.embedding.resource_status = "unverified";
                view.restart_required = true;
                Set("currentIndex", new EmbeddingIndexView { status = "waiting", provider_status = status, provider_error_code = code, pending = 3, counts_known = true });
                Invoke("RefreshIndexState");
                string runtime = ((TMP_Text)Get("embeddingConnectionState")).text;
                string text = runtime + ((TMP_Text)Get("indexState")).text;
                Assert.That(text, Does.Contain(expected)); Assert.That(text, Does.Contain("待重启"));
                Assert.That(text, Does.Contain("历史记忆仍保留"));
                if (status != "ready") Assert.That(runtime, Does.Not.Contain(" · 已加载"));
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        [Test] public void BuiltinFixedConfig_IgnoresHiddenDraftParametersAndRetainsExternalDraftOnCollapse()
        {
            AddBuiltin();
            ((TMP_InputField)Get("embeddingDimensions")).text = "invalid";
            ((TMP_InputField)Get("embeddingBaseUrl")).text = "https://not-used.invalid";
            ((TMP_InputField)Get("embeddingApiKey")).text = "synthetic-not-used";
            string json = Invoke("BuildProfileCommand", true).ToString();
            var command = JsonUtility.FromJson<BuiltinCommand>(json);
            Assert.AreEqual(768, command.config.dimensions); Assert.That(json, Does.Not.Contain("api_key"));
            Assert.AreEqual("retain", command.key_action);
            string draft = (string)Invoke("DraftSignature", true);
            Invoke("ToggleAdvancedSettings"); Invoke("RefreshLanguage"); Invoke("ToggleAdvancedSettings");
            Assert.AreEqual(draft, Invoke("DraftSignature", true));
        }

        [Serializable] private sealed class BuiltinCommand { public string key_action; public EmbeddingConfig config; }

        [Test] public void SavedBuiltinOptionalPins_RemainBackendControlledOnSave()
        {
            var view = AddBuiltin();
            view.builtin_embedding_defaults.dimensions = 0;
            view.builtin_embedding_defaults.model_version = null;
            string json = Invoke("BuildProfileCommand", true).ToString();
            Assert.That(json, Does.Contain("\"dimensions\": null"));
            Assert.That(json, Does.Contain("\"model_version\": null"));
            Assert.That(json, Does.Not.Contain("api_key"));
        }

        [Test] public void SavedBuiltinResourceLocation_IsPreservedInsteadOfDeploymentDefault()
        {
            var view = AddBuiltin();
            view.embedding_profiles = new[] { new EmbeddingProfileView { profile_id = "embedding-builtin_gemma", preset_id = "builtin_gemma",
                config = new EmbeddingConfig { provider_type = "builtin", model = "embeddinggemma-300m-q4",
                    dimensions = 768, resource_directory = "C:/already-installed-resources", resource_status = "unverified" } } };
            Invoke("RefreshProfileStates");
            Assert.That(((TMP_Text)Get("embeddingConnectionState")).text, Does.Not.Contain("资源缺失"));
            Assert.That(Invoke("BuildProfileCommand", true).ToString(), Does.Contain("C:/already-installed-resources"));
        }

        [Test] public void ExistingDisabledDraft_PreservesCloudDefaultsWithoutActivationOrKeys()
        {
            Assert.AreEqual(0, ((TMP_Dropdown)Get("mainProvider")).value);
            Assert.AreEqual(0, ((TMP_Dropdown)Get("embeddingProvider")).value);
            Assert.AreEqual("main-deepseek", Get("mainEditingId")); Assert.AreEqual("embedding-aliyun", Get("embeddingEditingId"));
            Assert.IsFalse(((Toggle)Get("embeddingEnabled")).isOn);
            Assert.IsFalse(((Toggle)Get("reuseMainApi")).isOn);
            Assert.AreEqual("", ((TMP_InputField)Get("mainApiKey")).text);
            Assert.AreEqual("", ((TMP_InputField)Get("embeddingApiKey")).text);
            Assert.AreEqual("https://embed.invalid/v1", ((TMP_InputField)Get("embeddingBaseUrl")).text);
        }

        private ModelProfilesView AddAllEmbeddingProviders()
        {
            var view = AddBuiltin(false);
            var ollama = Preset("ollama", "ollama", "http://127.0.0.1:11434"); ollama.source = "local";
            var gemini = Preset("gemini", "gemini_native", "https://native.invalid"); gemini.discovery = "gemini";
            view.embedding_presets = new[] { view.embedding_presets[0], view.embedding_presets[1], ollama, gemini };
            Invoke("LoadSupplierOptions", true, 1, "ollama"); Invoke("LoadEditingProfile", true);
            return view;
        }

        [TestCase(false)] [TestCase(true)]
        public void FirstEmbeddingDraft_WithoutEmbeddingState_SavesBothWithoutProviderRoundtrip(bool savedMain)
        {
            var view = AddBuiltin();
            view.revision = savedMain ? 3 : 0;
            view.default_embedding_preset_id = savedMain ? "aliyun" : "builtin_gemma";
            if (savedMain)
                view.main_profiles = new[] { new MainProfileView { profile_id = "kept-main", preset_id = "deepseek",
                    config = new MainModelConfig { provider_type = "deepseek_official_responses", base_url = "https://main.invalid",
                        model = "kept-main-model", context_length = 16384, key_present = true } } };
            Invoke("ChooseInitial", false); Invoke("ChooseInitial", true);
            Assert.IsTrue(((Toggle)Get("embeddingEnabled")).isOn);
            Assert.AreEqual("builtin_gemma", Get("embeddingEditingPresetId"));
            if (savedMain)
            {
                Assert.AreEqual("kept-main", Get("mainEditingId"));
                Assert.AreEqual("kept-main-model", ((TMP_InputField)Get("mainModel")).text);
                Assert.IsTrue((bool)Get("mainKeyPresent"));
            }
            else
            {
                Invoke("LoadSupplierOptions", false, 0, "deepseek"); Invoke("LoadEditingProfile", false);
                ((TMP_InputField)Get("mainModel")).text = "new-main-model";
            }
            Set("currentIndex", new EmbeddingIndexView { status = "disabled", applied = false });
            Invoke("RefreshMemoryIndex");
            Assert.That(((TMP_Text)Get("embeddingBrief")).text, Does.Contain("尚未保存").Or.Contain("not saved"));
            var command = Newtonsoft.Json.Linq.JObject.Parse(Invoke("BuildCombinedProfileCommand").ToString());
            Assert.IsTrue((bool)command["embedding_enabled"]);
            Assert.AreEqual("builtin_gemma", (string)command["embedding"]["preset_id"]);
            Assert.AreEqual("builtin", (string)command["embedding"]["config"]["provider_type"]);
            Assert.AreEqual("retain", (string)command["main"]["key_action"]);
            Assert.IsNull(view.selected_to_activate); Assert.IsNull(view.active_runtime);
            // A save response can arrive before the next index poll. An old
            // applied=true must not make a newly selected snapshot look active.
            view.selected_to_activate = new ModelActivationView { embedding_profile_id = "embedding-builtin_gemma",
                embedding = view.builtin_embedding_defaults };
            ((EmbeddingIndexView)Get("currentIndex")).applied = true;
            Invoke("RefreshMemoryIndex");
            Assert.That(((TMP_Text)Get("embeddingBrief")).text, Does.Contain("待重启").Or.Contain("restart to apply"));
        }

        [Test] public void SavedEmbeddingOff_WithoutProfile_IsNotTreatedAsMissingConfiguration()
        {
            var view = AddBuiltin(); view.revision = 2; view.configured = true;
            var off = new EmbeddingConfig { enabled = false };
            view.selected_to_activate = view.active_runtime = new ModelActivationView { embedding = off };
            Invoke("ChooseInitial", true);
            Assert.IsFalse(((Toggle)Get("embeddingEnabled")).isOn);
            Assert.AreSame(off, view.active_runtime.embedding);
        }

        [Test] public void FourEmbeddingModes_SwitchImmediatelyAndRetainEveryEmbeddingDraft()
        {
            var view = AddAllEmbeddingProviders();
            // Exercise mode switching from an explicitly saved off, rather than
            // inferring an Embedding choice from the document revision alone.
            view.selected_to_activate = view.active_runtime = new ModelActivationView { embedding = new EmbeddingConfig { enabled = false } };
            var savedActivation = view.active_runtime;
            Invoke("ChooseInitial", true);
            var categories = (Toggle[])Get("embeddingCategories");
            Assert.AreEqual(4, categories.Length); Assert.IsTrue(categories[3].isOn);
            categories[1].isOn = true;
            ((TMP_InputField)Get("embeddingModel")).text = "unsaved-ollama-model";
            ((TMP_InputField)Get("embeddingTimeout")).text = "41";
            ((TMP_InputField)Get("embeddingDimensions")).text = "1234";
            string ollamaDraft = (string)Invoke("DraftSignature", true);
            categories[0].isOn = true;
            Assert.AreEqual(1, Array.FindAll(categories, t => t.isOn).Length);
            Assert.IsFalse(((Component)Get("embeddingBaseUrl")).transform.parent.gameObject.activeSelf);
            Assert.IsFalse(((Component)Get("embeddingModel")).transform.parent.gameObject.activeSelf);
            Assert.IsFalse(((Button)Get("testEmbeddingButton")).gameObject.activeSelf);
            Assert.IsNull(Get("profileModal")); Assert.AreEqual(1, view.revision);
            categories[2].isOn = true;
            ((TMP_InputField)Get("embeddingModel")).text = "cloud-draft";
            ((TMP_InputField)Get("embeddingApiKey")).text = "synthetic-unsaved-key";
            string cloudDraft = (string)Invoke("DraftSignature", true);
            categories[1].isOn = true;
            Assert.AreEqual(ollamaDraft, Invoke("DraftSignature", true));
            Assert.IsFalse(((Component)Get("embeddingApiKey")).transform.parent.gameObject.activeSelf);
            categories[2].isOn = true;
            Assert.AreEqual(cloudDraft, Invoke("DraftSignature", true));
            Assert.IsTrue(((Component)Get("embeddingApiKey")).transform.parent.gameObject.activeSelf);
            var cloud = (TMP_Dropdown)Get("embeddingSupplier");
            Assert.AreEqual(2, cloud.options.Count); cloud.value = 1;
            Assert.That(Invoke("BuildProfileCommand", true).ToString(), Does.Contain("gemini_native"));
            Assert.AreSame(savedActivation, view.active_runtime); Assert.AreSame(savedActivation, view.selected_to_activate);
            Assert.IsFalse(savedActivation.embedding.enabled); Assert.IsNull(Get("profileModal"));
        }

        [Test] public void OffMode_PreservesConnectionKeyAndIndexDraft_AndKeepsSelectorReachable()
        {
            var view = AddAllEmbeddingProviders(); var modes = (Toggle[])Get("embeddingCategories");
            modes[2].isOn = true;
            ((TMP_InputField)Get("embeddingModel")).text = "draft-vector";
            ((TMP_InputField)Get("embeddingApiKey")).text = "synthetic-draft-only";
            var index = new EmbeddingIndexView { status = "paused", applied = true, pending = 5, total = 9, model = "active-vector" };
            Set("currentIndex", index);
            string beforeView = JsonUtility.ToJson(view);
            string id = (string)Get("embeddingEditingPresetId");
            modes[3].isOn = true;
            Assert.IsFalse(((Toggle)Get("embeddingEnabled")).isOn);
            Assert.IsFalse(((GameObject)Get("embeddingFields")).activeSelf);
            Assert.IsFalse(((Toggle)Get("embeddingEnabled")).transform.parent.gameObject.activeSelf);
            Assert.IsFalse(((Toggle)Get("reuseMainApi")).transform.parent.gameObject.activeSelf);
            Assert.IsFalse(modes[3].transform.IsChildOf(((GameObject)Get("embeddingFields")).transform));
            Assert.AreEqual(id, Get("embeddingEditingPresetId"));
            Assert.AreEqual("draft-vector", ((TMP_InputField)Get("embeddingModel")).text);
            Assert.AreEqual("synthetic-draft-only", ((TMP_InputField)Get("embeddingApiKey")).text);
            string json = Invoke("BuildCombinedProfileCommand").ToString();
            Assert.IsFalse(JsonUtility.FromJson<CombinedCommand>(json).embedding_enabled);
            Assert.That(json, Does.Contain("replace"));
            Assert.That(((TMP_Text)Get("embeddingModeInformation")).text, Does.Contain("History").Or.Contain("历史记忆"));
            modes[2].isOn = true;
            Assert.AreEqual("synthetic-draft-only", ((TMP_InputField)Get("embeddingApiKey")).text);
            Assert.AreEqual(beforeView, JsonUtility.ToJson(view), "Mode changes must not alter saved, selected, or active snapshots.");
            Assert.AreSame(index, Get("currentIndex")); Assert.AreEqual(5, index.pending);
            Assert.IsNull(Get("indexPoll")); Assert.IsNull(Get("profileModal"));
        }

        [TestCase("ollama", true)] [TestCase("openai_compatible", true)] [TestCase("openai_compatible", false)]
        public void ExistingConfiguration_EvenAtRevisionZero_IsNotReplacedByBuiltin(string provider, bool enabled)
        {
            var view = AddAllEmbeddingProviders(); view.revision = 0; view.configured = true;
            string preset = provider == "ollama" ? "ollama" : "aliyun";
            var config = new EmbeddingConfig { enabled = enabled, provider_type = provider, model = "saved-vector",
                base_url = provider == "ollama" ? "http://saved.invalid:11434" : "https://saved.invalid/v1", key_present = true };
            view.embedding_profiles = new[] { new EmbeddingProfileView { profile_id = "saved-embedding", preset_id = preset, config = config } };
            view.selected_to_activate = view.active_runtime = new ModelActivationView { embedding_profile_id = "saved-embedding", embedding = config };
            Invoke("ChooseInitial", true);
            Assert.AreEqual(preset, Get("embeddingEditingPresetId"));
            Assert.AreEqual("saved-vector", ((TMP_InputField)Get("embeddingModel")).text);
            Assert.AreEqual(config.base_url, ((TMP_InputField)Get("embeddingBaseUrl")).text);
            Assert.AreEqual(true, Get("embeddingKeyPresent"));
            Assert.IsTrue(((Toggle[])Get("embeddingCategories"))[enabled ? provider == "ollama" ? 1 : 2 : 3].isOn);
            Assert.AreSame(config, view.active_runtime.embedding);
        }

        [Test] public void LegacyReuseProjection_ShowsFrozenEffectiveConnectionAndRetainsKey()
        {
            var view = AddAllEmbeddingProviders(); view.revision = 6; view.configured = true; view.migration_required = true;
            view.migration_notes = new[] { "V1_CONNECTIONS_FROZEN", "LEGACY_INDEX_IDENTITY_UNVERIFIED" };
            var config = new EmbeddingConfig { enabled = true, provider_type = "openai_compatible", reuse_main_api = false,
                base_url = "https://effective-legacy.invalid/v1", model = "legacy-vector", key_present = true };
            // Backend.as_v2 projects a v1 reuse document into this independent public connection.
            view.embedding_profiles = new[] { new EmbeddingProfileView { profile_id = "legacy-embedding", preset_id = "aliyun", config = config } };
            view.selected_to_activate = view.active_runtime = new ModelActivationView { embedding_profile_id = "legacy-embedding", embedding = config };
            Invoke("ChooseInitial", true);
            ((TMP_InputField)Get("mainBaseUrl")).text = "https://new-main-draft.invalid";
            Assert.AreEqual(config.base_url, ((TMP_InputField)Get("embeddingBaseUrl")).text);
            var command = JsonUtility.FromJson<KeySaveCommand>(Invoke("BuildProfileCommand", true).ToString());
            Assert.AreEqual("retain", command.key_action); Assert.IsNull(command.config.api_key);
            Assert.IsFalse(((Toggle)Get("reuseMainApi")).transform.parent.gameObject.activeSelf);
            Assert.That(((TMP_Text)Get("embeddingModeInformation")).text, Does.Contain("Legacy connections").Or.Contain("旧版连接"));
            Assert.That(((TMP_Text)Get("embeddingModeInformation")).text, Does.Contain("unverified").Or.Contain("尚未验证"));
            Assert.AreSame(config, view.active_runtime.embedding); Assert.AreEqual(6, view.revision);
        }

        [TestCase("Assets/CafeDemo/Prefabs/UI/TitleScreen.prefab")]
        [TestCase("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab")]
        public void ActualSharedPrefabs_BindTheSameFourModesWithoutNetworkOrActivation(string path)
        {
            var view = AddAllEmbeddingProviders(); view.revision = 0;
            var assetForm = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<FirstRunModelSetup>(true);
            var sourceForm = PrefabUtility.GetCorrespondingObjectFromOriginalSource(assetForm) ?? assetForm;
            Assert.AreEqual("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab", AssetDatabase.GetAssetPath(sourceForm));
            var instance = PrefabUtility.LoadPrefabContents(path);
            TMP_FontAsset temporaryFont = null;
            try
            {
                instance.SetActive(false);
                // Runtime is allowed to rearrange these controls. Unpack only the unsaved test preview,
                // since Unity correctly protects nested prefab transforms during Edit Mode authoring.
                foreach (var nested in instance.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToArray())
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(nested))
                        PrefabUtility.UnpackPrefabInstance(nested, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var actual = instance.GetComponentInChildren<FirstRunModelSetup>(true);
                var source = instance.GetComponentInChildren<TMP_Text>(true).font.sourceFontFile;
                temporaryFont = TMP_FontAsset.CreateFontAsset(source); temporaryFont.isMultiAtlasTexturesEnabled = true;
                foreach (var text in instance.GetComponentsInChildren<TMP_Text>(true)) text.font = temporaryFont;
                foreach (var input in instance.GetComponentsInChildren<TMP_InputField>(true)) input.fontAsset = temporaryFont;
                typeof(FirstRunModelSetup).GetField("client", flags).SetValue(actual, null);
                Action<string> call = name => typeof(FirstRunModelSetup).GetMethod(name, flags).Invoke(actual, null);
                Func<string, object> read = name => typeof(FirstRunModelSetup).GetField(name, flags).GetValue(actual);
                call("EnsureControls");
                typeof(FirstRunModelSetup).GetMethod("ApplyProfiles", flags).Invoke(actual, new object[] { view });
                var context = (TMP_InputField)read("contextLength");
                Assert.AreEqual("32768", context.text);
                CollectionAssert.AreEqual(new[] { "16K", SettingsUiLanguage.T("32K（推荐）", "32K (Recommended)"), "64K" }, ((TMP_Dropdown)read("contextPresets")).options.ConvertAll(o => o.text));
                Assert.AreEqual(1, ((TMP_Dropdown)read("contextPresets")).value);
                Assert.AreSame(((GameObject)read("advancedSettings")).transform, context.transform.parent.parent);
                Assert.IsFalse(((GameObject)read("advancedSettings")).activeSelf);
                string capacityStatus = ((TMP_Text)read("mainCapacityState")).text;
                Assert.IsTrue(capacityStatus.Contains("尚未验证") || capacityStatus.Contains("unverified"));
                var modes = (Toggle[])read("embeddingCategories");
                Assert.AreEqual(4, modes.Length); Assert.IsTrue(modes[0].isOn);
                Assert.IsFalse(((Toggle)read("embeddingEnabled")).transform.parent.gameObject.activeSelf);
                Assert.IsFalse(((Toggle)read("reuseMainApi")).transform.parent.gameObject.activeSelf);
                Assert.That(((TMP_Text)read("embeddingModeInformation")).text, Does.Contain(view.builtin_embedding_defaults.model));
                string saved = JsonUtility.ToJson(view);
                for (int i = 0; i < 4; i++)
                {
                    modes[i].isOn = true;
                    Assert.IsTrue(modes[i].isOn);
                    Assert.AreEqual(1, Array.FindAll(modes, mode => mode.isOn).Length);
                    Assert.AreEqual(i != 3, ((Toggle)read("embeddingEnabled")).isOn);
                    Assert.AreEqual(i != 3, ((GameObject)read("embeddingFields")).activeSelf);
                    Assert.AreEqual(i is 1 or 2, ((TMP_InputField)read("embeddingBaseUrl")).transform.parent.gameObject.activeSelf);
                    Assert.AreEqual(i == 2, ((TMP_InputField)read("embeddingApiKey")).transform.parent.gameObject.activeSelf);
                    Assert.AreEqual(i is 1 or 2, ((Button)read("testEmbeddingButton")).gameObject.activeSelf);
                }
                Assert.AreEqual(saved, JsonUtility.ToJson(view));
                Assert.IsNull(read("indexPoll"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(instance);
                if (temporaryFont != null)
                {
                    foreach (var texture in temporaryFont.atlasTextures) if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    UnityEngine.Object.DestroyImmediate(temporaryFont);
                }
            }
        }

        [Test] public void OldBackend_NewUserStillSeesBuiltinRadioButCannotClaimSupportOrSave()
        {
            var view = (ModelProfilesView)Get("profilesView"); view.revision = 0;
            Invoke("ChooseInitial", true); Invoke("EnsureCompactLayout");
            Assert.IsTrue(((Toggle[])Get("embeddingCategories"))[0].isOn);
            Assert.IsTrue(((Toggle)Get("embeddingEnabled")).isOn);
            Assert.That(((TMP_Text)Get("embeddingConnectionState")).text, Does.Contain("Backend"));
            var save = (IEnumerator)Invoke("SaveBothProfiles");
            Assert.IsFalse(save.MoveNext());
            Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain("Backend"));
            Assert.IsNull(view.active_runtime); Assert.AreEqual(0, view.revision);
        }

        [TestCase(false)] [TestCase(true)] public void RestoreDefaults_ConfirmCancelAndKeyClearAreDraftOnly(bool english)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(english); var view = AddAllEmbeddingProviders();
                var savedMain = new MainModelConfig { model = "saved-main", key_present = true };
                view.main_profiles = new[] { new MainProfileView { profile_id = "main-deepseek", preset_id = "deepseek", config = savedMain } };
                var savedEmbedding = new EmbeddingConfig { enabled = true, provider_type = "ollama", model = "old-vector",base_url="http://127.0.0.1:11434" };
                view.embedding_profiles = new[]{new EmbeddingProfileView{profile_id="kept-embedding",preset_id="ollama",config=savedEmbedding}};
                var active = new ModelActivationView { main_profile_id="main-deepseek",embedding_profile_id="kept-embedding",main_model = savedMain,
                    embedding = new EmbeddingConfig { enabled = true, provider_type = "ollama", model = "old-vector" } };
                view.active_runtime = view.selected_to_activate = active;
                ((TMP_InputField)Get("temperature")).text = "0.77";
                ((TMP_InputField)Get("topP")).text = "0.42";
                ((TMP_InputField)Get("embeddingModel")).text = "unsaved-vector";
                ((TMP_InputField)Get("embeddingApiKey")).text = "synthetic-draft-key";
                string mainDraft = (string)Invoke("DraftSignature", false), embedDraft = (string)Invoke("DraftSignature", true);
                var button = (Button)Get("restoreDefaultsButton");
                Assert.That(button.GetComponentInChildren<TMP_Text>(true).text, Does.Contain(english ? "Restore Defaults" : "恢复默认"));
                button.onClick.Invoke();
                var modal = (GameObject)Get("profileModal"); Assert.IsNotNull(modal);
                modal.GetComponentsInChildren<Button>(true)[1].onClick.Invoke();
                Assert.AreEqual(mainDraft, Invoke("DraftSignature", false)); Assert.AreEqual(embedDraft, Invoke("DraftSignature", true));
                button.onClick.Invoke(); ((GameObject)Get("profileModal")).GetComponentsInChildren<Button>(true)[0].onClick.Invoke();
                Assert.IsTrue((bool)Get("defaultsRestored")); Assert.IsTrue((bool)Invoke("HasUnsavedSettings"));
                Assert.IsTrue(((Toggle[])Get("embeddingCategories"))[1].isOn); Assert.IsTrue(((Toggle)Get("embeddingEnabled")).isOn);
                Assert.AreEqual("old-vector",((TMP_InputField)Get("embeddingModel")).text);
                Assert.AreEqual("", ((TMP_InputField)Get("mainApiKey")).text); Assert.IsTrue((bool)Get("mainClearKey"));
                Assert.IsFalse((bool)Get("mainKeyPresent"));
                Assert.IsFalse((bool)Get("embeddingClearKey")); Assert.AreEqual("", ((TMP_InputField)Get("topP")).text);
                Assert.AreEqual("0", ((TMP_InputField)Get("temperature")).text);
                Assert.AreSame(active, view.active_runtime); Assert.AreEqual("saved-main", savedMain.model); Assert.IsTrue(savedMain.key_present);
                Assert.That(Invoke("BuildMainResetCommand").ToString(), Does.Contain("\"key_action\": \"clear\""));
                Assert.That(Invoke("BuildMainResetCommand").ToString(), Does.Contain("main-deepseek"));
                Assert.AreEqual("",((TMP_InputField)Get("mainModel")).text);Assert.AreEqual(-1,((TMP_Dropdown)Get("mainSupplier")).value);
                Assert.AreEqual(1, view.revision); Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain(english ? "not" : "尚未删除"));
                var routine=(IEnumerator)Invoke("ProbeCurrentProfile",false,"discover");Assert.IsFalse(routine.MoveNext());
                Assert.IsFalse(((Transform)Get("mainConnectionGroup")).GetComponent<CanvasGroup>().interactable);
                Invoke("ApplyProfiles",view);Assert.IsTrue((bool)Get("defaultsRestored"));
                Invoke("CancelPendingMainReset",view);Assert.IsFalse((bool)Get("defaultsRestored"));
                Assert.AreEqual("saved-main",((TMP_InputField)Get("mainModel")).text);Assert.IsTrue((bool)Get("mainKeyPresent"));
                button.onClick.Invoke();((GameObject)Get("profileModal")).GetComponentsInChildren<Button>(true)[0].onClick.Invoke();
                Assert.IsFalse((bool)Invoke("ApplyMainResetResult",null,"MODEL_CONFIG_WRITE_FAILED"));
                Assert.IsTrue((bool)Get("defaultsRestored"));Assert.AreEqual(1,view.revision);Assert.IsTrue(savedMain.key_present);
                var cleared = new ModelProfilesView {schema_version=2,revision=2,configured=false,restart_required=true,
                    main_profiles=new[]{new MainProfileView{profile_id="other-main",preset_id="qwen",config=new MainModelConfig{model="other",key_present=true}}},
                    embedding_profiles=view.embedding_profiles,main_presets=view.main_presets,embedding_presets=view.embedding_presets,
                    default_main_preset_id=view.default_main_preset_id,default_embedding_preset_id=view.default_embedding_preset_id,
                    builtin_embedding_defaults=view.builtin_embedding_defaults,active_runtime=active};
                Assert.IsTrue((bool)Invoke("ApplyMainResetResult",cleared,null));
                Assert.IsFalse((bool)Get("defaultsRestored"));Assert.IsFalse((bool)Invoke("HasUnsavedSettings"));
                Assert.AreEqual(-1,((TMP_Dropdown)Get("mainSupplier")).value);Assert.IsFalse((bool)Get("mainKeyPresent"));
                Assert.IsTrue(form.SavedAIRequiresRestart);Assert.IsFalse(ModelConfigClient.StartupReady(cleared,null));
                Assert.AreSame(active,cleared.active_runtime);Assert.IsTrue(cleared.active_runtime.main_model.key_present);
                Assert.IsTrue(((Transform)Get("mainConnectionGroup")).GetComponent<CanvasGroup>().interactable);
                // A fresh startup keeps Main blank despite unrelated saved profiles; explicit reconfiguration remains available.
                cleared.active_runtime=null;cleared.restart_required=false;Invoke("ApplyProfiles",cleared);Invoke("ChooseInitial",false);
                Assert.IsFalse(form.SavedAIRequiresRestart);Assert.IsFalse(ModelConfigClient.StartupReady(cleared,null));
                Assert.AreEqual(-1,((TMP_Dropdown)Get("mainSupplier")).value);
                Invoke("RequestSupplierSwitch",false,0);Assert.AreEqual(0,((TMP_Dropdown)Get("mainSupplier")).value);
                Assert.That(((Button)Get("saveButton")).GetComponentInChildren<TMP_Text>(true).text, Does.Contain(english ? "Save & Apply" : "保存并应用配置"));
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        [Test] public void RestoreDefaults_RecognizesHiddenProviderDraftAndDoesNotResetOnCancel()
        {
            AddAllEmbeddingProviders(); ((TMP_InputField)Get("embeddingModel")).text = "hidden-unsaved-draft";
            ((Toggle[])Get("embeddingCategories"))[0].isOn = true;
            ((Button)Get("restoreDefaultsButton")).onClick.Invoke(); Assert.IsNotNull(Get("profileModal"));
            ((GameObject)Get("profileModal")).GetComponentsInChildren<Button>(true)[1].onClick.Invoke();
            ((Toggle[])Get("embeddingCategories"))[1].isOn = true;
            Assert.AreEqual("hidden-unsaved-draft", ((TMP_InputField)Get("embeddingModel")).text);
        }

        [Test] public void RestoreDefaults_OnPristineNewFormIsLocalAndMarksPendingDraft()
        {
            var view = AddBuiltin(); ((Button)Get("restoreDefaultsButton")).onClick.Invoke();
            Assert.IsNotNull(Get("profileModal"));Assert.IsFalse((bool)Get("defaultsRestored"));
            ((GameObject)Get("profileModal")).GetComponentsInChildren<Button>(true)[0].onClick.Invoke();
            Assert.IsNull(Get("profileModal")); Assert.IsTrue((bool)Get("defaultsRestored"));
            Assert.AreEqual(0, view.revision); Assert.IsNull(view.selected_to_activate);
            Assert.IsTrue(((Toggle[])Get("embeddingCategories"))[0].isOn);
        }

        [Test] public void ConnectionConfirmation_DoesNotInheritTemporarySaveButtonLock()
        {
            AddAllEmbeddingProviders();
            ((Button)Get("saveButton")).interactable = false;
            Invoke("PromptProbe", true, "connection");
            Assert.IsNull(Get("profileModal"), "An empty model draft must not offer a paid request.");
            ((TMP_InputField)Get("embeddingModel")).text = "synthetic-confirmation-model";
            Invoke("PromptProbe", true, "connection");
            var modal = (GameObject)Get("profileModal");
            Assert.IsNotNull(modal, "A valid draft must still require explicit fee confirmation.");
            foreach (var button in modal.GetComponentsInChildren<Button>(true)) Assert.IsTrue(button.interactable);
        }

        [Test] public void ProviderSelectionChange_IsAnUnsavedDraftEvenWithoutParameterEdits()
        {
            AddBuiltin(); ((Toggle[])Get("embeddingCategories"))[2].isOn = true;
            Assert.IsTrue((bool)Invoke("HasUnsavedSettings"));
            ((Button)Get("restoreDefaultsButton")).onClick.Invoke(); Assert.IsNotNull(Get("profileModal"));
        }

        [Test] public void OldBackend_DisabledEmbeddingDoesNotRequireUnavailableBuiltinConfiguration()
        {
            var view = (ModelProfilesView)Get("profilesView"); view.revision = 0;
            Invoke("ChooseInitial", true); ((Toggle)Get("embeddingEnabled")).SetIsOnWithoutNotify(false);
            string json = Invoke("BuildCombinedProfileCommand").ToString();
            Assert.IsFalse(JsonUtility.FromJson<CombinedCommand>(json).embedding_enabled);
            Assert.That(json, Does.Match("\"embedding\":\\s*null"));
        }

        [TestCase("waiting", true)] [TestCase("running", true)] [TestCase("paused", true)]
        [TestCase("completed", false)] [TestCase("failed", true)] [TestCase("restart_required", false)]
        [TestCase("disabled", false)] [TestCase("unknown", false)]
        public void BasicMemoryIndex_UsesBackendProgressAndAppropriateControls(string state, bool controls)
        {
            AddBuiltin();
            Set("currentIndex", new EmbeddingIndexView { status = state, applied = state != "restart_required",
                control_allowed = true, counts_known = true, model = "actual-qwen", model_key = "space-qwen",
                total = 100, completed = 44, pending = 56, progress = 61 });
            Invoke("RefreshIndexState");
            Assert.IsFalse(((GameObject)Get("memoryIndexSection")).transform.IsChildOf(((GameObject)Get("advancedSettings")).transform));
            Assert.That(((TMP_Text)Get("indexState")).text, Does.Contain("actual-qwen"));
            Assert.That(((TMP_Text)Get("indexState")).text, Does.Not.Contain("Gemma"));
            Assert.AreEqual(61, ((Slider)Get("indexProgress")).value);
            Assert.AreEqual(controls, ((Button)Get("indexAction")).interactable);
            Assert.AreEqual(controls, ((Button)Get("indexAction")).transform.parent.gameObject.activeSelf);
        }

        [Test] public void BasicMemoryIndex_ZeroAndUnknownDoNotInventProgressOrTasks()
        {
            AddBuiltin(); Set("currentIndex", new EmbeddingIndexView {status="waiting",applied=true,counts_known=true,total=0});
            Invoke("RefreshIndexState"); Assert.IsFalse(((Slider)Get("indexProgress")).gameObject.activeSelf);
            Assert.IsFalse(((Button)Get("indexAction")).interactable);
            Set("currentIndex", null); Invoke("RefreshIndexState");
            Assert.IsFalse(((Slider)Get("indexProgress")).gameObject.activeSelf);
            Assert.That(((TMP_Text)Get("indexState")).text, Does.Not.Contain("%"));
        }

        [TestCase("http://127.0.0.1:8000", true)] [TestCase("http://127.0.0.1:18777", false)]
        [TestCase("http://example.test:8000", false)] [TestCase("https://127.0.0.1:8000", false)]
        [TestCase(null, false)]
        public void ManualRestart_OnlyTargetsManagedEndpoint(string endpoint, bool allowed)
            => Assert.AreEqual(allowed, Invoke("CanTargetLocalBackend", endpoint));

        [Test] public void AppliedSnapshot_RequiresActualParametersNotJustMatchingProfileId()
        {
            var target = new ModelActivationView { main_profile_id="m", embedding_profile_id="e",
                main_model=new MainModelConfig {model="synthetic-main", temperature=.5f},
                embedding=new EmbeddingConfig {enabled=true, provider_type="ollama", model="qwen"} };
            var active = new ModelActivationView { main_profile_id="m", embedding_profile_id="e",
                main_model=new MainModelConfig {model="synthetic-main", temperature=.5f},
                embedding=new EmbeddingConfig {enabled=true, provider_type="ollama", model="qwen"} };
            Assert.IsTrue((bool)Invoke("AppliedSnapshotMatches", target, active));
            active.main_model.temperature=.2f; Assert.IsFalse((bool)Invoke("AppliedSnapshotMatches",target,active));
            active.main_model.temperature=.5f; active.embedding.model="gemma";
            Assert.IsFalse((bool)Invoke("AppliedSnapshotMatches",target,active));
        }

        [TestCase(false)] [TestCase(true)]
        public void CompactLayout_DefaultCollapsedPreservesAdvancedDraftAndKeyActions(bool english)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(english);
                ((TMP_InputField)Get("topP")).text = "0.67";
                ((TMP_InputField)Get("embeddingVersion")).text = "draft-digest";
                Set("mainKeyPresent", true);
                Invoke("EnsureCompactLayout");
                var group = (GameObject)Get("advancedSettings");
                Assert.IsFalse(group.activeSelf);
                foreach (string name in new[] { "topP", "mainTimeout" })
                    Assert.IsTrue(((Component)Get(name)).transform.IsChildOf(group.transform), name);
                Assert.IsTrue(((Component)Get("embeddingVersion")).transform.IsChildOf((Transform)Get("vectorRetrievalGroup")));
                Assert.IsTrue(((Button)Get("contractTest")).transform.IsChildOf(((GameObject)Get("contractContent")).transform));
                Assert.IsTrue(((Button)Get("indexAction")).transform.IsChildOf(((GameObject)Get("memoryIndexSection")).transform));
                Assert.IsFalse(((Button)Get("indexAction")).gameObject.activeInHierarchy, "Maintenance is behind the developer entry.");
                Assert.IsFalse(((TMP_Text)Get("indexState")).transform.IsChildOf(group.transform));
                Assert.IsFalse(((Button)Get("mainSave")).transform.parent.gameObject.activeSelf);
                Assert.IsFalse(((Button)Get("embeddingApply")).transform.parent.gameObject.activeSelf);
                string mainDraft = (string)Invoke("DraftSignature", false);
                string embedDraft = (string)Invoke("DraftSignature", true);
                for (int i = 0; i < 4; i++) Invoke("ToggleAdvancedSettings");
                Assert.IsFalse(group.activeSelf);
                Assert.AreEqual(mainDraft, Invoke("DraftSignature", false));
                Assert.AreEqual(embedDraft, Invoke("DraftSignature", true));
                Assert.AreEqual("", ((TMP_InputField)Get("mainApiKey")).text);
                Assert.That(((Button)Get("saveButton")).GetComponentInChildren<TMP_Text>(true).text,
                    Does.Contain(english ? "Save & Apply" : "保存并应用"));
                Invoke("EnsureCompactLayout"); Assert.AreSame(group, Get("advancedSettings"));
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        [Test] public void SupplierList_IncludesOllamaAndSynchronizesSourceAndKeyVisibility()
        {
            var view = (ModelProfilesView)Get("profilesView");
            var local = Preset("ollama", "ollama", "http://127.0.0.1:11434"); local.source = "local";
            view.main_presets = new[] { view.main_presets[0], view.main_presets[1], local };
            Invoke("LoadSupplierOptions", false, 0, "deepseek");
            Invoke("RequestSupplierSwitch", false, 2);
            Assert.AreEqual("main-ollama", Get("mainEditingId"));
            Assert.AreEqual(1, ((TMP_Dropdown)Get("mainProvider")).value);
            Assert.AreEqual("http://127.0.0.1:11434", ((TMP_InputField)Get("mainBaseUrl")).text);
            Assert.IsFalse(((TMP_InputField)Get("mainApiKey")).transform.parent.gameObject.activeSelf);
            Invoke("RequestSupplierSwitch", false, 1);
            Assert.AreEqual(0, ((TMP_Dropdown)Get("mainProvider")).value);
            Assert.IsTrue(((TMP_InputField)Get("mainApiKey")).transform.parent.gameObject.activeSelf);
            Assert.AreEqual("embedding-aliyun", Get("embeddingEditingId"));
        }

        [TestCase("busy")] [TestCase("dropdownsWarming")]
        public void AdvancedToggle_DuringTemporaryLockDoesNotStartCompetingWarmup(string field)
        {
            Invoke("EnsureCompactLayout");
            Set(field, true); Invoke("ToggleAdvancedSettings");
            Assert.IsFalse(((GameObject)Get("advancedSettings")).activeSelf);
            Set(field, false); Invoke("ToggleAdvancedSettings");
            Assert.IsTrue(((GameObject)Get("advancedSettings")).activeSelf);
            Assert.IsTrue(((TMP_Dropdown)Get("schemaMode")).interactable);
        }

        [Serializable] private sealed class CombinedCommand
        {
            public int expected_revision;
            public KeySaveCommand main, embedding;
            public bool embedding_enabled;
        }
        [Test] public void CombinedSave_ValidatesBothDraftsAndOmitsOnlyUnchangedDisabledEmbedding()
        {
            var command = JsonUtility.FromJson<CombinedCommand>(Invoke("BuildCombinedProfileCommand").ToString());
            Assert.IsNotNull(command.main); Assert.IsNull(command.embedding.key_action); Assert.IsFalse(command.embedding_enabled);
            ((TMP_InputField)Get("embeddingModel")).text = "draft-vector";
            command = JsonUtility.FromJson<CombinedCommand>(Invoke("BuildCombinedProfileCommand").ToString());
            Assert.IsNotNull(command.embedding); Assert.AreEqual("retain", command.embedding.key_action);
            ((TMP_InputField)Get("embeddingTimeout")).text = "invalid";
            Assert.Throws<TargetInvocationException>(() => Invoke("BuildCombinedProfileCommand"));
            Assert.AreEqual("draft-vector", ((TMP_InputField)Get("embeddingModel")).text);
        }

        [Test] public void ProbeResult_UsesSingleLineSummaryAndScrollableDetailsInBothLanguages()
        {
            bool language = SettingsUiLanguage.IsEnglish;
            try
            {
                var result = new ModelProbeView { success = false, error_code = "MODEL_CONTRACT_RESPONSE_INVALID", results = new[] {
                    new ModelContractResult { @case = "step0", status = "PASS" },
                    new ModelContractResult { @case = "step1", status = "FAIL", error_code = "MODEL_CONTRACT_RESPONSE_INVALID" },
                    new ModelContractResult { @case = "continuation", status = "NOT_RUN" } } };
                Invoke("ApplyProbeResult", result, null);
                foreach (bool english in new[] { false, true })
                {
                    SettingsUiLanguage.SetEnglish(english); Invoke("RefreshLanguage");
                    var summary = (TMP_Text)Get("statusText"); var details = (TMP_Text)Get("probeDetails");
                    Assert.That(summary.text, Does.Not.Contain("\n"));
                    // P3-D intentionally wraps the readable Footer at 20-24pt.
                    Assert.AreEqual(TextWrappingModes.Normal, summary.textWrappingMode);
                    Assert.GreaterOrEqual(summary.fontSizeMin, 20);
                    Assert.That(summary.text, Does.Contain(english ? "Test failed" : "测试失败"));
                    Assert.That(details.text, Does.Contain("step0: PASS"));
                    Assert.That(details.text, Does.Contain("step1: FAIL (MODEL_CONTRACT_RESPONSE_INVALID)"));
                    Assert.That(details.text, Does.Contain("continuation: NOT_RUN"));
                    Assert.That(details.text, Does.Contain(english ? "world execution" : "世界执行"));
                    Assert.AreEqual(main, details.transform.parent);
                    Assert.AreEqual(-1, details.GetComponent<LayoutElement>().preferredHeight);
                    Assert.IsTrue(details.gameObject.activeSelf);
                }
                Invoke("ApplyProbeResult", null, "TIMEOUT");
                Assert.That(((TMP_Text)Get("probeDetails")).text, Does.Contain("TIMEOUT"));
            }
            finally { SettingsUiLanguage.SetEnglish(language); }
        }

        [TestCase(false)] [TestCase(true)]
        public void Language_UpdatesReparentedTitleVersionPlaceholderAndIndexAction(bool english)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            var page = new GameObject("ModelPage", typeof(RectTransform));
            root.transform.SetParent(page.transform);
            var title = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI)); title.transform.SetParent(page.transform);
            var placeholder = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
            var version = (TMP_InputField)Get("embeddingVersion"); placeholder.transform.SetParent(version.transform);
            version.placeholder = placeholder.GetComponent<TMP_Text>();
            try
            {
                SettingsUiLanguage.SetEnglish(english); Invoke("RefreshLanguage");
                Assert.AreEqual(english ? "Model / API Configuration" : "模型 / API 配置", title.GetComponent<TMP_Text>().text);
                Assert.AreEqual(english ? "Version / digest (optional)" : "版本 / digest（可选）", placeholder.GetComponent<TMP_Text>().text);
                foreach (string state in new[] { "waiting", "paused", "failed", "running", "completed" })
                {
                    Set("currentIndex", new EmbeddingIndexView { status = state, applied = true }); Invoke("RefreshIndexState");
                    var action = (Button)Get("indexAction");
                    string expected = state == "waiting" ? (english ? "Start indexing" : "开始构建") :
                        state == "running" ? (english ? "Pause indexing" : "暂停构建") : (english ? "Resume indexing" : "继续构建");
                    Assert.AreEqual(expected, action.transform.parent.Find("Label").GetComponent<TMP_Text>().text);
                    Assert.AreEqual(expected, action.GetComponentInChildren<TMP_Text>(true).text);
                }
            }
            finally { root.transform.SetParent(null); UnityEngine.Object.DestroyImmediate(page); SettingsUiLanguage.SetEnglish(original); }
        }

        private static IEnumerator OneFrameOperation() { yield return null; }

        private IEnumerator SyntheticDiscovery(bool embeddingKey)
        {
            form.ApplyDiscoveryResult(embeddingKey, new DiscoverModelsResponse {
                success = true, models = new[] { "synthetic-model-a", "synthetic-model-b" } }, null);
            yield break;
        }

        private IEnumerator SyntheticDiscoveryFailure(bool embeddingKey)
        {
            form.ApplyDiscoveryResult(embeddingKey, null, "MODEL_DISCOVERY_FAILED");
            yield break;
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void DiscoveryFailureThroughBusyRun_PreservesManualModelAndPriorAvailability(bool embeddingKey, bool priorModels)
        {
            var candidates = (TMP_Dropdown)Get(embeddingKey ? "embeddingCandidates" : "mainCandidates");
            var input = (TMP_InputField)Get(embeddingKey ? "embeddingModel" : "mainModel");
            if (priorModels) FirstRunModelSetup.ApplyDiscoveredModels(candidates, new[] { "previous-model" });
            input.text = "synthetic-manual";
            var run = (IEnumerator)Invoke("Run", SyntheticDiscoveryFailure(embeddingKey));
            Assert.IsTrue(run.MoveNext()); Assert.IsFalse(((IEnumerator)run.Current).MoveNext());
            Assert.IsFalse(run.MoveNext());
            Assert.AreEqual(priorModels, candidates.interactable);
            Assert.AreEqual(priorModels ? 1 : 0, candidates.options.Count);
            Assert.AreEqual("synthetic-manual", input.text);
        }

        [TestCase(false)] [TestCase(true)]
        public void DiscoveryThroughBusyRun_UnlocksNewCandidatesAndAllowsSelection(bool embeddingKey)
        {
            var candidates = (TMP_Dropdown)Get(embeddingKey ? "embeddingCandidates" : "mainCandidates");
            var input = (TMP_InputField)Get(embeddingKey ? "embeddingModel" : "mainModel");
            input.text = "synthetic-manual";
            var run = (IEnumerator)Invoke("Run", SyntheticDiscovery(embeddingKey));
            Assert.IsTrue(run.MoveNext());
            Assert.IsFalse(candidates.interactable);
            Assert.IsFalse(((IEnumerator)run.Current).MoveNext());
            Assert.IsFalse(candidates.interactable, "Discovery must not release the in-flight UI lock.");
            Assert.IsFalse(run.MoveNext());
            Assert.AreEqual(2, candidates.options.Count);
            Assert.IsTrue(candidates.interactable, "New candidates must override the pre-discovery disabled snapshot.");
            Assert.AreEqual("synthetic-manual", input.text);
            Invoke(embeddingKey ? "OnEmbeddingCandidateChosen" : "OnMainCandidateChosen", 0);
            Assert.AreEqual("synthetic-model-a", input.text);
            Assert.AreEqual(-1, candidates.value);
        }

        [TestCase(false)] [TestCase(true)]
        public void ProfileReloadThroughBusyLock_DiscardsOldCandidates(bool embeddingKey)
        {
            var candidates = (TMP_Dropdown)Get(embeddingKey ? "embeddingCandidates" : "mainCandidates");
            FirstRunModelSetup.ApplyDiscoveredModels(candidates, new[] { "previous-profile-model" });
            Invoke("SetProfileBusy", true);
            Invoke("LoadEditingProfile", embeddingKey);
            Invoke("SetProfileBusy", false);
            Assert.AreEqual(0, candidates.options.Count);
            Assert.IsFalse(candidates.interactable);
            Assert.AreEqual(-1, candidates.value);
        }

        [TestCase(false)] [TestCase(true)]
        public void ProfileReloadDuringWarmup_DoesNotRestoreStaleCandidateAvailability(bool embeddingKey)
        {
            var candidates = (TMP_Dropdown)Get(embeddingKey ? "embeddingCandidates" : "mainCandidates");
            FirstRunModelSetup.ApplyDiscoveredModels(candidates, new[] { "previous-profile-model" });
            var warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart");
            Assert.IsTrue(warmup.MoveNext());
            Invoke("LoadEditingProfile", embeddingKey);
            Assert.IsFalse(warmup.MoveNext());
            Assert.AreEqual(0, candidates.options.Count);
            Assert.IsFalse(candidates.interactable);
        }

        [Serializable] private sealed class KeySaveCommand
        {
            public string key_action;
            public KeySaveConfig config;
        }
        [Serializable] private sealed class KeySaveConfig { public string api_key; }

        [TestCase(false)] [TestCase(true)]
        public void SavedKeyPlaceholder_IsFixedMaskWithoutSubmittingStars(bool english)
        {
            bool original = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(english);
                foreach (bool embeddingKey in new[] { false, true })
                {
                    var input = (TMP_InputField)Get(embeddingKey ? "embeddingApiKey" : "mainApiKey");
                    var placeholder = new GameObject("SyntheticKeyPlaceholder", typeof(RectTransform), typeof(TextMeshProUGUI));
                    placeholder.transform.SetParent(input.transform); input.placeholder = placeholder.GetComponent<TMP_Text>();
                    Set(embeddingKey ? "embeddingKeyPresent" : "mainKeyPresent", true);
                    Invoke("RefreshProfileLanguage");
                    Assert.AreEqual("********", ((TMP_Text)input.placeholder).text);
                    Assert.AreEqual("", input.text);
                    var command = JsonUtility.FromJson<KeySaveCommand>(Invoke("BuildProfileCommand", embeddingKey).ToString());
                    Assert.AreEqual("retain", command.key_action);
                    Assert.IsNull(command.config.api_key);
                    input.text = "synthetic-replacement";
                    command = JsonUtility.FromJson<KeySaveCommand>(Invoke("BuildProfileCommand", embeddingKey).ToString());
                    Assert.AreEqual("replace", command.key_action);
                    Assert.AreEqual("synthetic-replacement", command.config.api_key);
                    input.text = "";
                    Set(embeddingKey ? "embeddingKeyPresent" : "mainKeyPresent", false);
                    Invoke("RefreshProfileLanguage");
                    Assert.AreEqual(english ? "Enter API key" : "输入 API 密钥", ((TMP_Text)input.placeholder).text);
                    Set(embeddingKey ? "embeddingKeyPresent" : "mainKeyPresent", true);
                    Set(embeddingKey ? "embeddingClearKey" : "mainClearKey", true);
                    command = JsonUtility.FromJson<KeySaveCommand>(Invoke("BuildProfileCommand", embeddingKey).ToString());
                    Assert.AreEqual("clear", command.key_action);
                    Assert.IsNull(command.config.api_key);
                }
            }
            finally { SettingsUiLanguage.SetEnglish(original); }
        }

        [Test] public void V2Unlock_ReappliesSourceRulesWithoutEnablingEmptyCandidates()
        {
            var source = (TMP_Dropdown)Get("embeddingProvider");
            var candidates = (TMP_Dropdown)Get("embeddingCandidates");
            source.interactable = false; candidates.interactable = false;
            var warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart");
            Assert.IsTrue(warmup.MoveNext());
            Invoke("RefreshEmbeddingFields"); Assert.IsFalse(warmup.MoveNext());
            Assert.IsTrue(source.interactable); Assert.IsFalse(candidates.interactable);
            source.interactable = false; Invoke("SetProfileBusy", true); Invoke("SetProfileBusy", false);
            Assert.IsTrue(source.interactable); Assert.IsFalse(candidates.interactable);
        }

        [Test] public void SameFrameWarmupAndBusy_RestoresBaselineInsteadOfTemporaryLock()
        {
            var supplier = (TMP_Dropdown)Get("mainSupplier"); var unavailable = (TMP_Dropdown)Get("mainCandidates");
            unavailable.interactable = false;
            var warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart"); Assert.IsTrue(warmup.MoveNext());
            var operation = (IEnumerator)Invoke("Run", OneFrameOperation()); Assert.IsTrue(operation.MoveNext());
            Assert.IsFalse(supplier.interactable);
            Assert.IsFalse(warmup.MoveNext());
            Assert.IsTrue(supplier.interactable);
            Assert.IsTrue(operation.MoveNext()); Assert.IsFalse(supplier.interactable);
            Assert.IsFalse(operation.MoveNext());
            Assert.IsTrue(supplier.interactable); Assert.IsFalse(unavailable.interactable);
            Assert.IsFalse((bool)Get("busy"));
        }

        [Test] public void DisableDuringWarmup_RestoresControlsBeforeNextEnable()
        {
            var warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart"); warmup.MoveNext();
            Invoke("OnDisable");
            Assert.IsFalse((bool)Get("dropdownsWarming"));
            Assert.IsTrue(((TMP_Dropdown)Get("mainSupplier")).interactable);
            warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart"); warmup.MoveNext(); warmup.MoveNext();
            Assert.IsTrue(((TMP_Dropdown)Get("mainSupplier")).interactable);
        }

        [Test] public void ProfileCloneDuringWarmup_InheritsUnlockedBaselineAndWaitsForStart()
        {
            var warmup = (IEnumerator)Invoke("EnableDropdownsAfterStart"); warmup.MoveNext();
            var clone = (TMP_Dropdown)Invoke("AddProfileRow", Get("mainProvider"), "LateDropdown", "测试", "Test", main, -1);
            Assert.IsFalse(clone.interactable);
            warmup.MoveNext(); Assert.IsTrue(clone.interactable);
        }
        [Test] public void StartupResult_InitializesV2WithoutManualModelTabClick()
        {
            var profiles = (ModelProfilesView)Get("profilesView");
            Set("profilesView", null); Set("mainChoices", null); Set("embeddingChoices", null);
            var config = new ModelConfigView { configured = false };
            FieldInfo field = typeof(ModelConfigView).GetField("profiles");
            Assert.IsNotNull(field, "Startup projection must carry the already-read v2 profiles.");
            field.SetValue(config, profiles);
            form.ApplyStartupResult(config, null);
            Assert.AreSame(profiles, Get("profilesView"));
            Assert.IsNull(Get("mainEditingId"), "A truly unconfigured startup must leave Main unselected.");
            Assert.IsNotNull(Get("contractTest")); Assert.IsNotNull(Get("indexState"));
        }
        [Test] public void MissingProfiles_BlocksLegacySaveAndTestsWithReloadGuidance()
        {
            Set("profilesView", null);
            Assert.IsFalse((bool)Invoke("RequireDeveloperProfiles"));
            Assert.That(((TMP_Text)Get("statusText")).text, Does.Contain("v2"));
        }
        [Test] public void UnsavedSupplierSwitch_PromptsAndCancelPreservesDraft()
        {
            ((TMP_InputField)Get("mainModel")).text = "unsaved-full-model/id";
            Invoke("RequestSupplierSwitch", false, 1);
            Assert.AreEqual(0, ((TMP_Dropdown)Get("mainSupplier")).value);
            var modal = (GameObject)Get("profileModal"); Assert.IsNotNull(modal);
            Button[] buttons = modal.GetComponentsInChildren<Button>(true);
            Assert.AreEqual(3, buttons.Length); buttons[2].onClick.Invoke();
            Assert.AreEqual("unsaved-full-model/id", ((TMP_InputField)Get("mainModel")).text);
            Assert.AreEqual("main-deepseek", Get("mainEditingId"));
        }
        [TestCase("waiting", null, true)]
        [TestCase("paused", null, true)]
        [TestCase("paused", "EMBEDDING_INDEX_FAILED", true)]
        [TestCase("failed", "EMBEDDING_PREVIOUS_REQUEST_FAILED", true)]
        [TestCase("paused", "EMBEDDING_CONTROL_STATE_INVALID", false)]
        [TestCase("restart_required", null, false)]
        public void IndexState_ExplainsRecoveryWithoutMemoryLossOrRawCodes(string state, string error, bool allowed)
        {
            bool language = SettingsUiLanguage.IsEnglish;
            try
            {
                Set("currentIndex", new EmbeddingIndexView { status = state, error_code = error,
                    model = "synthetic", applied = state != "restart_required", control_allowed = allowed, counts_known = false });
                foreach (bool english in new[] { false, true })
                {
                    SettingsUiLanguage.SetEnglish(english); Invoke("RefreshIndexState");
                    string text = ((TMP_Text)Get("indexState")).text;
                    Assert.AreEqual(allowed, ((Button)Get("indexAction")).interactable);
                    Assert.That(text, Does.Contain(english ? "unknown" : "未知"));
                    if (error != null) Assert.That(text, Does.Not.Contain(error));
                    if (state is "paused" or "waiting" or "failed")
                        Assert.That(text, Does.Contain(english ? "History is preserved" : "历史记忆仍保留"));
                    if (error == "EMBEDDING_CONTROL_STATE_INVALID") Assert.That(text, Does.Contain(english ? "Back up" : "备份"));
                    Invoke("RefreshIndexState"); Assert.AreEqual(text, ((TMP_Text)Get("indexState")).text);
                }
                Set("currentIndex", new EmbeddingIndexView { status = "running", applied = true, control_allowed = true, model = "synthetic", total = 3, pending = 3 });
                Invoke("RefreshIndexState");
                Assert.That(((TMP_Text)Get("indexState")).text, Does.Not.Contain("EMBEDDING_"));
                Assert.IsTrue(((Button)Get("indexAction")).interactable);
            }
            finally { SettingsUiLanguage.SetEnglish(language); }
        }
        [Test] public void SupplierSwitchWithoutEdits_DoesNotAlterEmbeddingDraft()
        {
            string endpoint = ((TMP_InputField)Get("embeddingBaseUrl")).text;
            Invoke("RequestSupplierSwitch", false, 1);
            Assert.AreEqual("main-qwen", Get("mainEditingId"));
            Assert.AreEqual(endpoint, ((TMP_InputField)Get("embeddingBaseUrl")).text);
            Assert.AreEqual("embedding-aliyun", Get("embeddingEditingId"));
        }
        [Test] public void SavedSupplierSelection_LoadsParametersAndRetainsMaskedKey()
        {
            var view = (ModelProfilesView)Get("profilesView");
            view.main_profiles = new[] { new MainProfileView { profile_id = "main-qwen", preset_id = "qwen",
                config = new MainModelConfig { provider_type = "openai_compatible", base_url = "https://saved.invalid/v1",
                    model = "full-original/model-id", temperature = .7f, max_output_tokens = 1234, key_present = true } } };
            Invoke("RequestSupplierSwitch", false, 1);
            Assert.AreEqual("https://saved.invalid/v1", ((TMP_InputField)Get("mainBaseUrl")).text);
            Assert.AreEqual("full-original/model-id", ((TMP_InputField)Get("mainModel")).text);
            Assert.AreEqual("1234", ((TMP_InputField)Get("maxOutputTokens")).text);
            Assert.AreEqual("", ((TMP_InputField)Get("mainApiKey")).text);
            Assert.AreEqual(true, Get("mainKeyPresent"));
        }
        [Test] public void StartupReadiness_RequiresAppliedSnapshotAndHealthyProvider_NotBootstrapHealth()
        {
            var profiles = new ModelProfilesView { configured = true, active_runtime = new ModelActivationView() };
            var status = new RuntimeStatusView { model_config = new ModelConfigStatus { configured = true },
                active_provider = new ActiveProviderStatus { available = true } };
            Assert.IsTrue(ModelConfigClient.StartupReady(profiles, status));
            profiles.restart_required = true; Assert.IsFalse(ModelConfigClient.StartupReady(profiles, status));
            profiles.restart_required = false; profiles.active_runtime = null; Assert.IsFalse(ModelConfigClient.StartupReady(profiles, status));
            profiles.active_runtime = new ModelActivationView(); status.active_provider.available = false; Assert.IsFalse(ModelConfigClient.StartupReady(profiles, status));
            status.active_provider.available = true; profiles.configured = false; Assert.IsFalse(ModelConfigClient.StartupReady(profiles, status));
        }
        [TestCase(false)] [TestCase(true)]
        public void BakedSettingsPrefab_CreatesProfileRowsWithoutSceneOrNetworkChanges(bool legacyEmbeddingDisabled)
        {
            GameObject prefab = PrefabUtility.LoadPrefabContents("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab");
            try
            {
                var actual = prefab.GetComponentInChildren<FirstRunModelSetup>(true);
                Assert.IsNotNull(actual);
                // The runtime reparenting is legal in Play Mode. Unpack only the isolated preview instance.
                foreach (Transform child in prefab.GetComponentsInChildren<Transform>(true))
                {
                    GameObject instance = PrefabUtility.GetOutermostPrefabInstanceRoot(child.gameObject);
                    if (instance != null) PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
                object synthetic = Get("profilesView");
                typeof(FirstRunModelSetup).GetMethod("EnsureControls", flags).Invoke(actual, null);
                if (legacyEmbeddingDisabled)
                    ((TMP_Dropdown)typeof(FirstRunModelSetup).GetField("embeddingProvider", flags).GetValue(actual)).interactable = false;
                actual.ApplyStartupResult(new ModelConfigView { profiles = (ModelProfilesView)synthetic, configured = false }, null);
                var mainSupplier = (TMP_Dropdown)typeof(FirstRunModelSetup).GetField("mainSupplier", flags).GetValue(actual);
                var embedSupplier = (TMP_Dropdown)typeof(FirstRunModelSetup).GetField("embeddingSupplier", flags).GetValue(actual);
                Assert.IsNotNull(mainSupplier.template); Assert.IsNotNull(embedSupplier.template);
                Assert.AreNotEqual(mainSupplier.transform.parent.parent, embedSupplier.transform.parent.parent);
                Assert.AreEqual(2, mainSupplier.options.Count); Assert.AreEqual(1, embedSupplier.options.Count);
                Assert.IsTrue(embedSupplier.interactable);
                Assert.IsTrue(((TMP_Dropdown)typeof(FirstRunModelSetup).GetField("embeddingProvider", flags).GetValue(actual)).interactable);
                Assert.IsNotNull(typeof(FirstRunModelSetup).GetField("indexState", flags).GetValue(actual));
                Assert.IsNotNull(typeof(FirstRunModelSetup).GetField("contractTest", flags).GetValue(actual));
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
        }
    }
}
