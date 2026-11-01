using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public enum ModelSettingsPage { Configuration, IndexMaintenance, Contracts, Diagnostics, Concurrency }

    public sealed partial class FirstRunModelSetup
    {
        private Transform developerDiagnostics;
        private bool developerToolsVisible;
        private GameObject configurationContent, contractContent, diagnosticsContent, concurrencyContent;
        private TMP_Text concurrencyHelp;
        private TMP_Text maintenanceTarget;
        private TMP_Text contractSummary;
        public ModelSettingsPage SettingsPage { get; private set; }
        public bool HasProfileSettings => profilesView != null;

        // Presentation boundary only. Profile commands and ContextPolicy transactions stay unchanged.
        private void ApplyInformationArchitecture()
        {
            developerDiagnostics = advancedSettings.transform.Find("SettingsDiagnostics");
            // Keep original controls/listeners. A view only owns their presentation containers.
            var content = advancedSettings.transform.parent;
            configurationContent = SettingsContent("ModelConfigurationContent", content);
            contractContent = SettingsContent("ModelContractsContent", content);
            var children = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in content) children.Add(child);
            foreach (var child in children)
                if(child.gameObject!=configurationContent && child.gameObject!=contractContent && child.gameObject!=memoryIndexSection)
                    child.SetParent(configurationContent.transform,false);
            contractTest.transform.parent.SetParent(contractContent.transform,false);
            FullWidthAction(contractTest);
            var actionRect=contractTest.GetComponent<RectTransform>();
            actionRect.anchorMin=actionRect.anchorMax=new Vector2(0,.5f);
            actionRect.offsetMin=new Vector2(12,-22);actionRect.offsetMax=new Vector2(332,22);
            var actionSize=contractTest.transform.parent.GetComponent<LayoutElement>();
            actionSize.minHeight=actionSize.preferredHeight=56;
            actionSize.flexibleHeight=0;
            if(contractTest.transform.parent.TryGetComponent<Image>(out var actionBackground))actionBackground.color=Color.clear;
            contractSummary=AddState(contractContent.transform,"ContractSummary");
            var summarySize=contractSummary.GetComponent<LayoutElement>();summarySize.minHeight=28;summarySize.preferredHeight=-1;
            contractSummary.fontSize=22;contractSummary.fontStyle=FontStyles.Bold;
            probeDetails.transform.SetParent(contractContent.transform,false);
            maintenanceTarget=AddState(contractContent.transform,"ContractTarget");maintenanceTarget.transform.SetAsFirstSibling();
            // Index action belongs with index status/progress, not with model credentials or generation.
            indexAction.transform.parent.SetParent(memoryIndexSection.transform,false);
            ArrangeAiSettingGroups();
            // Keep the original index control/progress/confirmation together in AI advanced settings.
            memoryIndexSection.transform.SetParent(advancedSettings.transform,false);
            diagnosticsContent = SettingsContent("ModelDiagnosticsContent", content);
            developerDiagnostics.SetParent(diagnosticsContent.transform, false);
            developerDiagnostics.gameObject.SetActive(true);
            mainCapacityState.transform.SetParent(diagnosticsContent.transform, false);
            diagnosticsToggle.transform.parent.gameObject.SetActive(false);
            var title=developerDiagnostics.Find("DiagnosticsTitle")?.GetComponent<TMP_Text>();
            if(title!=null)title.gameObject.SetActive(false);
            concurrencyContent = SettingsContent("ModelConcurrencyContent", content);
            concurrencyHelp = AddState(concurrencyContent.transform, "ConcurrencyHelp");
            var helpSize = concurrencyHelp.GetComponent<LayoutElement>();
            helpSize.minHeight = 72; helpSize.preferredHeight = -1;
            foreach (var input in new[] { totalConcurrency, foregroundConcurrency, backgroundConcurrency })
                input.transform.parent.SetParent(concurrencyContent.transform, false);
            SetSettingsPage(SettingsPage);
        }

        private static GameObject SettingsContent(string name, Transform parent)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(VerticalLayoutGroup));go.transform.SetParent(parent,false);
            var stack=go.GetComponent<VerticalLayoutGroup>();stack.spacing=SettingsPanelTheme.Spacing;
            stack.childControlHeight=stack.childControlWidth=true;stack.childForceExpandHeight=false;
            return go;
        }

        public void SetSettingsPage(ModelSettingsPage page)
        {
            SettingsPage=page;
            // View ownership cannot depend on the first successful profiles response.
            // Creating controls does not load or replace any configuration values.
            if(configurationContent==null)
            {
                EnsureControls();
                EnsureProfileControls();
                EnsureCompactLayout();
            }
            configurationContent.SetActive(page==ModelSettingsPage.Configuration);
            memoryIndexSection.SetActive(page==ModelSettingsPage.Configuration || page==ModelSettingsPage.IndexMaintenance);
            contractContent.SetActive(page==ModelSettingsPage.Contracts);
            diagnosticsContent.SetActive(page==ModelSettingsPage.Diagnostics);
            concurrencyContent.SetActive(page==ModelSettingsPage.Concurrency);
            concurrencyHelp.text=SettingsUiLanguage.T(
                "前台对话与后台记忆任务共享模型总并发，各自同时受前台／后台上限约束。云端默认 6 / 6 / 2，本地默认 1 / 1 / 1。\n保存并应用后需重启 Backend 生效；保存也会包含 AI 配置页的待保存修改。",
                "Foreground dialogue and background memory jobs share the total model capacity, with separate class limits. Cloud defaults: 6 / 6 / 2; local defaults: 1 / 1 / 1.\nSave & Apply requires a Backend restart and also saves pending edits in AI configuration.");
            saveButton.gameObject.SetActive(page==ModelSettingsPage.Configuration || page==ModelSettingsPage.Concurrency);
            if(restoreDefaultsButton!=null)restoreDefaultsButton.gameObject.SetActive(page==ModelSettingsPage.Configuration);
            if(saveButton.transform.parent.name=="FooterActions")
            {
                int count=0;foreach(Transform child in saveButton.transform.parent)if(child.gameObject.activeSelf)count++;
                var actions=(RectTransform)saveButton.transform.parent;
                actions.offsetMin=new Vector2(-Mathf.Max(260,count*220)-24,12);actions.offsetMax=new Vector2(-24,66);
            }
            if(maintenanceTarget!=null)maintenanceTarget.text=SettingsUiLanguage.T(
                "测试当前 AI 配置页选中的主模型草稿（含未保存修改）。本页不保存或应用配置；测试仍需原费用确认。\n模型：",
                "Tests the main-model draft selected in AI configuration, including unsaved edits. This page does not save/apply settings; the existing paid-request confirmation still applies.\nModel: ")+mainModel.text;
            if(maintenanceTarget!=null){var size=maintenanceTarget.GetComponent<LayoutElement>();size.minHeight=28;size.preferredHeight=-1;}
            RefreshProbeDetails();
            bool unavailable=profilesView==null || indexReadError is "BACKEND_UNAVAILABLE" or "TIMEOUT";
            if(unavailable && maintenanceTarget!=null)maintenanceTarget.text+=SettingsUiLanguage.T(
                "\nBackend 配置或状态不可用；请检查连接后重试。", "\nBackend configuration or status unavailable; check the connection and retry.");
            contractTest.interactable=!busy && !unavailable;
            // Serialized form Title is outside the scroll in the shared shell.
            Transform pageRoot=mainModel.transform;
            while(pageRoot!=null && pageRoot.name!="ModelPage")pageRoot=pageRoot.parent;
            var title=pageRoot?.Find("Title")?.GetComponent<TMP_Text>();
            if(title!=null)title.text=SettingsUiLanguage.T(page==ModelSettingsPage.Configuration?"AI 配置":page==ModelSettingsPage.Contracts?"模型合同测试":page==ModelSettingsPage.Diagnostics?"运行诊断 · 模型配置状态":page==ModelSettingsPage.Concurrency?"并发调整":"记忆索引维护",
                page==ModelSettingsPage.Configuration?"AI configuration":page==ModelSettingsPage.Contracts?"Model contracts":page==ModelSettingsPage.Diagnostics?"Runtime diagnostics · Model configuration":page==ModelSettingsPage.Concurrency?"Concurrency settings":"Memory index maintenance");
            RebuildSettingsLayout();
        }

        public void ShowAdvancedMaintenance()
        {
            if(!advancedSettings.activeSelf)ToggleAdvancedSettings();
        }

        public bool BlocksSettingsNavigation => busy || contextSaveInProgress || profileModal != null || restartExitRequested;
        public bool IsSavingSettings => busy || contextSaveInProgress;

        public bool CancelSettingsConfirmation()
        {
            if (profileModal == null) return false;
            if (IsSavingSettings) return true;
            var cancel = profileModalCancel;
            RemoveProfileModal(); profileModalCancel = null;
            cancel?.Invoke();
            return true;
        }

        public bool ConsumeSettingsEscape()
        {
            foreach (var dropdown in GetComponentsInChildren<TMP_Dropdown>())
                if (dropdown.IsExpanded) { dropdown.Hide(); return true; }
            return BlocksSettingsNavigation;
        }

        public void SetDeveloperTools(bool visible)
        {
            developerToolsVisible = visible;
            SetSettingsPage(visible ? ModelSettingsPage.Contracts : ModelSettingsPage.Configuration);
        }
    }
}
