using System;
using System.Collections.Generic;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Shared presentation for both scene hosts. Existing forms retain all service ownership.</summary>
    public sealed class PlayerSettingsNavigation : IDisposable
    {
        private readonly Transform window;
        private readonly FirstRunModelSetup form;
        private readonly Action<Action> request;
        private readonly Action showModel;
        private readonly Action showModelPresentation, showPerception;
        private readonly TMP_FontAsset font;
        private readonly List<GameObject> originalPages = new();
        private readonly List<(TMP_Text text, string zh, string en)> labels = new();
        private readonly GameObject general, display, audio, daily, ordinaryNav, developerNav;
        private readonly Button developerEntry;
        private readonly TMP_Dropdown language, npcReplyLanguage;
        private readonly TMP_Text help;
        private string ordinarySelection = "GeneralSettingsNav";
        private string developerSelection = "DeveloperTool0";
        private bool navigating;
        private readonly Dictionary<string, Button> navigationButtons = new();
        private GameObject debugSubNavigation;
        public string SelectedNavigation => IsDeveloper ? developerSelection : ordinarySelection;
        public const float SidebarWidth = 304;
        public bool IsDeveloper { get; private set; }
        public GameObject GeneralPage => general;
        public GameObject AudioPage => audio;
        public Transform DailyDebugRoot => daily.transform;

        public PlayerSettingsNavigation(Transform window, FirstRunModelSetup form, CafeBackgroundMusic music,
            TMP_Dropdown existingLanguage, GameObject[] existingPages, Action<Action> request, Action showModel,
            (string zh, string en, Action show)[] developerActions,
            Action showNpc = null, Action showKnowledge = null, Action showPerception = null,
            Action showModelPresentation = null)
        {
            this.window = window; this.form = form; this.request = request; this.showModel = showModel;
            this.showPerception = showPerception; this.showModelPresentation = showModelPresentation ?? showModel;
            font = window.GetComponentInChildren<TMP_Text>(true).font;
            originalPages.AddRange(existingPages);
            var oldNav = window.Find("SettingsNavigation");
            if (oldNav != null) oldNav.gameObject.SetActive(false);
            var oldVolume = window.Find("TitleBgmVolume");
            if (oldVolume != null) oldVolume.gameObject.SetActive(false);
            // A fixed sidebar owns navigation; page footers only own page operations.
            foreach (var page in originalPages) if (page != null) {LayoutPage(page.transform);LayoutPageOperations(page.transform);}
            var oldHeader = window.Find("Header");
            if (oldHeader != null) Place(oldHeader, new Vector2(0,1), new Vector2(0,1), new Vector2(24,-95), new Vector2(300,-59));
            var oldHeading = window.Find("SettingsHeading");
            if (oldHeading != null) Place(oldHeading,new Vector2(0,1),new Vector2(0,1),new Vector2(24,-53),new Vector2(300,-9));
            general = Page("GeneralSettingsPage"); display = Page("DisplaySettingsPage"); audio = Page("AudioSettingsPage"); daily = Page("DailyDebugPage");
            var generalContent = Node("GeneralContent", general.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var contentRect=(RectTransform)generalContent.transform; contentRect.anchorMin=new Vector2(0,1); contentRect.anchorMax=Vector2.one;
            contentRect.pivot=new Vector2(.5f,1); contentRect.sizeDelta=Vector2.zero;
            var fitter=generalContent.GetComponent<ContentSizeFitter>(); fitter.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            general.AddComponent<Image>().color=Color.clear; // Receive scroll/drag over help text and empty space too.
            general.AddComponent<RectMask2D>(); var generalScroll=general.AddComponent<ScrollRect>();
            generalScroll.viewport=(RectTransform)general.transform; generalScroll.content=contentRect; generalScroll.horizontal=false;
            generalScroll.movementType=ScrollRect.MovementType.Clamped; generalScroll.scrollSensitivity=32;
            var stack = generalContent.GetComponent<VerticalLayoutGroup>(); stack.padding = new RectOffset(36,36,20,20);
            stack.spacing = 12; stack.childForceExpandHeight = false;
            Height(Heading(generalContent.transform, "通用与操作", "General & controls").gameObject,44);
            var row = Node("LanguagePreference", generalContent.transform, typeof(LayoutElement)); Height(row, 68);
            Label(row.transform, "界面语言 / UI Language", "界面语言 / UI Language", 22, new Vector2(0, 0), new Vector2(.4f, 1));
            language = existingLanguage;
            if (language == null)
            {
                var template = window.GetComponentInChildren<TMP_Dropdown>(true);
                language = UnityEngine.Object.Instantiate(template, row.transform);
                language.name = "SettingsLanguageDropdown";
                language.onValueChanged = new TMP_Dropdown.DropdownEvent();
                language.onValueChanged.AddListener(ChangeLanguage);
            }
            if (existingLanguage != null) existingLanguage.transform.parent.gameObject.SetActive(false);
            language.ClearOptions(); language.AddOptions(new List<string> { "中文 / Chinese", "英文 / English" });
            language.transform.SetParent(row.transform, false);
            language.gameObject.SetActive(true); language.interactable = true;
            Place(language.transform, new Vector2(.45f, 0), Vector2.one, Vector2.zero, Vector2.zero);
            if (language.TryGetComponent<LayoutElement>(out var element)) element.ignoreLayout = true;
            var npcLanguageRow = Node("NpcReplyLanguagePreference", generalContent.transform, typeof(LayoutElement)); Height(npcLanguageRow, 68);
            Label(npcLanguageRow.transform, "NPC 回复语言", "NPC reply language", 22, Vector2.zero, new Vector2(.4f, 1));
            npcReplyLanguage = UnityEngine.Object.Instantiate(language, npcLanguageRow.transform);
            npcReplyLanguage.name = "NpcReplyLanguageDropdown";
            npcReplyLanguage.onValueChanged = new TMP_Dropdown.DropdownEvent();
            npcReplyLanguage.onValueChanged.AddListener(value => {
                NpcReplyLanguageSettings.SetPreference((NpcReplyLanguagePreference)value);
                npcReplyLanguage.SetValueWithoutNotify((int)NpcReplyLanguageSettings.Preference);
            });
            Place(npcReplyLanguage.transform, new Vector2(.45f, 0), Vector2.one, Vector2.zero, Vector2.zero);
            Height(Heading(generalContent.transform, "操作说明", "Controls").gameObject,40);
            help = Label(generalContent.transform, "", "", 23); Height(help.gameObject, 190);
            Height(Heading(generalContent.transform, "支持与诊断", "Support & diagnostics").gameObject,40);
            var actions=Node("DiagnosticsActions",generalContent.transform,typeof(HorizontalLayoutGroup),typeof(LayoutElement));Height(actions,56);
            var actionLayout=actions.GetComponent<HorizontalLayoutGroup>();actionLayout.spacing=12;actionLayout.childControlWidth=actionLayout.childControlHeight=true;actionLayout.childForceExpandWidth=true;
            var exportState=Label(generalContent.transform,"","",20); Height(exportState.gameObject,64);
            labels.RemoveAll(x=>x.text==exportState);
            ActionButton(actions.transform,"ExportDiagnostics","导出诊断日志","Export diagnostic logs",()=>exportState.text=DiagnosticsExport.TryExport());
            ActionButton(actions.transform,"OpenDiagnosticsFolder","打开导出目录","Open export folder",()=>exportState.text=DiagnosticsExport.TryOpenFolder());
            BuildDisplay();
            Heading(audio.transform, "音频", "Audio");
            BuildAudio(audio.transform, music);
            var dailyStack = daily.AddComponent<VerticalLayoutGroup>();
            dailyStack.padding = new RectOffset(70, 70, 30, 30); dailyStack.spacing = 18; dailyStack.childForceExpandHeight = false;
            Heading(daily.transform, "NPC 日常行为调试", "NPC daily behavior debug");
            ordinaryNav = NavigationColumn("PlayerSettingsNavigation");
            NavigationHeading(ordinaryNav.transform,"普通设置","Settings");
            ActionButton(ordinaryNav.transform, "GeneralSettingsNav", "通用与操作", "General & controls", ShowGeneral);
            ActionButton(ordinaryNav.transform, "DisplaySettingsNav", "画面", "Display", ShowDisplay);
            ActionButton(ordinaryNav.transform, "AudioSettingsNav", "音频", "Audio", ShowAudio);
            ActionButton(ordinaryNav.transform, "AiSettingsNav", "AI 配置", "AI configuration", () => ShowAi(false));
            if (showNpc != null || showKnowledge != null) NavigationHeading(ordinaryNav.transform,"内容自定义","Customize content");
            if (showNpc != null) ActionButton(ordinaryNav.transform,"NpcCharacterSettingsNav","NPC 人设","NPC persona",()=>Navigate(showNpc,"NpcCharacterSettingsNav",false));
            if (showKnowledge != null) ActionButton(ordinaryNav.transform,"WorldKnowledgeSettingsNav","世界知识","World knowledge",()=>Navigate(showKnowledge,"WorldKnowledgeSettingsNav",false));
            developerNav = NavigationColumn("DeveloperToolsNavigation");
            NavigationHeading(developerNav.transform,"开发者工具","Developer tools");
            for (int i = 0; i < developerActions.Length; i++)
            {
                var action = developerActions[i];
                string key = "DeveloperTool" + i;
                ActionButton(developerNav.transform, key, action.zh, action.en, () => Navigate(action.show,key,true));
                if(i==0)ActionButton(developerNav.transform,"ModelDiagnosticsNav","　模型配置状态","　Model configuration status",ShowModelDiagnostics);
                if (i == 1 && showPerception != null)
                {
                    debugSubNavigation = Node("NpcDebugSubNavigation",developerNav.transform,typeof(VerticalLayoutGroup),typeof(LayoutElement));
                    Height(debugSubNavigation,128);
                    var subStack = debugSubNavigation.GetComponent<VerticalLayoutGroup>(); subStack.spacing=8; subStack.padding=new RectOffset(18,0,0,0); subStack.childForceExpandHeight=false;
                    ActionButton(debugSubNavigation.transform,"DailyBehaviorDebugNav","日常行为","Daily behavior",ShowDaily);
                    ActionButton(debugSubNavigation.transform,"PerceptionDebugNav","感知与可视化","Perception & overlays",()=>Navigate(showPerception,"DeveloperTool1",true));
                }
            }
            if(developerActions.Length==0)ActionButton(developerNav.transform,"ModelDiagnosticsNav","运行诊断 · 模型配置状态","Runtime diagnostics · Models",ShowModelDiagnostics);
            ActionButton(developerNav.transform, "ModelContractsNav", "模型合同测试", "Model contracts", ShowModelContracts);
            developerEntry = ActionButton(window, "DeveloperToolsEntry", "开发者工具", "Developer tools", () => SetDeveloper(true));
            Place(developerEntry.transform, Vector2.zero,Vector2.zero,new Vector2(24,24),new Vector2(SidebarWidth,78));
            var back = ActionButton(window, "BackToPlayerSettings", "返回普通设置", "Back to settings", () => SetDeveloper(false));
            Place(back.transform,Vector2.zero,Vector2.zero,new Vector2(24,24),new Vector2(SidebarWidth,78));
            back.gameObject.SetActive(false);
            backButton = back;
            SettingsUiLanguage.Changed += RefreshLanguage;
            NpcReplyLanguageSettings.Changed += RefreshLanguage;
            RefreshLanguage(); SetDeveloper(false);
        }
        private readonly Button backButton;
        private static void ChangeLanguage(int value) => SettingsUiLanguage.SetEnglish(value == 1);
        public void Dispose() { SettingsUiLanguage.Changed -= RefreshLanguage; NpcReplyLanguageSettings.Changed -= RefreshLanguage; }

        public void RefreshLanguage()
        {
            foreach (var label in labels) if (label.text != null) label.text.text = SettingsUiLanguage.T(label.zh, label.en);
            language.SetValueWithoutNotify(SettingsUiLanguage.IsEnglish ? 1 : 0); language.RefreshShownValue();
            npcReplyLanguage.ClearOptions(); npcReplyLanguage.AddOptions(new List<string> {
                SettingsUiLanguage.T("跟随界面语言", "Follow interface"), SettingsUiLanguage.T("中文优先", "Chinese preferred"), SettingsUiLanguage.T("English 优先", "English preferred") });
            npcReplyLanguage.SetValueWithoutNotify((int)NpcReplyLanguageSettings.Preference); npcReplyLanguage.RefreshShownValue();
            help.text = SettingsOperationHelp.Text;
            var header = window.Find("Header")?.GetComponent<TMP_Text>();
            if (header != null) header.text = SettingsUiLanguage.T(IsDeveloper ? "EmbodiedNPC · 开发者工具" : "EmbodiedNPC · 设置", IsDeveloper ? "EmbodiedNPC · Developer tools" : "EmbodiedNPC · Settings");
            var heading = window.Find("SettingsHeading")?.GetComponent<TMP_Text>();
            if (heading != null) heading.text = SettingsUiLanguage.T("设置", "Settings");
            RefreshSelection();
            form.SetSettingsPage(form.SettingsPage);
        }
        private void RefreshSelection()
        {
            foreach (var pair in navigationButtons)
                SettingsPanelTheme.Surface(pair.Value.targetGraphic as Image,pair.Key==SelectedNavigation ? SettingsPanelTheme.Accent : SettingsPanelTheme.Row);
            if (debugSubNavigation != null) debugSubNavigation.SetActive(IsDeveloper && developerSelection=="DeveloperTool1");
        }
        private void HideNewPages() { general.SetActive(false); display.SetActive(false); audio.SetActive(false); daily.SetActive(false); }
        private void HideAll()
        {
            HideNewPages();
            foreach (var page in originalPages) if (page != null) page.SetActive(false);
            form.SetSettingsPage(ModelSettingsPage.Configuration);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }
        public void SelectExisting(GameObject page)
        {
            HideNewPages();
            if (!navigating && page != null)
            {
                switch (page.name)
                {
                    case "ModelPage":
                        if (!IsDeveloper || (developerSelection!="ModelMaintenanceNav" && developerSelection!="ModelContractsNav" && developerSelection!="ModelDiagnosticsNav"))
                        { SetMode(false); ordinarySelection="AiSettingsNav"; form.SetSettingsPage(ModelSettingsPage.Configuration); }
                        break;
                    case "NpcPage": SetMode(false); ordinarySelection="NpcCharacterSettingsNav"; break;
                    case "WorldKnowledgePage": case "KnowledgePage": SetMode(false); ordinarySelection="WorldKnowledgeSettingsNav"; break;
                    case "OverviewPage": SetMode(true); developerSelection="DeveloperTool0"; break;
                    case "DebugPage": SetMode(true); developerSelection="DeveloperTool1"; break;
                    case "TracePage": SetMode(true); developerSelection="DeveloperTool2"; break;
                }
            }
            RefreshSelection();
        }
        public void ShowGeneral() => Navigate(()=>general.SetActive(true),"GeneralSettingsNav",false);
        public void ShowDisplay() => Navigate(()=>display.SetActive(true),"DisplaySettingsNav",false);
        public void ShowAudio() => Navigate(()=>audio.SetActive(true),"AudioSettingsNav",false);
        public void ShowDaily() => Navigate(()=>daily.SetActive(true),"DeveloperTool1",true);
        public void ShowAi(bool maintenance)
        {
            if (maintenance) { ShowIndexMaintenance(); return; }
            Navigate(()=>{ OpenModelShell(); form.SetSettingsPage(ModelSettingsPage.Configuration); },"AiSettingsNav",false);
        }
        private void OpenModelShell() => showModelPresentation();
        public void ShowIndexMaintenance() => Navigate(()=>{OpenModelShell();form.SetSettingsPage(ModelSettingsPage.Configuration);form.ShowAdvancedMaintenance();},"AiSettingsNav",false);
        public void ShowModelContracts() => Navigate(()=>{OpenModelShell();form.SetSettingsPage(ModelSettingsPage.Contracts);},"ModelContractsNav",true);
        public void ShowModelDiagnostics() => Navigate(()=>{OpenModelShell();form.SetSettingsPage(ModelSettingsPage.Diagnostics);},"ModelDiagnosticsNav",true);
        private void SetMode(bool enabled)
        {
            IsDeveloper=enabled; ordinaryNav.SetActive(!enabled);developerNav.SetActive(enabled);
            developerEntry.gameObject.SetActive(!enabled);backButton.gameObject.SetActive(enabled);
        }
        private void Navigate(Action show,string key,bool developer)
        {
            HideAll();SetMode(developer);
            if(developer)developerSelection=key;else ordinarySelection=key;
            navigating=true;
            try {show();} finally {navigating=false;}
            ResetVisibleScroll();RefreshLanguage();
        }
        private void ResetVisibleScroll()
        {
            Canvas.ForceUpdateCanvases();
            foreach(var scroll in window.GetComponentsInChildren<ScrollRect>())
            { if(scroll.horizontal)continue; LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);scroll.verticalNormalizedPosition=1; }
        }
        public void SetDeveloper(bool enabled)
        {
            if (!enabled) {ShowGeneral();return;}
            var first=developerNav.GetComponentInChildren<Button>(true);
            if(first!=null)first.onClick.Invoke();
        }
        public bool ConsumeBack()
        {
            foreach (var dropdown in window.GetComponentsInChildren<TMP_Dropdown>())
                if (dropdown.IsExpanded) { dropdown.Hide(); return true; }
            if (form.BlocksSettingsNavigation) return true;
            if (!IsDeveloper) return false;
            request(() => SetDeveloper(false)); return true;
        }
        private GameObject Page(string name)
        {
            var page = Node(name, window);
            LayoutPage(page.transform);
            return page;
        }
        public static void LayoutPage(Transform page) => Place(page,Vector2.zero,Vector2.one,new Vector2(SidebarWidth+40,16),new Vector2(-24,-112));
        public static void LayoutPageOperations(Transform page)
        {
            var footer=page.Find("SettingsFooter");if(footer==null)return;
            Place(footer,Vector2.zero,Vector2.right,Vector2.zero,new Vector2(0,140));
            var actions=footer.Find("FooterActions");
            int count=0;foreach(Transform child in actions)if(child.gameObject.activeSelf)count++;
            float width=Mathf.Max(260,count*220);
            Place(actions,Vector2.right,Vector2.right,new Vector2(-width-24,12),new Vector2(-24,66));
            foreach(Transform child in footer)
                if(child!=actions && child.GetComponent<TMP_Text>()!=null)
                {
                    Place(child,Vector2.zero,Vector2.one,new Vector2(24,78),new Vector2(-24,-10));
                    var t=child.GetComponent<TMP_Text>();t.textWrappingMode=TextWrappingModes.Normal;t.overflowMode=TextOverflowModes.Ellipsis;
                }
            foreach(var scroll in page.GetComponentsInChildren<ScrollRect>(true))
                if(scroll.transform.parent==page || scroll.transform.parent?.name=="ModelConfigForm")
                {var r=(RectTransform)scroll.transform;r.offsetMin=new Vector2(r.offsetMin.x,154);}
        }
        private GameObject NavigationColumn(string name)
        {
            var go=Node(name,window,typeof(VerticalLayoutGroup));
            Place(go.transform,new Vector2(0,0),new Vector2(0,1),new Vector2(24,108),new Vector2(SidebarWidth,-120));
            var stack=go.GetComponent<VerticalLayoutGroup>();stack.spacing=10;stack.childControlHeight=stack.childControlWidth=true;stack.childForceExpandHeight=false;
            return go;
        }
        private void NavigationHeading(Transform parent,string zh,string en)
        {var t=Label(parent,zh,en,18);t.fontStyle=FontStyles.Bold;Height(t.gameObject,32);}
        private TMP_Text Heading(Transform parent, string zh, string en)
        {
            var t = Label(parent, zh, en, 30); t.fontStyle = FontStyles.Bold; Height(t.gameObject, 60);
            if (parent == audio.transform) Place(t.transform, new Vector2(.06f, 1), new Vector2(.94f, 1), new Vector2(0, -85), new Vector2(0, -20));
            return t;
        }
        private void BuildDisplay()
        {
            var layout=display.AddComponent<VerticalLayoutGroup>();layout.padding=new RectOffset(36,36,20,20);layout.spacing=12;layout.childForceExpandHeight=false;
            Height(Heading(display.transform,"画面","Display").gameObject,44);
            Label(display.transform,"分辨率","Resolution",24).gameObject.AddComponent<LayoutElement>().preferredHeight=40;
            var dropdown=UnityEngine.Object.Instantiate(language,display.transform);dropdown.name="DisplayResolution";
            dropdown.onValueChanged=new TMP_Dropdown.DropdownEvent();
            Height(dropdown.gameObject,60);dropdown.GetComponent<LayoutElement>().ignoreLayout=false;
            dropdown.ClearOptions();dropdown.AddOptions(new List<string>{"3840 × 2160 (4K)","2560 × 1440 (1440p)","1920 × 1080 (1080p)","1280 × 720 (720p)"});
            dropdown.SetValueWithoutNotify(DisplaySettings.SelectedIndex);dropdown.RefreshShownValue();
            var info=Label(display.transform,"Alt + Enter：切换全屏 / 窗口模式。窗口会适配桌面可用空间。","Alt + Enter: Toggle Fullscreen / Windowed. Window size fits the desktop work area.",22);FlexibleText(info,28);
            var state=Label(display.transform,"","",20);FlexibleText(state,0);state.gameObject.SetActive(false);labels.RemoveAll(x=>x.text==state);
            dropdown.onValueChanged.AddListener(index=>{state.text=DisplaySettings.Select(index);state.gameObject.SetActive(!string.IsNullOrEmpty(state.text));});
            var free = Node("FreeCameraPermissionRow", display.transform, typeof(Image), typeof(LayoutElement)); Height(free, 56);
            free.GetComponent<Image>().color=Color.clear;
            var switchRoot=Node("FreeCameraPermission",free.transform,typeof(Toggle));
            Place(switchRoot.transform,new Vector2(0,.5f),new Vector2(0,.5f),new Vector2(0,-20),new Vector2(64,20));
            var toggle=switchRoot.GetComponent<Toggle>();
            var box = Node("Background", switchRoot.transform, typeof(Image));
            Place(box.transform,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            SettingsPanelTheme.Surface(box.GetComponent<Image>(),SettingsPanelTheme.Row);
            toggle.targetGraphic=box.GetComponent<Image>();
            var freeCaption=Label(free.transform,"允许自由视角","Enable Free Camera",23,Vector2.zero,Vector2.one);
            freeCaption.rectTransform.offsetMin=new Vector2(80,4);
            toggle.SetIsOnWithoutNotify(DisplaySettings.FreeCameraEnabled);
            SettingsUiLanguage.StyleToggle(toggle);
            var note=Label(display.transform,"开启后 Tab 切换自由视角，H 隐藏 / 显示提示。设置与聊天期间快捷键暂停。",
                "When enabled, Tab toggles Free Camera and H toggles hints. Shortcuts pause in settings and chat.",22);FlexibleText(note,28);
            toggle.onValueChanged.AddListener(allowed=>{
                if(!DisplaySettings.SetFreeCameraEnabled(allowed))
                {toggle.SetIsOnWithoutNotify(DisplaySettings.FreeCameraEnabled);state.text=SettingsUiLanguage.T("无法保存画面设置。","Cannot save display settings.");return;}
                UnityEngine.Object.FindAnyObjectByType<CafeDemoOrbitCamera>()?.SetPresentationCameraAllowed(allowed);
            });
        }
        private void BuildAudio(Transform parent, CafeBackgroundMusic music)
        {
            var row = Node("BgmVolumeRow", parent, typeof(Image), typeof(CafeBgmVolumeSetting));
            SettingsPanelTheme.Surface(row.GetComponent<Image>(), SettingsPanelTheme.Row);
            Place(row.transform, new Vector2(.06f, 1), new Vector2(.94f, 1), new Vector2(0, -190), new Vector2(0, -110));
            Label(row.transform, "背景音乐音量", "Background music volume", 22, new Vector2(.02f, 0), new Vector2(.38f, 1));
            var percentage = Label(row.transform, "", "", 22, new Vector2(.88f, 0), new Vector2(.99f, 1));
            var slider = Node("BgmVolumeSlider", row.transform, typeof(Slider)).GetComponent<Slider>();
            Place(slider.transform, new Vector2(.42f, .3f), new Vector2(.85f, .7f), Vector2.zero, Vector2.zero);
            var track = Node("Track", slider.transform, typeof(Image)); Place(track.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(track.GetComponent<Image>(), SettingsPanelTheme.Muted);
            var fill = Node("Fill", track.transform, typeof(Image)); Place(fill.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(fill.GetComponent<Image>(), SettingsPanelTheme.Accent);
            var handle = Node("Handle", slider.transform, typeof(Image)); Place(handle.transform, new Vector2(0, .5f), new Vector2(0, .5f), new Vector2(-12, -16), new Vector2(12, 16));
            SettingsPanelTheme.Surface(handle.GetComponent<Image>(), SettingsPanelTheme.Accent);
            slider.fillRect = (RectTransform)fill.transform; slider.handleRect = (RectTransform)handle.transform; slider.targetGraphic = handle.GetComponent<Image>();
            slider.minValue = 0; slider.maxValue = 1;
            row.GetComponent<CafeBgmVolumeSetting>().Bind(music, slider, percentage);
            // Values are refreshed by the existing binder, never overwritten by localization.
            labels.RemoveAll(x => x.text == percentage);
        }
        private Button ActionButton(Transform parent, string name, string zh, string en, Action action)
        {
            var node = Node(name, parent, typeof(Image), typeof(Button), typeof(LayoutElement)); Height(node, 56);
            node.GetComponent<LayoutElement>().flexibleWidth = 1;
            var button = node.GetComponent<Button>(); button.targetGraphic = node.GetComponent<Image>(); SettingsPanelTheme.Button(button);
            navigationButtons[name]=button;
            var label = Label(node.transform, zh, en, 21, Vector2.zero, Vector2.one);
            label.alignment = TextAlignmentOptions.Center; label.enableAutoSizing = true; label.fontSizeMin = 13; label.fontSizeMax = 21;
            button.onClick.AddListener(() => { if (!form.BlocksSettingsNavigation) request(action); });
            return button;
        }
        private TMP_Text Label(Transform parent, string zh, string en, float size, Vector2? min = null, Vector2? max = null)
        {
            var t = Node("Caption", parent, typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            t.font = font; t.fontSize = size; t.color = SettingsPanelTheme.Ink; t.raycastTarget = false; t.alignment = TextAlignmentOptions.MidlineLeft;
            labels.Add((t, zh, en)); t.text = SettingsUiLanguage.T(zh, en);
            if (min.HasValue) Place(t.transform, min.Value, max.Value, new Vector2(10, 4), new Vector2(-10, -4));
            return t;
        }
        private static GameObject Node(string name, Transform parent, params Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            foreach (var type in components) go.AddComponent(type); return go;
        }
        private static void Height(GameObject go, float h)
        {
            var e = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); e.minHeight = e.preferredHeight = h;
        }
        private static void FlexibleText(TMP_Text text,float minimum)
        {
            var size=text.gameObject.AddComponent<LayoutElement>();size.minHeight=minimum;size.preferredHeight=-1;
            text.textWrappingMode=TextWrappingModes.Normal;
        }
        private static void Place(Transform t, Vector2 min, Vector2 max, Vector2 lo, Vector2 hi)
        { var r = (RectTransform)t; r.anchorMin = min; r.anchorMax = max; r.offsetMin = lo; r.offsetMax = hi; }
    }
}
