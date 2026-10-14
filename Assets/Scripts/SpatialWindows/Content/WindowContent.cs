using System.Collections.Generic;
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

        public SpatialWindow Window { get; private set; }
        protected Material BaseMaterial { get; private set; }

        readonly List<Material> ownedMaterials = new List<Material>();

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
            if (Window != null) Layout(ContentSize);
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

        /// <summary>A material instance owned (and destroyed) by this content.</summary>
        protected Material CreateMaterial(Color color, Texture texture = null)
        {
            var material = new Material(BaseMaterial) { color = color };
            if (texture != null) material.mainTexture = texture;
            ownedMaterials.Add(material);
            return material;
        }

        /// <summary>A quad without a collider, so clicks pass through to the window body.</summary>
        protected Transform CreateQuad(string quadName, Transform parent, Material material)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = quadName;
            DestroyImmediate(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(parent, false);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            return quad.transform;
        }

        /// <summary>A clickable quad with a thin collider and a <see cref="WindowControl"/> of type T.</summary>
        protected T CreateControl<T>(string controlName, Transform parent, Material material) where T : WindowControl
        {
            var quad = CreateQuad(controlName, parent, material);
            quad.gameObject.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, ControlColliderDepth);
            var control = quad.gameObject.AddComponent<T>();
            control.Register(Window);
            return control;
        }

        /// <summary>
        /// A quad that displays the source. Interactive sources (with <see cref="IContentSource.Input"/>) get a
        /// collider so the surface receives pointer input; display-only ones don't, so clicks reach the window body.
        /// </summary>
        protected ContentSurface CreateSurface(string surfaceName, Transform parent, IContentSource source)
        {
            var material = CreateMaterial(Color.black);
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
}
