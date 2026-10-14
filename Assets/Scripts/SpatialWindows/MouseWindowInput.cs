using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Editor stand-in for hand input: raycasts from the mouse through the camera.
    /// Click a window to select it, drag its title bar to move it, drag the bottom-right
    /// handle to resize it, and click the red button to close it.
    /// </summary>
    public class MouseWindowInput : MonoBehaviour, IWindowInput
    {
        [Tooltip("Camera to raycast from. Defaults to Camera.main.")]
        [SerializeField] Camera rayCamera;
        [SerializeField] LayerMask raycastMask = ~0;
        [SerializeField] float maxDistance = 20f;

        public event Action<SpatialWindow> SelectRequested;
        public event Action<SpatialWindow, Vector3> DragRequested;
        public event Action<SpatialWindow, Vector2> ResizeRequested;
        public event Action<SpatialWindow> CloseRequested;

        enum Mode { None, Drag, Resize, Close }

        Mode mode;
        SpatialWindow activeWindow;
        readonly RaycastHit[] hitBuffer = new RaycastHit[32];

        // Drag: keep the grabbed point at the same distance along the mouse ray.
        float grabDistance;
        Vector3 grabOffset;

        // Resize: measure the mouse on the window's plane as it was when the drag began.
        Plane resizePlane;
        Matrix4x4 resizeWorldToLocal;
        Vector3 resizeStartLocal;
        Vector2 resizeStartSize;

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (rayCamera == null) rayCamera = Camera.main;
            if (rayCamera == null) return;

            if (mode != Mode.None && activeWindow == null)
                mode = Mode.None; // the window was closed mid-gesture

            // The mouse can report infinite or off-screen positions (for example when the
            // Game view isn't focused); ScreenPointToRay logs an error for those.
            var screenPosition = mouse.position.ReadValue();
            bool inView = IsInView(screenPosition);
            var ray = inView ? rayCamera.ScreenPointToRay(screenPosition) : default;

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (inView) Begin(ray);
            }
            else if (mouse.leftButton.isPressed)
            {
                if (inView) Continue(ray);
            }
            else if (mouse.leftButton.wasReleasedThisFrame)
            {
                End(ray, inView);
            }
        }

        /// <summary>Simulates a press at a screen position (used by editor test helpers).</summary>
        public void PressAt(Vector2 screenPosition)
        {
            if (rayCamera == null) rayCamera = Camera.main;
            if (rayCamera != null && IsInView(screenPosition))
                Begin(rayCamera.ScreenPointToRay(screenPosition));
        }

        /// <summary>Simulates a release at a screen position (used by editor test helpers).</summary>
        public void ReleaseAt(Vector2 screenPosition)
        {
            if (rayCamera == null) rayCamera = Camera.main;
            if (rayCamera == null) return;
            bool inView = IsInView(screenPosition);
            End(inView ? rayCamera.ScreenPointToRay(screenPosition) : default, inView);
        }

        bool IsInView(Vector2 screenPosition)
        {
            if (!float.IsFinite(screenPosition.x) || !float.IsFinite(screenPosition.y)) return false;
            if (screenPosition.x < 0f || screenPosition.y < 0f
                || screenPosition.x >= Screen.width || screenPosition.y >= Screen.height) return false;
            return rayCamera.pixelRect.Contains(screenPosition);
        }

        void Begin(Ray ray)
        {
            if (!TryHit(ray, out var hit, out var part)) return;

            activeWindow = part.Window;
            SelectRequested?.Invoke(activeWindow);

            switch (part.Type)
            {
                case WindowPartType.TitleBar:
                    mode = Mode.Drag;
                    grabDistance = hit.distance;
                    grabOffset = activeWindow.transform.position - hit.point;
                    break;

                case WindowPartType.ResizeHandle:
                    mode = Mode.Resize;
                    var t = activeWindow.transform;
                    resizePlane = new Plane(t.forward, hit.point);
                    resizeWorldToLocal = t.worldToLocalMatrix;
                    resizeStartLocal = resizeWorldToLocal.MultiplyPoint3x4(hit.point);
                    resizeStartSize = activeWindow.Size;
                    break;

                case WindowPartType.CloseButton:
                    mode = Mode.Close;
                    break;

                default:
                    mode = Mode.None;
                    break;
            }
        }

        void Continue(Ray ray)
        {
            switch (mode)
            {
                case Mode.Drag:
                    DragRequested?.Invoke(activeWindow, ray.GetPoint(grabDistance) + grabOffset);
                    break;

                case Mode.Resize:
                    if (!resizePlane.Raycast(ray, out float enter)) break;
                    var local = resizeWorldToLocal.MultiplyPoint3x4(ray.GetPoint(enter));
                    var delta = local - resizeStartLocal;
                    // Dragging right grows the width, dragging down grows the height.
                    ResizeRequested?.Invoke(activeWindow, resizeStartSize + new Vector2(delta.x, -delta.y));
                    break;
            }
        }

        void End(Ray ray, bool inView)
        {
            // A close only counts if the button is still under the mouse on release.
            if (mode == Mode.Close && inView && TryHit(ray, out _, out var part)
                && part.Window == activeWindow && part.Type == WindowPartType.CloseButton)
            {
                CloseRequested?.Invoke(activeWindow);
            }

            mode = Mode.None;
            activeWindow = null;
        }

        // Returns the nearest window part along the ray. Other colliders are skipped rather than
        // blocking: in AR (and XR Simulation) the real world is drawn as background, so a window
        // can appear in front of a wall or table whose collider is physically closer.
        bool TryHit(Ray ray, out RaycastHit hit, out WindowPart part)
        {
            hit = default;
            part = null;
            int count = Physics.RaycastNonAlloc(ray, hitBuffer, maxDistance, raycastMask, QueryTriggerInteraction.Collide);
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (hitBuffer[i].distance >= nearest) continue;
                var candidate = hitBuffer[i].collider.GetComponent<WindowPart>();
                if (candidate == null || candidate.Window == null) continue;
                nearest = hitBuffer[i].distance;
                hit = hitBuffer[i];
                part = candidate;
            }
            return part != null;
        }
    }
}
