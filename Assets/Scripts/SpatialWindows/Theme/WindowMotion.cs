using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Theme
{
    /// <summary>
    /// Open and close motion of one window (Doc/ui-design.md section 8). Open: scale 0.92 to 1 and alpha 0 to 1 over 250 ms,
    /// ease-out cubic. Close: scale to 0.95 and alpha to 0 over 180 ms, ease-in cubic, then the callback (the manager
    /// destroys the window). A fade-only window (consent, or Reduce motion on) keeps scale 1; consent fades in 120 ms.
    /// A close that starts mid-open continues from the current scale and alpha.
    /// <para>
    /// The alpha goes to every renderer under the window as <c>_WindowFade</c> in its property block (merged, so a control's
    /// own <c>_Fade</c> is kept; the shader multiplies the two) and to every text label, as the label's own alpha times the
    /// fade. The scale goes to <see cref="SpatialWindow.MotionScale"/>, which multiplies the focus scale. Runs on unscaled
    /// time; <see cref="Step"/> does the work so tests can drive it. Allocation-free once warmed up.
    /// </para>
    /// </summary>
    // After the window's and controls' own LateUpdate/Update writes of property blocks and label alpha.
    [DefaultExecutionOrder(1000)]
    public class WindowMotion : MonoBehaviour
    {
        static readonly int WindowFadeId = Shader.PropertyToID("_WindowFade");

        /// <summary>Tests set this to force Reduce motion on or off; null reads the setting.</summary>
        public static bool? ReduceMotionOverride;
        /// <summary>Test hook: opens and closes finish at once, so Play-mode suites do not wait on the motion.</summary>
        public static bool Instant;
        public static bool ReduceMotion => ReduceMotionOverride ?? SettingsStore.Current.windows.reduceMotion;

        enum Phase { Idle, Opening, Closing }

        struct LabelFade { public float baseAlpha, written; }

        SpatialWindow window;
        Phase phase;
        bool fadeOnly, consent;
        float elapsed, duration;
        float fromAlpha = 1f, fromScale = 1f, toAlpha = 1f, toScale = 1f;
        Action done;

        // Created on first use (a MonoBehaviour field initializer may not create a property block).
        MaterialPropertyBlock block;
        readonly List<Renderer> renderers = new List<Renderer>();
        readonly List<TMP_Text> labels = new List<TMP_Text>();
        readonly Dictionary<TMP_Text, LabelFade> labelFades = new Dictionary<TMP_Text, LabelFade>();

        /// <summary>Whole-window alpha, 0 to 1.</summary>
        public float Alpha { get; private set; } = 1f;
        /// <summary>Scale the motion puts on the window, 1 when idle or fade-only.</summary>
        public float Scale { get; private set; } = 1f;
        public bool IsOpening => phase == Phase.Opening;
        public bool IsClosing => phase == Phase.Closing;
        public bool IsAnimating => phase != Phase.Idle;
        public bool FadeOnly => fadeOnly;

        void Awake() => window = GetComponent<SpatialWindow>();

        /// <summary>Starts at alpha 0 (scale 0.92) and opens. <paramref name="consentFade"/> is a 120 ms fade with no scale.</summary>
        public void BeginOpen(bool consentFade = false)
        {
            consent = consentFade;
            fadeOnly = consent || ReduceMotion;
            Begin(Phase.Opening, 0f, UITheme.OpenScaleFrom, 1f, 1f, consent ? UITheme.ConsentFadeMs : UITheme.OpenMs);
            done = null;
            if (Instant) Snap();
        }

        /// <summary>Closes from the current alpha and scale; <paramref name="onDone"/> runs once it has finished.</summary>
        public void BeginClose(Action onDone)
        {
            if (phase == Phase.Closing) { done += onDone; return; }
            // A consent window stays fade-only for its whole life.
            fadeOnly = consent || ReduceMotion;
            Begin(Phase.Closing, Alpha, Scale, 0f, UITheme.CloseScaleTo, consent ? UITheme.ConsentFadeMs : UITheme.CloseMs);
            done = onDone;
            if (Instant) Snap();
        }

        // A closing window that gets deactivated (Hide all) would never tick again; finish it so it is destroyed.
        void OnDisable() { if (IsClosing) Snap(); }

        void Begin(Phase next, float alpha0, float scale0, float alpha1, float scale1, float ms)
        {
            phase = next;
            elapsed = 0f;
            duration = ms * 0.001f;
            fromAlpha = alpha0;
            fromScale = fadeOnly ? 1f : scale0;
            toAlpha = alpha1;
            toScale = fadeOnly ? 1f : scale1;
            Evaluate(0f);
        }

        /// <summary>Advances by <paramref name="deltaTime"/> seconds (unscaled).</summary>
        public void Step(float deltaTime)
        {
            if (phase == Phase.Idle) return;
            elapsed += Mathf.Max(0f, deltaTime);
            // Float sums of small steps land a hair short of the duration; treat that as reached.
            float t = duration - elapsed <= 1e-5f ? 1f : elapsed / duration;
            Evaluate(t);
            if (t >= 1f) Finish();
        }

        /// <summary>Jumps to the end of the current motion (opening: shown; closing: finishes and runs the callback).</summary>
        public void Snap()
        {
            if (phase == Phase.Idle) return;
            Evaluate(1f);
            Finish();
        }

        void Evaluate(float t)
        {
            float e = phase == Phase.Opening ? UITheme.EaseOutCubic(t) : UITheme.EaseInCubic(t);
            Alpha = Mathf.Lerp(fromAlpha, toAlpha, e);
            Scale = Mathf.Lerp(fromScale, toScale, e);
            Apply();
        }

        void Finish()
        {
            var finished = done;
            bool wasOpening = phase == Phase.Opening;
            phase = Phase.Idle;
            done = null;
            if (wasOpening)
            {
                Alpha = 1f;
                Scale = 1f;
                Apply();
            }
            labelFades.Clear();
            finished?.Invoke();
        }

        void LateUpdate()
        {
            if (phase != Phase.Idle) Step(Time.unscaledDeltaTime);
        }

        void Apply()
        {
            if (window == null && !TryGetComponent(out window)) return;
            window.SetMotionScale(Scale);

            block ??= new MaterialPropertyBlock();
            GetComponentsInChildren(true, renderers);
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.TryGetComponent<TMP_Text>(out _)) continue;
                renderer.GetPropertyBlock(block);
                block.SetFloat(WindowFadeId, Alpha);
                renderer.SetPropertyBlock(block);
            }

            // A label's alpha is its own (a disabled control dims it) times the fade. If something else changed it since we
            // last wrote it, that is its new own alpha.
            GetComponentsInChildren(true, labels);
            foreach (var label in labels)
            {
                if (label == null) continue;
                float current = label.alpha;
                bool known = labelFades.TryGetValue(label, out var fade);
                if (!known || !Mathf.Approximately(current, fade.written)) fade.baseAlpha = current;
                float wanted = fade.baseAlpha * Alpha;
                if (!Mathf.Approximately(current, wanted)) label.alpha = wanted;
                fade.written = wanted;
                labelFades[label] = fade;
            }
        }
    }
}
