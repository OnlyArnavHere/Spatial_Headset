using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Spawns, tracks, focuses and closes <see cref="SpatialWindow"/>s.
    /// Windows are kept in focus order (last = front); the focused window is drawn in front
    /// of the others by pushing its visuals slightly towards the viewer.
    /// </summary>
    public class WindowManager : MonoBehaviour
    {
        [Tooltip("The user's head. Defaults to Camera.main.")]
        [SerializeField] Transform head;
        [Tooltip("A component implementing IWindowInput. Defaults to one on this GameObject.")]
        [SerializeField] MonoBehaviour inputSource;
        [Tooltip("Base material for window quads. Defaults to URP Unlit.")]
        [SerializeField] Material windowMaterial;

        [Header("Spawning")]
        [SerializeField] float spawnDistance = 1.5f;
        [Tooltip("Each new window is shifted right and down by this much, so windows don't spawn exactly on top of each other.")]
        [SerializeField] float cascadeOffset = 0.05f;
        [SerializeField] int cascadeSteps = 6;
        [SerializeField] Vector2 defaultSize = new Vector2(0.6f, 0.4f);
        [SerializeField] bool billboardByDefault = true;
        [SerializeField] bool worldLockedByDefault = true;

        [Header("Limits and ordering")]
        [SerializeField] Vector2 minSize = new Vector2(0.2f, 0.12f);
        [SerializeField] Vector2 maxSize = new Vector2(2f, 1.5f);
        [Tooltip("Distance between stacked unfocused windows, towards the viewer.")]
        [SerializeField] float depthStep = 0.01f;
        [Tooltip("Maximum total depth spread of unfocused windows, so many windows don't drift towards the viewer.")]
        [SerializeField] float maxDepthSpread = 0.05f;

        [Header("Focus")]
        [Tooltip("How far the focused window comes towards the viewer. Must clear the other windows' depth differences.")]
        [SerializeField] float focusedForwardOffset = 0.15f;
        [Tooltip("Scale of windows that aren't focused; the focused window is drawn at full size.")]
        [Range(0.5f, 1f)]
        [SerializeField] float unfocusedScale = 0.85f;

        public event Action<SpatialWindow> WindowOpened;
        public event Action<SpatialWindow> WindowClosed;
        public event Action<SpatialWindow> FocusChanged;

        /// <summary>Open windows in focus order; the last one is in front.</summary>
        public IReadOnlyList<SpatialWindow> Windows => windows;
        public SpatialWindow FocusedWindow => windows.Count > 0 ? windows[windows.Count - 1] : null;
        /// <summary>Base material for window quads; content can reuse it for its own quads.</summary>
        public Material WindowMaterial => windowMaterial;

        readonly List<SpatialWindow> windows = new List<SpatialWindow>();
        IWindowInput input;
        int spawnCount;

        void Awake()
        {
            if (windowMaterial == null)
            {
                // Shader.Find only works in builds if something else references the shader,
                // so assign Assets/Materials/SpatialWindow.mat instead of relying on this.
                Debug.LogWarning("WindowManager: no window material assigned; falling back to Shader.Find, which may be stripped from builds.", this);
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null)
                {
                    Debug.LogError("WindowManager: URP Unlit shader not found; windows cannot render.", this);
                    enabled = false;
                    return;
                }
                windowMaterial = new Material(shader) { name = "SpatialWindow (runtime)" };
            }
        }

        void OnEnable()
        {
            AdoptChildWindows();

            input = inputSource as IWindowInput ?? GetComponent<IWindowInput>();
            if (inputSource != null && !(inputSource is IWindowInput))
                Debug.LogWarning($"{inputSource.GetType().Name} does not implement IWindowInput.", this);
            if (input == null) return;

            input.SelectRequested += Focus;
            input.DragRequested += OnDragRequested;
            input.ResizeRequested += OnResizeRequested;
            input.CloseRequested += Close;
        }

        void OnDisable()
        {
            if (input == null) return;
            input.SelectRequested -= Focus;
            input.DragRequested -= OnDragRequested;
            input.ResizeRequested -= OnResizeRequested;
            input.CloseRequested -= Close;
            input = null;
        }

        /// <summary>
        /// Re-registers windows parented under this manager that the list doesn't know about.
        /// A script reload during Play mode resets the (unserialized) list while the window
        /// GameObjects survive; without this they ignore focus, drag and resize.
        /// </summary>
        void AdoptChildWindows()
        {
            windows.RemoveAll(w => w == null);
            bool adopted = false;
            foreach (var window in GetComponentsInChildren<SpatialWindow>())
            {
                if (windows.Contains(window)) continue;
                windows.Add(window);
                adopted = true;
            }
            if (adopted) ApplyOrder();
        }

        Transform Head
        {
            get
            {
                if (head == null && Camera.main != null) head = Camera.main.transform;
                return head;
            }
        }

        /// <summary>Opens a window about <see cref="spawnDistance"/> in front of the head and focuses it.</summary>
        public SpatialWindow Spawn(string title, Vector2? size = null)
        {
            var h = Head;
            if (h == null)
            {
                Debug.LogError("WindowManager: no head transform and no Camera.main; cannot place window.", this);
                return null;
            }

            float cascade = cascadeOffset * (spawnCount++ % Mathf.Max(1, cascadeSteps));
            var position = h.position + h.forward * spawnDistance + (h.right - h.up) * cascade;

            var go = new GameObject();
            go.transform.SetParent(transform, true);
            go.transform.position = position;

            var window = go.AddComponent<SpatialWindow>();
            window.Initialize(title, ClampSize(size ?? defaultSize), windowMaterial, h,
                billboardByDefault, worldLockedByDefault);

            // Start in the receded state so the new window grows forward into focus.
            window.SetPresentation(0f, unfocusedScale, instant: true);
            windows.Add(window);
            WindowOpened?.Invoke(window);
            Focus(window);
            return window;
        }

        /// <summary>Brings a window to the front and gives it focus.</summary>
        public void Focus(SpatialWindow window)
        {
            if (window == null || !windows.Contains(window)) return;
            bool changed = FocusedWindow != window;
            windows.Remove(window);
            windows.Add(window);
            ApplyOrder();

            if (changed) FocusChanged?.Invoke(window);
        }

        public void Close(SpatialWindow window)
        {
            if (window == null || !windows.Remove(window)) return;

            WindowClosed?.Invoke(window);
            Destroy(window.gameObject);
            ApplyOrder();
            FocusChanged?.Invoke(FocusedWindow);
        }

        public void CloseAll()
        {
            for (int i = windows.Count - 1; i >= 0; i--)
                Close(windows[i]);
        }

        // The focused (last) window grows to full size and comes forward; the rest shrink and recede,
        // stacked slightly by recency. Windows animate to these targets.
        void ApplyOrder()
        {
            int count = windows.Count;
            int unfocusedCount = count - 1;
            float step = unfocusedCount > 1 ? Mathf.Min(depthStep, maxDepthSpread / (unfocusedCount - 1)) : 0f;
            for (int i = 0; i < count; i++)
            {
                bool focused = i == count - 1;
                if (focused)
                    windows[i].SetPresentation(focusedForwardOffset, 1f);
                else
                    windows[i].SetPresentation(i * step, unfocusedScale);
                windows[i].SetFocused(focused);
            }
        }

        void OnDragRequested(SpatialWindow window, Vector3 position)
        {
            if (windows.Contains(window)) window.MoveTo(position);
        }

        void OnResizeRequested(SpatialWindow window, Vector2 size)
        {
            if (windows.Contains(window)) window.SetSize(ClampSize(size));
        }

        Vector2 ClampSize(Vector2 size) => new Vector2(
            Mathf.Clamp(size.x, minSize.x, maxSize.x),
            Mathf.Clamp(size.y, minSize.y, maxSize.y));
    }
}
