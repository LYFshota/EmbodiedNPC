using System;
using CafeDemo;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Authors only the new menu asset in a temporary scene. Never saves CafeDemoV2.</summary>
public static class CafeUiFlowSetup
{
    public const string PrefabPath = "Assets/CafeDemo/Resources/CafeUI/CafeGameMenu.prefab";
    private static TMP_FontAsset Font => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/NotoSansCJKsc/NotoSansCJKsc-Dynamic.asset");
    [MenuItem("EmbodiedNPC/UI/Build Cafe game menu P2A")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var previous = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try
        {
            var root = Node("CafeGameMenu", null, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CafeUiFlowController));
            var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.overrideSorting = true; canvas.sortingOrder = 20;
            var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var menu = Surface("Menu", root.transform);
            var card = Card(menu.transform, new Vector2(600, 540));
            var heading = Text("Heading", card.transform, "游戏菜单", 38); Band(heading.transform, 42, 60);
            var hint = Text("WorldContinues", card.transform, "菜单打开时，咖啡馆仍会继续运行。", 20); Band(hint.transform, 112, 54);
            var resume = Button("Resume", card.transform, "继续游戏"); Band(resume.transform, 210, 66);
            var settings = Button("Settings", card.transform, "设置"); Band(settings.transform, 298, 66);
            var help = Button("Controls", card.transform, "操作说明"); Band(help.transform, 386, 66);
            var helpSurface = Surface("Help", root.transform);
            var helpCard = Card(helpSurface.transform, new Vector2(960, 880));
            var helpText = Text("ControlsText", helpCard.transform, "操作说明", 24); Band(helpText.transform, 36, 700);
            helpText.alignment = TextAlignmentOptions.TopLeft;
            var back = Button("Back", helpCard.transform, "返回游戏菜单"); Band(back.transform, 766, 64);
            var flow = root.GetComponent<CafeUiFlowController>();
            Ref(flow, "inputActions", AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions"));
            Ref(flow, "menuSurface", menu); Ref(flow, "helpSurface", helpSurface);
            Ref(flow, "resumeButton", resume); Ref(flow, "settingsButton", settings); Ref(flow, "helpButton", help); Ref(flow, "helpBack", back);
            Ref(flow, "heading", heading); Ref(flow, "hint", hint); Ref(flow, "helpText", helpText);
            menu.SetActive(false); helpSurface.SetActive(false);
            root.transform.localScale = Vector3.one;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally { EditorSceneManager.CloseScene(scene, true); SceneManager.SetActiveScene(previous); }
    }
    private static GameObject Node(string name, Transform parent, params Type[] types)
    { var go = new GameObject(name, typeof(RectTransform)); if (parent != null) go.transform.SetParent(parent, false); foreach (var type in types) go.AddComponent(type); return go; }
    private static GameObject Surface(string name, Transform parent)
    { var go = Node(name, parent, typeof(Image)); Stretch(go.transform); go.GetComponent<Image>().color = new Color(.04f,.03f,.02f,.65f); return go; }
    private static GameObject Card(Transform parent, Vector2 size)
    {
        var go = Node("Card", parent, typeof(Image)); var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(.5f,.5f); rect.sizeDelta = size;
        SettingsPanelTheme.Surface(go.GetComponent<Image>(), new Color(.97f,.94f,.85f)); return go;
    }
    private static TMP_Text Text(string name, Transform parent, string value, float size)
    {
        var t = Node(name, parent, typeof(TextMeshProUGUI)).GetComponent<TMP_Text>(); t.font = Font; t.text = value; t.fontSize = size;
        t.color = SettingsPanelTheme.Ink; t.raycastTarget = false; t.alignment = TextAlignmentOptions.Center; return t;
    }
    private static Button Button(string name, Transform parent, string caption)
    {
        var go = Node(name, parent, typeof(Image), typeof(Button)); SettingsPanelTheme.Surface(go.GetComponent<Image>(), new Color(.80f,.72f,.58f));
        var b = go.GetComponent<Button>(); b.targetGraphic = go.GetComponent<Image>(); var t = Text("Caption", go.transform, caption, 28); Stretch(t.transform); return b;
    }
    private static void Band(Transform t, float top, float height)
    { var r=(RectTransform)t; r.anchorMin=new Vector2(0,1); r.anchorMax=Vector2.one; r.offsetMin=new Vector2(44,-top-height); r.offsetMax=new Vector2(-44,-top); }
    private static void Stretch(Transform t) { var r=(RectTransform)t; r.anchorMin=Vector2.zero; r.anchorMax=Vector2.one; r.offsetMin=r.offsetMax=Vector2.zero; }
    private static void Ref(UnityEngine.Object o, string field, UnityEngine.Object value)
    { var so=new SerializedObject(o); so.FindProperty(field).objectReferenceValue=value; so.ApplyModifiedPropertiesWithoutUndo(); }
}
