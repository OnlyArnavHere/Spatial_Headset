using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// A window's options: a round "..." button under the window opens a strip under the body with a Curve slider (flat to a
    /// gentle cylinder) and the aspect presets (Free, 16:9, 16:10, 21:9, 4:3, 32:9, 1:1, 3:2). Built by
    /// <see cref="SpatialWindow"/>; picking a preset asks the window's manager to apply it.
    /// </summary>
    public class WindowOptions : MonoBehaviour
    {
        static readonly Color ButtonColor = new Color(0.32f, 0.34f, 0.40f);
        static readonly Color PanelColor = new Color(0.13f, 0.14f, 0.17f);
        static readonly Color TrackColor = new Color(0.24f, 0.25f, 0.30f);
        static readonly Color FillColor = new Color(0.16f, 0.36f, 0.95f);
        static readonly Color SelectedColor = new Color(0.16f, 0.36f, 0.95f);

        const float RowHeight = 0.032f;
        const float Gap = 0.006f;
        const float LabelWidth = 0.07f;
        const float Depth = 0.002f;
        /// <summary>Height of the strip under the body while open.</summary>
        public const float PanelHeight = RowHeight * 2f + Gap * 3f;

        public SpatialWindow Window { get; private set; }
        public bool IsOpen { get; private set; }
        /// <summary>How far the open strip reaches below the body (0 when closed).</summary>
        public float ExtraHeight => IsOpen ? PanelHeight + Gap : 0f;
        public WindowButton ToggleButton { get; private set; }
        /// <summary>The drawn side of <see cref="ToggleButton"/> (a round glass control).</summary>
        public ChromeControl Toggle { get; private set; }
        public WindowSlider CurveSlider { get; private set; }
        /// <summary>Free first, then <see cref="AspectPresets.All"/>.</summary>
        public IReadOnlyList<WindowButton> AspectButtons => aspectButtons;

        static readonly AspectPreset[] Choices =
        {
            AspectPreset.Free, AspectPreset.Wide16x9, AspectPreset.Wide16x10, AspectPreset.Ultrawide21x9, AspectPreset.Classic4x3,
            AspectPreset.SuperUltrawide32x9, AspectPreset.Square1x1, AspectPreset.Photo3x2,
        };

        Transform root, panel, fill;
        TextMeshPro curveLabel;
        readonly List<WindowButton> aspectButtons = new List<WindowButton>();
        readonly List<(TextMeshPro label, Material material)> aspectVisuals = new List<(TextMeshPro, Material)>();
        readonly List<Material> materials = new List<Material>();
        Material baseMaterial;
        Vector2 size;

        public void Build(SpatialWindow window, Material material, Material chromeMaterial)
        {
            Window = window;
            baseMaterial = material;
            var visual = window.Visual;

            ToggleButton = Control<WindowButton>("OptionsButton", visual, ButtonColor);
            Toggle = new ChromeControl(ToggleButton.transform, visual, chromeMaterial, new Vector2(ChromeControl.HitPt, ChromeControl.HitPt),
                new Vector2(ChromeControl.DiscPt, ChromeControl.DiscPt), 0f, ChromeControl.DiscFill, true, UIIcons.MoreHoriz);
            ToggleButton.Clicked += () => SetOpen(!IsOpen);

            root = new GameObject("Options").transform;
            root.SetParent(visual, false);
            panel = Quad("OptionsPanel", root, PanelColor);
            curveLabel = Label("CurveLabel", root, "", 0.16f);
            CurveSlider = Control<WindowSlider>("CurveSlider", root, TrackColor);
            CurveSlider.ValueChanged += value => Window.SetCurve(value);
            fill = Quad("CurveFill", root, FillColor);
            foreach (var preset in Choices)
            {
                var button = Control<WindowButton>($"Aspect {AspectPresets.Label(preset)}", root, ButtonColor);
                var choice = preset;
                button.Clicked += () => Window.RequestAspect(choice);
                aspectButtons.Add(button);
                aspectVisuals.Add((Label($"AspectLabel {AspectPresets.Label(preset)}", root, AspectPresets.Label(preset), 0.14f),
                    button.GetComponent<MeshRenderer>().sharedMaterial));
            }
            root.gameObject.SetActive(false);
            window.ShapeChanged += _ => Refresh();
        }

        public void SetOpen(bool open)
        {
            if (open == IsOpen) return;
            IsOpen = open;
            root.gameObject.SetActive(open);
            Refresh();
            Window.LayoutChrome();
        }

        void Refresh()
        {
            curveLabel.text = Window.Curve <= 0f ? "Flat" : $"Curve {Mathf.RoundToInt(Window.Curve * 100f)}%";
            for (int i = 0; i < Choices.Length; i++)
                aspectVisuals[i].material.color = Window.Aspect == Choices[i] ? SelectedColor : ButtonColor;
            PlaceFill();
        }

        /// <summary>Called from the window's layout (the options button is placed by <see cref="SpatialWindow.LayoutChrome"/>).</summary>
        public void Layout(Vector2 bodySize)
        {
            size = bodySize;

            float top = -bodySize.y * 0.5f - Gap;
            float width = bodySize.x;
            panel.localPosition = new Vector3(0f, top - PanelHeight * 0.5f, 0f);
            panel.localScale = new Vector3(width, PanelHeight, 1f);

            float left = -width * 0.5f + Gap;
            float rowY = top - Gap - RowHeight * 0.5f;
            curveLabel.transform.localPosition = new Vector3(left + LabelWidth * 0.5f, rowY, -Depth * 1.5f);
            curveLabel.rectTransform.sizeDelta = new Vector2(LabelWidth, RowHeight);
            float sliderWidth = Mathf.Max(0.01f, width - LabelWidth - Gap * 3f);
            CurveSlider.transform.localPosition = new Vector3(left + LabelWidth + Gap + sliderWidth * 0.5f, rowY, -Depth * 0.5f);
            CurveSlider.transform.localScale = new Vector3(sliderWidth, RowHeight, 1f);
            PlaceFill();

            rowY -= RowHeight + Gap;
            int count = aspectButtons.Count;
            float cell = (width - Gap * (count + 1)) / count;
            for (int i = 0; i < count; i++)
            {
                float x = left + cell * (i + 0.5f) + Gap * i;
                aspectButtons[i].transform.localPosition = new Vector3(x, rowY, -Depth * 0.5f);
                aspectButtons[i].transform.localScale = new Vector3(cell, RowHeight, 1f);
                aspectVisuals[i].label.transform.localPosition = new Vector3(x, rowY, -Depth * 1.5f);
                aspectVisuals[i].label.rectTransform.sizeDelta = new Vector2(cell, RowHeight);
                aspectVisuals[i].label.fontSize = Mathf.Min(0.14f, cell * 2.2f);
            }
            Refresh();
        }

        void PlaceFill()
        {
            if (fill == null || size == Vector2.zero) return;
            var track = CurveSlider.transform;
            float width = track.localScale.x * Window.Curve;
            fill.localPosition = new Vector3(track.localPosition.x - track.localScale.x * 0.5f + width * 0.5f, track.localPosition.y, -Depth);
            fill.localScale = new Vector3(Mathf.Max(0.0005f, width), RowHeight, 1f);
        }

        T Control<T>(string name, Transform parent, Color color) where T : WindowControl
        {
            var quad = Quad(name, parent, color);
            quad.gameObject.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 0.001f);
            var control = quad.gameObject.AddComponent<T>();
            control.Register(Window);
            return control;
        }

        Transform Quad(string name, Transform parent, Color color)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            DestroyImmediate(quad.GetComponent<MeshCollider>());
            quad.transform.SetParent(parent, false);
            var material = new Material(baseMaterial) { color = color };
            materials.Add(material);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            return quad.transform;
        }

        static TextMeshPro Label(string name, Transform parent, string text, float fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        void OnDestroy()
        {
            foreach (var material in materials)
                if (material != null) Destroy(material);
            materials.Clear();
        }
    }
}
