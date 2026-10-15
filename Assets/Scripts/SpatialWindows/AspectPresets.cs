using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>Window shapes the user can pick. <see cref="Free"/> means no fixed aspect.</summary>
    public enum AspectPreset { Free, Wide16x9, Wide16x10, Ultrawide21x9, Classic4x3, SuperUltrawide32x9, Square1x1, Photo3x2 }

    /// <summary>
    /// Aspect preset math. A preset fixes the aspect of a window's content area (the body minus
    /// <see cref="WindowContent.Padding"/> on each side), which is what the user looks at.
    /// </summary>
    public static class AspectPresets
    {
        public static readonly AspectPreset[] All =
        {
            AspectPreset.Wide16x9, AspectPreset.Wide16x10, AspectPreset.Ultrawide21x9, AspectPreset.Classic4x3,
            AspectPreset.SuperUltrawide32x9, AspectPreset.Square1x1, AspectPreset.Photo3x2,
        };

        /// <summary>Width / height, or 0 for <see cref="AspectPreset.Free"/>.</summary>
        public static float Ratio(AspectPreset preset) => preset switch
        {
            AspectPreset.Wide16x9 => 16f / 9f,
            AspectPreset.Wide16x10 => 16f / 10f,
            AspectPreset.Ultrawide21x9 => 21f / 9f,
            AspectPreset.Classic4x3 => 4f / 3f,
            AspectPreset.SuperUltrawide32x9 => 32f / 9f,
            AspectPreset.Square1x1 => 1f,
            AspectPreset.Photo3x2 => 3f / 2f,
            _ => 0f,
        };

        public static string Label(AspectPreset preset) => preset switch
        {
            AspectPreset.Wide16x9 => "16:9",
            AspectPreset.Wide16x10 => "16:10",
            AspectPreset.Ultrawide21x9 => "21:9",
            AspectPreset.Classic4x3 => "4:3",
            AspectPreset.SuperUltrawide32x9 => "32:9",
            AspectPreset.Square1x1 => "1:1",
            AspectPreset.Photo3x2 => "3:2",
            _ => "Free",
        };

        /// <summary>
        /// The body size whose content area has the preset's aspect, keeping <paramref name="size"/>'s width where the
        /// limits allow; if the height would leave <paramref name="min"/>..<paramref name="max"/>, both shrink or grow
        /// together so the aspect still holds (as close as the limits permit). Free returns the size clamped.
        /// </summary>
        public static Vector2 Fit(Vector2 size, AspectPreset preset, Vector2 min, Vector2 max, float padding = WindowContent.Padding)
        {
            float ratio = Ratio(preset);
            if (ratio <= 0f) return Clamp(size, min, max);

            // Content width cw and height ch = cw / ratio; body = content + 2 * padding.
            float pad = padding * 2f;
            float contentWidth = Mathf.Max(0.001f, Mathf.Clamp(size.x, min.x, max.x) - pad);
            // The content widths the limits allow, from each axis.
            float lowest = Mathf.Max(min.x - pad, (min.y - pad) * ratio);
            float highest = Mathf.Min(max.x - pad, (max.y - pad) * ratio);
            if (lowest <= highest) contentWidth = Mathf.Clamp(contentWidth, lowest, highest);
            return new Vector2(contentWidth + pad, contentWidth / ratio + pad);
        }

        /// <summary>A resize under a preset: the dragged size's width decides, the height follows the aspect.</summary>
        public static Vector2 Constrain(Vector2 requested, AspectPreset preset, Vector2 min, Vector2 max,
            float padding = WindowContent.Padding) => Fit(requested, preset, min, max, padding);

        public static Vector2 Clamp(Vector2 size, Vector2 min, Vector2 max) =>
            new Vector2(Mathf.Clamp(size.x, min.x, max.x), Mathf.Clamp(size.y, min.y, max.y));

        /// <summary>The content area's aspect for a body size (what <see cref="Fit"/> makes equal to the preset).</summary>
        public static float ContentAspect(Vector2 bodySize, float padding = WindowContent.Padding) =>
            (bodySize.x - padding * 2f) / Mathf.Max(0.001f, bodySize.y - padding * 2f);
    }
}
