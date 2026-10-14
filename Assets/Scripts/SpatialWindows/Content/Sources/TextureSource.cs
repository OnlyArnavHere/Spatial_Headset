using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A still image: a whole Texture, or a Sprite's region of its texture.</summary>
    public class TextureSource : IContentSource
    {
        public Texture Texture { get; }
        public Rect UVRect { get; }
        public Vector2Int PixelSize { get; }
        public float Aspect { get; } = 1f;
        public IContentInput Input => null;
        public bool IsRunning { get; private set; }

        // A still image never changes once constructed.
        public event Action Changed { add { } remove { } }

        public TextureSource(Texture texture)
        {
            Texture = texture;
            UVRect = new Rect(0f, 0f, 1f, 1f);
            if (texture == null) return;
            PixelSize = new Vector2Int(texture.width, texture.height);
            Aspect = texture.width / (float)texture.height;
        }

        /// <summary>Shows only the sprite's rect, so sprites packed in an atlas display correctly.</summary>
        public TextureSource(Sprite sprite)
        {
            if (sprite == null)
            {
                UVRect = new Rect(0f, 0f, 1f, 1f);
                return;
            }
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            Texture = texture;
            UVRect = new Rect(rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height);
            PixelSize = new Vector2Int(Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
            Aspect = rect.width / rect.height;
        }

        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }
}
