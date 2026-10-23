using System.Collections;
using System.IO;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Edit-mode test of the glass restyle of the Home tiles, Settings and the keyboard: every icon codepoint exists in the icon
    /// font, white on every tile reaches 4.5:1, hit boxes are at least 44 mm with 8 mm gaps (6 mm between keys), the selected
    /// sidebar row, segmented choice and switches change fill with state, no inner panel is opaque and every shape draws within
    /// the C5a layers below the text. Settings use a file in Temp/, never the user's. Results use a [HomeSettingsKeyboardTest] prefix.
    /// </summary>
    static class HomeSettingsKeyboardTest
    {
        const string GlassPath = "Assets/Materials/SpatialGlass.mat";
        const string WindowPath = "Assets/Materials/SpatialWindow.mat";

        [MenuItem("Tools/Spatial Windows/Test Home Settings Keyboard")]
        static void Run() => EditorTestRunner.Run("HomeSettingsKeyboardTest", Steps(), false);

        [MenuItem("Tools/Spatial Windows/Test Home Settings Keyboard", true)]
        static bool CanRun() => !EditorTestRunner.IsRunning && !Application.isPlaying;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static void SetField(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool Near(float a, float b, float tolerance = 1e-5f) => Mathf.Abs(a - b) <= tolerance;

        static bool Near(Color a, Color b) => Near(a.r, b.r, 1e-3f) && Near(a.g, b.g, 1e-3f) && Near(a.b, b.b, 1e-3f) && Near(a.a, b.a, 1e-3f);

        static Material MaterialOf(Component c) => c.GetComponent<MeshRenderer>().sharedMaterial;

        static Vector2 HitSize(Component c)
        {
            var box = c.GetComponent<BoxCollider>();
            return new Vector2(box.size.x * c.transform.localScale.x, box.size.y * c.transform.localScale.y);
        }

        static float GapBetween(Component a, Component b)
        {
            var sa = HitSize(a) * 0.5f;
            var sb = HitSize(b) * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(a.transform.localPosition.x - b.transform.localPosition.x) - sa.x - sb.x);
            float dy = Mathf.Max(0f, Mathf.Abs(a.transform.localPosition.y - b.transform.localPosition.y) - sa.y - sb.y);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static int TextQueue()
        {
            var go = new GameObject("QueueProbe");
            try
            {
                var text = go.AddComponent<TextMeshPro>();
                return text.fontSharedMaterial != null ? text.fontSharedMaterial.renderQueue : 3000;
            }
            finally { Object.DestroyImmediate(go); }
        }

        // The smallest hit box (both sides) and the smallest gap between any two of the controls.
        static void Measure(Component[] controls, out float minSide, out float minGap)
        {
            minSide = float.MaxValue;
            minGap = float.MaxValue;
            foreach (var c in controls)
            {
                var size = HitSize(c);
                minSide = Mathf.Min(minSide, Mathf.Min(size.x, size.y));
            }
            for (int i = 0; i < controls.Length; i++)
            for (int j = i + 1; j < controls.Length; j++)
                minGap = Mathf.Min(minGap, GapBetween(controls[i], controls[j]));
        }

        static IEnumerator Steps()
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
            var window = AssetDatabase.LoadAssetAtPath<Material>(WindowPath);
            Check(glass != null && window != null, "the glass and window materials exist");
            if (glass == null || window == null) yield break;

            string userPath = SettingsStore.Path;
            var root = new GameObject("HomeSettingsKeyboardTest");
            try
            {
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                var manager = new GameObject("Manager").AddComponent<WindowManager>();
                manager.transform.SetParent(root.transform, false);
                SetField(manager, "windowMaterial", window);
                SetField(manager, "glassMaterial", glass);
                SetField(manager, "head", head);
                int textQueue = TextQueue();
                int minQueue = WindowContent.ShapeQueue;
                int maxQueue = WindowContent.ShapeQueue + WindowContent.FillLayer;
                float hit = UITheme.PtToMetres(UITheme.MinHitTargetMm) - 1e-6f;
                float gap = UITheme.PtToMetres(UITheme.MinHitGapMm) - 1e-6f;

                // 1. Icons: every codepoint is in the icon font.
                var font = ThemeApplier.FontFor(TextRole.Icon);
                Check(font != null, "the Material Icons Round font asset exists");
                string[] icons =
                {
                    UIIcons.Image, UIIcons.Movie, UIIcons.CheckBoxOutlineBlank, UIIcons.Laptop, UIIcons.DarkMode, UIIcons.Theaters,
                    UIIcons.WbSunny, UIIcons.Settings, UIIcons.VisibilityOff, UIIcons.Visibility, UIIcons.Cast, UIIcons.Apps,
                };
                bool allIcons = font != null;
                foreach (var icon in icons) allIcons &= font != null && icon.Length == 1 && font.HasCharacter(icon[0], false, true);
                Check(allIcons, "every icon codepoint passes HasCharacter on the icon font");

                // 2. Home tiles.
                var home = manager.Spawn("Home", LauncherContent.WindowSize);
                home.MoveTo(new Vector3(0f, 0f, 1.5f));
                var launcher = new GameObject().AddComponent<LauncherContent>();
                string[] labels = { "Image", "Video", "Blank", "Laptop", "Void", "Cinema", "Sky", "Settings", "Hide all", "Cast" };
                var colors = new[]
                {
                    new Color(0.20f, 0.62f, 0.45f), new Color(0.78f, 0.32f, 0.42f), new Color(0.42f, 0.44f, 0.52f), new Color(0.30f, 0.42f, 0.70f),
                    new Color(0.10f, 0.12f, 0.22f), new Color(0.45f, 0.10f, 0.14f), new Color(0.35f, 0.58f, 0.85f), new Color(0.45f, 0.47f, 0.52f),
                    new Color(0.35f, 0.30f, 0.45f), new Color(0.62f, 0.38f, 0.16f),
                };
                var entries = new LauncherContent.Entry[labels.Length + 1];
                for (int i = 0; i < labels.Length; i++)
                    entries[i] = new LauncherContent.Entry { Label = labels[i], Color = colors[i] };
                entries[labels.Length] = new LauncherContent.Entry { Label = "Mystery", Icon = null, Color = Color.white };
                launcher.Init(home, window, entries);

                bool contrast = true, tileColors = true, glyphsOk = true, queues = true;
                for (int i = 0; i < entries.Length; i++)
                {
                    var tile = launcher.Buttons[i];
                    var fill = MaterialOf(tile).color;
                    contrast &= UITheme.ContrastRatio(Color.white, fill) >= 4.5f;
                    tileColors &= fill.a == 1f && Near(fill, LauncherContent.TileColor(entries[i].Color));
                    var size = tile.transform.localScale;
                    queues &= MaterialOf(tile).renderQueue >= minQueue && MaterialOf(tile).renderQueue < textQueue;
                    var glyph = launcher.transform.Find($"Glyph {entries[i].Label}").GetComponent<TextMeshPro>();
                    glyphsOk &= glyph.text == LauncherContent.IconFor(entries[i].Label) && glyph.text.Length == 1 && glyph.color == Color.white
                                && Near(glyph.fontSize, UITheme.PtToMetres(32f) * ThemeApplier.TmpWorldScale)
                                && (font == null || glyph.font == font);
                    glyphsOk &= Near(size.x, UITheme.PtToMetres(72f)) && Near(size.y, UITheme.PtToMetres(72f));
                }
                Check(contrast, "white on every tile colour is at least 4.5:1");
                Check(tileColors, "tiles keep each app's colour, darkened only as far as needed");
                Check(glyphsOk, "each tile is 72 pt with a white 32 pt icon glyph, and an unknown entry gets the fallback icon");
                Check(LauncherContent.IconFor("Mystery") == UIIcons.Apps && LauncherContent.IconFor("Cast") == UIIcons.Cast
                      && LauncherContent.IconFor("Hide all") == UIIcons.VisibilityOff, "icons are mapped by entry name");
                // An entry's own Icon wins over the label mapping.
                entries[0].Icon = UIIcons.Cast;
                Check(LauncherContent.IconFor(entries[0].Label) == UIIcons.Image, "IconFor is only the fallback by label");
                launcher.SetEntryText(8, "Show all", UIIcons.Visibility);
                Check(launcher.transform.Find("Glyph Hide all").GetComponent<TextMeshPro>().text == UIIcons.Visibility
                      && launcher.EntryLabel(8) == "Show all", "Hide all becomes Show all with the visible icon");
                var captionOk = true;
                bool captionsFit = true;
                var captionFont = ThemeApplier.FontFor(TextRole.Caption);
                foreach (var label in labels)
                {
                    var caption = launcher.transform.Find($"Label {label}").GetComponent<TextMeshPro>();
                    captionOk &= caption.color == UITheme.TextPrimary && Near(caption.fontSize, UITheme.PtToMetres(UITheme.CaptionPt) * ThemeApplier.TmpWorldScale)
                                 && (captionFont == null || caption.font == captionFont);
                    caption.ForceMeshUpdate();
                    captionsFit &= caption.preferredWidth <= caption.rectTransform.sizeDelta.x && !caption.isTextOverflowing;
                }
                Check(captionOk && queues, "captions are TextPrimary in the 12 pt Caption role and tiles draw within the shape layers below the text");
                Check(captionsFit, "no caption overflows its cell (no ellipsis)");
                int rowsUsed = 0;
                float lastY = float.NaN;
                for (int i = 0; i < launcher.Buttons.Count; i++)
                {
                    float y = launcher.Buttons[i].transform.localPosition.y;
                    if (float.IsNaN(lastY) || !Near(y, lastY)) { rowsUsed++; lastY = y; }
                }
                float bottom = launcher.Buttons[launcher.Buttons.Count - 1].transform.localPosition.y - UITheme.PtToMetres(72f) * 0.5f - 0.03f;
                Check(rowsUsed == 3 && launcher.Buttons[3].transform.localPosition.y == launcher.Buttons[0].transform.localPosition.y
                      && bottom >= -home.Size.y * 0.5f + WindowContent.Padding - 1e-4f, "10 tiles fit in 4 columns and 3 rows inside the window");
                var tiles = new Component[launcher.Buttons.Count];
                for (int i = 0; i < tiles.Length; i++) tiles[i] = launcher.Buttons[i];
                Measure(tiles, out float tileSide, out float tileGap);
                Check(tileSide >= hit && tileGap >= gap, $"tile hit boxes are at least 44 mm ({tileSide * 1000f:F1}) and 8 mm apart ({tileGap * 1000f:F1})");

                // 3. Settings.
                SettingsStore.Path = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "HomeSettingsKeyboardTest.json");
                SettingsStore.Replace(new SpatialSettings());
                var settingsWindow = manager.Spawn("Settings", SettingsContent.PreferredSize + Vector2.one * WindowContent.Padding * 2f);
                var settings = new GameObject().AddComponent<SettingsContent>();
                settings.Init(settingsWindow, window, null);
                var content = settings.transform;

                var panel = content.Find("SettingsPanel");
                Check(MaterialOf(panel).color.a == 0f && SettingsContent.PanelColor.a == 0f && VirtualKeyboardContent.PanelColor.a == 0f,
                    "the settings and keyboard inner panels are transparent, not opaque");

                bool tabsOk = settings.Tabs.Count == 9;
                for (int i = 0; i < settings.Tabs.Count; i++)
                {
                    var size = HitSize(settings.Tabs[i]);
                    tabsOk &= Near(size.x, UITheme.PtToMetres(160f)) && Near(size.y, UITheme.PtToMetres(44f));
                    var shape = settings.Tabs[i].GetComponent<RoundedShape>();
                    tabsOk &= shape != null && shape.Capsule;
                }
                Check(tabsOk, "sidebar rows are 160 x 44 pt capsules");

                settings.Select(0);
                bool selected = Near(MaterialOf(settings.Tabs[0]).color, UITheme.AccentFill) && MaterialOf(settings.Tabs[1]).color.a == 0f;
                var label0 = content.Find("TabLabel Laptop").GetComponent<TextMeshPro>();
                var label1 = content.Find("TabLabel Display").GetComponent<TextMeshPro>();
                selected &= label0.color == Color.white && label1.color == UITheme.TextPrimary
                            && UITheme.ContrastRatio(Color.white, UITheme.AccentFill) >= 4.5f;
                settings.Select(1);
                selected &= Near(MaterialOf(settings.Tabs[1]).color, UITheme.AccentFill) && MaterialOf(settings.Tabs[0]).color.a == 0f;
                Check(selected, "the selected sidebar row is AccentFill with white text; the others are transparent");

                // Segmented control: Wi-Fi / Wired (USB) on one raised capsule track.
                settings.Select("Laptop");
                var link = content.Find("Section Laptop/Row Link");
                var track = link.Find("Track");
                var wifi = link.Find("Option Wi-Fi");
                var wired = link.Find("Option Wired (USB)");
                Check(track != null && Near(MaterialOf(track).color, UITheme.GlassRaised) && track.GetComponent<RoundedShape>().Capsule,
                    "the Link choice sits on one GlassRaised capsule track");
                bool segment = Near(MaterialOf(wifi).color, UITheme.AccentFill) && MaterialOf(wired).color.a == 0f;
                SettingsStore.Update(x => x.laptop.wired = true);
                settings.Select("Laptop");
                segment &= Near(MaterialOf(wired).color, UITheme.AccentFill) && MaterialOf(wifi).color.a == 0f;
                Check(segment, "the segmented control moves its AccentFill segment with the selection");

                // Switches.
                settings.Select("Gestures");
                var row = content.Find("Section Gestures/Row Fist grab");
                var switchTrack = row.Find("SwitchTrack");
                var knob = row.Find("SwitchKnob");
                bool on = SettingsStore.Current.hands.fistGrab;
                float offX = 0f, onX = 0f;
                Color offColor = default, onColor = default;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool state = SettingsStore.Current.hands.fistGrab;
                    if (state) { onColor = MaterialOf(switchTrack).color; onX = knob.localPosition.x; }
                    else { offColor = MaterialOf(switchTrack).color; offX = knob.localPosition.x; }
                    SettingsStore.Update(x => x.hands.fistGrab = !state);
                    settings.Select("Gestures");
                }
                Check(Near(onColor, UITheme.AccentFill) && Near(offColor, UITheme.GlassRaised) && onX > offX,
                    "a switch is AccentFill with the knob right when on, GlassRaised with the knob left when off");
                SettingsStore.Update(x => x.hands.fistGrab = on);
                Check(Near(switchTrack.localScale.x, UITheme.PtToMetres(52f)) && Near(switchTrack.localScale.y, UITheme.PtToMetres(32f))
                      && Near(knob.localScale.x, UITheme.PtToMetres(28f)) && MaterialOf(knob).color == Color.white,
                    "a switch is 52 x 32 pt with a white 28 pt knob");

                // Hit targets and layers across every section.
                bool settingsHits = true, settingsQueues = true;
                float settingsSide = float.MaxValue, settingsGap = float.MaxValue;
                foreach (var name in SettingsContent.SectionNames)
                {
                    var controls = new System.Collections.Generic.List<Component>(settings.Tabs);
                    foreach (var c in content.Find("Section " + name).GetComponentsInChildren<WindowControl>(true))
                        if (c.gameObject.activeInHierarchy || name != "Laptop") controls.Add(c);
                    Measure(controls.ToArray(), out float side, out float g);
                    settingsSide = Mathf.Min(settingsSide, side);
                    settingsGap = Mathf.Min(settingsGap, g);
                }
                settingsHits = settingsSide >= hit && settingsGap >= gap;
                foreach (var r in content.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == null || !m.HasProperty("_Flat") || m.GetFloat("_Flat") < 0.5f) continue;
                    settingsQueues &= m.renderQueue >= minQueue && m.renderQueue <= maxQueue && m.renderQueue < textQueue;
                }
                Check(settingsHits, $"settings hit boxes are at least 44 mm ({settingsSide * 1000f:F1}) and 8 mm apart ({settingsGap * 1000f:F1})");
                Check(settingsQueues, "settings shapes draw within the C5a layers, below the text");

                // 4. Keyboard.
                var kbWindow = manager.Spawn("Keyboard", VirtualKeyboardContent.WindowSize);
                var keyboard = new GameObject().AddComponent<VirtualKeyboardContent>();
                keyboard.Init(kbWindow, window, strokes => false, () => null);
                var remote = new GameObject().AddComponent<RemoteKeyboard>();
                remote.transform.SetParent(root.transform, false);
                SetField(remote, "manager", manager);
                var spawned = remote.Open();
                bool spawnedOk = spawned != null && Near(spawned.Size.x, VirtualKeyboardContent.WindowSize.x) && Near(spawned.Size.y, VirtualKeyboardContent.WindowSize.y);
                var spawnedKeys = new System.Collections.Generic.List<Component>();
                if (remote.KeyboardContent != null)
                    foreach (var key in VirtualKeyboardLayout.Keys) spawnedKeys.Add(remote.KeyboardContent.Button(key.Id));
                Measure(spawnedKeys.ToArray(), out float spawnedSide, out _);
                Check(spawnedOk && spawnedSide >= hit, $"the keyboard RemoteKeyboard opens has keys of at least 44 mm ({spawnedSide * 1000f:F1})");
                var keyControls = new System.Collections.Generic.List<Component>();
                bool keyColors = true, keyQueues = true;
                foreach (var key in VirtualKeyboardLayout.Keys)
                {
                    if (key.Kind == VirtualKeyKind.Spacer) continue;
                    var button = keyboard.Button(key.Id);
                    keyControls.Add(button);
                    var fill = MaterialOf(button).color;
                    keyColors &= key.Id == "Enter" ? Near(fill, UITheme.AccentFill) : Near(fill, UITheme.KeyFill);
                    keyQueues &= MaterialOf(button).renderQueue >= minQueue && MaterialOf(button).renderQueue < textQueue;
                }
                Check(keyColors && keyQueues, "keys are KeyFill and Return is AccentFill, drawn below the text");
                // Key labels: 17 pt, white on KeyFill reaches 4.5:1 on the worst glass, and none overflows its key.
                var wall = UITheme.CompositeOver(UITheme.GlassFallback, Color.white);
                var keyOnGlass = UITheme.CompositeOver(UITheme.KeyFill, wall);
                Check(UITheme.ContrastRatio(UITheme.TextPrimary, keyOnGlass) >= 4.5f && UITheme.KeyFill.a < UITheme.GlassRaised.a
                      && UITheme.ContrastRatio(UITheme.TextPrimary, UITheme.CompositeOver(UITheme.KeyFill, wall)) >= 4.5f,
                    $"white key labels on KeyFill over worst-case glass are at least 4.5:1 ({UITheme.ContrastRatio(UITheme.TextPrimary, keyOnGlass):F2})");
                bool labelsFit = true, labelSize = true;
                var iconFont = ThemeApplier.FontFor(TextRole.Icon);
                foreach (var key in VirtualKeyboardLayout.Keys)
                {
                    var label = keyboard.transform.Find($"KeyLabel {key.Id}").GetComponent<TextMeshPro>();
                    labelSize &= label.fontSize / ThemeApplier.TmpWorldScale * 1000f >= 17f - 1e-3f;
                    label.ForceMeshUpdate();
                    labelsFit &= label.preferredWidth <= label.rectTransform.sizeDelta.x && !label.isTextOverflowing;
                }
                Check(labelSize, "key labels are at least 17 pt");
                Check(labelsFit, "no key label overflows its key");
                bool iconKeys = true;
                foreach (var id in new[] { "Up", "Down", "Left", "Right", "Home" })
                {
                    string icon = VirtualKeyboardContent.KeyIcon(id);
                    iconKeys &= icon != null && keyboard.transform.Find($"KeyLabel {id}").GetComponent<TextMeshPro>().text == icon
                                && (iconFont == null || iconFont.HasCharacter(icon[0], false, true));
                }
                Check(iconKeys, "the arrow and Home keys show icons present in the icon font");
                Measure(keyControls.ToArray(), out float keySide, out float keyGap);
                Check(keySide >= hit && keyGap >= UITheme.PtToMetres(6f) - 1e-6f && Near(VirtualKeyboardContent.KeyGap, UITheme.PtToMetres(6f)),
                    $"keys are at least 44 mm ({keySide * 1000f:F1}) with 6 mm gaps ({keyGap * 1000f:F1})");
                VirtualKey shift = default;
                foreach (var k in VirtualKeyboardLayout.Keys)
                    if (k.Id == "Shift") shift = k;
                keyboard.Press(shift);
                bool shiftOn = Near(MaterialOf(keyboard.Button("Shift")).color, UITheme.AccentFill);
                keyboard.Press(shift);
                Check(shiftOn && Near(MaterialOf(keyboard.Button("Shift")).color, UITheme.KeyFill), "an active modifier key is AccentFill and goes back to KeyFill");
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
