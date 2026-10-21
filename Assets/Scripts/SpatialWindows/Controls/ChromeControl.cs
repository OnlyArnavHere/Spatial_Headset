using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A piece of window chrome (close, options, resize, grab bar): a hit quad that carries the collider (at least 44 mm)
    /// with a smaller rounded glass shape drawn on it, and optionally an icon glyph. The shape reuses the SpatialGlass
    /// shader with its radius set to half its height, so it is a plain 4-vertex quad that <see cref="WindowCurver"/> bends.
    /// <see cref="Apply"/> fades the drawing without touching the collider.
    /// </summary>
    public sealed class ChromeControl
    {
        /// <summary>Hit size of a round control and height of the grab bar's hit area (pt).</summary>
        public const float HitPt = 44f;
        /// <summary>Drawn size of a round control (pt).</summary>
        public const float DiscPt = 32f;
        public const float PillWidthPt = 120f, PillHeightPt = 10f, PillHitWidthPt = 140f;
        /// <summary>How far the drawn shape sits above the hit area's centre, so the hit area doesn't reach up into the slab (pt).</summary>
        public const float PillRisePt = 5f;
        /// <summary>
        /// Fill of the round controls: the raised overlay over the dark glass fallback, so the white icon reads on any
        /// backdrop (a translucent white disc would vanish against a white wall).
        /// </summary>
        public static readonly Color DiscFill = UITheme.CompositeOver(UITheme.GlassRaised, UITheme.GlassFallback);
        /// <summary>The grab bar is white and translucent, so it gets a dark outline to stay visible over a white wall.</summary>
        public static readonly Color PillOutline = new Color(0f, 0f, 0f, 0.75f);
        public const float PillOutlinePt = 1.5f;

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int FallbackId = Shader.PropertyToID("_Fallback");
        static readonly int RimTopId = Shader.PropertyToID("_RimTop");
        static readonly int RimBottomId = Shader.PropertyToID("_RimBottom");
        static readonly int RimWidthId = Shader.PropertyToID("_RimWidth");
        static readonly int ShadowColorId = Shader.PropertyToID("_ShadowColor");
        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int SizeId = Shader.PropertyToID("_Size");
        static readonly int QuadId = Shader.PropertyToID("_Quad");
        static readonly int SolidId = Shader.PropertyToID("_Solid");
        static readonly int ShadowOnlyId = Shader.PropertyToID("_ShadowOnly");

        public readonly Transform Hit;
        public Color Fill { get; private set; }
        /// <summary>Hover (per pointer), press and focus of this control; drawn by <see cref="Apply"/>.</summary>
        public ControlVisualState State { get; } = new ControlVisualState();
        /// <summary>Width in pt of a dark outline (<see cref="PillOutline"/>) drawn instead of the glass rim; 0 for none.</summary>
        public float OutlinePt;
        public float Alpha { get; private set; }
        public Transform Disc => disc;
        public TextMeshPro Glyph => glyph;

        readonly Transform disc;
        readonly MeshRenderer discRenderer;
        readonly TextMeshPro glyph;
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        readonly bool isGlass, rimmed;
        readonly Vector2 hitSize, visualSize;
        readonly float rise;

        /// <param name="hit">The quad with the collider; its own renderer is turned off.</param>
        /// <param name="visualParent">Parent of the glyph (an unscaled transform).</param>
        /// <param name="material">A glass material (without depth write), or a flat one when there is no glass.</param>
        public ChromeControl(Transform hit, Transform visualParent, Material material, Vector2 hitPt, Vector2 visualPt,
            float risePt, Color fill, bool rim, string glyphText)
        {
            Hit = hit;
            Fill = fill;
            rimmed = rim;
            isGlass = material != null && material.HasProperty("_Radius");
            hitSize = new Vector2(UITheme.PtToMetres(hitPt.x), UITheme.PtToMetres(hitPt.y));
            visualSize = new Vector2(UITheme.PtToMetres(visualPt.x), UITheme.PtToMetres(visualPt.y));
            rise = UITheme.PtToMetres(risePt);
            Alpha = -1f;

            if (hit.TryGetComponent<MeshRenderer>(out var hitRenderer)) hitRenderer.enabled = false;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Disc";
            quad.transform.SetParent(hit, false);
            Object.DestroyImmediate(quad.GetComponent<MeshCollider>());
            discRenderer = quad.GetComponent<MeshRenderer>();
            discRenderer.sharedMaterial = material;
            discRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            discRenderer.receiveShadows = false;
            disc = quad.transform;

            if (glyphText != null)
            {
                var go = new GameObject(hit.name + "Glyph");
                go.transform.SetParent(visualParent, false);
                glyph = go.AddComponent<TextMeshPro>();
                glyph.text = glyphText;
                glyph.alignment = TextAlignmentOptions.Center;
                glyph.textWrappingMode = TextWrappingModes.NoWrap;
                glyph.overflowMode = TextOverflowModes.Overflow;
                ThemeApplier.Apply(glyph, TextRole.Icon);
                glyph.rectTransform.sizeDelta = visualSize;
            }
        }

        /// <summary>Puts the hit area's centre at a point in the window's layout space.</summary>
        public void Place(float x, float y, float z)
        {
            Hit.localPosition = new Vector3(x, y, z);
            Hit.localScale = new Vector3(hitSize.x, hitSize.y, 1f);
            disc.localPosition = new Vector3(0f, rise / hitSize.y, 0f);
            disc.localScale = new Vector3(visualSize.x / hitSize.x, visualSize.y / hitSize.y, 1f);
            if (glyph != null) glyph.transform.localPosition = new Vector3(x, y + rise, z - 0.001f);
            Alpha = -1f; // the size is part of what Apply pushes
        }

        /// <summary>Draws the control with its fill (and rim) at this alpha, 0 = invisible; the collider is unaffected.</summary>
        public void Apply(float alpha, float fillAlpha = -1f)
        {
            alpha = Mathf.Clamp01(alpha);
            var fill = Fill;
            if (fillAlpha >= 0f) fill.a = fillAlpha; else fill.a *= alpha;
            bool visible = alpha > 0.001f;
            discRenderer.enabled = visible;
            if (glyph != null)
            {
                glyph.enabled = visible;
                var c = glyph.color;
                if (!Mathf.Approximately(c.a, alpha)) { c.a = alpha; glyph.color = c; }
            }
            if (isGlass)
            {
                var rimTop = OutlinePt > 0f ? PillOutline : rimmed ? UITheme.RimTop : Color.clear;
                var rimBottom = OutlinePt > 0f ? PillOutline : rimmed ? UITheme.RimBottom : Color.clear;
                rimTop.a *= alpha;
                rimBottom.a *= alpha;
                block.Clear();
                block.SetColor(TintId, fill);
                block.SetColor(FallbackId, fill);
                block.SetColor(RimTopId, rimTop);
                block.SetColor(RimBottomId, rimBottom);
                block.SetFloat(RimWidthId, UITheme.PtToMetres(OutlinePt));
                block.SetColor(ShadowColorId, Color.clear);
                block.SetFloat(RadiusId, visualSize.y * 0.5f);
                block.SetVector(SizeId, new Vector4(visualSize.x, visualSize.y, 0f, 0f));
                block.SetVector(QuadId, new Vector4(visualSize.x, visualSize.y, 0f, 0f));
                block.SetFloat(SolidId, 0f);
                block.SetFloat(ShadowOnlyId, 0f);
                State.Apply(block);
                discRenderer.SetPropertyBlock(block);
            }
            Alpha = alpha;
        }

        /// <summary>The colour the shape is currently drawn with (glass only; clear otherwise).</summary>
        public Color DrawnFill
        {
            get
            {
                if (!isGlass) return Color.clear;
                discRenderer.GetPropertyBlock(block);
                return block.GetColor(FallbackId);
            }
        }
    }
}
