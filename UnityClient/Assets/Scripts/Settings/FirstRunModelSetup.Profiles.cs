using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        private ModelProfilesView profilesView;
        private TMP_Dropdown mainSupplier, embeddingSupplier, schemaMode, tokenMode, thinkingMode, reasoningMode, healthMode;
        private TMP_InputField topP, thinkingBudget, embeddingDimensions, embeddingVersion;
        private Toggle sendTemperature;
        private TMP_Text mainProfileState, embeddingProfileState, indexState, probeDetails;
        private TMP_Text mainConnectionState, embeddingConnectionState;
        private ModelProbeView lastProbeResult;
        private string lastProbeError;
        private Button mainSave, mainApply, embeddingSave, embeddingApply, contractTest, indexAction;
        private readonly List<(Component control, string chinese, string english)> profileLabels = new();
        private ModelPresetView[] mainChoices, embeddingChoices;
        private string mainEditingId, embeddingEditingId, mainBaseline, embeddingBaseline;
        private bool mainClearKey, embeddingClearKey, profileSwitchPending;
        private GameObject profileModal;
        private Action profileModalCancel;
        private Coroutine indexPoll;
        private EmbeddingIndexView currentIndex;
        private string indexReadError;
        private string indexOperationError;
        private bool mainKeyPresent, embeddingKeyPresent;
        private GameObject advancedSettings;
        // Shared presentation root for advanced AI configuration; scene debug controls live in developer tools.
        public Transform AdvancedSettingsRoot => advancedSettings == null ? null : advancedSettings.transform;
        private Button advancedToggle;
        private readonly List<(Selectable control, bool interactable)> profileBusyControls = new();

        private static string ProviderCaption(ModelPresetView preset) => preset.protocol == "builtin"
            ? SettingsUiLanguage.T("内置本地模型（推荐）", "Built-in Local Embedding (Recommended)") : preset.name;

        private EmbeddingConfig BuiltinEditingConfig() => defaultsRestored ? profilesView.builtin_embedding_defaults : profilesView.embedding_profiles?
            .FirstOrDefault(p => p.profile_id == embeddingEditingId)?.config ?? profilesView.builtin_embedding_defaults;

        private bool EmbeddingSelectionNeedsRestart()
        {
            var selected = profilesView.selected_to_activate;
            var active = profilesView.active_runtime;
            return selected?.embedding != null && (active?.embedding == null ||
                selected.embedding.enabled != active.embedding.enabled || (selected.embedding.enabled &&
                (selected.embedding_profile_id != active.embedding_profile_id ||
                 !JToken.DeepEquals(ModelProfileJson.Embedding(selected.embedding), ModelProfileJson.Embedding(active.embedding)))));
        }

        private void RefreshBuiltinFields()
        {
            if (embeddingChoices == null) return;
            bool builtin = CurrentPreset(true).protocol == "builtin";
            bool editable = embeddingEnabled.isOn && !builtin;
            embeddingEnabled.transform.parent.gameObject.SetActive(false);
            embeddingBaseUrl.transform.parent.gameObject.SetActive(editable);
            embeddingApiKey.transform.parent.gameObject.SetActive(editable && embeddingProvider.value == 0);
            embeddingModel.interactable = editable && !busy;
            embeddingModel.readOnly = builtin;
            embeddingModel.transform.parent.gameObject.SetActive(editable);
            foreach (var field in new[] { embeddingTimeout, embeddingDimensions, embeddingVersion })
                if (field != null) field.transform.parent.gameObject.SetActive(editable);
            if (embeddingDimensions != null && CurrentPreset(true).protocol == "ollama")
                embeddingDimensions.transform.parent.gameObject.SetActive(false);
            embeddingCandidates.gameObject.SetActive(editable);
            discoverEmbeddingButton.gameObject.SetActive(editable && CurrentPreset(true).discovery != "manual");
            testEmbeddingButton.gameObject.SetActive(editable);
            SetButton(testEmbeddingButton, "测试连接", "Test connection");
            // The baked prefab combines these buttons into one row; hide that whole
            // action row only when both belong to it, preserving synthetic/legacy forms.
            if (testEmbeddingButton.transform.parent == discoverEmbeddingButton.transform.parent)
                testEmbeddingButton.transform.parent.gameObject.SetActive(editable);
            foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
                if (child.name == "ClearEmbeddingKeyRow") child.gameObject.SetActive(editable && embeddingProvider.value == 0);
            RefreshEmbeddingCategoryLabels();
        }

        private static string IncompleteIndexText() => SettingsUiLanguage.T(
            "历史记忆仍保留，索引构建期间部分语义召回可能受限。",
            "History is preserved; semantic recall may be limited during indexing.");

        private static string BuiltinErrorText(string code) => code switch
        {
            "BUILTIN_EMBEDDING_FILE_MISSING" => SettingsUiLanguage.T("模型资源缺失；资源不可用，不会自动下载。", "Model resources missing; unavailable. No automatic download."),
            "BUILTIN_EMBEDDING_SHA_MISMATCH" => SettingsUiLanguage.T("模型文件损坏或 SHA 不匹配。", "Model file damaged or SHA mismatch."),
            _ => SettingsUiLanguage.T("内置模型加载 / 推理失败：", "Built-in model load / inference failed: ") + code
        };

        private void RefreshBuiltinState()
        {
            if (embeddingConnectionState == null || profilesView == null || embeddingChoices == null) return;
            var active = profilesView.active_runtime?.embedding;
            bool builtin = active?.enabled == true && active.provider_type == "builtin";
            string text = SettingsUiLanguage.T("当前运行：", "Runtime: ") +
                (active?.enabled == true ? active.model : SettingsUiLanguage.T("未启用", "disabled"));
            if (builtin) text += " · " + EmbeddingLoadCaption();
            if (CurrentPreset(true).protocol == "builtin" && BuiltinEditingConfig() == null)
                text += SettingsUiLanguage.T("\nBackend 尚未提供内置配置，请更新后端。", "\nBuilt-in configuration unavailable; update the Backend.");
            else if (CurrentPreset(true).protocol == "builtin" && (BuiltinEditingConfig()?.resource_status ?? profilesView.builtin_resource_status) == "missing")
                text += "\n" + BuiltinErrorText("BUILTIN_EMBEDDING_FILE_MISSING");
            if (profilesView.restart_required || (currentIndex?.applied == false && profilesView.selected_to_activate != null))
                text += SettingsUiLanguage.T(" · 待重启应用", " · restart required");
            embeddingConnectionState.text = text;
        }

        private string EmbeddingLoadCaption() => currentIndex?.provider_status switch
        {
            "ready" => SettingsUiLanguage.T("已加载", "Loaded"),
            "unloaded" => SettingsUiLanguage.T("尚未加载", "Not loaded"),
            "failed" => SettingsUiLanguage.T("加载或推理失败", "Load or inference failed"),
            "closed" => SettingsUiLanguage.T("已关闭", "Closed"),
            _ => SettingsUiLanguage.T("状态未确认", "Status unconfirmed")
        };

        private static string IndexStateCaption(string state) => state switch
        {
            "waiting" => SettingsUiLanguage.T("等待开始", "Waiting"),
            "running" => SettingsUiLanguage.T("重建中", "Indexing"),
            "paused" => SettingsUiLanguage.T("已暂停", "Paused"),
            "failed" => SettingsUiLanguage.T("请求失败", "Failed"),
            "completed" => SettingsUiLanguage.T("已完成", "Completed"),
            "restart_required" => SettingsUiLanguage.T("待重启", "Restart required"),
            "disabled" => SettingsUiLanguage.T("未启用", "Disabled"),
            "legacy_auto" => SettingsUiLanguage.T("旧配置自动构建", "Legacy automatic indexing"),
            _ => SettingsUiLanguage.T("未知", "Unknown")
        };

        private void SetProfileBusy(bool value)
        {
            if (value)
            {
                profileBusyControls.Clear();
                if (profilesView == null || panel == null) return;
                foreach (var control in panel.GetComponentsInChildren<Selectable>(true))
                {
                    if (control is Scrollbar) continue;
                    profileBusyControls.Add((control, control.interactable)); control.interactable = false;
                }
                foreach (var button in new[] { saveButton, restoreDefaultsButton })
                    if (button != null && !button.transform.IsChildOf(panel.transform))
                    { profileBusyControls.Add((button, button.interactable)); button.interactable = false; }
            }
            else
            {
                foreach (var entry in profileBusyControls) if (entry.control != null) entry.control.interactable = entry.interactable;
                profileBusyControls.Clear();
                if (profilesView != null) RefreshEmbeddingFields();
                RefreshCandidateControls();
                RefreshIndexState();
            }
        }

        private static readonly ModelPresetView UnselectedMain = new() { preset_id = "", name = "", source = "cloud", protocol = "",
            base_url = "", schema_profile = "raw", token_parameter = "max_tokens", thinking_parameter = "none", send_temperature = true, health_strategy = "models" };
        private ModelPresetView CurrentPreset(bool embedding) => embedding ? EditingEmbeddingPreset() :
            mainChoices != null && mainSupplier.value >= 0 && mainSupplier.value < mainChoices.Length ? mainChoices[mainSupplier.value] : UnselectedMain;

        private IEnumerator LoadProfiles(bool initial)
        {
            ModelProfilesView result = null;
            string error = null;
            yield return client.GetProfiles((value, code) => { result = value; error = code; });
            if (error != null || result == null || result.schema_version != 2)
            {
                var message = P0FeedbackText.BackendError(error ?? "UNSUPPORTED_MODEL_CONFIG");
                SetStatus(message.Chinese, message.English);
                yield break;
            }
            if (defaultsRestored) CancelPendingMainReset(result);
            else ApplyProfiles(result);
            if (initial && result.configured && result.active_runtime != null)
            {
                RuntimeStatusView status = null;
                yield return client.GetRuntimeStatus((value, code) => { if (code == null) status = value; });
                if (ModelConfigClient.StartupReady(result, status)) Complete();
            }
            SetStatus(result.migration_required ? "旧配置将于首次保存时迁移；现有运行连接和索引行为保持不变。" : "供应商配置已加载。",
                result.migration_required ? "The first save migrates legacy profiles; runtime connections and indexing remain unchanged." : "Provider profiles loaded.");
        }

        private void ApplyProfiles(ModelProfilesView result)
        {
            if (result == null || result.schema_version != 2) return;
            profilesView = result;
            revision = result.revision;
            EnsureProfileControls();
            if (mainChoices == null || embeddingChoices == null)
            {
                ChooseInitial(false);
                ChooseInitial(true);
            }
            RefreshProfileStates();
            RefreshEmbeddingFields();
            RefreshLanguage();
            EnsureCompactLayout();
            if (client != null && Application.isPlaying) StartProfilePolling();
        }

        private void CancelPendingMainReset(ModelProfilesView result)
        {
            defaultsRestored = false; resetMainProfileId = null;
            mainChoices = null; embeddingChoices = null;
            ApplyProfiles(result);
        }

        private Component AddProfileRow(Component template, string name, string chinese, string english, Transform parent, int position = -1)
        {
            var row = new GameObject(name + "Row", typeof(RectTransform), typeof(LayoutElement), typeof(Image));
            row.transform.SetParent(parent, false);
            var element = row.GetComponent<LayoutElement>();
            element.preferredHeight = 66; element.minHeight = 66;
            SettingsPanelTheme.Surface(row.GetComponent<Image>(), SettingsPanelTheme.Row);
            row.GetComponent<Image>().raycastTarget = false;
            TMP_Text sourceLabel = mainProvider.transform.parent.Find("Label")?.GetComponent<TMP_Text>() ?? statusText;
            var label = Instantiate(sourceLabel, row.transform);
            label.name = "Label";
            var localized = label.GetComponent<SettingsLocalizedLabel>();
            if (localized != null) Destroy(localized);
            label.fontSize = 18; label.enableAutoSizing = true; label.fontSizeMin = 12; label.fontSizeMax = 18;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            RectTransform lr = label.rectTransform;
            lr.anchorMin = Vector2.zero; lr.anchorMax = new Vector2(.38f, 1);
            lr.offsetMin = new Vector2(20, 6); lr.offsetMax = new Vector2(-10, -6);
            Component control = Instantiate(template, row.transform);
            control.name = name; control.gameObject.SetActive(true);
            var rect = control.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(.4f, 0); rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(0, 12); rect.offsetMax = new Vector2(-20, -12);
            if (control is Toggle)
            { rect.anchorMin = new Vector2(1, 0); rect.offsetMin = new Vector2(-110, 12); }
            if (control.TryGetComponent<LayoutElement>(out var ownElement)) ownElement.ignoreLayout = true;
            if (control is TMP_Dropdown dropdown) dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            if (control is TMP_Dropdown clone && warmingDropdowns != null)
                for (int i = 0; i < warmingDropdowns.Length; i++)
                    if (warmingDropdowns[i] == template)
                    {
                        bool enabled = warmingInteractable[i];
                        int count = warmingDropdowns.Length;
                        Array.Resize(ref warmingDropdowns, count + 1); Array.Resize(ref warmingInteractable, count + 1);
                        warmingDropdowns[count] = clone; warmingInteractable[count] = enabled;
                        clone.interactable = false;
                        break;
                    }
            if (control is TMP_InputField input) input.onValueChanged = new TMP_InputField.OnChangeEvent();
            if (control is Button button) button.onClick = new Button.ButtonClickedEvent();
            if (control is Toggle toggle) toggle.onValueChanged = new Toggle.ToggleEvent();
            profileLabels.Add((control, chinese, english));
            if (position >= 0) row.transform.SetSiblingIndex(position);
            else PlaceMainRow(parent, row.transform);
            return control;
        }

        private void PlaceMainRow(Transform parent, Transform row)
        {
            if (parent != mainProvider.transform.parent.parent) return;
            Transform nextSection = parent.Find("Embedding");
            if (nextSection != null) row.SetSiblingIndex(nextSection.GetSiblingIndex());
        }

        private void EnsureProfileControls()
        {
            if (mainSupplier != null) return;
            Transform mainParent = mainProvider.transform.parent.parent;
            Transform embedParent = embeddingProvider.transform.parent.parent;
            mainSupplier = (TMP_Dropdown)AddProfileRow(mainProvider, "MainSupplier", "主模型供应商", "Main provider", mainParent,
                mainProvider.transform.parent.GetSiblingIndex() + 1);
            embeddingSupplier = (TMP_Dropdown)AddProfileRow(mainProvider, "EmbeddingSupplier", "Embedding 供应商", "Embedding provider", embedParent,
                embeddingProvider.transform.parent.GetSiblingIndex() + 1);
            schemaMode = (TMP_Dropdown)AddProfileRow(mainProvider, "SchemaMode", "出站 Schema", "Outbound schema", mainParent);
            tokenMode = (TMP_Dropdown)AddProfileRow(mainProvider, "TokenMode", "输出 Token 参数", "Output token field", mainParent);
            thinkingMode = (TMP_Dropdown)AddProfileRow(mainProvider, "ThinkingMode", "思考参数映射", "Thinking field", mainParent);
            reasoningMode = (TMP_Dropdown)AddProfileRow(mainProvider, "ReasoningMode", "推理强度", "Reasoning effort", mainParent);
            healthMode = (TMP_Dropdown)AddProfileRow(mainProvider, "HealthMode", "启动检测（生成会计费）", "Startup check (generation costs)", mainParent);
            SetOptions(schemaMode, "raw", "portable_union", "openai_strict", "anthropic");
            SetOptions(tokenMode, "max_tokens", "max_completion_tokens");
            SetOptions(thinkingMode, "none", "enable_thinking", "thinking", "reasoning_effort");
            SetOptions(reasoningMode, "default", "none", "minimal", "low", "medium", "high", "xhigh");
            SetOptions(healthMode, "models", "generation_once");
            sendTemperature = (Toggle)AddProfileRow(thinking, "SendTemperature", "发送 Temperature", "Send temperature", mainParent);
            topP = (TMP_InputField)AddProfileRow(mainTimeout, "TopP", "Top P（可选）", "Top P (optional)", mainParent);
            thinkingBudget = (TMP_InputField)AddProfileRow(mainTimeout, "ThinkingBudget", "原生思考预算（可选）", "Native thinking budget", mainParent);
            embeddingDimensions = (TMP_InputField)AddProfileRow(embeddingTimeout, "EmbeddingDimensions", "维度（可选）", "Dimensions (optional)", embedParent);
            embeddingVersion = (TMP_InputField)AddProfileRow(embeddingBaseUrl, "EmbeddingVersion", "版本 / digest（可选）", "Version / digest", embedParent);
            mainSave = AddAction(mainParent, "MainSave", "保存主模型", "Save main profile", () => Begin(SaveProfile(false, false)));
            mainApply = AddAction(mainParent, "MainApply", "保存并应用主模型", "Save and apply main", () => Begin(SaveProfile(false, true)));
            embeddingSave = AddAction(embedParent, "EmbeddingSave", "保存 Embedding", "Save embedding profile", () => Begin(SaveProfile(true, false)));
            embeddingApply = AddAction(embedParent, "EmbeddingApply", "应用 Embedding 开关 / 配置", "Apply embedding switch / profile", () => Begin(SaveProfile(true, true)));
            AddAction(mainParent, "ClearMainKey", "清除保存的主模型密钥", "Clear saved main key", () => { mainClearKey = true; mainApiKey.text = ""; });
            AddAction(embedParent, "ClearEmbeddingKey", "清除保存的 Embedding 密钥", "Clear saved embedding key", () => { embeddingClearKey = true; embeddingApiKey.text = ""; });
            contractTest = AddAction(mainParent, "ContractTest", "测试正式 NPC 合同", "Test NPC contracts", () => PromptProbe(false, "contracts"));
            probeDetails = AddState(mainParent, "ModelProbeDetails");
            var detailsLayout = probeDetails.GetComponent<LayoutElement>();
            detailsLayout.minHeight = 24; detailsLayout.preferredHeight = -1;
            probeDetails.richText = false;
            probeDetails.gameObject.SetActive(false);
            mainProfileState = AddState(mainParent, "MainProfileState");
            embeddingProfileState = AddState(embedParent, "EmbeddingProfileState");
            indexState = AddState(embedParent, "EmbeddingIndexState");
            indexAction = AddAction(embedParent, "IndexAction", "开始重建", "Start indexing", ControlIndex);
            mainSupplier.onValueChanged.AddListener(value => RequestSupplierSwitch(false, value));
            embeddingSupplier.onValueChanged.AddListener(RequestEmbeddingCloudSwitch);
            EnsureEmbeddingCategories();
        }

        private Button AddAction(Transform parent, string name, string chinese, string english, UnityEngine.Events.UnityAction action)
        {
            var button = (Button)AddProfileRow(saveButton, name, chinese, english, parent);
            button.onClick.AddListener(action);
            return button;
        }

        private TMP_Text AddState(Transform parent, string name)
        {
            TMP_Text text = Instantiate(statusText, parent);
            text.name = name; text.text = ""; text.fontSize = 16; text.enableAutoSizing = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            var element = text.GetComponent<LayoutElement>() ?? text.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = 140; element.minHeight = 140;
            var rect = text.rectTransform; rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one;
            text.gameObject.SetActive(true);
            PlaceMainRow(parent, text.transform);
            return text;
        }

        private void ChooseInitial(bool embedding)
        {
            string selected = embedding ? profilesView.selected_to_activate?.embedding_profile_id : profilesView.selected_to_activate?.main_profile_id;
            string preset = embedding ? profilesView.embedding_profiles?.FirstOrDefault(p => p.profile_id == selected)?.preset_id :
                profilesView.main_profiles?.FirstOrDefault(p => p.profile_id == selected)?.preset_id;
            if (!embedding && preset == null)
                preset = profilesView.main_profiles?.FirstOrDefault()?.preset_id;
            if (embedding && preset == null)
                preset = profilesView.embedding_profiles?.FirstOrDefault()?.preset_id;
            if (!embedding && preset == null)
            {
                LoadSupplierOptions(false, 0, null); mainSupplier.SetValueWithoutNotify(-1);
                LoadEditingProfile(false); initialMainPresetId = ""; return;
            }
            if (preset == null) preset = profilesView.default_embedding_preset_id;
            if (embedding && IsNewEmbeddingConfiguration) preset = "builtin_gemma";
            var all = embedding ? profilesView.embedding_presets : profilesView.main_presets;
            int source = preset == "builtin_gemma" ? 1 : all.First(p => p.preset_id == preset).source == "local" ? 1 : 0;
            (embedding ? embeddingProvider : mainProvider).SetValueWithoutNotify(source);
            LoadSupplierOptions(embedding, source, preset);
            if (embedding) embeddingEnabled.SetIsOnWithoutNotify(profilesView.selected_to_activate?.embedding?.enabled ??
                profilesView.active_runtime?.embedding?.enabled ??
                profilesView.embedding_profiles?.FirstOrDefault(p => p.preset_id == preset)?.config?.enabled ??
                (IsNewEmbeddingConfiguration && preset == "builtin_gemma"));
            LoadEditingProfile(embedding);
            if (embedding) initialEmbeddingPresetId = embeddingEditingPresetId;
            else initialMainPresetId = CurrentPreset(false).preset_id;
        }

        private void LoadSupplierOptions(bool embedding, int source, string desired = null)
        {
            if (embedding) { ConfigureEmbeddingSuppliers(source, desired); return; }
            var choices = profilesView.main_presets.OrderBy(p => p.preset_id == "custom" ? 0 : 1).ToArray();
            if (embedding) embeddingChoices = choices; else mainChoices = choices;
            TMP_Dropdown dropdown = embedding ? embeddingSupplier : mainSupplier;
            EnsureDropdownPlaceholder(dropdown, SettingsUiLanguage.T("请选择", "Please select"));
            SetOptions(dropdown, choices.Select(ProviderCaption).ToArray());
            int index = Array.FindIndex(choices, p => p.preset_id == desired);
            if (index < 0) index = Array.FindIndex(choices, p => p.source == (source == 1 ? "local" : "cloud"));
            dropdown.SetValueWithoutNotify(desired == null ? -1 : index < 0 ? 0 : index);
        }

        private void LoadEditingProfile(bool embedding)
        {
            ModelPresetView preset = CurrentPreset(embedding);
            (embedding ? embeddingProvider : mainProvider).SetValueWithoutNotify(preset.source == "local" ? 1 : 0);
            if (embedding)
            {
                embeddingSupplier.SetValueWithoutNotify(Math.Max(0, Array.FindIndex(embeddingCloudChoices, p => p.preset_id == preset.preset_id)));
                if (RestoreEmbeddingDraft()) return;
            }
            ClearCandidates(embedding ? embeddingCandidates : mainCandidates);
            if (embedding)
            {
                EmbeddingProfileView saved = profilesView.embedding_profiles?.FirstOrDefault(p => p.preset_id == preset.preset_id && p.profile_id == profilesView.selected_to_activate?.embedding_profile_id)
                    ?? profilesView.embedding_profiles?.FirstOrDefault(p => p.preset_id == preset.preset_id);
                EmbeddingConfig c = (loadingDefaults ? null : saved?.config) ?? (preset.protocol == "builtin" ? profilesView.builtin_embedding_defaults : null) ?? new EmbeddingConfig { base_url = preset.base_url, provider_type = preset.protocol,
                    reuse_main_api = false, enabled = true, model = "" };
                embeddingEditingId = saved?.profile_id ?? "embedding-" + preset.preset_id;
                embeddingBaseUrl.SetTextWithoutNotify(c.base_url ?? "");
                embeddingModel.SetTextWithoutNotify(preset.protocol == "builtin" ? "EmbeddingGemma 300M Q4" : c.model ?? "");
                embeddingApiKey.SetTextWithoutNotify(""); embeddingKeyPresent = c.key_present; embeddingClearKey = false;
                embeddingTimeout.SetTextWithoutNotify(c.timeout_seconds.ToString(CultureInfo.InvariantCulture));
                embeddingDimensions.SetTextWithoutNotify(c.dimensions > 0 ? c.dimensions.ToString() : "");
                embeddingVersion.SetTextWithoutNotify(c.model_version ?? "");
                embeddingBaseline = DraftSignature(true);
            }
            else
            {
                MainProfileView saved = profilesView.main_profiles?.FirstOrDefault(p => p.preset_id == preset.preset_id && p.profile_id == profilesView.selected_to_activate?.main_profile_id)
                    ?? profilesView.main_profiles?.FirstOrDefault(p => p.preset_id == preset.preset_id);
                MainModelConfig c = (loadingDefaults ? null : saved?.config) ?? new MainModelConfig { base_url = preset.base_url, provider_type = preset.protocol,
                    model = "",
                    context_length = preset.default_context_length > 0 ? preset.default_context_length : 32768,
                    schema_profile = preset.schema_profile, token_parameter = preset.token_parameter,
                    thinking_parameter = preset.thinking_parameter, send_temperature = preset.send_temperature,
                    health_strategy = preset.health_strategy ?? "models" };
                mainEditingId = saved?.profile_id ?? (string.IsNullOrEmpty(preset.preset_id) ? null : "main-" + preset.preset_id);
                mainBaseUrl.SetTextWithoutNotify(c.base_url ?? ""); mainModel.SetTextWithoutNotify(c.model ?? "");
                mainApiKey.SetTextWithoutNotify(""); mainKeyPresent = saved?.config?.key_present ?? false; mainClearKey = false;
                temperature.SetTextWithoutNotify(c.temperature.ToString(CultureInfo.InvariantCulture)); thinking.SetIsOnWithoutNotify(c.thinking);
                maxOutputTokens.SetTextWithoutNotify(c.max_output_tokens.ToString()); contextLength.SetTextWithoutNotify((c.context_length > 0 ? c.context_length : 8192).ToString());
                RefreshContextPreset();
                mainTimeout.SetTextWithoutNotify(c.timeout_seconds.ToString(CultureInfo.InvariantCulture));
                SelectOption(schemaMode, c.schema_profile); SelectOption(tokenMode, c.token_parameter);
                SelectOption(thinkingMode, c.thinking_parameter); SelectOption(reasoningMode, c.reasoning_effort ?? "default");
                SelectOption(healthMode, c.health_strategy);
                sendTemperature.SetIsOnWithoutNotify(c.send_temperature);
                topP.SetTextWithoutNotify(c.top_p >= 0 ? c.top_p.ToString(CultureInfo.InvariantCulture) : "");
                thinkingBudget.SetTextWithoutNotify(c.thinking_budget > 0 ? c.thinking_budget.ToString() : "");
                mainBaseline = DraftSignature(false);
            }
            RefreshEmbeddingFields(); RefreshLanguage(); RefreshProfileStates();
        }

        private static void SelectOption(TMP_Dropdown dropdown, string text)
        {
            int index = dropdown.options.FindIndex(o => o.text == text);
            dropdown.SetValueWithoutNotify(index < 0 ? 0 : index);
        }

        private string DraftSignature(bool embedding)
        {
            // Length-prefixed fields avoid conflating drafts without serializing credentials to logs or disk.
            string[] values = embedding ? new[] { embeddingBaseUrl.text, embeddingModel.text, embeddingApiKey.text, embeddingTimeout.text,
                embeddingDimensions.text, embeddingVersion.text, embeddingClearKey.ToString(), embeddingEnabled.isOn.ToString() } :
                new[] { mainBaseUrl.text, mainModel.text, mainApiKey.text, temperature.text, thinking.isOn.ToString(), maxOutputTokens.text,
                    contextLength.text, mainTimeout.text, schemaMode.value.ToString(), tokenMode.value.ToString(), thinkingMode.value.ToString(),
                    reasoningMode.value.ToString(), sendTemperature.isOn.ToString(), topP.text, thinkingBudget.text, mainClearKey.ToString(), healthMode.value.ToString() };
            return string.Concat(values.Select(v => (v ?? "").Length + ":" + v));
        }

        private void RequestSourceSwitch(bool embedding, int source)
        {
            if (embedding) { SelectEmbeddingCategory(source == 1 ? 1 : 2); return; }
            var previous = CurrentPreset(embedding);
            (embedding ? embeddingProvider : mainProvider).SetValueWithoutNotify(previous.source == "local" ? 1 : 0);
            RequestProfileSwitch(embedding, () => {
                (embedding ? embeddingProvider : mainProvider).SetValueWithoutNotify(source);
                LoadSupplierOptions(embedding, source); LoadEditingProfile(embedding);
            });
        }

        private void RequestSupplierSwitch(bool embedding, int selected)
        {
            if (embedding) { SwitchEmbeddingPreset(embeddingChoices[selected].preset_id); return; }
            var dropdown = embedding ? embeddingSupplier : mainSupplier;
            string id = embedding ? embeddingEditingId : mainEditingId;
            var choices = embedding ? embeddingChoices : mainChoices;
            string preset = embedding ? profilesView.embedding_profiles?.FirstOrDefault(p => p.profile_id == id)?.preset_id :
                profilesView.main_profiles?.FirstOrDefault(p => p.profile_id == id)?.preset_id;
            preset ??= id != null && id.StartsWith("main-") ? id.Substring(5) : null;
            dropdown.SetValueWithoutNotify(Array.FindIndex(choices, p => p.preset_id == preset));
            RequestProfileSwitch(embedding, () => { dropdown.SetValueWithoutNotify(selected); LoadEditingProfile(embedding); });
        }

        private void RequestProfileSwitch(bool embedding, Action change)
        {
            if (busy || profileSwitchPending) return;
            if (DraftSignature(embedding) == (embedding ? embeddingBaseline : mainBaseline)) { change(); return; }
            profileSwitchPending = true;
            ShowProfileModal("当前供应商有未保存的修改。", "This provider has unsaved changes.", new[] {
                ("保存", "Save", (Action)(() => Begin(SaveThenSwitch(embedding, change)))),
                ("放弃", "Discard", (Action)(() => { profileSwitchPending = false; change(); })),
                ("取消", "Cancel", (Action)(() => profileSwitchPending = false)) });
        }

        private IEnumerator SaveThenSwitch(bool embedding, Action change)
        {
            yield return SaveProfile(embedding, false);
            profileSwitchPending = false;
            if (DraftSignature(embedding) == (embedding ? embeddingBaseline : mainBaseline)) change();
        }

        private JObject BuildProfileCommand(bool embedding)
        {
            if (!embedding && mainSupplier.value < 0)
            { SetStatus("请选择主模型供应商，再保存或测试。", "Select a main provider before saving or testing."); throw new ArgumentException(); }
            ModelPresetView preset = CurrentPreset(embedding);
            JObject config;
            if (embedding)
            {
                if (preset.protocol == "builtin")
                {
                    var controlled = BuiltinEditingConfig();
                    if (controlled == null || string.IsNullOrEmpty(controlled.resource_directory)) throw new ArgumentException();
                    return ModelProfileJson.Save(revision, true, embeddingEditingId, preset.preset_id,
                        ModelProfileJson.Embedding(controlled), "", false);
                }
                if (!TryFloat(embeddingTimeout.text, .01f, 600, out float timeout)) throw new ArgumentException();
                int dimensions = 0;
                if (!string.IsNullOrWhiteSpace(embeddingDimensions.text) && !TryInt(embeddingDimensions.text, 1, 65536, out dimensions)) throw new ArgumentException();
                config = ModelProfileJson.Embedding(new EmbeddingConfig { provider_type = preset.protocol, base_url = embeddingBaseUrl.text.Trim(),
                    model = SelectedModel(embeddingModel, embeddingCandidates), timeout_seconds = timeout, dimensions = dimensions, model_version = embeddingVersion.text.Trim() });
            }
            else
            {
                if (!TryFloat(temperature.text, 0, 2, out float temp) || !TryInt(maxOutputTokens.text, 1, 32768, out int tokens) ||
                    !TryMainContext(contextLength.text, out int context) || !TryFloat(mainTimeout.text, .01f, 600, out float timeout)) throw new ArgumentException();
                float p = -1; int budget = 0;
                if (!string.IsNullOrWhiteSpace(topP.text) && !TryFloat(topP.text, 0, 1, out p)) throw new ArgumentException();
                if (!string.IsNullOrWhiteSpace(thinkingBudget.text) && !TryInt(thinkingBudget.text, 1024, 32768, out budget)) throw new ArgumentException();
                string displayName = profilesView.main_profiles?.FirstOrDefault(p => p.profile_id == mainEditingId)?.config?.display_name ?? preset.name;
                config = ModelProfileJson.Main(new MainModelConfig { display_name = displayName, provider_type = preset.protocol,
                    base_url = mainBaseUrl.text.Trim(), model = SelectedModel(mainModel, mainCandidates), temperature = temp,
                    thinking = thinking.isOn, max_output_tokens = tokens, context_length = context, timeout_seconds = timeout,
                    schema_profile = schemaMode.options[schemaMode.value].text, token_parameter = tokenMode.options[tokenMode.value].text,
                    thinking_parameter = thinkingMode.options[thinkingMode.value].text, send_temperature = sendTemperature.isOn, top_p = p,
                    thinking_budget = budget, reasoning_effort = reasoningMode.value == 0 ? null : reasoningMode.options[reasoningMode.value].text,
                    health_strategy = healthMode.options[healthMode.value].text });
                var existing = profilesView.main_profiles?.FirstOrDefault(p => p.profile_id == mainEditingId)?.config;
                if (existing != null && existing.context_length <= 0 && context == 8192)
                    config["context_length"] = JValue.CreateNull(); // Preserve an unchanged legacy unspecified capacity.
            }
            return ModelProfileJson.Save(revision, embedding, embedding ? embeddingEditingId : mainEditingId, preset.preset_id,
                config, embedding ? embeddingApiKey.text : mainApiKey.text, embedding ? embeddingClearKey : mainClearKey);
        }

        private IEnumerator SaveProfile(bool embedding, bool activate)
        {
            ModelProfilesView result = null;
            string error = null;
            if (!embedding || !activate || embeddingEnabled.isOn)
            {
                JObject command;
                try { command = BuildProfileCommand(embedding); }
                catch (ArgumentException) { SetStatus("配置参数无效。", "Invalid configuration parameters."); yield break; }
                yield return client.SaveProfile(command.ToString(Formatting.None), (value, code) => { result = value; error = code; });
                if (error != null || result == null) { SetStatus("保存失败：" + error, "Save failed: " + error); yield break; }
                profilesView = result; revision = result.revision;
                LoadEditingProfile(embedding);
            }
            if (activate)
            {
                var command = new JObject { ["expected_revision"] = revision, ["kind"] = embedding ? "embedding" : "main",
                    ["profile_id"] = embedding ? embeddingEditingId : mainEditingId, ["embedding_enabled"] = embedding && embeddingEnabled.isOn };
                yield return client.ActivateProfile(command.ToString(Formatting.None), (value, code) => { result = value; error = code; });
                if (error != null || result == null) { SetStatus("应用失败：" + error, "Apply failed: " + error); yield break; }
                profilesView = result; revision = result.revision; savedThisSession = true;
                SetStatus("已选择待启用配置，请重启 Backend 并检查状态。", "Configuration staged. Restart Backend and check its status.");
            }
            else SetStatus("供应商配置已保存，运行模型未改变。", "Profile saved; runtime models are unchanged.");
            RefreshProfileStates();
        }

        private IEnumerator SaveBothProfiles()
        {
            restartOfferPending = false;
            modelSaveConfirmed = false;
            if (defaultsRestored)
            {
                ModelProfilesView reset = null; string resetError = null;
                yield return client.ResetMainProfile(BuildMainResetCommand().ToString(Formatting.None),
                    (value, code) => { reset = value; resetError = code; });
                if (!ApplyMainResetResult(reset, resetError)) yield break;
                yield return ConfirmSavedApplication(reset);
                yield break;
            }
            if (embeddingEnabled.isOn && CurrentPreset(true).protocol == "builtin" && BuiltinEditingConfig() == null)
            {
                SetStatus("当前 Backend 不支持内置配置，草稿未保存；请在授权后重启新版 Backend。",
                    "This Backend does not support built-in configuration. Drafts not saved; restart the updated Backend when authorized.");
                yield break;
            }
            JObject command;
            try { command = BuildCombinedProfileCommand(); }
            catch (ArgumentException) { SetStatus("配置参数无效；未保存或应用任何配置。", "Invalid parameters; no configuration saved or applied."); yield break; }
            SetStatus("正在保存配置……", "Saving configuration...");
            ModelProfilesView result = null; string error = null;
            yield return client.SaveAndActivateProfiles(command.ToString(Formatting.None), (value, code) => { result = value; error = code; });
            if (error != null || result == null)
            {
                if (error == "MODEL_CONFIG_VALUE_INVALID")
                    SetStatus("配置无效，两套配置均未保存或应用；请检查参数，草稿保留。", "Invalid configuration; neither profile saved or applied. Check parameters; drafts retained.");
                else if (error == "MODEL_CONFIG_REVISION_CONFLICT")
                    SetStatus("配置版本已改变，未提交修改；请重新检查状态，草稿保留。", "Configuration changed; edits not submitted. Check status again; drafts retained.");
                else SetStatus("保存并应用未确认，请重新加载状态后检查；草稿保留。", "Save and apply unconfirmed. Reload status to check; drafts retained.");
                yield break;
            }
            profilesView = result; revision = result.revision; savedThisSession = true;
            defaultsRestored = false; embeddingDrafts.Remove(embeddingEditingPresetId);
            modelSaveConfirmed = true;
            initialMainPresetId = CurrentPreset(false).preset_id; initialEmbeddingPresetId = embeddingEditingPresetId;
            LoadEditingProfile(false); LoadEditingProfile(true);
            RefreshProfileStates();
            SetStatus("配置已保存，正在确认运行状态……", "Configuration saved; checking runtime status...");
            yield return ConfirmSavedApplication(result);
        }

        private JObject BuildMainResetCommand() => new JObject {
            ["expected_revision"] = resetMainRevision, ["profile_id"] = resetMainProfileId,
            ["key_action"] = "clear" };

        private bool ApplyMainResetResult(ModelProfilesView result, string error)
        {
            if (error != null || result == null || result.configured || result.selected_to_activate != null ||
                result.revision != resetMainRevision + 1 ||
                result.main_profiles?.Any(p => p.profile_id == resetMainProfileId) == true)
            {
                SetStatus("主模型重置未确认，草稿保留；请重新加载核对，不能视为密钥已删除。" + (error == null ? "" : " (" + error + ")"),
                    "Main reset unconfirmed; draft retained. Reload to verify; key removal is not confirmed." + (error == null ? "" : " (" + error + ")"));
                return false;
            }
            defaultsRestored = false; resetMainProfileId = null;
            profilesView = result; revision = result.revision; savedThisSession = modelSaveConfirmed = true;
            embeddingDrafts.Clear(); ChooseInitial(false); ChooseInitial(true);
            RefreshProfileLanguage(); RefreshCandidateControls();
            return true;
        }

        private JObject BuildCombinedProfileCommand()
        {
            bool unsupportedBuiltin = CurrentPreset(true).protocol == "builtin" && BuiltinEditingConfig() == null;
            bool saveEmbedding = !unsupportedBuiltin && (embeddingEnabled.isOn || DraftSignature(true) != embeddingBaseline ||
                profilesView.embedding_profiles?.Any(p => p.profile_id == embeddingEditingId) == true);
            return new JObject { ["expected_revision"] = revision, ["main"] = BuildProfileCommand(false),
                ["embedding"] = saveEmbedding ? BuildProfileCommand(true) : JValue.CreateNull(),
                ["embedding_enabled"] = embeddingEnabled.isOn };
        }

        private void EnsureCompactLayout()
        {
            if (advancedSettings != null) return;
            Transform content = mainProvider.transform.parent.parent;
            advancedToggle = AddAction(content, "AdvancedSettingsToggle", "高级设置", "Advanced settings", ToggleAdvancedSettings);
            advancedToggle.transform.parent.SetAsLastSibling();
            advancedToggle.transform.parent.Find("Label").gameObject.SetActive(false);
            var toggleRect = advancedToggle.GetComponent<RectTransform>();
            toggleRect.anchorMin = Vector2.zero; toggleRect.anchorMax = Vector2.one;
            toggleRect.offsetMin = new Vector2(20, 12); toggleRect.offsetMax = new Vector2(-20, -12);
            advancedSettings = new GameObject("AdvancedSettings", typeof(RectTransform), typeof(VerticalLayoutGroup));
            advancedSettings.transform.SetParent(content, false);
            var stack = advancedSettings.GetComponent<VerticalLayoutGroup>();
            stack.spacing = SettingsPanelTheme.Spacing; stack.childControlWidth = stack.childControlHeight = true;
            stack.childForceExpandWidth = true; stack.childForceExpandHeight = false;
            foreach (Component control in new Component[] { temperature, thinking, maxOutputTokens, contextLength, mainTimeout,
                schemaMode, tokenMode, thinkingMode, reasoningMode, healthMode, sendTemperature, topP, thinkingBudget,
                embeddingTimeout, embeddingDimensions, embeddingVersion })
                control.transform.parent.SetParent(advancedSettings.transform, false);
            var details = AddState(advancedSettings.transform, "ConfigurationDetailsTitle");
            details.fontStyle = FontStyles.Bold;
            CompactSectionTitle(details);
            var diagnostics = new GameObject("SettingsDiagnostics", typeof(RectTransform), typeof(VerticalLayoutGroup));
            diagnostics.transform.SetParent(advancedSettings.transform, false);
            var diagnosticsLayout = diagnostics.GetComponent<VerticalLayoutGroup>();
            diagnosticsLayout.spacing = 8; diagnosticsLayout.childForceExpandHeight = false;
            diagnosticsLayout.childControlHeight = diagnosticsLayout.childControlWidth = true;
            var diagnosticsTitle = AddState(diagnostics.transform, "DiagnosticsTitle"); diagnosticsTitle.fontStyle = FontStyles.Bold;
            CompactSectionTitle(diagnosticsTitle);
            contractTest.transform.parent.SetParent(diagnostics.transform, false);
            mainConnectionState = AddState(content, "MainConnectionState");
            mainConnectionState.transform.SetSiblingIndex(mainProfileState.transform.GetSiblingIndex());
            var embedContent = embeddingProfileState.transform.parent;
            embeddingConnectionState = AddState(embedContent, "EmbeddingConnectionState");
            embeddingConnectionState.gameObject.SetActive(false); // Runtime/resource feedback is displayed once in MemoryIndex.
            foreach (var state in new[] { mainConnectionState, embeddingConnectionState, mainProfileState, embeddingProfileState, indexState })
            {
                var size = state.GetComponent<LayoutElement>(); size.preferredHeight = -1; size.minHeight = 24;
            }
            mainProfileState.transform.SetParent(advancedSettings.transform, false);
            embeddingProfileState.transform.SetParent(advancedSettings.transform, false);
            mainProfileState.transform.SetSiblingIndex(details.transform.GetSiblingIndex() + 1);
            embeddingProfileState.transform.SetSiblingIndex(details.transform.GetSiblingIndex() + 2);
            // Keep the existing buttons and listeners, including the explicit restart authority.
            foreach (string name in new[] { "ClearMainKeyRow", "ClearEmbeddingKeyRow", "GenerationParameters" })
                foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
                    if (child.name == name) { child.SetParent(name == "GenerationParameters" ? advancedSettings.transform : diagnostics.transform, false); if (name == "GenerationParameters") child.SetAsFirstSibling(); break; }
            foreach (var localized in panel.GetComponentsInChildren<SettingsLocalizedLabel>(true))
            {
                if (localized.Chinese == "生成参数") { localized.transform.SetParent(advancedSettings.transform, false); localized.transform.SetAsFirstSibling(); }
                if (localized.gameObject.name == "Main LLM") { localized.Chinese = "主模型"; localized.English = "Main model"; }
            }
            foreach (Button button in new[] { restartBackendButton, recheckButton })
            {
                button.transform.SetParent(diagnostics.transform, false);
                var element = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
                element.ignoreLayout = false; element.minHeight = element.preferredHeight = 48;
                button.gameObject.SetActive(true);
            }
            foreach (Button button in new[] { mainSave, mainApply, embeddingSave, embeddingApply })
                button.transform.parent.gameObject.SetActive(false);
            mainProvider.transform.parent.gameObject.SetActive(false);
            embeddingProvider.transform.parent.gameObject.SetActive(false);
            EnsureRestoreDefaultsButton();
            EnsureMemoryIndexSection(content);
            EnsureContextPolicyControls();
            var caption = saveButton.GetComponentInChildren<TMP_Text>(true);
            if (caption != null) { caption.enableAutoSizing = true; caption.fontSizeMin = 12; caption.fontSizeMax = 20; }
            advancedSettings.SetActive(false);
            ApplyInformationArchitecture();
            RefreshEmbeddingFields();
            RefreshProfileLanguage();
            var scroll = panel.GetComponentInChildren<ScrollRect>(true);
            if (scroll != null) { LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content); scroll.verticalNormalizedPosition = 1; }
        }

        private void ToggleAdvancedSettings()
        {
            if (busy || dropdownsWarming) return;
            advancedSettings.SetActive(!advancedSettings.activeSelf);
            if (advancedSettings.activeSelf && Application.isPlaying && isActiveAndEnabled && !dropdownsWarming)
                StartCoroutine(EnableDropdownsAfterStart());
            RefreshProfileLanguage();
            RebuildSettingsLayout();
        }

        private void PromptProbe(bool embedding, string operation)
        {
            if (defaultsRestored) { SetStatus("主模型重置尚未保存；请保存并应用，或重新加载取消。", "Main reset is not saved. Save & Apply, or reload to cancel."); return; }
            if (string.IsNullOrWhiteSpace((embedding ? embeddingModel : mainModel).text))
            { SetStatus("请先填写当前草稿的模型 ID；尚未发起测试。", "Enter the current draft's model ID before testing; no request was sent."); return; }
            if (!embedding && string.IsNullOrWhiteSpace(mainApiKey.text) &&
                (mainClearKey || !mainKeyPresent && CurrentPreset(false).source == "cloud" && CurrentPreset(false).preset_id != "custom"))
            { SetStatus("请填写主模型密钥；待清除或已清除的密钥不会用于测试。", "Enter a main-model key; pending/cleared credentials are not used for tests."); return; }
            if (embedding && CurrentPreset(true).protocol == "builtin")
            { Begin(ProbeCurrentProfile(true, operation)); return; }
            ShowProfileModal(operation == "contracts" ? "将执行 3 次合成上下文请求，可能产生费用。" : "连接测试会发送合成文本，可能产生费用。",
                operation == "contracts" ? "Run 3 synthetic contract requests. API charges may apply." : "Send a synthetic connection test. API charges may apply.",
                new[] { ("确认", "Confirm", (Action)(() => Begin(ProbeCurrentProfile(embedding, operation)))), ("取消", "Cancel", (Action)(() => { })) });
        }

        private IEnumerator ProbeCurrentProfile(bool embedding, string operation)
        {
            if (defaultsRestored) { SetStatus("主模型密钥待清除，不使用旧配置获取模型或测试。", "Main key pending clear; the old configuration is not used for discovery or tests."); yield break; }
            if (!embedding && operation == "discover")
            {
                if (mainSupplier.value < 0)
                { SetStatus("请先选择主模型供应商。", "Select a main-model provider first."); yield break; }
                if (!Uri.TryCreate(mainBaseUrl.text.Trim(), UriKind.Absolute, out var url) ||
                    (url.Scheme != "http" && url.Scheme != "https"))
                { SetStatus("请填写有效的主模型 API 地址。", "Enter a valid main-model API URL."); yield break; }
                if (CurrentPreset(false).source == "cloud" && CurrentPreset(false).preset_id != "custom" &&
                    string.IsNullOrWhiteSpace(mainApiKey.text) && (!mainKeyPresent || mainClearKey))
                { SetStatus("请填写主模型 API 密钥。", "Enter the main-model API key."); yield break; }
            }
            if (operation != "discover" && string.IsNullOrWhiteSpace((embedding ? embeddingModel : mainModel).text))
            { SetStatus("请先填写当前草稿的模型 ID；尚未发起测试。", "Enter the current draft's model ID before testing; no request was sent."); yield break; }
            if (!embedding && mainClearKey && string.IsNullOrWhiteSpace(mainApiKey.text))
            { SetStatus("主模型密钥待清除；请填写新密钥后再获取模型或测试。", "Main key pending clear; enter a new key before discovery or testing."); yield break; }
            JObject command;
            try { command = BuildProfileCommand(embedding); }
            catch (ArgumentException) { SetStatus("配置参数无效。", "Invalid configuration parameters."); yield break; }
            if (operation == "discover" && string.IsNullOrEmpty((string)command["config"]["model"])) command["config"]["model"] = "discovery-only";
            command["operation"] = operation; command["confirm_paid_requests"] = operation != "discover";
            ModelProbeView result = null; string error = null;
            yield return client.ProbeProfile(command.ToString(Formatting.None), (value, code) => { result = value; error = code; });
            if (operation == "discover")
            {
                ApplyDiscoveryResult(embedding, result == null ? null : new DiscoverModelsResponse { success = result.success, models = result.models, error_code = result.error_code }, error);
                yield break;
            }
            ApplyProbeResult(result, error);
        }

        private void ApplyProbeResult(ModelProbeView result, string error)
        {
            lastProbeResult = result; lastProbeError = error;
            statusText.textWrappingMode = TextWrappingModes.NoWrap;
            statusText.overflowMode = TextOverflowModes.Ellipsis;
            statusText.enableAutoSizing = true; statusText.fontSizeMin = 12; statusText.fontSizeMax = 17;
            SetStatus(result?.success == true ? "测试通过；详见页内结果，未测试能力未确认。" : "测试失败；详见页内结果。",
                result?.success == true ? "Test passed; see results. Untested capabilities are unverified." : "Test failed; see results.");
            RefreshProbeDetails();
        }

        private void RefreshProbeDetails()
        {
            if (probeDetails == null) return;
            bool hasResult = lastProbeResult != null || lastProbeError != null;
            probeDetails.gameObject.SetActive(hasResult);
            probeDetails.fontSize=19;
            if(contractSummary!=null)
            {
                contractSummary.gameObject.SetActive(hasResult);
                var results=lastProbeResult?.results ?? Array.Empty<ModelContractResult>();
                int passed=results.Count(r=>r.status=="PASS"),failed=results.Count(r=>r.status=="FAIL"),notRun=results.Count(r=>r.status=="NOT_RUN");
                contractSummary.text=SettingsUiLanguage.T($"通过 {passed} 项 · 失败 {failed} 项 · 未测试 {notRun} 项",$"Passed {passed} · Failed {failed} · Not run {notRun}");
            }
            if (!hasResult) return;
            string detail = SettingsUiLanguage.T("合同 / 连接测试结果", "Contract / connection test results");
            foreach (var item in lastProbeResult?.results ?? Array.Empty<ModelContractResult>())
            {
                detail += "\n\n" + ContractCaseCaption(item.@case) + "  ·  " + item.status +
                    "\n  " + item.@case + (item.error_code == null ? "" : " · " + item.error_code);
                if (item.error_code == "COGNITIVE_PROMPT_BUDGET_EXCEEDED")
                    detail += SettingsUiLanguage.T("\n草稿的上下文容量不足；请求未发送，请选择模型支持的至少 16K。", "\nDraft context capacity is insufficient; no request was sent. Choose a supported capacity of at least 16K.");
            }
            string error = lastProbeError ?? lastProbeResult?.error_code;
            if (error != null) detail += SettingsUiLanguage.T("\n错误：", "\nError: ") + error;
            if (error == "MODEL_PROFILE_MODEL_REQUIRED") detail += SettingsUiLanguage.T("\n当前草稿缺少有效模型 ID；请求未发送。", "\nThe draft needs a valid model ID; no request was sent.");
            if (error == "MODEL_PROFILE_URL_INVALID") detail += SettingsUiLanguage.T("\n当前草稿 API 地址无效；请求未发送。", "\nThe draft API URL is invalid; no request was sent.");
            if (error == "MODEL_PROFILE_DRAFT_INVALID") detail += SettingsUiLanguage.T("\n当前草稿参数无效；请求未发送，请检查配置。", "\nDraft parameters are invalid; no request was sent. Check the configuration.");
            detail += SettingsUiLanguage.T("\n未执行的能力未确认；结构结果不证明语义或世界执行。",
                "\nUntested capabilities are unverified; structure does not prove semantics or world execution.");
            probeDetails.text = detail;
        }

        private static string ContractCaseCaption(string name) => name switch
        {
            "cognition_step_0" => SettingsUiLanguage.T("认知决策（首轮）","Cognition decision (first step)"),
            "cognition_step_1" => SettingsUiLanguage.T("记忆召回后的回复","Reply after memory recall"),
            "dialogue_continuation" => SettingsUiLanguage.T("主动续话","Proactive continuation"),
            "memory_consolidation" => SettingsUiLanguage.T("记忆整理","Memory consolidation"),
            "relationship_reflection" => SettingsUiLanguage.T("关系反思","Relationship reflection"),
            "action_feedback" => SettingsUiLanguage.T("动作失败说明","Action failure explanation"),
            _ => SettingsUiLanguage.T("连接或能力检查","Connection or capability check")
        };

        private void ShowProfileModal(string chinese, string english, (string chinese, string english, Action action)[] actions)
        {
            if (profileModal != null) return;
            // All existing callers put their explicit Cancel action last; use that same callback.
            profileModalCancel = actions.Length == 0 ? null : actions[actions.Length - 1].action;
            profileModal = new GameObject("ModelProfileConfirmation", typeof(RectTransform), typeof(Image));
            profileModal.transform.SetParent(panel.transform, false);
            var rect = profileModal.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            profileModal.GetComponent<Image>().color = new Color(.1f, .1f, .1f, .96f);
            var text = Instantiate(statusText, profileModal.transform); text.text = SettingsUiLanguage.T(chinese, english);
            text.fontSize = 20; text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 20;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.color = Color.white; text.alignment = TextAlignmentOptions.Center;
            text.rectTransform.anchorMin = new Vector2(.08f, .5f); text.rectTransform.anchorMax = new Vector2(.92f, .8f);
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            for (int i = 0; i < actions.Length; i++)
            {
                var action = actions[i]; var button = Instantiate(saveButton, profileModal.transform);
                button.onClick = new Button.ButtonClickedEvent(); button.gameObject.SetActive(true);
                // Probe dialogs are created while Run temporarily locks Save.
                // Confirmation buttons must not inherit that transient lock.
                button.interactable = true;
                SetButton(button, action.chinese, action.english);
                var br = button.GetComponent<RectTransform>();
                br.anchorMin = new Vector2(.08f + i * .84f / actions.Length, .25f);
                br.anchorMax = new Vector2(.08f + (i + 1) * .84f / actions.Length, .37f);
                br.offsetMin = new Vector2(5, 0); br.offsetMax = new Vector2(-5, 0);
                button.onClick.AddListener(() => { RemoveProfileModal(); action.action(); });
            }
        }

        private void RefreshProfileStates()
        {
            if (profilesView == null || mainProfileState == null || mainChoices == null || embeddingChoices == null) return;
            string Describe(bool embedding)
            {
                string editing = embedding ? embeddingEditingId : mainEditingId;
                var saved = embedding ? profilesView.embedding_profiles?.Any(p => p.profile_id == editing) : profilesView.main_profiles?.Any(p => p.profile_id == editing);
                string selected = embedding ? profilesView.selected_to_activate?.embedding_profile_id : profilesView.selected_to_activate?.main_profile_id;
                string active = embedding ? profilesView.active_runtime?.embedding_profile_id : profilesView.active_runtime?.main_profile_id;
                string activeModel = embedding ? profilesView.active_runtime?.embedding?.model : profilesView.active_runtime?.main_model?.model;
                string selectedModel = embedding ? profilesView.selected_to_activate?.embedding?.model : profilesView.selected_to_activate?.main_model?.model;
                return SettingsUiLanguage.T("编辑：", "Editing: ") + editing +
                    SettingsUiLanguage.T(" · 已保存：", " · Saved: ") + (saved == true ? editing : SettingsUiLanguage.T("无", "none")) +
                    SettingsUiLanguage.T("\n选定配置：", "\nSelected: ") + (selected ?? "-") + " · " + (selectedModel ?? "-") +
                    SettingsUiLanguage.T("\n实际运行：", "\nRuntime: ") + (active ?? "-") + " · " + (activeModel ?? "-");
            }
            mainProfileState.text = Describe(false); embeddingProfileState.text = Describe(true);
            if (mainConnectionState != null)
            {
                string pending = profilesView.restart_required ? SettingsUiLanguage.T(" · 待重启生效", " · restart required") : "";
                RefreshContextCapacityState();
                embeddingConnectionState.text = SettingsUiLanguage.T("当前运行：", "Runtime: ") +
                    (profilesView.active_runtime?.embedding?.enabled == true ? profilesView.active_runtime.embedding.model : SettingsUiLanguage.T("未启用", "disabled")) + pending;
                RefreshBuiltinState();
            }
        }

        private void RefreshContextCapacityState()
        {
            if (mainConnectionState == null) return;
            string pending = profilesView?.restart_required == true
                ? SettingsUiLanguage.T(" · 待重启生效", " · restart required") : "";
            string text = SettingsUiLanguage.T("当前运行：", "Runtime: ") +
                (profilesView?.active_runtime?.main_model?.model ?? "-") + pending;
            if (int.TryParse(contextLength?.text, out int draft) && draft > 0)
                text += SettingsUiLanguage.T("\n编辑配置：", "\nEditing capacity: ") + draft + " tokens。" +
                    (draft < 16384 ? SettingsUiLanguage.T(
                        "低于此 Demo 建议的最低 16K；请保持模型实际支持值，必要输入可能超限。",
                        "Below this Demo's recommended minimum 16K; keep the model's supported value. Required input may exceed it.") : "") +
                    SettingsUiLanguage.T("实际模型容量尚未验证。32K 是推荐预算，不代表模型支持；64K 同样需要实际 Provider 或本地模型支持。请按模型能力设置。",
                        "Actual model capacity is unverified. 32K is the recommended budget, not a capability guarantee. 64K also requires support from the actual provider or local model. Set a supported value.");
            var active = profilesView?.active_runtime?.main_model;
            if (active != null)
            {
                int capacity = active.context_length > 0 ? active.context_length : 8192;
                text += SettingsUiLanguage.T("\n实际运行配置容量：", "\nActive configured capacity: ") + capacity + " tokens";
                if (active.context_length <= 0) text += SettingsUiLanguage.T("（旧配置未声明，沿用 8K 兜底）", " (legacy unspecified; existing 8K fallback)");
                if (capacity < 16384) text += SettingsUiLanguage.T(
                    "。低于建议的最低 16K；请打开高级设置调整，保存后按待重启状态处理。",
                    ". Below the recommended 16K; adjust Advanced settings and follow the restart-required status after saving.");
            }
            (mainCapacityState ?? mainConnectionState).text = text;
            RefreshBriefAiState();
        }

        private void RefreshProfileLanguage()
        {
            if (mainSupplier == null) return;
            if (embeddingCloudChoices != null) SetOptions(embeddingSupplier, embeddingCloudChoices.Select(p => p.name).ToArray());
            foreach (var item in profileLabels)
            {
                SetRow(item.control, item.chinese, item.english);
                if (item.control is Button button) SetButton(button, item.chinese, item.english);
            }
            SetButton(saveButton, "保存并应用配置", "Save & Apply");
            SetButton(restoreDefaultsButton, defaultsRestored ? "取消重置（重新加载）" : "恢复默认",
                defaultsRestored ? "Cancel reset (reload)" : "Restore Defaults");
            if (advancedSettings != null)
            {
                if (developerDiagnostics != null) developerDiagnostics.Find("DiagnosticsTitle").GetComponent<TMP_Text>().text = SettingsUiLanguage.T("诊断信息", "Diagnostics");
            }
            if (advancedToggle != null) SetButton(advancedToggle, advancedSettings.activeSelf ? "主模型生成参数（高级） −" : "主模型生成参数（高级） +",
                advancedSettings.activeSelf ? "Main generation parameters −" : "Main generation parameters +");
            RefreshAiSectionLanguage();
            if (mainSupplier.placeholder is TMP_Text mainPlaceholder) mainPlaceholder.text = SettingsUiLanguage.T("请选择", "Please select");
            RefreshContextPolicy();
            SetPlaceholder(mainApiKey, mainKeyPresent ? "********" : "输入 API 密钥", mainKeyPresent ? "********" : "Enter API key");
            SetPlaceholder(embeddingApiKey, embeddingKeyPresent ? "********" : "输入 API 密钥", embeddingKeyPresent ? "********" : "Enter API key");
            SetPlaceholder(embeddingVersion, "版本 / digest（可选）", "Version / digest (optional)");
            SetPlaceholder(embeddingDimensions, "维度（可选）", "Dimensions (optional)");
            SetPlaceholder(topP, "Top P（可选）", "Top P (optional)");
            SetPlaceholder(thinkingBudget, "原生思考预算（可选）", "Native thinking budget (optional)");
            if(mainChoices==null || embeddingChoices==null)return;
            contextLength.transform.parent.gameObject.SetActive(true);
            thinkingBudget.transform.parent.gameObject.SetActive(CurrentPreset(false).protocol is "gemini_native" or "anthropic_native");
            thinkingMode.transform.parent.gameObject.SetActive(CurrentPreset(false).protocol == "openai_compatible");
            tokenMode.transform.parent.gameObject.SetActive(CurrentPreset(false).protocol == "openai_compatible");
            healthMode.transform.parent.gameObject.SetActive(CurrentPreset(false).protocol == "openai_compatible");
            RefreshEmbeddingFields(); RefreshProfileStates(); RefreshIndexState(); RefreshProbeDetails();
            RefreshResetDraftLock();
        }

        private void StartProfilePolling()
        {
            StopProfilePolling();
            if (isActiveAndEnabled) indexPoll = StartCoroutine(PollIndex());
        }
        private void StopProfilePolling()
        {
            if (indexPoll != null) StopCoroutine(indexPoll);
            indexPoll = null;
            RemoveProfileModal(); profileSwitchPending = false;
        }
        private void RemoveProfileModal()
        {
            if (profileModal != null)
            {
                if (Application.isPlaying) Destroy(profileModal); else DestroyImmediate(profileModal);
            }
            profileModal = null;
        }
        private IEnumerator PollIndex()
        {
            while (isActiveAndEnabled)
            {
                yield return client.GetEmbeddingIndex((value, error) => {
                    indexReadError = error; currentIndex = error == null ? value : null;
                    if (value != null && error == null) indexOperationError = null;
                    RefreshIndexState();
                    if(configurationContent!=null)SetSettingsPage(SettingsPage);
                });
                yield return new WaitForSecondsRealtime(2);
            }
        }
        private void RefreshIndexState() => RefreshMemoryIndex();
        private void ControlIndex()
        {
            if (busy || currentIndex == null || !currentIndex.applied || currentIndex.control_allowed == false ||
                currentIndex.error_code == "EMBEDDING_CONTROL_STATE_INVALID") return;
            if (!indexAction.interactable) return;
            string action = currentIndex.status == "running" ? "pause" : currentIndex.status == "waiting" ? "start" : "resume";
            if (action != "pause" && currentIndex.cloud && !currentIndex.consented)
                ShowProfileModal("待处理记忆摘要将上传至此云端服务，可能产生费用；原有记忆不会删除。", "Pending memory summaries will be sent to this cloud service. API charges may apply. Existing memories are preserved.",
                    new[] { ("确认开始", "Confirm start", (Action)(() => Begin(ChangeIndex(action, true)))), ("取消", "Cancel", (Action)(() => { })) });
            else Begin(ChangeIndex(action, false));
        }
        private IEnumerator ChangeIndex(string action, bool confirm)
        {
            var command = new JObject { ["action"] = action, ["model_key"] = currentIndex.model_key, ["confirm_cloud_transfer"] = confirm };
            yield return client.ControlEmbeddingIndex(command.ToString(Formatting.None), (value, error) => {
                if (error == null && value != null)
                {
                    currentIndex = value; indexReadError = indexOperationError = null;
                    SetStatus(action == "pause" ? "新向量请求已暂停；在途请求可能仍会完成。" : "索引操作已确认，请查看当前状态。",
                        action == "pause" ? "New vector requests are paused; in-flight requests may still finish." : "Index operation confirmed. See current status.");
                }
                else
                {
                    indexOperationError = error ?? "INVALID_RESPONSE";
                    SetStatus("", "");
                    if (error == "EMBEDDING_CONTROL_STATE_INVALID") currentIndex.control_allowed = false;
                }
                RefreshIndexState();
            });
        }
        private static string IndexErrorText(string code)
        {
            if (code?.StartsWith("BUILTIN_EMBEDDING_", StringComparison.Ordinal) == true) return BuiltinErrorText(code);
            var message = P0FeedbackText.IndexError(code);
            return SettingsUiLanguage.T(message.Chinese, message.English);
        }
    }
}
