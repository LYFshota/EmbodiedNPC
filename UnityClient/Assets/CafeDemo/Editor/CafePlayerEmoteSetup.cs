using System;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using TMPro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>Authors only the three player emotes and their small UI. Source FBX imports are temporary.</summary>
public static class CafePlayerEmoteSetup
{
    private const string AnimationFolder = "Assets/CafeDemo/Animations/ThirdParty/KayKit/";
    private const string PrefabPath = "Assets/CafeDemo/Prefabs/UI/PlayerEmotePanel.prefab";
    private static readonly Color Cream = new Color32(248, 240, 213, 255);
    private static readonly Color Surface = new Color32(29, 42, 39, 232);

    [MenuItem("Tools/CafeDemoV2/Configure player emote panel")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/Scenes/CafeDemoV2.unity" || scene.isDirty)
            throw new InvalidOperationException("Open the saved CafeDemoV2 in Edit Mode before authoring.");
        Extract("Rig_Medium", "Cheering"); Extract("Rig_Large", "Flexing");
        var player = UnityEngine.Object.FindAnyObjectByType<CafeDemoPlayerController>();
        var emotes = player.GetComponent<PlayerEmoteController>();
        var animator = player.GetComponentInChildren<Animator>();
        var controller = animator.runtimeAnimatorController as AnimatorController;
        if (controller == null || !animator.isHuman) throw new InvalidOperationException("Existing player Humanoid controller required.");
        var layer = controller.layers.Single(l => l.name == PlayerEmoteController.LayerName);
        AddState(layer.stateMachine, "Cheer", AnimationFolder + "Cheering.anim");
        AddState(layer.stateMachine, "Proud", AnimationFolder + "Flexing.anim");
        EditorUtility.SetDirty(controller); AssetDatabase.SaveAssetIfDirty(controller);
        var canvas = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Single(c => c.name == "CognitiveDialogueUI");
        if (UnityEngine.Object.FindAnyObjectByType<PlayerEmotePanel>() != null)
            throw new InvalidOperationException("Panel already exists; do not duplicate the scene instance.");
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/NotoSansCJKsc/NotoSansCJKsc-Dynamic.asset");
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/CafeDemo/Resources/CafeUI/RoundedSurface.png");
        if (font == null || sprite == null) throw new InvalidOperationException("Existing CJK font and rounded surface required.");
        GameObject root = BuildPrefab(font, sprite);
        GameObject asset;
        try { asset = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath); }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, canvas.transform);
        var rect = (RectTransform)instance.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var panel = instance.GetComponent<PlayerEmotePanel>();
        Ref(panel, "emotes", emotes); Ref(panel, "player", player);
        Ref(panel, "orbit", UnityEngine.Object.FindAnyObjectByType<CafeDemoOrbitCamera>());
        Ref(panel, "interactor", player.GetComponent<PlayerDialogueInteractor>());
        Ref(panel, "dialogue", UnityEngine.Object.FindAnyObjectByType<DialogueController>());
        Ref(panel, "settings", UnityEngine.Object.FindAnyObjectByType<CafeDemoDeveloperPanel>());
        Ref(panel, "inputActions", AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions"));
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }

    private static void Extract(string rig, string clipName)
    {
        string output = AnimationFolder + clipName + ".anim";
        if (AssetDatabase.LoadAssetAtPath<AnimationClip>(output) != null) return;
        string source = Path.GetFullPath("../SceneResources/KayKit_Character_Animations_1.1/Animations/fbx/" + rig + "/" + rig + "_Simulation.fbx");
        if (!File.Exists(source)) throw new FileNotFoundException("Existing local KayKit source required.", source);
        string folderName = "EmoteImport_" + Guid.NewGuid().ToString("N");
        string folder = "Assets/CafeDemo/Editor/" + folderName;
        AssetDatabase.CreateFolder("Assets/CafeDemo/Editor", folderName);
        string temporary = folder + "/" + rig + ".fbx";
        try
        {
            File.Copy(source, temporary);
            AssetDatabase.ImportAsset(temporary, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(temporary);
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.bakeAxisConversion = true;
            importer.importAnimation = true;
            importer.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(temporary).OfType<Avatar>().Single();
            if (!avatar.isValid || !avatar.isHuman) throw new InvalidOperationException("Invalid Humanoid source: " + rig);
            var selected = importer.defaultClipAnimations.Single(c => c.name == clipName);
            selected.loopTime = false; selected.loopPose = false;
            selected.lockRootRotation = true; selected.lockRootHeightY = true; selected.lockRootPositionXZ = true;
            selected.keepOriginalOrientation = true; selected.keepOriginalPositionY = true; selected.keepOriginalPositionXZ = true;
            importer.clipAnimations = new[] { selected }; importer.SaveAndReimport();
            var sourceClip = AssetDatabase.LoadAllAssetsAtPath(temporary).OfType<AnimationClip>().Single(c => c.name == clipName);
            if (!sourceClip.isHumanMotion) throw new InvalidOperationException("Humanoid motion required for " + clipName);
            var copy = UnityEngine.Object.Instantiate(sourceClip); copy.name = clipName;
            copy.events = Array.Empty<AnimationEvent>();
            AssetDatabase.CreateAsset(copy, output); AssetDatabase.SaveAssetIfDirty(copy);
        }
        finally
        {
            // This exact, freshly-created folder belongs to this invocation; no supplied sources are removed.
            if (!folder.StartsWith("Assets/CafeDemo/Editor/EmoteImport_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary import path.");
            AssetDatabase.DeleteAsset(folder);
        }
    }
    private static void AddState(AnimatorStateMachine machine, string name, string path)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (clip == null || !clip.isHumanMotion) throw new InvalidOperationException("Missing extracted motion " + path);
        var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == name) ?? machine.AddState(name);
        state.motion = clip; state.writeDefaultValues = false; state.speed = 1;
        if (state.transitions.Length == 0)
        {
            var exit = state.AddTransition(machine.defaultState);
            exit.hasExitTime = true; exit.exitTime = 1; exit.hasFixedDuration = true; exit.duration = .12f;
        }
    }
    private static GameObject BuildPrefab(TMP_FontAsset font, Sprite sprite)
    {
        var root = Rect("PlayerEmotePanel", null, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).gameObject;
        var script = root.AddComponent<PlayerEmotePanel>();
        var hint = Rect("EmoteHint", root.transform, Vector2.zero, Vector2.zero, new Vector2(32, 30), new Vector2(190, 62));
        hint.pivot = Vector2.zero;
        var hintButton = Button(hint, sprite, new Color32(38, 48, 39, 210));
        Icon(hint, "Smile", PlayerEmoteIcon.Symbol.Smile, new Vector2(29, 31), 38);
        var key = Rect("Keycap", hint, Vector2.zero, Vector2.zero, new Vector2(72, 31), new Vector2(30, 32));
        SurfaceImage(key, sprite, Cream); Label(key, font, "G", Vector2.zero, new Vector2(30, 32), 23, new Color32(66, 67, 47, 255), TextAlignmentOptions.Center);
        Label(hint, font, "表情", new Vector2(132, 31), new Vector2(68, 40), 25, Cream, TextAlignmentOptions.Center);
        var window = Rect("EmoteWindow", root.transform, new Vector2(1, .5f), new Vector2(1, .5f), new Vector2(-34, 10), new Vector2(330, 440));
        window.pivot = new Vector2(1, .5f);
        SurfaceImage(window, sprite, Surface);
        var shadow = window.gameObject.AddComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .2f); shadow.effectDistance = new Vector2(0, -5);
        var title = Rect("Title", window, new Vector2(0, 1), new Vector2(0, 1), new Vector2(26, -34), new Vector2(230, 42));
        title.pivot = new Vector2(0, .5f);
        var titleText = title.gameObject.AddComponent<TextMeshProUGUI>(); TextStyle(titleText, font, "表情", 29, Cream, TextAlignmentOptions.Left);
        var closeRect = Rect("Close", window, Vector2.one, Vector2.one, new Vector2(-32, -34), new Vector2(36, 36));
        var close = Button(closeRect, sprite, new Color(1, 1, 1, .07f));
        Icon(closeRect, "X", PlayerEmoteIcon.Symbol.Close, Vector2.zero, 28);
        var wave = Row(window, font, sprite, "WaveButton", "挥手", PlayerEmoteIcon.Symbol.Wave, -119);
        var cheer = Row(window, font, sprite, "CheerButton", "庆祝", PlayerEmoteIcon.Symbol.Cheer, -229);
        var proud = Row(window, font, sprite, "ProudButton", "得意", PlayerEmoteIcon.Symbol.Proud, -339);
        Ref(script, "hint", hint.gameObject); Ref(script, "window", window.gameObject);
        Ref(script, "hintButton", hintButton); Ref(script, "closeButton", close);
        Ref(script, "waveButton", wave); Ref(script, "cheerButton", cheer); Ref(script, "proudButton", proud);
        window.gameObject.SetActive(false);
        return root;
    }
    private static Button Row(RectTransform parent, TMP_FontAsset font, Sprite sprite, string name, string text, PlayerEmoteIcon.Symbol symbol, float y)
    {
        var rect = Rect(name, parent, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, y), new Vector2(282, 94));
        var button = Button(rect, sprite, new Color32(62, 77, 67, 218));
        Icon(rect, "Icon", symbol, new Vector2(-79, 0), 62);
        Label(rect, font, text, new Vector2(35, 0), new Vector2(130, 50), 27, Cream, TextAlignmentOptions.Left);
        return button;
    }
    private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = 5;
        var r = (RectTransform)go.transform; r.SetParent(parent, false); r.anchorMin = min; r.anchorMax = max; r.anchoredPosition = position; r.sizeDelta = size;
        return r;
    }
    private static Image SurfaceImage(RectTransform rect, Sprite sprite, Color color)
    {
        var image = rect.gameObject.AddComponent<Image>(); image.sprite = sprite; image.type = Image.Type.Sliced; image.color = color; return image;
    }
    private static Button Button(RectTransform rect, Sprite sprite, Color color)
    {
        var image = SurfaceImage(rect, sprite, color); var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var colors = button.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.23f, 1.2f, 1.06f);
        colors.selectedColor = new Color(1.1f, 1.1f, 1.02f); colors.pressedColor = new Color(.8f, .85f, .78f); colors.fadeDuration = .08f; button.colors = colors;
        return button;
    }
    private static void Icon(RectTransform parent, string name, PlayerEmoteIcon.Symbol symbol, Vector2 position, float size)
    {
        var rect = Rect(name, parent, new Vector2(.5f, .5f), new Vector2(.5f, .5f), position, new Vector2(size, size));
        // Hint children use coordinates from its lower-left; card children use centered coordinates.
        if (parent.name == "EmoteHint") rect.anchorMin = rect.anchorMax = Vector2.zero;
        var icon = rect.gameObject.AddComponent<PlayerEmoteIcon>(); icon.Kind = symbol; icon.color = Cream; icon.raycastTarget = false;
    }
    private static void Label(RectTransform parent, TMP_FontAsset font, string content, Vector2 position, Vector2 size, int points, Color color, TextAlignmentOptions align)
    {
        var r = Rect("Label", parent, new Vector2(.5f, .5f), new Vector2(.5f, .5f), position, size);
        if (parent.name == "EmoteHint") r.anchorMin = r.anchorMax = Vector2.zero;
        var text = r.gameObject.AddComponent<TextMeshProUGUI>(); TextStyle(text, font, content, points, color, align);
    }
    private static void TextStyle(TMP_Text text, TMP_FontAsset font, string content, int points, Color color, TextAlignmentOptions align)
    {
        text.font = font; text.text = content; text.fontSize = points; text.color = color; text.alignment = align;
        text.raycastTarget = false; text.extraPadding = false;
    }
    private static void Ref(UnityEngine.Object target, string field, UnityEngine.Object value)
    {
        var so = new SerializedObject(target); so.FindProperty(field).objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo();
    }
}
