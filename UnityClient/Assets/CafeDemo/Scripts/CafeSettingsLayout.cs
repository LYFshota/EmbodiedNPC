using System;
using System.Collections.Generic;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Reuses the wired views; only owns layout and navigation presentation.</summary>
    public sealed class CafeSettingsLayout
    {
        private readonly Transform window;
        private readonly TMP_FontAsset font;
        private readonly CafeDemoDeveloperPanel controller;
        private readonly Dictionary<string, Transform> objects = new();
        private readonly List<(Button button, GameObject page)> pages = new();
        private readonly List<(TMP_Text text, string chinese, string english)> labels = new();
        private PlayerSettingsNavigation playerNavigation;
        public PlayerSettingsNavigation PlayerNavigation => playerNavigation;
        public CafeSettingsLayout(Transform window, CafeDemoDeveloperPanel controller)
        {
            this.window = window;
            this.controller = controller;
            font = window.GetComponentInChildren<TMP_Text>(true).font;
            foreach (Transform t in window.GetComponentsInChildren<Transform>(true))
                if (!objects.ContainsKey(t.name)) objects.Add(t.name, t);
        }
        private Transform Find(string name) => objects.TryGetValue(name, out var t) ? t : null;
        private GameObject New(string name, Transform parent, params Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            foreach (var type in components) go.AddComponent(type);
            return go;
        }
        public void Build(Button[] navigation, GameObject[] pageObjects)
        {
            window.gameObject.AddComponent<SettingsPanelTheme>();
            Rect(window, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            window.GetComponent<Image>().color = SettingsPanelTheme.Paper;
            // Keep world/dialogue scaling unchanged; anchors adapt within the existing canvas.
            var heading = Find("Header").GetComponent<TMP_Text>();
            Rect(heading.transform,new Vector2(0,1),new Vector2(.24f,1),new Vector2(40,-99),new Vector2(-10,-58));
            heading.fontSize=18; heading.fontStyle=FontStyles.Normal;
            heading.alignment=TextAlignmentOptions.MidlineLeft;
            var title=Text(window,"SettingsHeading","设置","Settings",36);
            Rect(title.transform,new Vector2(0,1),new Vector2(.2f,1),new Vector2(40,-49),new Vector2(-10,-8));
            var nav=New("SettingsNavigation",window,typeof(Image));
            Rect(nav.transform,new Vector2(.25f,1),new Vector2(.81f,1),new Vector2(0,-99),new Vector2(0,-19));
            SettingsPanelTheme.Surface(nav.GetComponent<Image>(),SettingsPanelTheme.Navigation);
            var group=nav.AddComponent<HorizontalLayoutGroup>();
            group.padding=new RectOffset(9,9,7,7);group.spacing=5;
            group.childControlHeight=group.childControlWidth=true;
            group.childForceExpandWidth=true;group.childForceExpandHeight=true;
            for(int i=0;i<navigation.Length;i++)
            {
                var button=navigation[i];button.transform.SetParent(nav.transform,false);
                var element=Element(button.gameObject,0);element.flexibleWidth=1; element.minWidth=0;
                var caption=button.GetComponentInChildren<TMP_Text>(true);
                caption.fontSize=18;caption.enableAutoSizing=true;caption.fontSizeMin=13;caption.fontSizeMax=18;
                caption.alignment=TextAlignmentOptions.Center;
                Rect(caption.transform,Vector2.zero,Vector2.one,new Vector2(3,4),new Vector2(-3,-36));
                var icon=New("CategoryIcon",button.transform,typeof(SettingsNavigationIcon)).GetComponent<SettingsNavigationIcon>();
                icon.Kind=i;icon.raycastTarget=false;
                Rect(icon.transform,new Vector2(.5f,1),new Vector2(.5f,1),new Vector2(-15,-34),new Vector2(15,-4));
                pages.Add((button,pageObjects[i]));
            }
            var close=Find("Close");
            Rect(close,new Vector2(1,1),new Vector2(1,1),new Vector2(-91,-85),new Vector2(-29,-23));
            close.GetComponentInChildren<TMP_Text>(true).fontSize=30;
            foreach(var page in pageObjects)
            {
                Rect(page.transform,Vector2.zero,Vector2.one,new Vector2(0,0),new Vector2(0,-115));
                if(page.TryGetComponent<Image>(out var image)) image.enabled=false;
            }
            Overview(pageObjects[0]);Model(pageObjects[1]);Npc(pageObjects[2]);Debug(pageObjects[3]);Trace(pageObjects[4]);
            AddHearingControl();
            SettingsPanelTheme.Apply(window.gameObject);
            foreach (var label in labels)
            {
                var localized = label.text.gameObject.AddComponent<SettingsLocalizedLabel>();
                localized.Chinese = label.chinese; localized.English = label.english;
            }
            RefreshLanguage();
        }
        public void Bind(Button[] navigation, GameObject[] pageObjects)
        {
            for (int i = 0; i < navigation.Length; i++) pages.Add((navigation[i], pageObjects[i]));
            foreach (var label in window.GetComponentsInChildren<SettingsLocalizedLabel>(true))
                labels.Add((label.GetComponent<TMP_Text>(), label.Chinese, label.English));
            // The shipped prefab already has its layout; Build() is an authoring path.
            // Apply these local changes to the live prefab too, without rebuilding its pages.
            foreach (string name in new[]{"NpcStatus","TraceStatus","SelectedNpcReadout","PerceptionReadout"})
            {
                var text=Find(name)?.GetComponent<TMP_Text>();if(text==null)continue;
                text.enableAutoSizing=false;text.fontSize=name.EndsWith("Readout")?24:21;text.color=SettingsPanelTheme.Ink;
                var stack=text.transform.parent.GetComponent<VerticalLayoutGroup>();
                if(stack!=null)stack.padding=new RectOffset(24,24,12,12);
            }
            AddHearingControl();
            Find("RefreshStatus").GetComponent<Button>().onClick.AddListener(controller.ShowOverview);
            playerNavigation = new PlayerSettingsNavigation(window, controller.ModelForm, controller.BackgroundMusic,
                controller.LanguageDropdown,
                pageObjects, controller.RequestSettingsNavigation, controller.ShowModel,
                new (string, string, Action)[] {
                    ("运行诊断", "Runtime diagnostics", controller.ShowOverview),
                    ("NPC 行为与感知调试", "NPC behavior & perception", () => playerNavigation.ShowDaily()),
                    ("运行追踪", "Runtime Trace", controller.ShowTrace) },
                controller.ShowNpc, controller.ShowKnowledge, controller.ShowDebug,controller.ShowModelPresentation);
            controller.GetComponent<CafeDailyBehaviorSettings>()?.BindDebugRoot(playerNavigation.DailyDebugRoot);
            RefreshLanguage();
        }
        private void BgmVolume(GameObject page)
        {
            var music = controller.BackgroundMusic;
            var content = page.GetComponentInChildren<ScrollRect>(true)?.content;
            if (music == null || content == null || content.Find("BgmVolumeRow") != null) return;
            var section = Section(content, "音频", "Audio");
            section.SetAsFirstSibling();
            var row = New("BgmVolumeRow", content, typeof(Image), typeof(LayoutElement), typeof(CafeBgmVolumeSetting));
            row.transform.SetSiblingIndex(1);
            SettingsPanelTheme.Surface(row.GetComponent<Image>(), SettingsPanelTheme.Row);
            row.GetComponent<Image>().raycastTarget = false;
            Element(row, SettingsPanelTheme.RowHeight);
            var label = Text(row.transform, "BgmVolumeLabel", "背景音乐音量", "Background music volume", 21);
            label.fontStyle = FontStyles.Bold;
            label.enableAutoSizing = true; label.fontSizeMin = 16; label.fontSizeMax = 21;
            Rect(label.transform, Vector2.zero, new Vector2(.39f, 1), new Vector2(32, 6), new Vector2(-12, -6));
            var value = Text(row.transform, "BgmVolumePercent", "50%", "50%", 21);
            // This is a value, not a localized label; language refresh must not reset it to 50%.
            labels.RemoveAt(labels.Count - 1);
            value.alignment = TextAlignmentOptions.MidlineRight;
            Rect(value.transform, new Vector2(.86f, 0), Vector2.one, new Vector2(0, 6), new Vector2(-28, -6));
            var root = New("BgmVolumeSlider", row.transform, typeof(Slider));
            Rect(root.transform, new Vector2(.41f, 0), new Vector2(.83f, 1), new Vector2(0, 12), new Vector2(0, -12));
            var track = New("Track", root.transform, typeof(Image));
            Rect(track.transform, new Vector2(0, .5f), new Vector2(1, .5f), new Vector2(0, -5), new Vector2(0, 5));
            SettingsPanelTheme.Surface(track.GetComponent<Image>(), SettingsPanelTheme.Muted);
            var fill = New("Fill", track.transform, typeof(Image));
            Rect(fill.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(fill.GetComponent<Image>(), SettingsPanelTheme.Accent);
            var handleArea = New("HandleArea", root.transform);
            Rect(handleArea.transform, new Vector2(0, .5f), new Vector2(1, .5f), new Vector2(14, 0), new Vector2(-14, 0));
            var handle = New("Handle", handleArea.transform, typeof(Image));
            Rect(handle.transform, new Vector2(0, .5f), new Vector2(0, .5f), new Vector2(-14, -14), new Vector2(14, 14));
            SettingsPanelTheme.Surface(handle.GetComponent<Image>(), SettingsPanelTheme.Accent);
            var slider = root.GetComponent<Slider>();
            slider.minValue = 0; slider.maxValue = 1; slider.wholeNumbers = false;
            slider.fillRect = (RectTransform)fill.transform;
            slider.handleRect = (RectTransform)handle.transform;
            slider.targetGraphic = handle.GetComponent<Image>();
            row.GetComponent<CafeBgmVolumeSetting>().Bind(music, slider, value);
        }
        private TMP_Text Text(Transform parent,string name,string chinese,string english,float size)
        {
            var text=New(name,parent,typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            text.font=font;text.fontSize=size;text.color=SettingsPanelTheme.Ink;
            text.alignment=TextAlignmentOptions.MidlineLeft;text.raycastTarget=false;
            labels.Add((text,chinese,english));text.text=SettingsUiLanguage.T(chinese,english);return text;
        }
        private void Title(Transform title,Transform page)
        {
            title.SetParent(page,false);
            Rect(title,new Vector2(.09f,1),new Vector2(.91f,1),new Vector2(0,-64),new Vector2(0,-8));
            var text=title.GetComponent<TMP_Text>();text.fontSize=30;text.fontStyle=FontStyles.Bold;
            text.alignment=TextAlignmentOptions.MidlineLeft;
        }
        private Transform Scroll(Transform page)
        {
            var go=New("SettingsScroll",page,typeof(ScrollRect));
            Rect(go.transform,new Vector2(.09f,0),new Vector2(.92f,1),new Vector2(0,112),new Vector2(0,-76));
            var viewport=New("Viewport",go.transform,typeof(Image),typeof(RectMask2D));
            Rect(viewport.transform,Vector2.zero,Vector2.one,Vector2.zero,new Vector2(-22,0));
            viewport.GetComponent<Image>().color=new Color(1,1,1,.001f);
            var content=New("Content",viewport.transform,typeof(VerticalLayoutGroup),typeof(ContentSizeFitter));
            Rect(content.transform,new Vector2(0,1),Vector2.one,Vector2.zero,Vector2.zero);
            ((RectTransform)content.transform).pivot=new Vector2(.5f,1);
            Stack(content);var scroll=go.GetComponent<ScrollRect>();
            scroll.viewport=(RectTransform)viewport.transform;scroll.content=(RectTransform)content.transform;
            scroll.horizontal=false;scroll.scrollSensitivity=35;scroll.movementType=ScrollRect.MovementType.Clamped;
            Bar(scroll);return content.transform;
        }
        private static void Stack(GameObject go)
        {
            var layout=go.GetComponent<VerticalLayoutGroup>();layout.spacing=SettingsPanelTheme.Spacing;
            layout.padding=new RectOffset(0,0,4,12);layout.childControlWidth=layout.childControlHeight=true;
            layout.childForceExpandWidth=true;layout.childForceExpandHeight=false;
            go.GetComponent<ContentSizeFitter>().verticalFit=ContentSizeFitter.FitMode.PreferredSize;
        }
        private void Bar(ScrollRect scroll)
        {
            var bar=New("SettingsScrollbar",scroll.transform,typeof(Image),typeof(Scrollbar));
            Rect(bar.transform,new Vector2(1,0),Vector2.one,new Vector2(-10,0),Vector2.zero);
            SettingsPanelTheme.Surface(bar.GetComponent<Image>(),SettingsPanelTheme.Row);
            var thumb=New("Handle",bar.transform,typeof(Image));
            Rect(thumb.transform,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            SettingsPanelTheme.Surface(thumb.GetComponent<Image>(),SettingsPanelTheme.Muted);
            var sb=bar.GetComponent<Scrollbar>();sb.handleRect=(RectTransform)thumb.transform;
            sb.targetGraphic=thumb.GetComponent<Image>();sb.direction=Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar=sb;scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
        }
        private Transform Section(Transform parent,string chinese,string english)
        {
            var text=Text(parent,"Section",chinese,english,23);text.fontStyle=FontStyles.Bold;
            Element(text.gameObject,46);return text.transform;
        }
        private Transform Row(Transform parent,Transform label,Transform control,float height=SettingsPanelTheme.RowHeight)
        {
            var row=New("SettingRow",parent,typeof(Image),typeof(LayoutElement));
            SettingsPanelTheme.Surface(row.GetComponent<Image>(),SettingsPanelTheme.Row);
            row.GetComponent<Image>().raycastTarget=false;Element(row,height);
            if(label!=null)
            {
                label.SetParent(row.transform,false);
                Rect(label,Vector2.zero,new Vector2(.38f,1),new Vector2(32,6),new Vector2(-12,-6));
                var text=label.GetComponent<TMP_Text>();text.fontSize=21;text.fontStyle=FontStyles.Bold;
                text.alignment=TextAlignmentOptions.MidlineLeft;
            }
            control.SetParent(row.transform,false);
            bool toggle=control.GetComponent<Toggle>()!=null;
            Rect(control,new Vector2(toggle?1:.40f,0),Vector2.one,new Vector2(toggle?-110:0,12),new Vector2(-28,-12));
            if(control.GetComponent<TMP_Text>() is TMP_Text value)
            {value.fontSize=21;value.alignment=TextAlignmentOptions.MidlineRight;}
            return row.transform;
        }
        private void Footer(Transform page,Transform status,params Transform[] buttons)
        {
            var footer=New("SettingsFooter",page,typeof(Image));
            Rect(footer.transform,Vector2.zero,new Vector2(1,0),Vector2.zero,new Vector2(0,99));
            footer.GetComponent<Image>().color=SettingsPanelTheme.Paper;
            if(status!=null)
            {
                status.SetParent(footer.transform,false);
                Rect(status,Vector2.zero,new Vector2(1,1),new Vector2(40,12),new Vector2(-40,-65));
                var text=status.GetComponent<TMP_Text>();text.enableAutoSizing=false;text.fontSize=21;text.color=SettingsPanelTheme.Ink;text.alignment=TextAlignmentOptions.MidlineLeft;
            }
            var actions=New("FooterActions",footer.transform,typeof(HorizontalLayoutGroup));
            Rect(actions.transform,new Vector2(.94f-.13f*buttons.Length,0),new Vector2(.94f,0),new Vector2(0,10),new Vector2(0,58));
            var layout=actions.GetComponent<HorizontalLayoutGroup>();layout.spacing=12;
            layout.childControlWidth=layout.childControlHeight=true;layout.childForceExpandWidth=true;
            foreach(var button in buttons){button.SetParent(actions.transform,false);Element(button.gameObject,48).flexibleWidth=1;}
        }
        private void Overview(GameObject page)
        {
            Title(Find("OverviewTitle"),page.transform);var content=Scroll(page.transform);
            Section(content,"运行概况","Runtime overview");
            string[] names={"Backend","Main LLM","Embedding","Provider Health","Restart Required"};
            string[] chinese={"后端","主模型","Embedding","服务状态","需要重启"};
            string[] english={"Backend","Main model","Embedding","Provider health","Restart required"};
            for(int i=0;i<names.Length;i++)
            {
                var value=Find(names[i]);
                var label=Text(content,"OverviewLabel",chinese[i],english[i],21);
                Row(content,label.transform,value);
            }
            FormatOverview();
            Section(content,"界面","Interface");
            var language=FindRuntime(page.transform,"LanguageSelector");
            Row(content,language.Find("LanguageLabel"),language.Find("LanguageDropdown"));
            language.gameObject.SetActive(false);
            var refresh=New("RefreshStatus",page.transform,typeof(Image),typeof(Button));
            var caption=Text(refresh.transform,"Caption","刷新状态","Refresh status",20);
            Rect(caption.transform,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);caption.alignment=TextAlignmentOptions.Center;
            Footer(page.transform,null,refresh.transform);
            refresh.GetComponent<Button>().onClick.AddListener(controller.ShowOverview);
        }
        private void Model(GameObject page)
        {
            var form=Find("ModelConfigForm");Rect(form,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            form.GetComponent<Image>().enabled=false;Title(form.Find("Title"),page.transform);
            var scroll=form.Find("ScrollView").GetComponent<ScrollRect>();
            Rect(scroll.transform,new Vector2(.09f,0),new Vector2(.92f,1),new Vector2(0,112),new Vector2(0,-76));
            Rect(scroll.viewport,Vector2.zero,Vector2.one,Vector2.zero,new Vector2(-22,0));
            Stack(scroll.content.gameObject);scroll.content.GetComponent<VerticalLayoutGroup>().padding=new RectOffset(0,0,4,12);
            scroll.movementType=ScrollRect.MovementType.Clamped;Bar(scroll);
            var modelRows=scroll.content.GetComponentsInChildren<HorizontalLayoutGroup>(true);
            foreach(var layout in modelRows)
            {
                var row=layout.transform;var label=row.Find("Label");if(label==null)continue;
                var children=new List<Transform>();foreach(Transform child in row)children.Add(child);
                layout.enabled=false;
                var image=row.GetComponent<Image>()??row.gameObject.AddComponent<Image>();
                SettingsPanelTheme.Surface(image,SettingsPanelTheme.Row);image.raycastTarget=false;
                Element(row.gameObject,SettingsPanelTheme.RowHeight);
                Rect(label,Vector2.zero,new Vector2(.38f,1),new Vector2(32,6),new Vector2(-12,-6));
                label.GetComponent<TMP_Text>().fontSize=21;label.GetComponent<TMP_Text>().fontStyle=FontStyles.Bold;
                foreach(var child in children)
                {
                    if(child==label)continue;
                    if(child.TryGetComponent<LayoutElement>(out var element))element.ignoreLayout=true;
                    bool toggle=child.GetComponent<Toggle>()!=null;
                    Rect(child,new Vector2(toggle?1:.40f,0),Vector2.one,new Vector2(toggle?-110:0,12),new Vector2(-28,-12));
                }
            }
            // Preserve row ownership used by the form's language and provider logic.
            foreach(var stack in scroll.content.GetComponentsInChildren<VerticalLayoutGroup>(true))
            {stack.spacing=SettingsPanelTheme.Spacing;stack.childForceExpandHeight=false;}
            var mainTitle=scroll.content.Find("Main LLM");
            if(mainTitle!=null)
            {
                var text=mainTitle.GetComponent<TMP_Text>();text.fontSize=23;text.fontStyle=FontStyles.Bold;
                labels.Add((text,"主模型","Main model"));Element(mainTitle.gameObject,46);
            }
            var embeddingTitle=scroll.content.Find("Embedding");
            if(embeddingTitle!=null)
            {
                var text=embeddingTitle.GetComponent<TMP_Text>();text.fontSize=23;text.fontStyle=FontStyles.Bold;
                labels.Add((text,"Embedding 配置","Embedding settings"));Element(embeddingTitle.gameObject,46);
            }
            var temperature=scroll.content.Find("TemperatureRow");
            if(temperature!=null)
            {
                var heading=Section(scroll.content,"生成参数","Generation parameters");
                heading.name="GenerationParameters";
                heading.SetSiblingIndex(temperature.GetSiblingIndex());
                TemperatureSlider(temperature);
            }
            CombineActions(scroll.content,"获取主模型Row","测试主模型Row","主模型操作","Main model actions");
            var embed=scroll.content.Find("EmbeddingFields");
            if(embed!=null)CombineActions(embed,"获取 Embedding 模型Row","测试 EmbeddingRow","Embedding 操作","Embedding actions");
            var actions=form.Find("Actions");var buttons=new List<Transform>();foreach(Transform child in actions)buttons.Add(child);
            Footer(page.transform,form.Find("Status"),buttons.ToArray());actions.gameObject.SetActive(false);
        }
        private void CombineActions(Transform parent,string first,string second,string chinese,string english)
        {
            var a=parent.Find(first);var b=parent.Find(second);if(a==null||b==null)return;
            var buttonA=a.GetComponentInChildren<Button>(true);var buttonB=b.GetComponentInChildren<Button>(true);
            var label=a.Find("Label").GetComponent<TMP_Text>();labels.Add((label,chinese,english));
            buttonB.transform.SetParent(a,false);
            Rect(buttonA.transform,new Vector2(.4f,0),new Vector2(.69f,1),new Vector2(0,12),new Vector2(-10,-12));
            Rect(buttonB.transform,new Vector2(.69f,0),Vector2.one,new Vector2(0,12),new Vector2(-28,-12));
            b.gameObject.SetActive(false);
        }
        private void TemperatureSlider(Transform row)
        {
            var input=row.GetComponentInChildren<TMP_InputField>();
            Rect(input.transform,new Vector2(.82f,0),Vector2.one,new Vector2(0,12),new Vector2(-28,-12));
            var root=New("TemperatureSlider",row,typeof(Slider),typeof(SettingsNumericSlider));
            Rect(root.transform,new Vector2(.4f,0),new Vector2(.79f,1),new Vector2(0,12),new Vector2(0,-12));
            var track=New("Track",root.transform,typeof(Image));
            Rect(track.transform,new Vector2(0,.5f),new Vector2(1,.5f),new Vector2(0,-5),new Vector2(0,5));
            SettingsPanelTheme.Surface(track.GetComponent<Image>(),SettingsPanelTheme.Row*new Color(.9f,.9f,.9f,1));
            var fill=New("Fill",track.transform,typeof(Image));
            Rect(fill.transform,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
            SettingsPanelTheme.Surface(fill.GetComponent<Image>(),SettingsPanelTheme.Accent);
            var handleArea=New("HandleArea",root.transform);
            Rect(handleArea.transform,new Vector2(0,.5f),new Vector2(1,.5f),Vector2.zero,Vector2.zero);
            var handle=New("Handle",handleArea.transform,typeof(Image));
            Rect(handle.transform,new Vector2(0,.5f),new Vector2(0,.5f),new Vector2(-14,-14),new Vector2(14,14));
            SettingsPanelTheme.Surface(handle.GetComponent<Image>(),SettingsPanelTheme.Accent);
            var slider=root.GetComponent<Slider>();slider.minValue=0;slider.maxValue=2;
            slider.fillRect=(RectTransform)fill.transform;slider.handleRect=(RectTransform)handle.transform;
            slider.targetGraphic=handle.GetComponent<Image>();root.GetComponent<SettingsNumericSlider>().Bind(slider,input);
        }
        private void Npc(GameObject page)
        {
            Title(Find("NpcTitle"),page.transform);var content=Scroll(page.transform);
            Section(content,"当前角色","Selected character");
            Row(content,Find("NpcPickerLabel"),Find("NpcPicker"));Row(content,Find("NpcIdLabel"),Find("NpcId"));
            Row(content,Find("NameLabel"),Find("DisplayName"));Section(content,"角色背景","Character background");
            Row(content,Find("RoleLabel"),Find("RoleSummary"),160);
            Row(content,Find("BackstoryLabel"),Find("Backstory"),190);
            Section(content,"人格与表达","Personality & communication");
            Row(content,Find("PersonalityLabel"),Find("Personality"),190);
            Row(content,Find("StyleLabel"),Find("SpeakingStyle"),160);
            Row(content,Find("BoundariesLabel"),Find("Boundaries"),190);
            Footer(page.transform,Find("NpcStatus"),Find("RestoreDefault"),Find("SaveNpc"));
        }
        private void Debug(GameObject page)
        {
            Title(Find("DebugTitle"),page.transform);var content=Scroll(page.transform);
            Row(content,Find("DebugPickerLabel"),Find("DebugNpcPicker"));
            Readout(content,Find("SelectedNpcReadout"));Readout(content,Find("PerceptionReadout"));
            Section(content,"可视化","Visualization");
            foreach(string name in new[]{"VisionToggle","ProximityToggle","EntityIdsToggle","PerceivedToggle"})
                Row(content,Find(name+"Label"),Find(name));
            Footer(page.transform,null);
        }
        private void AddHearingControl()
        {
            var original=Find("VisionToggle");if(original==null)return;
            var content=original.parent.parent;
            var hearing = FindRuntime(content,"HearingToggle")?.GetComponent<Toggle>();
            if (hearing == null)
            {
                hearing = UnityEngine.Object.Instantiate(original.GetComponent<Toggle>(),content);
                hearing.name = "HearingToggle"; hearing.onValueChanged = new Toggle.ToggleEvent(); hearing.SetIsOnWithoutNotify(false);
                var label = Text(content,"HearingToggleLabel","显示潜在听觉范围","Show potential hearing range",21);
                Row(content,label.transform,hearing.transform);
            }
            var summary = FindRuntime(content,"PerceptionSensorSummary")?.GetComponent<TMP_Text>();
            if (summary == null)
            {
                summary = Text(content,"PerceptionSensorSummary","","",21);
                labels.RemoveAll(x => x.text == summary); // Dynamic sensor state owns this text, not static localization.
                Readout(content, summary.transform);
                summary.fontSize=21;
            }
            // Keep the five controls ahead of the long runtime readouts, also in the shipped prefab.
            string[] names={"VisionToggle","ProximityToggle","EntityIdsToggle","PerceivedToggle","HearingToggle"};
            FindRuntime(content,"Section")?.SetSiblingIndex(1);
            for(int i=0;i<names.Length;i++)
            {
                var row=FindRuntime(content,names[i])?.parent;
                if(row==null)continue;
                row.SetSiblingIndex(i+2);Element(row.gameObject,60);
            }
            summary.transform.parent.SetSiblingIndex(7);
            controller.GetComponent<CafeNpcDebugPanel>()?.BindVisualizationControls(hearing,summary);
        }
        private void Readout(Transform parent,Transform text)
        {
            var go=New("Readout",parent,typeof(Image),typeof(VerticalLayoutGroup));
            SettingsPanelTheme.Surface(go.GetComponent<Image>(),SettingsPanelTheme.Row);go.GetComponent<Image>().raycastTarget=false;
            var layout=go.GetComponent<VerticalLayoutGroup>();layout.padding=new RectOffset(24,24,12,12);
            layout.childControlWidth=layout.childControlHeight=true;layout.childForceExpandHeight=false;
            text.SetParent(go.transform,false);var label=text.GetComponent<TMP_Text>();
            label.enableAutoSizing=false;label.fontSize=24;label.color=SettingsPanelTheme.Ink;label.alignment=TextAlignmentOptions.TopLeft;label.enableWordWrapping=true;
            // TMP preferred height propagates through the layout group as state changes.
        }
        private void Trace(GameObject page)
        {
            Title(Find("TraceTitle"),page.transform);
            var controls=New("TraceControls",page.transform,typeof(VerticalLayoutGroup));
            Rect(controls.transform,new Vector2(.09f,1),new Vector2(.91f,1),new Vector2(0,-226),new Vector2(0,-76));
            var layout=controls.GetComponent<VerticalLayoutGroup>();layout.spacing=12;layout.childControlWidth=layout.childControlHeight=true;layout.childForceExpandHeight=false;
            Row(controls.transform,Find("FilterLabel"),Find("TraceCategoryFilter"));
            Row(controls.transform,Find("AutoScrollLabel"),Find("AutoScroll"));
            var scroll=Find("TraceScrollView").GetComponent<ScrollRect>();
            Rect(scroll.transform,new Vector2(.09f,0),new Vector2(.92f,1),new Vector2(0,112),new Vector2(0,-246));
            SettingsPanelTheme.Surface(scroll.GetComponent<Image>(),SettingsPanelTheme.Row);
            Rect(scroll.viewport,Vector2.zero,Vector2.one,new Vector2(24,14),new Vector2(-30,-14));
            if(scroll.verticalScrollbar!=null)scroll.verticalScrollbar.gameObject.SetActive(false);
            Bar(scroll);
            var entries=scroll.content.GetComponent<TMP_Text>();entries.fontSize=18;
            entries.margin=new Vector4(24,16,24,16);
            Footer(page.transform,Find("TraceStatus"),Find("PauseView"),Find("ClearView"),Find("OpenLogFolder"));
        }
        public void Select(GameObject selected)
        {
            playerNavigation?.SelectExisting(selected);
            foreach(var entry in pages)
            {
                bool active=entry.page==selected;
                SettingsPanelTheme.Surface(entry.button.targetGraphic as Image,active?SettingsPanelTheme.Accent:SettingsPanelTheme.Navigation);
                foreach(var text in entry.button.GetComponentsInChildren<TMP_Text>(true))text.color=active?SettingsPanelTheme.Ink:SettingsPanelTheme.Field;
                var icon=entry.button.GetComponentInChildren<SettingsNavigationIcon>();if(icon!=null)icon.color=active?SettingsPanelTheme.Ink:SettingsPanelTheme.Field;
            }
        }
        public void RefreshLanguage()
        {
            foreach(var label in labels)label.text.text=SettingsUiLanguage.T(label.chinese,label.english);
            var close=Find("Close").GetComponentInChildren<TMP_Text>(true);close.text="×";
            SettingsPanelTheme.Surface(Find("Close").GetComponent<Image>(),SettingsPanelTheme.Navigation);close.color=SettingsPanelTheme.Field;
            foreach(var entry in pages)if(entry.page.activeSelf){Select(entry.page);break;}
            playerNavigation?.RefreshLanguage();
        }
        public void FormatOverview()
        {
            string[] names={"Backend","Main LLM","Embedding","Provider Health","Restart Required"};
            string[] chinese={"后端：","主模型：","Embedding：","服务状态：","需要重启："};
            string[] english={"Backend:","Main model:","Embedding:","Provider health:","Restart required:"};
            for(int i=0;i<names.Length;i++)
            {
                var text=Find(names[i]).GetComponent<TMP_Text>();
                // Strip only the presentation label; model IDs and URLs can contain colons.
                string prefix=text.text.StartsWith(chinese[i],StringComparison.Ordinal)?chinese[i]:
                    text.text.StartsWith(english[i],StringComparison.Ordinal)?english[i]:null;
                if(prefix!=null)text.text=text.text.Substring(prefix.Length).TrimStart();
            }
        }
        public static Transform FindRuntime(Transform root,string name)
        {
            foreach(var t in root.GetComponentsInChildren<Transform>(true))if(t.name==name)return t;return null;
        }
        private static LayoutElement Element(GameObject go,float height)
        {
            var element=go.GetComponent<LayoutElement>()??go.AddComponent<LayoutElement>();
            element.preferredHeight=height;element.minHeight=height;return element;
        }
        private static void Rect(Transform t,Vector2 min,Vector2 max,Vector2 offsetMin,Vector2 offsetMax)
        {
            var rect=(RectTransform)t;rect.anchorMin=min;rect.anchorMax=max;rect.offsetMin=offsetMin;rect.offsetMax=offsetMax;
        }
    }
}
