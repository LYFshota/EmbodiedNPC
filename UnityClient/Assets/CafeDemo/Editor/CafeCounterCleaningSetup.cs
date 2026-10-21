using System;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Authors only the single counter proxy, arm clip and temporary rag; leaves model/colliders/NavMesh untouched.</summary>
public static class CafeCounterCleaningSetup
{
    private const string Folder = "Assets/CafeDemo/Animations/";
    [MenuItem("Tools/CafeDemoV2/Configure counter cleaning (P0)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/Scenes/CafeDemoV2.unity") throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode.");
        var merchant = UnityEngine.Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e => e.EntityId == "npc_cafe_01");
        var root = GameObject.Find("CounterArea").transform;
        var stationTransform = root.Find("CounterCleaningStation");
        if (stationTransform == null) { stationTransform = new GameObject("CounterCleaningStation").transform; stationTransform.SetParent(root, false); }
        stationTransform.position = new Vector3(-1.62f, 1.025f, 3.18f);
        var entity = stationTransform.GetComponent<WorldEntity>() ?? stationTransform.gameObject.AddComponent<WorldEntity>();
        entity.Configure("counter_cafe_clean_01", "cafe.counter.spare_surface", EntityKind.Environment);
        var station = stationTransform.GetComponent<CounterCleaningStation>() ?? stationTransform.gameObject.AddComponent<CounterCleaningStation>();
        var approach = Anchor(stationTransform, "CleaningApproach", new Vector3(-2.08f, .0025f, 3.18f));
        var operation = Anchor(stationTransform, "WipingSurface", new Vector3(-1.62f, 1.025f, 3.18f));
        station.Configure(approach, operation, UnityEngine.Object.FindAnyObjectByType<WorldStateService>(), UnityEngine.Object.FindAnyObjectByType<EntityRegistry>());
        var cap = merchant.GetComponent<CounterCleaningCapability>() ?? merchant.gameObject.AddComponent<CounterCleaningCapability>(); cap.Configure(station);
        var profile = merchant.GetComponent<AgentActionCapabilityProfile>();
        profile.Configure(profile.AllowedActionIds.Concat(new[] { "clean_counter" }).Distinct().ToArray());
        var animator = merchant.GetComponentsInChildren<Animator>().Single(a => a.isHuman && a.gameObject.activeInHierarchy);
        var controller = animator.runtimeAnimatorController as AnimatorController;
        if (controller == null) throw new InvalidOperationException("Humanoid controller required.");
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Folder + "CafeCleanCounter.anim");
        if (clip == null) { clip = new AnimationClip { name = "CafeCleanCounter", frameRate = 30 }; AssetDatabase.CreateAsset(clip, Folder + "CafeCleanCounter.anim"); }
        // Forearms extend toward the low work surface; both arms sweep sideways, rather than holding a vessel.
        Curve(clip, "Right Arm Down-Up", -.65f, -.50f, -.31f, -.50f, -.31f, -.65f);
        Curve(clip, "Left Arm Down-Up", -.65f, -.31f, -.50f, -.31f, -.50f, -.65f);
        Curve(clip, "Right Arm Front-Back", .05f, -.58f, -.42f, -.58f, -.42f, .05f);
        Curve(clip, "Left Arm Front-Back", .05f, -.42f, -.58f, -.42f, -.58f, .05f);
        Curve(clip, "Right Forearm Stretch", .65f, .78f, .78f, .78f, .78f, .65f);
        Curve(clip, "Left Forearm Stretch", .65f, .78f, .78f, .78f, .78f, .65f);
        Curve(clip, "Right Forearm Twist In-Out", 0, -.6f, -.6f, -.6f, -.6f, 0);
        Curve(clip, "Left Forearm Twist In-Out", 0, .6f, .6f, .6f, .6f, 0);
        Curve(clip, "Right Hand Down-Up", 0, .1f, .1f, .1f, .1f, 0);
        Curve(clip, "Left Hand Down-Up", 0, .1f, .1f, .1f, .1f, 0);
        var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(clip, settings); EditorUtility.SetDirty(clip);
        if (controller.layers.All(l => l.name != CounterCleaningPresenter.LayerName))
        {
            controller.AddLayer(CounterCleaningPresenter.LayerName); var layers = controller.layers; var layer = layers[layers.Length - 1];
            layer.avatarMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(Folder + "CafePreparationArms.mask"); layer.defaultWeight = 0;
            var empty = layer.stateMachine.AddState("Empty"); empty.writeDefaultValues = false; layer.stateMachine.defaultState = empty;
            var wipe = layer.stateMachine.AddState("CleanCounter"); wipe.motion = clip; wipe.writeDefaultValues = false; controller.layers = layers;
        }
        var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var rag = hand.Find("CleaningRag_VisualOnly");
        if (rag == null)
        {
            rag = GameObject.CreatePrimitive(PrimitiveType.Cube).transform; rag.name = "CleaningRag_VisualOnly";
            UnityEngine.Object.DestroyImmediate(rag.GetComponent<Collider>()); rag.SetParent(hand, false);
        }
        var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "CafeCleaningRag.mat");
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "CafeCleaningRag" };
            material.SetColor("_BaseColor", new Color(.32f, .68f, .7f)); material.SetFloat("_Smoothness", .05f); AssetDatabase.CreateAsset(material, Folder + "CafeCleaningRag.mat");
        }
        rag.GetComponent<Renderer>().sharedMaterial = material;
        AnimationMode.StartAnimationMode();
        try
        {
            AnimationMode.BeginSampling(); AnimationMode.SampleAnimationClip(animator.gameObject, clip, .9f); AnimationMode.EndSampling();
            rag.rotation = merchant.transform.rotation; rag.position = hand.position + Vector3.down * .025f;
            rag.localScale = new Vector3(.16f, .016f, .13f) / merchant.transform.lossyScale.x / animator.transform.localScale.x;
        }
        finally { AnimationMode.StopAnimationMode(); }
        rag.gameObject.SetActive(false);
        var presenter = merchant.GetComponent<CounterCleaningPresenter>() ?? merchant.gameObject.AddComponent<CounterCleaningPresenter>();
        presenter.Configure(merchant.GetComponent<GoalPlanRunner>(), merchant.GetComponent<MotionChannelCoordinator>(), cap, animator, rag.gameObject);
        foreach (var o in new UnityEngine.Object[] { entity, station, cap, profile, presenter, controller }) EditorUtility.SetDirty(o);
        AssetDatabase.SaveAssetIfDirty(clip); AssetDatabase.SaveAssetIfDirty(controller); AssetDatabase.SaveAssetIfDirty(material);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
    private static Transform Anchor(Transform parent, string name, Vector3 position)
    { var t = parent.Find(name); if (t == null) { t = new GameObject(name).transform; t.SetParent(parent, false); } t.position = position; return t; }
    private static void Curve(AnimationClip clip, string muscle, params float[] values)
    {
        if (!HumanTrait.MuscleName.Contains(muscle)) throw new InvalidOperationException("Unknown muscle: " + muscle);
        float[] times = { 0, .4f, .9f, 1.4f, 1.9f, 2.5f };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), new AnimationCurve(times.Select((t, i) => new Keyframe(t, values[i])).ToArray()));
    }
}
