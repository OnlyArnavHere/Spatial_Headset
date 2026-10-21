using SpatialLauncher.SpatialWindows.Theme;
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
    /// (<see cref="OpenSettings"/>, <see cref="SettingsService"/>). The Hide icon hides every other window and becomes Show
    /// (as does the cross-and-sweep gesture, <see cref="CrossSweepRecognizer"/>).
    /// The Cast icon opens the same picker for a view-only Cast window (<see cref="OpenLinkedStream"/>): media controls
    /// only, for an agent started with --cast-only.
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
        static readonly Color HideIconColor = new Color(0.35f, 0.30f, 0.45f);
        static readonly Color CastIconColor = new Color(0.62f, 0.38f, 0.16f);
        const int HideEntry = 8;

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

        /// <summary>The open pairing screen, if any (a destroyed window reads as null).</summary>
        public SpatialWindow PairingWindow { get; private set; }
        /// <summary>The Hand Setup window, while open.</summary>
        public SpatialWindow HandSetupWindow { get; private set; }

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
            if (!TryGetComponent<GestureRecognizers>(out _)) gameObject.AddComponent<GestureRecognizers>();
            if (!TryGetComponent<GestureDebugOverlay>(out _)) gameObject.AddComponent<GestureDebugOverlay>();
            if (!TryGetComponent<VoiceTyping>(out _)) gameObject.AddComponent<VoiceTyping>();
            if (!TryGetComponent<StereoPassthrough>(out _)) gameObject.AddComponent<StereoPassthrough>();
            if (!TryGetComponent<DeviceStatusHud>(out _)) gameObject.AddComponent<DeviceStatusHud>();
            if (!TryGetComponent<PassthroughZoomControl>(out _)) gameObject.AddComponent<PassthroughZoomControl>();
            if (!TryGetComponent<ConsentGate>(out _)) gameObject.AddComponent<ConsentGate>();
            if (!TryGetComponent<LaptopLink.LinkService>(out _)) gameObject.AddComponent<LaptopLink.LinkService>();
        }

        void OnEnable()
        {
            if (manager != null) manager.HiddenChanged += OnHiddenChanged;
        }

        void OnDisable()
        {
            if (manager != null) manager.HiddenChanged -= OnHiddenChanged;
        }

        void OnHiddenChanged(bool isHidden)
        {
            if (LauncherContent != null) LauncherContent.SetEntryText(HideEntry, isHidden ? "Show all" : "Hide all", isHidden ? UIIcons.Visibility : UIIcons.VisibilityOff);
        }

        /// <summary>Hides every window but the launcher, or brings them back.</summary>
        public void ToggleHideAll()
        {
            if (manager.WindowsHidden) manager.ShowAll();
            else manager.HideAll(Launcher);
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

            Launcher = manager.Spawn("Home", LauncherContent.WindowSize);
            if (Launcher == null) return null;

            LauncherContent = new GameObject().AddComponent<LauncherContent>();
            LauncherContent.Init(Launcher, manager.WindowMaterial, new[]
            {
                new LauncherContent.Entry { Label = "Image", Icon = UIIcons.Image, Color = ImageIconColor, Open = () => OpenImageWindow() },
                new LauncherContent.Entry { Label = "Video", Icon = UIIcons.Movie, Color = VideoIconColor, Open = () => OpenVideoWindow() },
                new LauncherContent.Entry { Label = "Blank", Icon = UIIcons.CheckBoxOutlineBlank, Color = BlankIconColor, Open = () => OpenBlankWindow() },
                new LauncherContent.Entry { Label = "Laptop", Icon = UIIcons.Laptop, Color = LaptopIconColor, Open = () => OpenLaptopWindow() },
                new LauncherContent.Entry { Label = "Void", Icon = UIIcons.DarkMode, Color = VoidIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Void) },
                new LauncherContent.Entry { Label = "Cinema", Icon = UIIcons.Theaters, Color = CinemaIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Cinema) },
                new LauncherContent.Entry { Label = "Sky", Icon = UIIcons.WbSunny, Color = SkyIconColor, Open = () => SelectEnvironment(ImmersiveEnvironment.Sky) },
                new LauncherContent.Entry { Label = "Settings", Icon = UIIcons.Settings, Color = SettingsIconColor, Open = () => OpenSettings() },
                new LauncherContent.Entry { Label = "Hide all", Icon = UIIcons.VisibilityOff, Color = HideIconColor, Open = ToggleHideAll },
                new LauncherContent.Entry { Label = "Cast", Icon = UIIcons.Cast, Color = CastIconColor, Open = () => OpenCastPicker() },
            });
            OnHiddenChanged(manager.WindowsHidden);
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
        public SpatialWindow OpenLaptopPicker() => OpenPicker("Laptop");

        /// <summary>The picker for a view-only Cast window: each pick opens a view-only linked stream window.</summary>
        public SpatialWindow OpenCastPicker() => OpenPicker("Cast");

        SpatialWindow OpenPicker(string title)
        {
            bool cast = title == "Cast";
            var size = new Vector2(mediaWidth, LaptopPickerContent.ContentHeight) + Vector2.one * (WindowContent.Padding * 2f);
            var window = manager.Spawn(title, size);
            if (window == null) return null;
            var content = new GameObject().AddComponent<LaptopPickerContent>();
            content.Init(window, manager.WindowMaterial, () => OpenPairing());
#if SPATIAL_LAUNCHER_WEBRTC
            content.OpenLinked = (session, opened, target) => OpenLinkedStream(session, opened, target, cast);
#endif
            return window;
        }

#if SPATIAL_LAUNCHER_WEBRTC
        /// <summary>A stream window on one slot of the link session (view-only until control is requested; Cast: media only).</summary>
        public SpatialWindow OpenLinkedStream(LinkSession session, OpenedWindow opened, LaptopTarget target, bool cast)
        {
            var resolution = target.width > 0 && target.height > 0 ? new Vector2Int(target.width, target.height)
                : connectionSettings != null ? connectionSettings.expectedResolution : laptopResolution;
            float aspect = resolution.x / (float)Mathf.Max(1, resolution.y);
            string title = string.IsNullOrEmpty(target.title) ? "Laptop" : target.title.Length > 40 ? target.title.Substring(0, 39) + "…" : target.title;
            if (cast) title = "Cast: " + title;
            float extra = RemoteStreamContent.ConnectPanelHeight + (cast ? RemoteStreamContent.CastBarHeight : 0f);
            var window = manager.Spawn(title, MediaWindowSize(aspect, extra));
            if (window == null) return null;

            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.InitLinked(window, manager.WindowMaterial, connectionSettings, session, opened.Slot, target, cast);
            if (!cast) content.KeyboardRequested += () => OpenKeyboard();
            return window;
        }
#endif

        /// <summary>The pairing screen: a code to type into <c>agent.py --pair</c>. Closing it ends pairing mode.</summary>
        public SpatialWindow OpenPairing()
        {
            if (PairingWindow != null)
            {
                manager.Focus(PairingWindow);
                return PairingWindow;
            }
            var link = LaptopLink.LinkService.Current;
            var size = PairingCodeContent.PreferredSize + Vector2.one * (WindowContent.Padding * 2f);
            PairingWindow = manager.Spawn("Pair laptop", size);
            if (PairingWindow == null) return null;
            new GameObject().AddComponent<PairingCodeContent>().Init(PairingWindow, manager.WindowMaterial, link != null ? link.Host : null);
            return PairingWindow;
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

        /// <summary>Opens Hand Setup (calibration of pinch, fist and hand length), or brings it to the front.</summary>
        public SpatialWindow OpenHandSetup()
        {
            if (HandSetupWindow != null)
            {
                manager.Focus(HandSetupWindow);
                return HandSetupWindow;
            }
            HandSetupWindow = manager.Spawn("Hand Setup", HandSetupContent.PreferredSize + Vector2.one * (WindowContent.Padding * 2f));
            if (HandSetupWindow == null) return null;
            var hands = GetComponent<HandWindowInput>();
            if (hands == null) hands = FindFirstObjectByType<HandWindowInput>();
            new GameObject().AddComponent<HandSetupContent>().Init(HandSetupWindow, manager.WindowMaterial, hands, GetComponent<GestureRecognizers>());
            return HandSetupWindow;
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
