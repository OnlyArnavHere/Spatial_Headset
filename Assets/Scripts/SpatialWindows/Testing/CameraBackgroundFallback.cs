using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SpatialLauncher.SpatialWindows.Testing
{
    /// <summary>
    /// The AR camera doesn't clear its buffers (clear flags "Nothing") because ARCameraBackground
    /// is meant to draw the camera feed every frame. In the editor there is no camera subsystem, so
    /// nothing clears colour or depth: frames smear and new geometry fails the depth test against stale depth.
    /// While no AR camera feed is running, this clears to a solid colour instead.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class CameraBackgroundFallback : MonoBehaviour
    {
        [SerializeField] Color backgroundColor = new Color(0.05f, 0.06f, 0.08f);

        Camera cam;
        ARCameraManager cameraManager;
        CameraClearFlags originalClearFlags;
        bool applied;

        void Awake()
        {
            cam = GetComponent<Camera>();
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
            }
            else if (feedRunning && applied)
            {
                cam.clearFlags = originalClearFlags;
                applied = false;
            }
        }

        void OnDisable()
        {
            if (applied && cam != null) cam.clearFlags = originalClearFlags;
            applied = false;
        }
    }
}
