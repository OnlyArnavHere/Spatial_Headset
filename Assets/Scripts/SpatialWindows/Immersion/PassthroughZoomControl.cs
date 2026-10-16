using HoloKit;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Quick passthrough zoom toggle that needs no hand tracking: each press steps Settings > Display > Passthrough zoom
    /// to the next preset (Wide, Normal, Close, then Wide again). In Stereo it is HoloKit's stereo bar TRIGGER, relabelled
    /// ZOOM (the wearer can reach the phone's top edge in the headset); in Mono it is a small button at the bottom right of
    /// the screen (tap or click); F5 works in both. Only in scenes with HoloKit, where there is passthrough to zoom.
    /// While <see cref="ConsentGate"/> asks for approval, TRIGGER is ALLOW instead and doesn't zoom.
    /// </summary>
    public class PassthroughZoomControl : MonoBehaviour
    {
        const string TriggerLabel = "ZOOM";

        [SerializeField] Key toggleKey = Key.F5;
        [Tooltip("Show the on-screen button in Mono.")]
        [SerializeField] bool showButton = true;
        [Tooltip("Use the stereo bar's TRIGGER as the toggle in Stereo.")]
        [SerializeField] bool useStereoTrigger = true;

        public bool ShowButton
        {
            get => showButton;
            set => showButton = value;
        }

        /// <summary>The on-screen button is drawn (Mono with HoloKit, switched on).</summary>
        public bool ButtonVisible => showButton && holoKit != null && holoKit.ScreenRenderMode != ScreenRenderMode.Stereo;
        /// <summary>The button's rect (IMGUI, top-left origin) on the current screen.</summary>
        public Rect ButtonRect => ButtonRectFor(new Vector2(Screen.width, Screen.height));
        /// <summary>Presses taken so far (any input).</summary>
        public int Presses { get; private set; }

        HoloKitCameraManager holoKit;
        GUIStyle style;
        Texture2D backgroundTexture;

        /// <summary>
        /// Where the button goes on a screen of this size (IMGUI rect, top-left origin): bottom right, about 4% of the
        /// screen's shorter side from the edges.
        /// </summary>
        public static Rect ButtonRectFor(Vector2 screen)
        {
            float unit = Mathf.Min(screen.x, screen.y);
            var size = new Vector2(unit * 0.3f, unit * 0.09f);
            float margin = unit * 0.04f;
            return new Rect(screen.x - margin - size.x, screen.y - margin - size.y, size.x, size.y);
        }

        /// <summary>Whether a pointer at <paramref name="pointer"/> (Input System screen position, bottom-left origin) is on the button.</summary>
        public static bool Hits(Rect button, Vector2 pointer, float screenHeight) =>
            button.Contains(new Vector2(pointer.x, screenHeight - pointer.y));

        /// <summary>The button's caption for a zoom.</summary>
        public static string Caption(float zoom) => $"Zoom: {DisplaySettings.ZoomLabel(zoom)}";

        /// <summary>Steps the zoom to the next preset, as each input does.</summary>
        public void Step()
        {
            var gate = ConsentGate.Current;
            if (gate != null && (gate.HasPending || gate.TriggerUsedThisFrame)) return; // TRIGGER is ALLOW right now
            Presses++;
            SettingsStore.Update(s => s.display.passthroughZoom = DisplaySettings.NextZoomPreset(s.display.passthroughZoom));
        }

        void Awake()
        {
            holoKit = FindFirstObjectByType<HoloKitCameraManager>();
        }

        void OnEnable()
        {
            if (holoKit == null || !useStereoTrigger) return;
            holoKit.OnStereoTrigger += Step;
            holoKit.StereoTriggerLabel = TriggerLabel;
        }

        void OnDisable()
        {
            if (holoKit != null) holoKit.OnStereoTrigger -= Step;
        }

        void Update()
        {
            if (holoKit == null) return;
            var gate = ConsentGate.Current;
            bool asking = gate != null && gate.HasPending;
            // The stereo bar shows the pressed label briefly after each press: the zoom it switched to.
            if (useStereoTrigger && !asking)
            {
                holoKit.StereoTriggerLabel = TriggerLabel;
                float zoom = SettingsStore.Current.display.passthroughZoom;
                int preset = DisplaySettings.ZoomPresetIndex(zoom);
                holoKit.StereoTriggerPressedLabel = preset >= 0 ? DisplaySettings.ZoomPresetNames[preset].ToUpperInvariant() : $"{zoom:0.00}x";
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && !KeyboardShortcuts.Blocked && keyboard[toggleKey].wasPressedThisFrame) Step();
            if (ButtonVisible && PressedOnButton()) Step();
        }

        bool PressedOnButton()
        {
            var button = ButtonRect;
            var screen = Touchscreen.current;
            if (screen != null)
                foreach (var touch in screen.touches)
                    if (touch.press.wasPressedThisFrame && Hits(button, touch.position.ReadValue(), Screen.height)) return true;
            var mouse = Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame && Hits(button, mouse.position.ReadValue(), Screen.height);
        }

        void OnGUI()
        {
            if (!ButtonVisible || Event.current.type != EventType.Repaint) return;
            if (style == null)
            {
                backgroundTexture = new Texture2D(1, 1) { name = "Passthrough Zoom Button" };
                backgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
                backgroundTexture.Apply();
                style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
                style.normal.textColor = Color.white;
                style.normal.background = backgroundTexture;
            }
            var rect = ButtonRect;
            style.fontSize = Mathf.Max(12, Mathf.RoundToInt(rect.height * 0.4f));
            GUI.Label(rect, Caption(SettingsStore.Current.display.passthroughZoom), style);
        }

        void OnDestroy()
        {
            if (backgroundTexture != null) Destroy(backgroundTexture);
        }
    }
}
