using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Theme
{
    /// <summary>
    /// The visual state of one interactive element (Doc/ui-design.md section 7): hovered by any of several pointers, pressed,
    /// focused, disabled. <see cref="Step"/> moves the overlay alpha and the scale towards their targets with
    /// k = 1 - exp(-dt / tau), so the result does not depend on the frame rate; <see cref="Apply"/> pushes it to a material
    /// property block of the SpatialGlass shader. Plain C#, so it can be tested without a scene.
    /// </summary>
    public sealed class ControlVisualState
    {
        static readonly int OverlayId = Shader.PropertyToID("_Overlay");
        static readonly int FocusRingId = Shader.PropertyToID("_FocusRing");
        static readonly int FocusBandId = Shader.PropertyToID("_FocusBand");
        static readonly int PressScaleId = Shader.PropertyToID("_PressScale");
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        // One entry per pointer (the mouse, each hand), so one leaving does not clear another that is still over.
        readonly HashSet<object> hovers = new HashSet<object>();
        bool pressed, focused, disabled;

        public float OverlayAlpha { get; private set; }
        public float Scale { get; private set; } = 1f;

        public int HoverCount => hovers.Count;
        public bool Hovered => !disabled && hovers.Count > 0;
        public bool Pressed => !disabled && pressed;
        public bool Focused => focused;
        public bool Disabled => disabled;
        /// <summary>The focus ring is drawn only while focused (and not disabled).</summary>
        public bool RingVisible => focused && !disabled;
        /// <summary>Alpha of the element's content: <see cref="UITheme.DisabledAlpha"/> while disabled, else 1.</summary>
        public float ContentAlpha => disabled ? UITheme.DisabledAlpha : 1f;

        public float TargetOverlayAlpha => Pressed ? UITheme.PressedOverlay.a : Hovered ? UITheme.HoverOverlay.a : 0f;
        public float TargetScale => Pressed ? UITheme.PressScale : 1f;

        public void SetHover(object pointer, bool over)
        {
            if (pointer == null) return;
            if (over) hovers.Add(pointer);
            else hovers.Remove(pointer);
        }

        public void SetPressed(bool value) => pressed = value;
        public void SetFocused(bool value) => focused = value;
        public void SetDisabled(bool value)
        {
            disabled = value;
            if (value) pressed = false;
        }

        /// <summary>True while the overlay or scale is still moving towards its target.</summary>
        public bool Animating =>
            Mathf.Abs(OverlayAlpha - TargetOverlayAlpha) > 1e-4f || Mathf.Abs(Scale - TargetScale) > 1e-4f;

        /// <summary>Moves overlay and scale towards their targets by <paramref name="deltaTime"/> seconds (use unscaled time).</summary>
        public void Step(float deltaTime)
        {
            float overlayTau = (Pressed ? UITheme.PressMs : UITheme.HoverTauMs) * 0.001f;
            OverlayAlpha += (TargetOverlayAlpha - OverlayAlpha) * K(deltaTime, overlayTau);
            Scale += (TargetScale - Scale) * K(deltaTime, UITheme.PressMs * 0.001f);
        }

        /// <summary>Jumps to the targets (first frame, the UI gallery's stills).</summary>
        public void Snap()
        {
            OverlayAlpha = TargetOverlayAlpha;
            Scale = TargetScale;
        }

        static float K(float dt, float tau) => 1f - Mathf.Exp(-Mathf.Max(0f, dt) / Mathf.Max(1e-4f, tau));

        /// <summary>
        /// Alpha a label is drawn with: dimmed to <see cref="UITheme.DisabledAlpha"/> while disabled, unless it is one the
        /// spec requires to keep 4.5:1 (<paramref name="keepContrast"/>), which stays at <paramref name="baseAlpha"/>.
        /// </summary>
        public float LabelAlpha(float baseAlpha, bool keepContrast) => keepContrast ? baseAlpha : baseAlpha * ContentAlpha;

        /// <summary>Writes the overlay, focus ring, press scale and fade into the block (merging with what it already holds), and dims the label.</summary>
        public void Apply(MaterialPropertyBlock block, TMP_Text label = null, float labelBaseAlpha = 1f, bool keepContrast = false)
        {
            if (block != null)
            {
                var overlay = UITheme.HoverOverlay;
                overlay.a = OverlayAlpha;
                block.SetColor(OverlayId, overlay);
                block.SetColor(FocusRingId, RingVisible ? UITheme.FocusRing : Color.clear);
                block.SetVector(FocusBandId, new Vector4(UITheme.PtToMetres(UITheme.FocusRingGap), UITheme.PtToMetres(UITheme.FocusRingWidth), 0f, 0f));
                block.SetFloat(PressScaleId, Scale);
                block.SetFloat(FadeId, ContentAlpha);
            }
            if (label != null)
            {
                float alpha = LabelAlpha(labelBaseAlpha, keepContrast);
                if (!Mathf.Approximately(label.alpha, alpha)) label.alpha = alpha;
            }
        }
    }
}
