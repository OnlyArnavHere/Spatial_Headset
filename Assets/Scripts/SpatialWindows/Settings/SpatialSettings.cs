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

        public SpatialSettings Clone() => JsonUtility.FromJson<SpatialSettings>(JsonUtility.ToJson(this));
    }

    [Serializable]
    public class LaptopSettings
    {
        public string host = "127.0.0.1";
        public int videoPort = 8889;
        /// <summary>The agent prints a new pair code (pairing token) every run; this is the last one typed.</summary>
        public string pairCode = "";
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
    }

    [Serializable]
    public class WindowSettings
    {
        /// <summary>Curve of new windows, 0..1.</summary>
        public float defaultCurve;
        /// <summary>Aspect of new windows opened without a size of their own.</summary>
        public AspectPreset defaultAspect = AspectPreset.Free;
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
    public class VoiceSettings
    {
        /// <summary>Speech recognition language as a BCP 47 tag, e.g. "en-US".</summary>
        public string language = "en-US";
        /// <summary>Recognise on the phone when the language supports it (nothing leaves the device).</summary>
        public bool preferOnDevice = true;

        public static readonly string[] Languages = { "en-US", "en-GB", "en-IN", "hi-IN", "de-DE", "fr-FR", "es-ES", "ja-JP" };
    }
}
