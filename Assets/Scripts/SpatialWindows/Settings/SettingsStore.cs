using System;
using System.IO;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Loads and saves <see cref="SpatialSettings"/> as JSON in <see cref="Path"/> (settings.json in the app's persistent
    /// data folder). Saving writes a temporary file and then replaces the old one, so a crash mid-save never leaves a
    /// half-written file. A missing, empty or unreadable file gives the defaults (an unreadable one is kept aside as
    /// settings.json.bad). Values are clamped to their ranges on load. <see cref="Changed"/> fires after every change made
    /// through <see cref="Update"/>; saving is the caller's choice (<see cref="SettingsService"/> saves shortly after).
    /// </summary>
    public static class SettingsStore
    {
        static SpatialSettings current;
        static string path;

        /// <summary>The settings file. Tests point it elsewhere so they never touch the user's settings.</summary>
        public static string Path
        {
            get => path ??= System.IO.Path.Combine(Application.persistentDataPath, "settings.json");
            set
            {
                path = value;
                current = null;
            }
        }

        /// <summary>The settings in use, loaded from <see cref="Path"/> the first time.</summary>
        public static SpatialSettings Current => current ??= Load(Path);

        /// <summary>Something changed (through <see cref="Update"/> or <see cref="Replace"/>).</summary>
        public static event Action<SpatialSettings> Changed;

        /// <summary>Changes the settings and raises <see cref="Changed"/>.</summary>
        public static void Update(Action<SpatialSettings> change)
        {
            change(Current);
            Sanitize(Current);
            Changed?.Invoke(Current);
        }

        public static void Replace(SpatialSettings settings)
        {
            current = settings ?? new SpatialSettings();
            Sanitize(current);
            Changed?.Invoke(current);
        }

        /// <summary>Reads settings from a file; defaults if it's missing or unreadable.</summary>
        public static SpatialSettings Load(string file)
        {
            SpatialSettings loaded = null;
            try
            {
                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file);
                    if (!string.IsNullOrWhiteSpace(json)) loaded = JsonUtility.FromJson<SpatialSettings>(json);
                }
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Settings] {file} couldn't be read ({error.Message}); using defaults and keeping it as .bad");
                try { File.Copy(file, file + ".bad", true); } catch (Exception) { /* best effort */ }
            }
            loaded ??= new SpatialSettings();
            Sanitize(loaded);
            return loaded;
        }

        /// <summary>Writes the current settings to <see cref="Path"/>, replacing the file only once the new one is complete.</summary>
        public static void Save() => Save(Current, Path);

        public static void Save(SpatialSettings settings, string file)
        {
            settings.version = SpatialSettings.CurrentVersion;
            string folder = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(settings, true));
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }

        /// <summary>Clamps every value to its range and fills in anything missing.</summary>
        public static void Sanitize(SpatialSettings s)
        {
            s.laptop ??= new LaptopSettings();
            s.display ??= new DisplaySettings();
            s.hands ??= new HandSettings();
            s.windows ??= new WindowSettings();
            s.immersion ??= new ImmersionSettings();
            s.voice ??= new VoiceSettings();
            s.performance ??= new PerformanceSettings();

            s.display.brightness = Mathf.Clamp(s.display.brightness, 0.1f, 1f);
            s.display.ipdMm = Mathf.Clamp(s.display.ipdMm, 54f, 74f);
            s.display.viewportOffsetMm = new Vector2(Mathf.Clamp(s.display.viewportOffsetMm.x, -10f, 10f),
                Mathf.Clamp(s.display.viewportOffsetMm.y, -10f, 10f));
            s.display.passthroughZoom = StereoPassthrough.ClampZoom(s.display.passthroughZoom);
            s.hands.sensitivity = Mathf.Clamp(s.hands.sensitivity, 0.5f, 2f);
            if (!Enum.IsDefined(typeof(Handedness), s.hands.dominantHand)) s.hands.dominantHand = Handedness.Right;
            if (s.hands.visionFrameInterval < 1 || s.hands.visionFrameInterval > 3)
                s.hands.visionFrameInterval = HoloKitHandSource.DefaultVisionFrameInterval;
            if (!Enum.IsDefined(typeof(PointerMode), s.hands.pointerMode)) s.hands.pointerMode = PointerMode.Absolute;
            s.hands.relativeGain = float.IsNaN(s.hands.relativeGain) ? 1.5f : Mathf.Clamp(s.hands.relativeGain, 0.5f, 3f);
            s.hands.calibration ??= new HandCalibration();
            s.hands.calibration.Sanitize();
            s.windows.defaultCurve = Mathf.Clamp01(s.windows.defaultCurve);
            if (!Enum.IsDefined(typeof(AspectPreset), s.windows.defaultAspect)) s.windows.defaultAspect = AspectPreset.Free;
            if (!Enum.IsDefined(typeof(ImmersiveEnvironment), s.immersion.environment)) s.immersion.environment = ImmersiveEnvironment.Void;
            s.immersion.level = Mathf.Clamp01(s.immersion.level);
            if (string.IsNullOrEmpty(s.voice.language)) s.voice.language = "en-US";
        }
    }
}
