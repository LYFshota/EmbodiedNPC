using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ChatHudIcon : MaskableGraphic
    {
        public bool IsOutline;
        public bool IsCapsuleBackground;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect r = rectTransform.rect;
            if (IsOutline || IsCapsuleBackground)
            {
                const int segments = 12;
                float radius = Mathf.Min(r.width, r.height) * .5f;
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector2 centre = new Vector2(corner < 2 ? r.xMax-radius : r.xMin+radius,
                        corner == 0 || corner == 3 ? r.yMax-radius : r.yMin+radius);
                    for (int step = 0; step <= segments; step++)
                    {
                        float angle = (90 - corner * 90 - step * 90f / segments) * Mathf.Deg2Rad;
                        Vector2 normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        mesh.AddVert(centre + normal * radius, color, Vector2.zero);
                        if (IsOutline) mesh.AddVert(centre + normal * Mathf.Max(0, radius - 4), color, Vector2.zero);
                    }
                }
                int count = 4 * (segments+1);
                if (IsCapsuleBackground && !IsOutline)
                {
                    mesh.AddVert(r.center, color, Vector2.zero);
                    for (int i=0; i<count; i++) mesh.AddTriangle(count,i,(i+1)%count);
                    return;
                }
                for (int i=0; i<count; i++)
                {
                    int a=i*2, b=((i+1)%count)*2;
                    mesh.AddTriangle(a,b,a+1); mesh.AddTriangle(a+1,b,b+1);
                }
                return;
            }
            Vector2[] points = { new(.06f,.54f), new(.97f,.96f), new(.64f,.04f),
                new(.47f,.39f), new(.81f,.78f), new(.35f,.46f) };
            foreach (var p in points) mesh.AddVert(new Vector3(r.x + p.x*r.width, r.y+p.y*r.height), color, Vector2.zero);
            mesh.AddTriangle(0,1,5); mesh.AddTriangle(1,4,5);
            mesh.AddTriangle(1,2,3); mesh.AddTriangle(1,3,4);
        }
    }
}
