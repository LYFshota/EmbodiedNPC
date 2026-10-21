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

            recentDialogueGroup = SettingsContent("RecentDialogueGroup", root).transform;
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
            mainCapacityState = AddState(advancedSettings.transform, "MainCapacityState");
            mainCapacityState.GetComponent<LayoutElement>().preferredHeight = -1;
            mainCapacityState.GetComponent<LayoutElement>().minHeight = 28;
            developerDiagnostics.SetParent(root, false);
            embeddingModeInformation.transform.SetParent(developerDiagnostics,false);
            mainProfileState.transform.SetParent(developerDiagnostics, false);
            embeddingProfileState.transform.SetParent(developerDiagnostics, false);
            recentHelp.transform.SetParent(developerDiagnostics, false);
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
                recentDialogueGroup, vectorRetrievalGroup, aiStateGroup, diagnosticsToggle.transform.parent, developerDiagnostics })
                section.SetSiblingIndex(order++);
            foreach (var field in mainConnectionGroup.GetComponentsInChildren<TMP_InputField>(true)
                .Concat(advancedSettings.GetComponentsInChildren<TMP_InputField>(true)))
                if (field.textComponent != null) { field.textComponent.enableAutoSizing = true; field.textComponent.fontSizeMin = 14; field.textComponent.fontSizeMax = 21; }
            foreach (var field in root.GetComponentsInChildren<TMP_InputField>(true)) field.onValueChanged.AddListener(_ => RefreshBriefAiState());
            foreach (var field in root.GetComponentsInChildren<TMP_Dropdown>(true)) field.onValueChanged.AddListener(_ => RefreshBriefAiState());
            foreach (var field in root.GetComponentsInChildren<Toggle>(true)) field.onValueChanged.AddListener(_ => RefreshBriefAiState());
            foreach (var button in root.GetComponentsInChildren<Button>(true))
                if (button.name is "ClearMainKey" or "ClearEmbeddingKey") button.onClick.AddListener(RefreshBriefAiState);
            RefreshAiSectionLanguage();
        }

        private void RefreshAiSectionLanguage()
        {
            if (mainConnectionGroup == null) return;
            mainSectionTitle.text = SettingsUiLanguage.T("主模型连接", "Main model connection");
            embeddingSectionTitle.text = SettingsUiLanguage.T("向量检索（Embedding）", "Vector retrieval (Embedding)");
            embeddingPurpose.text = SettingsUiLanguage.T("用于辅助 NPC 检索相关记忆。", "Helps NPCs retrieve relevant memories.");
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
                    ? SettingsUiLanguage.T("玩家自定义", "Custom OpenAI-compatible") : ProviderCaption(p))).ToList();
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
                ? SettingsUiLanguage.T("已生效", "Applied") : profilesView.configured ? SettingsUiLanguage.T("待应用", "Pending application") : SettingsUiLanguage.T("尚未配置", "Not configured");
            mainConnectionState.text = SettingsUiLanguage.T("当前运行主模型：", "Current main model: ") +
                (profilesView.active_runtime?.main_model?.model ?? "—") + "\n" + state;
            configurationDiagnostics.text = "Revision: " + profilesView.revision +
                SettingsUiLanguage.T(" · 需要迁移：", " · Migration required: ") + profilesView.migration_required +
                (profilesView.migration_notes?.Length > 0 ? "\n" + string.Join("\n", profilesView.migration_notes) : "");
            int activeCapacity = profilesView.active_runtime?.main_model?.context_length ?? 0;
            if (profilesView.active_runtime?.main_model != null && activeCapacity < 16384)
                mainConnectionState.text += SettingsUiLanguage.T(" · 当前运行容量低于建议的16K", " · Active capacity is below the recommended 16K");
        }
    }
}
