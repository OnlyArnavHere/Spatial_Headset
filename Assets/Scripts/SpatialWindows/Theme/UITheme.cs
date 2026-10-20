using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Theme
{
    /// <summary>
    /// Glass UI tokens (Doc/ui-design.md section 3 and 5). Sizes in points: 1 pt = 1 mm at 1 m viewing distance,
    /// so a world size is pt x distance(m) mm. Colours are sRGB.
    /// </summary>
    public static class UITheme
    {
        // Colours
        public static readonly Color GlassTint = Hex(0x2A2C33, 0.72f);
        public static readonly Color GlassFallback = Hex(0x1E2026, 0.92f);
        public static readonly Color GlassRaised = Hex(0xFFFFFF, 0.12f);
        public static readonly Color TextPrimary = Hex(0xFFFFFF, 1f);
        public static readonly Color TextSecondary = Hex(0xEBEBF5, 0.70f);
        public static readonly Color Accent = Hex(0x0A84FF, 1f);
        public static readonly Color Destructive = Hex(0xFF453A, 1f);
        public static readonly Color Success = Hex(0x30D158, 1f);
        /// <summary>Fill of a solid button with white text (about 5.3:1); Success and Destructive are for accents without text.</summary>
        /// <summary>Fill of a primary button with white text (about 5.8:1); Accent is for text-free accents.</summary>
        public static readonly Color AccentFill = Hex(0x0062CC, 1f);
        public static readonly Color SuccessFill = Hex(0x1E7B34, 1f);
        public static readonly Color DestructiveFill = Hex(0xD70015, 1f);
        public static readonly Color RimTop = Hex(0xFFFFFF, 0.28f);
        public static readonly Color RimBottom = Hex(0xFFFFFF, 0.08f);
        public static readonly Color Shadow = Hex(0x000000, 0.35f);
        public static readonly Color FocusRing = Hex(0xFFFFFF, 0.9f);
        public static readonly Color HoverOverlay = Hex(0xFFFFFF, 0.10f);
        public static readonly Color PressedOverlay = Hex(0xFFFFFF, 0.18f);

        // Glass
        public const float BackdropMaxLuminance = 0.25f;
        public const float SheenTop = 0.04f;

        // Radii (pt)
        public const float WindowRadius = 24f;
        public const float ControlRadius = 12f;

        // Spacing (pt)
        public static readonly float[] SpacingScale = { 4f, 8f, 12f, 16f, 24f, 32f };
        public const float WindowPadding = 24f;
        public const float ItemGap = 12f;

        // Shadow and focus ring (pt)
        public const float ShadowOffsetY = -6f;
        public const float ShadowBlur = 24f;
        public const float FocusRingWidth = 2f;
        public const float FocusRingGap = 3f;

        // Pressed scale
        public const float PressedScale = 0.96f;

        // Type scale (pt)
        public const float LargeTitlePt = 29f;
        public const float TitlePt = 22f;
        public const float HeadlinePt = 17f;
        public const float BodyPt = 15f;
        public const float CaptionPt = 12f;
        public const float IconPt = 20f;
        public const float LineHeight = 1.3f;

        public const float MinHitTargetMm = 44f;
        public const float MinTextPt = 10f;
        public const float MinHitGapMm = 8f;

        /// <summary>Metres of a size in points seen at <paramref name="distance"/> metres (1 pt = 1 mm at 1 m).</summary>
        public static float PtToMetres(float pt, float distance = 1f) => pt * 0.001f * distance;

        /// <summary>Type size in points for a role.</summary>
        public static float SizePt(TextRole role)
        {
            switch (role)
            {
                case TextRole.LargeTitle: return LargeTitlePt;
                case TextRole.Title: return TitlePt;
                case TextRole.Headline: return HeadlinePt;
                case TextRole.Caption: return CaptionPt;
                case TextRole.Icon: return IconPt;
                default: return BodyPt;
            }
        }

        /// <summary>Colour for a role.</summary>
        public static Color ColorFor(TextRole role) => role == TextRole.Secondary ? TextSecondary : TextPrimary;

        static float Linear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>WCAG relative luminance of an sRGB colour (alpha ignored).</summary>
        public static float RelativeLuminance(Color c) => 0.2126f * Linear(c.r) + 0.7152f * Linear(c.g) + 0.0722f * Linear(c.b);

        /// <summary>WCAG contrast ratio of two opaque colours, 1 to 21.</summary>
        public static float ContrastRatio(Color fg, Color bg)
        {
            float a = RelativeLuminance(fg), b = RelativeLuminance(bg);
            return (Mathf.Max(a, b) + 0.05f) / (Mathf.Min(a, b) + 0.05f);
        }

        /// <summary>Source-over of sRGB colours, blended in linear light (as the spec's worst-case sum does).</summary>
        public static Color CompositeOver(Color top, Color bottom)
        {
            float a = top.a + bottom.a * (1f - top.a);
            if (a <= 0f) return new Color(0, 0, 0, 0);
            float wb = bottom.a * (1f - top.a);
            float Mix(float t, float b) => ToSrgb((Linear(t) * top.a + Linear(b) * wb) / a);
            return new Color(Mix(top.r, bottom.r), Mix(top.g, bottom.g), Mix(top.b, bottom.b), a);
        }

        /// <summary>
        /// The glass shader's backdrop clamp: if the linear luminance exceeds <paramref name="max"/>, scale the colour
        /// (in linear space) down to it. Takes and returns sRGB.
        /// </summary>
        public static Color ClampLuminance(Color srgb, float max = BackdropMaxLuminance)
        {
            float l = RelativeLuminance(srgb);
            if (l <= max || l <= 0f) return srgb;
            float k = max / l;
            return new Color(ToSrgb(Linear(srgb.r) * k), ToSrgb(Linear(srgb.g) * k), ToSrgb(Linear(srgb.b) * k), srgb.a);
        }

        static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

        static Color Hex(int rgb, float alpha) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    }
}
