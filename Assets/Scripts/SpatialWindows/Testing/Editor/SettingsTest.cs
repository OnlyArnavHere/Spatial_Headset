using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing.Editor
{
    /// <summary>
    /// Play-mode test for settings: the file (defaults, round trip, old and broken files, clamping, atomic save), the
    /// service (debounced saving, applying window, hand and immersion settings, the viewport offset maths) and the
    /// Settings window opened from the launcher, driven with mouse clicks. It works on a settings file in Temp/ and puts
    /// the user's own settings back afterwards. Results use a [SettingsTest] prefix.
    /// </summary>
    static class SettingsTest
    {
        const float Settle = 0.6f;
        static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "SettingsTest");

        [MenuItem("Tools/Spatial Windows/Test Settings")]
        static void Run() => EditorTestRunner.Run("SettingsTest", Steps());

        [MenuItem("Tools/Spatial Windows/Test Settings", true)]
        static bool CanRun() => Application.isPlaying && !EditorTestRunner.IsRunning;

        static void Check(bool condition, string what) => EditorTestRunner.Check(condition, what);

        static IEnumerator Steps()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            Directory.CreateDirectory(Folder);
            FileChecks();

            var launcher = Object.FindFirstObjectByType<LauncherWindow>();
            var manager = Object.FindFirstObjectByType<WindowManager>();
            var mouse = Object.FindFirstObjectByType<MouseWindowInput>();
            var cam = Camera.main;
            if (launcher == null || manager == null || mouse == null || cam == null || launcher.Settings == null)
            {
                Check(false, "scene has a LauncherWindow (with its SettingsService), WindowManager, MouseWindowInput and main camera");
                yield break;
            }

            string userPath = SettingsStore.Path;
            launcher.Settings.SaveNow();
            int previousInput = WindowTestUtil.SelectInput<MouseWindowInput>(manager);
            var immersion = launcher.Immersion;
            float userLevel = immersion.Level;
            var userEnvironment = immersion.Environment;
            SpatialWindow spawned = null;
            try
            {
                SettingsStore.Path = Path.Combine(Folder, "service.json");
                SettingsStore.Replace(new SpatialSettings());
                yield return Service(launcher, manager, w => spawned = w);
                yield return Window(launcher, manager, mouse, cam);
            }
            finally
            {
                if (spawned != null) manager.Close(spawned);
                if (launcher.SettingsWindow != null) manager.Close(launcher.SettingsWindow);
                launcher.Settings.SaveNow(); // into the test file
                SettingsStore.Path = userPath;
                SettingsStore.Replace(SettingsStore.Load(userPath));
                immersion.SetEnvironment(userEnvironment);
                immersion.SetLevel(userLevel);
                launcher.Settings.SaveNow();
                if (previousInput >= 0) manager.SetActiveInput(previousInput);
            }
        }

        static void FileChecks()
        {
            string file = Path.Combine(Folder, "settings.json");
            var defaults = SettingsStore.Load(file);
            Check(!File.Exists(file) && defaults.version == SpatialSettings.CurrentVersion && !defaults.laptop.wired
                  && defaults.display.ipdMm == 64f && defaults.hands.dominantHand == Handedness.Right && defaults.voice.language == "en-US",
                "a missing file gives the defaults (and isn't created by reading)");

            var changed = new SpatialSettings();
            changed.laptop.wired = true;
            changed.display.brightness = 0.4f;
            changed.display.ipdMm = 61.5f;
            changed.display.viewportOffsetMm = new Vector2(-2.5f, 3f);
            changed.hands.dominantHand = Handedness.Left;
            changed.hands.sensitivity = 1.5f;
            changed.hands.palmPush = false;
            changed.windows.defaultCurve = 0.7f;
            changed.windows.defaultAspect = AspectPreset.Ultrawide21x9;
            changed.immersion.environment = ImmersiveEnvironment.Cinema;
            changed.immersion.level = 0.35f;
            changed.immersion.resumeOnStart = true;
            changed.voice.language = "de-DE";
            changed.voice.preferOnDevice = false;
            SettingsStore.Save(changed, file);
            var loaded = SettingsStore.Load(file);
            Check(JsonUtility.ToJson(loaded) == JsonUtility.ToJson(changed) && !File.Exists(file + ".tmp"),
                $"every section survives a save and load ({new FileInfo(file).Length} bytes, no temporary file left)");

            changed.laptop.wired = false;
            SettingsStore.Save(changed, file);
            Check(!SettingsStore.Load(file).laptop.wired, "saving again replaces the file");

            File.WriteAllText(file, "{\"version\":1,\"hands\":{\"sensitivity\":1.25}}");
            var partial = SettingsStore.Load(file);
            Check(Mathf.Approximately(partial.hands.sensitivity, 1.25f) && partial.hands.pinch && partial.display.ipdMm == 64f && partial.voice.language == "en-US",
                "an older file with missing fields keeps what it has and defaults the rest");

            File.WriteAllText(file, "{\"display\":{\"brightness\":5,\"ipdMm\":10},\"windows\":{\"defaultCurve\":-1},\"laptop\":{\"videoPort\":0,\"host\":\"old\",\"pairCode\":\"x\"},\"hands\":{\"sensitivity\":9}}");
            var clamped = SettingsStore.Load(file);
            Check(clamped.display.brightness == 1f && clamped.display.ipdMm == 54f && clamped.windows.defaultCurve == 0f
                  && clamped.hands.sensitivity == 2f, "out-of-range values are clamped on load, and the old laptop host/port/code fields are ignored");

            File.WriteAllText(file, "{\"version\":1,\"laptop\":{\"host\":\"192.168.1.5\",\"videoPort\":9000,\"pairCode\":\"123456\",\"wired\":true},\"hands\":{\"sensitivity\":1.5}}");
            var oldLaptop = SettingsStore.Load(file);
            Check(oldLaptop != null && oldLaptop.laptop.wired && Mathf.Approximately(oldLaptop.hands.sensitivity, 1.5f) && !File.Exists(file + ".bad"),
                "an old file with host, videoPort and pairCode loads without error, keeping its other fields");

            File.WriteAllText(file, "{ this is not json");
            var broken = SettingsStore.Load(file);
            Check(broken != null && !broken.laptop.wired && File.Exists(file + ".bad"),
                "a broken file gives the defaults and is kept aside as settings.json.bad");
        }

        static IEnumerator Service(LauncherWindow launcher, WindowManager manager, System.Action<SpatialWindow> spawned)
        {
            var service = launcher.Settings;
            string file = SettingsStore.Path;

            SettingsStore.Update(s => s.voice.language = "fr-FR");
            bool pending = service.SavePending;
            yield return 0.8f;
            Check(pending && !service.SavePending && File.Exists(file) && SettingsStore.Load(file).voice.language == "fr-FR",
                "a change is saved to the file about half a second later");

            SettingsStore.Update(s =>
            {
                s.windows.defaultCurve = 0.5f;
                s.windows.defaultAspect = AspectPreset.Classic4x3;
            });
            var window = manager.Spawn("Settings Test Window");
            spawned(window);
            Check(Mathf.Approximately(window.Curve, 0.5f) && window.Aspect == AspectPreset.Classic4x3
                  && Mathf.Abs(AspectPresets.ContentAspect(window.Size) - 4f / 3f) < 1e-3f,
                $"new windows take the default curve and aspect ({window.Curve:F2}, {AspectPresets.Label(window.Aspect)}, content {AspectPresets.ContentAspect(window.Size):F3})");
            manager.Close(window);
            SettingsStore.Update(s =>
            {
                s.windows.defaultCurve = 0f;
                s.windows.defaultAspect = AspectPreset.Free;
            });

            SettingsStore.Update(s =>
            {
                s.hands.sensitivity = 1.5f;
                s.hands.pinch = false;
                s.hands.dominantHand = Handedness.Left;
            });
            var hands = launcher.GetComponent<HandWindowInput>();
            var simulated = launcher.GetComponent<SimulatedHandSource>();
            Check(hands != null && hands.Sensitivity == 1.5f && !hands.PinchEnabled && simulated != null && simulated.DominantHand == Handedness.Left,
                "hand settings reach the hand input: sensitivity, pinch off, left-handed simulated hand");
            SettingsStore.Update(s =>
            {
                s.hands.sensitivity = 1f;
                s.hands.pinch = true;
                s.hands.dominantHand = Handedness.Right;
            });

            var immersion = launcher.Immersion;
            SettingsStore.Update(s => s.immersion.environment = ImmersiveEnvironment.Sky);
            immersion.SetLevel(0.4f);
            Check(immersion.Environment == ImmersiveEnvironment.Sky && Mathf.Approximately(SettingsStore.Current.immersion.level, 0.4f),
                "the environment setting reaches the immersion controller, and the dial's level is recorded in the settings");
            immersion.SetLevel(0f);

            var shift = SettingsService.ViewportShift(new Vector2(10f, -5f), new Vector2(2556f, 1179f), 460f);
            float perMm = 460f / 25.4f;
            Check(Mathf.Abs(shift.x - 10f * perMm / 2556f) < 1e-6f && Mathf.Abs(shift.y + 5f * perMm / 1179f) < 1e-6f
                  && SettingsService.ViewportShift(Vector2.one, Vector2.one, 0f) == Vector2.zero,
                $"a viewport offset in mm becomes a fraction of the screen from its DPI ({shift.x:F4}, {shift.y:F4}; unknown DPI: none)");

        }

        static IEnumerator Window(LauncherWindow launcher, WindowManager manager, MouseWindowInput mouse, Camera cam)
        {
            launcher.OpenLauncher();
            yield return Settle;
            var icons = launcher.LauncherContent.Buttons;
            Check(icons.Count >= 8, $"the launcher has a Settings icon ({icons.Count} icons)");
            if (icons.Count < 8) yield break;
            Click(mouse, cam, icons[7].transform.position);
            yield return Settle;
            var window = launcher.SettingsWindow;
            var settings = window != null ? window.GetComponentInChildren<SettingsContent>() : null;
            Check(settings != null && manager.FocusedWindow == window && settings.Tabs.Count == SettingsContent.SectionNames.Length && settings.Tabs.Count == 8,
                "the Settings icon opens the Settings window with 8 sections (Gestures split from Hands)");
            if (settings == null) yield break;

            Click(mouse, cam, settings.Tabs[2].transform.position);
            yield return 0.2f;
            Check(settings.SelectedSection == "Hands", $"clicking a tab shows its section ({settings.SelectedSection})");
            Click(mouse, cam, settings.Control("Hands", "Pinch", "On").transform.position);
            yield return 0.1f;
            Check(!SettingsStore.Current.hands.pinch, "clicking the Pinch toggle turns pinching off");
            Click(mouse, cam, settings.Control("Hands", "Pinch", "On").transform.position);
            var slider = settings.Control("Hands", "Sensitivity", "Slider").transform;
            Click(mouse, cam, slider.TransformPoint(new Vector3(0f, 0f, 0f)));
            yield return 0.1f;
            Check(Mathf.Abs(SettingsStore.Current.hands.sensitivity - 1.25f) < 0.03f && SettingsStore.Current.hands.pinch,
                $"clicking the middle of the Sensitivity slider sets 1.25x (\"{settings.ValueText("Hands", "Sensitivity")}\")");
            Click(mouse, cam, settings.Control("Hands", "Dominant hand", "Left").transform.position);
            yield return 0.1f;
            Check(SettingsStore.Current.hands.dominantHand == Handedness.Left, "the Left button makes the left hand dominant");

            settings.Select("Windows");
            yield return 0.2f;
            Click(mouse, cam, settings.Control("Windows", "Default aspect", "21:9").transform.position);
            yield return 0.1f;
            Check(SettingsStore.Current.windows.defaultAspect == AspectPreset.Ultrawide21x9, "the 21:9 button sets the default aspect");

            settings.Select("About");
            yield return 0.3f;
            Check(settings.ValueText("About", "Thermal state") == "n/a (editor)" && settings.ValueText("About", "Versions").Contains(Application.unityVersion)
                  && settings.ValueText("About", "Bluetooth") == DeviceStatus.BluetoothNote,
                $"About shows the thermal state, versions and Bluetooth note (battery \"{settings.ValueText("About", "Battery")}\")");

            settings.Select("Laptop");
            yield return 0.2f;
            int windowsBefore = manager.Windows.Count;
            Click(mouse, cam, settings.Control("Laptop", "Connection", "Open Laptop picker").transform.position);
            yield return 0.5f;
            Check(manager.Windows.Count == windowsBefore + 1 && manager.FocusedWindow != null && manager.FocusedWindow.Title == "Laptop"
                  && manager.FocusedWindow.GetComponentInChildren<LaptopPickerContent>() != null,
                "Connection > Open Laptop picker opens the Laptop picker");
            Check(settings.ValueText("Laptop", "Status") == SettingsContent.LinkStatus(), "the status row shows the headset's listen state");
            manager.Close(manager.FocusedWindow);

            // The three "Paired" rows fit inside the window.
            foreach (var rowName in new[] { "Row Paired laptops", "Row Paired #2", "Row Paired #3" })
            {
                Transform row = null;
                foreach (var t in settings.GetComponentsInChildren<Transform>(true))
                    if (t.name == rowName) row = t;
                var value = row != null ? row.Find("Value") : null;
                float bottom = value != null ? settings.transform.InverseTransformPoint(value.position).y - 0.018f : float.NegativeInfinity;
                Check(bottom >= -SettingsContent.PreferredSize.y * 0.5f, $"{rowName} fits in the Settings window ({bottom:F3} m from the middle)");
            }

            yield return 0.8f;
            var saved = SettingsStore.Load(SettingsStore.Path);
            Check(saved.hands.dominantHand == Handedness.Left && saved.windows.defaultAspect == AspectPreset.Ultrawide21x9,
                "what was changed in the window is in the settings file");

            int count = manager.Windows.Count;
            Check(launcher.OpenSettings() == window && manager.Windows.Count == count, "opening Settings again brings the same window back");
        }

        static void Click(MouseWindowInput mouse, Camera cam, Vector3 worldPoint)
        {
            Vector2 screen = cam.WorldToScreenPoint(worldPoint);
            mouse.PressAt(screen);
            mouse.ReleaseAt(screen);
        }
    }
}
