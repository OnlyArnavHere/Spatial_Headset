using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Window input from hands (<see cref="IHandSource"/>). Each hand's pointing ray is filtered, cast
    /// against the windows, and marked with a cursor dot. A pinch works like a mouse button:
    /// pinch any part to select and focus its window; hold a pinch on the title bar to drag (in the
    /// window's plane, and in/out as the hand moves towards or away from the head); pinch the resize
    /// handle to resize; pinch the close button to close; and pinch content controls to use them.
    /// Both hands work independently.
    /// </summary>
    public class HandWindowInput : MonoBehaviour, IWindowInput
    {
        [Tooltip("A component implementing IHandSource. Defaults to one on this GameObject.")]
        [SerializeField] MonoBehaviour handSource;
        [Tooltip("The user's head, used for in/out drag depth. Defaults to Camera.main.")]
        [SerializeField] Transform head;
        [SerializeField] LayerMask raycastMask = ~0;
        [SerializeField] float maxDistance = 10f;

        [Header("Filtering")]
        [Tooltip("Smoothing rate per second for the pointing ray and hand depth; higher follows faster.")]
        [SerializeField] float smoothing = 18f;
        [Tooltip("Ray direction changes smaller than this (degrees) are ignored as tremor.")]
        [SerializeField] float deadZoneAngle = 0.4f;
        [Tooltip("Ray origin and hand depth changes smaller than this (metres) are ignored as tremor.")]
        [SerializeField] float deadZoneDistance = 0.004f;

        [Header("Drag depth")]
        [Tooltip("How far a dragged window moves in or out per metre the pinching hand moves towards or away from the head.")]
        [SerializeField] float depthGain = 3f;
        [SerializeField] float minWindowDistance = 0.4f;
        [SerializeField] float maxWindowDistance = 5f;

        [Header("Cursor")]
        [Tooltip("Base material for the cursor and ray. Defaults to the WindowManager's window material.")]
        [SerializeField] Material cursorMaterial;
        [SerializeField] float cursorSize = 0.012f;
        [SerializeField] Color cursorColor = Color.white;
        [SerializeField] Color pinchedCursorColor = new Color(0.20f, 0.78f, 0.90f);
        [SerializeField] bool showRay = true;
        [SerializeField] float rayWidth = 0.0015f;

        public event Action<SpatialWindow> SelectRequested;
        public event Action<SpatialWindow, Vector3> DragRequested;
        public event Action<SpatialWindow, Vector2> ResizeRequested;
        public event Action<SpatialWindow> CloseRequested;

        public IHandSource HandSource => source;
        /// <summary>
        /// 0.5..2 (Settings > Hands): above 1 a held window follows smaller hand motions and moves further in/out per
        /// hand motion; below 1, the reverse.
        /// </summary>
        public float Sensitivity { get; set; } = 1f;
        /// <summary>False ignores pinches (Settings > Hands): the cursor still shows, nothing is pressed.</summary>
        public bool PinchEnabled { get; set; } = true;
        public float DepthGain => depthGain;
        public float DeadZoneDistance => deadZoneDistance;

        IHandSource source;
        HandPointer left, right;
        readonly bool[] suppressed = new bool[2];

        /// <summary>
        /// Pauses one hand's pointer (no cursor, no pinches) while a gesture recognizer uses that hand
        /// (<see cref="GestureRecognizers"/>), so the two never act on a window at once. Ends any gesture it had.
        /// </summary>
        public void SetSuppressed(Handedness hand, bool value)
        {
            suppressed[(int)hand] = value;
            if (value) Pointer(hand).Cancel();
        }

        public bool IsSuppressed(Handedness hand) => suppressed[(int)hand];

        /// <summary>Replaces the hand source (for example with a test or device source). Ends any gesture in progress.</summary>
        public void SetHandSource(IHandSource newSource)
        {
            CancelAll();
            source = newSource;
        }

        /// <summary>Where the hand's cursor is, if it is over a window or in a gesture.</summary>
        public bool TryGetCursor(Handedness hand, out Vector3 position)
        {
            var pointer = Pointer(hand);
            position = pointer.CursorPosition;
            return pointer.CursorVisible;
        }

        /// <summary>The window the hand is dragging, resizing or pressing a control on, if any.</summary>
        public SpatialWindow GetActiveWindow(Handedness hand) => Pointer(hand).ActiveWindow;

        /// <summary>The hand's pointing ray after filtering.</summary>
        public Ray GetFilteredRay(Handedness hand) => Pointer(hand).Filter.Current;

        HandPointer Pointer(Handedness hand)
        {
            EnsurePointers();
            return hand == Handedness.Left ? left : right;
        }

        void EnsurePointers()
        {
            left ??= new HandPointer(this, Handedness.Left);
            right ??= new HandPointer(this, Handedness.Right);
        }

        void Update()
        {
            EnsurePointers();
            if (source == null)
            {
                source = handSource as IHandSource ?? GetComponent<IHandSource>();
                if (source == null) return;
            }
            if (head == null && Camera.main != null) head = Camera.main.transform;

            float deltaTime = Time.deltaTime;
            if (suppressed[0]) left.Cancel();
            else left.Tick(source.GetHand(Handedness.Left), deltaTime);
            if (suppressed[1]) right.Cancel();
            else right.Tick(source.GetHand(Handedness.Right), deltaTime);
        }

        void OnDisable() => CancelAll();

        void OnDestroy()
        {
            left?.Dispose();
            right?.Dispose();
        }

        void CancelAll()
        {
            left?.Cancel();
            right?.Cancel();
        }

        bool TryHit(Ray ray, out RaycastHit hit, out WindowPart part, out Vector3 surfacePoint) =>
            WindowRaycast.TryHit(ray, maxDistance, raycastMask, out hit, out part, out surfacePoint);

        // Keeps a dragged window within comfortable reach of the head.
        Vector3 ClampToReach(Vector3 position)
        {
            if (head == null) return position;
            var offset = position - head.position;
            float distance = offset.magnitude;
            if (distance < 1e-4f) return position;
            return head.position + offset * (Mathf.Clamp(distance, minWindowDistance, maxWindowDistance) / distance);
        }

        Material CursorBaseMaterial
        {
            get
            {
                if (cursorMaterial == null && TryGetComponent<WindowManager>(out var manager))
                    cursorMaterial = manager.WindowMaterial;
                return cursorMaterial;
            }
        }

        /// <summary>One hand's filtering, gesture state and cursor.</summary>
        sealed class HandPointer
        {
            enum Mode { None, Drag, Resize, Close, Control }

            public readonly Handedness Hand;
            public readonly RayFilter Filter = new RayFilter();
            public SpatialWindow ActiveWindow { get; private set; }
            public bool CursorVisible { get; private set; }
            public Vector3 CursorPosition { get; private set; }

            readonly HandWindowInput owner;
            readonly ScalarFilter handDistance = new ScalarFilter();
            Mode mode;
            bool wasPinched;
            WindowControl activeControl;
            WindowControl hoveredControl;
            Vector3 gesturePoint;

            // Drag: a plane parallel to the window at grab time, pushed in/out by hand depth.
            Vector3 dragNormal, dragOrigin, grabOffset;
            float startHandDistance;

            // Resize: measured on the window's plane as it was when the pinch began.
            Plane resizePlane;
            Matrix4x4 resizeWorldToLocal;
            Vector3 resizeStartLocal;
            Vector2 resizeStartSize;

            GameObject cursor;
            LineRenderer line;
            Material cursorMaterial, lineMaterial;

            public HandPointer(HandWindowInput owner, Handedness hand)
            {
                this.owner = owner;
                Hand = hand;
            }

            public void Tick(HandState state, float deltaTime)
            {
                if (!state.IsTracked)
                {
                    Cancel();
                    return;
                }

                Filter.Smoothing = handDistance.Smoothing = owner.smoothing;
                float sensitivity = Mathf.Clamp(owner.Sensitivity, 0.5f, 2f);
                Filter.DeadZoneAngle = owner.deadZoneAngle / sensitivity;
                Filter.DeadZoneDistance = handDistance.DeadZone = owner.deadZoneDistance / sensitivity;

                var ray = Filter.Update(state.PointingRay, deltaTime);
                float rawHandDistance = owner.head != null ? Vector3.Distance(state.IndexTipPosition, owner.head.position) : 0f;
                float handDepth = handDistance.Update(rawHandDistance, deltaTime);

                if (mode != Mode.None && (ActiveWindow == null || (mode == Mode.Control && activeControl == null)))
                    ResetGesture(); // the window or control was destroyed mid-gesture

                bool hasHit = owner.TryHit(ray, out var hit, out var part, out var surfacePoint);
                bool pinched = state.IsPinched && owner.PinchEnabled;

                if (pinched && !wasPinched)
                {
                    if (hasHit) Begin(hit, part, handDepth);
                }
                else if (pinched && mode != Mode.None)
                {
                    Continue(ray, handDepth);
                }
                else if (!pinched && wasPinched)
                {
                    End(hasHit, part);
                }
                wasPinched = pinched;

                // Hover goes to the control under the ray while no gesture is in progress (hands have no scroll).
                WindowControl hovered = null;
                if (mode == Mode.None && hasHit && part.Type == WindowPartType.Control) part.TryGetComponent(out hovered);
                if (hoveredControl != hovered && hoveredControl != null) hoveredControl.HoverExit();
                hoveredControl = hovered;
                if (hovered != null) hovered.Hover(hit.point);

                // During a gesture the cursor stays on the grabbed point; otherwise it marks what the ray hits.
                bool inGesture = mode == Mode.Drag || mode == Mode.Resize || mode == Mode.Control;
                if (inGesture) UpdateCursor(ray, gesturePoint, true, pinched);
                else UpdateCursor(ray, hasHit ? surfacePoint : Vector3.zero, hasHit, pinched);
            }

            void Begin(RaycastHit hit, WindowPart part, float handDepth)
            {
                var window = part.Window;
                owner.SelectRequested?.Invoke(window);
                gesturePoint = hit.point;

                switch (part.Type)
                {
                    case WindowPartType.TitleBar:
                        mode = Mode.Drag;
                        dragNormal = window.transform.forward;
                        dragOrigin = hit.point;
                        grabOffset = window.transform.position - hit.point;
                        startHandDistance = handDepth;
                        break;

                    case WindowPartType.ResizeHandle:
                        mode = Mode.Resize;
                        var t = window.transform;
                        resizePlane = new Plane(t.forward, hit.point);
                        resizeWorldToLocal = t.worldToLocalMatrix;
                        resizeStartLocal = resizeWorldToLocal.MultiplyPoint3x4(hit.point);
                        resizeStartSize = window.Size;
                        break;

                    case WindowPartType.CloseButton:
                        mode = Mode.Close;
                        break;

                    case WindowPartType.Control when part.TryGetComponent(out activeControl):
                        mode = Mode.Control;
                        activeControl.Press(hit.point);
                        break;

                    default:
                        mode = Mode.None; // content: selecting is all a pinch does
                        break;
                }
                ActiveWindow = mode != Mode.None ? window : null;
            }

            void Continue(Ray ray, float handDepth)
            {
                switch (mode)
                {
                    case Mode.Drag:
                        // Moving the hand away from the head pushes the window away (along its normal), and vice versa.
                        float depth = (handDepth - startHandDistance) * owner.depthGain * Mathf.Clamp(owner.Sensitivity, 0.5f, 2f);
                        var plane = new Plane(dragNormal, dragOrigin + dragNormal * depth);
                        if (!plane.Raycast(ray, out float dragDistance)) break;
                        gesturePoint = ray.GetPoint(dragDistance);
                        owner.DragRequested?.Invoke(ActiveWindow, owner.ClampToReach(gesturePoint + grabOffset));
                        break;

                    case Mode.Resize:
                        if (!resizePlane.Raycast(ray, out float resizeDistance)) break;
                        gesturePoint = ray.GetPoint(resizeDistance);
                        var delta = resizeWorldToLocal.MultiplyPoint3x4(gesturePoint) - resizeStartLocal;
                        // Moving right grows the width, moving down grows the height.
                        owner.ResizeRequested?.Invoke(ActiveWindow, resizeStartSize + new Vector2(delta.x, -delta.y));
                        break;

                    case Mode.Control:
                        // Through the window's surface, so a drag follows a curved window too.
                        if (!WindowRaycast.TryProjectToLayout(activeControl.Window, ray, activeControl.transform, out gesturePoint)) break;
                        activeControl.Drag(gesturePoint);
                        break;
                }
            }

            void End(bool hasHit, WindowPart part)
            {
                // Close and control clicks only count if the hand is still on the same target when the pinch opens.
                if (mode == Mode.Close && hasHit && part.Window == ActiveWindow && part.Type == WindowPartType.CloseButton)
                {
                    owner.CloseRequested?.Invoke(ActiveWindow);
                }
                else if (mode == Mode.Control)
                {
                    bool inside = hasHit && part.TryGetComponent<WindowControl>(out var control) && control == activeControl;
                    activeControl.Release(inside);
                }
                ResetGesture();
            }

            void ResetGesture()
            {
                mode = Mode.None;
                ActiveWindow = null;
                activeControl = null;
            }

            /// <summary>Ends any gesture without completing it and hides the cursor (hand lost or input disabled).</summary>
            public void Cancel()
            {
                if (mode == Mode.Control && activeControl != null) activeControl.Release(false);
                if (hoveredControl != null) hoveredControl.HoverExit();
                hoveredControl = null;
                ResetGesture();
                wasPinched = false;
                Filter.Reset();
                handDistance.Reset();
                CursorVisible = false;
                if (cursor != null) cursor.SetActive(false);
                if (line != null) line.enabled = false;
            }

            void UpdateCursor(Ray ray, Vector3 point, bool visible, bool pinched)
            {
                if (!EnsureVisuals()) return;

                CursorVisible = visible;
                CursorPosition = point;
                var color = pinched ? owner.pinchedCursorColor : owner.cursorColor;
                cursorMaterial.color = color;
                lineMaterial.color = color;

                cursor.SetActive(visible);
                if (visible)
                {
                    // Pull the dot back towards the hand so it isn't half buried in the window.
                    cursor.transform.position = point - ray.direction * (owner.cursorSize * 0.5f);
                    cursor.transform.localScale = Vector3.one * owner.cursorSize;
                }

                line.enabled = owner.showRay;
                if (owner.showRay)
                {
                    line.widthMultiplier = owner.rayWidth;
                    line.SetPosition(0, ray.origin);
                    line.SetPosition(1, visible ? point : ray.GetPoint(0.3f));
                }
            }

            bool EnsureVisuals()
            {
                if (cursor != null) return true;
                var baseMaterial = owner.CursorBaseMaterial;
                if (baseMaterial == null) return false;

                cursorMaterial = new Material(baseMaterial) { name = $"{Hand} Hand Cursor" };
                lineMaterial = new Material(baseMaterial) { name = $"{Hand} Hand Ray" };

                cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                cursor.name = $"{Hand} Hand Cursor";
                UnityEngine.Object.DestroyImmediate(cursor.GetComponent<Collider>());
                var renderer = cursor.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = cursorMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                cursor.transform.SetParent(owner.transform, true);

                var lineObject = new GameObject($"{Hand} Hand Ray");
                lineObject.transform.SetParent(owner.transform, false);
                line = lineObject.AddComponent<LineRenderer>();
                line.sharedMaterial = lineMaterial;
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.enabled = false;
                return true;
            }

            public void Dispose()
            {
                if (cursor != null) UnityEngine.Object.Destroy(cursor);
                if (line != null) UnityEngine.Object.Destroy(line.gameObject);
                if (cursorMaterial != null) UnityEngine.Object.Destroy(cursorMaterial);
                if (lineMaterial != null) UnityEngine.Object.Destroy(lineMaterial);
            }
        }
    }
}
