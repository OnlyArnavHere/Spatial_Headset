using TMPro;
using UnityEngine;

namespace SpatialLauncher.SpatialWindows.Testing
{
    /// <summary>Test content: a coloured panel with a centred label that follows the window's size.</summary>
    public class PlaceholderContent : MonoBehaviour
    {
        const float Padding = 0.02f;

        SpatialWindow window;
        Transform panel;
        TextMeshPro label;
        Material material;

        public void Init(SpatialWindow target, Material baseMaterial, Color color, string text)
        {
            window = target;
            transform.SetParent(window.ContentRoot, false);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Panel";
            // Clicks pass through to the window body behind the panel.
            Destroy(quad.GetComponent<MeshCollider>());
            panel = quad.transform;
            panel.SetParent(transform, false);
            material = new Material(baseMaterial) { color = color };
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;

            var labelObject = new GameObject("Label");
            labelObject.transform.SetParent(transform, false);
            labelObject.transform.localPosition = new Vector3(0f, 0f, -0.002f);
            label = labelObject.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = 0.6f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;

            window.SizeChanged += Layout;
            Layout(window);
        }

        void Layout(SpatialWindow w)
        {
            var size = new Vector2(Mathf.Max(0.01f, w.Size.x - Padding * 2f), Mathf.Max(0.01f, w.Size.y - Padding * 2f));
            panel.localScale = new Vector3(size.x, size.y, 1f);
            label.rectTransform.sizeDelta = size;
        }

        void OnDestroy()
        {
            if (window != null) window.SizeChanged -= Layout;
            if (material != null) Destroy(material);
        }
    }
}
