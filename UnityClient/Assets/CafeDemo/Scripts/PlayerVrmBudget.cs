using System;
using System.IO;
using System.Linq;
using UniGLTF;
using Unity.Collections;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Checks UniGLTF's parsed data before resource allocation; never parses VRM/glTF itself.</summary>
    public static class PlayerVrmBudget
    {
        // Material splits inflate UniGLTF's temporary capacity; allow headroom without changing the loaded-mesh budget.
        public const long MaxImportVertexCapacity = 1200000;
        public static void Validate(GltfData data)
        {
            var g = data.GLTF;
            if (g.nodes.Count > 1024 || g.meshes.Count > 32 || g.materials.Count > 64 || g.images.Count > 64 ||
                g.textures.Count > 64 || g.accessors.Count > 4096 || g.animations.Count > 0 || g.cameras.Count > 0)
                throw new InvalidDataException("Avatar exceeds the node/mesh/material budget, or contains embedded animations/cameras.");
            if (g.buffers.Any(b => !string.IsNullOrEmpty(b.uri)) || g.images.Any(i => !string.IsNullOrEmpty(i.uri)))
                throw new InvalidDataException("Only self-contained binary VRM files are supported; external/data URI resources are disabled.");
            long elements = 0;
            foreach (var a in g.accessors)
            { if (a.count < 0 || a.count > 1000000) throw new InvalidDataException("Avatar accessor exceeds the resource budget."); elements += a.count; }
            if (elements > 16000000) throw new InvalidDataException("Avatar geometry exceeds the resource budget.");
            long declaredVertices = 0, declaredIndices = 0;
            int AccessorCount(int index)
            {
                if (index < 0 || index >= g.accessors.Count) throw new InvalidDataException("Invalid mesh accessor.");
                return g.accessors[index].count;
            }
            foreach (var mesh in g.meshes)
            {
                if (mesh.primitives.Count > 64) throw new InvalidDataException("Too many avatar mesh primitives.");
                foreach (var primitive in mesh.primitives)
                {
                    if (primitive.attributes == null || primitive.targets.Count > 256)
                        throw new InvalidDataException("Invalid mesh or too many avatar blend shapes.");
                    declaredVertices += AccessorCount(primitive.attributes.POSITION);
                    declaredIndices += primitive.indices >= 0 ? AccessorCount(primitive.indices) : AccessorCount(primitive.attributes.POSITION);
                }
            }
            if (declaredVertices > MaxImportVertexCapacity || declaredIndices > 1800000)
                // UniGLTF's temporary vertex capacity also sums primitives, even for shared vertex buffers.
                // Keep this allocation guard; the count is not the final rendered vertex count.
                throw new InvalidDataException(EmbodiedNPC.Settings.SettingsUiLanguage.T(
                    $"模型超出导入几何预算（顶点容量 {declaredVertices}/{MaxImportVertexCapacity}，索引 {declaredIndices}/1800000）。请简化模型后重试。",
                    $"Avatar geometry exceeds the import budget (vertex capacity {declaredVertices}/{MaxImportVertexCapacity}, indices {declaredIndices}/1800000). Simplify the model and retry."));
            long pixels = 0;
            for (int i = 0; i < g.images.Count; i++)
            {
                var image = data.GetBytesFromImage(i) ?? throw new InvalidDataException("Missing avatar image.");
                var size = ImageSize(image.binary, image.mimeType);
                if (size.x <= 0 || size.y <= 0 || size.x > 4096 || size.y > 4096)
                    throw new InvalidDataException("Avatar textures must be PNG/JPEG, at most 4096 × 4096.");
                pixels += (long)size.x * size.y;
            }
            if (pixels > 64L * 1024 * 1024) throw new InvalidDataException("Avatar textures exceed the 64 megapixel budget.");
        }
        private static Vector2Int ImageSize(NativeArray<byte> b, string mime)
        {
            int U16(int p) => (b[p] << 8) | b[p + 1];
            int U32(int p) => (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            if (mime == "image/png" && b.Length >= 24 && b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71 &&
                b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10 && U32(8) == 13 && U32(12) == 0x49484452)
                return new Vector2Int(U32(16), U32(20));
            if (mime == "image/jpeg" && b.Length >= 4 && b[0] == 255 && b[1] == 216)
            {
                int p = 2;
                while (p + 4 < b.Length)
                {
                    if (b[p++] != 255) break;
                    while (p < b.Length && b[p] == 255) p++;
                    if (p >= b.Length) break;
                    int marker = b[p++];
                    if (marker == 0xD9 || marker == 0xDA) break;
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                    if (p + 2 > b.Length) break;
                    int length = U16(p);
                    if (length < 2 || length > b.Length - p) break;
                    if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                        (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                    { if (length < 7) break; return new Vector2Int(U16(p + 5), U16(p + 3)); }
                    p += length;
                }
            }
            throw new InvalidDataException("Unsupported or invalid avatar image header (PNG/JPEG required).");
        }
        public static Bounds ValidateLoaded(GameObject model, RuntimeGltfInstance resources)
        {
            if (resources == null || model.GetComponentsInChildren<Animator>(true).Length != 1 ||
                model.GetComponentsInChildren<Transform>(true).Length > 2048 ||
                model.GetComponentsInChildren<Camera>(true).Length != 0 || model.GetComponentsInChildren<AudioListener>(true).Length != 0 ||
                model.GetComponentsInChildren<Collider>(true).Length != 0 || model.GetComponentsInChildren<Rigidbody>(true).Length != 0)
                throw new InvalidDataException("Avatar contains unsupported extra animation, camera or physics components.");
            long vertices = 0, triangles = 0;
            foreach (var mesh in resources.Meshes)
            {
                vertices += mesh.vertexCount;
                if (mesh.blendShapeCount > 256) throw new InvalidDataException("Too many avatar blend shapes.");
                for (int i = 0; i < mesh.subMeshCount; i++) triangles += (long)mesh.GetIndexCount(i) / 3;
            }
            if (vertices > 300000 || triangles > 600000) throw new InvalidDataException("Avatar exceeds 300K vertices / 600K triangles.");
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                if (!float.IsFinite(t.localPosition.sqrMagnitude) || !float.IsFinite(t.localScale.sqrMagnitude) ||
                    !float.IsFinite(t.localRotation.x) || !float.IsFinite(t.localRotation.y) ||
                    !float.IsFinite(t.localRotation.z) || !float.IsFinite(t.localRotation.w) || t.localPosition.magnitude > 20)
                    throw new InvalidDataException("Avatar has invalid transforms.");
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0 || renderers.Length > 64) throw new InvalidDataException("Avatar renderer budget exceeded.");
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var size = bounds.size;
            if (!float.IsFinite(size.sqrMagnitude) || !float.IsFinite(bounds.center.sqrMagnitude) || size.y < .5f || size.y > 3f ||
                size.x > size.y * 2.5f || size.z > size.y || bounds.center.magnitude > 5)
                throw new InvalidDataException("Avatar proportions are unsupported (height must be 0.5–3 m).");
            return bounds;
        }
    }
}
