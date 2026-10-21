using System;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Explicit V2-only face wiring. Does not touch imported VRMs, personas or controllers.</summary>
public static class CafeV2NaturalInteractionSetup
{
    private const string ScenePath = "Assets/Scenes/CafeDemoV2.unity";
    public static void ReimportAndApplyBatch()
    {
        if (!Application.isBatchMode || Application.isPlaying) throw new InvalidOperationException("Batch Edit Mode required.");
        // Fresh Libraries can evaluate GLTF before package shaders have finished importing. Retry only these assets.
        foreach (string path in new[] { "Assets/CafeDemo/V2/Characters/薇薇.vrm", "Assets/CafeDemo/V2/Characters/Ao.vrm",
            "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
            "Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb", "Assets/VRMCompatibilityTest/Xingmang.vrm" })
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        ApplyBatch();
    }
    public static void ApplyBatch()
    {
        if (!Application.isBatchMode || Application.isPlaying) throw new InvalidOperationException("Batch Edit Mode required.");
        EditorSceneManager.OpenScene(ScenePath);
        Apply();
    }
    [MenuItem("Tools/CafeDemoV2/Configure natural face presentation (P0)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != ScenePath) throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode first.");
        var actors = UnityEngine.Object.FindObjectsByType<WorldEntity>()
            .Where(x => x.IsAgent(AgentRole.Npc) && x.GetComponent<ExpressiveController>() != null).ToArray();
        if (actors.Length != 4) throw new InvalidOperationException("Expected the four configured V2 NPCs.");
        foreach (var actor in actors)
        {
            var animator = actor.GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman || !animator.avatar.isValid)
                throw new InvalidOperationException("Invalid Humanoid on " + actor.EntityId);
        }
        foreach (var actor in actors)
        {
            var face = actor.GetComponent<CafeVrmExpressionPresenter>();
            if (face == null) face = Undo.AddComponent<CafeVrmExpressionPresenter>(actor.gameObject);
            var so = new SerializedObject(face);
            so.FindProperty("expressive").objectReferenceValue = actor.GetComponent<ExpressiveController>();
            so.FindProperty("animator").objectReferenceValue = actor.GetComponentInChildren<Animator>();
            // Small visual parameter differences follow the current persona assets; identity is never changed.
            so.FindProperty("smileWeight").floatValue = actor.EntityId == "npc_cafe_01" ? .18f :
                actor.EntityId == "npc_cafe_quiet_01" ? .10f : .12f;
            so.ApplyModifiedProperties();
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[NaturalInteractionP0] Four face presenters wired in CafeDemoV2.");
    }
}
