using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Small consistent line icons, independent of the font's symbol coverage.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SettingsNavigationIcon : MaskableGraphic
    {
        public int Kind;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            switch (Kind)
            {
                case 0:
                    Path(vh, new Vector2(.15f,.48f), new Vector2(.5f,.82f), new Vector2(.85f,.48f));
                    Path(vh, new Vector2(.25f,.48f), new Vector2(.25f,.18f), new Vector2(.75f,.18f), new Vector2(.75f,.48f));
                    Path(vh, new Vector2(.43f,.18f), new Vector2(.43f,.42f), new Vector2(.57f,.42f), new Vector2(.57f,.18f)); break;
                case 1:
                    Path(vh, new Vector2(.2f,.22f), new Vector2(.2f,.7f), new Vector2(.8f,.7f), new Vector2(.8f,.22f), new Vector2(.2f,.22f));
                    Path(vh, new Vector2(.5f,.7f), new Vector2(.5f,.88f));
                    Path(vh, new Vector2(.32f,.5f), new Vector2(.38f,.5f));
                    Path(vh, new Vector2(.62f,.5f), new Vector2(.68f,.5f));
                    Path(vh, new Vector2(.35f,.34f), new Vector2(.65f,.34f)); break;
                case 2:
                    Circle(vh, new Vector2(.5f,.7f), .14f);
                    Path(vh, new Vector2(.22f,.18f), new Vector2(.25f,.4f), new Vector2(.4f,.48f), new Vector2(.6f,.48f), new Vector2(.75f,.4f), new Vector2(.78f,.18f), new Vector2(.22f,.18f)); break;
                case 3:
                    Path(vh, new Vector2(.15f,.25f), new Vector2(.85f,.25f));
                    Path(vh, new Vector2(.15f,.5f), new Vector2(.85f,.5f));
                    Path(vh, new Vector2(.15f,.75f), new Vector2(.85f,.75f));
                    Circle(vh, new Vector2(.38f,.25f), .07f); Circle(vh,new Vector2(.65f,.5f),.07f); Circle(vh,new Vector2(.38f,.75f),.07f); break;
                default:
                    Path(vh,new Vector2(.25f,.15f),new Vector2(.25f,.85f),new Vector2(.75f,.85f),new Vector2(.75f,.15f),new Vector2(.25f,.15f));
                    for(int i=0;i<3;i++) Path(vh,new Vector2(.36f,.65f-i*.16f),new Vector2(.64f,.65f-i*.16f)); break;
            }
        }
        private void Circle(VertexHelper vh, Vector2 center, float radius)
        {
            var points = new Vector2[17];
            for (int i=0;i<points.Length;i++) points[i] = center + new Vector2(Mathf.Cos(i*Mathf.PI/8), Mathf.Sin(i*Mathf.PI/8))*radius;
            Path(vh, points);
        }
        private void Path(VertexHelper vh, params Vector2[] points)
        {
            Rect rect = rectTransform.rect;
            for(int i=1;i<points.Length;i++)
            {
                Vector2 a = rect.min + Vector2.Scale(points[i-1],rect.size);
                Vector2 b = rect.min + Vector2.Scale(points[i],rect.size);
                Vector2 n = new Vector2(-(b-a).y,(b-a).x).normalized * 1.4f;
                int start=vh.currentVertCount;
                vh.AddVert(a+n,color,Vector2.zero); vh.AddVert(b+n,color,Vector2.zero);
                vh.AddVert(b-n,color,Vector2.zero); vh.AddVert(a-n,color,Vector2.zero);
                vh.AddTriangle(start,start+1,start+2);vh.AddTriangle(start,start+2,start+3);
            }
        }
    }
}
