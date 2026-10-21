using System;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>V2-only authoring of a small original Humanoid clip; never edits imported VRM bones or gameplay configuration.</summary>
public static class CafeDrinkPreparationVisualSetup
{
    private const string Folder = "Assets/CafeDemo/Animations/";
    [MenuItem("Tools/CafeDemoV2/Configure drink preparation visual (P1)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/Scenes/CafeDemoV2.unity")
            throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode first.");
        var merchant = UnityEngine.Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "npc_cafe_01");
        var animator = merchant.GetComponentsInChildren<Animator>().Single(a => a.isHuman && a.gameObject.activeInHierarchy);
        var controller = animator.runtimeAnimatorController as AnimatorController;
        if (controller == null) throw new InvalidOperationException("Existing Humanoid AnimatorController required.");
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Folder + "CafePrepareDrink.anim");
        if (clip == null) { clip = new AnimationClip { name = "CafePrepareDrink", frameRate = 30 }; AssetDatabase.CreateAsset(clip, Folder + "CafePrepareDrink.anim"); }
        // Arms reach to the work surface, the left hand steadies, the right makes two small operating strokes, then both lower.
        Curve(clip, "Left Arm Down-Up", -.65f, -.30f, -.30f, -.30f, -.30f, -.65f);
        Curve(clip, "Right Arm Down-Up", -.65f, -.10f, -.24f, -.10f, -.24f, -.65f);
        Curve(clip, "Left Arm Front-Back", .05f, -.20f, -.20f, -.20f, -.20f, .05f);
        Curve(clip, "Right Arm Front-Back", .05f, -.28f, -.42f, -.28f, -.42f, .05f);
        Curve(clip, "Left Arm Twist In-Out", 0, 0, 0, 0, 0, 0);
        Curve(clip, "Right Arm Twist In-Out", 0, 0, -.08f, .08f, -.08f, 0);
        Curve(clip, "Left Forearm Stretch", .65f, .40f, .40f, .40f, .40f, .65f);
        Curve(clip, "Right Forearm Stretch", .65f, .55f, .05f, .55f, .05f, .65f);
        Curve(clip, "Right Hand Down-Up", 0, -.08f, .14f, -.12f, .14f, 0);
        Curve(clip, "Left Hand Down-Up", 0, -.08f, -.08f, -.08f, -.08f, 0);
        var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(clip, settings);
        EditorUtility.SetDirty(clip);
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(Folder + "CafePreparationArms.mask");
        if (mask == null) { mask = new AvatarMask { name = "CafePreparationArms" }; AssetDatabase.CreateAsset(mask, Folder + "CafePreparationArms.mask"); }
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
        foreach (var part in new[] { AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers })
            mask.SetHumanoidBodyPartActive(part, true);
        EditorUtility.SetDirty(mask);
        if (controller.layers.All(l => l.name != DrinkPreparationPresenter.LayerName))
        {
            controller.AddLayer(DrinkPreparationPresenter.LayerName);
            var layers = controller.layers; var layer = layers[layers.Length - 1];
            layer.avatarMask = mask; layer.defaultWeight = 0;
            var empty = layer.stateMachine.AddState("Empty"); empty.writeDefaultValues = false;
            layer.stateMachine.defaultState = empty;
            var prepare = layer.stateMachine.AddState("PrepareDrink"); prepare.motion = clip; prepare.writeDefaultValues = false;
            controller.layers = layers;
        }
        EditorUtility.SetDirty(controller);
        var presentation = merchant.GetComponent<DrinkPreparationPresenter>();
        if (presentation == null) presentation = Undo.AddComponent<DrinkPreparationPresenter>(merchant.gameObject);
        var station = merchant.GetComponent<DrinkPreparationCapability>().Station;
        var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        var vessel = hand.Find("PreparationVessel_VisualOnly");
        var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Models/Food/cup-coffee.fbx");
        if (model == null) throw new InvalidOperationException("Existing cup visual model missing.");
        if (vessel == null)
        {
            vessel = ((GameObject)PrefabUtility.InstantiatePrefab(model)).transform;
            vessel.name = "PreparationVessel_VisualOnly"; vessel.SetParent(hand, false);
        }
        vessel.gameObject.SetActive(true);
        Vector3 steamPosition = Vector3.zero; Quaternion steamRotation = Quaternion.identity;
        AnimationMode.StartAnimationMode();
        try
        {
            AnimationMode.BeginSampling(); AnimationMode.SampleAnimationClip(animator.gameObject, clip, .9f); AnimationMode.EndSampling();
            // Author the visual prop's grip offset once against a sampled pose, never write runtime bones.
            vessel.rotation = merchant.transform.rotation * model.transform.localRotation;
            vessel.position = hand.position + merchant.transform.TransformDirection(new Vector3(.025f, -.06f, .03f));
            vessel.localScale = model.transform.localScale;
            vessel.localScale *= .14f / MeshBounds(vessel).size.y;
            var bounds = MeshBounds(vessel);
            steamPosition = vessel.InverseTransformPoint(bounds.center + Vector3.up * (bounds.extents.y + .01f));
            steamRotation = Quaternion.Inverse(vessel.rotation) * Quaternion.Euler(-90, 0, 0);
        }
        finally { AnimationMode.StopAnimationMode(); }
        var steamRoot = vessel.Find("PreparationSteam_VisualOnly") ?? station.transform.Find("PreparationSteam_VisualOnly");
        if (steamRoot == null) { steamRoot = new GameObject("PreparationSteam_VisualOnly").transform; steamRoot.SetParent(station.transform, false); }
        steamRoot.SetParent(vessel, false); steamRoot.localPosition = steamPosition;
        steamRoot.localRotation = steamRotation; steamRoot.localScale = Vector3.one / vessel.lossyScale.y;
        var steam = steamRoot.GetComponent<ParticleSystem>(); if (steam == null) steam = steamRoot.gameObject.AddComponent<ParticleSystem>();
        steam.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = steam.main; main.playOnAwake = false; main.loop = true; main.duration = 1;
        main.startLifetime = .7f; main.startSpeed = .16f; main.startSize = .07f;
        main.startColor = new Color(.92f, .95f, 1, .25f); main.maxParticles = 16; main.simulationSpace = ParticleSystemSimulationSpace.World;
        var emission = steam.emission; emission.rateOverTime = 12;
        var shape = steam.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 8; shape.radius = .035f;
        var color = steam.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.7f, .2f), new GradientAlphaKey(0, 1) }); color.color = gradient;
        var size = steam.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, .5f, 1, 1.6f));
        var material = SteamMaterial(); steam.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
        presentation.Configure(merchant.GetComponent<GoalPlanRunner>(), merchant.GetComponent<MotionChannelCoordinator>(),
            merchant.GetComponent<DrinkPreparationCapability>(), animator, steam, vessel.gameObject);
        EditorUtility.SetDirty(presentation);
        AssetDatabase.SaveAssetIfDirty(clip); AssetDatabase.SaveAssetIfDirty(mask); AssetDatabase.SaveAssetIfDirty(controller);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
    private static Bounds MeshBounds(Transform root)
    {
        bool found = false; var result = new Bounds();
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
        {
            var bounds = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = filter.transform.TransformPoint(bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                if (!found) { result = new Bounds(p, Vector3.zero); found = true; } else result.Encapsulate(p);
            }
        }
        if (!found || result.size.y <= 0) throw new InvalidOperationException("Preparation visual mesh is invalid.");
        return result;
    }
    private static Material SteamMaterial()
    {
        string texturePath = Folder + "CafePreparationSteam.png", materialPath = Folder + "CafePreparationSteam.mat";
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) == null)
        {
            var image = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            { float radius = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15.5f; image.SetPixel(x, y, new Color(1, 1, 1, Mathf.Pow(Mathf.Clamp01(1 - radius), 2))); }
            image.Apply(); System.IO.File.WriteAllBytes(texturePath, image.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(texturePath);
        }
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) throw new InvalidOperationException("URP particle shader unavailable.");
            material = new Material(shader) { name = "CafePreparationSteam" }; AssetDatabase.CreateAsset(material, materialPath);
        }
        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath)); material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", 1); material.SetFloat("_Blend", 0); material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); material.SetFloat("_ZWrite", 0);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.renderQueue = 3000;
        EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material); return material;
    }
    private static void Curve(AnimationClip clip, string muscle, params float[] values)
    {
        if (!HumanTrait.MuscleName.Contains(muscle)) throw new InvalidOperationException("Unknown Humanoid muscle: " + muscle);
        float[] times = { 0, .4f, .9f, 1.4f, 1.9f, 2.5f };
        var keys = times.Select((t, i) => new Keyframe(t, values[i])).ToArray();
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), new AnimationCurve(keys));
    }
}
