using System.Reflection;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Puts <see cref="SettingsStore.Current"/> into effect and keeps it saved. On start and after every change it
    /// applies: window defaults (curve, aspect) to the <see cref="WindowManager"/>; hand sensitivity, the pinch toggle
    /// and the dominant hand to the hand input; the hand tracking rate and Hand Setup's thresholds to <see cref="HoloKitHandSource"/> (hand length to
    /// the gesture recognizers too); the pointer mode to the hand input; the passthrough zoom to <see cref="StereoPassthrough"/>; the environment (and, if asked, the level) to the
    /// <see cref="ImmersionController"/>, whose own changes are recorded back; the phone's brightness; and IPD and
    /// viewport offsets to HoloKit's stereo cameras. Changes are saved half a second after the last one (and when the
    /// app pauses or quits), so dragging a slider doesn't write the file every frame.
    /// </summary>
    public class SettingsService : MonoBehaviour
    {
        const float SaveDelay = 0.5f;

        [SerializeField] WindowManager manager;
        [SerializeField] ImmersionController immersion;
        [SerializeField] RemoteConnectionSettings connectionSettings;

        public static SettingsService Current { get; private set; }
        public SpatialSettings Settings => SettingsStore.Current;
        public bool SavePending => saveAt > 0f;

        public WindowManager Manager
        {
            get => manager;
            set => manager = value;
        }

        public ImmersionController Immersion
        {
            get => immersion;
            set
            {
                if (immersion != null) immersion.Changed -= RecordImmersion;
                immersion = value;
                if (immersion != null) immersion.Changed += RecordImmersion;
            }
        }

        public RemoteConnectionSettings ConnectionSettings
        {
            get => connectionSettings;
            set => connectionSettings = value;
        }

        float saveAt;
        bool applying;
        readonly HoloKitTuning holoKit = new HoloKitTuning();

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
        }

        void OnEnable()
        {
            Current = this;
            SettingsStore.Changed += OnChanged;
            if (immersion != null) immersion.Changed += RecordImmersion;
        }

        void OnDisable()
        {
            SettingsStore.Changed -= OnChanged;
            if (immersion != null) immersion.Changed -= RecordImmersion;
            if (Current == this) Current = null;
            SaveNow();
        }

        void Start()
        {
            var s = SettingsStore.Current;
            if (immersion != null)
            {
                applying = true;
                immersion.SetEnvironment(s.immersion.environment);
                if (s.immersion.resumeOnStart) immersion.SetLevel(s.immersion.level);
                applying = false;
            }
            Apply(s);
        }

        void OnChanged(SpatialSettings settings)
        {
            Apply(settings);
            saveAt = Time.unscaledTime + SaveDelay;
        }

        /// <summary>Applies every setting that has somewhere to go in this scene.</summary>
        public void Apply(SpatialSettings s)
        {
            if (manager != null)
            {
                manager.DefaultCurve = s.windows.defaultCurve;
                manager.DefaultAspect = s.windows.defaultAspect;
            }

            foreach (var hands in GetComponentsInChildren<HandWindowInput>(true))
            {
                hands.Sensitivity = s.hands.sensitivity;
                hands.PinchEnabled = s.hands.pinch;
                hands.Mode = s.hands.pointerMode;
                hands.RelativeGain = s.hands.relativeGain;
            }
            foreach (var simulated in GetComponentsInChildren<SimulatedHandSource>(true))
                simulated.DominantHand = s.hands.dominantHand;
            var thresholds = s.hands.calibration.Thresholds;
            foreach (var device in GetComponentsInChildren<HoloKitHandSource>(true))
            {
                device.VisionFrameInterval = s.hands.visionFrameInterval;
                device.Thresholds = thresholds;
            }
            if (TryGetComponent<GestureRecognizers>(out var gestures))
            {
                gestures.HandLength = thresholds.HandLength;
                gestures.FistGrab.Enabled = s.hands.fistGrab;
                gestures.PalmPush.Enabled = s.hands.palmPush;
                gestures.Stretch.Enabled = s.hands.twoHandStretch;
                gestures.CrossSweep.Enabled = s.hands.crossSweepHide;
                gestures.PinchHold.Enabled = s.hands.pinchHoldSwitcher;
            }
            if (TryGetComponent<GestureDebugOverlay>(out var overlay)) overlay.Visible = s.hands.debugOverlay;
            if (TryGetComponent<StereoPassthrough>(out var passthrough)) passthrough.Zoom = s.display.passthroughZoom;

            if (immersion != null && !applying && immersion.Environment != s.immersion.environment)
            {
                applying = true;
                immersion.SetEnvironment(s.immersion.environment);
                applying = false;
            }

#if !UNITY_EDITOR
            Screen.brightness = s.display.brightness;
#endif
            holoKit.Apply(s.display);
        }

        // The dial and the [ ] keys change immersion directly; remember where they leave it.
        void RecordImmersion()
        {
            if (applying || immersion == null) return;
            var s = SettingsStore.Current;
            if (s.immersion.environment == immersion.Environment && Mathf.Approximately(s.immersion.level, immersion.Level)) return;
            SettingsStore.Update(x =>
            {
                x.immersion.environment = immersion.Environment;
                x.immersion.level = immersion.Level;
            });
        }

        void Update()
        {
            holoKit.Tick(SettingsStore.Current.display);
            if (saveAt > 0f && Time.unscaledTime >= saveAt) SaveNow();
        }

        /// <summary>Writes pending changes now.</summary>
        public void SaveNow()
        {
            if (saveAt <= 0f) return;
            saveAt = 0f;
            try
            {
                SettingsStore.Save();
            }
            catch (System.Exception error) when (error is System.IO.IOException || error is System.UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Settings] Couldn't save {SettingsStore.Path}: {error.Message}");
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) SaveNow();
        }

        void OnApplicationQuit() => SaveNow();


        /// <summary>
        /// The fraction of the screen a viewport moves for a shift in millimetres (Unity camera rects are 0..1 of the
        /// screen): pixels per millimetre from the screen's DPI.
        /// </summary>
        public static Vector2 ViewportShift(Vector2 millimetres, Vector2 screenPixels, float dpi)
        {
            if (dpi <= 0f || screenPixels.x <= 0f || screenPixels.y <= 0f) return Vector2.zero;
            float pixelsPerMm = dpi / 25.4f;
            return new Vector2(millimetres.x * pixelsPerMm / screenPixels.x, millimetres.y * pixelsPerMm / screenPixels.y);
        }

        /// <summary>
        /// IPD and viewport offsets for HoloKit's stereo cameras. HoloKit keeps them private (m_Ipd, the eye cameras), so
        /// this sets them by reflection, re-runs HoloKit's camera setup for a new IPD, and shifts both eye viewports by
        /// the offset each time HoloKit lays them out. Does nothing in scenes without HoloKit (the editor test scene).
        /// </summary>
        sealed class HoloKitTuning
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            Component cameraManager;
            FieldInfo ipdField, leftField, rightField;
            MethodInfo setup;
            Rect leftBase, rightBase, leftSet, rightSet;
            float appliedIpd = -1f;
            bool searched;

            bool Find()
            {
                if (cameraManager != null) return true;
                if (searched) return false;
                searched = true;
                var type = FindType("HoloKit.HoloKitCameraManager");
                if (type == null) return false;
                cameraManager = Object.FindFirstObjectByType(type) as Component;
                if (cameraManager == null) return false;
                ipdField = type.GetField("m_Ipd", Private);
                leftField = type.GetField("m_LeftEyeCamera", Private);
                rightField = type.GetField("m_RightEyeCamera", Private);
                setup = type.GetMethod("SetupCameraData", Private, null, System.Type.EmptyTypes, null);
                return true;
            }

            static System.Type FindType(string name)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType(name);
                    if (type != null) return type;
                }
                return null;
            }

            public void Apply(DisplaySettings display)
            {
                if (!Find() || ipdField == null) return;
                float ipd = display.ipdMm / 1000f;
                if (Mathf.Approximately(ipd, appliedIpd)) return;
                appliedIpd = ipd;
                ipdField.SetValue(cameraManager, ipd);
                // Re-lay out the stereo cameras; HoloKit throws if it isn't in stereo yet, which is fine.
                try { setup?.Invoke(cameraManager, null); } catch (TargetInvocationException) { }
            }

            public void Tick(DisplaySettings display)
            {
                if (cameraManager == null || leftField == null || rightField == null) return;
                if (!(leftField.GetValue(cameraManager) is Camera left) || !(rightField.GetValue(cameraManager) is Camera right)) return;
                if (!left.isActiveAndEnabled) return;
                // A rect we didn't set is HoloKit's own layout: take it as the new base.
                if (left.rect != leftSet) leftBase = left.rect;
                if (right.rect != rightSet) rightBase = right.rect;
                var shift = ViewportShift(display.viewportOffsetMm, new Vector2(Screen.width, Screen.height), Screen.dpi);
                leftSet = new Rect(leftBase.position + shift, leftBase.size);
                rightSet = new Rect(rightBase.position + shift, rightBase.size);
                left.rect = leftSet;
                right.rect = rightSet;
            }
        }
    }
}
