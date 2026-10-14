using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Video;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Opens the home panel: a launcher window whose icons open an Image window, a Video window, a blank
    /// window or a Laptop window (a remote-screen stream, not connected yet). Press H (or call
    /// <see cref="OpenLauncher"/>) to bring the launcher back.
    /// </summary>
    public class LauncherWindow : MonoBehaviour
    {
        static readonly Color ImageIconColor = new Color(0.20f, 0.62f, 0.45f);
        static readonly Color VideoIconColor = new Color(0.78f, 0.32f, 0.42f);
        static readonly Color BlankIconColor = new Color(0.42f, 0.44f, 0.52f);
        static readonly Color LaptopIconColor = new Color(0.30f, 0.42f, 0.70f);

        [SerializeField] WindowManager manager;

        [Header("Sample content")]
        [SerializeField] Texture2D sampleImage;
        [Tooltip("Optional. Used instead of Sample Image when set.")]
        [SerializeField] Sprite sampleSprite;
        [SerializeField] VideoClip sampleVideo;
        [Tooltip("A local file path or URL, used when no Sample Video clip is set.")]
        [SerializeField] string sampleVideoUrl = "";

        [Header("Laptop (remote screen)")]
        [Tooltip("A component implementing IRemoteInputSink that receives pointer input aimed at the laptop screen. " +
                 "Defaults to one on this GameObject; without one, input is dropped.")]
        [SerializeField] MonoBehaviour remoteInputSink;
        [Tooltip("The laptop's screen resolution, used for the window's aspect until a stream reports its own.")]
        [SerializeField] Vector2Int laptopResolution = new Vector2Int(1920, 1080);

        [Header("Launcher")]
        [SerializeField] bool openOnStart = true;
        [SerializeField] Key openKey = Key.H;
        [SerializeField] Vector2 launcherSize = new Vector2(0.5f, 0.3f);
        [Tooltip("Width of the media shown in Image and Video windows, in metres.")]
        [SerializeField] float mediaWidth = 0.6f;

        public SpatialWindow Launcher { get; private set; }
        public LauncherContent LauncherContent { get; private set; }

        void Awake()
        {
            if (manager == null) manager = GetComponent<WindowManager>();
        }

        void Start()
        {
            if (openOnStart) OpenLauncher();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[openKey].wasPressedThisFrame)
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

        public SpatialWindow OpenLaptopWindow()
        {
            float aspect = laptopResolution.x / (float)Mathf.Max(1, laptopResolution.y);
            var window = manager.Spawn("Laptop", MediaWindowSize(aspect, 0f));
            if (window == null) return null;

            var sink = remoteInputSink as IRemoteInputSink ?? GetComponent<IRemoteInputSink>();
            if (remoteInputSink != null && !(remoteInputSink is IRemoteInputSink))
                Debug.LogWarning($"{remoteInputSink.GetType().Name} does not implement IRemoteInputSink.", this);

            var content = new GameObject().AddComponent<RemoteStreamContent>();
            content.Init(window, manager.WindowMaterial, sink, laptopResolution);
            return window;
        }

        // A window whose content area fits media of the given aspect at mediaWidth, plus any extra height.
        Vector2 MediaWindowSize(float aspect, float extraHeight) => new Vector2(
            mediaWidth + WindowContent.Padding * 2f,
            mediaWidth / aspect + extraHeight + WindowContent.Padding * 2f);
    }
}
