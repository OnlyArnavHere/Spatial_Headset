using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// An interactive element inside window content, such as a button or slider. It needs a collider;
    /// <see cref="Register"/> tags it as a <see cref="WindowPartType.Control"/> part of its window.
    /// Input implementations (mouse now, hands later) call Press, Drag and Release directly;
    /// pressing a control also selects its window like any other part.
    /// Hover, press, focus and disabled are kept in one <see cref="ControlVisualState"/> and drawn through the
    /// control's own renderer (overlay, focus ring, press shrink, fade); see Doc/ui-design.md section 7.
    /// </summary>
    public abstract class WindowControl : MonoBehaviour
    {
        public SpatialWindow Window { get; private set; }

        /// <summary>Pointer id used by the legacy Hover / HoverExit calls that carry no pointer.</summary>
        static readonly object DefaultPointer = new object();
        static readonly int PressScaleId = Shader.PropertyToID("_PressScale");
        static readonly int OverlayId = Shader.PropertyToID("_Overlay");
        static readonly int FocusRingId = Shader.PropertyToID("_FocusRing");

        /// <summary>Raised after the state is redrawn, so content can move extra visuals (a tile's highlight plate) with it.</summary>
        public event System.Action VisualRefreshed;

        public ControlVisualState State { get; } = new ControlVisualState();

        // Created on first use: a MonoBehaviour field initializer may not create one.
        MaterialPropertyBlock block;
        MeshRenderer shapeRenderer;
        bool interactable = true;
        readonly List<(TMP_Text label, float baseAlpha, bool keepContrast)> labels = new List<(TMP_Text, float, bool)>();

        /// <summary>False: no input reaches the control, it is drawn at <see cref="UITheme.DisabledAlpha"/> and shows no hover.</summary>
        public bool Interactable
        {
            get => interactable;
            set
            {
                if (!value && alwaysEnabled) return;
                interactable = value;
                State.SetDisabled(!value);
                OnInteractableChanged(value);
                RefreshVisual();
            }
        }

        /// <summary>True for controls that must never be disabled (the consent buttons); they also skip the press shrink.</summary>
        public bool AlwaysEnabled
        {
            get => alwaysEnabled;
            set
            {
                alwaysEnabled = value;
                if (value) Interactable = true;
            }
        }

        bool alwaysEnabled;

        /// <summary>Called when <see cref="Interactable"/> is set; a disabled control gives up focus.</summary>
        protected virtual void OnInteractableChanged(bool nowInteractable)
        {
            if (!nowInteractable) State.SetFocused(false);
        }

        /// <summary>Whether the state is drawn on this control's renderer (off for a video surface).</summary>
        protected virtual bool DrawsState => true;

        /// <summary>False when another renderer (<see cref="BindRenderer"/>) shows hover, press and focus, and this one only fades.</summary>
        public bool OwnShapeReacts { get; set; } = true;

        readonly List<MeshRenderer> extraRenderers = new List<MeshRenderer>();

        /// <summary>Draws the state on one more renderer (a slider's knob).</summary>
        public void BindRenderer(MeshRenderer extra)
        {
            if (extra == null || extraRenderers.Contains(extra)) return;
            extraRenderers.Add(extra);
            RefreshVisual();
        }

        public void Register(SpatialWindow window)
        {
            Window = window;
            if (!TryGetComponent<WindowPart>(out var part))
                part = gameObject.AddComponent<WindowPart>();
            part.Init(window, WindowPartType.Control);
        }

        /// <summary>Marks the control as the keyboard target / selected item: it draws the focus ring.</summary>
        public void SetFocused(bool focused)
        {
            State.SetFocused(focused);
            RefreshVisual();
        }

        /// <summary>Dims this label with the control when it is disabled; <paramref name="keepContrast"/> labels stay readable.</summary>
        public void BindLabel(TMP_Text label, bool keepContrast = false)
        {
            if (label == null) return;
            labels.Add((label, label.alpha, keepContrast));
            RefreshVisual();
        }

        /// <summary>A pointer is over this control. Hover(point) is then called as before.</summary>
        public void HoverFrom(object pointer, Vector3 worldPoint)
        {
            if (!interactable) return;
            State.SetHover(pointer ?? DefaultPointer, true);
            Hover(worldPoint);
        }

        /// <summary>A pointer left; <see cref="HoverExit"/> runs once the last one has.</summary>
        public void HoverExitFrom(object pointer)
        {
            State.SetHover(pointer ?? DefaultPointer, false);
            if (State.HoverCount == 0) HoverExit();
        }

        /// <summary>Starts a press for subclasses: false (and nothing drawn) while disabled.</summary>
        protected bool BeginPress()
        {
            if (!interactable) return false;
            State.SetPressed(true);
            return true;
        }

        protected void EndPress() => State.SetPressed(false);

        /// <summary>Moves the state forward by a time step and redraws; called every frame with unscaled time.</summary>
        public void Tick(float deltaTime)
        {
            if (!State.Animating) return;
            State.Step(deltaTime);
            RefreshVisual();
        }

        /// <summary>Jumps the animation to the current targets and redraws (first frame, gallery stills, tests).</summary>
        public void SnapVisual()
        {
            State.Snap();
            RefreshVisual();
        }

        /// <summary>Draws the state on the control's renderer and labels.</summary>
        public void RefreshVisual()
        {
            if (!DrawsState) return;
            block ??= new MaterialPropertyBlock();
            if (shapeRenderer != null || TryGetComponent(out shapeRenderer)) DrawOn(shapeRenderer, OwnShapeReacts);
            foreach (var extra in extraRenderers)
                if (extra != null) DrawOn(extra, true);
            foreach (var entry in labels)
                if (entry.label != null) State.Apply(null, entry.label, entry.baseAlpha, entry.keepContrast);
            VisualRefreshed?.Invoke();
        }

        void DrawOn(MeshRenderer target, bool full)
        {
            target.GetPropertyBlock(block);
            State.Apply(block);
            if (!full)
            {
                // Only the fade: the track of a slider doesn't light up or shrink, its knob does.
                block.SetColor(OverlayId, Color.clear);
                block.SetColor(FocusRingId, Color.clear);
            }
            if (!full || AlwaysEnabled) block.SetFloat(PressScaleId, 1f);
            if (AlwaysEnabled) block.SetColor(OverlayId, Color.clear);
            target.SetPropertyBlock(block);
        }

        protected virtual void Update() => Tick(Time.unscaledDeltaTime);

        /// <summary>The pointer went down on this control at a world-space point.</summary>
        public virtual void Press(Vector3 worldPoint) { }

        /// <summary>The pointer moved while held; the point is projected onto the control's plane.</summary>
        public virtual void Drag(Vector3 worldPoint) { }

        /// <summary>The pointer was released; <paramref name="inside"/> is true if it was still over this control.</summary>
        public virtual void Release(bool inside) { }

        /// <summary>The pointer is over this control without pressing; called every frame it stays.</summary>
        public virtual void Hover(Vector3 worldPoint) { }

        /// <summary>The pointer left this control (or input stopped) after hovering.</summary>
        public virtual void HoverExit() { }

        /// <summary>Scroll while hovering, in notches; positive is up / away from the user.</summary>
        public virtual void Scroll(Vector3 worldPoint, float delta) { }
    }
}
