using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Base for window content. Parents itself under the window's <see cref="SpatialWindow.ContentRoot"/>
    /// and lays itself out again whenever the window is resized. Subclasses build their visuals in an
    /// Init method (calling <see cref="Attach"/> first) and implement <see cref="Layout"/>. Pixels come from an
    /// <see cref="IContentSource"/> shown on a <see cref="ContentSurface"/> (<see cref="CreateSurface"/>); the content
    /// adds its own chrome (controls, labels) around it and starts/stops the source.
    /// Content sits in front of the window body: use small negative local Z offsets to layer it.
    /// </summary>
    public abstract class WindowContent : MonoBehaviour
    {
        /// <summary>Gap between the window body's edge and the content area, in metres.</summary>
        public const float Padding = 0.02f;
        const float ControlColliderDepth = 0.001f;
        /// <summary>Render queue of in-window shapes: above the glass slab (2950), below TextMeshPro text (3000).</summary>
        public const int ShapeQueue = 2960;
        /// <summary>Layers within the shape queues: panels and bars, then controls on them, then fills and knobs on those.</summary>
        public const int ControlLayer = 1, FillLayer = 2;
        /// <summary>Corner radius (pt) of an image or video: the window radius less the padding the content is inset by, at least the control radius.</summary>
        public static readonly float InsetRadiusPt = Mathf.Max(UITheme.ControlRadius, UITheme.WindowRadius - Padding * 1000f);

        public SpatialWindow Window { get; private set; }
        protected Material BaseMaterial { get; private set; }

        readonly List<Material> ownedMaterials = new List<Material>();
        readonly List<RoundedShape> shapes = new List<RoundedShape>();

        protected void Attach(SpatialWindow window, Material baseMaterial)
        {
            Window = window;
            BaseMaterial = baseMaterial;
            name = GetType().Name;
            transform.SetParent(window.ContentRoot, false);
            window.SizeChanged += OnWindowResized;
        }

        /// <summary>The content area in metres: the window body minus padding. Centred on this transform.</summary>
        protected Vector2 ContentSize => new Vector2(
            Mathf.Max(0.01f, Window.Size.x - Padding * 2f),
            Mathf.Max(0.01f, Window.Size.y - Padding * 2f));

        /// <summary>Positions and scales the visuals to fill a content area of the given size.</summary>
        protected abstract void Layout(Vector2 size);

        protected void Relayout()
        {
            if (Window == null) return;
            Layout(ContentSize);
            foreach (var shape in shapes)
            {
                if (shape == null) continue;
                shape.Refresh();
                if (shape.TryGetComponent<WindowSlider>(out var slider)) slider.Refit();
            }
        }

        void OnWindowResized(SpatialWindow _) => Relayout();

        /// <summary>Largest size with the given width/height aspect that fits inside the area.</summary>
        public static Vector2 FitAspect(Vector2 area, float aspect)
        {
            if (aspect <= 0f) return area;
            return area.x / area.y > aspect
                ? new Vector2(area.y * aspect, area.y)
                : new Vector2(area.x, area.x / aspect);
        }

        /// <summary>
        /// A material instance owned (and destroyed) by this content: a square flat SpatialGlass shape when the window has
        /// glass (see <see cref="CreateRounded"/>), otherwise a plain colour of the base material.
        /// </summary>
        protected Material CreateMaterial(Color color, Texture texture = null)
        {
            if (Window != null && Window.GlassMaterial != null) return CreateRounded(color, 0f, texture);
            var material = new Material(BaseMaterial) { color = color };
            if (texture != null) material.mainTexture = texture;
            ownedMaterials.Add(material);
            return material;
        }

        /// <summary>
        /// A solid rounded fill: the window's SpatialGlass shader in flat mode (no backdrop, sheen, rim or shadow), drawn
        /// after the slab and before text. The radius is the quad's default; <see cref="RoundedShape"/> keeps it fitted to the quad's size.
        /// Without a glass material this is a plain colour.
        /// </summary>
        protected Material CreateRounded(Color fill, float radiusPt, Texture tex = null)
        {
            var glass = Window != null ? Window.GlassMaterial : null;
            if (glass == null)
            {
                var plain = new Material(BaseMaterial) { color = fill };
                if (tex != null) plain.mainTexture = tex;
                ownedMaterials.Add(plain);
                return plain;
            }
            var material = new Material(glass) { name = "SpatialGlass Shape", renderQueue = ShapeQueue };
            material.SetFloat("_Flat", 1f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Radius", UITheme.PtToMetres(radiusPt));
            material.SetColor("_Color", fill);
            material.SetFloat("_UseTex", tex != null ? 1f : 0f);
            if (tex != null) material.mainTexture = tex;
            ownedMaterials.Add(material);
            return material;
        }

        /// <summary>Sets the corner radius of a shape made by <see cref="CreateQuad"/> (or a capsule: radius half its height).</summary>
        protected static void Round(Transform quad, float radiusPt, bool capsule = false)
        {
            if (!quad.TryGetComponent<RoundedShape>(out var shape)) return;
            shape.Radius = UITheme.PtToMetres(radiusPt);
            shape.Capsule = capsule;
            shape.Refresh();
        }

        /// <summary>A quad without a collider, so clicks pass through to the window body.</summary>
        protected Transform CreateQuad(string quadName, Transform parent, Material material, int layer = 0)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = quadName;
            DestroyImmediate(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(parent, false);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (material.HasProperty("_Flat") && material.GetFloat("_Flat") > 0.5f)
            {
                var shape = quad.AddComponent<RoundedShape>();
                shape.Radius = material.GetFloat("_Radius");
                material.renderQueue = ShapeQueue + layer;
                shapes.Add(shape);
            }
            return quad.transform;
        }

        /// <summary>A clickable quad with a thin collider and a <see cref="WindowControl"/> of type T.</summary>
        protected T CreateControl<T>(string controlName, Transform parent, Material material) where T : WindowControl
        {
            var quad = CreateQuad(controlName, parent, material, ControlLayer);
            quad.gameObject.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, ControlColliderDepth);
            var control = quad.gameObject.AddComponent<T>();
            // Buttons and fields get the 12 pt control radius, sliders a capsule (unless the caller set a radius already).
            if (quad.TryGetComponent<RoundedShape>(out var shape))
            {
                if (control is WindowSlider) shape.Capsule = true;
                else if (shape.Radius <= 0f) shape.Radius = UITheme.PtToMetres(UITheme.ControlRadius);
            }
            control.Register(Window);
            return control;
        }

        /// <summary>
        /// A quad that displays the source. Interactive sources (with <see cref="IContentSource.Input"/>) get a
        /// collider so the surface receives pointer input; display-only ones don't, so clicks reach the window body.
        /// </summary>
        protected ContentSurface CreateSurface(string surfaceName, Transform parent, IContentSource source)
        {
            var material = CreateRounded(Color.black, InsetRadiusPt);
            if (material.HasProperty("_UseTex")) material.SetFloat("_UseTex", 1f);
            ContentSurface surface;
            if (source.Input != null)
            {
                surface = CreateControl<ContentSurface>(surfaceName, parent, material);
            }
            else
            {
                surface = CreateQuad(surfaceName, parent, material).gameObject.AddComponent<ContentSurface>();
            }
            surface.Bind(source, material);
            return surface;
        }

        /// <summary>Adds the round 28 pt knob to a slider made by <see cref="CreateControl"/>; the slider keeps it on its value.</summary>
        protected void AddKnob(WindowSlider slider)
        {
            var knob = CreateQuad("Knob", slider.transform, CreateRounded(UITheme.TextPrimary, WindowSlider.KnobPt / 2f), FillLayer);
            slider.AttachKnob(knob);
        }

        protected static TextMeshPro CreateLabel(string labelName, Transform parent, string text, float fontSize,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(labelName);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        protected virtual void OnDestroy()
        {
            if (Window != null) Window.SizeChanged -= OnWindowResized;
            foreach (var material in ownedMaterials)
                if (material != null) Destroy(material);
            ownedMaterials.Clear();
        }
    }

    /// <summary>
    /// Fits a flat SpatialGlass shape (<see cref="WindowContent.CreateRounded"/>) to its quad: the quad's size and corner radius
    /// go to the shader through a property block (so shapes can share a material). The quad stays a plain 4-vertex mesh that
    /// <see cref="WindowCurver"/> bends. A capsule has a radius of half its height.
    /// </summary>
    public sealed class RoundedShape : MonoBehaviour
    {
        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int SizeId = Shader.PropertyToID("_Size");
        static readonly int QuadId = Shader.PropertyToID("_Quad");

        /// <summary>Corner radius in metres (limited to half the smaller side).</summary>
        public float Radius;
        public bool Capsule;
        /// <summary>Drawn size in metres when the quad's scale is not it (a child of a scaled quad); zero to use the scale.</summary>
        public Vector2 FixedSize;

        // Created on first use: a MonoBehaviour field initializer may not create one.
        MaterialPropertyBlock block;
        Vector3 fittedScale;

        public Vector2 Size => FixedSize != Vector2.zero ? FixedSize : new Vector2(transform.localScale.x, transform.localScale.y);

        public void Refresh()
        {
            fittedScale = transform.localScale;
            if (!TryGetComponent<MeshRenderer>(out var meshRenderer)) return;
            var size = Size;
            float half = Mathf.Max(0f, Mathf.Min(size.x, size.y) * 0.5f);
            block ??= new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(block);
            block.SetVector(SizeId, new Vector4(size.x, size.y, 0f, 0f));
            block.SetVector(QuadId, new Vector4(size.x, size.y, 0f, 0f));
            block.SetFloat(RadiusId, Capsule ? half : Mathf.Min(Radius, half));
            meshRenderer.SetPropertyBlock(block);
        }

        void LateUpdate()
        {
            if (transform.localScale != fittedScale) Refresh();
        }
    }
}
