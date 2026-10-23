using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SpatialLauncher.SpatialWindows.Testing
{
    /// <summary>
    /// The AR camera doesn't clear its buffers (clear flags "Nothing") because ARCameraBackground
    /// is meant to draw the camera feed every frame. In the editor there is no camera subsystem, so
    /// nothing clears colour or depth: frames smear and new geometry fails the depth test against stale depth.
    /// While no AR camera feed is running, this clears to a solid colour instead, and hands <see cref="BackdropBlur"/> a
    /// made-up room as the camera's picture, so glass shows a blur in the editor.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class CameraBackgroundFallback : MonoBehaviour
    {
        [SerializeField] Color backgroundColor = new Color(0.05f, 0.06f, 0.08f);

        Camera cam;
        ARCameraManager cameraManager;
        CameraClearFlags originalClearFlags;
        bool applied;
        Texture2D room;

        void Awake()
        {
            cam = GetComponent<Camera>();
#if !UNITY_EDITOR
            // Editor-only fallback: on device the AR camera feed (or HoloKit's stereo cameras) clears the screen.
            enabled = false;
#endif
            cameraManager = GetComponent<ARCameraManager>();
            originalClearFlags = cam.clearFlags;
        }

        void Update()
        {
            bool feedRunning = cameraManager != null && cameraManager.enabled
                && cameraManager.subsystem != null && cameraManager.subsystem.running;

            if (!feedRunning && !applied)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = backgroundColor;
                applied = true;
                BackdropBlur.SimulatedFeed = Room();
                BackdropBlur.SimulatedViewport = new Rect(0f, 0f, 1f, 1f);
            }
            else if (feedRunning && applied)
            {
                cam.clearFlags = originalClearFlags;
                applied = false;
                ReleaseRoom();
            }
        }

        void OnDisable()
        {
            if (applied && cam != null) cam.clearFlags = originalClearFlags;
            applied = false;
            ReleaseRoom();
        }

        void ReleaseRoom()
        {
            if (room == null) return;
            if (BackdropBlur.SimulatedFeed == room) BackdropBlur.SimulatedFeed = null;
            Destroy(room);
            room = null;
        }

        /// <summary>A small made-up room: wall and floor gradients, a dark couch, a lamp and a bright window.</summary>
        public static Texture2D MakeRoom(int w, int h)
        {
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    var c = v < 0.3f ? Color.Lerp(new Color(0.38f, 0.28f, 0.2f), new Color(0.3f, 0.22f, 0.16f), v / 0.3f)
                        : Color.Lerp(new Color(0.62f, 0.6f, 0.55f), new Color(0.78f, 0.76f, 0.7f), (v - 0.3f) / 0.7f);
                    if (u > 0.12f && u < 0.5f && v > 0.18f && v < 0.45f) c = new Color(0.16f, 0.2f, 0.3f);
                    if (u > 0.62f && u < 0.88f && v > 0.5f && v < 0.85f) c = new Color(0.95f, 0.97f, 1f);
                    if (u > 0.55f && u < 0.58f && v > 0.25f && v < 0.7f) c = new Color(0.9f, 0.8f, 0.4f);
                    pixels[y * w + x] = c;
                }
            }
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Simulated Room", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(pixels);
            texture.Apply(false);
            return texture;
        }

        Texture2D Room() => room != null ? room : room = MakeRoom(128, 96);
    }
}
