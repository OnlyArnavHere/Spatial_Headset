using HoloKit;
using HoloKit.UI;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Keeps HoloKit's own controls out of the Stereo view: its stereo bar along the top edge (MORE, RECORD, MONO,
    /// OPTIONS, TRIGGER) is switched off, its alignment guide (outline and marker line) is hidden, and its default canvas
    /// (the Stereo/Mono and record buttons) is hidden while in Stereo. Also steadies HoloKit's predicted pose
    /// (<see cref="SetSmoothing"/>). In Mono the canvas shows again, since its Stereo button is how you enter Stereo.
    /// To leave Stereo: Settings > Display > Leave Stereo (<see cref="LeaveStereo"/>).
    /// Does nothing in scenes without HoloKit (the editor test scene).
    /// </summary>
    public class StereoChrome : MonoBehaviour
    {
        HoloKitCameraManager holoKit;
        HoloKitDefaultUICanvas canvas;

        /// <summary>The view is in HoloKit's Stereo mode.</summary>
        public static bool InStereo
        {
            get
            {
                var holoKit = FindFirstObjectByType<HoloKitCameraManager>();
                return holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo;
            }
        }

        /// <summary>Back to Mono (the phone's screen without the headset).</summary>
        public static void LeaveStereo()
        {
            var holoKit = FindFirstObjectByType<HoloKitCameraManager>();
            if (holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo) holoKit.ScreenRenderMode = ScreenRenderMode.Mono;
        }

        /// <summary>
        /// Seconds over which HoloKit's predicted Stereo pose low-passes ARKit's corrections (HoloKit: 10 ms rotation, 30 ms
        /// position). The pose still follows the gyroscope at once, so turning doesn't lag; only the small jumps each
        /// ARKit frame makes are spread out. At HoloKit's values they showed as every window jittering.
        /// Keep these small: between corrections the pose is carried forward on the IMU, so a long time constant lets
        /// sensor bias build up. At 50 ms / 100 ms the whole view crept sideways (windows drifting right pixel by pixel);
        /// position is the worst, as its velocity comes from the accelerometer, so it stays at HoloKit's 30 ms.
        /// </summary>
        public const float RotationSmoothing = 0.02f, PositionSmoothing = 0.03f;

        void Awake()
        {
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
            canvas = FindFirstObjectByType<HoloKitDefaultUICanvas>(FindObjectsInactive.Include);
            if (holoKit == null) return;
            SetSmoothing(FindFirstObjectByType<LowLatencyTrackingManager>(FindObjectsInactive.Include));
            holoKit.StereoBarItems = HoloKitStereoBarItems.None;
            holoKit.OnScreenRenderModeChanged += OnModeChanged;
            OnModeChanged(holoKit.ScreenRenderMode);
        }

        /// <summary>Sets the smoothing on HoloKit's tracking (private fields it reads on entering Stereo).</summary>
        public static bool SetSmoothing(LowLatencyTrackingManager tracking)
        {
            if (tracking == null) return false;
            const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var rotation = typeof(LowLatencyTrackingManager).GetField("m_RotationSmoothing", Private);
            var position = typeof(LowLatencyTrackingManager).GetField("m_PositionSmoothing", Private);
            if (rotation == null || position == null)
            {
                Debug.LogWarning("[StereoChrome] HoloKit's tracking smoothing fields not found; left as they are");
                return false;
            }
            rotation.SetValue(tracking, RotationSmoothing);
            position.SetValue(tracking, PositionSmoothing);
            return true;
        }

        void OnDestroy()
        {
            if (holoKit != null) holoKit.OnScreenRenderModeChanged -= OnModeChanged;
        }

        void OnModeChanged(ScreenRenderMode mode)
        {
            // The canvas would come back in Stereo once the bar is gone (it only hides behind the bar): hide it outright.
            if (canvas != null) canvas.gameObject.SetActive(mode != ScreenRenderMode.Stereo);
            // HoloKit's alignment guide (the outline of the phone opening and the marker line): spawned just before this
            // event, so it can be switched off here. Not shown at all.
            if (holoKit.AlignmentGuide != null) holoKit.AlignmentGuide.gameObject.SetActive(false);
        }
    }
}
