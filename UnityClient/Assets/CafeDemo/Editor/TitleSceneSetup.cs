using System;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Authors only Title assets. Never saves or rebuilds CafeDemoV2.</summary>
public static class TitleSceneSetup
{
    public const string ScenePath = "Assets/Scenes/TitleScene.unity";
    public const string UiPath = "Assets/CafeDemo/Prefabs/UI/TitleScreen.prefab";
    private static TMP_FontAsset Font => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/NotoSansCJKsc/NotoSansCJKsc-Dynamic.asset");
    private static readonly Color Cream = new Color(.97f, .94f, .84f);
    private static readonly Color Coffee = new Color(.13f, .085f, .065f, .90f);
    private static readonly Color Gold = new Color(.91f, .74f, .43f);

    [MenuItem("EmbodiedNPC/UI/Build TitleScene P0")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var previous = SceneManager.GetActiveScene();
        if (previous.isDirty) throw new InvalidOperationException("Keep the current scene's unsaved work before authoring TitleScene.");
        var backgroundImporter = (TextureImporter)AssetImporter.GetAtPath("Assets/CafeDemo/UI/TitleCafeBackground.png");
        if (backgroundImporter.npotScale != TextureImporterNPOTScale.None)
        {
            backgroundImporter.npotScale = TextureImporterNPOTScale.None;
            backgroundImporter.maxTextureSize = 2048;
            backgroundImporter.SaveAndReimport();
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try
        {
            var camera = new GameObject("TitleCamera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Coffee;
            camera.cullingMask = 0;
            var audio = new GameObject("TitleBackgroundMusic", typeof(AudioSource), typeof(CafeBackgroundMusic)).GetComponent<AudioSource>();
            audio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/CafeDemo/Audio/BGM/little_cafe.mp3");
            audio.loop = true; audio.playOnAwake = false; audio.spatialBlend = 0; audio.volume = .5f;
            var service = new GameObject("TitleModelService", typeof(BackendWebSocketClient), typeof(ModelConfigClient));
            var backend = service.GetComponent<BackendWebSocketClient>();
            Bool(backend, "connectOnStart", false);
            var client = service.GetComponent<ModelConfigClient>(); Ref(client, "backendClient", backend);
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();

            var root = Node("TitleScreen", null, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(TitleScreenController));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var bg = Node("CafeBackground", root.transform, typeof(RawImage), typeof(AspectRatioFitter));
            Stretch(bg.transform);
            bg.GetComponent<RawImage>().texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/CafeDemo/UI/TitleCafeBackground.png");
            bg.GetComponent<RawImage>().raycastTarget = false;
            bg.GetComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            bg.GetComponent<AspectRatioFitter>().aspectRatio = 16f / 9f;
            var shade = Node("ReadabilityShade", root.transform, typeof(Image)); Stretch(shade.transform);
            shade.GetComponent<Image>().color = new Color(.075f, .04f, .02f, .19f); shade.GetComponent<Image>().raycastTarget = false;
            var menu = Node("MainMenu", root.transform, typeof(Image));
            Rect(menu.transform, new Vector2(.60f,.10f), new Vector2(.96f,.90f), Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(menu.GetComponent<Image>(), Coffee);
            Band(Text("Title", menu.transform, "EmbodiedNPC", 52).transform, 32, 76);
            Band(Text("Subtitle", menu.transform, "一个拥有记忆与自主行为的 AI 咖啡馆", 24).transform, 122, 76);
            var state = Text("ConnectionStatus", menu.transform, "正在检查 AI 连接…", 27); state.color = Gold;
            Band(state.transform, 227, 50);
            var details = Text("ConnectionDetails", menu.transform, "", 18); details.color = new Color(.84f,.79f,.70f);
            Band(details.transform, 285, 110);
            var enter = Button("EnterCafe", menu.transform, "进入咖啡馆", true); Band(enter.transform, 420, 64);
            var settingsButton = Button("Settings", menu.transform, "设置"); Band(settingsButton.transform, 501, 58);
            var help = Button("Instructions", menu.transform, "操作说明"); Band(help.transform, 576, 58);
            var quit = Button("Quit", menu.transform, "退出游戏"); Band(quit.transform, 651, 58);
            var retry = Button("Retry", menu.transform, "重试连接"); Band(retry.transform, 746, 44);
            var instructions = Node("InstructionsModal", root.transform, typeof(Image)); Stretch(instructions.transform);
            instructions.GetComponent<Image>().color = new Color(.10f,.06f,.04f,.96f);
            var helpText = Text("InstructionsText", instructions.transform,
                "操作说明\n\nWASD  移动\n鼠标  控制镜头\nE  与 NPC 交互\nG  打开表情面板\nESC  设置 / 取消\n\n打开菜单时，咖啡馆仍会继续运行。", 30);
            Rect(helpText.transform, new Vector2(.3f,.25f), new Vector2(.7f,.85f), Vector2.zero, Vector2.zero);
            var helpClose = Button("CloseInstructions", instructions.transform, "返回主菜单");
            Rect(helpClose.transform, new Vector2(.38f,.13f), new Vector2(.62f,.20f), Vector2.zero, Vector2.zero);
            instructions.SetActive(false);

            var host = Node("SettingsHost", root.transform, typeof(TitleSettingsHost)); Stretch(host.transform);
            var settings = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab"), host.transform);
            settings.name = "SharedSettings";
            var form = settings.GetComponent<FirstRunModelSetup>();
            Ref(form, "client", client); Ref(form, "dialogueController", null);
            var window = settings.transform.Find("Window");
            var close = window.Find("Close").GetComponent<Button>();
            var save = (Button)new SerializedObject(form).FindProperty("saveButton").objectReferenceValue;
            Ref(host.GetComponent<TitleSettingsHost>(), "surface", settings);
            Ref(host.GetComponent<TitleSettingsHost>(), "form", form);
            Ref(host.GetComponent<TitleSettingsHost>(), "closeButton", close);
            Ref(host.GetComponent<TitleSettingsHost>(), "saveButton", save);
            settings.SetActive(false);
            var controller = root.GetComponent<TitleScreenController>();
            Ref(controller, "client", client); Ref(controller, "backend", backend); Ref(controller, "settings", host.GetComponent<TitleSettingsHost>());
            Ref(controller, "menu", menu); Ref(controller, "instructions", instructions);
            Ref(controller, "enterButton", enter); Ref(controller, "settingsButton", settingsButton); Ref(controller, "instructionsButton", help);
            Ref(controller, "quitButton", quit); Ref(controller, "retryButton", retry); Ref(controller, "instructionsClose", helpClose);
            Ref(controller, "stateText", state); Ref(controller, "detailText", details); Ref(controller, "enterCaption", enter.GetComponentInChildren<TMP_Text>());
            // The scene references the service/BGM; keep these as scene overrides on the UI instance.
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, UiPath);
            Object.DestroyImmediate(root);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            controller = instance.GetComponent<TitleScreenController>();
            Ref(controller,"client",client); Ref(controller,"backend",backend);
            var savedForm = instance.GetComponentInChildren<FirstRunModelSetup>(true); Ref(savedForm,"client",client);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true), new EditorBuildSettingsScene("Assets/Scenes/CafeDemoV2.unity", true) };
        }
        finally { EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(previous); }
    }
    private static void Volume(Transform parent, CafeBackgroundMusic music)
    {
        var row = Node("TitleBgmVolume", parent, typeof(CafeBgmVolumeSetting));
        Rect(row.transform,new Vector2(.40f,1),new Vector2(.84f,1),new Vector2(0,-88),new Vector2(0,-24));
        var label=Text("Label",row.transform,"背景音乐",20); label.color=SettingsPanelTheme.Ink; Rect(label.transform,Vector2.zero,new Vector2(.30f,1),Vector2.zero,Vector2.zero);
        var percent=Text("Percent",row.transform,"50%",20); percent.color=SettingsPanelTheme.Ink; Rect(percent.transform,new Vector2(.85f,0),Vector2.one,Vector2.zero,Vector2.zero);
        var slider=Node("Volume",row.transform,typeof(Slider)).GetComponent<Slider>();
        Rect(slider.transform,new Vector2(.32f,.3f),new Vector2(.81f,.7f),Vector2.zero,Vector2.zero);
        var track=Node("Track",slider.transform,typeof(Image)); Stretch(track.transform); SettingsPanelTheme.Surface(track.GetComponent<Image>(),new Color(.42f,.36f,.28f));
        var fill=Node("Fill",track.transform,typeof(Image)); Stretch(fill.transform); SettingsPanelTheme.Surface(fill.GetComponent<Image>(),Gold);
        var handle=Node("Handle",slider.transform,typeof(Image)); Rect(handle.transform,new Vector2(0,.5f),new Vector2(0,.5f),new Vector2(-12,-12),new Vector2(12,12));
        SettingsPanelTheme.Surface(handle.GetComponent<Image>(),Cream);
        slider.fillRect=(RectTransform)fill.transform; slider.handleRect=(RectTransform)handle.transform; slider.targetGraphic=handle.GetComponent<Image>();
        slider.minValue=0;slider.maxValue=1;
        // CafeBgmVolumeSetting holds only runtime references. The Title host binds these on enable.
        var binding=row.AddComponent<TitleVolumeBinding>();
        Ref(binding,"music",music); Ref(binding,"slider",slider); Ref(binding,"percentage",percent);
    }
    private static GameObject Node(string name,Transform parent,params Type[] types)
    {var go=new GameObject(name,typeof(RectTransform));if(parent!=null)go.transform.SetParent(parent,false);foreach(var type in types)go.AddComponent(type);return go;}
    private static TMP_Text Text(string name,Transform parent,string value,float size)
    {var text=Node(name,parent,typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();text.font=Font;text.text=value;text.fontSize=size;text.color=Cream;text.raycastTarget=false;text.alignment=TextAlignmentOptions.MidlineLeft;return text;}
    private static Button Button(string name,Transform parent,string value,bool primary=false)
    {var go=Node(name,parent,typeof(Image),typeof(Button));SettingsPanelTheme.Surface(go.GetComponent<Image>(),primary?Gold:new Color(.29f,.22f,.17f,.93f));var b=go.GetComponent<Button>();b.targetGraphic=go.GetComponent<Image>();var colors=b.colors;colors.highlightedColor=new Color(1.12f,1.08f,1);colors.selectedColor=colors.highlightedColor;colors.pressedColor=new Color(.80f,.73f,.63f);b.colors=colors;var t=Text("Caption",go.transform,value,25);t.alignment=TextAlignmentOptions.Center;if(primary)t.color=Coffee;Stretch(t.transform);return b;}
    private static void Band(Transform t,float top,float height) => Rect(t,new Vector2(0,1),Vector2.one,new Vector2(36,-top-height),new Vector2(-36,-top));
    private static void Stretch(Transform t) => Rect(t,Vector2.zero,Vector2.one,Vector2.zero,Vector2.zero);
    private static void Rect(Transform t,Vector2 min,Vector2 max,Vector2 lo,Vector2 hi){var r=(RectTransform)t;r.anchorMin=min;r.anchorMax=max;r.offsetMin=lo;r.offsetMax=hi;}
    private static void Ref(Object o,string field,Object value){var so=new SerializedObject(o);so.FindProperty(field).objectReferenceValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
    private static void Bool(Object o,string field,bool value){var so=new SerializedObject(o);so.FindProperty(field).boolValue=value;so.ApplyModifiedPropertiesWithoutUndo();}
}
