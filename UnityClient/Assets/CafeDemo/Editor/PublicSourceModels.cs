using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Read-only checks for the six source models used by the public scenes.</summary>
public static class PublicSourceModels
{
    public static readonly string[] Paths = {
        "Assets/CafeDemo/V2/Characters/薇薇.vrm", "Assets/CafeDemo/V2/Characters/Ao.vrm",
        "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
        "Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb", "Assets/VRMCompatibilityTest/Xingmang.vrm" };

    public static Dictionary<string, string> FindInvalidModels()
    {
        var invalid = new Dictionary<string, string>();
        foreach (var path in Paths)
        {
            string problem = GetProblem(path);
            if (problem != null) invalid.Add(path, problem);
        }
        return invalid;
    }

    public static string GetProblem(string path)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (root == null) return "Model root missing (see the Asset Import log)";
        if (path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
        {
            if (!AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().Any(a => a.isHuman && a.isValid))
                return "Valid Humanoid Avatar subasset missing";
            var animators = root.GetComponentsInChildren<Animator>(true);
            if (!animators.Any(a => a.avatar != null && a.avatar.isHuman && a.avatar.isValid))
                return "Humanoid Avatar is not bound to an Animator";
        }
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return "Model has no renderers";
        foreach (var renderer in renderers)
        {
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh == null)
                return "Skinned mesh missing: " + renderer.name;
            if (renderer is MeshRenderer && (renderer.GetComponent<MeshFilter>() == null || renderer.GetComponent<MeshFilter>().sharedMesh == null))
                return "Mesh missing: " + renderer.name;
            if (renderer.sharedMaterials.Length == 0) return "Materials missing: " + renderer.name;
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null) return "Material missing: " + renderer.name;
                var shader = material.shader;
                if (shader == null || shader.name == "Hidden/InternalErrorShader" || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                    return "Shader missing, unsupported or failed: " + material.name;
            }
        }
        return null;
    }

    public static void Validate()
    {
        var invalid = FindInvalidModels();
        if (invalid.Count != 0)
            throw new InvalidOperationException("Public source model validation failed:\n" + string.Join("\n", invalid.Select(p => p.Key + ": " + p.Value)));
        Debug.Log("PUBLIC_PREVIEW_MODELS_VALIDATED: six models, five bound Humanoid avatars; meshes/materials/shaders valid (read-only)");
    }
}
