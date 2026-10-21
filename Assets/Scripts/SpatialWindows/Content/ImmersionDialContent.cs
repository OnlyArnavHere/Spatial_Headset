using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// The immersion "dial": a readout ("Immersion 40% · Cinema"), a slider from passthrough (left) to the full
    /// environment (right) and a button per environment, the selected one highlighted. Drives an
    /// <see cref="ImmersionController"/> and follows its changes (for example from the [ and ] keys).
    /// </summary>
    public class ImmersionDialContent : WindowContent
    {
        static readonly Color TrackColor = new Color(0.24f, 0.25f, 0.30f);
        static readonly Color FillColor = new Color(0.16f, 0.36f, 0.95f);
        static readonly Color KnobColor = new Color(0.92f, 0.93f, 0.96f);
        static readonly Color ButtonColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color SelectedColor = new Color(0.16f, 0.36f, 0.95f);

        const float ReadoutHeight = 0.04f;
        const float SliderHeight = 0.03f;
        const float HintHeight = 0.02f;
        const float ButtonHeight = 0.045f;
        const float Gap = 0.01f;
        const float EndSnap = 0.02f;

        /// <summary>Content height the dial needs.</summary>
        public const float ContentHeight = ReadoutHeight + SliderHeight + HintHeight + ButtonHeight + Gap * 3f;

        public ImmersionController Controller { get; private set; }
        public WindowSlider Slider { get; private set; }
        public string Readout => readout != null ? readout.text : "";
        /// <summary>The environment buttons, in <see cref="ImmersiveEnvironment"/> order.</summary>
        public IReadOnlyList<WindowButton> EnvironmentButtons => buttons;

        TextMeshPro readout, passthroughHint, environmentHint;
        Transform fill, knob;
        readonly List<WindowButton> buttons = new List<WindowButton>();
        readonly List<(TextMeshPro label, Material material)> buttonVisuals = new List<(TextMeshPro, Material)>();
        Vector2 area;

        public void Init(SpatialWindow window, Material baseMaterial, ImmersionController controller)
        {
            Attach(window, baseMaterial);
            Controller = controller;

            readout = CreateLabel("Readout", transform, "", TextRole.Headline, TextAlignmentOptions.MidlineLeft);
            Slider = CreateControl<WindowSlider>("Slider", transform, CreateMaterial(TrackColor));
            Slider.ValueChanged += value => Controller.SetLevel(SnapToEnds(value));
            fill = CreateQuad("SliderFill", transform, CreateMaterial(FillColor));
            knob = CreateQuad("SliderKnob", transform, CreateMaterial(KnobColor));
            passthroughHint = CreateLabel("PassthroughHint", transform, "Passthrough", TextRole.Secondary, TextAlignmentOptions.MidlineLeft);
            environmentHint = CreateLabel("EnvironmentHint", transform, "Environment   [ ]", TextRole.Secondary, TextAlignmentOptions.MidlineRight);

            foreach (ImmersiveEnvironment environment in Enum.GetValues(typeof(ImmersiveEnvironment)))
            {
                var material = CreateMaterial(ButtonColor);
                var button = CreateControl<WindowButton>($"Environment {environment}", transform, material);
                var choice = environment;
                button.Clicked += () => Controller.SetEnvironment(choice);
                buttons.Add(button);
                buttonVisuals.Add((CreateLabel($"EnvironmentLabel {environment}", transform, environment.ToString(), TextRole.Body,
                    TextAlignmentOptions.Center), material));
            }

            Controller.Changed += Refresh;
            Refresh();
            Relayout();
        }

        /// <summary>
        /// A pointer or a pinch never lands exactly on an end of the slider, so the last <see cref="EndSnap"/> at each
        /// end counts as the end: full passthrough (nothing drawn) or the full environment.
        /// </summary>
        public static float SnapToEnds(float value) => value < EndSnap ? 0f : value > 1f - EndSnap ? 1f : value;

        void Refresh()
        {
            if (Controller == null) return;
            readout.text = $"Immersion {Mathf.RoundToInt(Controller.Level * 100f)}%  ·  {Controller.Environment}";
            for (int i = 0; i < buttonVisuals.Count; i++)
                buttonVisuals[i].material.color = (int)Controller.Environment == i ? SelectedColor : ButtonColor;
            PlaceSliderFill();
        }

        protected override void Layout(Vector2 size)
        {
            area = size;
            float top = size.y * 0.5f;
            float y = top - ReadoutHeight * 0.5f;
            readout.transform.localPosition = new Vector3(0f, y, -0.003f);
            readout.rectTransform.sizeDelta = new Vector2(size.x, ReadoutHeight);

            y = top - ReadoutHeight - Gap - SliderHeight * 0.5f;
            Slider.transform.localPosition = new Vector3(0f, y, -0.001f);
            Slider.transform.localScale = new Vector3(size.x, SliderHeight, 1f);
            PlaceSliderFill();

            y = top - ReadoutHeight - Gap - SliderHeight - HintHeight * 0.5f;
            foreach (var hint in new[] { passthroughHint, environmentHint })
            {
                hint.transform.localPosition = new Vector3(0f, y, -0.003f);
                hint.rectTransform.sizeDelta = new Vector2(size.x, HintHeight);
            }

            y = -size.y * 0.5f + ButtonHeight * 0.5f;
            int count = buttons.Count;
            float width = (size.x - Gap * (count - 1)) / count;
            for (int i = 0; i < count; i++)
            {
                float x = -size.x * 0.5f + width * (i + 0.5f) + Gap * i;
                buttons[i].transform.localPosition = new Vector3(x, y, -0.001f);
                buttons[i].transform.localScale = new Vector3(width, ButtonHeight, 1f);
                buttonVisuals[i].label.transform.localPosition = new Vector3(x, y, -0.003f);
                buttonVisuals[i].label.rectTransform.sizeDelta = new Vector2(width, ButtonHeight);
            }
        }

        void PlaceSliderFill()
        {
            if (fill == null || area == Vector2.zero) return;
            float level = Controller.Level;
            var track = Slider.transform.localPosition;
            float left = -area.x * 0.5f;
            float width = Mathf.Max(0.0005f, area.x * level);
            fill.localPosition = new Vector3(left + width * 0.5f, track.y, -0.002f);
            fill.localScale = new Vector3(width, SliderHeight, 1f);
            knob.localPosition = new Vector3(left + area.x * level, track.y, -0.0025f);
            knob.localScale = new Vector3(0.008f, SliderHeight * 1.4f, 1f);
        }

        protected override void OnDestroy()
        {
            if (Controller != null) Controller.Changed -= Refresh;
            base.OnDestroy();
        }
    }
}
