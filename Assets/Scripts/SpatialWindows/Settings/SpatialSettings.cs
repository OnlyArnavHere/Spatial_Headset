using System;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Everything the Settings window changes, saved as one JSON file by <see cref="SettingsStore"/>. Fields missing from
    /// an older file keep these defaults; unknown fields are ignored. About (battery, thermal state, versions) is read
    /// live by <see cref="DeviceStatus"/> and not stored.
    /// </summary>
    [Serializable]
    public class SpatialSettings
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public LaptopSettings laptop = new LaptopSettings();
        public DisplaySettings display = new DisplaySettings();
        public HandSettings hands = new HandSettings();
        public WindowSettings windows = new WindowSettings();
        public ImmersionSettings immersion = new ImmersionSettings();
        public VoiceSettings voice = new VoiceSettings();
        public PerformanceSettings performance = new PerformanceSettings();

        public SpatialSettings Clone() => JsonUtility.FromJson<SpatialSettings>(JsonUtility.ToJson(this));
    }

    [Serializable]
    public class LaptopSettings
    {
        /// <summary>
        /// Wired (USB): the headset iPhone's Personal Hotspot over a cable; the laptop dials 172.20.10.1 (see
        /// <see cref="UsbHotspot"/> and Doc/wired.md). False: Wi-Fi (the same network).
        /// </summary>
        public bool wired;
    }

    [Serializable]
    public class DisplaySettings
    {
        /// <summary>Screen brightness, 0.1..1 (the phone's own brightness on the device).</summary>
        public float brightness = 1f;
        /// <summary>Distance between the user's pupils, millimetres (HoloKit's stereo, 54..74).</summary>
        public float ipdMm = 64f;
        /// <summary>Shift of both stereo viewports on the screen, millimetres, for a printed helmet's lens position.</summary>
        public Vector2 viewportOffsetMm;
        /// <summary>
        /// Size of the camera image (passthrough), 0.5..1.5: 1 matches the camera's field of view (real-world scale),
        /// less shrinks it with black around it, more magnifies it. Windows and hand tracking don't change.
        /// </summary>
        public float passthroughZoom = 1f;

        /// <summary>Settings > Display > Zoom preset: the choices, in order, and their names.</summary>
        public static readonly float[] ZoomPresets = { 0.7f, 1f, 1.3f };
        public static readonly string[] ZoomPresetNames = { "Wide", "Normal", "Close" };

        /// <summary>The preset a zoom is at, or -1.</summary>
        public static int ZoomPresetIndex(float zoom)
        {
            for (int i = 0; i < ZoomPresets.Length; i++)
                if (Mathf.Abs(zoom - ZoomPresets[i]) < 0.005f) return i;
            return -1;
        }

        /// <summary>The quick toggle's next zoom: the next larger preset, back to the first after the last.</summary>
        public static float NextZoomPreset(float zoom)
        {
            foreach (float preset in ZoomPresets)
                if (preset > zoom + 0.005f) return preset;
            return ZoomPresets[0];
        }

        /// <summary>"Wide (0.70x)", or just "0.85x" between presets.</summary>
        public static string ZoomLabel(float zoom)
        {
            int preset = ZoomPresetIndex(zoom);
            return preset >= 0 ? $"{ZoomPresetNames[preset]} ({zoom:0.00}x)" : $"{zoom:0.00}x";
        }
    }

    [Serializable]
    public class HandSettings
    {
        public Handedness dominantHand = Handedness.Right;
        /// <summary>0.5..2: how strongly hand motion moves windows (and how small a motion counts as tremor).</summary>
        public float sensitivity = 1f;
        public bool pinch = true;
        public bool fistGrab = true;
        public bool palmPush = true;
        public bool twoHandStretch = true;
        public bool crossSweepHide = true;
        /// <summary>Shows hand state and gesture recognizer outputs on screen, for tuning thresholds.</summary>
        public bool debugOverlay;
        /// <summary>
        /// Apple Vision looks for hands on every this-many-th camera frame, 1..3 (about 60, 30 or 20 Hz at ARKit's 60
        /// fps). More often follows hands more closely and heats the phone more.
        /// </summary>
        public int visionFrameInterval = HoloKitHandSource.DefaultVisionFrameInterval;
        /// <summary>Absolute: the hand's own ray. Relative: the cursor moves by the hand's motion and freezes during a pinch.</summary>
        public PointerMode pointerMode = PointerMode.Absolute;
        /// <summary>Relative pointer: cursor rotation per unit of hand rotation, 0.5..3.</summary>
        public float relativeGain = 1.5f;
        /// <summary>The user's own thresholds and hand length from Hand Setup (defaults until calibrated).</summary>
        public HandCalibration calibration = new HandCalibration();

        /// <summary>Settings > Hands > Tracking rate: the choices, in order, and what each one costs in heat.</summary>
        public static readonly int[] VisionFrameIntervals = { 3, 2, 1 };
        public static readonly string[] VisionRateLabels = { "20 Hz", "30 Hz", "60 Hz" };

        public static string HeatNote(int interval) => interval switch
        {
            1 => "Every frame: hottest. Short sessions only; watch About.",
            2 => "Warmer than 20 Hz; watch Thermal state in About.",
            _ => "Coolest; hands lag a little more.",
        };
    }

    [Serializable]
    public class WindowSettings
    {
        /// <summary>Curve of new windows, 0..1.</summary>
        public float defaultCurve;
        /// <summary>Aspect of new windows opened without a size of their own.</summary>
        public AspectPreset defaultAspect = AspectPreset.Free;
        /// <summary>Windows open and close with a fade only (no scale).</summary>
        public bool reduceMotion;
    }

    [Serializable]
    public class ImmersionSettings
    {
        public ImmersiveEnvironment environment = ImmersiveEnvironment.Void;
        public float level;
        /// <summary>Start in the saved environment at the saved level; otherwise start in passthrough.</summary>
        public bool resumeOnStart;
    }

    [Serializable]
    public class PerformanceSettings
    {
        /// <summary>
        /// Windows' glass blurs the passthrough behind it (one shared pass per frame, <see cref="BackdropBlur"/>). Off: flat
        /// glass.fallback colour, saves the pass's GPU time and a little heat.
        /// </summary>
        public bool glassBlur = true;
    }

    [Serializable]
    public class VoiceSettings
    {
        /// <summary>Speech recognition language as a BCP 47 tag, e.g. "en-US".</summary>
        public string language = "en-US";
        /// <summary>Recognise on the phone when the language supports it (nothing leaves the device).</summary>
        public bool preferOnDevice = true;

        public static readonly string[] Languages = { "en-US", "en-GB", "en-IN", "hi-IN", "de-DE", "fr-FR", "es-ES", "ja-JP" };
    }
}
