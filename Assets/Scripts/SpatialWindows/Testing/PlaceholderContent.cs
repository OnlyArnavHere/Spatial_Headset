using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing
{
    /// <summary>Test content: a coloured panel with a centred label that follows the window's size.</summary>
    public class PlaceholderContent : WindowContent
    {
        Transform panel;
        TextMeshPro label;

        public void Init(SpatialWindow window, Material baseMaterial, Color color, string text)
        {
            Attach(window, baseMaterial);
            panel = CreateQuad("Panel", transform, CreateMaterial(color));
            label = CreateLabel("Label", transform, text, 0.6f, TextAlignmentOptions.Center);
            label.transform.localPosition = new Vector3(0f, 0f, -0.002f);
            label.textWrappingMode = TextWrappingModes.Normal;
            Relayout();
        }

        protected override void Layout(Vector2 size)
        {
            panel.localScale = new Vector3(size.x, size.y, 1f);
            label.rectTransform.sizeDelta = size;
        }
    }
}
