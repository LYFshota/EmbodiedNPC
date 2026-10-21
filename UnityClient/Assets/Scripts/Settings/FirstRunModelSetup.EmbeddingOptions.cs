using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        private Toggle[] embeddingCategories;
        private TMP_Text embeddingModeInformation;
        private Button restoreDefaultsButton;
        private ModelPresetView[] embeddingCloudChoices;
        private string embeddingEditingPresetId, lastEmbeddingCloudPreset, initialMainPresetId, initialEmbeddingPresetId;
        private bool loadingDefaults, defaultsRestored;
        private string resetMainProfileId;
        private int resetMainRevision;
        // Session drafts only. Credentials are never serialized to assets, logs or disk.
        private sealed class EmbeddingDraft
        {
            public string id, baseline, signature, url, model, key, timeout, dimensions, version;
            public bool keyPresent, clear;
            public string[] candidates;
            public int candidate;
        }
        private readonly Dictionary<string, EmbeddingDraft> embeddingDrafts = new();

        private ModelPresetView EditingEmbeddingPreset() => embeddingChoices.First(p => p.preset_id == embeddingEditingPresetId);
        private static int EmbeddingCategory(ModelPresetView preset) => preset.protocol == "builtin" ? 0 : preset.protocol == "ollama" ? 1 : 2;
        // Main drafts/revisions do not establish an Embedding choice. Any saved
        // Embedding snapshot (including an explicit off) must take precedence.
        private bool IsNewEmbeddingConfiguration => !profilesView.migration_required &&
            profilesView.selected_to_activate?.embedding == null && profilesView.active_runtime?.embedding == null &&
            (profilesView.embedding_profiles == null || profilesView.embedding_profiles.Length == 0);

        private void ConfigureEmbeddingSuppliers(int source, string desired)
        {
            var choices = profilesView.embedding_presets.ToList();
            if (!choices.Any(p => p.preset_id == "builtin_gemma"))
                choices.Add(new ModelPresetView { preset_id = "builtin_gemma", protocol = "builtin", source = "local", discovery = "manual" });
            embeddingChoices = choices.ToArray();
            embeddingCloudChoices = embeddingChoices.Where(p => EmbeddingCategory(p) == 2).ToArray();
            embeddingEditingPresetId = desired ?? embeddingChoices.FirstOrDefault(p => p.source == (source == 1 ? "local" : "cloud"))?.preset_id;
            if (EmbeddingCategory(EditingEmbeddingPreset()) == 2) lastEmbeddingCloudPreset = embeddingEditingPresetId;
            SetOptions(embeddingSupplier, embeddingCloudChoices.Select(p => p.name).ToArray());
            embeddingSupplier.SetValueWithoutNotify(Math.Max(0, Array.FindIndex(embeddingCloudChoices, p => p.preset_id == embeddingEditingPresetId)));
        }

        private void EnsureEmbeddingCategories()
        {
            if (embeddingCategories != null) return;
            Transform parent = embeddingSupplier.transform.parent.parent;
            var root = new GameObject("EmbeddingProviderChoices", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ToggleGroup));
            root.transform.SetParent(parent, false);
            root.transform.SetSiblingIndex(embeddingSupplier.transform.parent.GetSiblingIndex());
            var stack = root.GetComponent<VerticalLayoutGroup>();
            stack.spacing = 7; stack.childControlWidth = stack.childControlHeight = true;
            stack.childForceExpandWidth = true; stack.childForceExpandHeight = false;
            var group = root.GetComponent<ToggleGroup>(); group.allowSwitchOff = false;
            var title = Instantiate(statusText, root.transform); title.name = "EmbeddingCategoryTitle";
            title.fontSize = 21; title.fontStyle = FontStyles.Bold; title.text = "";
            var titleSize = title.GetComponent<LayoutElement>() ?? title.gameObject.AddComponent<LayoutElement>();
            titleSize.preferredHeight = titleSize.minHeight = 28;
            embeddingCategories = new Toggle[4];
            for (int i = 0; i < embeddingCategories.Length; i++)
            {
                int category = i;
                var row = new GameObject("EmbeddingCategory" + i, typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Toggle));
                row.transform.SetParent(root.transform, false);
                row.GetComponent<LayoutElement>().preferredHeight = row.GetComponent<LayoutElement>().minHeight = 38;
                row.GetComponent<Image>().color = Color.clear;
                var toggle = row.GetComponent<Toggle>(); toggle.group = group;
                toggle.toggleTransition = Toggle.ToggleTransition.None;
                var ring = new GameObject("Ring", typeof(RectTransform), typeof(Image)); ring.transform.SetParent(row.transform, false);
                var rr = ring.GetComponent<RectTransform>(); rr.anchorMin = rr.anchorMax = new Vector2(0, .5f);
                rr.anchoredPosition = new Vector2(18, 0); rr.sizeDelta = new Vector2(24, 24);
                SettingsPanelTheme.Surface(ring.GetComponent<Image>(), SettingsPanelTheme.Ink); ring.GetComponent<Image>().raycastTarget = false;
                var center = new GameObject("Center", typeof(RectTransform), typeof(Image)); center.transform.SetParent(ring.transform, false);
                var cr = center.GetComponent<RectTransform>(); cr.anchorMin = cr.anchorMax = new Vector2(.5f, .5f); cr.sizeDelta = new Vector2(20, 20);
                SettingsPanelTheme.Surface(center.GetComponent<Image>(), SettingsPanelTheme.Paper); center.GetComponent<Image>().raycastTarget = false;
                var dot = new GameObject("Selected", typeof(RectTransform), typeof(Image)); dot.transform.SetParent(center.transform, false);
                var dr = dot.GetComponent<RectTransform>(); dr.anchorMin = dr.anchorMax = new Vector2(.5f, .5f); dr.sizeDelta = new Vector2(12, 12);
                SettingsPanelTheme.Surface(dot.GetComponent<Image>(), SettingsPanelTheme.Accent); dot.GetComponent<Image>().raycastTarget = false;
                toggle.graphic = dot.GetComponent<Image>(); toggle.targetGraphic = ring.GetComponent<Image>();
                var label = Instantiate(title, row.transform); label.name = "Label"; label.fontStyle = FontStyles.Normal;
                if (label.TryGetComponent<LayoutElement>(out var own)) own.ignoreLayout = true;
                label.enableAutoSizing = true; label.fontSizeMin = 14; label.fontSizeMax = 21;
                label.textWrappingMode = TextWrappingModes.NoWrap; label.raycastTarget = false;
                label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
                label.rectTransform.offsetMin = new Vector2(42, 0); label.rectTransform.offsetMax = new Vector2(-12, 0);
                embeddingCategories[i] = toggle;
                toggle.onValueChanged.AddListener(value => { if (value) SelectEmbeddingCategory(category); });
            }
            embeddingModeInformation = AddState(parent, "EmbeddingModeInformation");
            var informationSize = embeddingModeInformation.GetComponent<LayoutElement>();
            informationSize.minHeight = 56; informationSize.preferredHeight = -1;
            foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
                if (child.name == "Embedding" && child.GetComponent<TMP_Text>() != null) child.gameObject.SetActive(false);
        }

        private void SelectEmbeddingCategory(int category)
        {
            if (busy || profileSwitchPending || embeddingChoices == null) { RefreshEmbeddingCategoryLabels(); return; }
            if (category == 3)
            {
                embeddingEnabled.SetIsOnWithoutNotify(false);
                RefreshEmbeddingFields(); RebuildSettingsLayout();
                return;
            }
            string id = category == 0 ? "builtin_gemma" : category == 1 ? "ollama" :
                lastEmbeddingCloudPreset ?? embeddingCloudChoices.FirstOrDefault()?.preset_id;
            if (id != null && embeddingChoices.Any(p => p.preset_id == id))
            {
                embeddingEnabled.SetIsOnWithoutNotify(true);
                SwitchEmbeddingPreset(id);
                RefreshEmbeddingFields(); RebuildSettingsLayout();
            }
            else RefreshEmbeddingCategoryLabels();
        }

        private void RequestEmbeddingCloudSwitch(int selected)
        {
            if (embeddingCloudChoices != null && selected >= 0 && selected < embeddingCloudChoices.Length)
                SwitchEmbeddingPreset(embeddingCloudChoices[selected].preset_id);
        }

        private void SwitchEmbeddingPreset(string id)
        {
            if (busy || profileSwitchPending) { RefreshEmbeddingCategoryLabels(); return; }
            if (embeddingEditingPresetId == id) return;
            CaptureEmbeddingDraft();
            embeddingEditingPresetId = id;
            if (EmbeddingCategory(EditingEmbeddingPreset()) == 2) lastEmbeddingCloudPreset = id;
            LoadEditingProfile(true);
            RefreshEmbeddingCategoryLabels(); RebuildSettingsLayout();
        }

        private void CaptureEmbeddingDraft()
        {
            if (embeddingEditingPresetId == null) return;
            embeddingDrafts[embeddingEditingPresetId] = new EmbeddingDraft { id = embeddingEditingId, baseline = embeddingBaseline, signature = DraftSignature(true),
                url = embeddingBaseUrl.text, model = embeddingModel.text, key = embeddingApiKey.text, timeout = embeddingTimeout.text,
                dimensions = embeddingDimensions.text, version = embeddingVersion.text, keyPresent = embeddingKeyPresent, clear = embeddingClearKey,
                candidates = embeddingCandidates.options.Select(o => o.text).ToArray(), candidate = embeddingCandidates.value };
        }

        private bool RestoreEmbeddingDraft()
        {
            if (loadingDefaults || !embeddingDrafts.TryGetValue(embeddingEditingPresetId, out var d)) return false;
            embeddingEditingId = d.id; embeddingBaseline = d.baseline;
            embeddingBaseUrl.SetTextWithoutNotify(d.url); embeddingModel.SetTextWithoutNotify(d.model);
            embeddingApiKey.SetTextWithoutNotify(d.key); embeddingTimeout.SetTextWithoutNotify(d.timeout);
            embeddingDimensions.SetTextWithoutNotify(d.dimensions); embeddingVersion.SetTextWithoutNotify(d.version);
            embeddingKeyPresent = d.keyPresent; embeddingClearKey = d.clear;
            SetOptions(embeddingCandidates, d.candidates); embeddingCandidates.SetValueWithoutNotify(d.candidate);
            RefreshEmbeddingFields(); RefreshLanguage(); RefreshProfileStates(); return true;
        }

        private void RefreshEmbeddingCategoryLabels()
        {
            if (embeddingCategories == null || embeddingChoices == null) return;
            int category = embeddingEnabled.isOn ? EmbeddingCategory(EditingEmbeddingPreset()) : 3;
            string[] labels = { SettingsUiLanguage.T("内置模型（新配置默认） · EmbeddingGemma 300M Q4", "Built-in model (new configuration default) · EmbeddingGemma 300M Q4"),
                SettingsUiLanguage.T("本地 Ollama", "Local Ollama"), SettingsUiLanguage.T("云端 API", "Cloud API"),
                SettingsUiLanguage.T("关闭 Embedding", "Embedding off") };
            for (int i = 0; i < embeddingCategories.Length; i++)
            {
                embeddingCategories[i].SetIsOnWithoutNotify(i == category);
                embeddingCategories[i].interactable = !busy;
                embeddingCategories[i].GetComponentInChildren<TMP_Text>(true).text = labels[i];
            }
            embeddingCategories[0].transform.parent.Find("EmbeddingCategoryTitle").GetComponent<TMP_Text>().text =
                SettingsUiLanguage.T("Embedding 模式", "Embedding mode");
            embeddingSupplier.transform.parent.gameObject.SetActive(category == 2);
            SetRow(embeddingSupplier, "云端供应商", "Cloud provider");
            RefreshEmbeddingModeInformation();
        }

        private void RefreshEmbeddingModeInformation()
        {
            if (embeddingModeInformation == null || embeddingChoices == null || profilesView == null) return;
            string text;
            if (!embeddingEnabled.isOn)
                text = SettingsUiLanguage.T("当前草稿：关闭 Embedding 向量检索。历史记忆仍保留，其他记忆功能不因此全部关闭。",
                    "Draft: Embedding vector retrieval off. History is preserved; this does not disable all memory features.");
            else if (EditingEmbeddingPreset().protocol == "builtin")
            {
                var config = BuiltinEditingConfig();
                text = SettingsUiLanguage.T("内置模型：", "Built-in model: ") + (config?.model ?? SettingsUiLanguage.T("Backend 未提供", "not supplied by Backend"));
                text += "\n" + BuiltinSetupCaption(SettingsUiLanguage.IsEnglish);
                text += config == null ? SettingsUiLanguage.T("\n内置配置不可用，请更新 Backend。", "\nBuilt-in configuration unavailable; update the Backend.") :
                    (config.resource_status ?? profilesView.builtin_resource_status) == "missing" ? "\n" + BuiltinErrorText("BUILTIN_EMBEDDING_FILE_MISSING") :
                    (config.resource_status ?? profilesView.builtin_resource_status) == "unverified" ?
                    SettingsUiLanguage.T("\n资源文件已发现；未提供文件完整性验证结果。实际加载与索引状态见索引摘要。",
                        "\nResource files found; file integrity verification is not reported. See the index summary for runtime loading and indexing.") :
                    SettingsUiLanguage.T("\n资源状态未确认；加载与索引状态见记忆索引摘要。选择内置不代表已就绪。",
                        "\nResource status unconfirmed; see the memory index summary for loading/index status. Selection does not mean ready.");
            }
            else text = SettingsUiLanguage.T("当前草稿：", "Draft: ") +
                (EditingEmbeddingPreset().protocol == "ollama" ? SettingsUiLanguage.T("本地 Ollama", "Local Ollama") : SettingsUiLanguage.T("云端 API", "Cloud API"));
            if (profilesView.migration_notes?.Contains("V1_CONNECTIONS_FROZEN") == true)
                text += SettingsUiLanguage.T("\n旧版连接（含原复用配置）以独立的有效地址和密钥显示；保存沿用现有迁移流程，不随主模型草稿自动改变。",
                    "\nLegacy connections, including reuse, are projected as independent effective URLs/keys. Saving follows the existing migration; main drafts do not change them.");
            if (profilesView.migration_notes?.Contains("LEGACY_INDEX_IDENTITY_UNVERIFIED") == true)
                text += SettingsUiLanguage.T("\n旧索引身份尚未验证，不能认定与当前模型兼容；历史记忆保留，请通过开发者工具检查。",
                    "\nLegacy index identity is unverified; compatibility with this model is unconfirmed. History is preserved; review in Developer tools.");
            text += SettingsUiLanguage.T("\n来源选择只修改草稿；需保存并应用。运行和索引状态见索引摘要，操作位于 AI 配置的高级维护区域。",
                "\nSelection edits a draft; Save & Apply is required. See the index summary; controls are in AI advanced maintenance.");
            embeddingModeInformation.text = text;
            if (embeddingBrief != null)
            {
                bool dirty = embeddingEditingPresetId != initialEmbeddingPresetId || DraftSignature(true) != embeddingBaseline;
                string application = dirty ? SettingsUiLanguage.T("未保存修改", "Unsaved changes") :
                    IsNewEmbeddingConfiguration ? SettingsUiLanguage.T("尚未保存（默认启用草稿）", "Default enabled draft; not saved") :
                    EmbeddingSelectionNeedsRestart() ? SettingsUiLanguage.T("已保存，待重启应用", "Saved; restart to apply") :
                    currentIndex == null ? SettingsUiLanguage.T("应用状态未确认", "Application status unconfirmed") :
                    currentIndex.provider_status == "failed" || profilesView.active_runtime?.embedding?.resource_status == "missing"
                        ? SettingsUiLanguage.T("已保存，模型不可用", "Saved; model unavailable") :
                    currentIndex.applied ? SettingsUiLanguage.T("已应用", "Applied") :
                    profilesView.selected_to_activate == null ? SettingsUiLanguage.T("尚未配置", "Not configured") :
                    SettingsUiLanguage.T("已保存，待重启应用", "Saved; restart to apply");
                embeddingBrief.text = SettingsUiLanguage.T("Embedding 配置：", "Embedding configuration: ") + application;
                if (!embeddingEnabled.isOn) embeddingBrief.text += SettingsUiLanguage.T(" · 关闭向量检索", " · Vector retrieval off");
                else if (EditingEmbeddingPreset().protocol == "builtin" &&
                    (BuiltinEditingConfig()?.resource_status ?? profilesView.builtin_resource_status) == "missing")
                    embeddingBrief.text += SettingsUiLanguage.T("\n内置资源缺失，请检查安装。", "\nBuilt-in resources missing; check installation.");
            }
        }

        // Presentation only: saved selection, applied snapshot and provider loading remain separate authorities.
        private string BuiltinSetupCaption(bool english)
        {
            string T(string zh, string en) => english ? en : zh;
            if (!embeddingEnabled.isOn || EditingEmbeddingPreset().protocol != "builtin") return "";
            if ((BuiltinEditingConfig()?.resource_status ?? profilesView.builtin_resource_status) == "missing")
                return T("内置资源缺失；请检查安装后再保存应用。", "Built-in resources are missing. Check the installation before saving and applying.");
            if (IsNewEmbeddingConfiguration)
                return profilesView.configured || profilesView.main_profiles?.Length > 0
                    ? T("内置向量模型已默认选择。下次保存主模型配置时会一并启用，之后无需重复设置。",
                        "The built-in embedding model is selected by default. Your next main-model save also enables it; no repeated setup is needed afterward.")
                    : T("内置向量模型已默认选择。首次保存主模型配置时会一并启用，之后无需重复设置。",
                    "The built-in embedding model is selected by default. Your first main-model save also enables it; no repeated setup is needed afterward.");
            if (embeddingEditingPresetId != initialEmbeddingPresetId || DraftSignature(true) != embeddingBaseline)
                return T("当前选择尚未保存；保存并应用后才会使用内置向量模型。", "This selection is not saved yet. Save and apply it to use the built-in embedding model.");
            if (EmbeddingSelectionNeedsRestart() || currentIndex?.status == "restart_required")
                return T("内置向量配置已保存，等待按当前提示重启以应用；之后无需重复设置。",
                    "The built-in embedding configuration is saved. Follow the restart prompt to apply it; no repeated setup is needed afterward.");
            if (profilesView.active_runtime?.embedding?.provider_type == "builtin" &&
                profilesView.active_runtime.embedding.enabled && currentIndex?.applied == true)
                return currentIndex.provider_status switch
                {
                    "ready" => T("内置向量模型已加载，后续启动会沿用此配置，无需重复设置。", "The built-in embedding model is loaded. Future launches reuse this configuration; no repeated setup is needed."),
                    "unloaded" => T("内置向量配置已启用；模型尚未加载。", "The built-in embedding configuration is enabled; the model is not loaded yet."),
                    "failed" => T("内置向量配置已启用，但模型加载或推理失败；请查看下方错误说明。", "The built-in embedding configuration is enabled, but model loading or inference failed. See the error details below."),
                    _ => T("内置向量配置已启用；模型加载状态尚未确认。", "The built-in embedding configuration is enabled; model loading status is unconfirmed.")
                };
            return T("内置向量配置的应用状态尚未确认，请查看当前运行摘要。", "Application of the built-in embedding configuration is unconfirmed. Check the runtime summary.");
        }

        private void EnsureRestoreDefaultsButton()
        {
            if (restoreDefaultsButton != null) return;
            restoreDefaultsButton = Instantiate(saveButton, saveButton.transform.parent);
            restoreDefaultsButton.name = "RestoreDefaultsButton";
            restoreDefaultsButton.onClick = new Button.ButtonClickedEvent();
            restoreDefaultsButton.onClick.AddListener(RestoreDefaults);
            restoreDefaultsButton.transform.SetSiblingIndex(saveButton.transform.GetSiblingIndex());
            foreach (var button in new[] { restoreDefaultsButton, saveButton })
            {
                var size = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
                size.ignoreLayout = false; size.flexibleWidth = button == saveButton ? 1.5f : 1;
                size.preferredWidth = button == saveButton ? 260 : 185;
                var text = button.GetComponentInChildren<TMP_Text>(true);
                text.enableAutoSizing = true; text.fontSizeMin = 12; text.fontSizeMax = 20;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                SettingsPanelTheme.Button(button, button == saveButton);
            }
            var footer = saveButton.transform.parent.GetComponent<RectTransform>();
            if (footer.name == "FooterActions")
            {
                footer.anchorMin = footer.anchorMax = Vector2.right;
                if (statusText.transform.parent == footer.parent)
                {
                    // The shared shell reserves a separate row above these actions.
                    // The former side-by-side 38% allocation clipped readable feedback at 720p.
                    var rect=statusText.rectTransform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;
                    rect.offsetMin=new Vector2(24,78);rect.offsetMax=new Vector2(-24,-10);
                }
            }
        }

        private bool HasUnsavedSettings() => defaultsRestored || CurrentPreset(false).preset_id != initialMainPresetId ||
            embeddingEditingPresetId != initialEmbeddingPresetId || DraftSignature(false) != mainBaseline || DraftSignature(true) != embeddingBaseline ||
            embeddingDrafts.Any(pair => pair.Value.signature != pair.Value.baseline);

        private void RestoreDefaults()
        {
            if (busy || profileSwitchPending || profilesView == null) return;
            if (defaultsRestored) { Begin(LoadProfiles(false)); return; }
            profileSwitchPending = true;
            ShowProfileModal("恢复主模型默认设置？\n\n保存后将清除当前主模型配置及其 API 密钥。其他已保存的模型和 Embedding 配置会保留。\n\n保存成功后需要重启才能生效。",
                "Restore main-model defaults?\n\nSaving clears the current main-model profile and its API key. Other saved model and Embedding profiles are retained.\n\nRestart after saving to apply.", new[] {
                    ("确认恢复", "Confirm reset", (Action)(() => { profileSwitchPending = false; RestoreDefaultDrafts(); })),
                    ("取消", "Cancel", (Action)(() => profileSwitchPending = false)) });
        }

        private void RestoreDefaultDrafts()
        {
            resetMainProfileId = profilesView.selected_to_activate?.main_profile_id ??
                profilesView.main_profiles?.FirstOrDefault(p => p.profile_id == mainEditingId)?.profile_id;
            resetMainRevision = revision;
            embeddingDrafts.Clear(); lastEmbeddingCloudPreset = null;
            loadingDefaults = true; defaultsRestored = true;
            try
            {
                LoadSupplierOptions(false, 0, null); LoadEditingProfile(false);
                mainApiKey.SetTextWithoutNotify(""); mainKeyPresent = false; mainClearKey = true;
                loadingDefaults = false;
                ChooseInitial(true);
                if (advancedSettings != null) advancedSettings.SetActive(false);
            }
            finally { loadingDefaults = false; }
            SetStatus("主模型待重置，密钥尚未删除。保存并应用后清除，重启后生效。",
                "Main reset pending; key not yet deleted. Save & Apply to clear, then restart.");
            RefreshProfileLanguage(); RebuildSettingsLayout();
        }

        private void RefreshResetDraftLock()
        {
            foreach (var group in new[] { mainConnectionGroup, advancedSettings == null ? null : advancedSettings.transform, vectorRetrievalGroup })
            {
                if (group == null) continue;
                var canvas = group.GetComponent<CanvasGroup>();
                if (canvas == null) canvas = group.gameObject.AddComponent<CanvasGroup>();
                canvas.interactable = !defaultsRestored;
            }
        }

        private void RebuildSettingsLayout()
        {
            var scroll = panel?.GetComponentInChildren<ScrollRect>(true);
            if (scroll != null) LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
        }
    }
}
