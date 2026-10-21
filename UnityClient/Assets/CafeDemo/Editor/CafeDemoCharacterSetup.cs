using System;
using UnityEngine;

/// <summary>Shared humanoid bounds measurement used by CafeDemoV2 authoring.</summary>
public static class CafeDemoCharacterSetup
{
    public static Bounds Measure(GameObject model)
    {
        var result = new Bounds();
        bool first = true;
        foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            try
            {
                // Include the renderer transform scale in the bake's inverse transform;
                // otherwise nested scaled FBX rigs produce inflated world-space measurements.
                renderer.BakeMesh(mesh, true);
                foreach (var vertex in mesh.vertices)
                {
                    var point = model.transform.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                    if (first) { result = new Bounds(point, Vector3.zero); first = false; }
                    else result.Encapsulate(point);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        if (first) throw new InvalidOperationException("Character has no skinned geometry.");
        return result;
    }
}
