using System.Collections;
using System.Collections.Generic;
using System.IO;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of C6 typography: every TextMeshPro text a window and its content create uses an Inter or the icon
    /// font asset (never LiberationSans or a Light weight), is at least 10 pt, running text at least 12 pt, secondary text
    /// at least 15 pt, has line spacing for a 1.3 line height, and the keyboard's icon keys are 20 pt.
    /// Settings use a file in Temp/, never the user's. Results use a [TypographyTest] prefix.
    /// </summary>
    static class TypographyTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";

        [MenuItem("Tools/Spatial Windows/Test Typography")]
        static void Run() => EditorTestRunner.Run("TypographyTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Typography", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static void SetField(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static float Pt(TMP_Text text) => text.fontSize / ThemeApplier.TmpWorldScale * 1000f;

        static bool AllowedFont(TMP_Text text)
        {
            if (text.font == null) return false;
            string name = text.font.name;
            return name == ThemeApplier.RegularName || name == ThemeApplier.MediumName || name == ThemeApplier.SemiBoldName
                   || name == ThemeApplier.IconName;
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && window != null, "the glass and window materials exist");
            if (glass == null || window == null) yield break;
            Check(ThemeApplier.FontFor(TextRole.Body) != null && ThemeApplier.FontFor(TextRole.Icon) != null,
                "the Inter and icon font assets exist (build them with Tools/Spatial Windows/Build Theme Fonts)");

            string userPath = SettingsStore.Path;
            var root = new GameObject("TypographyTest");
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);
                SettingsStore.Path = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "TypographyTest.json");
                SettingsStore.Replace(new SpatialSettings());

                var contents = new List<Component>();

                var home = manager.Spawn("Home", LauncherContent.WindowSize);
                var launcher = new GameObject().AddComponent<LauncherContent>();
                launcher.Init(home, window, new[]
                {
                    new LauncherContent.Entry { Label = "Image", Color = Color.gray },
                    new LauncherContent.Entry { Label = "Settings", Color = Color.gray },
                });
                contents.Add(launcher);

                var placeholderWindow = manager.Spawn("Placeholder", new Vector2(0.6f, 0.4f));
                var placeholder = new GameObject().AddComponent<PlaceholderContent>();
                placeholder.Init(placeholderWindow, window, Color.gray, "Hello");
                contents.Add(placeholder);

                var settingsWindow = manager.Spawn("Settings", SettingsContent.PreferredSize + Vector2.one * WindowContent.Padding * 2f);
                var settings = new GameObject().AddComponent<SettingsContent>();
                settings.Init(settingsWindow, window, null);
                contents.Add(settings);

                var kbWindow = manager.Spawn("Keyboard", VirtualKeyboardContent.WindowSize);
                var keyboard = new GameObject().AddComponent<VirtualKeyboardContent>();
                keyboard.Init(kbWindow, window, strokes => false, () => null);
                contents.Add(keyboard);

                // Every text under the test root, including inactive ones (window titles, options strips, hidden sections).
                var texts = new HashSet<TMP_Text>(root.GetComponentsInChildren<TMP_Text>(true));
                foreach (var content in contents) texts.UnionWith(content.GetComponentsInChildren<TMP_Text>(true));
                Check(texts.Count > 50, $"the windows and contents create texts to check ({texts.Count})");

                int badFont = 0, tooSmall = 0, runningSmall = 0, secondarySmall = 0, spacing = 0, secondary = 0;
                string firstBad = null;
                float expectedSpacing = (UITheme.LineHeight - 1.2f) * 100f;
                foreach (var text in texts)
                {
                    if (!AllowedFont(text)) { badFont++; firstBad ??= $"{text.name} font {(text.font != null ? text.font.name : "none")}"; }
                    float pt = Pt(text);
                    if (pt < UITheme.MinTextPt - 1e-3f) { tooSmall++; firstBad ??= $"{text.name} is {pt:F1} pt"; }
                    bool icon = text.font != null && text.font.name == ThemeApplier.IconName;
                    if (!icon && pt < UITheme.CaptionPt - 1e-3f) { runningSmall++; firstBad ??= $"{text.name} is {pt:F1} pt"; }
                    if (text.color == UITheme.TextSecondary)
                    {
                        secondary++;
                        if (pt < UITheme.BodyPt - 1e-3f) { secondarySmall++; firstBad ??= $"{text.name} secondary is {pt:F1} pt"; }
                    }
                    if (Mathf.Abs(text.lineSpacing - expectedSpacing) > 1e-3f) { spacing++; firstBad ??= $"{text.name} spacing {text.lineSpacing}"; }
                }
                Check(badFont == 0, $"every text uses Inter or the icon font, no LiberationSans or Light ({badFont} not; {firstBad})");
                Check(tooSmall == 0, $"every text is at least {UITheme.MinTextPt} pt ({tooSmall} smaller)");
                Check(runningSmall == 0, $"every non-icon text is at least {UITheme.CaptionPt} pt ({runningSmall} smaller)");
                Check(secondary > 0 && secondarySmall == 0, $"secondary text is at least {UITheme.BodyPt} pt ({secondary} found, {secondarySmall} smaller)");
                Check(spacing == 0, $"line spacing gives a {UITheme.LineHeight} line height ({spacing} differ)");

                // Keyboard icon keys are 20 pt in the icon font; text keys are 17 pt Headline.
                bool iconKeys = true, textKeys = true;
                foreach (var key in VirtualKeyboardLayout.Keys)
                {
                    if (key.Kind == VirtualKeyKind.Spacer) continue;
                    var label = keyboard.transform.Find($"KeyLabel {key.Id}").GetComponent<TextMeshPro>();
                    if (VirtualKeyboardContent.KeyIcon(key.Id) != null)
                        iconKeys &= Mathf.Abs(Pt(label) - 20f) < 1e-3f && label.font == ThemeApplier.FontFor(TextRole.Icon);
                    else
                        textKeys &= Mathf.Abs(Pt(label) - UITheme.HeadlinePt) < 1e-3f;
                }
                Check(iconKeys, "the keyboard's icon keys are 20 pt in the icon font");
                Check(textKeys, "the keyboard's text keys are 17 pt");

                // Window chrome: the title is the Title role.
                bool titles = true;
                int titleCount = 0;
                foreach (var text in texts)
                    if (text.name == "TitleText") { titleCount++; titles &= Mathf.Abs(Pt(text) - UITheme.TitlePt) < 1e-3f; }
                Check(titleCount >= 4 && titles, $"window titles are the 22 pt Title role ({titleCount} found)");
            }
            finally
            {
                WindowTextField.BlurAll();
                SettingsStore.Path = userPath;
                Object.DestroyImmediate(root);
            }
            yield break;
        }
    }
}
