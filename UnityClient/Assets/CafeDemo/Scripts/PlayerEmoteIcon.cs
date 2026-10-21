using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Tiny vector placeholders: no emoji font dependency, textures or downloaded artwork.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PlayerEmoteIcon : MaskableGraphic
    {
        public enum Symbol { Smile, Wave, Cheer, Proud, Close }
        [SerializeField] private Symbol symbol;
        public Symbol Kind { get => symbol; set { symbol = value; SetVerticesDirty(); } }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (symbol == Symbol.Close) { Line(vh, -.25f, -.25f, .25f, .25f); Line(vh, -.25f, .25f, .25f, -.25f); return; }
            if (symbol == Symbol.Smile)
            {
                Arc(vh, 0, 0, .39f, 0, 360);
                Line(vh, -.17f, .08f, -.11f, .08f); Line(vh, .1f, .08f, .18f, .08f);
                Arc(vh, 0, -.01f, .2f, 205, 335); return;
            }
            Arc(vh, 0, .23f, .105f, 0, 360);
            Line(vh, 0, .1f, 0, -.19f); Line(vh, 0, -.19f, -.16f, -.38f); Line(vh, 0, -.19f, .16f, -.38f);
            if (symbol == Symbol.Wave)
            {
                Line(vh, 0, .06f, -.23f, -.1f); Line(vh, 0, .06f, .2f, .16f); Line(vh, .2f, .16f, .25f, .39f);
                Arc(vh, .26f, .39f, .12f, 5, 95); Arc(vh, .26f, .39f, .21f, 8, 90);
            }
            else if (symbol == Symbol.Cheer)
            {
                Line(vh, 0, .06f, -.25f, .25f); Line(vh, 0, .06f, .25f, .25f);
                Star(vh, -.32f, .38f); Star(vh, .32f, .38f);
            }
            else
            {
                Line(vh, 0, .06f, -.25f, -.02f); Line(vh, -.25f, -.02f, -.29f, .2f);
                Line(vh, 0, .06f, .25f, -.02f); Line(vh, .25f, -.02f, .29f, .2f);
                Line(vh, -.29f, .2f, -.18f, .2f); Line(vh, .29f, .2f, .18f, .2f);
            }
        }
        private void Star(VertexHelper vh, float x, float y)
        {
            Line(vh, x - .06f, y, x + .06f, y); Line(vh, x, y - .06f, x, y + .06f);
        }
        private void Arc(VertexHelper vh, float x, float y, float radius, float start, float end)
        {
            int count = Mathf.Max(5, Mathf.CeilToInt((end - start) / 15));
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.Lerp(start, end, (float)i / count) * Mathf.Deg2Rad;
                float b = Mathf.Lerp(start, end, (float)(i + 1) / count) * Mathf.Deg2Rad;
                Line(vh, x + Mathf.Cos(a) * radius, y + Mathf.Sin(a) * radius,
                    x + Mathf.Cos(b) * radius, y + Mathf.Sin(b) * radius);
            }
        }
        private void Line(VertexHelper vh, float x1, float y1, float x2, float y2)
        {
            Rect r = rectTransform.rect; float size = Mathf.Min(r.width, r.height);
            Vector2 a = r.center + new Vector2(x1, y1) * size, b = r.center + new Vector2(x2, y2) * size;
            Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * size * .022f;
            int i = vh.currentVertCount;
            vh.AddVert(a - n, color, Vector2.zero); vh.AddVert(a + n, color, Vector2.zero);
            vh.AddVert(b + n, color, Vector2.zero); vh.AddVert(b - n, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
