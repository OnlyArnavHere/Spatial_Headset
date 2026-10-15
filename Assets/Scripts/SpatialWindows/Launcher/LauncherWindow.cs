using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Video;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Opens the home panel: a launcher window whose icons open an Image window, a Video window, a blank window or the
    /// Laptop picker, which lists the laptop's windows (and the whole desktop) streamed by Tools/LaptopAgent and opens
    /// a stream window for each one picked. In Placeholder mode the Laptop icon opens a single "Not connected" screen.
    /// A stream window's Keys button opens the virtual keyboard (<see cref="OpenKeyboard"/>, <see cref="RemoteKeyboard"/>).
    /// The Void, Cinema and Sky icons select an immersive environment and open the immersion dial
    /// (<see cref="SelectEnvironment"/>, <see cref="ImmersionController"/>). The Settings icon opens the Settings window
    /// (<see cref="OpenSettings"/>, <see cref="SettingsService"/>).
    /// Press H (or call <see cref="OpenLauncher"/>) to bring the launcher back.
    /// </summary>
    public class LauncherWindow : MonoBehaviour
    {
        static readonly Color ImageIconColor = new Color(0.20f, 0.62f, 0.45f);
        static readonly Color VideoIconColor = new Color(0.78f, 0.32f, 0.42f);
        static readonly Color BlankIconColor = new Color(0.42f, 0.44f, 0.52f);
        static readonly Color LaptopIconColor = new Color(0.30f, 0.42f, 0.70f);
        static readonly Color VoidIconColor = new Color(0.10f, 0.12f, 0.22f);
        static readonly Color CinemaIconColor = new Color(0.45f, 0.10f, 0.14f);
        static readonly Color SkyIconColor = new Color(0.35f, 0.58f, 0.85f);
        static readonly Color SettingsIconColor = new Color(0.45f, 0.47f, 0.52f);

        [SerializeField] WindowManager manager;
        [Tooltip("Keyboard input for laptop windows. Added to this GameObject at start if not set.")]
        [SerializeField] RemoteKeyboard keyboard;
        [Tooltip("Immersion level and environments. Added to this GameObject at start if not set.")]
        [SerializeField] ImmersionController immersion;
        [SerializeField] Vector2 dialSize = new Vector2(0.5f, 0.22f);
        [Tooltip("Settings (saved to a file) and applying them. Added to this GameObject at start if not set.")]
        [SerializeField] SettingsService settings;

        [Header("Sample content")]
        [SerializeField] Texture2D sampleImage;
        [Tooltip("Optional. Used instead of Sample Image when set.")]
        [SerializeField] Sprite sampleSprite;
        [SerializeField] VideoClip sampleVideo;
        [Tooltip("A local file path or URL, used when no Sample Video clip is set.")]
        [SerializeField] string sampleVideoUrl = "";

        [Header("Laptop (remote screen)")]
        [Tooltip("Placeholder: a 'Not connected' screen only. WebRTC: a picker listing the laptop's windows, each " +
                 "streamed with video and input from the laptop agent (Tools/LaptopAgent). Needs the com.unity.webrtc package.")]
        [SerializeField] LaptopStreaming laptopStreaming = LaptopStreaming.Placeholder;
        [Tooltip("Defaults for the connect panels (host and ports).")]
        [SerializeField] RemoteConnectionSettings connectionSettings;
        [Tooltip("Optional: a component implementing IRemoteInputSink to receive the laptop screen's pointer input " +
                 "instead of the agent (for example LogRemoteInputSink while debugging).")]
        [SerializeField] MonoBehaviour remoteInputSink;
        [Tooltip("The laptop's screen resolution in Placeholder mode (WebRTC mode uses the connection settings).")]
        [SerializeField] Vector2Int laptopResolution = new Vector2Int(1920, 1080);

        public enum LaptopStreaming { Placeholder, WebRtc }

        [Header("Launcher")]
        [SerializeField] bool openOnStart = true;
        [SerializeField] Key openKey = Key.H;
        [SerializeField] Vector2 launcherSize = new Vector2(0.5f, 0.3f);
        [Tooltip("Width of the media shown in Image and Video windows, in metres.")]
        [SerializeField] float mediaWidth = 0.6f;

        public SpatialWindow Launcher { get; private set; }
        public LauncherContent LauncherContent { get; private set; }
        public RemoteKeyboard KeyboardInput => keyboard;
        public ImmersionController Immersion => immersion;
        /// <summary>The immersion dial window, while open.</summary>
        public SpatialWindow Dial { get; private set; }
        public SettingsService Settings => settings;
        /// <summary>The Settings window, while open.</summary>
        public SpatialWindow SettingsWindow { get; private set; }

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
            if (keyboard == null && !TryGetComponent(out keyboard)) keyboard = gameObject.AddComponent<RemoteKeyboard>();
            keyboard.Manager = manager;
            if (immersion == null && !TryGetComponent(out immersion)) immersion = gameObject.AddComponent<ImmersionController>();
            if (immersion.Manager == null) immersion.Manager = manager;
            if (settings == null && !TryGetComponent(out settings)) settings = gameObject.AddComponent<SettingsService>();
            if (settings.Manager == null) settings.Manager = manager;
            if (settings.Immersion == null) settings.Immersion = immersion;
            if (settings.ConnectionSettings == null) settings.ConnectionSettings = connectionSettings;
        }

        void Start()
        {
            if (openOnStart) OpenLauncher();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[openKey].wasPressedThisFrame)
                OpenLauncher();
        }

        /// <summary>Opens the launcher, or brings it to the front if it is already open.</summary>
        public SpatialWindow OpenLauncher()
        {
            if (Launcher != null)
            {
                manager.Focus(Launcher);
                return Launcher;
            }

            Launcher = manager.Spawn("Home", launcherSize);
            if (Launcher == null) return null;

            LauncherContent = new GameObject().AddComponent<LauncherContent>();
            LauncherContent.Init(Launcher, manager.WindowMaterial, new[]
            {
                new LauncherContent.Entry { Label = "Image", Glyph = "IMG", Color = ImageIconColor, Open = () => OpenImageWindow() },
                new LauncherContent.Entry { Label = "Video", Glyph = "VID", Color = VideoIconColor, Open = () => OpenVideoWindow() },
                new LauncherContent.Entry { Label = "Blank", Glyph = "+", Color = BlankIconColor, Open = () => OpenBlankWindow() },
                new LauncherContent.Entry { Label = "Laptop", Glyph = "PC", Color = LaptopIconColor, Open = () => OpenLaptopWindow() },
                new LauncherContent.Entry { Label = "Void", Glyph = "VOID", Color = VoidIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Void) },
                new LauncherContent.Entry { Label = "Cinema", Glyph = "CIN", Color = CinemaIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Cinema) },
                new LauncherContent.Entry { Label = "Sky", Glyph = "SKY", Color = SkyIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Sky) },
                new LauncherContent.Entry { Label = "Settings", Glyph = "SET", Color = SettingsIconColor, Open = () => OpenSettings() },
            });
            return Launcher;
        }

        public SpatialWindow OpenImageWindow()
        {
            if (sampleSprite == null && sampleImage == null)
            {
                Debug.LogWarning("LauncherWindow: no sample image or sprite assigned.", this);
                return null;
            }

            float aspect = sampleSprite != null
                ? sampleSprite.rect.width / sampleSprite.rect.height
                : sampleImage.width / (float)sampleImage.height;
            var window = manager.Spawn("Image", MediaWindowSize(aspect, 0f));
            if (window == null) return null;

            var content = new GameObject().AddComponent<ImageContent>();
            if (sampleSprite != null) content.Init(window, manager.WindowMaterial, sampleSprite);
            else content.Init(window, manager.WindowMaterial, sampleImage);
            return window;
        }

        public SpatialWindow OpenVideoWindow()
        {
            if (sampleVideo == null && string.IsNullOrEmpty(sampleVideoUrl))
            {
                Debug.LogWarning("LauncherWindow: no sample video clip or URL assigned.", this);
                return null;
            }

            float aspect = sampleVideo != null ? sampleVideo.width / (float)sampleVideo.height : 16f / 9f;
            var window = manager.Spawn("Video", MediaWindowSize(aspect, VideoContent.ControlBarHeight));
            if (window == null) return null;

            var content = new GameObject().AddComponent<VideoContent>();
            if (sampleVideo != null) content.Init(window, manager.WindowMaterial, sampleVideo);
            else content.Init(window, manager.WindowMaterial, sampleVideoUrl);
            return window;
        }

        public SpatialWindow OpenBlankWindow() => manager.Spawn("Blank");

        /// <summary>What the Laptop icon opens: the picker (WebRTC mode) or a "Not connected" screen (Placeholder mode).</summary>
        public SpatialWindow OpenLaptopWindow()
        {
            if (laptopStreaming == LaptopStreaming.WebRtc) return OpenLaptopPicker();

            float aspect = laptopResolution.x / (float)Mathf.Max(1, laptopResolution.y);
            var window = manager.Spawn("Laptop", MediaWindowSize(aspect, 0f));
            if (window == null) return null;
            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.Init(window, manager.WindowMaterial, InputOverride(), laptopResolution);
            return window;
        }

        /// <summary>A window that lists the laptop's windows and opens a stream window for each one picked.</summary>
        public SpatialWindow OpenLaptopPicker()
        {
            var size = new Vector2(mediaWidth, LaptopPickerContent.ContentHeight) + Vector2.one * (WindowContent.Padding * 2f);
            var window = manager.Spawn("Laptop", size);
            if (window == null) return null;
            var content = new GameObject().AddComponent<LaptopPickerContent>();
            content.Init(window, manager.WindowMaterial, connectionSettings, (target, connection) => OpenLaptopStream(target, connection));
            return window;
        }

        /// <summary>
        /// A window streaming one laptop window (or the whole desktop) with its own video and input connection.
        /// With a connection it connects at once; without, its connect panel waits for Connect.
        /// </summary>
        public SpatialWindow OpenLaptopStream(LaptopTarget target, LaptopConnection connection = null)
        {
            var resolution = target.width > 0 && target.height > 0 ? new Vector2Int(target.width, target.height)
                : connectionSettings != null ? connectionSettings.expectedResolution : laptopResolution;
            float aspect = resolution.x / (float)Mathf.Max(1, resolution.y);
            string title = string.IsNullOrEmpty(target.title) ? "Laptop" : target.title.Length > 40 ? target.title.Substring(0, 39) + "…" : target.title;
            var window = manager.Spawn(title, MediaWindowSize(aspect, RemoteStreamContent.ConnectPanelHeight));
            if (window == null) return null;

            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.InitConnected(window, manager.WindowMaterial, connectionSettings, InputOverride(), target);
            content.KeyboardRequested += () => OpenKeyboard();
            if (connection != null) content.ConnectWith(connection);
            return window;
        }

        /// <summary>
        /// Switches to an environment and opens the dial. From full passthrough it goes fully immersive, so the pick shows;
        /// otherwise the level stays where the dial has it.
        /// </summary>
        public SpatialWindow SelectEnvironment(ImmersiveEnvironment environment)
        {
            immersion.SetEnvironment(environment);
            if (immersion.Level <= 0f) immersion.SetLevel(1f);
            return OpenImmersionDial();
        }

        /// <summary>Opens the immersion dial (slider and environment choice), or brings it to the front.</summary>
        public SpatialWindow OpenImmersionDial()
        {
            if (Dial != null)
            {
                manager.Focus(Dial);
                return Dial;
            }
            Dial = manager.Spawn("Immersion", dialSize);
            if (Dial == null) return null;
            new GameObject().AddComponent<ImmersionDialContent>().Init(Dial, manager.WindowMaterial, immersion);
            return Dial;
        }

        /// <summary>Opens the Settings window, or brings it to the front.</summary>
        public SpatialWindow OpenSettings()
        {
            if (SettingsWindow != null)
            {
                manager.Focus(SettingsWindow);
                return SettingsWindow;
            }
            SettingsWindow = manager.Spawn("Settings", SettingsContent.PreferredSize + Vector2.one * (WindowContent.Padding * 2f));
            if (SettingsWindow == null) return null;
            new GameObject().AddComponent<SettingsContent>().Init(SettingsWindow, manager.WindowMaterial, settings);
            return SettingsWindow;
        }

        /// <summary>Opens the virtual keyboard, which types into the front-most laptop window.</summary>
        public SpatialWindow OpenKeyboard() => keyboard.Open();

        IRemoteInputSink InputOverride()
        {
            var sink = remoteInputSink as IRemoteInputSink;
            if (remoteInputSink != null && sink == null)
                Debug.LogWarning($"{remoteInputSink.GetType().Name} does not implement IRemoteInputSink.", this);
            return sink;
        }

        // A window whose content area fits media of the given aspect at mediaWidth, plus any extra height.
        Vector2 MediaWindowSize(float aspect, float extraHeight) => new Vector2(
            mediaWidth + WindowContent.Padding * 2f,
            mediaWidth / aspect + extraHeight + WindowContent.Padding * 2f);
    }
}
