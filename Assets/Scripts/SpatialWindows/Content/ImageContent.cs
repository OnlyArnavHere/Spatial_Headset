using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Shows a Texture2D or Sprite, as large as fits the window while keeping its aspect ratio
    /// (the window body shows as bars on the spare sides).
    /// </summary>
    public class ImageContent : WindowContent
    {
        Transform imageQuad;

        public Texture Texture { get; private set; }
        /// <summary>Width / height of the image (the sprite's rect for sprites).</summary>
        public float Aspect { get; private set; } = 1f;
        /// <summary>Current size of the displayed image in metres.</summary>
        public Vector2 DisplaySize { get; private set; }

        public void Init(SpatialWindow window, Material baseMaterial, Texture2D texture)
        {
            float aspect = texture != null ? texture.width / (float)texture.height : 1f;
            Build(window, baseMaterial, texture, new Rect(0f, 0f, 1f, 1f), aspect);
        }

        public void Init(SpatialWindow window, Material baseMaterial, Sprite sprite)
        {
            if (sprite == null)
            {
                Build(window, baseMaterial, null, new Rect(0f, 0f, 1f, 1f), 1f);
                return;
            }

            // A sprite can be a region of a larger texture (an atlas): show only its rect.
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            var uv = new Rect(rect.x / texture.width, rect.y / texture.height,
                rect.width / texture.width, rect.height / texture.height);
            Build(window, baseMaterial, texture, uv, rect.width / rect.height);
        }

        void Build(SpatialWindow window, Material baseMaterial, Texture texture, Rect uv, float aspect)
        {
            Attach(window, baseMaterial);
            if (texture == null)
                Debug.LogWarning("ImageContent: no image given; showing an empty panel.", this);

            Texture = texture;
            Aspect = aspect;
            var material = CreateMaterial(texture != null ? Color.white : Color.grey, texture);
            material.mainTextureOffset = uv.position;
            material.mainTextureScale = uv.size;
            imageQuad = CreateQuad("Image", transform, material);
            Relayout();
        }

        protected override void Layout(Vector2 size)
        {
            DisplaySize = FitAspect(size, Aspect);
            imageQuad.localScale = new Vector3(DisplaySize.x, DisplaySize.y, 1f);
        }
    }
}
