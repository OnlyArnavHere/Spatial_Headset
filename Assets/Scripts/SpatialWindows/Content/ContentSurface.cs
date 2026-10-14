using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The quad that displays an <see cref="IContentSource"/>: shows its texture (or <see cref="EmptyColor"/>
    /// while it has none), keeps its aspect ratio inside the area given to <see cref="Fit"/>, and follows the
    /// source's changes. For interactive sources it is also a control: pointer press, drag, release, hover and
    /// scroll on the quad become normalized positions for the source's <see cref="IContentInput"/>.
    /// Created by <see cref="WindowContent.CreateSurface"/>.
    /// </summary>
    public class ContentSurface : WindowControl
    {
        public IContentSource Source { get; private set; }
        /// <summary>Current size of the displayed image in metres.</summary>
        public Vector2 DisplaySize { get; private set; }
        /// <summary>Colour shown while the source has no texture.</summary>
        public Color EmptyColor { get; set; } = Color.black;

        Material material;
        Vector2 area;

        public void Bind(IContentSource source, Material surfaceMaterial)
        {
            Source = source;
            material = surfaceMaterial;
            source.Changed += OnSourceChanged;
            ApplyTexture();
        }

        /// <summary>Sizes the quad to the largest rectangle with the source's aspect that fits the area.</summary>
        public void Fit(Vector2 contentArea)
        {
            area = contentArea;
            DisplaySize = WindowContent.FitAspect(area, Source != null ? Source.Aspect : 1f);
            transform.localScale = new Vector3(DisplaySize.x, DisplaySize.y, 1f);
        }

        void OnSourceChanged()
        {
            ApplyTexture();
            if (area != Vector2.zero) Fit(area);
        }

        void ApplyTexture()
        {
            var texture = Source.Texture;
            material.mainTexture = texture;
            material.mainTextureOffset = Source.UVRect.position;
            material.mainTextureScale = Source.UVRect.size;
            material.color = texture != null ? Color.white : EmptyColor;
        }

        /// <summary>A world point on the quad as a normalized position over the displayed image (0,0 bottom-left).</summary>
        public Vector2 ToNormalized(Vector3 worldPoint)
        {
            // A quad spans -0.5..0.5 in local space, whatever its scale.
            var local = transform.InverseTransformPoint(worldPoint);
            return new Vector2(Mathf.Clamp01(local.x + 0.5f), Mathf.Clamp01(local.y + 0.5f));
        }

        IContentInput Input => Source?.Input;

        // Release carries no point (the pointer may have left the quad), so it reports the last one seen.
        Vector2 lastPosition;

        Vector2 Track(Vector3 worldPoint) => lastPosition = ToNormalized(worldPoint);

        public override void Press(Vector3 worldPoint) => Input?.PointerDown(Track(worldPoint));
        public override void Drag(Vector3 worldPoint) => Input?.PointerMove(Track(worldPoint));
        public override void Release(bool inside) => Input?.PointerUp(lastPosition);
        public override void Hover(Vector3 worldPoint) => Input?.PointerMove(Track(worldPoint));
        public override void Scroll(Vector3 worldPoint, float delta) => Input?.Scroll(Track(worldPoint), delta);

        void OnDestroy()
        {
            if (Source != null) Source.Changed -= OnSourceChanged;
        }
    }
}
