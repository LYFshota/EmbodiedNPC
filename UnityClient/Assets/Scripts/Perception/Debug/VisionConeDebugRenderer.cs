using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    public readonly struct VisionDebugSegment
    {
        public VisionDebugSegment(Vector3 start, Vector3 end)
        {
            Start = start;
            End = end;
        }

        public Vector3 Start { get; }
        public Vector3 End { get; }
    }

    /// <summary>Builds a dashed horizontal outline without changing perception state.</summary>
    public static class VisionConeDebugGeometry
    {
        public static IReadOnlyList<VisionDebugSegment> Build(float range, float halfFovDegrees,
            float dashLength, float gapLength)
        {
            var segments = new List<VisionDebugSegment>();
            if (range <= 0f || halfFovDegrees <= 0f || dashLength <= 0f) return segments;

            float clampedHalfFov = Mathf.Clamp(halfFovDegrees, 0.1f, 179f);
            float safeGap = Mathf.Max(0f, gapLength);
            AddRay(segments, Quaternion.AngleAxis(-clampedHalfFov, Vector3.up) * Vector3.forward,
                range, dashLength, safeGap);
            AddRay(segments, Vector3.forward, range, dashLength, safeGap);
            AddRay(segments, Quaternion.AngleAxis(clampedHalfFov, Vector3.up) * Vector3.forward,
                range, dashLength, safeGap);
            AddArc(segments, range, clampedHalfFov, dashLength, safeGap);
            return segments;
        }

        private static void AddRay(ICollection<VisionDebugSegment> output, Vector3 direction, float range,
            float dashLength, float gapLength)
        {
            float stride = dashLength + gapLength;
            for (float start = 0f; start < range; start += stride)
            {
                float end = Mathf.Min(start + dashLength, range);
                output.Add(new VisionDebugSegment(direction * start, direction * end));
            }
        }

        private static void AddArc(ICollection<VisionDebugSegment> output, float range, float halfFovDegrees,
            float dashLength, float gapLength)
        {
            float totalAngleRadians = 2f * halfFovDegrees * Mathf.Deg2Rad;
            float arcLength = range * totalAngleRadians;
            float stride = dashLength + gapLength;
            for (float start = 0f; start < arcLength; start += stride)
            {
                float end = Mathf.Min(start + dashLength, arcLength);
                float startAngle = -halfFovDegrees + start / range * Mathf.Rad2Deg;
                float endAngle = -halfFovDegrees + end / range * Mathf.Rad2Deg;
                Vector3 startPoint = Quaternion.AngleAxis(startAngle, Vector3.up) * Vector3.forward * range;
                Vector3 endPoint = Quaternion.AngleAxis(endAngle, Vector3.up) * Vector3.forward * range;
                output.Add(new VisionDebugSegment(startPoint, endPoint));
            }
        }
    }

    /// <summary>Runtime Game-view visualization for the configured VisionSensor cone.</summary>
    [RequireComponent(typeof(VisionSensor))]
    public sealed class VisionConeDebugRenderer : MonoBehaviour
    {
        [SerializeField] private VisionSensor visionSensor;
        [SerializeField] private Material lineMaterial;
        [SerializeField] private bool visibleAtStart;
        [SerializeField, Min(0.005f)] private float lineWidth = 0.035f;
        [SerializeField, Min(0.05f)] private float dashLength = 0.35f;
        [SerializeField, Min(0f)] private float gapLength = 0.22f;
        [SerializeField] private Color lineColor = new(0.1f, 0.9f, 1f, 0.9f);

        private GameObject meshRoot;
        private Mesh runtimeMesh;

        public bool IsVisible { get; private set; }
        public int SegmentCount { get; private set; }

        private void Awake()
        {
            if (visionSensor == null) visionSensor = GetComponent<VisionSensor>();
            BuildMesh();
            SetVisible(visibleAtStart);
        }

        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool value)
        {
            IsVisible = value;
            if (meshRoot != null) meshRoot.SetActive(value);
        }

        private void BuildMesh()
        {
            if (visionSensor == null)
            {
                Debug.LogWarning("[VisionDebug] VisionSensor is missing.", this);
                return;
            }

            meshRoot = new GameObject("VisionConeDebugMesh");
            meshRoot.transform.SetParent(transform, false);
            // The sensor itself still casts from EyeHeight. Its horizontal field of view is
            // projected near the floor here so the debug footprint stays readable in Game view.
            meshRoot.transform.localPosition = Vector3.up * 0.05f;
            MeshFilter filter = meshRoot.AddComponent<MeshFilter>();
            MeshRenderer renderer = meshRoot.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = lineMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            IReadOnlyList<VisionDebugSegment> segments = VisionConeDebugGeometry.Build(
                visionSensor.EnterDistance, visionSensor.EnterHalfFovDegrees, dashLength, gapLength);
            SegmentCount = segments.Count;
            runtimeMesh = CreateMesh(segments, lineWidth, lineColor);
            filter.sharedMesh = runtimeMesh;

            if (lineMaterial == null)
                Debug.LogWarning("[VisionDebug] Line material is not configured.", this);
        }

        private static Mesh CreateMesh(IReadOnlyList<VisionDebugSegment> segments, float width, Color color)
        {
            var vertices = new Vector3[segments.Count * 4];
            var colors = new Color[vertices.Length];
            var triangles = new int[segments.Count * 12];
            float halfWidth = Mathf.Max(0.005f, width) * 0.5f;

            for (int index = 0; index < segments.Count; index++)
            {
                VisionDebugSegment segment = segments[index];
                Vector3 direction = segment.End - segment.Start;
                Vector3 side = direction.sqrMagnitude < 0.000001f
                    ? Vector3.right * halfWidth
                    : Vector3.Cross(Vector3.up, direction.normalized) * halfWidth;
                int vertex = index * 4;
                vertices[vertex] = segment.Start - side;
                vertices[vertex + 1] = segment.Start + side;
                vertices[vertex + 2] = segment.End + side;
                vertices[vertex + 3] = segment.End - side;
                colors[vertex] = colors[vertex + 1] = colors[vertex + 2] = colors[vertex + 3] = color;

                int triangle = index * 12;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 2;
                triangles[triangle + 2] = vertex + 1;
                triangles[triangle + 3] = vertex;
                triangles[triangle + 4] = vertex + 3;
                triangles[triangle + 5] = vertex + 2;
                triangles[triangle + 6] = vertex;
                triangles[triangle + 7] = vertex + 1;
                triangles[triangle + 8] = vertex + 2;
                triangles[triangle + 9] = vertex;
                triangles[triangle + 10] = vertex + 2;
                triangles[triangle + 11] = vertex + 3;
            }

            var mesh = new Mesh { name = "Runtime Vision Cone Debug Mesh" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            if (runtimeMesh == null) return;
            if (Application.isPlaying) Destroy(runtimeMesh);
            else DestroyImmediate(runtimeMesh);
        }
    }
}
