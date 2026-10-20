using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Theme
{
    public enum TextRole { LargeTitle, Title, Headline, Body, Caption, Secondary, Icon }

    /// <summary>
    /// Styles TextMeshPro texts by role: font asset, size in metres and colour. Font assets are loaded from
    /// Resources (built by Tools/Spatial Windows/Build Theme Fonts), so it works in builds and in Edit mode.
    /// A missing font leaves the text's current font; a null text is ignored.
    /// </summary>
    public static class ThemeApplier
    {
        public const string FontFolder = "Fonts/";
        public const string RegularName = "Inter-Regular SDF";
        public const string MediumName = "Inter-Medium SDF";
        public const string SemiBoldName = "Inter-SemiBold SDF";
        public const string IconName = "MaterialIconsRound SDF";

        /// <summary>Resource name of the font asset for a role.</summary>
        public static string FontName(TextRole role)
        {
            switch (role)
            {
                case TextRole.LargeTitle:
                case TextRole.Title:
                case TextRole.Headline: return SemiBoldName;
                case TextRole.Caption: return MediumName;
                case TextRole.Icon: return IconName;
                default: return RegularName;
            }
        }

        /// <summary>Font asset for a role, or null if not built yet.</summary>
        public static TMP_FontAsset FontFor(TextRole role) => Resources.Load<TMP_FontAsset>(FontFolder + FontName(role));

        /// <param name="distance">Viewing distance in metres the text is meant to be read at (scales the pt size).</param>
        public static void Apply(TMP_Text text, TextRole role, float distance = 1f)
        {
            if (text == null) return;
            var font = FontFor(role);
            if (font != null) text.font = font;
            text.fontSize = UITheme.PtToMetres(UITheme.SizePt(role), distance);
            text.color = UITheme.ColorFor(role);
            text.lineSpacing = (UITheme.LineHeight - 1.2f) * 100f;
        }
    }
}
