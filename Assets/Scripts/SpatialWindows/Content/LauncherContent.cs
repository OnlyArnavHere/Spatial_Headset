using System;
using System.Collections.Generic;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>A home panel: a grid of app icons with labels, each opening something when clicked.</summary>
    public class LauncherContent : WindowContent
    {
        public struct Entry
        {
            public string Label;
            /// <summary>Icon codepoint from <see cref="UIIcons"/>; null falls back to <see cref="IconFor"/> by label.</summary>
            public string Icon;
            public Color Color;
            public Action Open;
        }

        const int MaxColumns = 4;
        const float Gap = 0.02f;
        const float LabelHeight = 0.03f;
        /// <summary>Content size that fits 10 tiles in 4 columns and 3 rows.</summary>
        public static readonly Vector2 PreferredSize = new Vector2(0.46f, 0.36f);
        public static Vector2 WindowSize => PreferredSize + Vector2.one * (Padding * 2f);
        /// <summary>Tile size and glyph size (pt).</summary>
        public const float TilePt = 72f, GlyphPt = 32f;
        /// <summary>White on a tile must reach this contrast.</summary>
        public const float MinTileContrast = 4.5f;

        readonly List<WindowButton> buttons = new List<WindowButton>();
        readonly List<TextMeshPro> glyphs = new List<TextMeshPro>();
        readonly List<TextMeshPro> labels = new List<TextMeshPro>();
        // A soft plate behind each whole tile (icon and caption) that lights up with the hover state.
        readonly List<Transform> plates = new List<Transform>();
        readonly List<Vector3> plateScales = new List<Vector3>();
        MaterialPropertyBlock plateBlock;
        public static readonly Color PlateColor = new Color(1f, 1f, 1f, 0.12f);
        public const float PlateRadiusPt = 16f;
        const float PlatePad = 0.008f;
        static readonly int PlateColorId = Shader.PropertyToID("_Color");

        /// <summary>The icon glyph of an entry by its label (Material Icons Round), with a fallback for unknown ones.</summary>
        public static string IconFor(string label)
        {
            switch (label)
            {
                case "Image": return UIIcons.Image;
                case "Video": return UIIcons.Movie;
                case "Blank": return UIIcons.CheckBoxOutlineBlank;
                case "Laptop": return UIIcons.Laptop;
                case "Void": return UIIcons.DarkMode;
                case "Cinema": return UIIcons.Theaters;
                case "Sky": return UIIcons.WbSunny;
                case "Settings": return UIIcons.Settings;
                case "Hide all": return UIIcons.VisibilityOff;
                case "Show all": return UIIcons.Visibility;
                case "Cast": return UIIcons.Cast;
                default: return UIIcons.Apps;
            }
        }

        /// <summary>The app's colour darkened until white on it reaches <see cref="MinTileContrast"/>.</summary>
        public static Color TileColor(Color color)
        {
            color.a = 1f;
            for (int i = 0; i < 40 && UITheme.ContrastRatio(Color.white, color) < MinTileContrast; i++)
                color = new Color(color.r * 0.92f, color.g * 0.92f, color.b * 0.92f, 1f);
            return color;
        }

        /// <summary>The icon buttons, in entry order.</summary>
        public IReadOnlyList<WindowButton> Buttons => buttons;

        /// <summary>Changes a tile's label and icon (for example Hide / Show).</summary>
        public void SetEntryText(int index, string label, string icon)
        {
            if (index < 0 || index >= buttons.Count) return;
            labels[index].text = label;
            glyphs[index].text = icon ?? IconFor(label);
        }

        public string EntryLabel(int index) => index >= 0 && index < labels.Count ? labels[index].text : null;

        public void Init(SpatialWindow window, Material baseMaterial, IReadOnlyList<Entry> entries)
        {
            Attach(window, baseMaterial);
            foreach (var entry in entries)
            {
                var plate = CreateQuad($"Plate {entry.Label}", transform, CreateRounded(new Color(1f, 1f, 1f, 0f), PlateRadiusPt), 0);
                plates.Add(plate);
                plateScales.Add(Vector3.one);
                int tile = plates.Count - 1;
                var button = CreateControl<WindowButton>($"Icon {entry.Label}", transform,
                    CreateRounded(TileColor(entry.Color), UITheme.ControlRadius));
                var open = entry.Open;
                button.Clicked += () => open?.Invoke();
                button.VisualRefreshed += () => RefreshPlate(tile);
                buttons.Add(button);
                var glyph = CreateLabel($"Glyph {entry.Label}", transform, entry.Icon ?? IconFor(entry.Label), TextRole.Icon, TextAlignmentOptions.Center);
                glyph.fontSize = UITheme.PtToMetres(GlyphPt) * ThemeApplier.TmpWorldScale;
                glyph.color = Color.white;
                glyph.overflowMode = TextOverflowModes.Overflow;
                button.BindLabel(glyph);
                glyphs.Add(glyph);
                var caption = CreateLabel($"Label {entry.Label}", transform, entry.Label, TextRole.Caption, TextAlignmentOptions.Center);
                caption.overflowMode = TextOverflowModes.Ellipsis;
                button.BindLabel(caption);
                labels.Add(caption);
            }
            Relayout();
        }

        // Plate alpha follows the smoothed overlay (0.10 hover, 0.18 pressed -> 1x and 1.8x), and the tile content shrinks with a press.
        void RefreshPlate(int i)
        {
            plateBlock ??= new MaterialPropertyBlock();
            if (i < buttons.Count && i < plates.Count && i < glyphs.Count && i < labels.Count)
            {
                var state = buttons[i].State;
                float k = state.OverlayAlpha / UITheme.HoverOverlay.a;
                var renderer = plates[i].GetComponent<MeshRenderer>();
                renderer.GetPropertyBlock(plateBlock);
                var color = PlateColor;
                color.a = Mathf.Clamp01(PlateColor.a * k);
                plateBlock.SetColor(PlateColorId, color);
                renderer.SetPropertyBlock(plateBlock);
                plates[i].localScale = plateScales[i] * state.Scale;
                glyphs[i].transform.localScale = Vector3.one * state.Scale;
                labels[i].transform.localScale = Vector3.one * state.Scale;
            }
        }

        protected override void Layout(Vector2 size)
        {
            int count = buttons.Count;
            if (count == 0) return;
            int columns = Mathf.Min(MaxColumns, count);
            int rows = Mathf.CeilToInt(count / (float)columns);

            float cellWidth = (size.x - Gap * (columns - 1)) / columns;
            float cellHeight = (size.y - Gap * (rows - 1)) / rows;
            float icon = Mathf.Max(0.01f, Mathf.Min(UITheme.PtToMetres(TilePt), Mathf.Min(cellWidth, cellHeight - LabelHeight)));

            for (int i = 0; i < count; i++)
            {
                int row = i / columns, column = i % columns;
                float x = -size.x * 0.5f + cellWidth * (column + 0.5f) + Gap * column;
                float cellY = size.y * 0.5f - cellHeight * (row + 0.5f) - Gap * row;
                float iconY = cellY + LabelHeight * 0.5f;

                plates[i].localPosition = new Vector3(x, cellY, -0.0005f);
                plateScales[i] = new Vector3(cellWidth, icon + LabelHeight + PlatePad * 2f, 1f);
                plates[i].localScale = plateScales[i];

                buttons[i].transform.localPosition = new Vector3(x, iconY, -0.001f);
                buttons[i].transform.localScale = new Vector3(icon, icon, 1f);

                glyphs[i].transform.localPosition = new Vector3(x, iconY, -0.003f);
                glyphs[i].rectTransform.sizeDelta = new Vector2(icon, icon);
                
                labels[i].transform.localPosition = new Vector3(x, iconY - icon * 0.5f - LabelHeight * 0.5f, -0.002f);
                labels[i].rectTransform.sizeDelta = new Vector2(cellWidth, LabelHeight);
            }
        }
    }
}
