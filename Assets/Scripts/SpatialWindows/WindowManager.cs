using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Spawns, tracks, focuses and closes <see cref="SpatialWindow"/>s.
    /// Windows are kept in focus order (last = front); the focused window is drawn in front
    /// of the others by pushing its visuals slightly towards the viewer. New windows get
    /// <see cref="DefaultCurve"/>, and windows spawned without a size get <see cref="DefaultAspect"/>;
    /// a window with an aspect preset keeps it while resized (<see cref="SetAspect"/>).
    /// </summary>
    public class WindowManager : MonoBehaviour
    {
        [Tooltip("The user's head. Defaults to Camera.main.")]
        [SerializeField] Transform head;
        [Header("Input")]
        [Tooltip("Components implementing IWindowInput (for example MouseWindowInput, HandWindowInput). " +
                 "Only the active one is enabled. If empty, every IWindowInput on this GameObject is used, in component order.")]
        [SerializeField] MonoBehaviour[] inputSources;
        [Tooltip("Index into Input Sources of the input that drives the windows.")]
        [SerializeField] int activeInput;

        [Header("Rendering")]
        [Tooltip("Base material for window quads. Defaults to URP Unlit.")]
        [SerializeField] Material windowMaterial;
        [Tooltip("Glass material for window bodies (Assets/Materials/SpatialGlass.mat). Without it windows get a plain dark body.")]
        [SerializeField] Material glassMaterial;

        [Header("Spawning")]
        [SerializeField] float spawnDistance = 1.5f;
        [Tooltip("Each new window is shifted right and down by this much, so windows don't spawn exactly on top of each other.")]
        [SerializeField] float cascadeOffset = 0.05f;
        [SerializeField] int cascadeSteps = 6;
        [SerializeField] Vector2 defaultSize = new Vector2(0.6f, 0.4f);
        [SerializeField] bool billboardByDefault = true;
        [SerializeField] bool worldLockedByDefault = true;
        [Tooltip("Curve of new windows: 0 = flat, 1 = the gentlest cylinder (SpatialWindow.Curve).")]
        [Range(0f, 1f)]
        [SerializeField] float defaultCurve;
        [Tooltip("Aspect of new windows opened without a size of their own (blank windows).")]
        [SerializeField] AspectPreset defaultAspect = AspectPreset.Free;

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

        /// <summary>Raised with true when <see cref="HideAll"/> hides the windows and false when they come back.</summary>
        public event Action<bool> HiddenChanged;
        public event Action<SpatialWindow> WindowOpened;
        public event Action<SpatialWindow> WindowClosed;
        public event Action<SpatialWindow> FocusChanged;

        /// <summary>Open windows in focus order; the last one is in front.</summary>
        public IReadOnlyList<SpatialWindow> Windows => windows;
        public SpatialWindow FocusedWindow => windows.Count > 0 ? windows[windows.Count - 1] : null;
        /// <summary>Base material for window quads; content can reuse it for its own quads.</summary>
        public Material WindowMaterial => windowMaterial;
        public Material GlassMaterial => glassMaterial;
        public Vector2 MinSize => minSize;
        public Vector2 MaxSize => maxSize;

        public float DefaultCurve
        {
            get => defaultCurve;
            set => defaultCurve = Mathf.Clamp01(value);
        }

        public AspectPreset DefaultAspect
        {
            get => defaultAspect;
            set => defaultAspect = value;
        }

        readonly List<SpatialWindow> windows = new List<SpatialWindow>();
        readonly List<SpatialWindow> hidden = new List<SpatialWindow>();

        /// <summary>True between <see cref="HideAll"/> and <see cref="ShowAll"/>.</summary>
        public bool WindowsHidden { get; private set; }
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
            ApplyInputSelection();
        }

        void OnDisable() => UnsubscribeInput();

        /// <summary>The input sources this manager can switch between.</summary>
        public IReadOnlyList<MonoBehaviour> InputSources
        {
            get
            {
                if (inputSources == null || inputSources.Length == 0)
                {
                    var found = new List<MonoBehaviour>();
                    foreach (var component in GetComponents<MonoBehaviour>())
                        if (component is IWindowInput) found.Add(component);
                    inputSources = found.ToArray();
                }
                return inputSources;
            }
        }

        public int ActiveInputIndex => activeInput;
        public IWindowInput ActiveInput => input;
        public string ActiveInputName => input is MonoBehaviour behaviour ? behaviour.GetType().Name : "none";

        /// <summary>Makes the input at this index drive the windows and disables the others.</summary>
        public void SetActiveInput(int index)
        {
            activeInput = index;
            if (isActiveAndEnabled) ApplyInputSelection();
        }

        /// <summary>Switches to the next input source.</summary>
        public void CycleInput()
        {
            int count = InputSources.Count;
            if (count > 0) SetActiveInput((activeInput + 1) % count);
        }

        void ApplyInputSelection()
        {
            UnsubscribeInput();
            var sources = InputSources;
            if (sources.Count == 0) return;

            activeInput = Mathf.Clamp(activeInput, 0, sources.Count - 1);
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] == null) continue;
                if (!(sources[i] is IWindowInput))
                    Debug.LogWarning($"{sources[i].GetType().Name} does not implement IWindowInput.", this);
                sources[i].enabled = i == activeInput;
            }

            input = sources[activeInput] as IWindowInput;
            if (input == null) return;
            input.SelectRequested += Focus;
            input.DragRequested += OnDragRequested;
            input.ResizeRequested += OnResizeRequested;
            input.CloseRequested += Close;
        }

        void UnsubscribeInput()
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
                window.AspectRequested -= SetAspect;
                window.AspectRequested += SetAspect;
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
            var aspect = size.HasValue ? AspectPreset.Free : defaultAspect;
            window.Initialize(title, AspectPresets.Fit(size ?? defaultSize, aspect, minSize, maxSize), windowMaterial, h,
                billboardByDefault, worldLockedByDefault, glassMaterial);
            window.SetAspectPreset(aspect);
            window.SetCurve(defaultCurve);
            window.AspectRequested += SetAspect;

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

        /// <summary>
        /// Gives the window an aspect preset: its content area takes that aspect (keeping the width where the size limits
        /// allow) and keeps it while resized. Free only lifts the constraint.
        /// </summary>
        public void SetAspect(SpatialWindow window, AspectPreset preset)
        {
            if (window == null) return;
            window.SetAspectPreset(preset);
            if (preset != AspectPreset.Free) window.SetSize(AspectPresets.Fit(window.Size, preset, minSize, maxSize));
        }

        /// <summary>Hides every window except <paramref name="keep"/> (for example the launcher) until <see cref="ShowAll"/>.</summary>
        public void HideAll(SpatialWindow keep = null)
        {
            if (WindowsHidden) return;
            foreach (var window in windows)
            {
                if (window == null || window == keep || !window.gameObject.activeSelf) continue;
                window.gameObject.SetActive(false);
                hidden.Add(window);
            }
            WindowsHidden = true;
            HiddenChanged?.Invoke(true);
        }

        /// <summary>Brings back the windows <see cref="HideAll"/> hid.</summary>
        public void ShowAll()
        {
            if (!WindowsHidden) return;
            foreach (var window in hidden)
                if (window != null) window.gameObject.SetActive(true);
            hidden.Clear();
            WindowsHidden = false;
            ApplyOrder();
            HiddenChanged?.Invoke(false);
        }

        /// <summary>Resizes a window about its centre, within the size limits and its aspect preset.</summary>
        public void Resize(SpatialWindow window, Vector2 size)
        {
            if (window != null && windows.Contains(window))
                window.SetSize(AspectPresets.Constrain(size, window.Aspect, minSize, maxSize), keepTopLeft: false);
        }

        void OnDragRequested(SpatialWindow window, Vector3 position)
        {
            if (windows.Contains(window)) window.MoveTo(position);
        }

        void OnResizeRequested(SpatialWindow window, Vector2 size)
        {
            if (windows.Contains(window)) window.SetSize(AspectPresets.Constrain(size, window.Aspect, minSize, maxSize));
        }
    }
}
