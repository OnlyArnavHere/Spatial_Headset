using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Editor stand-in for hand input: raycasts from the mouse through the camera.
    /// Click a window to select it, drag its title bar or grab bar to move it, drag the
    /// resize control at the bottom-right corner to resize it, and click the close button. Content controls
    /// (<see cref="WindowControl"/>) receive press, drag and release directly.
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

        enum Mode { None, Drag, Resize, Close, Control }

        Mode mode;
        SpatialWindow activeWindow;
        SpatialWindow pointerWindow;
        WindowControl activeControl;
        WindowControl hoveredControl;

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

            if (mode != Mode.None && (activeWindow == null || (mode == Mode.Control && activeControl == null)))
                mode = Mode.None; // the window or control was destroyed mid-gesture

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

            UpdateHover(ray, inView, mouse.scroll.ReadValue().y);
            UpdatePointerOver(ray, inView);
        }

        // The window under the mouse (or being dragged) shows its controls; the one the mouse left hides them.
        void UpdatePointerOver(Ray ray, bool inView)
        {
            SpatialWindow over = null;
            if (mode != Mode.None && activeWindow != null) over = activeWindow;
            else if (inView && TryHit(ray, out _, out var part)) over = part.Window;

            if (pointerWindow != over && pointerWindow != null) pointerWindow.SetPointerOver(this, false);
            pointerWindow = over;
            if (over != null) over.SetPointerOver(this, true);
        }

        // Hover and scroll go to the control under the mouse while no gesture is in progress.
        void UpdateHover(Ray ray, bool inView, float scroll)
        {
            WindowControl hovered = null;
            Vector3 point = default;
            if (inView && mode == Mode.None && TryHit(ray, out var hit, out var part) && part.Type == WindowPartType.Control)
            {
                part.TryGetComponent(out hovered);
                point = hit.point;
            }

            if (hoveredControl != hovered && hoveredControl != null) hoveredControl.HoverExit();
            hoveredControl = hovered;
            if (hovered == null) return;

            hovered.Hover(point);
            // Mouse wheels report about 120 per notch on Windows and 1 elsewhere; pass whole notches.
            if (Mathf.Abs(scroll) > 0.01f) hovered.Scroll(point, Mathf.Abs(scroll) >= 60f ? scroll / 120f : scroll);
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
                case WindowPartType.GrabBar:
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

                case WindowPartType.Control when part.TryGetComponent(out activeControl):
                    mode = Mode.Control;
                    activeControl.Press(hit.point);
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

                case Mode.Control:
                    // Through the window's surface, so a drag follows a curved window too.
                    if (WindowRaycast.TryProjectToLayout(activeControl.Window, ray, activeControl.transform, out var point))
                        activeControl.Drag(point);
                    break;
            }
        }

        void End(Ray ray, bool inView)
        {
            if (mode == Mode.Control && activeControl != null)
            {
                bool inside = inView && TryHit(ray, out _, out var hitPart)
                    && hitPart.TryGetComponent<WindowControl>(out var control) && control == activeControl;
                activeControl.Release(inside);
            }
            activeControl = null;

            // A close only counts if the button is still under the mouse on release.
            if (mode == Mode.Close && inView && TryHit(ray, out _, out var part)
                && part.Window == activeWindow && part.Type == WindowPartType.CloseButton)
            {
                CloseRequested?.Invoke(activeWindow);
            }

            mode = Mode.None;
            activeWindow = null;
        }

        bool TryHit(Ray ray, out RaycastHit hit, out WindowPart part) =>
            WindowRaycast.TryHit(ray, maxDistance, raycastMask, out hit, out part);

        void OnDisable()
        {
            // Switching input sources mid-gesture: let go cleanly.
            if (mode == Mode.Control && activeControl != null) activeControl.Release(false);
            if (hoveredControl != null) hoveredControl.HoverExit();
            if (pointerWindow != null) pointerWindow.SetPointerOver(this, false);
            pointerWindow = null;
            mode = Mode.None;
            activeWindow = null;
            activeControl = null;
            hoveredControl = null;
        }
    }
}
