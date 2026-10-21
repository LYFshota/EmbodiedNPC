using System;
using System.Collections;
using System.Globalization;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        private GameObject memoryIndexSection;
        private Slider indexProgress;
        private TMP_Text indexTitle;
        private TMP_Text ordinaryIndexState;
        private Button ordinaryIndexAction;

        private void EnsureOrdinaryIndexEntry(Transform parent)
        {
            if (ordinaryIndexState != null) return;
            ordinaryIndexState = AddState(parent, "OrdinaryMemoryIndex");
            var size = ordinaryIndexState.GetComponent<LayoutElement>();
            size.minHeight = 28; size.preferredHeight = -1;
            ordinaryIndexAction = AddAction(parent, "OrdinaryIndexMaintenance", "检查并维护索引", "Check & maintain index", () =>
            {
                // The original command owns Active, busy and cloud-consent checks.
                if (indexAction.interactable) ControlIndex();
                else StartCoroutine(CheckIndexStatus());
            });
            FullWidthAction(ordinaryIndexAction);
        }
        private IEnumerator CheckIndexStatus()
        {
            ordinaryIndexAction.interactable = false;
            yield return client.GetEmbeddingIndex((value, error) => {
                indexReadError = error; currentIndex = error == null ? value : null; RefreshIndexState();
            });
        }

        private void EnsureMemoryIndexSection(Transform content)
        {
            if (memoryIndexSection != null) return;
            memoryIndexSection = new GameObject("MemoryIndex", typeof(RectTransform), typeof(VerticalLayoutGroup));
            memoryIndexSection.transform.SetParent(content, false);
            var stack = memoryIndexSection.GetComponent<VerticalLayoutGroup>();
            stack.spacing = 8; stack.childControlHeight = stack.childControlWidth = true;
            stack.childForceExpandHeight = false;
            indexTitle = AddState(memoryIndexSection.transform, "MemoryIndexTitle"); indexTitle.fontStyle = FontStyles.Bold;
            CompactSectionTitle(indexTitle);
            indexState.transform.SetParent(memoryIndexSection.transform, false);
            var stateSize = indexState.GetComponent<LayoutElement>();
            stateSize.minHeight = 28; stateSize.preferredHeight = -1;
            var bar = new GameObject("IndexProgress", typeof(RectTransform), typeof(LayoutElement), typeof(Image), typeof(Slider));
            bar.transform.SetParent(memoryIndexSection.transform, false);
            bar.GetComponent<LayoutElement>().preferredHeight = 10;
            SettingsPanelTheme.Surface(bar.GetComponent<Image>(), SettingsPanelTheme.Row);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(bar.transform, false);
            var rect = fill.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.sizeDelta = Vector2.zero;
            SettingsPanelTheme.Surface(fill.GetComponent<Image>(), SettingsPanelTheme.Accent);
            indexProgress = bar.GetComponent<Slider>(); indexProgress.fillRect = rect;
            indexProgress.minValue = 0; indexProgress.maxValue = 100; indexProgress.interactable = false;
            indexAction.transform.parent.SetParent(memoryIndexSection.transform, false);
            indexAction.transform.parent.Find("Label").gameObject.SetActive(false);
            var buttonRect = indexAction.GetComponent<RectTransform>();
            buttonRect.anchorMin = Vector2.zero; buttonRect.anchorMax = Vector2.one;
            buttonRect.offsetMin = new Vector2(20, 8); buttonRect.offsetMax = new Vector2(-20, -8);
            memoryIndexSection.transform.SetSiblingIndex(embeddingFields.transform.GetSiblingIndex());
            RefreshIndexState();
        }

        private static void CompactSectionTitle(TMP_Text title)
        {
            title.fontSize = 20; title.alignment = TextAlignmentOptions.MidlineLeft;
            var size = title.GetComponent<LayoutElement>(); size.preferredHeight = size.minHeight = 28;
        }

        private void RefreshMemoryIndex()
        {
            if (indexState == null) return;
            RefreshBuiltinState();
            RefreshEmbeddingModeInformation();
            if (indexTitle != null) indexTitle.text = SettingsUiLanguage.T("记忆索引", "Memory index");
            indexState.enableAutoSizing = false; indexState.fontSize = 21; indexState.color = SettingsPanelTheme.Ink;
            bool known = currentIndex != null && currentIndex.counts_known != false;
            string state = currentIndex?.status ?? "unknown";
            bool supportedState = state is "waiting" or "running" or "paused" or "failed" or "completed" or "restart_required" or "disabled" or "legacy_auto";
            if (!supportedState) state = "unknown";
            string summary = currentIndex == null ? SettingsUiLanguage.T("无法取得索引状态。", "Index status unavailable.") :
                SettingsUiLanguage.T("当前运行：", "Runtime: ") + (currentIndex.model ?? "-");
            if (profilesView?.active_runtime?.embedding?.provider_type == "builtin" && state != "disabled")
                summary += "\n" + SettingsUiLanguage.T("模型加载：", "Model loading: ") + EmbeddingLoadCaption();
            if (currentIndex != null && !(known && currentIndex.total == 0 && state == "completed"))
                summary += "\n" + SettingsUiLanguage.T("索引：", "Index: ") + IndexStateCaption(state);
            if (currentIndex != null && state != "disabled" && (!known || currentIndex.total > 0))
                summary += known ? " · " + currentIndex.completed + " / " + currentIndex.total + " (" +
                    currentIndex.progress.ToString("0.0", CultureInfo.InvariantCulture) + "%)" +
                    SettingsUiLanguage.T(" · 待处理 ", " · pending ") + currentIndex.pending : SettingsUiLanguage.T(" · 进度未知", " · progress unknown");
            if (known && currentIndex.total == 0 && state != "disabled")
                summary += SettingsUiLanguage.T("\n索引：当前无可索引数据；尚未验证召回效果。", "\nIndex: no indexable data; retrieval quality has not been verified.");
            if (currentIndex?.in_flight > 0) summary += SettingsUiLanguage.T(" · 在途请求结算中", " · settling in-flight requests");
            string error = indexOperationError ?? currentIndex?.error_code ?? currentIndex?.provider_error_code ?? indexReadError;
            if (error != null) summary += "\n" + IndexErrorText(error);
            if (embeddingChoices != null && CurrentPreset(true).protocol == "builtin")
            {
                if (BuiltinEditingConfig() == null)
                    summary += SettingsUiLanguage.T("\nBackend 尚未提供内置配置，请更新后端。", "\nBuilt-in configuration unavailable; update the Backend.");
                else if ((BuiltinEditingConfig()?.resource_status ?? profilesView.builtin_resource_status) == "missing" &&
                    error != "BUILTIN_EMBEDDING_FILE_MISSING") summary += "\n" + BuiltinErrorText("BUILTIN_EMBEDDING_FILE_MISSING");
            }
            if ((state is "waiting" or "running" or "paused" or "failed") && (!known || currentIndex.pending > 0))
                summary += "\n" + IncompleteIndexText();
            indexState.text = summary;
            bool canOperate = currentIndex != null && currentIndex.applied && currentIndex.control_allowed != false &&
                currentIndex.error_code != "EMBEDDING_CONTROL_STATE_INVALID" &&
                (state is "waiting" or "running" or "paused" or "failed") && (!known || currentIndex.total > 0);
            indexAction.interactable = !busy && canOperate;
            indexAction.transform.parent.gameObject.SetActive(canOperate);
            string zh = state == "running" ? "暂停构建" : state == "waiting" ? "开始构建" : "继续构建";
            string en = state == "running" ? "Pause indexing" : state == "waiting" ? "Start indexing" : "Resume indexing";
            SetRow(indexAction, zh, en); SetButton(indexAction, zh, en);
            if (indexProgress != null)
            {
                indexProgress.gameObject.SetActive(known && currentIndex.total > 0 && state is not "disabled" and not "unknown");
                indexProgress.SetValueWithoutNotify(currentIndex == null ? 0 : currentIndex.progress);
            }
            if (ordinaryIndexState != null)
            {
                ordinaryIndexState.fontSize = 21; ordinaryIndexState.color = SettingsPanelTheme.Ink;
                ordinaryIndexState.text = SettingsUiLanguage.T("记忆索引维护\n", "Memory Index Maintenance\n") + summary;
                if (state == "disabled") ordinaryIndexState.text += profilesView?.active_runtime?.embedding == null
                    ? profilesView != null && embeddingChoices != null && embeddingEnabled.isOn &&
                        EditingEmbeddingPreset().protocol == "builtin" && IsNewEmbeddingConfiguration
                        ? SettingsUiLanguage.T("\n当前运行尚未启用 Embedding。默认选择仍是草稿，保存主模型配置时会一并启用；之后无需重复设置。",
                            "\nRuntime Embedding is not enabled yet. The default selection is still a draft; saving the main-model configuration also enables it, with no repeated setup afterward.")
                        : SettingsUiLanguage.T("\n当前运行尚未配置 Embedding；启用草稿须保存并应用后才能维护索引。",
                            "\nRuntime Embedding is not configured; save and apply the enabled draft before maintaining the index.")
                    : SettingsUiLanguage.T("\n当前运行 Embedding 已关闭，不能维护向量索引。", "\nRuntime Embedding is off; vector maintenance is unavailable.");
                else if (state == "restart_required") ordinaryIndexState.text += SettingsUiLanguage.T(
                    "\nEmbedding 配置已保存；请重启 Backend 以应用。", "\nEmbedding configuration is saved; restart the Backend to apply it.");
                else if (currentIndex?.applied != true) ordinaryIndexState.text += SettingsUiLanguage.T("\n请先保存并应用 Embedding 配置。", "\nSave and apply Embedding configuration first.");
                ordinaryIndexAction.interactable = !busy && currentIndex?.applied == true && state != "running" && state != "disabled";
                SetButton(ordinaryIndexAction, "检查并维护索引", "Check & maintain index");
            }
            RebuildSettingsLayout();
        }

        // A matching port is necessary for manual management, not permission for an automatic kill.
        private static bool CanTargetLocalBackend(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == 8000;

        private static bool AppliedSnapshotMatches(ModelActivationView target, ModelActivationView active)
        {
            if (target?.main_model == null || active?.main_model == null || target.main_profile_id != active.main_profile_id ||
                target.embedding_profile_id != active.embedding_profile_id) return false;
            if (!JToken.DeepEquals(ModelProfileJson.Main(target.main_model), ModelProfileJson.Main(active.main_model))) return false;
            if (target.embedding?.enabled != active.embedding?.enabled) return false;
            return target.embedding?.enabled != true || JToken.DeepEquals(ModelProfileJson.Embedding(target.embedding), ModelProfileJson.Embedding(active.embedding));
        }

        private IEnumerator ConfirmSavedApplication(ModelProfilesView saved)
        {
            ModelProfilesView fresh = null; string error = null;
            yield return client.GetProfiles((value, code) => { fresh = value; error = code; });
            if (fresh == null || error != null)
            { SetStatus("配置已保存；运行状态未确认，请检查连接。", "Configuration saved; runtime unconfirmed. Check the connection."); yield break; }
            profilesView = fresh; revision = fresh.revision; RefreshProfileStates(); RefreshIndexState();
            if (fresh.restart_required)
            {
                // Offer only after the complete model + independent ContextPolicy save operation settles.
                restartOfferPending = true;
                SetStatus(fresh.configured ? "AI 配置已保存，需要重新启动以生效。" : "主模型配置和已保存密钥已移除；旧运行模型须重启后才失效。",
                    fresh.configured ? "AI configuration saved; restart required to apply." : "Main profile and saved key removed; the old runtime remains until restart.");
                yield break;
            }
            if (!fresh.configured && fresh.selected_to_activate == null && fresh.active_runtime == null)
            {
                SetStatus("主模型未配置，请填写新配置后保存；Embedding 已保存配置及密钥仍保留。",
                    "Main model is unconfigured. Enter and save a new configuration; saved embedding profiles and keys are retained.");
                yield break;
            }
            RuntimeStatusView runtime = null;
            yield return client.GetRuntimeStatus((value, code) => { runtime = value; error = code; });
            if (error == null && runtime != null && ModelConfigClient.StartupReady(fresh, runtime) &&
                AppliedSnapshotMatches(saved.selected_to_activate, fresh.active_runtime))
            {
                SetStatus("配置已生效。", "Configuration applied."); ConfigurationActivated?.Invoke();
                if (!developerMode && savedThisSession) Complete();
            }
            else SetStatus("配置已保存；运行配置尚未确认，请检查连接。", "Configuration saved; runtime configuration unconfirmed. Check the connection.");
        }
    }
}
