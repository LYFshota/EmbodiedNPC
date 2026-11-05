using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        private Transform mainConnectionGroup, recentDialogueGroup, vectorRetrievalGroup, aiStateGroup;
        private TMP_Text mainCapacityState, mainSectionTitle, embeddingSectionTitle, embeddingPurpose, aiStateTitle, configurationDiagnostics, embeddingBrief;
        private Button diagnosticsToggle;
        private TMP_Text operationFeedback, advancedPurpose;
        private string operationChinese, operationEnglish, operationSignature, operationKind;
        private bool operationEmbedding;
        private string verifiedMainSignature;
        private bool? verifiedMainSuccess;
        private RuntimeStatusView checkedRuntimeStatus;
        public RuntimeStatusView CheckedRuntimeStatus => checkedRuntimeStatus;
        public string RuntimeReadError => runtimeReadError;
        private string runtimeReadError;
        private void DraftEdited()
        {
            RefreshBriefAiState();
            if (!busy && profilesView != null && mainChoices != null && embeddingChoices != null && (HasUnsavedSettings() || ContextPolicyDirty()))
                SetStatus("存在未保存修改，请保存并应用。", "There are unsaved changes. Save & Apply when ready.");
        }
        private string verifiedMainError;

        private string ProbeSignature(bool embedding) => (embedding ? embeddingEditingId : mainEditingId) + ":" +
            (embedding ? embeddingEditingPresetId : CurrentPreset(false).preset_id) + ":" + revision + ":" + DraftSignature(embedding);

        private void ShowOperation(string chinese, string english, bool embedding, string kind, string signature = null)
        {
            operationChinese = chinese; operationEnglish = english; operationEmbedding = embedding; operationKind = kind;
            operationSignature = signature ?? ProbeSignature(embedding);
            RefreshOperationFeedback();
            SetStatus(chinese, english);
        }

        private void RefreshOperationFeedback()
        {
            if (operationFeedback == null || mainChoices == null || embeddingChoices == null) return;
            operationFeedback.gameObject.SetActive(operationChinese != null);
            bool current = operationSignature == ProbeSignature(operationEmbedding);
            if (!current && !busy) SetStatus("配置已修改，先前结果不代表当前草稿，请重新获取或测试。", "Configuration changed. Previous results do not verify this draft; fetch or test again.");
            operationFeedback.text = current ? SettingsUiLanguage.T(operationChinese, operationEnglish) :
                SettingsUiLanguage.T("配置已修改，先前获取或测试结果不代表当前草稿，请重新操作。", "Configuration changed. Previous discovery or test results do not verify this draft; run again.");
        }

        private static void EnsureDropdownPlaceholder(TMP_Dropdown dropdown, string caption)
        {
            if (dropdown.placeholder == null)
            {
                TMP_Text text;
                if (dropdown.captionText != null) text = Instantiate(dropdown.captionText, dropdown.transform);
                else
                {
                    var go = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
                    go.transform.SetParent(dropdown.transform, false); text = go.GetComponent<TMP_Text>();
                }
                text.name = "Placeholder"; text.raycastTarget = false;
                text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 20;
                var rect = text.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(12, 0); rect.offsetMax = new Vector2(-38, 0);
                dropdown.placeholder = text;
            }
            if (dropdown.placeholder is TMP_Text placeholder) placeholder.text = caption;
        }

        private TMP_Text SectionTitle(Transform parent, string name)
        {
            // A section is a fresh label, not a clone of the live Footer status renderer.
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var title = go.GetComponent<TMP_Text>(); title.font = statusText.font; title.color = statusText.color;
            title.raycastTarget = false; title.textWrappingMode = TextWrappingModes.Normal;
            CompactSectionTitle(title); title.fontStyle = FontStyles.Bold;
            return title;
        }

        private static void FullWidthAction(Button button)
        {
            button.transform.parent.Find("Label")?.gameObject.SetActive(false);
            var rect = button.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12, 8); rect.offsetMax = new Vector2(-12, -8);
        }

        private void ArrangeAiSettingGroups()
        {
            if (mainConnectionGroup != null) return;
            var root = configurationContent.transform;
            mainConnectionGroup = SettingsContent("MainConnectionGroup", root).transform;
            mainSectionTitle = SectionTitle(mainConnectionGroup, "MainConnectionTitle");
            foreach (Component field in new Component[] { mainSupplier, mainBaseUrl, mainApiKey, mainModel })
                field.transform.parent.SetParent(mainConnectionGroup, false);
            // This added row must use the same label metrics as the existing main fields.
            var supplierLabel = mainSupplier.transform.parent.Find("Label").GetComponent<TMP_Text>();
            var modelLabel = mainModel.transform.parent.Find("Label").GetComponent<TMP_Text>();
            supplierLabel.fontSize = modelLabel.fontSize; supplierLabel.enableAutoSizing = modelLabel.enableAutoSizing;
            supplierLabel.fontSizeMin = modelLabel.fontSizeMin; supplierLabel.fontSizeMax = modelLabel.fontSizeMax;
            supplierLabel.alignment = modelLabel.alignment;
            var labelRect = supplierLabel.rectTransform; var referenceRect = modelLabel.rectTransform;
            labelRect.anchorMin = referenceRect.anchorMin; labelRect.anchorMax = referenceRect.anchorMax;
            labelRect.offsetMin = referenceRect.offsetMin; labelRect.offsetMax = referenceRect.offsetMax;
            discoverMainButton.transform.parent.SetParent(mainConnectionGroup, false);
            if (testMainButton.transform.parent != discoverMainButton.transform.parent)
                testMainButton.transform.parent.SetParent(mainConnectionGroup, false);

            operationFeedback = AddState(mainConnectionGroup, "OperationFeedback");
            operationFeedback.GetComponent<LayoutElement>().preferredHeight = -1;
            foreach (var text in new[] { operationFeedback })
            {
                text.fontSize = 21; text.enableAutoSizing = false; text.alignment = TextAlignmentOptions.TopLeft;
                text.textWrappingMode = TextWrappingModes.Normal; text.overflowMode = TextOverflowModes.Overflow;
                var size = text.GetComponent<LayoutElement>(); size.minHeight = 28; size.preferredHeight = -1;
            }
            operationFeedback.textWrappingMode = TextWrappingModes.Normal;
            operationFeedback.gameObject.SetActive(false);
            recentDialogueGroup = SettingsContent("RecentDialogueGroup", advancedSettings.transform).transform;
            foreach (Component control in new Component[] { recentTitle, recentMode, recentTarget, recentState, recentHelp })
                (control is TMP_InputField || control is TMP_Dropdown ? control.transform.parent : control.transform).SetParent(recentDialogueGroup, false);
            vectorRetrievalGroup = SettingsContent("VectorRetrievalGroup", root).transform;
            embeddingSectionTitle = SectionTitle(vectorRetrievalGroup, "EmbeddingSectionTitle");
            embeddingPurpose = AddState(vectorRetrievalGroup, "EmbeddingPurpose");
            embeddingPurpose.GetComponent<LayoutElement>().preferredHeight = -1;
            embeddingPurpose.GetComponent<LayoutElement>().minHeight = 28;
            embeddingCategories[0].transform.parent.SetParent(vectorRetrievalGroup, false);
            embeddingBrief = AddState(vectorRetrievalGroup,"EmbeddingBrief");
            embeddingBrief.GetComponent<LayoutElement>().preferredHeight = -1;
            embeddingBrief.GetComponent<LayoutElement>().minHeight = 28;
            embeddingFields.transform.SetParent(vectorRetrievalGroup, false);
            EnsureOrdinaryIndexEntry(vectorRetrievalGroup);
            foreach (Component control in new Component[] { embeddingTimeout, embeddingDimensions, embeddingVersion })
                control.transform.parent.SetParent(embeddingFields.transform, false);
            embeddingEnabled.transform.parent.SetParent(vectorRetrievalGroup, false);
            embeddingEnabled.transform.parent.gameObject.SetActive(false);

            aiStateGroup = SettingsContent("AiStateGroup", root).transform;
            aiStateTitle = SectionTitle(aiStateGroup, "AiStateTitle");
            mainConnectionState.transform.SetParent(aiStateGroup, false);
            advancedPurpose = AddState(advancedSettings.transform, "AdvancedPurpose"); advancedPurpose.transform.SetAsFirstSibling();
            advancedPurpose.GetComponent<LayoutElement>().minHeight = 28; advancedPurpose.GetComponent<LayoutElement>().preferredHeight = -1;
            mainCapacityState = AddState(advancedSettings.transform, "MainCapacityState");
            mainCapacityState.GetComponent<LayoutElement>().preferredHeight = -1;
            mainCapacityState.GetComponent<LayoutElement>().minHeight = 28;
            developerDiagnostics.SetParent(root, false);
            embeddingModeInformation.transform.SetParent(developerDiagnostics,false);
            mainProfileState.transform.SetParent(developerDiagnostics, false);
            embeddingProfileState.transform.SetParent(developerDiagnostics, false);
            recentHelp.transform.SetParent(recentDialogueGroup, false);
            configurationDiagnostics = AddState(developerDiagnostics, "ConfigurationDiagnostics");
            configurationDiagnostics.GetComponent<LayoutElement>().preferredHeight = -1;
            configurationDiagnostics.GetComponent<LayoutElement>().minHeight = 28;
            var obsolete = advancedSettings.transform.Find("ConfigurationDetailsTitle");
            if (obsolete != null) obsolete.gameObject.SetActive(false);
            diagnosticsToggle = AddAction(root, "AiDiagnosticsToggle", "诊断信息 +", "Diagnostics +", () => {
                if (BlocksSettingsNavigation) return;
                developerDiagnostics.gameObject.SetActive(!developerDiagnostics.gameObject.activeSelf);
                RefreshAiSectionLanguage(); RebuildSettingsLayout(); });
            FullWidthAction(diagnosticsToggle); developerDiagnostics.gameObject.SetActive(false);
            // Preserve the legacy Editor-only tool in diagnostics. Player uses the C2 consent flow.
            restartBackendButton.gameObject.SetActive(Application.isEditor);

            // Keep existing labels from occupying a second title/section in the new ownership groups.
            foreach (var label in root.GetComponentsInChildren<SettingsLocalizedLabel>(true))
                if (label.gameObject.name is "Main LLM" or "Embedding" or "GenerationParameters" || label.Chinese == "生成参数")
                    label.gameObject.SetActive(false);
            int order = 0;
            foreach (Transform section in new[] { mainConnectionGroup, advancedToggle.transform.parent, advancedSettings.transform,
                vectorRetrievalGroup, aiStateGroup, diagnosticsToggle.transform.parent, developerDiagnostics })
                section.SetSiblingIndex(order++);
            foreach (var field in mainConnectionGroup.GetComponentsInChildren<TMP_InputField>(true)
                .Concat(advancedSettings.GetComponentsInChildren<TMP_InputField>(true)))
                if (field.textComponent != null) { field.textComponent.enableAutoSizing = true; field.textComponent.fontSizeMin = 14; field.textComponent.fontSizeMax = 21; }
            foreach (var field in root.GetComponentsInChildren<TMP_InputField>(true)) field.onValueChanged.AddListener(_ => DraftEdited());
            foreach (var field in root.GetComponentsInChildren<TMP_Dropdown>(true)) field.onValueChanged.AddListener(_ => DraftEdited());
            foreach (var field in root.GetComponentsInChildren<Toggle>(true)) field.onValueChanged.AddListener(_ => DraftEdited());
            foreach (var button in root.GetComponentsInChildren<Button>(true))
                if (button.name is "ClearMainKey" or "ClearEmbeddingKey") button.onClick.AddListener(DraftEdited);
            ArrangeAdvancedSettings();
            RefreshAiSectionLanguage();
        }

        private TMP_Text generationTitle, compatibilityTitle;
        private void ArrangeAdvancedSettings()
        {
            generationTitle = SectionTitle(advancedSettings.transform, "ModelGenerationTitle");
            compatibilityTitle = SectionTitle(advancedSettings.transform, "ApiCompatibilityTitle");
            int order = 0;
            generationTitle.transform.SetSiblingIndex(order++);
            foreach (Component c in new Component[] { contextLength, maxOutputTokens, temperature, mainTimeout, thinking })
                c.transform.parent.SetSiblingIndex(order++);
            mainCapacityState.transform.SetSiblingIndex(order++);
            recentDialogueGroup.SetSiblingIndex(order++);
            compatibilityTitle.transform.SetAsLastSibling();
            advancedPurpose.transform.SetAsLastSibling();
            foreach (Component c in new Component[] { schemaMode, tokenMode, thinkingMode, reasoningMode, healthMode, sendTemperature, topP, thinkingBudget })
                c.transform.parent.SetAsLastSibling();
        }

        private void RefreshAiSectionLanguage()
        {
            if (mainConnectionGroup == null) return;
            mainSectionTitle.text = SettingsUiLanguage.T("主模型连接", "Main model connection");
            embeddingSectionTitle.text = SettingsUiLanguage.T("记忆查找", "Memory search");
            embeddingPurpose.text = SettingsUiLanguage.T("帮助 NPC 找到与当前对话相关的已有记忆。Windows 试玩包已内置默认模型，推荐保持默认，无需反复配置。", "Helps NPCs find existing memories relevant to this conversation. The Windows preview includes the default model; keep the default without repeated setup.");
            if (generationTitle != null) generationTitle.text = SettingsUiLanguage.T("模型生成参数", "Model generation");
            if (compatibilityTitle != null) compatibilityTitle.text = SettingsUiLanguage.T("接口兼容性设置", "API compatibility");
            advancedPurpose.text = SettingsUiLanguage.T("通常保持默认。接口兼容性选“自动”时，可在确认费用的测试中适配；手动选择会优先使用你的设置。", "Keep the defaults in most cases. Automatic API settings may adapt during a confirmed paid test; manual choices take priority.");
            foreach (var text in new[] { embeddingPurpose, embeddingBrief, mainConnectionState, configurationDiagnostics, mainCapacityState, recentHelp, advancedPurpose })
                if (text != null) { text.fontSize = 21; text.enableAutoSizing = false; }
            RefreshOperationFeedback();
            aiStateTitle.text = SettingsUiLanguage.T("配置状态与保存", "Configuration status and saving");
            SetRow(mainBaseUrl, "主模型 API 地址", "Main model API URL");
            SetRow(contextLength, "主模型上下文长度", "Main model context length");
            if (discoverMainButton.transform.parent == testMainButton.transform.parent)
                SetRow(discoverMainButton, "主模型操作", "Main model actions");
            if (discoverEmbeddingButton.transform.parent == testEmbeddingButton.transform.parent)
                SetRow(discoverEmbeddingButton, "Embedding 操作", "Embedding actions");
            if (mainChoices != null)
            {
                mainSupplier.options = mainChoices.Select(p => new TMP_Dropdown.OptionData(p.preset_id == "custom"
                    ? SettingsUiLanguage.T("自定义（OpenAI 兼容接口）", "Custom OpenAI-compatible") : ProviderCaption(p))).ToList();
                mainSupplier.RefreshShownValue();
            }
            SetButton(recheckButton, "刷新配置 / 运行状态", "Refresh configuration / runtime status");
            SetButton(restartBackendButton, "Editor 本地后端重启", "Editor local Backend restart");
            SetButton(diagnosticsToggle, developerDiagnostics.gameObject.activeSelf ? "诊断信息 −" : "诊断信息 +",
                developerDiagnostics.gameObject.activeSelf ? "Diagnostics −" : "Diagnostics +");
        }

        private void RefreshBriefAiState()
        {
            RefreshCandidateControls();
            RefreshEmbeddingModeInformation();
            if (mainCapacityState == null || profilesView == null) return;
            bool dirty = mainChoices != null && embeddingChoices != null && (HasUnsavedSettings() || ContextPolicyDirty());
            string state = dirty ? SettingsUiLanguage.T("未保存修改", "Unsaved changes") : profilesView.restart_required
                ? SettingsUiLanguage.T("重启后生效", "Applies after restart") : AppliedSnapshotMatches(profilesView.selected_to_activate, profilesView.active_runtime)
                ? SettingsUiLanguage.T("运行配置已应用", "Runtime configuration applied") : profilesView.configured ? SettingsUiLanguage.T("待应用", "Pending application") : SettingsUiLanguage.T("尚未配置", "Not configured");
            RefreshOperationFeedback();
            string connection = verifiedMainSignature == ProbeSignature(false) && verifiedMainSuccess.HasValue
                ? verifiedMainSuccess.Value ? SettingsUiLanguage.T("简短连接测试已通过（当前草稿）", "Short connection test passed (current draft)")
                    : SettingsUiLanguage.T("连接测试失败：", "Connection test failed: ") + SettingsUiLanguage.T(P0FeedbackText.ModelError(verifiedMainError).Chinese, P0FeedbackText.ModelError(verifiedMainError).English)
                : SettingsUiLanguage.T("当前草稿连接尚未验证", "Connection for this draft is unverified");
            mainConnectionState.text = SettingsUiLanguage.T("当前运行主模型：", "Current main model: ") +
                (profilesView.active_runtime?.main_model?.model ?? "—") + "\n" + state + "\n" + connection;
            if (checkedRuntimeStatus?.active_provider != null)
            {
                bool available = checkedRuntimeStatus.active_provider.available;
                var reason = P0FeedbackText.ModelError(checkedRuntimeStatus.active_provider.detail);
                mainConnectionState.text += "\n" + SettingsUiLanguage.T("运行服务检查：", "Runtime service check: ") +
                    (available ? SettingsUiLanguage.T("可用（不代表完整 NPC 兼容）", "Available (full NPC compatibility unverified)") : SettingsUiLanguage.T("失败：", "Failed: ") + SettingsUiLanguage.T(reason.Chinese, reason.English));
            }
            else if (runtimeReadError != null) mainConnectionState.text += "\n" + SettingsUiLanguage.T("运行服务状态未确认，请刷新。", "Runtime service status is unconfirmed; refresh to check.");
            configurationDiagnostics.text = SettingsUiLanguage.T("配置版本号：", "Configuration revision: ") + profilesView.revision +
                SettingsUiLanguage.T(" · 需要处理旧版配置：", " · Legacy configuration requires processing: ") + (profilesView.migration_required ? SettingsUiLanguage.T("是", "Yes") : SettingsUiLanguage.T("否", "No")) +
                (profilesView.migration_notes?.Length > 0 ? "\n" + string.Join("\n", profilesView.migration_notes.Select(note => note switch { "V1_CONNECTIONS_FROZEN" => SettingsUiLanguage.T("旧版连接已按原有效地址与密钥保留", "Legacy connections keep their effective URL and key") + " (" + note + ")", "LEGACY_INDEX_IDENTITY_UNVERIFIED" => SettingsUiLanguage.T("旧版查找数据与当前模型的兼容性未确认", "Compatibility of legacy search data with this model is unconfirmed") + " (" + note + ")", _ => note })) : "");
            int activeCapacity = profilesView.active_runtime?.main_model?.context_length ?? 0;
            if (profilesView.active_runtime?.main_model != null && activeCapacity < 16384)
                mainConnectionState.text += SettingsUiLanguage.T(" · 当前运行容量低于建议的16K", " · Active capacity is below the recommended 16K");
        }
    }
}
