using System.Collections.Generic;
using System.Text;
using HoloKit;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>One moment of <see cref="DeviceStatusHud"/>'s readings.</summary>
    public struct StatusReading
    {
        /// <summary>"Mono", "Stereo", or "no HoloKit" (the editor scenes).</summary>
        public string Mode;
        public string Session;
        /// <summary>The hand source's type name.</summary>
        public string HandSource;
        /// <summary>The device pipeline, when the hand source is <see cref="HoloKitHandSource"/>.</summary>
        public bool HasDevicePipeline;
        public HandTrackingStatus Hands;
        public bool LeftTracked, RightTracked, LeftOnWindow, RightOnWindow, LeftPaused, RightPaused;
        public string Input;
        public bool HandInputActive;
        public string Passthrough;
        public int Frame;
        public float Fps;
        /// <summary>Vision results per second, measured.</summary>
        public float VisionHz;
        /// <summary>ARKit's camera format now ("1920x1440 @ 60 fps"), or null when unknown.</summary>
        public string CameraFormat;
        /// <summary>The camera formats this phone offers, or null when unknown.</summary>
        public List<string> CameraFormats;
        /// <summary>The camera image's field of view from ARKit's intrinsics (degrees along its width and height), or null.</summary>
        public Vector2? CameraFov;
        /// <summary>Passthrough zoom (1 = real size).</summary>
        public float Zoom;
        /// <summary>Degrees across and up the screen that the shown passthrough image spans, or null while it isn't shown.</summary>
        public Vector2? ImageFov;
        /// <summary>Degrees the whole camera image spans at zoom 1, from the AR camera's projection, or null.</summary>
        public Vector2? FullImageFov;
        /// <summary>Degrees across and up that one eye (Stereo) or the screen (Mono) shows, or null.</summary>
        public Vector2? ViewFov;
    }

    /// <summary>
    /// Status lines on screen, so the device can be checked without Xcode: render mode, AR session, whether HoloKit's hand
    /// tracking started (and why not), AR frames, Vision requests and rate, hands seen and tracked, where hand depth comes
    /// from, ARKit's camera format, its field of view and the formats the phone offers, the passthrough zoom with the
    /// angles the image and an eye's view span, the window input, stereo passthrough, and the frame
    /// count. Shown by default on the device (hidden in the editor,
    /// F3 there); a three-finger tap toggles it. In Stereo it is drawn inside each eye's viewport, so it reads in the
    /// headset (IMGUI across the whole screen would be split between the eyes).
    /// </summary>
    public class DeviceStatusHud : MonoBehaviour
    {
        const float Refresh = 0.25f;

        [SerializeField] bool showOnDevice = true;
        [SerializeField] bool showInEditor;
        [SerializeField] Key toggleKey = Key.F3;
        [SerializeField] int fontSize = 24;
        [Tooltip("Font size in Stereo, where each eye gets half the screen.")]
        [SerializeField] int stereoFontSize = 16;
        [SerializeField] float margin = 24f;

        public bool Visible { get; set; }
        /// <summary>The text shown (also used by tests).</summary>
        public string Text { get; private set; } = "";
        /// <summary>The screen rects (IMGUI, top-left origin) the text was last drawn in.</summary>
        public IReadOnlyList<Rect> Panels => panels;

        HoloKitHandSource deviceSource;
        HandWindowInput hands;
        WindowManager manager;
        HoloKitCameraManager holoKit;
        StereoPassthrough passthrough;
        ARCameraManager cameraManager;
        List<string> cameraFormats;
        int visionRequestsAt;
        float visionTimeAt, visionHz;
        float nextText, fps;
        bool threeFingers;
        GUIStyle style;
        Texture2D backgroundTexture;
        readonly List<Rect> panels = new List<Rect>();
        readonly List<Rect> eyes = new List<Rect>();

        void Awake()
        {
            Visible = Application.isEditor ? showInEditor : showOnDevice;
            manager = GetComponent<WindowManager>();
            hands = GetComponent<HandWindowInput>();
            if (hands == null) hands = FindFirstObjectByType<HandWindowInput>();
            deviceSource = FindFirstObjectByType<HoloKitHandSource>();
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
            passthrough = GetComponent<StereoPassthrough>();
            cameraManager = FindFirstObjectByType<ARCameraManager>();
        }

        /// <summary>A camera format as the status line shows it.</summary>
        public static string FormatName(XRCameraConfiguration configuration) =>
            $"{configuration.width}x{configuration.height}" + (configuration.framerate.HasValue ? $" @ {configuration.framerate.Value} fps" : "");

        // The formats don't change while the app runs: read them once the camera reports some.
        List<string> CameraFormats()
        {
            if (cameraFormats != null || cameraManager == null || cameraManager.subsystem == null || !cameraManager.subsystem.running) return cameraFormats;
            using (var configurations = cameraManager.GetConfigurations(Unity.Collections.Allocator.Temp))
            {
                if (!configurations.IsCreated || configurations.Length == 0) return null;
                cameraFormats = new List<string>();
                foreach (var configuration in configurations) cameraFormats.Add(FormatName(configuration));
            }
            return cameraFormats;
        }

        // Vision results per second over the last second or so.
        void MeasureVision()
        {
            if (deviceSource == null) return;
            int requests = deviceSource.Status.VisionRequests;
            float now = Time.unscaledTime;
            if (now - visionTimeAt < 1f) return;
            if (visionTimeAt > 0f && requests >= visionRequestsAt) visionHz = (requests - visionRequestsAt) / (now - visionTimeAt);
            visionRequestsAt = requests;
            visionTimeAt = now;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) fps = fps <= 0f ? 1f / dt : Mathf.Lerp(fps, 1f / dt, 0.05f);

            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[toggleKey].wasPressedThisFrame) Visible = !Visible;
            if (ThreeFingerTap()) Visible = !Visible;
            MeasureVision();

            if (Visible && Time.unscaledTime >= nextText)
            {
                nextText = Time.unscaledTime + Refresh;
                Text = Describe(Read());
            }
        }

        bool ThreeFingerTap()
        {
            var screen = Touchscreen.current;
            if (screen == null) return false;
            int down = 0;
            foreach (var touch in screen.touches)
                if (touch.press.isPressed) down++;
            bool tapped = down >= 3 && !threeFingers;
            threeFingers = down >= 3;
            return tapped;
        }

        /// <summary>Reads the scene's state now.</summary>
        public StatusReading Read()
        {
            var s = new StatusReading
            {
                Mode = holoKit == null ? "no HoloKit" : holoKit.ScreenRenderMode == ScreenRenderMode.Stereo ? "Stereo" : "Mono",
                Session = ARSession.state + (ARSession.notTrackingReason != NotTrackingReason.None
                    ? $" ({ARSession.notTrackingReason})" : ""),
                Input = manager != null ? manager.ActiveInputName : "none",
                Passthrough = passthrough != null ? passthrough.Status : "not in scene",
                Frame = Time.frameCount,
                Fps = fps,
                VisionHz = visionHz,
                CameraFormat = cameraManager != null && cameraManager.currentConfiguration.HasValue
                    ? FormatName(cameraManager.currentConfiguration.Value) : null,
                CameraFormats = CameraFormats(),
                CameraFov = IntrinsicsFov(),
                Zoom = passthrough != null ? passthrough.Zoom : SettingsStore.Current.display.passthroughZoom,
                ImageFov = passthrough != null ? passthrough.ImageFov : null,
                FullImageFov = passthrough != null ? passthrough.CameraFov : null,
                ViewFov = ViewFov(holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo),
            };
            var source = hands != null ? hands.HandSource : null;
            s.HandSource = source != null ? source.GetType().Name : "none";
            s.HasDevicePipeline = source is HoloKitHandSource || (source == null && deviceSource != null);
            if (deviceSource != null) s.Hands = deviceSource.Status;
            if (hands != null)
            {
                s.HandInputActive = hands.isActiveAndEnabled;
                if (source != null)
                {
                    s.LeftTracked = source.GetHand(Handedness.Left).IsTracked;
                    s.RightTracked = source.GetHand(Handedness.Right).IsTracked;
                }
                s.LeftOnWindow = hands.TryGetCursor(Handedness.Left, out _);
                s.RightOnWindow = hands.TryGetCursor(Handedness.Right, out _);
                s.LeftPaused = hands.IsSuppressed(Handedness.Left);
                s.RightPaused = hands.IsSuppressed(Handedness.Right);
            }
            return s;
        }

        /// <summary>The status lines.</summary>
        public static string Describe(in StatusReading s)
        {
            string YesNo(bool value) => value ? "yes" : "no";
            string Hand(bool tracked, bool onWindow, bool paused) =>
                !tracked ? "no" : paused ? "yes (gesture)" : onWindow ? "yes, on a window" : "yes";
            var h = s.Hands;
            var text = new StringBuilder();
            text.AppendLine($"Mode: {s.Mode}   frame {s.Frame}   {s.Fps:0} fps");
            text.AppendLine($"AR session: {s.Session}   AR frames: {h.ArFrames}");
            if (s.HasDevicePipeline)
            {
                text.AppendLine($"Hand tracking started: {YesNo(h.Started)}{(h.Started ? "" : " - " + h.NotStartedReason)}");
                text.AppendLine($"Vision requests: {h.VisionRequests}   hands seen: {h.HandsSeen}" +
                                $"{(h.HandsRejected > 0 ? $" ({h.HandsRejected} rejected)" : "")}");
                text.AppendLine($"Vision: {EveryNthFrame(h.VisionFrameInterval)}, {s.VisionHz:0.0} Hz   max hands: {h.MaxHands}");
                text.AppendLine($"Depth: {h.DepthPath ?? "-"}   LiDAR: {YesNo(h.SceneDepth)}   2D smoothing: {(h.Smoothing2D ? "on" : "off")}");
            }
            else text.AppendLine($"Hand source: {s.HandSource} (no device hand tracking)");
            text.AppendLine($"Hands tracked: left {Hand(s.LeftTracked, s.LeftOnWindow, s.LeftPaused)}, " +
                            $"right {Hand(s.RightTracked, s.RightOnWindow, s.RightPaused)}");
            text.AppendLine($"Input: {s.Input}{(s.HandInputActive ? "" : " (hand input off)")}");
            text.AppendLine($"Passthrough: {s.Passthrough}");
            // The zoom, what the image spans now, and what one eye (or the screen) shows: below "fits at", all of the
            // camera image is in view.
            text.Append($"Zoom: {DisplaySettings.ZoomLabel(s.Zoom)}   image {Degrees(s.ImageFov)}   " +
                        $"{(s.Mode == "Stereo" ? "eye" : "screen")} {Degrees(s.ViewFov)}");
            if (s.FullImageFov is Vector2 full && s.ViewFov is Vector2 view && full.x > 0f && full.y > 0f)
                text.Append($"   whole image fits at {StereoPassthrough.FitZoom(full, view):0.00}x");
            // ARKit's intrinsics, and the plane's own angles at 1x (they should agree: 1x is the camera's real size).
            text.Append($"\nCamera: {s.CameraFormat ?? "unknown"}   FOV {Degrees(s.CameraFov)}");
            if (s.FullImageFov.HasValue) text.Append($" (plane at 1x: {Degrees(s.FullImageFov)})");
            if (s.CameraFormats != null && s.CameraFormats.Count > 0)
            {
                // Three formats per line, so the list fits an eye's half of the screen.
                text.Append($"\nFormats ({s.CameraFormats.Count}):");
                for (int i = 0; i < s.CameraFormats.Count; i++)
                    text.Append(i % 3 == 0 ? "\n  " : ",  ").Append(s.CameraFormats[i]);
            }
            return text.ToString();
        }

        static string Degrees(Vector2? fov) => fov is Vector2 f && f.x > 0f && f.y > 0f ? $"{f.x:0.0}° x {f.y:0.0}°" : "-";

        // The camera image's field of view, from ARKit's focal length and principal point for the current format.
        Vector2? IntrinsicsFov()
        {
            if (cameraManager == null || cameraManager.subsystem == null || !cameraManager.subsystem.running) return null;
            if (!cameraManager.TryGetIntrinsics(out var k)) return null;
            var fov = StereoPassthrough.IntrinsicsFov(k.focalLength, k.principalPoint, k.resolution);
            return fov.x > 0f ? fov : (Vector2?)null;
        }

        // What one eye (HoloKit's left eye camera) or, in Mono, the AR camera shows.
        Vector2? ViewFov(bool stereo)
        {
            Camera view = null;
            if (stereo)
            {
                foreach (var camera in Camera.allCameras)
                    if (camera.cullingMask != 0 && camera.rect.width < 0.99f && (view == null || camera.rect.x < view.rect.x))
                        view = camera;
            }
            else if (cameraManager != null) view = cameraManager.GetComponent<Camera>();
            return view != null ? StereoPassthrough.ProjectionFov(view.projectionMatrix) : (Vector2?)null;
        }

        static string EveryNthFrame(int n) => n switch { 1 => "every frame", 2 => "every 2nd frame", 3 => "every 3rd frame", _ => $"every {n}th frame" };

        /// <summary>
        /// Where to draw the text (IMGUI rects, top-left origin): once at the top left of the screen, or in Stereo once at
        /// the top left of each eye's viewport (<paramref name="eyePixelRects"/>, camera pixel rects with a bottom-left
        /// origin), clipped to the viewport.
        /// </summary>
        public static void Layout(bool stereo, IReadOnlyList<Rect> eyePixelRects, float screenHeight, Vector2 textSize,
            float margin, List<Rect> result)
        {
            result.Clear();
            if (!stereo || eyePixelRects == null || eyePixelRects.Count == 0)
            {
                result.Add(new Rect(margin, margin, textSize.x, textSize.y));
                return;
            }
            foreach (var eye in eyePixelRects)
            {
                float top = screenHeight - eye.yMax;
                result.Add(new Rect(eye.xMin + margin, top + margin,
                    Mathf.Max(0f, Mathf.Min(textSize.x, eye.width - 2f * margin)),
                    Mathf.Max(0f, Mathf.Min(textSize.y, eye.height - 2f * margin))));
            }
        }

        // HoloKit's eye cameras: the enabled cameras that draw something into part of the screen.
        void FindEyes()
        {
            eyes.Clear();
            foreach (var camera in Camera.allCameras)
                if (camera.cullingMask != 0 && camera.rect.width < 0.99f)
                    eyes.Add(camera.pixelRect);
            eyes.Sort((x, y) => x.x.CompareTo(y.x));
        }

        void OnGUI()
        {
            if (!Visible || string.IsNullOrEmpty(Text) || Event.current.type != EventType.Repaint) return;
            bool stereo = holoKit != null && holoKit.ScreenRenderMode == ScreenRenderMode.Stereo;
            if (style == null)
            {
                backgroundTexture = new Texture2D(1, 1) { name = "Device Status Background" };
                backgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f));
                backgroundTexture.Apply();
                style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, richText = false, wordWrap = false, clipping = TextClipping.Clip };
                style.normal.textColor = Color.white;
                style.normal.background = backgroundTexture;
                style.padding = new RectOffset(10, 10, 8, 8);
            }
            style.fontSize = stereo ? stereoFontSize : fontSize;
            if (stereo) FindEyes();
            var size = style.CalcSize(new GUIContent(Text));
            Layout(stereo, eyes, Screen.height, size, stereo ? margin * 0.5f : margin, panels);
            foreach (var panel in panels)
                GUI.Label(panel, Text, style);
        }

        void OnDestroy()
        {
            if (backgroundTexture != null) Destroy(backgroundTexture);
        }
    }
}
