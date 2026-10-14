using System;
using System.Collections.Generic;
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
            /// <summary>Short text drawn on the icon.</summary>
            public string Glyph;
            public Color Color;
            public Action Open;
        }

        const int MaxColumns = 3;
        const float Gap = 0.02f;
        const float LabelHeight = 0.04f;

        readonly List<WindowButton> buttons = new List<WindowButton>();
        readonly List<TextMeshPro> glyphs = new List<TextMeshPro>();
        readonly List<TextMeshPro> labels = new List<TextMeshPro>();

        /// <summary>The icon buttons, in entry order.</summary>
        public IReadOnlyList<WindowButton> Buttons => buttons;

        public void Init(SpatialWindow window, Material baseMaterial, IReadOnlyList<Entry> entries)
        {
            Attach(window, baseMaterial);
            foreach (var entry in entries)
            {
                var button = CreateControl<WindowButton>($"Icon {entry.Label}", transform, CreateMaterial(entry.Color));
                var open = entry.Open;
                button.Clicked += () => open?.Invoke();
                buttons.Add(button);
                glyphs.Add(CreateLabel($"Glyph {entry.Label}", transform, entry.Glyph, 0.5f, TextAlignmentOptions.Center));
                labels.Add(CreateLabel($"Label {entry.Label}", transform, entry.Label, 0.25f, TextAlignmentOptions.Center));
            }
            Relayout();
        }

        protected override void Layout(Vector2 size)
        {
            int count = buttons.Count;
            if (count == 0) return;
            int columns = Mathf.Min(MaxColumns, count);
            int rows = Mathf.CeilToInt(count / (float)columns);

            float cellWidth = (size.x - Gap * (columns - 1)) / columns;
            float cellHeight = (size.y - Gap * (rows - 1)) / rows;
            float icon = Mathf.Max(0.01f, Mathf.Min(cellWidth, cellHeight - LabelHeight));

            for (int i = 0; i < count; i++)
            {
                int row = i / columns, column = i % columns;
                float x = -size.x * 0.5f + cellWidth * (column + 0.5f) + Gap * column;
                float cellY = size.y * 0.5f - cellHeight * (row + 0.5f) - Gap * row;
                float iconY = cellY + LabelHeight * 0.5f;

                buttons[i].transform.localPosition = new Vector3(x, iconY, -0.001f);
                buttons[i].transform.localScale = new Vector3(icon, icon, 1f);

                glyphs[i].transform.localPosition = new Vector3(x, iconY, -0.003f);
                glyphs[i].rectTransform.sizeDelta = new Vector2(icon, icon);
                glyphs[i].fontSize = icon * 3f;

                labels[i].transform.localPosition = new Vector3(x, iconY - icon * 0.5f - LabelHeight * 0.5f, -0.002f);
                labels[i].rectTransform.sizeDelta = new Vector2(cellWidth, LabelHeight);
            }
        }
    }
}
