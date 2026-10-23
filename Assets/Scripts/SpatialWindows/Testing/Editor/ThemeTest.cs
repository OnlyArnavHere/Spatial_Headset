using System.Collections;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the glass theme tokens and <see cref="ThemeApplier"/>: point-to-metre scaling, the worst-case
    /// text contrast on glass, minimum sizes, and styling texts by role (null text and missing fonts don't throw).
    /// Results use a [ThemeTest] prefix.
    /// </summary>
    static class ThemeTest
    {
        [MenuItem("Tools/Spatial Windows/Test Theme")]
        static void Run() => EditorTestRunner.Run("ThemeTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Theme", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            Check(Mathf.Approximately(UITheme.PtToMetres(10f, 1f), 0.010f) && Mathf.Approximately(UITheme.PtToMetres(10f, 2f), 0.020f),
                "PtToMetres(10, 1) is 0.010 and PtToMetres(10, 2) is 0.020");

            // Worst case: white wall behind the glass, clamped to 0.25 luminance, then the tint over it.
            var wall = UITheme.ClampLuminance(Color.white);
            var glass = UITheme.CompositeOver(UITheme.GlassTint, wall);
            float primary = UITheme.ContrastRatio(UITheme.TextPrimary, glass);
            Check(Mathf.Abs(UITheme.RelativeLuminance(wall) - 0.25f) < 1e-3f, $"white clamps to 0.25 luminance ({UITheme.RelativeLuminance(wall):F3})");
            Check(primary >= 7f, $"text.primary over glass on a white wall is {primary:F2}:1 (>= 7:1)");
            var secondary = UITheme.CompositeOver(UITheme.TextSecondary, glass);
            float sec = UITheme.ContrastRatio(secondary, glass);
            Check(sec >= 4.5f, $"text.secondary over glass on a white wall is {sec:F2}:1 (>= 4.5:1)");
            Check(Mathf.Abs(UITheme.ContrastRatio(Color.white, Color.black) - 21f) < 0.01f, "contrast of white on black is 21:1");

            var dark = new Color(0.1f, 0.1f, 0.1f);
            Check(UITheme.ClampLuminance(dark) == dark, "a backdrop under the clamp is left alone");

            var over = UITheme.CompositeOver(new Color(1, 1, 1, 0), Color.black);
            Check(Mathf.Approximately(over.r, 0f) && Mathf.Approximately(over.a, 1f), "a transparent colour composites to the backdrop");

            bool sizesOk = true;
            foreach (TextRole role in System.Enum.GetValues(typeof(TextRole)))
                sizesOk &= UITheme.SizePt(role) >= UITheme.MinTextPt;
            Check(sizesOk, "every type-scale size is at least 10 pt");
            Check(UITheme.MinHitTargetMm >= 44f && UITheme.MinTextPt >= 10f, "hit targets are at least 44 mm");

            ThemeApplier.Apply(null, TextRole.Body);
            Check(true, "Apply with a null text doesn't throw");

            var go = new GameObject("ThemeTestText");
            try
            {
                var text = go.AddComponent<TextMeshPro>();
                ThemeApplier.Apply(text, TextRole.Title, 2f);
                Check(Mathf.Approximately(text.fontSize, 0.044f * ThemeApplier.TmpWorldScale) && text.color == UITheme.TextPrimary,
                    $"Title at 2 m is 0.044 m and primary colour ({text.fontSize})");
                ThemeApplier.Apply(text, TextRole.Secondary);
                Check(Mathf.Approximately(text.fontSize, 0.015f * ThemeApplier.TmpWorldScale) && text.color == UITheme.TextSecondary, "Secondary is body size in the secondary colour");
                var font = ThemeApplier.FontFor(TextRole.Body);
                if (font != null)
                {
                    ThemeApplier.Apply(text, TextRole.Body);
                    Check(text.font == font, "Apply sets the role's font asset");
                }
                else Debug.Log("[ThemeTest] Fonts not built yet: font check skipped, missing font handled.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            yield break;
        }
    }
}
