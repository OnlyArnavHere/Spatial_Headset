using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Shows a Texture2D or Sprite (a <see cref="TextureSource"/>), as large as fits the window while keeping
    /// its aspect ratio (the window body shows as bars on the spare sides).
    /// </summary>
    public class ImageContent : WindowContent
    {
        ContentSurface surface;

        public TextureSource Source { get; private set; }
        public Texture Texture => Source?.Texture;
        /// <summary>Width / height of the image (the sprite's rect for sprites).</summary>
        public float Aspect => Source?.Aspect ?? 1f;
        /// <summary>Current size of the displayed image in metres.</summary>
        public Vector2 DisplaySize => surface != null ? surface.DisplaySize : Vector2.zero;

        public void Init(SpatialWindow window, Material baseMaterial, Texture2D texture) =>
            Build(window, baseMaterial, new TextureSource(texture));

        public void Init(SpatialWindow window, Material baseMaterial, Sprite sprite) =>
            Build(window, baseMaterial, new TextureSource(sprite));

        void Build(SpatialWindow window, Material baseMaterial, TextureSource source)
        {
            Attach(window, baseMaterial);
            if (source.Texture == null)
                Debug.LogWarning("ImageContent: no image given; showing an empty panel.", this);

            Source = source;
            surface = CreateSurface("Image", transform, source);
            surface.EmptyColor = Color.grey;
            source.Start();
            Relayout();
        }

        protected override void Layout(Vector2 size) => surface.Fit(size);

        protected override void OnDestroy()
        {
            Source?.Stop();
            base.OnDestroy();
        }
    }
}
