using System;
using System.IO;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CafeDemo.Tests
{
    public static class MotionAdapterTestSceneSetup
    {
        public const string ScenePath = "Assets/CafeDemo/Tests/Scenes/MotionAdapter.unity";
        public const string ViviPath = "Assets/CafeDemo/V2/Characters/薇薇.vrm";
        public const string XingmangPath = "Assets/VRMCompatibilityTest/Xingmang.vrm";
        public static void ReimportAndBuild()
        {
            foreach (string path in new[] { ViviPath, XingmangPath, "Assets/CafeDemo/V2/Characters/Ao.vrm",
                "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb" })
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Build();
        }
        [MenuItem("Tools/Motion Adapter/Build isolated two-character test scene")]
        public static void Build()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Edit Mode required.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var registry = new GameObject("Offline test registry").AddComponent<EntityRegistry>();
            BuildActor("Vivi", ViviPath, "CafeV2Expressive", new Vector3(-1.2f, 0, 0), 1.65f, registry);
            BuildActor("Xingmang", XingmangPath, "CafeExpressive", new Vector3(1.2f, 0, 0), 1.85f, registry);
            var light = new GameObject("Test light").AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 1.2f; light.transform.rotation = Quaternion.Euler(35, -30, 0);
            RenderSettings.ambientLight = Color.gray;
            var camera = new GameObject("Test camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 1.1f, 4.8f); camera.transform.LookAt(new Vector3(0, .9f, 0));
            camera.backgroundColor = new Color(.16f, .18f, .22f); camera.clearFlags = CameraClearFlags.SolidColor;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            AssetDatabase.Refresh(); EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[MotionAdapter] Saved isolated scene; no Backend, cognition, tasks or formal Cafe scene edits.");
        }
        static void BuildActor(string name, string path, string controllerName, Vector3 position, float height, EntityRegistry registry)
        {
            var actor = new GameObject(name); actor.transform.position = position;
            var owner = actor.AddComponent<WorldEntity>(); owner.Configure("motion-test-" + name, "motion-test", EntityKind.Agent, AgentRole.Npc);
            var visual = new GameObject("Visual").transform; visual.SetParent(actor.transform, false);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            model.transform.SetParent(visual, false);
            var bounds = Measure(model);
            model.transform.localScale = Vector3.one * (height / bounds.size.y);
            model.transform.localPosition = Vector3.up * (-bounds.min.y * model.transform.localScale.y);
            var animator = model.GetComponent<Animator>();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/CafeDemo/Animations/" + controllerName + ".controller");
            animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var motion = actor.AddComponent<MotionChannelCoordinator>();
            var speech = actor.AddComponent<SpeechPresentationLifecycle>();
            var expressive = actor.AddComponent<ExpressiveController>();
            var gesture = actor.AddComponent<GestureController>();
            var gaze = visual.gameObject.AddComponent<HeadGazeController>();
            expressive.Configure(owner, motion, speech, null, registry, gesture);
            gaze.Configure(null, animator, visual, actor.transform); gaze.SetGesturePlayback(gesture);
            gesture.Configure(animator, motion, gaze);
            var serialized = new SerializedObject(gesture);
            serialized.FindProperty("waveController").objectReferenceValue = animator.runtimeAnimatorController;
            serialized.FindProperty("waveClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/CafeDemo/Animations/ThirdParty/KayKit/Waving.anim");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            visual.gameObject.AddComponent<FacingController>().Configure(visual, actor.transform, motion);
            actor.AddComponent<DialogueAnimationPresenter>().Configure(expressive, motion, animator);
            actor.AddComponent<LocomotionPresenter>().Configure(null, motion, animator);
            actor.AddComponent<CarryPresenter>().Configure(owner, registry, animator);
            var hold = new GameObject("HandHoldPoint").transform; hold.SetParent(actor.transform, false);
            visual.gameObject.AddComponent<CarryAnchorFollower>().Configure(animator, hold, Vector3.zero, Vector3.zero);
            actor.AddComponent<CafeVrmExpressionPresenter>().Configure(expressive, animator);
            if (controllerName == "CafeV2Expressive")
            {
                actor.AddComponent<DrinkPreparationPresenter>().Configure(null, motion, null, animator);
                actor.AddComponent<CounterCleaningPresenter>().Configure(null, motion, null, animator, null);
            }
            actor.AddComponent<HumanoidMotionAdapter>().Configure(visual, animator);
        }
        static Bounds Measure(GameObject model)
        {
            var bounds = new Bounds(); bool first = true;
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var mesh = new Mesh();
                try { renderer.BakeMesh(mesh, true);
                    foreach (var vertex in mesh.vertices)
                    { var point = model.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                        if (first) { bounds = new Bounds(point, Vector3.zero); first = false; } else bounds.Encapsulate(point); } }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
            if (first || bounds.size.y <= 0) throw new InvalidOperationException("No measurable skinned geometry.");
            return bounds;
        }
    }
}
